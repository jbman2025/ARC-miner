using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Akoya.Miner.Mining.Stratum;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Akoya.Miner.Tests;

/// <summary>
/// A one-connection stratum server on loopback. The session's framing and id
/// correlation are only meaningful over a real socket, so these tests use one
/// rather than mocking the stream — it stays fast (no external network) and
/// catches things a mock would not, like a frame left unflushed.
/// </summary>
internal sealed class FakePool : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public FakePool()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public async Task AcceptAsync()
    {
        _client = await _listener.AcceptTcpClientAsync();
        var s = _client.GetStream();
        _reader = new StreamReader(s, Encoding.UTF8);
        _writer = new StreamWriter(s, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
    }

    public Task<string?> ReadLineAsync() => _reader!.ReadLineAsync();
    public Task SendAsync(string line) => _writer!.WriteLineAsync(line);

    public void Close() { _client?.Close(); _client?.Dispose(); _client = null; }

    public ValueTask DisposeAsync()
    {
        Close();
        _listener.Stop();
        return ValueTask.CompletedTask;
    }
}

public class StratumSessionTests
{
    private static StratumSession NewSession() => new(NullLogger.Instance, "test-pool");

    private static async Task<(FakePool Pool, StratumSession Session)> ConnectedPairAsync()
    {
        var pool = new FakePool();
        var session = NewSession();
        var accept = pool.AcceptAsync();
        await session.ConnectAsync("127.0.0.1", pool.Port, useTls: false, CancellationToken.None);
        await accept;
        return (pool, session);
    }

    // ── framing + correlation ────────────────────────────────────────────────

    [Fact]
    public async Task CallSendsAFramedRequestAndResolvesTheMatchingResponse()
    {
        var (pool, session) = await ConnectedPairAsync();
        await using var poolScope = pool;
        await using var sessionScope = session;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var read = session.ReadLoopAsync((_, _) => { }, null, cts.Token);
        var call = session.CallAsync("login", StratumJson.Obj(("login", "addr"), ("pass", "x")), cts.Token);

        var line = await pool.ReadLineAsync();
        Assert.NotNull(line);
        using (var doc = JsonDocument.Parse(line!))
        {
            var root = doc.RootElement;
            Assert.Equal("login", root.GetProperty("method").GetString());
            Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
            Assert.Equal("addr", root.GetProperty("params").GetProperty("login").GetString());
            var id = root.GetProperty("id").GetInt64();
            await pool.SendAsync($"{{\"id\":{id},\"result\":{{\"status\":\"OK\"}},\"error\":null}}");
        }

        var result = await call;
        Assert.Equal("OK", result.GetProperty("status").GetString());

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }

    [Fact]
    public async Task ResultIsUsableAfterTheFramesDocumentIsDisposed()
    {
        // The reader disposes each JsonDocument per frame, so the result must be
        // cloned out. Without that this read throws ObjectDisposedException —
        // and only under timing that a mock would never reproduce.
        var (pool, session) = await ConnectedPairAsync();
        await using var poolScope = pool;
        await using var sessionScope = session;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        _ = session.ReadLoopAsync((_, _) => { }, null, cts.Token);
        var call = session.CallAsync("job", "{}", cts.Token);

        using (var doc = JsonDocument.Parse((await pool.ReadLineAsync())!))
        {
            var id = doc.RootElement.GetProperty("id").GetInt64();
            await pool.SendAsync($"{{\"id\":{id},\"result\":{{\"blob\":\"aabb\",\"height\":42}}}}");
        }

        var result = await call;
        await Task.Delay(50, CancellationToken.None);      // let the reader move on
        Assert.Equal("aabb", result.GetProperty("blob").GetString());
        Assert.Equal(42, result.GetProperty("height").GetInt32());
        cts.Cancel();
    }

