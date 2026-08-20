// RandomX (rx) algorithm module — CPU mining via randomx_capi.{dll,so}
// (native/randomx-xmrig), a C ABI over XMRig's RandomX fork. Self-contained per the
// --algo plugin rules: config comes from ARC_RX_* / shared env only, and it
// spins its own CPU worker pool rather than a GPU orchestrator.
//
// Two modes:
//   • SOLO MINING against a monerod daemon (get_block_template / submit_block)
//     when a node + wallet address are configured. RandomX key = the template's
//     seed_hash; the worker pool rebuilds on a seed-epoch change.
//   • BENCHMARK (selftest + multi-thread hashrate) when no node/address is set,
//     to validate the binding and report CPU hashrate.
//
// Not yet: stratum pool mining, and the systems tuning (huge pages beyond the
// flag, MSR, NUMA/affinity) that drives competitive hashrate.

using Akoya.Miner.Algos.Cpu;
using System.Collections.Concurrent;
using System.Diagnostics;
using Akoya.Miner.Config;
using Akoya.Miner.Mining;
using Akoya.Miner.Observability;
using Microsoft.Extensions.Logging;

namespace Akoya.Miner.Algos.Rx;

internal sealed class RxAlgo : IMiningAlgo
{
    public string Name => "rx";

    private static readonly byte[] BenchSeedKey =
        System.Text.Encoding.ASCII.GetBytes("arc-miner randomx benchmark v1");
    private static readonly long[] DefaultHeartbeats = { 0L };

    // The shared pool/wallet/threads chain lives in CpuAlgoConfig; only the two
    // RandomX-specific memory knobs are local. LightMode selects the ~256 MB
    // cache-only VM over the ~2.3 GB dataset; LargePages defaults ON (opt OUT
    // with =0) because RandomX loses roughly a third of its hashrate without it.
    private sealed record RxConfig(CpuAlgoConfig Common, bool LightMode, bool LargePages)
    {
        public int Threads => Common.Threads;
        public bool Affinity => Common.Affinity;
        public string Worker => Common.Worker;
        public string? NodeUrl => Common.PoolUrl;
        public string? Address => Common.Address;
        public string Password => Common.Password;
        public double PollSec => Common.PollSec;
    }

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    // Dashboard slot: the CPU row Metrics.InitCpu appended after the GPUs. Index
    // 0 is a real GPU when dual-mining, so hardcoding it makes rx and the GPU
    // overwrite each other's hashrate and share counts.
    private static int CpuIndex => Metrics.CpuIndex >= 0 ? Metrics.CpuIndex : 0;

    private static RxConfig LoadConfig() => new(
        CpuAlgoConfigLoader.Load("RX", defaultKeepaliveSec: 30, defaultPollSec: 4.0),
        LightMode: Env("ARC_RX_LIGHT") == "1",
        LargePages: Env("ARC_RX_LARGE_PAGES") != "0");

