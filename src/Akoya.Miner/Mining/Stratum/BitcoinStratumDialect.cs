// The Bitcoin Stratum V1 protocol layer: handshake, extranonce/difficulty
// state, mining.notify parsing, and share submission with correct attribution.
//
// This sits between StratumSession (sockets, framing, id correlation) and an
// algo's solver loop. It owns the PROTOCOL, not the mining: how many threads
// grind, how work is partitioned, what a header looks like and how difficulty
// maps to a target all stay with the algo, because gr and csd genuinely
// disagree about every one of those.
//
// ── Why submits are awaited ──────────────────────────────────────────────────
// Before this class the three classic clients had three different ways of
// deciding whose share the pool just acked:
//
//   csd   a FIFO List<int> of GPU ordinals, paired by ARRIVAL ORDER, guarded by
//         an extra lock, with a comment explaining that a failed send has to
//         remove the TAIL or every later share is credited to the wrong device.
//         It also hardcoded id=4 on every submit, so responses could not be
//         correlated at all — the FIFO existed BECAUSE of that.
//   gr    a ConcurrentDictionary<id, timestamp>, correlated properly, but
//         fire-and-forget so the caller never learns the verdict.
//   rx    an awaited CallAsync — the only one that cannot mis-attribute.
//
// SubmitAsync uses a unique id per submit and awaits the response, so the
// caller that found the share is the caller that learns its fate. There is no
// pending table, no FIFO, and no ordering invariant to get wrong.

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Akoya.Miner.Mining.Stratum;

/// <summary>Immutable view of the pool state a solver needs to build work.
/// A CLASS, not a struct, so the whole thing is published by one reference
/// write and read without a lock — see the note on _work below.</summary>
internal sealed record BitcoinStratumWork(
    BitcoinStratumJob? Job,
    string Extranonce1,
    int Extranonce2Size,
    double Difficulty,
    long Generation);

