// CSD (Compute Substrate) stratum client — canonical Bitcoin Stratum V1 over
// sha256d. Validated end-to-end against csd-ca.lproute.com (accepted shares).
//
// Protocol (captured from the reference forge miner + live pool):
//   mining.subscribe -> [[..subs..], extranonce1(4B), extranonce2_size]
//   mining.authorize [<addr>.<worker>, x] -> true
//   mining.set_difficulty [d]
//   mining.notify [job_id, prevhash, coinb1, coinb2, [branch], version, nbits, ntime, clean]
//   mining.submit [<addr>.<worker>, job_id, extranonce2, ntime, nonce] -> true/err
//
// Work: coinbase = coinb1 + extranonce1 + extranonce2 + coinb2; merkle root =
// fold(sha256d(coinbase), branch); header (84 B, u64 time) = version|prev|root|
// time|bits|nonce; sha256d(header) <= target. Nonce space is u32, so the full
// sweep rolls extranonce2 when exhausted.

using System.Diagnostics;
using System.Text.Json;
using Akoya.Miner.Mining.Stratum;
using Microsoft.Extensions.Logging;

namespace Akoya.Miner.Algos.Csd;

internal sealed class CsdStratumClient : IAsyncDisposable
{
    internal sealed record PoolConfig(string Host, int Port, string Address, string Worker, IReadOnlyList<int> DeviceIndices, bool UseTls);

    private readonly PoolConfig _cfg;
    private readonly ILogger _log;
    // Transport, framing and id correlation live in StratumSession.
    private readonly StratumSession _session;

    // Handshake, extranonce/difficulty state, mining.notify parsing,
    // coinbase+merkle and id-correlated submits live in BitcoinStratumDialect.
    //
    // That deletes this class's most delicate machinery. Every submit used to
    // carry a hardcoded id=4, so the pool's ack could not be correlated to a
    // request at all; attribution was therefore done by ARRIVAL ORDER through a
    // FIFO List<int> of GPU ordinals, guarded by an extra SemaphoreSlim, with a
    // rule that a failed send must remove the TAIL — get that wrong and every
    // later share is credited to the wrong device for the rest of the session.
    // The shared dialect gives each submit a unique id and awaits its verdict,
    // so the GPU that found the share is the one that learns its fate.
    private BitcoinStratumDialect _dialect = null!;

    private readonly long[] _hashesPerGpu;

    // 256M nonces per launch: at ~1.4 GH/s that is ~0.18 s, far under the
    // Windows TDR watchdog, and cuts host round-trips (wait + P/Invoke marshal)
    // 16x versus a 16M slice. A full u32 sweep is then 16 launches, not 256.
    private const uint SliceNonces = 1u << 28;


    public CsdStratumClient(PoolConfig cfg, ILogger log)
    {
        _cfg = cfg; _log = log;
        _hashesPerGpu = new long[cfg.DeviceIndices.Count];
        _session = new StratumSession(log, "csd-pool");
    }

    public async Task RunSessionAsync(CancellationToken ct)
    {
        await _session.ConnectAsync(_cfg.Host, _cfg.Port, _cfg.UseTls, ct).ConfigureAwait(false);
        Akoya.Miner.Observability.Metrics.SetPoolConnected(true);

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _dialect = new BitcoinStratumDialect(_session, _log, "csd-pool", WorkerUser(), "x");
        // The read loop must be running before the handshake: subscribe and
        // authorize are awaited now, and their responses arrive through it.
        var readerTask = _dialect.ReadLoopAsync(sessionCts.Token);
        // One dedicated OS thread per GPU: the csd_capi context is thread_local,
        // so each thread opens its own device and searches a disjoint slice of
        // the work (partitioned by extranonce2). Thread-pool tasks would migrate
        // threads and break that isolation, so these must be real Threads.
        var solvers = new Thread[_cfg.DeviceIndices.Count];
        try
        {
            await _dialect.HandshakeAsync(Akoya.Miner.Observability.VersionInfo.UserAgent, sessionCts.Token).ConfigureAwait(false);
            for (int ord = 0; ord < solvers.Length; ord++)
            {
                int o = ord, dev = _cfg.DeviceIndices[ord];
                solvers[ord] = new Thread(() => SolverLoop(o, dev, solvers.Length, sessionCts.Token))
                { IsBackground = true, Name = $"csd-solver-{o}" };
                solvers[ord].Start();
            }
            // The session ends when the reader loop ends (pool closed / error /
            // cancellation); solvers are then cancelled and joined below.
            await readerTask.ConfigureAwait(false);
        }
        finally
        {
            sessionCts.Cancel();
            try { await readerTask.ConfigureAwait(false); } catch { }
            foreach (var t in solvers) t?.Join(TimeSpan.FromSeconds(2));
            await _session.DisposeAsync().ConfigureAwait(false);
            Akoya.Miner.Observability.Metrics.SetPoolConnected(false);
        }
    }

