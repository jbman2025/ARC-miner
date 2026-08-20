// BitcoinIII (BC3) stratum client — canonical Bitcoin Stratum V1 over the
// chain's SHA3-256t proof of work.
//
// BC3 is Bitcoin Core v29.1 with exactly one consensus change, so the protocol
// is the stock nine-field notify and the work assembly is the stock 80-byte
// header. Only the hash differs. Concretely, versus its two siblings here:
//   • the header is 80 bytes with a u32 ntime — csd's is 84 with a u64;
//   • prevhash is swab32'd per word like gr, NOT reversed whole like csd;
//   • the merkle root goes in raw, exactly as sha256d produced it.
// Getting any of those wrong is not a crash, it is a pool full of rejects, so
// all three are pinned by Sha3tGoldenVectorTests against mainnet block 56000.
//
// Work: coinbase = coinb1 + extranonce1 + extranonce2 + coinb2; merkle root =
// fold(sha256d(coinbase), branch); header = version|prev|root|time|bits|nonce;
// sha3t(header) <= target read little-endian. The nonce space is u32, so a full
// sweep rolls extranonce2 when exhausted.

using System.Diagnostics;
using Akoya.Miner.Mining.Stratum;
using Microsoft.Extensions.Logging;

namespace Akoya.Miner.Algos.Sha3t;

internal sealed class Sha3tStratumClient : IAsyncDisposable
{
    internal sealed record PoolConfig(string Host, int Port, string Address, string Worker, IReadOnlyList<int> DeviceIndices, bool UseTls);

    private readonly PoolConfig _cfg;
    private readonly ILogger _log;
    private readonly StratumSession _session;
    private BitcoinStratumDialect _dialect = null!;

    private readonly long[] _hashesPerGpu;

    // Nonces per kernel launch. sha3t is three keccak-f permutations and no
    // memory traffic, so it runs an order of magnitude slower per nonce than
    // csd's sha256d — 16M keeps a launch at roughly a tenth of a second, well
    // clear of the Windows TDR watchdog, while still amortising the host
    // round-trip. A full u32 sweep is 256 launches.
    private const uint SliceNonces = 1u << 24;

    public Sha3tStratumClient(PoolConfig cfg, ILogger log)
    {
        _cfg = cfg; _log = log;
        _hashesPerGpu = new long[cfg.DeviceIndices.Count];
        _session = new StratumSession(log, "sha3t-pool");
    }