    [Fact]
    public async Task ConcurrentCallsGetTheirOwnResponsesEvenOutOfOrder()
    {
        var (pool, session) = await ConnectedPairAsync();
        await using var poolScope = pool;
        await using var sessionScope = session;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        _ = session.ReadLoopAsync((_, _) => { }, null, cts.Token);

        var a = session.CallAsync("first", "{}", cts.Token);
        var b = session.CallAsync("second", "{}", cts.Token);

        var ids = new List<(long Id, string Method)>();
        for (int i = 0; i < 2; i++)
        {
            using var doc = JsonDocument.Parse((await pool.ReadLineAsync())!);
            ids.Add((doc.RootElement.GetProperty("id").GetInt64(),
                     doc.RootElement.GetProperty("method").GetString()!));
        }
        Assert.Distinct(ids.Select(x => x.Id));

        // Answer in REVERSE order — correlation must be by id, not arrival.
        foreach (var (id, method) in ids.AsEnumerable().Reverse())
            await pool.SendAsync($"{{\"id\":{id},\"result\":\"{method}\"}}");

        Assert.Equal("first", (await a).GetString());
        Assert.Equal("second", (await b).GetString());
        cts.Cancel();
    }

    [Fact]
    public async Task AnErrorResponseSurfacesAsPoolRpcException()
    {
        var (pool, session) = await ConnectedPairAsync();
        await using var poolScope = pool;
        await using var sessionScope = session;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        _ = session.ReadLoopAsync((_, _) => { }, null, cts.Token);
        var call = session.CallAsync("submit", "{}", cts.Token);

        using (var doc = JsonDocument.Parse((await pool.ReadLineAsync())!))
        {
            var id = doc.RootElement.GetProperty("id").GetInt64();
            await pool.SendAsync($"{{\"id\":{id},\"error\":{{\"code\":-1,\"message\":\"Low difficulty share\"}}}}");
        }

        var ex = await Assert.ThrowsAsync<PoolRpcException>(() => call);
        Assert.Contains("Low difficulty share", ex.Message, StringComparison.Ordinal);
        cts.Cancel();
    }

    [Fact]
    public async Task AnExplicitNullErrorIsNotTreatedAsAFailure()
    {
        // Pools routinely send "error":null alongside a good result.
        var (pool, session) = await ConnectedPairAsync();
        await using var poolScope = pool;
        await using var sessionScope = session;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        _ = session.ReadLoopAsync((_, _) => { }, null, cts.Token);
        var call = session.CallAsync("submit", "{}", cts.Token);

        using (var doc = JsonDocument.Parse((await pool.ReadLineAsync())!))
        {
            var id = doc.RootElement.GetProperty("id").GetInt64();
            await pool.SendAsync($"{{\"id\":{id},\"result\":{{\"status\":\"OK\"}},\"error\":null}}");
        }

        Assert.Equal("OK", (await call).GetProperty("status").GetString());
        cts.Cancel();
    }

    [Fact]
    public async Task NotificationsGoToTheCallbackNotThePendingTable()
    {
        var (pool, session) = await ConnectedPairAsync();
        await using var poolScope = pool;
        await using var sessionScope = session;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var seen = new TaskCompletionSource<(string Method, string JobId)>();
        _ = session.ReadLoopAsync(
            (method, root) =>
            {
                var jobId = root.GetProperty("params").GetProperty("job_id").GetString() ?? "";
                seen.TrySetResult((method, jobId));
            }, null, cts.Token);

        await pool.SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"job\",\"params\":{\"job_id\":\"abc123\"}}");

