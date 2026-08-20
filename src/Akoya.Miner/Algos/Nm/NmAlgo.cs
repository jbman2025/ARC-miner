// NeuroMorph (nm) algorithm module — CPU mining of Cereblix (CRB) via
// neuromorph_capi.{dll,so} (native/randomx-xmrig/neuromorph_capi.cpp), a C ABI
// over the NeuroMorph implementation vendored from the xmrig-cereblix fork.
// Self-contained per the --algo plugin rules: config comes from ARC_NM_* /
// shared env only, and it spins its own CPU worker pool.
//
// Modes:
//   • POOL MINING (stratum) — the only supported path. Monero/XMRig login
//     dialect; see NmPoolClient and the fork's PROTOCOL.md.
//   • BENCHMARK (selftest + multi-thread hashrate) when no pool/address is set.
//
// There is no solo path: Cereblix solo mining goes through the coin's own
// getwork/submitwork HTTP API (or its cereblix-stratum bridge on port 3334),
// which is a different protocol from the pool stratum implemented here.

using System.Diagnostics;
using Akoya.Miner.Algos.Cpu;
using Akoya.Miner.Config;
using Akoya.Miner.Mining;
using Akoya.Miner.Observability;
using Microsoft.Extensions.Logging;

namespace Akoya.Miner.Algos.Nm;

internal sealed class NmAlgo : IMiningAlgo
{
    public string Name => "nm";

    // Dashboard slot: the CPU row Metrics.InitCpu appended after the GPUs. Index
    // 0 is a real GPU when dual-mining (prl+nm and friends), so never hardcode it.
    private static int CpuIndex => Metrics.CpuIndex >= 0 ? Metrics.CpuIndex : 0;

    // NeuroMorph has no algo-specific config knobs beyond the shared chain (its
    // dataset and scratchpad sizes are fixed by the protocol), so NmConfig is a
    // thin alias over CpuAlgoConfig kept for call-site readability.
    private sealed record NmConfig(CpuAlgoConfig Common)
    {
        public int Threads => Common.Threads;
        public string Worker => Common.Worker;
        public string? PoolUrl => Common.PoolUrl;
        public string? Address => Common.Address;
        public string Password => Common.Password;
        public bool UseTls => Common.UseTls;
        public int KeepaliveSec => Common.KeepaliveSec;
        public bool Affinity => Common.Affinity;
    }

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    private static NmConfig LoadConfig() =>
        new(CpuAlgoConfigLoader.Load("NM", defaultKeepaliveSec: 60));

    public async Task<int> RunAsync(MinerOptions opts, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("nm");
        var cfg = LoadConfig();

        if (cfg.Common.IsDual && !cfg.Common.ThreadsExplicit && cfg.Threads < Environment.ProcessorCount)
        {
            log.LogInformation("nm: dual-mining — using {Threads}/{Total} logical CPUs (reserving {Reserve} for the GPU host; override with --threads-cpu or ARC_NM_DUAL_RESERVE)",
                cfg.Threads, Environment.ProcessorCount, Environment.ProcessorCount - cfg.Threads);
        }

        try { _ = NmNative.AbiVersion(); }
        catch (DllNotFoundException)
        {
            log.LogError("nm: neuromorph_capi not found next to the miner binary — this build has no NeuroMorph backend (see native/randomx-xmrig/build_nm_capi)");
            return 78;
        }

        // Guard against a stale native lib silently disagreeing on the header
        // layout — that would produce plausible-looking but always-rejected shares.
        if (NmNative.NativeHeaderLen() != NmNative.HeaderBytes ||
            NmNative.NativeNonceOffset() != NmNative.NonceOffset)
        {
            log.LogError("nm: neuromorph_capi header layout mismatch — native says len={NLen} nonce={NOff}, expected len={Len} nonce={Off}. Rebuild the native lib.",
                NmNative.NativeHeaderLen(), NmNative.NativeNonceOffset(), NmNative.HeaderBytes, NmNative.NonceOffset);
            return 78;
        }

        if (NmNative.Selftest() != 0)
        {
            log.LogError("nm: NeuroMorph selftest failed — {Err}", NmNative.LastError());
            return 78;
        }
        log.LogInformation("nm: NeuroMorph selftest OK (abi v{Abi})", NmNative.AbiVersion());

        // The selftest builds the dataset, so this is now meaningful.
        if (NmNative.HugePages() == 0)
        {
            log.LogWarning("nm: huge pages unavailable — hashrate will be roughly a third lower. NeuroMorph is DRAM-latency bound; grant SeLockMemoryPrivilege (Windows: Lock pages in memory) or run elevated.");
        }

        bool mining = cfg.PoolUrl is not null && cfg.Address is not null;
        Metrics.InitCpu(cfg.Threads, $"CPU · {cfg.Threads}T NeuroMorph");
        Metrics.SetCpuSessionInfo(mining ? cfg.PoolUrl! : "benchmark (no pool)", cfg.Worker);
        Metrics.SetCpuPoolConnected(false);

        if (!mining)
        {
            var why = CpuAlgoConfigLoader.DescribeWhyNotMining(cfg.Common, "nm", "crb1 address");
            if (cfg.PoolUrl is null && cfg.Address is null && !cfg.Common.IsDual) log.LogWarning("{Why}", why);
            else log.LogError("{Why}", why);
            return await BenchmarkAsync(cfg, log, ct).ConfigureAwait(false);
        }

        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await MinePoolAsync(cfg, log, ct).ConfigureAwait(false); attempt = 0; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                attempt++;
                var backoff = ReconnectBackoff.NextDelay(attempt);
                log.LogWarning("nm: pool session failed: {Msg} — retry in {Delay:F0}s (attempt {Attempt})", ex.Message, backoff.TotalSeconds, attempt);
                try { await Task.Delay(backoff, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
        return 0;
    }

    private static async Task MinePoolAsync(NmConfig cfg, ILogger log, CancellationToken ct)
    {
        string url = cfg.PoolUrl!;
        var hp = url.Contains("://") ? url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..] : url;
        int slash = hp.IndexOf('/');
        if (slash >= 0) hp = hp[..slash];
        var colon = hp.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(hp[(colon + 1)..], out int port))
            throw new InvalidOperationException($"Invalid pool URL format: {url}");
        string host = hp[..colon];
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];