    public async Task RunSessionAsync(CancellationToken ct)
    {
        await _session.ConnectAsync(_cfg.Host, _cfg.Port, _cfg.UseTls, ct).ConfigureAwait(false);
        Akoya.Miner.Observability.Metrics.SetPoolConnected(true);

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _dialect = new BitcoinStratumDialect(_session, _log, "sha3t-pool", WorkerUser(), "x");
        // The read loop must be running before the handshake: subscribe and
        // authorize are awaited, and their responses arrive through it.
        var readerTask = _dialect.ReadLoopAsync(sessionCts.Token);
        // One dedicated OS thread per GPU: the sha3t_capi context is
        // thread_local, so each thread opens its own device and searches a
        // disjoint slice of the work (partitioned by extranonce2). Thread-pool
        // tasks would migrate threads and break that isolation.
        var solvers = new Thread[_cfg.DeviceIndices.Count];
        try
        {
            await _dialect.HandshakeAsync(Akoya.Miner.Observability.VersionInfo.UserAgent, sessionCts.Token).ConfigureAwait(false);
            for (int ord = 0; ord < solvers.Length; ord++)
            {
                int o = ord, dev = _cfg.DeviceIndices[ord];
                solvers[ord] = new Thread(() => SolverLoop(o, dev, solvers.Length, sessionCts.Token))
                { IsBackground = true, Name = $"sha3t-solver-{o}" };
                solvers[ord].Start();
            }
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

    private void SolverLoop(int ord, int deviceIndex, int deviceCount, CancellationToken ct)
    {
        if (Sha3tNative.Open(deviceIndex) != 0)
        {
            _log.LogError("sha3t-pool: GPU[{Idx}] open failed ({Err}) — this device will not mine", deviceIndex, Sha3tNative.LastError());
            return;
        }
        _log.LogInformation("sha3t-pool: GPU[{Ord}] mining on device[{Idx}] {Name}", ord, deviceIndex, Sha3tNative.DeviceName());

        try
        {
            var found = new uint[4096];
            var statsTimer = Stopwatch.StartNew();
            var runTimer = Stopwatch.StartNew();
            string miningJob = "";
            uint en2 = (uint)ord;
            ulong baseNonce = 0;
            byte[] header = [];
            ulong[] lanes = [];
            string en2Hex = "", ntimeHex = "", jobId = "";

            while (!ct.IsCancellationRequested)
            {
                var work = _dialect.Snapshot();
                var job = work.Job;
                if (job is null || work.Extranonce1.Length == 0) { Thread.Sleep(50); continue; }

                if (job.JobId != miningJob)
                {
                    miningJob = job.JobId; en2 = (uint)ord; baseNonce = 0;
                    (header, lanes, en2Hex) = Rebuild(job, work.Extranonce1, work.Extranonce2Size, en2);
                    ntimeHex = job.NtimeHex; jobId = job.JobId;
                    if (ord == 0) _log.LogInformation("sha3t-pool: mining job={Job} diff={Diff:F0} on {N} GPU(s)", jobId, work.Difficulty, deviceCount);
                }

                // Re-read the target every slice: the pool raises difficulty
                // mid-job via vardiff, and a share found against a stale lower
                // target comes back "low difficulty".
                var tgt = Sha3tHash.PdiffTarget(work.Difficulty);

                int status = Sha3tNative.Search(lanes, tgt, (uint)baseNonce, SliceNonces, found, (uint)found.Length, out uint total);
                if (status != 0)
                {
                    _log.LogWarning("sha3t-pool: GPU[{Ord}] search failed ({Status}): {Err}", ord, status, Sha3tNative.LastError());
                    Thread.Sleep(250);
                    continue;
                }
                _hashesPerGpu[ord] += SliceNonces;

                uint nf = Math.Min(total, (uint)found.Length);
                for (uint k = 0; k < nf; ++k)
                {
                    uint nonce = found[k];
                    if (!VerifyOnHost(header, nonce, tgt, ord)) continue;
                    // The header stores the nonce little-endian; stratum submits
                    // the same u32 spelled big-endian, which is just its hex.
                    _ = ReportShareAsync(ord, jobId, en2Hex, ntimeHex, nonce.ToString("x8"), ct);
                }

                baseNonce += SliceNonces;
                // Beat on every completed slice: the dashboard reads heartbeat
                // age as liveness and calls a worker stale after 5s.
                Akoya.Miner.Observability.Metrics.TouchHeartbeat(ord);

                if (baseNonce >= 0x100000000UL)
                {
                    baseNonce = 0; en2 += (uint)deviceCount;   // next coinbase in this GPU's stride
                    (header, lanes, en2Hex) = Rebuild(job, work.Extranonce1, work.Extranonce2Size, en2);
                }

                if (statsTimer.Elapsed.TotalSeconds >= 10)
                {
                    statsTimer.Restart();
                    double hps = _hashesPerGpu[ord] / runTimer.Elapsed.TotalSeconds;
                    // hashesPerSec is the 4th arg. NOT SetHashRate: like csd,
                    // sha3t has no iteration concept and deliberately reports
                    // itersPerSec=0 rather than aliasing it to the hash rate.
                    Akoya.Miner.Observability.Metrics.SetThroughput(ord, 0, 0, hps, 0);
                    Akoya.Miner.Observability.Metrics.SetDiff(ord, Akoya.Miner.Observability.DisplayFormat.DiffValue(work.Difficulty));
                    if (!Akoya.Miner.Observability.Dashboard.Active)
                        _log.LogInformation("sha3t-pool: GPU[{Ord}] {Mhs:F2} MH/s diff={Diff:F0} a/r={Acc}/{Rej} (en2={En2:x8})",
                            ord, hps / 1e6, work.Difficulty, _dialect.Accepted, _dialect.Rejected, en2);
                }
            }
        }
        finally { Sha3tNative.Close(); }   // free this thread's device context
    }

    /// <summary>Re-hash a candidate on the CPU before it goes to the pool.</summary>
    /// <remarks>
    /// The kernel is the only thing standing between a driver hiccup and a
    /// stream of bogus submits, and pools ban for those. One SHA3 triple per
    /// FOUND nonce is free next to the 16M hashes that produced it, so there is
    /// no reason not to check. Skipped only where the platform has no SHA-3.
    /// </remarks>
    private bool VerifyOnHost(byte[] header, uint nonce, ulong[] target, int ord)
    {
        if (!System.Security.Cryptography.SHA3_256.IsSupported) return true;

        var h = (byte[])header.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(76, 4), nonce);
        if (Sha3tHash.MeetsTarget(Sha3tHash.Sha3t(h), target)) return true;

        _log.LogWarning("sha3t-pool: GPU[{Ord}] kernel reported nonce {Nonce:x8} that does not verify on the host — not submitting", ord, nonce);
        return false;
    }

    /// <summary>Header bytes + the ten kernel lanes for one (job, extranonce2).</summary>
    internal static (byte[] header, ulong[] lanes, string en2Hex) Rebuild(
        BitcoinStratumJob job, string e1, int e2sz, uint en2)
    {
        string en2Hex = BitcoinStratumJob.FormatExtranonce2(en2, e2sz);
        var root = job.MerkleRoot(e1, en2Hex);

        // prevhash arrives byte-swapped PER 32-BIT WORD; csd's whole-32-byte
        // reversal is the other convention in this codebase and is wrong here.
        var prev = (byte[])job.PrevHashRaw.Clone();
        Sha3tHash.Swab32(prev);

        var hdr = new byte[80];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0, 4), job.Version);
        prev.CopyTo(hdr, 4);
        root.CopyTo(hdr, 36);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(68, 4), (uint)job.Time);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(72, 4), job.Bits);
        // hdr[76..80] is the nonce slot; the kernel fills it per work-item.

        return (hdr, Sha3tHash.HeaderLanes(hdr), en2Hex);
    }

    // Awaits the pool's verdict for ONE share and credits the GPU that found
    // it — the submit id correlates the response, so concurrent submits from
    // different devices cannot be credited to each other.
    private async Task ReportShareAsync(
        int gpuOrd, string jobId, string en2Hex, string ntimeHex, string nonceHex, CancellationToken ct)
    {
        bool ok = await _dialect.SubmitAsync(jobId, en2Hex, ntimeHex, nonceHex, ct).ConfigureAwait(false);
        if (ok)
        {
            Akoya.Miner.Observability.Metrics.IncShareAccepted(gpuOrd);
            _log.LogInformation("sha3t-pool: GPU[{Gpu}] share OK (a/r={Acc}/{Rej})",
                gpuOrd, _dialect.Accepted, _dialect.Rejected);
        }
        else
        {
            Akoya.Miner.Observability.Metrics.IncShareRejected(gpuOrd);
        }
    }

    public ValueTask DisposeAsync() => _session.DisposeAsync();
}