    private string WorkerUser() => $"{_cfg.Address}.{_cfg.Worker}";

    // -------------------------------------------------------------- solver ---

    // One GPU's search loop. Runs on a dedicated thread that owns device
    // deviceIndex; the csd_capi context is thread_local so this is isolated from
    // the other GPUs' threads. Work is partitioned by extranonce2: this GPU
    // walks en2 = ord, ord+deviceCount, ord+2·deviceCount, … so each device
    // mines a disjoint coinbase space and no nonce is searched twice.
    private void SolverLoop(int ord, int deviceIndex, int deviceCount, CancellationToken ct)
    {
        if (CsdNative.Open(deviceIndex) != 0)
        {
            _log.LogError("csd-pool: GPU[{Idx}] open failed ({Err}) — this device will not mine", deviceIndex, CsdNative.LastError());
            return;
        }
        _log.LogInformation("csd-pool: GPU[{Ord}] mining on device[{Idx}] {Name}", ord, deviceIndex, CsdNative.DeviceName());

        try
        {
            var found = new uint[4096];
            var statsTimer = Stopwatch.StartNew();
            var runTimer = Stopwatch.StartNew();
            string miningJob = "";
            uint en2 = (uint)ord;
            ulong baseNonce = 0;
            uint[] mid = Array.Empty<uint>(), tail = Array.Empty<uint>(), tgt;
            string en2Hex = "", ntimeHex = "", jobId = "";

            while (!ct.IsCancellationRequested)
            {
                var work = _dialect.Snapshot();
                var job = work.Job;
                if (job is null || work.Extranonce1.Length == 0) { Thread.Sleep(50); continue; }

                if (job.JobId != miningJob)
                {
                    miningJob = job.JobId; en2 = (uint)ord; baseNonce = 0;
                    (mid, tail, en2Hex) = Rebuild(job, work.Extranonce1, work.Extranonce2Size, en2);
                    ntimeHex = job.NtimeHex; jobId = job.JobId;
                    if (ord == 0) _log.LogInformation("csd-pool: mining job={Job} diff={Diff:F0} on {N} GPU(s)", jobId, work.Difficulty, deviceCount);
                }

                // Target tracks the CURRENT difficulty every slice: the pool raises
                // it via mid-job set_difficulty (vardiff), and a share found against
                // a stale lower target is rejected code-23 "low difficulty".
                tgt = CsdHash.PdiffTarget(work.Difficulty);

                int status = CsdNative.Search(mid, tail, tgt, (uint)baseNonce, SliceNonces, found, (uint)found.Length, out uint total);
                if (status != 0)
                {
                    _log.LogWarning("csd-pool: GPU[{Ord}] search failed ({Status}): {Err}", ord, status, CsdNative.LastError());
                    Thread.Sleep(250);
                    continue;
                }
                _hashesPerGpu[ord] += SliceNonces;
                uint nf = Math.Min(total, (uint)found.Length);
                for (uint k = 0; k < nf; ++k)
                {
                    uint w = found[k];
                    // Header nonce bytes = big-endian(w) (what the kernel hashed);
                    // the submit hex is the little-endian order (verified against
                    // accepted reference-miner shares).
                    var nb = new byte[] { (byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w };
                    string nonceHex = CsdHash.Hex(new[] { nb[3], nb[2], nb[1], nb[0] });
                    _ = ReportShareAsync(ord, jobId, en2Hex, ntimeHex, nonceHex, ct);
                }

                baseNonce += SliceNonces;
                // Beat on every completed slice, NOT inside the 10s stats block
                // below. The dashboard reads heartbeat age as liveness and calls
                // a worker "stale" after 5s, so a heartbeat that only ticked with
                // the stats timer made two perfectly healthy B580s render as
                // permanently wounded while hashing at 1.33 GH/s each.
                Akoya.Miner.Observability.Metrics.TouchHeartbeat(ord);

                if (baseNonce >= 0x100000000UL)
                {
                    baseNonce = 0; en2 += (uint)deviceCount;   // next coinbase in this GPU's stride
                    (mid, tail, en2Hex) = Rebuild(job, work.Extranonce1, work.Extranonce2Size, en2);
                }

                if (statsTimer.Elapsed.TotalSeconds >= 10)
                {
                    statsTimer.Restart();
                    double hps = _hashesPerGpu[ord] / runTimer.Elapsed.TotalSeconds;
                    // hashesPerSec is the 4th arg — the slot the dashboard reads.
                    // NOT SetHashRate: csd deliberately reports itersPerSec=0
                    // (it has no iteration concept), and SetHashRate would set
                    // it equal to the hash rate — changing the exported
                    // arc_miner_iters_per_second gauge.
                    Akoya.Miner.Observability.Metrics.SetThroughput(ord, 0, 0, hps, 0);
                    Akoya.Miner.Observability.Metrics.SetDiff(ord, Akoya.Miner.Observability.DisplayFormat.DiffValue(work.Difficulty));
                    // Redundant with the dashboard's per-worker table when it is
                    // active, and at one line per GPU every 10s it crowds out
                    // every real event. Metrics above still feeds the table.
                    if (!Akoya.Miner.Observability.Dashboard.Active)
                    _log.LogInformation("csd-pool: GPU[{Ord}] {Ghs:F2} GH/s diff={Diff:F0} a/r={Acc}/{Rej} (en2={En2:x8})",
                        ord, hps / 1e9, work.Difficulty, _dialect.Accepted, _dialect.Rejected, en2);
                }
            }
        }
        finally { CsdNative.Close(); }   // free this thread's device context
    }

    internal static (uint[] mid, uint[] tail, string en2Hex) Rebuild(BitcoinStratumJob job, string e1, int e2sz, uint en2)
    {
        string en2Hex = BitcoinStratumJob.FormatExtranonce2(en2, e2sz);
        var root = job.MerkleRoot(e1, en2Hex);

        // prevhash arrives byte-reversed relative to the header layout; csd
        // wants the full 32-byte reversal (verified against accepted
        // reference-miner shares). gr, sharing the same dialect, applies a
        // per-word swab32 instead — which is why the dialect keeps the field RAW
        // and each algo converts here.
        var prev = (byte[])job.PrevHashRaw.Clone();
        Array.Reverse(prev);

        var hdr = new byte[84];
        hdr[0] = (byte)job.Version; hdr[1] = (byte)(job.Version >> 8); hdr[2] = (byte)(job.Version >> 16); hdr[3] = (byte)(job.Version >> 24);
        Array.Copy(prev, 0, hdr, 4, 32);
        Array.Copy(root, 0, hdr, 36, 32);
        for (int i = 0; i < 8; ++i) hdr[68 + i] = (byte)(job.Time >> (8 * i));
        hdr[76] = (byte)job.Bits; hdr[77] = (byte)(job.Bits >> 8); hdr[78] = (byte)(job.Bits >> 16); hdr[79] = (byte)(job.Bits >> 24);

        var mid = CsdHash.Midstate(hdr.AsSpan(0, 64));
        var tail = new uint[] { CsdHash.Be32(hdr.AsSpan(64, 4)), CsdHash.Be32(hdr.AsSpan(68, 4)), CsdHash.Be32(hdr.AsSpan(72, 4)), CsdHash.Be32(hdr.AsSpan(76, 4)), 0 };
        return (mid, tail, en2Hex);
    }

    // Awaits the pool's verdict for ONE share and credits the GPU that found it.
    //
    // The old version could not do this. Every submit went out with id=4, so the
    // ack carried nothing to correlate against and the device had to be inferred
    // from arrival order via a FIFO. Two GPUs submitting concurrently, or one
    // send that threw, and the queue shifted — mis-crediting every later share.
    // With a unique id per submit, gpuOrd is simply captured in this closure.
    private async Task ReportShareAsync(
        int gpuOrd, string jobId, string en2Hex, string ntimeHex, string nonceHex, CancellationToken ct)
    {
        bool ok = await _dialect.SubmitAsync(jobId, en2Hex, ntimeHex, nonceHex, ct).ConfigureAwait(false);
        if (ok)
        {
            Akoya.Miner.Observability.Metrics.IncShareAccepted(gpuOrd);
            _log.LogInformation("csd-pool: GPU[{Gpu}] share OK (a/r={Acc}/{Rej})",
                gpuOrd, _dialect.Accepted, _dialect.Rejected);
        }
        else
        {
            Akoya.Miner.Observability.Metrics.IncShareRejected(gpuOrd);
        }
    }

    public ValueTask DisposeAsync() => _session.DisposeAsync();
}
