using Akoya.Crypto;
using Akoya.Miner.Algos.Cpu;
using System.Buffers.Binary;
using System.Numerics;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Akoya.Miner.Mining.Stratum;
using Akoya.Miner.Observability;

namespace Akoya.Miner.Algos.Rx;

internal sealed class RxPoolClient : IAsyncDisposable
{
    internal sealed record PoolConfig(
        string Host,
        int Port,
        string Address,
        string Worker,
        string Password,
        int Threads,
        bool LightMode,
        bool LargePages,
        bool Affinity,
        bool UseTls,
        int KeepaliveSec);

    private readonly PoolConfig _cfg;
    private readonly ILogger _log;
    // Transport, framing and id/response correlation live in StratumSession —
    // see the note there on why the five clients no longer each own a copy.
    private readonly StratumSession _session;

    // login / job pushes / keepalived / submit live in
    // CryptoNoteStratumDialect. What stays here is RandomX's own: the TWO blob
    // layouts (Monero nonce@39 vs Bitcoin-header nonce@76), the compact target,
    // the vardiff stale-share guard, and seed-driven VM re-init.
    private CryptoNoteStratumDialect _dialect = null!;
    private byte[]? _currentSeed;
    private readonly long[] _counts;
    private static int CpuIndex => Metrics.CpuIndex >= 0 ? Metrics.CpuIndex : 0;

    public RxPoolClient(PoolConfig cfg, ILogger log)
    {
        _cfg = cfg;
        _log = log;
        _counts = new long[cfg.Threads * 8];
        _session = new StratumSession(log, "rx-pool");
    }

