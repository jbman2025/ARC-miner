// The transport and JSON-RPC framing every pool client needs, in one place.
//
// Five hand-rolled stratum clients (csd, gr, btx, nm, rx) each reimplemented
// this layer: TCP connect + optional TLS, newline-delimited JSON framing, a
// write mutex, request-id allocation, id→response correlation, timeouts, and
// error mapping. Roughly 70% of ~2,800 lines was duplicated, and EVERY per-algo
// pool bug found while bringing up gr and nm lived in it — merkle byte order,
// an inverted diff_to_target, big- vs little-endian target compares, and an AOT
// JsonSerializer throw. Sharing the mechanical part leaves each algo with only
// what is genuinely its own: job parsing, target math, and share payloads.
//
// Deliberately NOT handled here: reconnect/backoff (the algos differ on whether
// a reconnect must tear down solver threads and re-seed) and anything that
// touches Metrics. This type owns a socket and a pending-request table, nothing
// else.

using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Akoya.Miner.Algos;
using Microsoft.Extensions.Logging;

namespace Akoya.Miner.Mining.Stratum;

/// <summary>A JSON-RPC `error` object came back for a request we made.</summary>
internal sealed class PoolRpcException(string message) : Exception(message);

internal sealed class StratumSession : IAsyncDisposable
{
    private readonly ILogger _log;
    private readonly string _tag;
    private readonly bool _jsonRpcVersion;

    private TcpClient? _tcp;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    // One writer at a time: a stratum frame is a whole line, and two concurrent
    // WriteLineAsync calls on the same stream can interleave into corrupt JSON.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private long _msgId;

    /// <param name="tag">Short log prefix, e.g. "rx-pool".</param>
    /// <param name="jsonRpcVersion">Emit <c>"jsonrpc":"2.0"</c> in requests.
    /// The Monero/XMRig-dialect pools (rx, nm) are sent it. The only dialect
    /// that was NOT sent it was btx's ninja (LuckyPool) form, removed
    /// 2026-08-14 — so every remaining caller wants the default true.</param>
    public StratumSession(ILogger log, string tag, bool jsonRpcVersion = true)
    {
        _log = log;
        _tag = tag;
        _jsonRpcVersion = jsonRpcVersion;
    }

    public bool IsConnected => _tcp?.Connected == true;

    /// <summary>Monotonic request id. Exposed for the classic-stratum clients,
    /// which allocate an id before building their params array.</summary>
    public long NextId() => Interlocked.Increment(ref _msgId);

    public async Task ConnectAsync(string host, int port, bool useTls, CancellationToken ct)
    {
        _tcp = new TcpClient();
        await PoolTls.ConnectTcpAsync(_tcp, host, port, ct).ConfigureAwait(false);
        var stream = await PoolTls.WrapAsync(_tcp, host, useTls, _log, ct).ConfigureAwait(false);

        _reader = new StreamReader(stream, Encoding.UTF8);
        // NewLine must be "\n", not Environment.NewLine — a bare CR upsets some
        // pools' line parsers. AutoFlush off; every send flushes explicitly under
        // the write lock so a frame never sits half-written in the buffer.
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = false };

        _log.LogInformation("{Tag}: connected to {Host}:{Port} (tls={Tls})", _tag, host, port, useTls);
    }

    /// <summary>Write one framed JSON line. No response is awaited.</summary>
    public async Task SendAsync(string json, CancellationToken ct)
    {
        if (_writer is null) throw new InvalidOperationException($"{_tag}: not connected");

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
            await _writer.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>Send a JSON-RPC request and await its response.</summary>
    /// <param name="paramsJson">Raw JSON for the params member — an object or an
    /// array, already serialised (see <see cref="StratumJson"/>).</param>
    /// <exception cref="PoolRpcException">The pool returned an `error`.</exception>
    /// <exception cref="TimeoutException">No response within the timeout.</exception>
    public async Task<JsonElement> CallAsync(
        string method, string paramsJson, CancellationToken ct, TimeSpan? timeout = null)
    {
        var id = NextId();
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) { _pending[id] = tcs; }

        try
        {
            var version = _jsonRpcVersion ? "\"jsonrpc\":\"2.0\"," : "";
            await SendAsync(
                $"{{\"id\":{id},{version}\"method\":\"{method}\",\"params\":{paramsJson}}}",
                ct).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
            await using var reg = timeoutCts.Token.Register(
                () => tcs.TrySetException(new TimeoutException($"{method} timed out")));

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            // Always drop the entry — on the success path the reader already
            // removed it, but a send failure or timeout would otherwise leak it.
            lock (_pending) { _pending.Remove(id); }
        }
    }

    /// <summary>
    /// Read frames until the connection closes or <paramref name="ct"/> fires.
    /// Responses carrying an id we are waiting on complete that request;
    /// everything else is handed to the callbacks.
    /// </summary>
    /// <param name="onNotification">Server-initiated call: (method, root).</param>
    /// <param name="onUnmatchedResponse">A response whose id has no pending
    /// request. The classic-stratum clients track their own submits this way.</param>
    public async Task ReadLoopAsync(
        Action<string, JsonElement> onNotification,
        Action<long, JsonElement>? onUnmatchedResponse,
        CancellationToken ct)
    {
        if (_reader is null) throw new InvalidOperationException($"{_tag}: not connected");

        while (!ct.IsCancellationRequested)
        {
            string? line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) throw new IOException($"{_tag}: pool closed connection");
            if (line.Length == 0) continue;

            JsonDocument doc;
            // A malformed frame is the pool's problem, not ours: skip it rather
            // than tearing down a working session.
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }

            using (doc)
            {
                var root = doc.RootElement;

                if (root.TryGetProperty("method", out var mEl) && mEl.ValueKind == JsonValueKind.String)
                {
                    onNotification(mEl.GetString() ?? "", root);
                    continue;
                }

                if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number) continue;

                long id = idEl.GetInt64();
                TaskCompletionSource<JsonElement>? tcs;
                lock (_pending) { _pending.Remove(id, out tcs); }

                if (tcs is null)
                {
                    onUnmatchedResponse?.Invoke(id, root);
                    continue;
                }

                if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                {
                    tcs.TrySetException(new PoolRpcException(errEl.GetRawText()));
                }
                else
                {
                    // Clone: the JsonDocument is disposed when this frame ends.
                    tcs.TrySetResult(root.TryGetProperty("result", out var resEl) ? resEl.Clone() : default);
                }
            }
        }
    }

    /// <summary>Fail every in-flight request. Call when a session ends so
    /// awaiting callers unblock instead of waiting out their timeout.</summary>
    public void CancelPending()
    {
        lock (_pending)
        {
            foreach (var p in _pending.Values) p.TrySetCanceled(CancellationToken.None);
            _pending.Clear();
        }
    }

    public ValueTask DisposeAsync()
    {
        CancelPending();
        _tcp?.Dispose();
        _tcp = null;
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
