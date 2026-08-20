// Akoya.Miner — GhostRider (Raptoreum) solver.
//
// The Bitcoin Stratum V1 protocol — handshake, extranonce/difficulty state,
// mining.notify parsing, coinbase+merkle, and id-correlated share submission —
// lives in BitcoinStratumDialect. What remains here is GhostRider's own: the
// 80-byte header with its SELECTIVE swab32, cpuminer diff_to_target, and a
// nonce-stride threading model that never rolls extranonce2.

using System.Buffers.Binary;
using System.Diagnostics;
using Akoya.Miner.Algos.Cpu;
using Akoya.Miner.Mining.Stratum;
using Akoya.Miner.Mining;
using Akoya.Miner.Observability;
using Microsoft.Extensions.Logging;

namespace Akoya.Miner.Algos.Gr;

internal sealed class GrStratumClient : IAsyncDisposable
{
    public sealed record PoolConfig(
        string Host,
        int Port,
        string Address,
        string Worker,
        string Password,
        int Threads,
        bool UseTls,
        bool Affinity = false
    );

    private readonly PoolConfig _cfg;
    private readonly ILogger _log;

    // Protocol (handshake, extranonce/difficulty state, mining.notify parsing,
    // coinbase+merkle, submit with id-correlated acks) lives in
    // BitcoinStratumDialect. What stays here is what is genuinely GhostRider's:
    // the 80-byte header with its SELECTIVE swab32, cpuminer diff_to_target, and
    // the nonce-stride threading model.
    //
    // Created per connection rather than per client: this class owns the
    // reconnect loop and each attempt needs a fresh socket.
    private volatile BitcoinStratumDialect? _dialect;

    // Dashboard slot. GhostRider is a CPU algo, so it reports into the CPU row
    // Metrics.InitCpu appended after the GPUs — NOT index 0, which is a real GPU
    // when dual-mining (gr+prl and friends).
    private static int CpuIndex => Metrics.CpuIndex >= 0 ? Metrics.CpuIndex : 0;

    private readonly ulong[] _counts;
    private readonly CancellationTokenSource _cts = new();

    public GrStratumClient(PoolConfig cfg, ILogger log)
    {
        _cfg = cfg;
        _log = log;
        _counts = new ulong[Math.Max(1, cfg.Threads) * 8];
    }