    public async Task<int> RunAsync(MinerOptions opts, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("rx");
        var cfg = LoadConfig();

        if (cfg.Common.IsDual && !cfg.Common.ThreadsExplicit && cfg.Threads < Environment.ProcessorCount)
        {
            log.LogInformation("rx: dual-mining — using {Threads}/{Total} logical CPUs (reserving {Reserve} for the GPU host; override with --threads-cpu)",
                cfg.Threads, Environment.ProcessorCount, Environment.ProcessorCount - cfg.Threads);
        }

        try { _ = RxNative.AbiVersion(); }
        catch (DllNotFoundException)
        {
            log.LogError("rx: randomx_capi not found next to the miner binary — this build has no RandomX backend (see native/randomx-xmrig)");
            return 78;
        }

        if (RxNative.Selftest() != 0)
        {
            log.LogError("rx: RandomX selftest failed — {Err}", RxNative.LastError());
            return 78;
        }
        log.LogInformation("rx: RandomX selftest OK (abi v{Abi})", RxNative.AbiVersion());

        if (OperatingSystem.IsWindows()) MsrTweaker.Apply(log);
        try
        {
            bool mining = cfg.Common.CanMine;
            var cpuName = $"CPU · {cfg.Threads}T RandomX ({(cfg.LightMode ? "light" : "fast")})";
            Metrics.InitCpu(cfg.Threads, cpuName);
            Metrics.SetCpuSessionInfo(mining ? cfg.NodeUrl! : "benchmark (no node)", cfg.Worker);
            Metrics.SetCpuPoolConnected(false);

            if (!mining)
            {
                var why = CpuAlgoConfigLoader.DescribeWhyNotMining(cfg.Common, "rx", "monero address");
                if (cfg.NodeUrl is null && cfg.Address is null && !cfg.Common.IsDual) log.LogWarning("{Why}", why);
                else log.LogError("{Why}", why);
                return await BenchmarkAsync(cfg, log, ct).ConfigureAwait(false);
            }

            bool isPool = cfg.Common.IsStratumPool;

            if (isPool)
            {
                int attempt = 0;
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await MinePoolAsync(cfg, log, ct).ConfigureAwait(false);
                        attempt = 0;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        attempt++;
                        var backoff = ReconnectBackoff.NextDelay(attempt);
                        log.LogWarning("rx: pool session failed: {Msg} — retry in {Delay:F0}s (attempt {Attempt})", ex.Message, backoff.TotalSeconds, attempt);
                        try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                    }
                }
                RxNative.Shutdown();
                return 0;
            }

