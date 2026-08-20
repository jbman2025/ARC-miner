// GhostRider (gr) algorithm module — CPU mining via ghostrider_capi.{dll,so}
// (native/randomx-xmrig/ghostrider_capi.cpp), a C ABI over XMRig's GhostRider
// (Raptoreum). Self-contained per the --algo plugin rules: config comes from
// ARC_GR_* / shared env only, and it spins its own CPU worker pool.
//
// Modes:
//   • POOL MINING (stratum) against a Raptoreum-style pool — the primary path
//     (see GrStratumClient). Canonical Bitcoin Stratum V1 with a GhostRider PoW.
//   • SOLO MINING against a raptoreumd node via getblocktemplate/submitblock
//     (see GrSolo). NOTE: standard Bitcoin coinbase only — Raptoreum mainnet's
//     smartnode/founder coinbase rules are NOT implemented, so mainnet solo is
//     for validation/regtest; pool mining is the supported production path.
//   • BENCHMARK (selftest + multi-thread hashrate) when no node/address is set.

using System.Diagnostics;
using Akoya.Miner.Algos.Cpu;
using Akoya.Miner.Config;
using Akoya.Miner.Mining;
using Akoya.Miner.Observability;
using Microsoft.Extensions.Logging;

namespace Akoya.Miner.Algos.Gr;

internal sealed class GrAlgo : IMiningAlgo
{
    public string Name => "gr";

    // Dashboard slot: the CPU row Metrics.InitCpu appended after the GPUs. Index
    // 0 is a real GPU when dual-mining (gr+prl and friends), so never hardcode it.
    private static int CpuIndex => Metrics.CpuIndex >= 0 ? Metrics.CpuIndex : 0;

    // GhostRider has no algo-specific config knobs beyond the shared chain, so
    // GrConfig is a thin alias over CpuAlgoConfig kept for call-site readability.
    private sealed record GrConfig(CpuAlgoConfig Common)
    {
        public int Threads => Common.Threads;
        public string Worker => Common.Worker;
        public string? NodeUrl => Common.PoolUrl;
        public string? Address => Common.Address;
        public string Password => Common.Password;
        public bool UseTls => Common.UseTls;
        public double PollSec => Common.PollSec;
        public bool Affinity => Common.Affinity;
    }

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    private static GrConfig LoadConfig() =>
        new(CpuAlgoConfigLoader.Load("GR", defaultKeepaliveSec: 30, defaultPollSec: 2.0));