    public async Task RunSessionAsync(CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var token = linkedCts.Token;

        int attempt = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var session = new StratumSession(_log, "gr-pool");
                await session.ConnectAsync(_cfg.Host, _cfg.Port, _cfg.UseTls, token);

                var dialect = new BitcoinStratumDialect(session, _log, "gr-pool", WorkerUser(), _cfg.Password);
                dialect.DifficultyChanged += d =>
                    Metrics.SetDiff(CpuIndex, Akoya.Miner.Observability.DisplayFormat.DiffValue(d));

                Metrics.SetCpuPoolConnected(true);
                attempt = 0;                 // a successful connect resets the ramp
                _dialect = dialect;

                // Dedicated OS threads, not Task.Run. These loops never yield, so
                // on the thread pool N of them would occupy every pool thread and
                // starve the reader/submit/reporter tasks that share it — the pool
                // only grows by one thread per second once saturated. Dedicated
                // threads also let us pin them (below).
                //
                // Scoped to this connection: cancelling sessionCts stops the
                // workers without touching `token`, so the outer reconnect loop
                // still runs.
                using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                var sessionToken = sessionCts.Token;

                int threads = Math.Max(1, _cfg.Threads);
                var workers = new Thread[threads];
                var pinOrder = CpuAffinity.BuildPinOrder(threads);
                for (int t = 0; t < threads; t++)
                {
                    int tid = t;
                    int cpu = _cfg.Affinity ? pinOrder[t % pinOrder.Length] : -1;
                    workers[t] = new Thread(() => WorkerLoop(dialect, tid, threads, cpu, sessionToken))
                    {
                        IsBackground = true,
                        Name = $"gr-solver-{tid}",
                        Priority = ThreadPriority.Normal
                    };
                    workers[t].Start();
                }
                var reporterTask = Task.Run(() => ReporterLoopAsync(sessionToken), sessionToken);

                // The read loop must be running before the handshake: subscribe
                // and authorize are awaited now, and their responses arrive
                // through it.
                try
                {
                    var reader = dialect.ReadLoopAsync(sessionToken);
                    await dialect.HandshakeAsync(VersionInfo.UserAgent, sessionToken);
                    await reader;
                }
                finally
                {
                    // Reap this session's workers before the next connect attempt
                    // allocates another set — otherwise a flapping pool leaks
                    // 16 MiB of scratchpads per thread per reconnect.
                    sessionCts.Cancel();
                    foreach (var w in workers) w.Join(TimeSpan.FromSeconds(5));
                    try { await reporterTask; } catch { }
                }
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                attempt++;
                var backoff = ReconnectBackoff.NextDelay(attempt);
                _log.LogWarning("gr: pool session failed: {Message} — retry in {Sec:F1}s (attempt {Attempt})",
                    ex.Message, backoff.TotalSeconds, attempt);
                try { await Task.Delay(backoff, token); } catch { }
            }
            finally
            {
                _dialect = null;
                Metrics.SetCpuPoolConnected(false);
            }
        }
    }

    private async Task ReporterLoopAsync(CancellationToken ct)
    {
        double lastHashes = 0;
        var sw = Stopwatch.StartNew();
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(5000, ct); } catch { break; }
            double totalHashes = GetHashrate();
            double deltaHashes = totalHashes - lastHashes;
            lastHashes = totalHashes;
            double sec = sw.Elapsed.TotalSeconds;
            sw.Restart();
            double hs = sec > 0 ? deltaHashes / sec : 0;
            Metrics.SetHashRate(CpuIndex, hs, hs > 0 ? 1000.0 * _cfg.Threads / hs : 0);
            var d = _dialect;
            // Redundant with the dashboard's per-worker table — see the note in
            // RxPoolClient. Metrics still feeds the table.
            if (!Akoya.Miner.Observability.Dashboard.Active)
            _log.LogInformation("gr-pool: {Hs:F1} H/s ({T}T) a/r={A}/{R}", hs, _cfg.Threads,
                d?.Accepted ?? 0, d?.Rejected ?? 0);
        }
    }

    private string WorkerUser() =>
        string.IsNullOrEmpty(_cfg.Worker) ? _cfg.Address : $"{_cfg.Address}.{_cfg.Worker}";

    // Awaits the pool's verdict for one share. The dialect correlates it by
    // request id, so unlike the old fire-and-forget path this cannot credit a
    // share to the wrong submitter.
    private async Task ReportShareAsync(
        BitcoinStratumDialect dialect, string jobId, string en2Hex, string ntimeHex,
        string nonceHex, CancellationToken ct)
    {
        bool ok = await dialect.SubmitAsync(jobId, en2Hex, ntimeHex, nonceHex, ct).ConfigureAwait(false);
        if (ok)
        {
            Metrics.IncShareAccepted(CpuIndex);
            _log.LogInformation("gr-pool: Share OK (a/r={A}/{R})", dialect.Accepted, dialect.Rejected);
        }
        else
        {
            Metrics.IncShareRejected(CpuIndex);
        }
    }

    public double GetHashrate()
    {
        ulong total = 0;
        int threads = Math.Max(1, _cfg.Threads);
        for (int i = 0; i < threads; i++) total += Volatile.Read(ref _counts[i * 8]);
        return total;
    }

    private unsafe void WorkerLoop(BitcoinStratumDialect dialect, int idx, int totalThreads, int cpu, CancellationToken ct)
    {
        CpuAffinity.PinCurrentThread(cpu);

        IntPtr ctx = GrNative.CreateCtx();
        if (ctx == IntPtr.Zero) return;

        const int lanes = GrNative.Lanes;

        byte[] header = new byte[80];
        byte[] blob = new byte[80 * lanes];     // 8 copies of header, differing only in nonce
        byte[] outbuf = new byte[32 * lanes];
        ulong local = 0;
        long lastJobGen = -1;

        string jobId = "";
        string ntimeHex = "";
        string en2Hex = "00000000";

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var work = dialect.Snapshot();
                var job = work.Job;
                if (job is null || work.Extranonce1.Length == 0) { Thread.Sleep(50); continue; }

                if (work.Generation != lastJobGen)
                {
                    lastJobGen = work.Generation;
                    jobId = job.JobId;
                    ntimeHex = job.NtimeHex;
                    en2Hex = BuildHeader(job, work.Extranonce1, work.Extranonce2Size, header);

                    // BuildHeader already emits the exact bytes GhostRider hashes.
                    for (int l = 0; l < lanes; l++) Array.Copy(header, 0, blob, l * 80, 80);
                }

                var target = GrHash.Diff256Target(work.Difficulty);

                // Grind nonces, 8 per native call. Thread `idx` walks the nonce
                // space in strides of totalThreads * lanes so the threads never
                // overlap.
                uint nonce = (uint)(idx * lanes);
                uint stride = (uint)(totalThreads * lanes);
                while (!ct.IsCancellationRequested)
                {
                    for (int l = 0; l < lanes; l++)
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(l * 80 + 76, 4), nonce + (uint)l);
                    }

                    fixed (byte* pBlob = blob)
                    fixed (byte* pOut = outbuf)
                        GrNative.HashOcta(ctx, pBlob, 80, pOut);

                    for (int l = 0; l < lanes; l++)
                    {
                        if (!GrHash.MeetsTarget(outbuf.AsSpan(l * 32, 32), target)) continue;

                        uint winner = nonce + (uint)l;
                        _log.LogInformation("gr-pool: share found nonce={Nonce:x8} job={Job}", winner, jobId);
                        // Fire-and-forget the AWAIT, not the attribution: the
                        // dialect correlates the ack by request id, so this
                        // task learns the verdict for this exact share.
                        _ = ReportShareAsync(dialect, jobId, en2Hex, ntimeHex, winner.ToString("x8"), ct);
                    }

                    nonce += stride;
                    local += lanes;
                    Volatile.Write(ref _counts[idx * 8], local);
                    if ((local & 0x3FF) == 0) Metrics.TouchHeartbeat(CpuIndex);

                    // Re-check job generation
                    if (dialect.Snapshot().Generation != lastJobGen) break;
                }
            }
        }
        finally
        {
            GrNative.DestroyCtx(ctx);
        }
    }

    // internal, not private: GrHeaderGoldenTests pins these exact bytes. The
    // selective swab32 below is the single most fragile thing in this file.
    internal static string BuildHeader(BitcoinStratumJob job, string e1, int e2sz, byte[] header)
    {
        // GhostRider never rolls extranonce2 — work is partitioned by nonce
        // stride across threads instead — so en2 is a fixed run of zeros of the
        // pool's width. Keep that: rolling it here would double-search nonces.
        string en2Hex = new string('0', e2sz * 2);
        var root = job.MerkleRoot(e1, en2Hex);

        // Byte order. Bitcoin Stratum sends most header fields as big-endian
        // hex, and the hashed header needs each 32-bit word byte-swapped — but
        // NOT uniformly. XMRig (EthStratumClient.cpp, GHOSTRIDER_RTM branch)
        // swaps words only where `(i < 36) || (i >= 68)`: version, prevhash,
        // ntime, nbits and nonce. The merkle root at [36,68) is written in its
        // natural sha256d output order, unswapped. Swapping it too is what made
        // every share the pools recomputed come out wrong.
        //
        // Fields written via WriteUInt32LittleEndian below are parsed from hex
        // as big-endian ints, so writing them little-endian *is* the swap.
        Array.Clear(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), job.Version);

        Array.Copy(job.PrevHashRaw, 0, header, 4, 32);
        GrHash.Swab32(header.AsSpan(4, 32));

        Array.Copy(root, 0, header, 36, 32);   // no swap — see above

        Array.Copy(GrHash.Unhex(job.NtimeHex), 0, header, 68, 4);
        GrHash.Swab32(header.AsSpan(68, 4));

        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(72, 4), job.Bits);
        // Nonce at offset 76 filled per candidate (already in final order).
        return en2Hex;
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _cts.Dispose();
        // The per-connection StratumSession is disposed by the `await using` in
        // RunSessionAsync's reconnect loop.
        return ValueTask.CompletedTask;
    }

}