            // Solo mining. Retry/backoff around transient node/RPC failures.
            int soloAttempt = 0;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await MineAsync(cfg, log, ct).ConfigureAwait(false);
                    soloAttempt = 0;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    soloAttempt++;
                    var backoff = ReconnectBackoff.NextDelay(soloAttempt);
                    log.LogWarning("rx: {Msg} — retry in {Delay:F0}s (attempt {Attempt})", ex.Message, backoff.TotalSeconds, soloAttempt);
                    try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
            RxNative.Shutdown();
            return 0;
        }
        finally
        {
            if (OperatingSystem.IsWindows()) MsrTweaker.Restore();
        }
    }

    // ── pool mining ───────────────────────────────────────────────────────────
    private static async Task MinePoolAsync(RxConfig cfg, ILogger log, CancellationToken ct)
    {
        string poolUrl = cfg.NodeUrl!;
        bool useTls = cfg.Common.UseTls;

        var hp = poolUrl.Contains("://") ? poolUrl[(poolUrl.IndexOf("://", StringComparison.Ordinal) + 3)..] : poolUrl;
        var colon = hp.LastIndexOf(':');
        string host; int port;
        if (colon <= 0 || !int.TryParse(hp[(colon + 1)..], out port))
        {
            throw new InvalidOperationException($"Invalid pool URL format: {poolUrl}");
        }
        host = hp[..colon];
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            host = host[1..^1];
        }

        int keepalive = cfg.Common.KeepaliveSec;

        var poolCfg = new RxPoolClient.PoolConfig(
            Host: host,
            Port: port,
            Address: cfg.Address!,
            Worker: cfg.Worker,
            Password: cfg.Password,
            Threads: cfg.Threads,
            LightMode: cfg.LightMode,
            LargePages: cfg.LargePages,
            Affinity: cfg.Affinity,
            UseTls: useTls,
            KeepaliveSec: keepalive);

        await using var client = new RxPoolClient(poolCfg, log);
        await client.RunSessionAsync(ct).ConfigureAwait(false);
    }

    // ── solo mining ───────────────────────────────────────────────────────────
    private static async Task MineAsync(RxConfig cfg, ILogger log, CancellationToken ct)
    {
        using var rpc = new RxRpcClient(cfg.NodeUrl!, TimeSpan.FromSeconds(30));
        var counts = new long[cfg.Threads * 8];
        byte[]? currentSeed = null;
        bool fullMem = !cfg.LightMode;

        while (!ct.IsCancellationRequested)
        {
            var job = await FetchTemplateAsync(rpc, cfg, log, ct).ConfigureAwait(false);

            if (currentSeed is null || !currentSeed.AsSpan().SequenceEqual(job.SeedHash))
            {
                log.LogInformation("rx: seed epoch {Seed} — (re)building RandomX {Mode} ({Threads}T){Lp}",
                    Convert.ToHexString(job.SeedHash)[..12], fullMem ? "fast (~2.3 GB dataset)" : "light (~256 MB)",
                    cfg.Threads, cfg.LargePages ? " large-pages" : "");
                var sw = Stopwatch.StartNew();
                int rc = RxNative.Init(job.SeedHash, (uint)job.SeedHash.Length, fullMem ? 1 : 0, cfg.LargePages ? 1 : 0, cfg.Threads);
                if (rc != 0) throw new InvalidOperationException($"randomx init failed ({rc}): {RxNative.LastError()}");
                currentSeed = job.SeedHash;
                log.LogInformation("rx: RandomX ready in {S:F1}s — mining height={H} diff={D}", sw.Elapsed.TotalSeconds, job.Height, job.Difficulty);
            }

            await MineEpochAsync(rpc, cfg, job, counts, log, ct).ConfigureAwait(false);
        }
    }

    // Mine one seed epoch: a worker pool grinds the current job; the poll loop
    // refreshes the job and submits hits, and returns when the seed changes so
    // MineAsync can rebuild RandomX.
    private static async Task MineEpochAsync(RxRpcClient rpc, RxConfig cfg, RxJobData first, long[] counts, ILogger log, CancellationToken ct)
    {
        using var epochCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var box = new JobBox();
        box.Publish(first);
        var hits = new ConcurrentQueue<(RxJobData Job, uint Nonce)>();
        var seedHex = Convert.ToHexString(first.SeedHash);
        long curHeight = first.Height;

        // Dedicated OS threads, NOT Task.Run — these loops never yield, so on the
        // thread pool they would starve the poll/submit loop that shares it.
        var pinOrder = CpuAffinity.BuildPinOrder(cfg.Threads);
        var workers = new Thread[cfg.Threads];
        for (int i = 0; i < cfg.Threads; i++)
        {
            int idx = i;
            int cpu = cfg.Affinity ? pinOrder[i % pinOrder.Length] : -1;
            workers[i] = new Thread(() => MineWorker(idx, cfg.Threads, cpu, box, counts, hits, log, epochCts.Token))
            {
                IsBackground = true, Name = $"rx-worker-{idx}", Priority = ThreadPriority.Normal,
            };
            workers[i].Start();
        }
        Metrics.SetCpuPoolConnected(true);

        try
        {
            var reportSw = Stopwatch.StartNew();
            long lastTotal = 0; double lastSec = 0;
            var poll = TimeSpan.FromSeconds(cfg.PollSec);
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(poll, ct).ConfigureAwait(false);

                while (hits.TryDequeue(out var hit))
                    await SubmitAsync(rpc, hit.Job, hit.Nonce, log, ct).ConfigureAwait(false);

                RxJobData next;
                try { next = await FetchTemplateAsync(rpc, cfg, log, ct).ConfigureAwait(false); }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    log.LogWarning("rx: template refresh failed ({Msg})", e.Message);
                    continue;
                }

                if (Convert.ToHexString(next.SeedHash) != seedHex)
                {
                    log.LogInformation("rx: seed changed — ending epoch to re-seed");
                    break;
                }
                if (next.Height != curHeight)
                {
                    box.Publish(next);
                    curHeight = next.Height;
                    log.LogInformation("rx: new job height={H} diff={D}", next.Height, next.Difficulty);
                }

                // Hashrate → dashboard.
                long total = 0;
                for (int i = 0; i < cfg.Threads; i++) total += Volatile.Read(ref counts[i * 8]);
                double now = reportSw.Elapsed.TotalSeconds, dt = now - lastSec;
                double hs = dt > 0 ? (total - lastTotal) / dt : 0;
                lastTotal = total; lastSec = now;
                Metrics.SetHashRate(CpuIndex, hs, hs > 0 ? 1000.0 * cfg.Threads / hs : 0);
            }
        }
        finally
        {
            Metrics.SetCpuPoolConnected(false);
            epochCts.Cancel();
            foreach (var w in workers) w.Join(TimeSpan.FromSeconds(2));
        }
    }

    private static unsafe void MineWorker(int idx, int threads, int cpu, JobBox box, long[] counts,
        ConcurrentQueue<(RxJobData, uint)> hits, ILogger log, CancellationToken ct)
    {
        CpuAffinity.PinCurrentThread(cpu);

        nint vm = RxNative.CreateVm();
        if (vm == nint.Zero) { log.LogError("rx: worker {Idx} VM create failed — {Err}", idx, RxNative.LastError()); return; }
        try
        {
            long lastGen = -1;
            byte[] blob = Array.Empty<byte>();
            ulong diff = 0;
            RxJobData? job = null;
            uint nonce = (uint)idx;
            long local = 0;
            var outbuf = new byte[RxNative.HashBytes];

            // Pipelined hashing (XMRig first/next): HashNext emits the hash of the
            // input submitted on the PREVIOUS call while it starts the next one, so
            // the scratchpad fill overlaps program execution. The emitted hash
            // belongs to the previous nonce — and, since a job can change while a
            // hash is in flight, to that nonce's OWN difficulty — so we carry the
            // pending job/nonce/diff alongside. There is no HashLast in this ABI;
            // the in-flight hash is flushed by the next HashNext (on a job change
            // the switched blob simply becomes the next input).
            bool primed = false;
            uint pendingNonce = 0;
            RxJobData? pendingJob = null;
            ulong pendingDiff = 0;

            while (!ct.IsCancellationRequested)
            {
                var (j, gen) = box.Snapshot();
                if (gen != lastGen)
                {
                    job = j;
                    blob = (byte[])j.HashingBlob.Clone();
                    diff = j.Difficulty;
                    lastGen = gen;
                    nonce = (uint)idx;      // restart this thread's stride for the new job
                }

                RxJob.WriteNonce(blob, nonce);
                fixed (byte* pBlob = blob)
                fixed (byte* pOut = outbuf)
                {
                    if (!primed)
                    {
                        RxNative.HashFirst(vm, pBlob, (uint)blob.Length);
                    }
                    else
                    {
                        // Emits the hash of (pendingJob, pendingNonce); starts `nonce`.
                        RxNative.HashNext(vm, pBlob, (uint)blob.Length, pOut);
                        if (RxJob.CheckHash(outbuf, pendingDiff))
                        {
                            hits.Enqueue((pendingJob!, pendingNonce));
                            log.LogInformation("rx: candidate! nonce={Nonce} height={Height}", pendingNonce, pendingJob!.Height);
                        }
                    }
                }
                pendingNonce = nonce;
                pendingJob = job;
                pendingDiff = diff;
                primed = true;

                nonce += (uint)threads;    // disjoint stride per worker
                if ((++local & 0x3F) == 0) Volatile.Write(ref counts[idx * 8], local);
                if ((local & 0x7FF) == 0) Metrics.TouchHeartbeat(CpuIndex);
            }
            Volatile.Write(ref counts[idx * 8], local);
        }
        finally { RxNative.DestroyVm(vm); }
    }

    private static async Task<RxJobData> FetchTemplateAsync(RxRpcClient rpc, RxConfig cfg, ILogger log, CancellationToken ct)
    {
        var p = $"{{\"wallet_address\":\"{cfg.Address}\",\"reserve_size\":8}}";
        using var doc = await rpc.CallAsync("get_block_template", p, ct).ConfigureAwait(false);
        var r = doc.RootElement.GetProperty("result");

        var hashingHex = r.GetProperty("blockhashing_blob").GetString() ?? throw new InvalidOperationException("no blockhashing_blob");
        var templateHex = r.GetProperty("blocktemplate_blob").GetString() ?? throw new InvalidOperationException("no blocktemplate_blob");
        if (!r.TryGetProperty("seed_hash", out var seedEl) || seedEl.GetString() is not { Length: > 0 } seedHex)
            throw new InvalidOperationException("node returned no seed_hash — not a RandomX chain?");

        var hashing = Convert.FromHexString(hashingHex);
        if (hashing.Length < RxJob.NonceOffset + 4)
            throw new InvalidOperationException($"blockhashing_blob too short ({hashing.Length} bytes)");

        return new RxJobData(
            HashingBlob: hashing,
            TemplateBlob: Convert.FromHexString(templateHex),
            Difficulty: r.GetProperty("difficulty").GetUInt64(),
            Height: r.GetProperty("height").GetInt64(),
            PrevHash: r.TryGetProperty("prev_hash", out var ph) ? ph.GetString() ?? "" : "",
            SeedHash: Convert.FromHexString(seedHex));
    }

    private static async Task SubmitAsync(RxRpcClient rpc, RxJobData job, uint nonce, ILogger log, CancellationToken ct)
    {
        var blob = (byte[])job.TemplateBlob.Clone();
        RxJob.WriteNonce(blob, nonce);
        var hex = Convert.ToHexString(blob).ToLowerInvariant();
        try
        {
            using var doc = await rpc.CallAsync("submit_block", $"[\"{hex}\"]", ct).ConfigureAwait(false);
            var status = doc.RootElement.GetProperty("result").TryGetProperty("status", out var s) ? s.GetString() : "OK";
            Metrics.IncBlockFind();
            Metrics.IncShareAccepted(CpuIndex);
            log.LogInformation("rx: BLOCK submitted height={Height} nonce={Nonce} status={Status}", job.Height, nonce, status);
        }
        catch (Exception e)
        {
            Metrics.IncShareRejected(CpuIndex);
            log.LogWarning("rx: block submit rejected height={Height} nonce={Nonce} — {Err}", job.Height, nonce, e.Message);
        }
    }

    // Publishes the current job with a generation counter workers poll cheaply.
    private sealed class JobBox
    {
        private RxJobData? _job;
        private long _gen;
        public void Publish(RxJobData j) { Volatile.Write(ref _job, j); Interlocked.Increment(ref _gen); }
        public (RxJobData Job, long Gen) Snapshot()
        {
            long g = Interlocked.Read(ref _gen);
            return (Volatile.Read(ref _job)!, g);
        }
    }

    // ── benchmark (no node) ─────────────────────────────────────────────────────
    private static async Task<int> BenchmarkAsync(RxConfig cfg, ILogger log, CancellationToken ct)
    {
        bool fullMem = !cfg.LightMode;
        log.LogInformation("rx: init {Mode} mode, {Threads} threads{Lp} — allocating {Mem}…",
            fullMem ? "fast (dataset)" : "light (cache-only)", cfg.Threads,
            cfg.LargePages ? " (large pages)" : "", fullMem ? "~2.3 GB" : "~256 MB");
        var initSw = Stopwatch.StartNew();
        int rc = RxNative.Init(BenchSeedKey, (uint)BenchSeedKey.Length, fullMem ? 1 : 0, cfg.LargePages ? 1 : 0, cfg.Threads);
        if (rc != 0) { log.LogError("rx: init failed ({Rc}) — {Err}", rc, RxNative.LastError()); return 78; }
        log.LogInformation("rx: ready in {Sec:F1}s — BENCHMARK mode, Ctrl-C to stop", initSw.Elapsed.TotalSeconds);

        var counts = new long[cfg.Threads * 8];
        var pinOrder = CpuAffinity.BuildPinOrder(cfg.Threads);
        var workers = new Thread[cfg.Threads];
        for (int i = 0; i < cfg.Threads; i++)
        {
            int idx = i;
            int cpu = cfg.Affinity ? pinOrder[i % pinOrder.Length] : -1;
            workers[i] = new Thread(() => BenchWorker(idx, cpu, counts, log, ct))
            { IsBackground = true, Name = $"rx-bench-{idx}", Priority = ThreadPriority.Normal };
            workers[i].Start();
        }
        try { await ReportLoopAsync(counts, cfg.Threads, log, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally { foreach (var w in workers) w.Join(TimeSpan.FromSeconds(2)); RxNative.Shutdown(); }
        return 0;
    }

    private static unsafe void BenchWorker(int idx, int cpu, long[] counts, ILogger log, CancellationToken ct)
    {
        CpuAffinity.PinCurrentThread(cpu);

        nint vm = RxNative.CreateVm();
        if (vm == nint.Zero) { log.LogError("rx: worker {Idx} could not create VM — {Err}", idx, RxNative.LastError()); return; }
        try
        {
            var input = new byte[76];
            var output = new byte[RxNative.HashBytes];
            ulong nonce = (ulong)idx << 40;
            long local = 0;

            // Pipeline: prime with HashFirst, then each HashNext emits the prior
            // hash while starting the next — overlapping scratchpad fill with the
            // program run. The emitted hash is discarded (benchmark only counts).
            for (int i = 0; i < 8; i++) input[i] = (byte)(nonce >> (8 * i));
            fixed (byte* pInput = input) RxNative.HashFirst(vm, pInput, (uint)input.Length);
            nonce++;

            while (!ct.IsCancellationRequested)
            {
                for (int i = 0; i < 8; i++) input[i] = (byte)(nonce >> (8 * i));
                fixed (byte* pInput = input)
                fixed (byte* pOut = output)
                {
                    RxNative.HashNext(vm, pInput, (uint)input.Length, pOut);
                }

                nonce++;
                if ((++local & 0x3F) == 0) Volatile.Write(ref counts[idx * 8], local);
                if ((local & 0x7FF) == 0) Metrics.TouchHeartbeat(CpuIndex);
            }
            Volatile.Write(ref counts[idx * 8], local);
        }
        finally { RxNative.DestroyVm(vm); }
    }

    private static async Task ReportLoopAsync(long[] counts, int threads, ILogger log, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        var sw = Stopwatch.StartNew();
        long lastTotal = 0; double lastSec = 0;
        bool reverified = false;
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            // Once, ~10s into mining, confirm the MSR writes are still in effect
            // (they verify at apply-time but a briefly-idle core can reset them).
            if (!reverified && sw.Elapsed.TotalSeconds >= 10)
            {
                reverified = true;
                if (OperatingSystem.IsWindows()) MsrTweaker.Reverify(log);
            }

            long total = 0;
            for (int i = 0; i < threads; i++) total += Volatile.Read(ref counts[i * 8]);
            double now = sw.Elapsed.TotalSeconds, dt = now - lastSec;
            double hs = dt > 0 ? (total - lastTotal) / dt : 0;
            lastTotal = total; lastSec = now;
            Metrics.SetHashRate(CpuIndex, hs, hs > 0 ? 1000.0 * threads / hs : 0);
            // Redundant with the dashboard's per-worker table — see the note in
            // RxPoolClient. Metrics above still feeds the table.
            if (!Akoya.Miner.Observability.Dashboard.Active)
                log.LogInformation("rx: {Hs:F1} H/s ({Threads} threads, {Total} hashes)", hs, threads, total);
        }
    }
}