internal sealed class BitcoinStratumDialect(
    StratumSession session,
    ILogger log,
    string tag,
    string workerUser,
    string password)
{
    // Solvers call Snapshot() inside their inner grind loop, so the read side
    // must not take a lock. An early version of the CryptoNote sibling did and
    // cost nm 15% hashrate by serialising 12 solver threads on one mutex; gr
    // only hid the same defect because GhostRider is ~10x slower per hash.
    //
    // Everything a solver needs is bundled into one immutable BitcoinStratumWork
    // and swapped by a single volatile reference write, so a reader never sees a
    // torn mix of old job with new extranonce. Writes are rare, reads constant.
    private volatile BitcoinStratumWork _work = new(null, "", 4, 1.0, 0);
    private readonly object _writeLock = new();

    private long _accepted;
    private long _rejected;

    public long Accepted => Interlocked.Read(ref _accepted);
    public long Rejected => Interlocked.Read(ref _rejected);

    /// <summary>Raised on each mining.notify, after the job is installed.</summary>
    public event Action<BitcoinStratumJob>? JobReceived;

    /// <summary>Raised on each mining.set_difficulty.</summary>
    public event Action<double>? DifficultyChanged;

    /// <summary>
    /// Atomic snapshot. Solvers must take job, extranonce and difficulty
    /// together — reading them separately can pair a new job's id with the
    /// previous job's extranonce.
    /// </summary>
    public BitcoinStratumWork Snapshot() => _work;

    /// <summary>
    /// mining.subscribe then mining.authorize. Both are awaited, so the caller's
    /// read loop MUST already be running or the responses can never arrive.
    /// A pool that answers notify before the subscribe reply is fine: the job is
    /// installed by the read loop and the solver waits for a non-empty
    /// extranonce1 anyway.
    /// </summary>
    public async Task HandshakeAsync(string agent, CancellationToken ct)
    {
        var sub = await session.CallAsync("mining.subscribe", StratumJson.StrArray(agent), ct)
                               .ConfigureAwait(false);
        ApplySubscribeResult(sub);

        await session.CallAsync("mining.authorize", StratumJson.StrArray(workerUser, password), ct)
                     .ConfigureAwait(false);
    }

    /// <summary>Read frames until the pool closes or <paramref name="ct"/> fires.</summary>
    public Task ReadLoopAsync(CancellationToken ct) =>
        session.ReadLoopAsync(OnNotification, onUnmatchedResponse: null, ct);

    // The subscribe result is [[subscriptions…], extranonce1, extranonce2_size].
    // Some pools omit the size; 4 is the near-universal default and what every
    // one of these clients assumed before.
    private void ApplySubscribeResult(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() < 2) return;

        var arr = result.EnumerateArray().ToList();
        if (arr[1].ValueKind != JsonValueKind.String) return;

        string e1 = arr[1].GetString() ?? "";
        int size = arr.Count >= 3 && arr[2].ValueKind == JsonValueKind.Number ? arr[2].GetInt32() : 4;
        lock (_writeLock)
        {
            _work = _work with { Extranonce1 = e1, Extranonce2Size = size };
        }
        log.LogInformation("{Tag}: subscribed extranonce1={E1} en2size={Size}", tag, e1, size);
    }

    private void OnNotification(string method, JsonElement root)
    {
        if (!root.TryGetProperty("params", out var p) || p.ValueKind != JsonValueKind.Array) return;

        switch (method)
        {
            case "mining.notify":
                OnNotify(p);
                break;

            case "mining.set_difficulty":
                if (p.GetArrayLength() > 0 && p[0].TryGetDouble(out var diff))
                {
                    lock (_writeLock) _work = _work with { Difficulty = diff };
                    log.LogInformation("{Tag}: difficulty set to {Diff}", tag, diff);
                    DifficultyChanged?.Invoke(diff);
                }
                break;
        }
    }

    private void OnNotify(JsonElement p)
    {
        BitcoinStratumJob job;
        try
        {
            var branch = new List<string>();
            foreach (var el in p[4].EnumerateArray()) branch.Add(el.GetString() ?? "");

            job = new BitcoinStratumJob(
                JobId: p[0].GetString() ?? "",
                // Raw, unreversed: gr swab32s it into the header later, csd
                // reverses the whole 32 bytes. Normalising here would silently
                // break one of them.
                PrevHashRaw: Akoya.Crypto.Hex.Decode(p[1].GetString() ?? ""),
                Coinb1: p[2].GetString() ?? "",
                Coinb2: p[3].GetString() ?? "",
                Branch: branch,
                Version: Convert.ToUInt32(p[5].GetString(), 16),
                Bits: Convert.ToUInt32(p[6].GetString(), 16),
                // u64: csd's ntime is a 16-hex field, gr's is 8. Parsing the
                // wider type covers both without a per-algo switch.
                Time: Convert.ToUInt64(p[7].GetString(), 16),
                NbitsHex: p[6].GetString() ?? "",
                NtimeHex: p[7].GetString() ?? "",
                Clean: p.GetArrayLength() > 8 && p[8].ValueKind == JsonValueKind.True);
        }
        catch (Exception ex)
        {
            // A malformed frame is the pool's problem. Keep mining the job we
            // already have rather than tearing down a working session.
            log.LogWarning("{Tag}: malformed mining.notify ({Msg})", tag, ex.Message);
            return;
        }

        lock (_writeLock)
        {
            _work = _work with { Job = job, Generation = _work.Generation + 1 };
        }
        log.LogInformation("{Tag}: job={Job} version={Ver:x8} nbits={Bits} ntime={Time} branch={Count}",
            tag, job.JobId, job.Version, job.NbitsHex, job.NtimeHex, job.Branch.Count);
        JobReceived?.Invoke(job);
    }

    /// <summary>
    /// Submit a share and wait for the pool's verdict. Returns true if accepted.
    /// The response is correlated by request id, so concurrent submits from
    /// different devices cannot be credited to each other — see the note at the
    /// top of this file.
    /// </summary>
    public async Task<bool> SubmitAsync(
        string jobId, string extranonce2Hex, string ntimeHex, string nonceHex, CancellationToken ct)
    {
        try
        {
            var result = await session.CallAsync(
                "mining.submit",
                StratumJson.StrArray(workerUser, jobId, extranonce2Hex, ntimeHex, nonceHex),
                ct).ConfigureAwait(false);

            // Pools answer true, or (rarely) null with no error member.
            bool ok = result.ValueKind is JsonValueKind.True or JsonValueKind.Null
                   || result.ValueKind == JsonValueKind.Undefined;

            if (ok) Interlocked.Increment(ref _accepted);
            else Interlocked.Increment(ref _rejected);
            return ok;
        }
        catch (PoolRpcException ex)
        {
            Interlocked.Increment(ref _rejected);
            log.LogWarning("{Tag}: share REJECTED job={Job} {Error} (a/r={A}/{R})",
                tag, jobId, ex.Message, Accepted, Rejected);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The frame never went out, or the session died waiting. Not a
            // pool rejection — do not count it as one.
            log.LogWarning("{Tag}: share submit failed job={Job}: {Msg}", tag, jobId, ex.Message);
            return false;
        }
    }
}