        var poolCfg = new NmPoolClient.PoolConfig(
            Host: host, Port: port, Address: cfg.Address!, Worker: cfg.Worker,
            Password: cfg.Password, Threads: cfg.Threads, UseTls: cfg.UseTls,
            KeepaliveSec: cfg.KeepaliveSec, Affinity: cfg.Affinity);

        await using var client = new NmPoolClient(poolCfg, log);
        await client.RunSessionAsync(ct).ConfigureAwait(false);
    }

    // ── benchmark (no pool) ─────────────────────────────────────────────────────
    private static async Task<int> BenchmarkAsync(NmConfig cfg, ILogger log, CancellationToken ct)
    {
        log.LogInformation("nm: BENCHMARK mode, {Threads} threads — Ctrl-C to stop", cfg.Threads);
        var counts = new long[cfg.Threads * 8];
        var workers = new Thread[cfg.Threads];
        for (int i = 0; i < cfg.Threads; i++)
        {
            int idx = i;
            workers[i] = new Thread(() => BenchWorker(idx, cfg.Threads, counts, log, ct))
            { IsBackground = true, Name = $"nm-bench-{idx}", Priority = ThreadPriority.Normal };
            workers[i].Start();
        }

        try { await ReportLoopAsync(counts, cfg.Threads, log, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        foreach (var w in workers) w.Join(TimeSpan.FromSeconds(2));
        return 0;
    }

    private static unsafe void BenchWorker(int idx, int threads, long[] counts, ILogger log, CancellationToken ct)
    {
        nint ctx = NmNative.CreateCtx();
        if (ctx == nint.Zero) { log.LogError("nm: bench worker {Idx} ctx create failed — {Err}", idx, NmNative.LastError()); return; }
        try
        {
            // A fixed synthetic epoch seed so every run is comparable.
            var seed = new byte[NmNative.SeedBytes];
            for (int i = 0; i < seed.Length; i++) seed[i] = (byte)(i * 7 + 1);
            fixed (byte* pSeed = seed)
            {
                if (NmNative.SetSeed(ctx, pSeed) != 0)
                {
                    log.LogError("nm: bench worker {Idx} set_seed failed — {Err}", idx, NmNative.LastError());
                    return;
                }
            }

            var header = new byte[NmNative.HeaderBytes];
            var output = new byte[NmNative.HashBytes];
            for (int i = 0; i < NmNative.NonceOffset; i++) header[i] = (byte)(i * 3 + idx);

            uint nonce = (uint)idx;
            long local = 0;
            while (!ct.IsCancellationRequested)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(NmNative.NonceOffset, 4), nonce);
                fixed (byte* pH = header)
                fixed (byte* pO = output)
                    NmNative.Hash(ctx, pH, 100000UL, pO);   // above the dataset activation height

                nonce += (uint)threads;
                if ((++local & 0x3F) == 0) Volatile.Write(ref counts[idx * 8], local);
                if ((local & 0x3FF) == 0) Metrics.TouchHeartbeat(CpuIndex);
            }
            Volatile.Write(ref counts[idx * 8], local);
        }
        finally { NmNative.DestroyCtx(ctx); }
    }

    private static async Task ReportLoopAsync(long[] counts, int threads, ILogger log, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        var sw = Stopwatch.StartNew();
        long lastTotal = 0; double lastSec = 0;
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            long total = 0;
            for (int i = 0; i < threads; i++) total += Volatile.Read(ref counts[i * 8]);
            double now = sw.Elapsed.TotalSeconds, dt = now - lastSec;
            double hs = dt > 0 ? (total - lastTotal) / dt : 0;
            lastTotal = total; lastSec = now;
            Metrics.SetHashRate(CpuIndex, hs, hs > 0 ? 1000.0 * threads / hs : 0);
            // Redundant with the dashboard's per-worker table — see the note in
            // RxPoolClient. Metrics above still feeds the table.
            if (!Akoya.Miner.Observability.Dashboard.Active)
                log.LogInformation("nm: {Hs:F1} H/s ({Threads} threads, {Total} hashes)", hs, threads, total);
        }
    }
}