    public async Task<int> RunAsync(MinerOptions opts, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("gr");
        var cfg = LoadConfig();

        if (cfg.Common.IsDual && !cfg.Common.ThreadsExplicit && cfg.Threads < Environment.ProcessorCount)
        {
            log.LogInformation("gr: dual-mining — using {Threads}/{Total} logical CPUs (reserving {Reserve} for the GPU host; override with --threads-cpu or ARC_GR_DUAL_RESERVE)",
                cfg.Threads, Environment.ProcessorCount, Environment.ProcessorCount - cfg.Threads);
        }

        try { _ = GrNative.AbiVersion(); }
        catch (DllNotFoundException)
        {
            log.LogError("gr: ghostrider_capi not found next to the miner binary — this build has no GhostRider backend (see native/randomx-xmrig/build_gr_capi)");
            return 78;
        }

        if (GrNative.Selftest() != 0)
        {
            log.LogError("gr: GhostRider selftest failed — {Err}", GrNative.LastError());
            return 78;
        }
        log.LogInformation("gr: GhostRider selftest OK (abi v{Abi})", GrNative.AbiVersion());

        // The selftest allocates a context, so the huge-pages state is known now.
        if (GrNative.HugePages() == 0)
        {
            log.LogWarning("gr: huge pages unavailable — hashrate will be roughly HALF. Each worker random-walks 16 MiB of CryptoNight scratchpads, so 4 KiB pages thrash the TLB. Grant SeLockMemoryPrivilege (Windows: Lock pages in memory) or run elevated.");
        }

        // XMRig applies the same Ryzen/Intel MSR preset for GhostRider as it does
        // for RandomX (its gr runs log "FAILED TO APPLY MSR MOD, HASHRATE WILL BE
        // LOW" when it can't). Needs Administrator; no-ops with a warning otherwise.
        if (OperatingSystem.IsWindows()) MsrTweaker.Apply(log);

        try
        {
        bool mining = cfg.NodeUrl is not null && cfg.Address is not null;
        var cpuName = $"CPU · {cfg.Threads}T GhostRider";
        Metrics.InitCpu(cfg.Threads, cpuName);
        Metrics.SetCpuSessionInfo(mining ? cfg.NodeUrl! : "benchmark (no pool)", cfg.Worker);
        Metrics.SetCpuPoolConnected(false);

        if (!mining)
        {
            var why = CpuAlgoConfigLoader.DescribeWhyNotMining(cfg.Common, "gr", "RTM address");
            if (cfg.NodeUrl is null && cfg.Address is null && !cfg.Common.IsDual) log.LogWarning("{Why}", why);
            else log.LogError("{Why}", why);
            return await BenchmarkAsync(cfg, log, ct).ConfigureAwait(false);
        }

        // Solo vs pool: a bare host:port (or http URL) is a daemon; a stratum
        // scheme, or ARC_POOL_HOST/ARC_POOL_STRATUM, selects the pool path.
        bool isPool = cfg.Common.IsStratumPool;

        if (isPool)
        {
            int attempt = 0;
            while (!ct.IsCancellationRequested)
            {
                try { await MinePoolAsync(cfg, log, ct).ConfigureAwait(false); attempt = 0; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    attempt++;
                    var backoff = ReconnectBackoff.NextDelay(attempt);
                    log.LogWarning("gr: pool session failed: {Msg} — retry in {Delay:F0}s (attempt {Attempt})", ex.Message, backoff.TotalSeconds, attempt);
                    try { await Task.Delay(backoff, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                }
            }
            return 0;
        }

        int soloAttempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await GrSolo.MineAsync(cfg.NodeUrl!, cfg.Address!, cfg.Password, cfg.Threads, cfg.PollSec, cfg.Worker, log, ct).ConfigureAwait(false); soloAttempt = 0; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (GrSolo.UnsupportedChainException ex)
            {
                // Not transient — backing off and retrying would just repeat the
                // same refusal every minute forever.
                log.LogError("{Msg}", ex.Message);
                return 78;
            }
            catch (Exception ex)
            {
                soloAttempt++;
                var backoff = ReconnectBackoff.NextDelay(soloAttempt);
                log.LogWarning("gr: {Msg} — retry in {Delay:F0}s (attempt {Attempt})", ex.Message, backoff.TotalSeconds, soloAttempt);
                try { await Task.Delay(backoff, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
        return 0;
        }
        finally
        {
            // Leave the MSRs as we found them; they are machine-wide state.
            if (OperatingSystem.IsWindows()) MsrTweaker.Restore();
        }
    }

    // ── pool mining ───────────────────────────────────────────────────────────
    private static async Task MinePoolAsync(GrConfig cfg, ILogger log, CancellationToken ct)
    {
        string poolUrl = cfg.NodeUrl!;
        var hp = poolUrl.Contains("://") ? poolUrl[(poolUrl.IndexOf("://", StringComparison.Ordinal) + 3)..] : poolUrl;
        int slash = hp.IndexOf('/');
        if (slash >= 0) hp = hp[..slash];
        var colon = hp.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(hp[(colon + 1)..], out int port))
            throw new InvalidOperationException($"Invalid pool URL format: {poolUrl}");
        string host = hp[..colon];
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];

        // GhostRider pools speak Bitcoin/Yiimp Stratum V1, without exception —
        // XMRig routes GHOSTRIDER_RTM to its EthStratumClient and offers no
        // Monero-style login dialect for this algo. Both pools we tested
        // (flockpool, zpool) answer a Monero `login` with "Method not found".
        log.LogInformation("gr: using Bitcoin/Yiimp stratum (coinbase+merkle)");
        var poolCfg = new GrStratumClient.PoolConfig(
            Host: host, Port: port, Address: cfg.Address!, Worker: cfg.Worker,
            Password: cfg.Password, Threads: cfg.Threads, UseTls: cfg.UseTls,
            Affinity: cfg.Affinity);
        await using var client = new GrStratumClient(poolCfg, log);
        await client.RunSessionAsync(ct).ConfigureAwait(false);
    }

    // ── benchmark (no pool) ─────────────────────────────────────────────────────
    //
    // GhostRider picks its trio of CryptoNight variants from the block header's
    // previous-hash field, and the six variants differ several-fold in cost. A
    // benchmark on one fixed header therefore measures ONE trio and reports it
    // as if it were the machine's GhostRider hashrate — which it is not; live
    // mining rotates the trio every block.
    //
    // So the header's prev-hash is rotated on a timer and the headline figure is
    // the average across rotations. Per-rotation numbers are still logged, since
    // the spread between the cheapest and dearest trio is itself useful.
    // ARC_GR_BENCH_ROTATE_SEC tunes the interval; 0 pins the seed (the old
    // behaviour) for A/B comparisons where a stable figure is what you want.

    /// <summary>Current benchmark seed generation. Bumped by the report loop;
    /// workers re-derive their header prev-hash when it changes.</summary>
    private static long _benchGen;

    /// <summary>Deterministic 32-byte prev-hash for a seed generation. Same for
    /// every thread, as it would be on a real block.</summary>
    internal static byte[] BenchSeedFor(long generation)
    {
        Span<byte> src = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(src, generation);
        return System.Security.Cryptography.SHA256.HashData(src);
    }

    private static async Task<int> BenchmarkAsync(GrConfig cfg, ILogger log, CancellationToken ct)
    {
        double rotateSec = double.TryParse(Env("ARC_GR_BENCH_ROTATE_SEC"), out var rs) && rs >= 0 ? rs : 15.0;
        Interlocked.Exchange(ref _benchGen, 0);

        log.LogInformation(
            "gr: BENCHMARK mode, {Threads} threads — {Rot} — Ctrl-C to stop",
            cfg.Threads,
            rotateSec > 0
                ? $"rotating the CryptoNight trio every {rotateSec:F0}s (headline = average across trios)"
                : "seed PINNED (single trio; comparable across runs but not representative)");

        var counts = new long[cfg.Threads * 8];
        var workers = new Thread[cfg.Threads];
        for (int i = 0; i < cfg.Threads; i++)
        {
            int idx = i;
            workers[i] = new Thread(() => BenchWorker(idx, cfg.Threads, counts, log, ct))
            { IsBackground = true, Name = $"gr-bench-{idx}", Priority = ThreadPriority.Normal };
            workers[i].Start();
        }
        try { await BenchReportLoopAsync(counts, cfg.Threads, rotateSec, log, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally { foreach (var w in workers) w.Join(TimeSpan.FromSeconds(2)); }
        return 0;
    }

    // Samples one rotation at a time and reports the mean over rotations, which
    // is the only figure comparable to a live pool average.
    private static async Task BenchReportLoopAsync(
        long[] counts, int threads, double rotateSec, ILogger log, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        var sw = Stopwatch.StartNew();
        long lastTotal = 0; double lastSec = 0;

        var rotationRates = new List<double>();
        double rotationStartSec = 0; long rotationStartTotal = 0;

        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            long total = 0;
            for (int i = 0; i < threads; i++) total += Volatile.Read(ref counts[i * 8]);

            double now = sw.Elapsed.TotalSeconds, dt = now - lastSec;
            double hs = dt > 0 ? (total - lastTotal) / dt : 0;
            lastTotal = total; lastSec = now;
            Metrics.SetHashRate(CpuIndex, hs, hs > 0 ? 1000.0 * threads / hs : 0);

            if (rotateSec > 0 && now - rotationStartSec >= rotateSec)
            {
                double span = now - rotationStartSec;
                double rate = span > 0 ? (total - rotationStartTotal) / span : 0;
                rotationRates.Add(rate);
                rotationStartSec = now; rotationStartTotal = total;

                Interlocked.Increment(ref _benchGen);

                double mean = rotationRates.Average();
                log.LogInformation(
                    "gr: {Hs:F1} H/s (trio {N}: {Rate:F1}) | mean over {Count} trio(s) {Mean:F1} H/s, range {Min:F1}–{Max:F1}",
                    hs, rotationRates.Count, rate, rotationRates.Count, mean,
                    rotationRates.Min(), rotationRates.Max());
            }
            else
            {
                // Redundant with the dashboard's per-worker table — see the note
                // in RxPoolClient. Metrics still feeds the table.
                if (!Akoya.Miner.Observability.Dashboard.Active)
                    log.LogInformation("gr: {Hs:F1} H/s ({Threads} threads, {Total} hashes)", hs, threads, total);
            }
        }
    }

    private static unsafe void BenchWorker(int idx, int threads, long[] counts, ILogger log, CancellationToken ct)
    {
        nint ctx = GrNative.CreateCtx();
        if (ctx == nint.Zero) { log.LogError("gr: bench worker {Idx} ctx create failed — {Err}", idx, GrNative.LastError()); return; }
        try
        {
            const int lanes = GrNative.Lanes;

            // 8 lanes of the same header; only the nonce at offset 76 differs.
            var blob = new byte[80 * lanes];
            var output = new byte[GrNative.HashBytes * lanes];

            uint nonce = (uint)(idx * lanes);
            uint stride = (uint)(threads * lanes);
            long local = 0;
            long seenGen = -1;

            while (!ct.IsCancellationRequested)
            {
                // Re-seed the prev-hash when the report loop rotates. All threads
                // share one seed per generation, exactly as they would share one
                // block — otherwise threads would run different trios at once and
                // the per-rotation figure would be a blur of several.
                long gen = Interlocked.Read(ref _benchGen);
                if (gen != seenGen)
                {
                    seenGen = gen;
                    var seed = BenchSeedFor(gen);
                    for (int l = 0; l < lanes; l++) seed.CopyTo(blob.AsSpan(l * 80 + 4, 32));
                }

                for (int l = 0; l < lanes; l++)
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(l * 80 + 76, 4), nonce + (uint)l);

                fixed (byte* pB = blob)
                fixed (byte* pO = output)
                    GrNative.HashOcta(ctx, pB, 80, pO);

                nonce += stride;
                local += lanes;
                Volatile.Write(ref counts[idx * 8], local);
                if ((local & 0x3FF) == 0) Metrics.TouchHeartbeat(CpuIndex);
            }
            Volatile.Write(ref counts[idx * 8], local);
        }
        finally { GrNative.DestroyCtx(ctx); }
    }

}
