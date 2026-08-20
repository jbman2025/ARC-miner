// The Monero/XMRig ("CryptoNote") stratum dialect: login, job pushes,
// keepalived, and share submission.
//
// Members today are rx and nm; the CPU algos proposed in new coin.md
// (yescryptr32, yespower, minotaurx, cpupower, panthera, vrsc) speak the same
// login/job/submit shape.
//
// Both members already used awaited CallAsync for login and submit, so unlike
// the Bitcoin-stratum family there is no attribution model to fix here — this
// extraction is pure de-duplication of the plumbing: session id tracking, the
// job generation counter, keepalive, the "status":"OK" verdict check, and the
// accepted/rejected counters.
//
// What deliberately stays with the algo, because rx and nm genuinely differ:
//   • login params — rx sends {login, pass, agent}; nm adds an "algo" ARRAY
//     member and puts "<address>.<worker>" in login.
//   • job payload parsing — rx sniffs the nonce offset from the blob length
//     (Monero @39 vs Bitcoin-header @76) and right-aligns a compact target;
//     nm has a fixed 124-byte blob with the nonce at 116 and a BIG-endian
//     target. Getting either wrong makes every share "low difficulty".
//   • the nonce hex width: rx submits 4 bytes, nm submits all 8.

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Akoya.Miner.Mining.Stratum;

/// <summary>One job as the pool pushed it, after the algo has parsed it.</summary>
internal sealed record CryptoNoteJob(
    string JobId,
    byte[] Blob,
    byte[] Target,
    byte[] SeedHash,
    long Height,
    int NonceOffset,
    bool FullWidthNonce);

internal sealed class CryptoNoteStratumDialect(
    StratumSession session,
    ILogger log,
    string tag,
    Func<JsonElement, CryptoNoteJob> parseJob)
{
    // Publication is lock-free ON THE READ SIDE and that is not a
    // micro-optimisation: every solver thread calls Snapshot() inside its inner
    // grind loop. An early version of this class guarded it with a mutex and
    // cost nm 15% hashrate (6.55 -> 5.55 kH/s on 12 threads) by serialising the
    // solvers on one lock. Writes are rare (a job push), reads are constant.
    //
    // Ordering mirrors the hand-rolled JobBox this replaced: writers publish the
    // job, THEN bump the generation; readers take the generation, THEN the job.
    // A reader can therefore observe a newer job with an older generation, which
    // is benign — the mismatch just makes it re-read on the next iteration.
    private volatile CryptoNoteJob? _job;
    private long _generation;
    private volatile string _sessionId = "";
    private readonly object _writeLock = new();

    private long _accepted;
    private long _rejected;

    public long Accepted => Interlocked.Read(ref _accepted);
    public long Rejected => Interlocked.Read(ref _rejected);
    public string SessionId => _sessionId;

    /// <summary>Raised on the login job and on every subsequent push.</summary>
    public event Action<CryptoNoteJob>? JobReceived;

    /// <summary>Current job and its generation, read together.</summary>
    public (CryptoNoteJob? Job, long Generation) Snapshot()
    {
        long gen = Interlocked.Read(ref _generation);
        return (_job, gen);
    }

    /// <summary>
    /// Send login and install the job that comes back with it. The caller's
    /// read loop must already be running — the response arrives through it.
    /// </summary>
    /// <param name="loginParamsJson">Already-serialised params object. Built by
    /// the algo because the members disagree on its members.</param>
    public async Task LoginAsync(string loginParamsJson, CancellationToken ct)
    {
        var resp = await session.CallAsync("login", loginParamsJson, ct).ConfigureAwait(false);

        var id = resp.GetProperty("id").GetString() ?? "";
        var job = parseJob(resp.GetProperty("job"));

        lock (_writeLock)
        {
            _sessionId = id;
            _job = job;
            Interlocked.Increment(ref _generation);
        }
        log.LogInformation("{Tag}: logged in, session={Session}", tag, id);
        JobReceived?.Invoke(job);
    }

    public Task ReadLoopAsync(CancellationToken ct) =>
        session.ReadLoopAsync(OnNotification, onUnmatchedResponse: null, ct);

    private void OnNotification(string method, JsonElement root)
    {
        if (method != "job" || !root.TryGetProperty("params", out var p)) return;

        CryptoNoteJob job;
        try
        {
            job = parseJob(p);
        }
        catch (Exception ex)
        {
            // A malformed or unexpected-shape job is the pool's problem. Keep
            // mining the one we have rather than dropping the session.
            log.LogWarning("{Tag}: malformed job push ({Msg})", tag, ex.Message);
            return;
        }

        lock (_writeLock)
        {
            _job = job;
            Interlocked.Increment(ref _generation);
        }
        JobReceived?.Invoke(job);
    }

    /// <summary>
    /// Periodic keepalived. Pools drop idle miners; this is what keeps a
    /// low-hashrate CPU session alive between shares.
    /// </summary>
    public async Task KeepAliveLoopAsync(int intervalSeconds, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct).ConfigureAwait(false);

                var id = SessionId;
                if (string.IsNullOrEmpty(id)) continue;

                try
                {
                    await session.CallAsync("keepalived", StratumJson.Obj(("id", id)), ct,
                                            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // Not fatal: the next share or job push will surface a
                    // genuinely dead connection.
                    log.LogWarning("{Tag}: keepalive failed: {Msg}", tag, ex.Message);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Submit a share and await the verdict. CryptoNote pools answer
    /// {"status":"OK"}; anything else, or a JSON-RPC error, is a rejection.
    /// </summary>
    public async Task<bool> SubmitAsync(string jobId, string nonceHex, string resultHex, CancellationToken ct)
    {
        try
        {
            var resp = await session.CallAsync("submit", StratumJson.Obj(
                ("id", SessionId),
                ("job_id", jobId),
                ("nonce", nonceHex),
                ("result", resultHex)), ct).ConfigureAwait(false);

            bool ok = resp.ValueKind == JsonValueKind.Object
                   && resp.TryGetProperty("status", out var st)
                   && st.GetString() == "OK";

            if (ok) Interlocked.Increment(ref _accepted);
            else
            {
                Interlocked.Increment(ref _rejected);
                log.LogWarning("{Tag}: share rejected job={Job} (a/r={A}/{R})", tag, jobId, Accepted, Rejected);
            }
            return ok;
        }
        catch (PoolRpcException ex)
        {
            Interlocked.Increment(ref _rejected);
            log.LogWarning("{Tag}: share rejected by pool job={Job}: {Msg} (a/r={A}/{R})",
                tag, jobId, ex.Message, Accepted, Rejected);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Interlocked.Increment(ref _rejected);
            log.LogWarning("{Tag}: share submit failed job={Job}: {Msg}", tag, jobId, ex.Message);
            return false;
        }
    }
}