        var (m, j) = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("job", m);
        Assert.Equal("abc123", j);
        cts.Cancel();
    }

    [Fact]
    public async Task ResponsesWithNoPendingRequestGoToTheUnmatchedCallback()
    {
        // How the classic-stratum clients (gr, csd) track fire-and-forget submits.
        var (pool, session) = await ConnectedPairAsync();
        await using var poolScope = pool;
        await using var sessionScope = session;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var seen = new TaskCompletionSource<long>();
        _ = session.ReadLoopAsync((_, _) => { }, (id, _) => seen.TrySetResult(id), cts.Token);

        await pool.SendAsync("{\"id\":9876,\"result\":true}");
        Assert.Equal(9876, await seen.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        cts.Cancel();
    }

    [Fact]
    public async Task AMalformedFrameIsSkippedRatherThanKillingTheSession()
    {
        var (pool, session) = await ConnectedPairAsync();
        await using var poolScope = pool;
        await using var sessionScope = session;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var seen = new TaskCompletionSource<string>();
        var read = session.ReadLoopAsync((m, _) => seen.TrySetResult(m), null, cts.Token);

        await pool.SendAsync("this is not json {{{");
        await pool.SendAsync("");
        await pool.SendAsync("{\"method\":\"job\",\"params\":{}}");

        Assert.Equal("job", await seen.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(read.IsCompleted);      // still alive
        cts.Cancel();
    }

    [Fact]
    public async Task ClosedConnectionEndsTheReadLoopWithIOException()
    {
        var (pool, session) = await ConnectedPairAsync();
        await using var poolScope = pool;
        await using var sessionScope = session;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var read = session.ReadLoopAsync((_, _) => { }, null, cts.Token);
        pool.Close();

        await Assert.ThrowsAnyAsync<IOException>(() => read);
    }

    [Fact]
    public async Task ACallThatIsNeverAnsweredTimesOut()
    {
        var (pool, session) = await ConnectedPairAsync();
        await using var poolScope = pool;
        await using var sessionScope = session;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        _ = session.ReadLoopAsync((_, _) => { }, null, cts.Token);
        var call = session.CallAsync("keepalived", "{}", cts.Token, TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAsync<TimeoutException>(() => call);
        cts.Cancel();
    }

    [Fact]
    public async Task CancelPendingUnblocksInFlightCalls()
    {
        // A session ending must not leave a caller waiting out a 30s timeout.
        var (pool, session) = await ConnectedPairAsync();
        await using var poolScope = pool;
        await using var sessionScope = session;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        _ = session.ReadLoopAsync((_, _) => { }, null, cts.Token);
        var call = session.CallAsync("submit", "{}", cts.Token);
        await pool.ReadLineAsync();

        session.CancelPending();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        cts.Cancel();
    }

    [Fact]
    public async Task RequestIdsAreUniqueAndMonotonic()
    {
        var (pool, session) = await ConnectedPairAsync();
        await using var poolScope = pool;
        await using var sessionScope = session;
        Assert.Equal(new[] { 1L, 2L, 3L }, new[] { session.NextId(), session.NextId(), session.NextId() });
    }

    [Fact]
    public async Task SendAsyncWritesExactlyOneNewlineTerminatedFrame()
    {
        var (pool, session) = await ConnectedPairAsync();
        await using var poolScope = pool;
        await using var sessionScope = session;

        await session.SendAsync("{\"id\":1,\"method\":\"mining.subscribe\"}", CancellationToken.None);
        await session.SendAsync("{\"id\":2,\"method\":\"mining.authorize\"}", CancellationToken.None);

        Assert.Equal("{\"id\":1,\"method\":\"mining.subscribe\"}", await pool.ReadLineAsync());
        Assert.Equal("{\"id\":2,\"method\":\"mining.authorize\"}", await pool.ReadLineAsync());
    }

    [Fact]
    public async Task JsonRpcVersionCanBeOmittedForPoolsThatNeverGotIt()
    {
        // btx's ninja (LuckyPool) dialect never sent "jsonrpc":"2.0"; a refactor
        // must not silently start adding fields to the wire.
        var pool = new FakePool();
        var session = new StratumSession(NullLogger.Instance, "btx-pool", jsonRpcVersion: false);
        await using var poolScope = pool;
        await using var sessionScope = session;
        var accept = pool.AcceptAsync();
        await session.ConnectAsync("127.0.0.1", pool.Port, useTls: false, CancellationToken.None);
        await accept;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        _ = session.ReadLoopAsync((_, _) => { }, null, cts.Token);
        _ = session.CallAsync("login", "{}", cts.Token);

        var line = await pool.ReadLineAsync();
        Assert.DoesNotContain("jsonrpc", line!, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(line!);
        Assert.Equal("login", doc.RootElement.GetProperty("method").GetString());
        cts.Cancel();
    }

    [Fact]
    public async Task JsonRpcVersionIsIncludedByDefault()
    {
        var (pool, session) = await ConnectedPairAsync();
        await using var poolScope = pool;
        await using var sessionScope = session;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        _ = session.ReadLoopAsync((_, _) => { }, null, cts.Token);
        _ = session.CallAsync("login", "{}", cts.Token);

        using var doc = JsonDocument.Parse((await pool.ReadLineAsync())!);
        Assert.Equal("2.0", doc.RootElement.GetProperty("jsonrpc").GetString());
        cts.Cancel();
    }

    [Fact]
    public async Task SendingBeforeConnectingFailsLoudly()
    {
        await using var session = NewSession();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.SendAsync("{}", CancellationToken.None));
    }
}

public class StratumJsonTests
{
    [Theory]
    [InlineData("plain")]
    [InlineData("has\"quote")]
    [InlineData("has\\backslash")]
    [InlineData("has\nnewline")]
    [InlineData("has\ttab")]
    public void StringsAreQuotedAndEscapedSoTheyRoundTrip(string value)
    {
        // Assert on meaning, not on a particular escape form: System.Text.Json's
        // default encoder writes a quote as " rather than \", which is
        // equally valid JSON. What matters is that it parses back unchanged.
        var json = StratumJson.Str(value);
        Assert.StartsWith("\"", json, StringComparison.Ordinal);
        Assert.EndsWith("\"", json, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(value, doc.RootElement.GetString());
    }

    [Fact]
    public void PlainStringsAreNotMangled()
    {
        Assert.Equal("\"plain\"", StratumJson.Str("plain"));
    }

    [Fact]
    public void AWorkerNameWithAQuoteCannotBreakOutOfTheFrame()
    {
        // Hand-rolled concatenation would emit a frame the pool mis-parses.
        var json = StratumJson.Obj(("login", "addr"), ("pass", "p\"ass"));
        using var doc = JsonDocument.Parse(json);       // must still be valid JSON
        Assert.Equal("p\"ass", doc.RootElement.GetProperty("pass").GetString());
    }

    [Fact]
    public void ObjectPreservesFieldOrder()
    {
        Assert.Equal("{\"login\":\"a\",\"pass\":\"b\",\"agent\":\"c\"}",
            StratumJson.Obj(("login", "a"), ("pass", "b"), ("agent", "c")));
    }

    [Fact]
    public void EmptyObjectIsValid()
    {
        Assert.Equal("{}", StratumJson.Obj());
    }

    [Fact]
    public void StringArrayEscapesEachElement()
    {
        Assert.Equal("[\"a\",\"b\"]", StratumJson.StrArray("a", "b"));
        using var doc = JsonDocument.Parse(StratumJson.StrArray("we\"ird"));
        Assert.Equal("we\"ird", doc.RootElement[0].GetString());
    }

    [Fact]
    public void RawArrayPassesLiteralsThrough()
    {
        Assert.Equal("[\"job1\",true,42]", StratumJson.RawArray(StratumJson.Str("job1"), "true", "42"));
    }

    [Fact]
    public void NonAsciiSurvivesARoundTrip()
    {
        using var doc = JsonDocument.Parse(StratumJson.Obj(("worker", "rig-café-01")));
        Assert.Equal("rig-café-01", doc.RootElement.GetProperty("worker").GetString());
    }
}