    public async Task RunSessionAsync(CancellationToken ct)
    {
        await _session.ConnectAsync(_cfg.Host, _cfg.Port, _cfg.UseTls, ct).ConfigureAwait(false);

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _dialect = new CryptoNoteStratumDialect(_session, _log, "rx-pool", ParsePoolJob);
        _dialect.JobReceived += j =>
        {
            Metrics.SetDiff(CpuIndex, Akoya.Miner.Observability.DisplayFormat.DiffValue(ParseDifficulty(j.Target)));
            Metrics.SetCpuBlockHeight(j.Height);
            _log.LogInformation("rx-pool: new job={Job} height={Height} blob={Blob}B nonce@{Off}{Width}",
                j.JobId, j.Height, j.Blob.Length, j.NonceOffset, j.FullWidthNonce ? "/32" : "/24");
        };
        // Order matters: the read loop must be running before LoginAsync awaits.
        var readerTask = _dialect.ReadLoopAsync(sessionCts.Token);
        var submitQueue = new ConcurrentQueue<(string JobId, uint Nonce, byte[] Hash)>();
        var submitTask = SubmitLoopAsync(submitQueue, sessionCts.Token);
        var keepaliveTask = _dialect.KeepAliveLoopAsync(_cfg.KeepaliveSec, sessionCts.Token);

        Thread[]? solvers = null;
        CancellationTokenSource? solverCts = null;

        try
        {
            await _dialect.LoginAsync(StratumJson.Obj(
                ("login", _cfg.Address),
                ("pass", _cfg.Password),
                ("agent", VersionInfo.UserAgent)), sessionCts.Token).ConfigureAwait(false);
            Metrics.SetCpuPoolConnected(true);

            var reportSw = Stopwatch.StartNew();
            long lastTotal = 0; double lastSec = 0;

            while (!sessionCts.Token.IsCancellationRequested)
            {
                var (activeJob, _) = _dialect.Snapshot();

                if (activeJob is null)
                {
                    await Task.Delay(100, sessionCts.Token).ConfigureAwait(false);
                    continue;
                }

                // Check if we need to initialize or re-initialize RandomX due to seed change
                if (_currentSeed is null || !_currentSeed.AsSpan().SequenceEqual(activeJob.SeedHash))
                {
                    if (solvers is not null && solverCts is not null)
                    {
                        _log.LogInformation("rx-pool: seed changed, stopping solver threads to re-seed...");
                        solverCts.Cancel();
                        foreach (var t in solvers) t.Join(TimeSpan.FromSeconds(2));
                        solverCts.Dispose();
                    }

                    _log.LogInformation("rx-pool: init RandomX seed {Seed} ({Threads} threads)...",
                        Convert.ToHexString(activeJob.SeedHash)[..12], _cfg.Threads);
                    
                    bool fullMem = !_cfg.LightMode;
                    int rc = RxNative.Init(activeJob.SeedHash, (uint)activeJob.SeedHash.Length, fullMem ? 1 : 0, _cfg.LargePages ? 1 : 0, _cfg.Threads);
                    if (rc != 0) throw new InvalidOperationException($"RandomX init failed ({rc}): {RxNative.LastError()}");

                    _currentSeed = activeJob.SeedHash;

                    solverCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token);
                    solvers = new Thread[_cfg.Threads];

                    // Dedicated OS threads, NOT Task.Run. A solver never yields:
                    // on the thread pool, _cfg.Threads of them would occupy every
                    // pool thread and starve the reader / submit / keepalive tasks
                    // that share it — and the pool only grows by one thread per
                    // second once saturated, so the session would look alive while
                    // shares never left. Dedicated threads also let us pin them.
                    var pinOrder = CpuAffinity.BuildPinOrder(_cfg.Threads);
                    for (int i = 0; i < _cfg.Threads; i++)
                    {
                        int idx = i;
                        int cpu = _cfg.Affinity ? pinOrder[i % pinOrder.Length] : -1;
                        solvers[i] = new Thread(() => SolverThread(idx, _cfg.Threads, cpu, _dialect, _counts, submitQueue, _log, solverCts.Token))
                        {
                            IsBackground = true,
                            Name = $"rx-pool-solver-{idx}",
                            Priority = ThreadPriority.Normal
                        };
                        solvers[i].Start();
                    }
                    _log.LogInformation("rx-pool: solver threads started");
                }

                // Report throughput to Metrics periodically
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), sessionCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                long total = 0;
                for (int i = 0; i < _cfg.Threads; i++) total += Volatile.Read(ref _counts[i * 8]);
                double now = reportSw.Elapsed.TotalSeconds, dt = now - lastSec;
                double hs = dt > 0 ? (total - lastTotal) / dt : 0;
                lastTotal = total; lastSec = now;
                Metrics.SetHashRate(CpuIndex, hs, hs > 0 ? 1000.0 * _cfg.Threads / hs : 0);
                // Redundant with the dashboard's per-worker table when it is
                // active — and at one line every few seconds it crowds every
                // real event out of the panel's log pane. Metrics above still
                // feeds the table. Mirrors GpuWorker's guard.
                if (!Akoya.Miner.Observability.Dashboard.Active)
                _log.LogInformation("rx-pool: {Hs:F1} H/s ({Threads} threads, {Total} hashes) | shares: {Acc}/{Rej} | diff: {Diff}",
                    hs, _cfg.Threads, total, _dialect.Accepted, _dialect.Rejected, (long)Math.Round(GetCurrentDifficulty()));

                if (readerTask.IsCompleted)
                {
                    await readerTask.ConfigureAwait(false);
                    throw new IOException("pool reader task completed unexpectedly");
                }
                if (submitTask.IsCompleted)
                {
                    await submitTask.ConfigureAwait(false);
                    throw new IOException("pool submit task completed unexpectedly");
                }
                if (keepaliveTask.IsCompleted)
                {
                    await keepaliveTask.ConfigureAwait(false);
                    throw new IOException("pool keepalive task completed unexpectedly");
                }
            }
        }
        finally
        {
            sessionCts.Cancel();
            if (solverCts is not null)
            {
                solverCts.Cancel();
                if (solvers is not null)
                {
                    foreach (var t in solvers) t.Join(TimeSpan.FromSeconds(2));
                }
                solverCts.Dispose();
            }

            try { await readerTask.ConfigureAwait(false); } catch { }
            try { await submitTask.ConfigureAwait(false); } catch { }
            try { await keepaliveTask.ConfigureAwait(false); } catch { }

            await _session.DisposeAsync().ConfigureAwait(false);
            Metrics.SetCpuPoolConnected(false);
        }
    }

    private async Task SubmitLoopAsync(ConcurrentQueue<(string JobId, uint Nonce, byte[] Hash)> queue, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!queue.TryDequeue(out var hit))
                {
                    await Task.Delay(10, ct).ConfigureAwait(false);
                    continue;
                }

                var (current, _) = _dialect.Snapshot();

                // Vardiff races the solver: the pool can tighten the target
                // mid-job, and some pools (Blockzero/suprnova) reuse the job_id
                // when they do. A hash found against the previous, easier target
                // is then a guaranteed code-23 "low-difficulty" reject — drop it
                // rather than spend a round trip being told, and keep the reject
                // counter meaningful for real problems.
                if (current is not null && current.JobId == hit.JobId &&
                    !Le256(hit.Hash, current.Target))
                {
                    _log.LogDebug("rx-pool: dropping share job={Job} nonce={Nonce} — target tightened since it was found",
                        hit.JobId, hit.Nonce);
                    continue;
                }

                // RandomX submits only the 4 nonce bytes; nm submits all 8.
                bool ok = await _dialect.SubmitAsync(
                    hit.JobId,
                    Convert.ToHexString(BitConverter.GetBytes(hit.Nonce)).ToLowerInvariant(),
                    Convert.ToHexString(hit.Hash).ToLowerInvariant(),
                    ct).ConfigureAwait(false);

                if (ok)
                {
                    Metrics.IncShareAccepted(CpuIndex);
                    _log.LogInformation("rx-pool: share accepted job={Job} nonce={Nonce} (a/r={Acc}/{Rej})",
                        hit.JobId, hit.Nonce, _dialect.Accepted, _dialect.Rejected);
                }
                else
                {
                    Metrics.IncShareRejected(CpuIndex);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // Where the nonce lives inside the blob, and how much of it we may search.
    //
    // Two layouts share this dialect:
    //
    //  • Monero / CryptoNote (rx/0 and friends): variable-length hashing blob,
    //    4-byte nonce at offset 39. By convention only the low 3 bytes are
    //    searched — the top byte is reserved for a NiceHash/proxy extranonce
    //    and must be preserved.
    //
    //  • RandomX over a Bitcoin-style header (e.g. Blockzero's rx/blockzero):
    //    an exactly-80-byte header, version|prevhash|merkle|ntime|nbits|nonce,
    //    with the 4-byte nonce LAST at offset 76 and no extranonce in it, so
    //    the full 32 bits are ours.
    //
    // Writing the Monero offset into an 80-byte header lands in the middle of
    // the merkle root: the header is corrupted, the real nonce stays zero, and
    // the pool — which recomputes with our nonce at 76 — sees a completely
    // different hash and rejects every share as "low-difficulty".
    //
    // ARC_RX_NONCE_OFFSET forces the offset if another coin turns up with a
    // layout this heuristic does not cover.
    private const int MoneroNonceOffset = 39;
    private const int BitcoinHeaderBytes = 80;
    private const int BitcoinHeaderNonceOffset = BitcoinHeaderBytes - 4;

    // internal, not private, so the layout rules can be unit-tested against a
    // captured job without reflection.
    internal static (int Offset, bool FullWidth) NonceLayout(byte[] blob, string algo)
    {
        var forced = Environment.GetEnvironmentVariable("ARC_RX_NONCE_OFFSET");
        if (int.TryParse(forced, out var f) && f >= 0 && f + 4 <= blob.Length)
        {
            return (f, true);
        }

        if (blob.Length == BitcoinHeaderBytes)
        {
            return (BitcoinHeaderNonceOffset, true);
        }

        if (blob.Length < MoneroNonceOffset + 4)
        {
            throw new InvalidOperationException(
                $"rx: pool sent a {blob.Length}-byte blob (algo={algo}), too short for a nonce at {MoneroNonceOffset}");
        }
        return (MoneroNonceOffset, false);
    }

    private static CryptoNoteJob ParsePoolJob(JsonElement el)
    {
        var jobId = el.GetProperty("job_id").GetString() ?? "";
        var blobHex = el.GetProperty("blob").GetString() ?? "";
        var targetHex = el.GetProperty("target").GetString() ?? "";
        var seedHex = el.GetProperty("seed_hash").GetString() ?? "";
        var height = el.GetProperty("height").GetInt64();
        var algo = el.TryGetProperty("algo", out var aEl) ? aEl.GetString() ?? "" : "";

        var blob = Convert.FromHexString(blobHex);
        var seedHash = Convert.FromHexString(seedHex);
        var (nonceOffset, fullWidthNonce) = NonceLayout(blob, algo);

        var targetBytes = Convert.FromHexString(targetHex);
        var target = new byte[32];
        if (targetBytes.Length < 32)
        {
            Array.Fill(target, (byte)0xFF);
            Array.Copy(targetBytes, 0, target, 32 - targetBytes.Length, targetBytes.Length);
        }
        else
        {
            Array.Copy(targetBytes, target, Math.Min(targetBytes.Length, 32));
        }

        return new CryptoNoteJob(jobId, blob, target, seedHash, height, nonceOffset, fullWidthNonce);
    }

    private double GetCurrentDifficulty()
    {
        var (job, _) = _dialect.Snapshot();
        return job is null ? 0 : ParseDifficulty(job.Target);
    }

    private static double ParseDifficulty(byte[] target)
    {
        var t = new BigInteger(target, isUnsigned: true, isBigEndian: false);
        if (t.IsZero) return 0;

        var dividendBytes = new byte[33];
        dividendBytes[32] = 0x01;
        var dividend = new BigInteger(dividendBytes, isUnsigned: true, isBigEndian: false);

        return (double)(dividend / t);
    }

    private static unsafe void SolverThread(int idx, int threads, int cpu, CryptoNoteStratumDialect box, long[] counts,
        ConcurrentQueue<(string JobId, uint Nonce, byte[] Hash)> hits, ILogger log, CancellationToken ct)
    {
        CpuAffinity.PinCurrentThread(cpu);

        nint vm = RxNative.CreateVm();
        if (vm == nint.Zero) { log.LogError("rx-pool: worker {Idx} VM create failed — {Err}", idx, RxNative.LastError()); return; }
        try
        {
            long lastGen = -1;
            byte[] blob = Array.Empty<byte>();
            byte[] target = Array.Empty<byte>();
            string jobId = "";
            int nonceOffset = MoneroNonceOffset;
            bool fullWidthNonce = false;
            uint nonce = (uint)idx;
            long local = 0;
            var outbuf = new byte[RxNative.HashBytes];

            // Pipelined hashing (XMRig first/next): HashNext emits the hash of the
            // input given on the PREVIOUS call while starting the next, overlapping
            // the scratchpad fill with program execution. The emitted hash belongs to
            // the previous nonce — and, since the job can change while a hash is in
            // flight, to that nonce's own job/target — so we carry those alongside.
            bool primed = false;
            string pendingJobId = "";
            uint pendingSubmitNonce = 0;
            byte[] pendingTarget = Array.Empty<byte>();

            while (!ct.IsCancellationRequested)
            {
                var (j, gen) = box.Snapshot();
                // The old JobBox returned a null-forgiving `PoolJob` here; the
                // shared dialect is honestly nullable, so guard rather than
                // trusting the start-order invariant.
                if (j is null) { Thread.Sleep(10); continue; }
                if (gen != lastGen)
                {
                    bool resetNonce = jobId != j.JobId;
                    blob = (byte[])j.Blob.Clone();
                    target = j.Target;
                    jobId = j.JobId;
                    nonceOffset = j.NonceOffset;
                    fullWidthNonce = j.FullWidthNonce;
                    lastGen = gen;
                    if (resetNonce)
                    {
                        nonce = (uint)idx;
                    }
                }

                uint submittedNonce;
                if (fullWidthNonce)
                {
                    // Bitcoin-style header: the whole 32-bit field is ours.
                    BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(nonceOffset, 4), nonce);
                    submittedNonce = nonce;
                }
                else
                {
                    // NiceHash/proxy compatibility: the high byte of the Monero
                    // nonce is the pool's extranonce — preserve it and iterate
                    // only the low 3 bytes.
                    uint searchNonce = nonce & 0x00FFFFFF;
                    blob[nonceOffset] = (byte)(searchNonce & 0xFF);
                    blob[nonceOffset + 1] = (byte)((searchNonce >> 8) & 0xFF);
                    blob[nonceOffset + 2] = (byte)((searchNonce >> 16) & 0xFF);
                    submittedNonce = searchNonce | ((uint)blob[nonceOffset + 3] << 24);
                }

                fixed (byte* pBlob = blob)
                fixed (byte* pOut = outbuf)
                {
                    if (!primed)
                    {
                        RxNative.HashFirst(vm, pBlob, (uint)blob.Length);
                    }
                    else
                    {
                        // outbuf receives the hash of (pendingJobId, pendingSubmitNonce).
                        RxNative.HashNext(vm, pBlob, (uint)blob.Length, pOut);
                        if (Le256(outbuf, pendingTarget))
                        {
                            hits.Enqueue((pendingJobId, pendingSubmitNonce, (byte[])outbuf.Clone()));
                        }
                    }
                }
                pendingJobId = jobId;
                pendingSubmitNonce = submittedNonce;
                pendingTarget = target;
                primed = true;

                nonce += (uint)threads;
                if ((++local & 0x3F) == 0) Volatile.Write(ref counts[idx * 8], local);
                if ((local & 0x7FF) == 0) Metrics.TouchHeartbeat(Metrics.CpuIndex >= 0 ? Metrics.CpuIndex : 0);
            }
            Volatile.Write(ref counts[idx * 8], local);
        }
        finally { RxNative.DestroyVm(vm); }
    }

    private static bool Le256(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        => Uint256.LeLessOrEqual(a, b);

    private Task<JsonElement> CallAsync(string method, string paramsJson, CancellationToken ct, TimeSpan? timeout = null)
        => _session.CallAsync(method, paramsJson, ct, timeout);

    public ValueTask DisposeAsync() => _session.DisposeAsync();
}
