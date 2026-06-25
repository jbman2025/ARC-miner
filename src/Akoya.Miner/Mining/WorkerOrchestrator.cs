// WorkerOrchestrator — the top-level coordinator that wires:
//
//   PoolConnection → MiningSession (one per process)
//        │
//        ├── OnJob ──▶ JobBus.Publish ──▶ N × GpuWorker
//        │
//        │   each GpuWorker emits ShareSubmission ─┐
//        │                                          ▼
//        ├── EnqueueAsync(MinerEvent{Share}) ◀── ShareSink
//        │
//        ├── PingPump        (RTT → Metrics.SetPoolLatencyMs)
//        ├── HeartbeatPump   (per-GPU 5m hashrate from Metrics.GetSnapshot)
//        └── ReconnectHint → reconnect with Resume
//
// V2 unifies all GPUs under ONE miner_id / ONE stream. There is no per-GPU
// pool connection. Workers share the same JobBus and write to the same
// outbound queue.
//
// Reconnect semantics: ReconnectHint or stream-end → tear down workers'
// JobBus subscription (they keep the buffers, wait on a new JobBus), open a
// new MiningSession via Resume, and rewire callbacks.

using System.Diagnostics;
using Akoya.Cuda;
using Akoya.Miner.Config;
using Akoya.Miner.Observability;
using Akoya.MinerCore;
using Akoya.PearlGemm;
using Akoya.Pool;
using Microsoft.Extensions.Logging;
using PearlPool.Proto.V2;

namespace Akoya.Miner.Mining;

internal sealed class WorkerTripException : Exception
{
    public WorkerTripException(string reason, Exception? innerException = null)
        : base($"local worker trip: {reason}", innerException)
    {
        Reason = reason;
    }

    public string Reason { get; }
}

internal sealed class WorkerOrchestrator : IAsyncDisposable
{
    /// <summary>Number of consecutive malformed <see cref="JobAssignment"/>
    /// messages we'll tolerate from the gateway before tripping reconnect.
    /// One bad job is logged + ignored; a sustained run indicates the wire
    /// is hosed (or the peer changed protocol mid-flight) and dropping the
    /// stream is the right response. Exposed as internal so tests can
    /// assert against the boundary.</summary>
    internal const int OnJobParseFailureThreshold = 3;

    /// <summary>Static OnJob handler — extracted from the closure built in
    /// <see cref="RunSessionAsync"/> so it's directly unit-testable. Parses
    /// the incoming <paramref name="job"/> into a <see cref="SigmaContext"/>,
    /// publishes on the bus, and resets <paramref name="parseFailures"/>;
    /// on parse failure increments the counter and trips
    /// <paramref name="tripCts"/> once the threshold is exceeded.</summary>
    internal static void HandleJobAssignment(
        JobAssignment job,
        JobBus bus,
        ReadOnlySpan<byte> minerId,
        uint commonDim,
        ushort rank,
        ILogger log,
        ref int parseFailures,
        int threshold,
        CancellationTokenSource tripCts)
    {
        try
        {
            var ctx = SigmaContext.FromJobAssignment(job, minerId, commonDim, rank);
            bus.Publish(ctx);
            Interlocked.Exchange(ref parseFailures, 0);
            log.LogDebug(
                "orchestrator: job published id={Job} height={H} nbits=0x{Nbits:X}",
                ctx.JobId, ctx.BlockHeight, ctx.TargetNbits);
        }
        catch (Exception ex)
        {
            var n = Interlocked.Increment(ref parseFailures);
            log.LogError(ex,
                "orchestrator: bad JobAssignment (consecutive failures = {N}/{Threshold})",
                n, threshold);
            if (n >= threshold)
            {
                log.LogCritical(
                    "orchestrator: {N} consecutive bad JobAssignments — tripping reconnect", n);
                try { tripCts.Cancel(); } catch { /* race ok */ }
            }
        }
    }

    /// <summary>Static OnVardiff handler — extracted from the closure in
    /// <see cref="RunSessionAsync"/> for unit-testability. Republishes the
    /// current σ with the new target nbits when valid; logs + skips
    /// otherwise. <see cref="Mining.GpuWorker"/> recognises the "same σ,
    /// different nbits" pattern and takes the vardiff fast path (PowTarget
    /// re-upload only, no noise rebuild — and crucially the trigger-clock
    /// IS reset, see <see cref="ILivenessTarget.LastTriggerOrSigmaTicks"/>).</summary>
    internal static void HandleVardiff(DifficultyAdjust v, JobBus bus, ILogger log)
    {
        var cur = bus.Current;
        if (cur is null)
        {
            log.LogInformation(
                "vardiff: new_nbits=0x{N:X} reason={R} measured={M:F0} — no σ yet, will apply on first job",
                v.NewTargetNbits, v.Reason, v.MeasuredHashrate);
        }
        else if (cur.TargetNbits == v.NewTargetNbits)
        {
            // Same nbits — no-op. Deliberately not logged; pool repeats vardiff
            // on every job and the noise drowned out useful events.
        }
        else
        {
            log.LogInformation(
                "vardiff: 0x{Old:X} → 0x{New:X} reason={R} measured={M:F0}",
                cur.TargetNbits, v.NewTargetNbits, v.Reason, v.MeasuredHashrate);
            bus.Publish(cur.WithTargetNbits(v.NewTargetNbits));
        }
    }

    private readonly MinerOptions _opts;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WorkerOrchestrator> _log;

    /// <summary>Conversion factor from internal throughput (TMADs/s,
    /// tera-multiply-accumulates per second — the unit the benchmark and
    /// per-worker stats report in) to the wire unit the pool expects on
    /// every hashrate field (H/s — hashrate-equivalent units per second).
    /// Pearl's per-tile target is scaled by dot-product work, so block rate
    /// tracks MAD/s; the wire conversion is therefore purely scale: ×1e12.
    /// Apply at the wire boundary only; everything inside the miner stays in
    /// TMADs/s for human-readable logs.</summary>
    private const double TmadsToHashesPerSec = 1e12;

    /// <summary>Last <see cref="ReconnectHint"/> observed on the inbound
    /// stream before <see cref="RunAsync"/> returned (or threw). The outer
    /// reconnect loop honours this — server-driven backoff overrides our
    /// local jitter when larger. Null = no hint received.</summary>
    public ReconnectHint? LastReconnectHint { get; private set; }

    /// <summary>Cached benchmark result. Populated on the first
    /// <see cref="RunAsync"/> call; reused on every reconnect.
    ///
    /// Rationale: the benchmark exists to give the pool a
    /// claimed_total_hashrate in <see cref="SessionIdentity"/> and to size
    /// MatmulsPerPoll from observed iter_ms. Both are stable properties
    /// of the GPU rig — they do not change across a TCP reconnect — so
    /// re-running a 10-second benchmark on every Resume is wasted GPU
    /// time and a wasted hashrate-pause on the pool side. On the first
    /// reconnect after process start, the cache is populated and
    /// subsequent attempts skip the benchmark block entirely.
    ///
    /// Null = benchmark has not yet completed (first attempt is in
    /// flight or hasn't started).</summary>
    private BenchmarkResult? _cachedBenchmark;

    internal sealed record BenchmarkResult(
        double[] PerGpu,
        double[] IterMs,
        double Total,
        int Mpp);

    /// <summary>Max wait at HandleTrigger for the half's already-queued
    /// matmuls to drain before re-deriving A. Used to size mpp from
    /// worst-case iter_ms.</summary>
    internal const double BenchmarkTriggerBudgetMs = 10.0;
    /// <summary>Hard cap on MatmulsPerPoll. Large mpp risks header-buffer
    /// pressure (one pinned slot per matmul).</summary>
    internal const int BenchmarkMaxMpp = 16;

    private const int DefaultM = 8192;
    private const int DefaultN = 32768;
    private const int DefaultK = 2048;
    private const int DefaultNoiseRank = 128;

    /// <summary>Run-or-reuse the GPU benchmark.
    ///
    /// On the FIRST call (after process start) this drives the supplied
    /// <paramref name="runBench"/> in parallel across <paramref name="gpus"/>,
    /// computes <c>mpp</c> from the slowest GPU's iter_ms, caches the
    /// result on the orchestrator instance, and returns it.
    ///
    /// On EVERY SUBSEQUENT call (i.e. reconnect / Resume) the cache is
    /// returned directly with no GPU work — the rig's hashrate and
    /// iter_ms don't change between a stream-end and the Resume that
    /// follows, so re-benchmarking is wasted GPU time and a wasted
    /// hashrate-pause on the pool side.
    ///
    /// Visible to tests for direct verification; production callers go
    /// through <see cref="RunAsync"/>.</summary>
    internal BenchmarkResult RunOrReuseBenchmark(
        IReadOnlyList<GpuInfo> gpus,
        Func<int, ILogger, CancellationToken, GpuWorker.BenchmarkResult> runBench,
        CancellationToken ct)
    {
        if (_cachedBenchmark is { } cached && cached.PerGpu.Length == gpus.Count)
        {
            _log.LogInformation(
                "benchmark: reusing cached result from process start " +
                "({Total:F2} TMADs/s, mpp={Mpp}) — skipped on reconnect",
                cached.Total, cached.Mpp);
            return cached;
        }

        var perGpu = new double[gpus.Count];
        var iterMs = new double[gpus.Count];

        // Run all GPUs in parallel — each owns its own CUDA context and
        // stream, so wall-clock = max(per-GPU duration) ≈ duration, not
        // N×duration. Dedicated Threads (not Task.Run) because the
        // benchmark is CPU+GPU bound and we don't want async-scheduler
        // stutter biasing the first-batch numbers.
        var threads = new Thread[gpus.Count];
        var errors  = new Exception?[gpus.Count];
        for (int i = 0; i < gpus.Count; i++)
        {
            int idx = i;
            var ord = gpus[i].Ordinal;
            threads[i] = new Thread(() =>
            {
                try
                {
                    var r = runBench(ord, _loggerFactory.CreateLogger($"bench-{ord}"), ct);
                    perGpu[idx] = r.TmadsPerSec;
                    iterMs[idx] = r.IterMs;
                }
                catch (Exception ex) { errors[idx] = ex; }
            })
            {
                IsBackground = true,
                Name = $"bench-{ord}",
            };
            threads[i].Start();
        }
        foreach (var t in threads) t.Join();

        double total = 0.0;
        List<Exception>? failures = null;
        for (int i = 0; i < gpus.Count; i++)
        {
            if (errors[i] is { } ex)
            {
                _log.LogError(ex,
                    "benchmark[{Ord}]: failed — refusing to register without valid hashrate",
                    gpus[i].Ordinal);
                failures ??= new List<Exception>();
                failures.Add(new InvalidOperationException(
                    $"benchmark[{gpus[i].Ordinal}] failed", ex));
                continue;
            }
            else if (perGpu[i] <= 0.0 || !double.IsFinite(perGpu[i]))
            {
                var invalidEx = new InvalidOperationException(
                    $"benchmark[{gpus[i].Ordinal}] returned invalid hashrate {perGpu[i]}");
                _log.LogError(invalidEx,
                    "benchmark[{Ord}]: invalid hashrate {Value} — refusing to register",
                    gpus[i].Ordinal, perGpu[i]);
                failures ??= new List<Exception>();
                failures.Add(invalidEx);
                continue;
            }
            total += perGpu[i];
        }

        if (failures is { Count: > 0 })
        {
            Exception inner = failures.Count == 1
                ? failures[0]
                : new AggregateException(failures);
            throw new InvalidOperationException(
                $"GPU benchmark failed on {failures.Count}/{gpus.Count} GPU(s); "
                + "refusing to connect to the pool without valid hashrate details.",
                inner);
        }

        int mpp = ComputeMpp(iterMs);

        _log.LogInformation(
            "benchmark: total claimed hashrate = {Total:F2} TMADs/s across {N} GPU(s); " +
            "mpp={Mpp} (target {Budget:F0}ms drain budget)",
            total, gpus.Count, mpp, BenchmarkTriggerBudgetMs);

        var result = new BenchmarkResult(perGpu, iterMs, total, mpp);
        _cachedBenchmark = result;
        return result;
    }

    /// <summary>Compute MatmulsPerPoll from per-GPU iter_ms readings.
    /// Sizes for the SLOWEST GPU's iter_ms (worst-case drain budget):
    /// all workers share the same <see cref="MineOptions"/>, so the
    /// slowest member of the rig sets the ceiling. Returns 1 when no
    /// finite iter_ms is available (bench failed on every GPU).
    /// Clamped to [1, <see cref="BenchmarkMaxMpp"/>].</summary>
    internal static int ComputeMpp(double[] iterMs)
    {
        double worst = 0.0;
        for (int i = 0; i < iterMs.Length; i++)
            if (double.IsFinite(iterMs[i]) && iterMs[i] > worst)
                worst = iterMs[i];

        if (worst <= 0.0 || !double.IsFinite(worst))
            return 1;

        int mpp = (int)Math.Floor(BenchmarkTriggerBudgetMs / worst);
        if (mpp < 1)              return 1;
        if (mpp > BenchmarkMaxMpp) return BenchmarkMaxMpp;
        return mpp;
    }

    /// <summary>Read a positive int from an env var, falling back to
    /// <paramref name="fallback"/> when unset, unparseable, or non-positive.</summary>
    private static int ParseEnvInt(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrEmpty(raw)
            && int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var v)
            && v > 0)
            return v;
        return fallback;
    }

    public WorkerOrchestrator(MinerOptions opts, ILoggerFactory loggerFactory)
    {
        _opts          = opts;
        _loggerFactory = loggerFactory;
        _log           = loggerFactory.CreateLogger<WorkerOrchestrator>();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // LastReconnectHint is per stream attempt. Program.cs reuses the
        // orchestrator across reconnects for benchmark caching, so clear any
        // stale hint before a new attempt can accidentally honour it.
        LastReconnectHint = null;

        // 1. Enumerate GPUs and build per-card metadata for Register.
        var gpus = EnumerateGpus();
        if (gpus.Count == 0)
            throw new InvalidOperationException("No CUDA devices found.");
        EnsureNativeGemmSupportsGpus(gpus);

        bool isStratum = _opts.Pool.Host.Contains("herominers")
                      || _opts.Pool.Port == 1200
                      || Environment.GetEnvironmentVariable("AKOYA_POOL_STRATUM") == "true"
                      || Environment.GetEnvironmentVariable("AKOYA_POOL_STRATUM") == "1";

        var mineProfile = ApplyGpuProfileDefaults(
            _opts.Mine,
            gpus,
            _opts.Mine.ShapeOverridePresent,
            out var mineProfileName);

        // Per-pool committed config. The miner DECLARES (k, noise_rank) in
        // ConfigBytes and the pool validates them against its network mining
        // params — a mismatch rejects with config_r (rank) / config_k or
        // audit_proof_merkle_mismatch. The two pool families use DIFFERENT params:
        //
        //   • Stratum (HeroMiners-family): full canonical shape — m=n=131072,
        //     k=4096, noise_rank=256. M/N are NOT tunable here (committed), so the
        //     per-GPU table can only feed the GEMM search window, not the shape.
        //   • Akoya gRPC pool: k=2048, noise_rank=128 are network-committed; M/N
        //     are the miner's FREE working set, so we use the per-GPU efficient
        //     shape rather than the large 131072² canonical footprint.
        //
        // AKOYA_MAINNET_M / AKOYA_MAINNET_N are an escape hatch for the stratum
        // path; explicit AKOYA_MINE_* overrides bypass this whole block.
        if (!_opts.Mine.ShapeOverridePresent)
        {
            if (isStratum)
            {
                int mainnetM = ParseEnvInt("AKOYA_MAINNET_M", 131072);
                int mainnetN = ParseEnvInt("AKOYA_MAINNET_N", 131072);
                mineProfile = mineProfile with { M = mainnetM, N = mainnetN, K = 4096, NoiseRank = 256 };
                mineProfileName = "stratum-mainnet";
                if (mainnetM != 131072 || mainnetN != 131072)
                    _log.LogWarning(
                        "mine-profile: mainnet M/N overridden to {M}/{N} — stratum pools will REJECT shares (code 23) unless these match network mining params",
                        mainnetM, mainnetN);
            }
            else
            {
                // Akoya gRPC pool: pin the network-committed k=2048 / rank=128,
                // keep M/N at the per-GPU efficient working set (free for this pool).
                int m = mineProfile.M, n = mineProfile.N;
                if (ResolvePerGpuShape(gpus) is { } pg) { m = pg.M; n = pg.N; }
                mineProfile = mineProfile with { M = m, N = n, K = 2048, NoiseRank = 128 };
                mineProfileName = "rpc-akoya";
            }
        }
        else
        {
            // Explicit AKOYA_MINE_M/N/K/NOISE_RANK override — caller takes
            // responsibility for matching the network's committed shape.
            mineProfileName = isStratum ? "stratum-overridden" : "rpc-overridden";
        }

        if (!mineProfile.Equals(_opts.Mine))
        {
            _log.LogInformation(
                "mine-profile: auto-selected {Profile}",
                mineProfileName);
        }

        Metrics.Init(gpus.Count, new long[gpus.Count]);
        Metrics.SetSessionInfo($"{_opts.Pool.Host}:{_opts.Pool.Port}", _opts.Pool.WorkerName);
        Metrics.SetGpuNames(gpus.Select(g => g.Name).ToArray());

        // Auto-apply a cached autotune profile (arc-tune.conf), if present, so a
        // tuned card mines + benchmarks at its best config with zero config.
        // The kernel knobs are process-global env (the kernel reads them via
        // getenv), so this applies the FIRST GPU's cached profile rig-wide — fine
        // for the common homogeneous-SKU rig. Any knob the user set explicitly
        // (in their .bat, before start) wins and is left untouched. MUST go
        // through NativeEnv (UCRT _putenv_s): Environment.SetEnvironmentVariable
        // alone is invisible to the native getenv on Windows.
        ApplyTunedProfile(gpus);

        // 2. Telemetry sampler (CPU/GPU; logs the periodic cpuPct/rssMB line).
        using var sampler = new MetricsSampler(_loggerFactory.CreateLogger<MetricsSampler>());

        // 3. Connect to pool + Register/Resume.
        PoolConnection? connection = null;
        SessionStore? sessionStore = null;
        IPoolSession session;

        if (isStratum)
        {
            session = new StratumSession(
                _opts.Pool.Host, _opts.Pool.Port,
                _opts.Pool.UseTls, _opts.Pool.TlsInsecure,
                _loggerFactory.CreateLogger<StratumSession>());
        }
        else
        {
            connection = PoolConnection.Create(
                _opts.Pool.Host, _opts.Pool.Port, _opts.Pool.UseTls,
                _loggerFactory.CreateLogger<PoolConnection>(),
                tlsInsecure: _opts.Pool.TlsInsecure,
                keepAlivePingDelay: TimeSpan.FromSeconds(_opts.Pool.KeepAlivePingSec),
                keepAlivePingTimeout: TimeSpan.FromSeconds(_opts.Pool.KeepAliveTimeoutSec));

            sessionStore = new SessionStore(
                _opts.Session.FilePath,
                _loggerFactory.CreateLogger<SessionStore>());
            session = new MiningSession(
                connection, sessionStore,
                _loggerFactory.CreateLogger<MiningSession>());
        }

        try
        {

        // 4a. Hashrate benchmark — sample each GPU for AKOYA_BENCH_DURATION_SEC
        //     so we can give the pool a real claimed_total_hashrate in Register.
        //     If Resume succeeds the pool will use its own historical numbers
        //     and these are advisory only; on cold Register they drive
        //     vardiff's initial-difficulty pick, so the more accurate the
        //     fewer wasted shares on the first σ.
        //
        //     Also derives MatmulsPerPoll from measured iter_ms so the
        //     trigger drain-wait stays under TriggerBudgetMs regardless of
        //     GPU class (H100 ≈ 1 ms iter → mpp 10; dev rig ≈ 31 ms iter →
        //     mpp 1). Mandatory — there is no AKOYA_BENCH_DISABLE.
        //
        //     Runs IN PARALLEL across GPUs.
        var bench = RunOrReuseBenchmark(
            gpus,
            (ord, log, token) => GpuWorker.RunBenchmark(ord, mineProfile, TimeSpan.FromSeconds(Math.Max(1, mineProfile.BenchmarkDurationSec)), log, token),
            ct);
        var benchPerGpu = bench.PerGpu;
        var benchTotal  = bench.Total;
        var computedMpp = bench.Mpp;

        // Bake the computed mpp into a local MineOptions used from here on
        // for worker construction. (We avoid mutating _opts because it's
        // readonly and shared with the reconnect path.)
        var mine = mineProfile with { MatmulsPerPoll = computedMpp };

        var identity = new SessionIdentity(
            WalletAddress:        _opts.Pool.WalletAddress,
            WorkerName:           _opts.Pool.WorkerName,
            MinerVersion:         VersionInfo.MinerVersion,
            GitSha:               VersionInfo.GitSha,
            K:                    (uint)mine.K,
            // Pool expects H/s (proto §4 / doc table). Benchmark reports
            // TMADs/s (tera-MADs/s); because Pearl normalizes per-tile
            // targets by dot-product work, the hashrate-equivalent wire
            // conversion is just ×1e12. Without this we ship "17.11"
            // hash/s, the pool concludes we're dead, and assigns
            // network-difficulty jobs from which we'll never produce a
            // share.
            ClaimedTotalHashrate: benchTotal * TmadsToHashesPerSec,
            GpuCards:             gpus.Select((g, i) => new GpuCard
            {
                Uuid    = g.Uuid,
                Model   = g.Name,
                Index   = (uint)g.Ordinal,
                Hashrate = (i < benchPerGpu.Length ? benchPerGpu[i] : 0.0) * TmadsToHashesPerSec,
            }).ToList());

        var initialJob = await session.ConnectAsync(identity, ct).ConfigureAwait(false);
        Metrics.SetPoolConnected(true);
        _log.LogInformation("orchestrator: session ready ({Path})",
            initialJob is not null ? "Resume" : "Register");

        // 5. Build the fan-out plumbing.
        var bus = new JobBus();
        var sink = new MiningSessionShareSink(session, ct);
        var rttSink = new MetricsRttSink();
        var hbSource = new MetricsHeartbeatSource(gpus);

        // CTS that gets tripped by ANY of: parent ct, worker watchdog (GPU
        // hang), or OnError unauthenticated_* handler. Declared up-front so
        // callbacks can capture it.
        using var workerTripCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        string? workerTripReason = null;
        Exception? workerTripException = null;
        int workerTripReasonSet = 0;

        void TripWorkers(string reason, Exception? ex = null)
        {
            if (Interlocked.CompareExchange(ref workerTripReasonSet, 1, 0) == 0)
            {
                workerTripReason = reason;
                workerTripException = ex;
            }

            try { workerTripCts.Cancel(); } catch { /* race ok */ }
        }

        if (initialJob is not null)
        {
            var asAssignment = ToAssignment(initialJob);
            if (SigmaContext.IsValidInitialJob(asAssignment))
            {
                // Resume returned the cached job — publish immediately.
                var ctx = SigmaContext.FromJobAssignment(
                    asAssignment, session.MinerId.Span,
                    (uint)mine.K, (ushort)mine.NoiseRank);
                bus.Publish(ctx);
            }
            else
            {
                // Pool returned Success=true but no usable initial job
                // (empty/short job_id or sigma). Per pool integration doc the
                // current σ SHOULD ride along on Resume, but a transient
                // between-σ window on the pool can produce this. We are
                // already authenticated; just wait for the first OnJob over
                // the bidi stream rather than tripping reconnect. Logged at
                // INFO so it's visible if it ever happens steady-state.
                _log.LogInformation(
                    "orchestrator: Resume succeeded without a valid initial job "
                    + "(job_id={JobIdLen}B, sigma={SigmaLen}B) — waiting for OnJob push",
                    initialJob.JobId?.Length ?? -1,
                    initialJob.Sigma?.Length ?? -1);
            }
        }

        // Persistent OnJob parse failures (oversized σ, malformed K/R,
        // bogus nbits, …) used to be silently dropped — the worker would
        // keep mining the previous σ until the liveness watchdog noticed,
        // minutes later. Count consecutive parse failures; once we hit
        // OnJobParseFailureThreshold without a successful publish, trip
        // reconnect so we drop and re-Resume against the gateway. A single
        // bad message is logged and ignored (the gateway might be racing
        // through a bad-state recovery); a sustained run of them indicates
        // the wire is hosed and we must reconnect.
        int onJobParseFailures = 0;

        var callbacks = new MiningSessionCallbacks
        {
            OnJob = job =>
            {
                var alreadyTripped = workerTripCts.IsCancellationRequested;
                HandleJobAssignment(
                    job, bus, session.MinerId.Span,
                    (uint)mine.K, (ushort)mine.NoiseRank,
                    _log,
                    ref onJobParseFailures,
                    OnJobParseFailureThreshold,
                    workerTripCts);
                if (!alreadyTripped && workerTripCts.IsCancellationRequested)
                    TripWorkers("bad_job_assignment_threshold");
                return ValueTask.CompletedTask;
            },
            OnShareResult = result =>
            {
                if (result.Accepted) Metrics.IncShareAccepted(0);
                else                 Metrics.IncShareRejected(0);
                // ShareResult.computed_hash is the pool-recomputed BLAKE3
                // jackpot hash (doc §5.3) — use the first 8 bytes as a
                // compact correlation key in routine logs.
                var corr = result.ComputedHash.Length >= 8
                    ? Convert.ToHexString(result.ComputedHash.Span[..8]).ToLowerInvariant()
                    : "";
                if (result.IsBlockFind)
                {
                    // Block find! Pool says this share also met the network
                    // target — they will be broadcasting the block to the
                    // network. Make this absolutely unmissable in logs and
                    // bump a dedicated counter for ops dashboards / alerting.
                    Metrics.IncBlockFind();
                    var full = result.ComputedHash.Length > 0
                        ? Convert.ToHexString(result.ComputedHash.Span).ToLowerInvariant()
                        : "(no-hash)";
                    _log.LogWarning(
                        "============================================================");
                    _log.LogWarning(
                        " BLOCK FIND  hash={Hash}  outcome={Outcome}  accepted={Acc}",
                        full, result.Outcome, result.Accepted);
                    _log.LogWarning(
                        "============================================================");
                }
                else if (result.Accepted)
                {
                    // Happy path. Lead with running accepted/rejected totals so a
                    // glance at the line tells the whole-session score; hash is the
                    // compact correlation key for wire-level reconciliation.
                    var (acc, rej) = Metrics.ShareTotals();
                    _log.LogInformation(
                        "✓ share accepted ({Acc}/{Rej}) hash={Hash} outcome={Outcome}",
                        acc, rej, corr, result.Outcome);
                }
                else
                {
                    // Rejected — include the pool's Message field which carries
                    // the specific reason (e.g. "a_merkle_mismatch",
                    // "claimed_hash_mismatch", "below_target").
                    var (acc, rej) = Metrics.ShareTotals();
                    _log.LogWarning(
                        "✗ share rejected ({Acc}/{Rej}) reason={Reason} hash={Hash}",
                        acc, rej, result.Message ?? "", corr);
                }
                return ValueTask.CompletedTask;
            },
            OnVardiff = v =>
            {
                HandleVardiff(v, bus, _log);
                return ValueTask.CompletedTask;
            },
            OnPoolInfo = info =>
            {
                // pool-info/v1 fee transparency. Shown distinctly from the
                // miner's own 0% software dev fee. Display + stats only.
                Metrics.SetPoolInfo(info.FeePercent, info.PayoutScheme, info.MinPayout,
                    Akoya.Pool.PoolInfo.TrustLabel(info.Trust));
                _log.LogInformation(
                    "Pool fee : {Fee:0.##}%  {Scheme}  ({Trust}){Min}",
                    info.FeePercent, info.PayoutScheme, Akoya.Pool.PoolInfo.TrustLabel(info.Trust),
                    string.IsNullOrEmpty(info.MinPayout) ? "" : $"   min payout {info.MinPayout}");
                if (info.Trust == Akoya.Pool.PoolInfoTrust.RegistryMismatch)
                    _log.LogWarning(
                        "Pool fee : advertised value disagrees with the community registry — verify with the pool.");
                return ValueTask.CompletedTask;
            },
            OnError = e =>
            {
                _log.LogWarning("pool-error: {Code} {Msg} fatal={Fatal}", e.Code, e.Message, e.Fatal);

                // Per V2 integration doc §7: on fatal unauthenticated_* errors,
                // drop the cached session_token (so we can't try Resume again)
                // and trip the stream so Program.cs falls into Register on the
                // next reconnect. Keep identity_key so we reclaim the same
                // miner_id (Tier-1) instead of getting a fresh one (Tier-2/3).
                if (e.Fatal && e.Code is not null &&
                    e.Code.StartsWith("unauthenticated_", StringComparison.Ordinal))
                {
                    _log.LogWarning(
                        "pool-error: unauthenticated_* fatal — clearing SessionToken and forcing re-Register");
                    sessionStore?.ClearSessionToken();
                    TripWorkers("pool_unauthenticated");
                }
                return ValueTask.CompletedTask;
            },
            OnReconnect = r =>
            {
                _log.LogInformation("pool: ReconnectHint wait={W}s reason={R}", r.WaitSeconds, r.Reason);
                LastReconnectHint = r;
                return ValueTask.CompletedTask;
            },
        };

        // 6. Spin up pumps.
        PingPump? ping = null;
        HeartbeatPump? heartbeat = null;
        MiningSessionCallbacks allCallbacks = callbacks;

        if (!isStratum)
        {
            ping = new PingPump((MiningSession)session,
                TimeSpan.FromSeconds(_opts.Pool.PingIntervalSec),
                rttSink, _loggerFactory.CreateLogger<PingPump>());
            // PingPump consumes Pong itself — fold its handler into our callbacks
            // (MiningSessionCallbacks is a sealed init-only class, not a record, so
            // we build a new one rather than using a `with` expression).
            allCallbacks = WithPongHandler(callbacks, ping.HandlePong);
            ping.Start(ct);

            heartbeat = new HeartbeatPump((MiningSession)session,
                TimeSpan.FromSeconds(_opts.Pool.HeartbeatIntervalSec),
                hbSource, _loggerFactory.CreateLogger<HeartbeatPump>());
            heartbeat.Start(ct);
        }

        // 7. Spin up GpuWorkers (one per enumerated device).
        // Every worker's onFatal trips workerTripCts so the orchestrator's
        // RunStreamAsync wakes up and Program.cs reconnects. A dead worker
        // must not be silently absorbed — we already paid for that bug
        // once (an IndexOutOfRangeException from undersized Metrics killed
        // a GpuWorker mid-mine; the orchestrator kept blocking on the
        // pool stream and only the LIVENESS watchdog eventually noticed
        // the stalled progress ticks, minutes later).
        var orchLog = _loggerFactory.CreateLogger("orchestrator");
        Action<GpuWorker, Exception> tripOnFatal = (w, ex) =>
        {
            orchLog.LogError(ex,
                "worker[{Gpu}] reported fatal: {Kind} — tripping reconnect",
                w.GpuIndex, ex.GetType().Name);
            TripWorkers($"worker_fatal_gpu_{w.GpuIndex}_{ex.GetType().Name}", ex);
        };

        var workers = new List<GpuWorker>(gpus.Count);
        for (int i = 0; i < gpus.Count; i++)
        {
            var w = new GpuWorker(
                gpuIndex:      i,
                deviceOrdinal: gpus[i].Ordinal,
                gpuUuid:       gpus[i].Uuid,
                bus:           bus,
                sink:          sink,
                minerId:       session.MinerId,
                mine:          mine,
                log:           _loggerFactory.CreateLogger($"gpu-worker-{i}"),
                onFatal:       tripOnFatal);
            w.Start(ct);
            workers.Add(w);
        }

        // 7a. Worker liveness watchdog — separate scope from the stream
        //     watchdog. Two guards:
        //       (i) progress budget — if a GPU iteration deadlocks (kernel
        //           hang, driver wedge), trip reconnect.
        //       (ii) aggregate no-trigger budget — if workers are iterating
        //           but the rig as a whole produces no shares for
        //           TriggerWatchdogSec, the pool target is too hard
        //           (bad vardiff); trip reconnect.
        await using var workerWd = new WorkerLivenessWatchdog(
            workers,
            TimeSpan.FromSeconds(_opts.Mine.WatchdogTimeoutSec),
            TimeSpan.FromSeconds(_opts.Mine.TriggerWatchdogSec),
            _opts.Mine.TriggerWatchdogK,
            workerTripCts,
            _loggerFactory.CreateLogger("worker-watchdog"),
            TripWorkers);
        workerWd.Start();

        try
        {
            // 8. Block on the bidi MiningStream. Returns when the server hangs
            //    up, sends ReconnectHint, our stream watchdog trips, or the
            //    worker watchdog trips. Either way Program.cs reconnects.
            await session.RunStreamAsync(
                allCallbacks,
                TimeSpan.FromSeconds(_opts.Pool.StreamWatchdogSec),
                TimeSpan.FromSeconds(_opts.Pool.PongTimeoutSec),
                _opts.Pool.OutboundDepthTrip,
                workerTripCts.Token).ConfigureAwait(false);

            ThrowIfLocalWorkerTrip(workerTripCts,
                workerTripReason ?? "worker_watchdog_or_local_cancel",
                workerTripException,
                ct);
        }
        finally
        {
            Metrics.SetPoolConnected(false);
            if (ping != null) await ping.DisposeAsync().ConfigureAwait(false);
            if (heartbeat != null) await heartbeat.DisposeAsync().ConfigureAwait(false);
            // Parallel dispose: with N=8 workers each waiting up to
            // DisposeGrace seconds for a clean exit, sequential await would
            // be 8 × grace = 80s worst case before we can start reconnect.
            // Fan out so total bound is ~DisposeGrace. A worker that
            // doesn't exit within grace logs critical and proceeds
            // (orphaned thread). See GpuWorker.DisposeAsync for the
            // grace + orphan-thread contract.
            await Task.WhenAll(workers.Select(w => w.DisposeAsync().AsTask()))
                      .ConfigureAwait(false);
            foreach (var w in workers)
            {
                if (!w.DisposeWasClean)
                {
                    _log.LogCritical(
                        "orchestrator: worker[{Gpu}] orphaned during teardown — fresh reconnect may race orphan for primary context",
                        w.GpuIndex);
                }
            }
        }
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
            if (connection != null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    internal static void ThrowIfLocalWorkerTrip(
        CancellationTokenSource workerTripCts,
        string reason,
        Exception? cause,
        CancellationToken parentCt)
    {
        if (workerTripCts.IsCancellationRequested && !parentCt.IsCancellationRequested)
            throw new WorkerTripException(reason, cause);
    }

    private static MiningSessionCallbacks WithPongHandler(
        MiningSessionCallbacks cb, Func<PongEvent, ValueTask> pong)
    {
        var existing = cb.OnPong;
        return new MiningSessionCallbacks
        {
            OnJob         = cb.OnJob,
            OnShareResult = cb.OnShareResult,
            OnVardiff     = cb.OnVardiff,
            OnError       = cb.OnError,
            OnReconnect   = cb.OnReconnect,
            OnPong = async p =>
            {
                await pong(p).ConfigureAwait(false);
                if (existing is not null) await existing(p).ConfigureAwait(false);
            },
        };
    }

    // Bridge for Resume's ResumeResponse → JobAssignment shape.
    private static JobAssignment ToAssignment(ResumeResponse r) => new()
    {
        JobId              = r.JobId,
        Sigma              = r.Sigma,
        TargetNbits        = r.TargetNbits,
        NetworkTargetNbits = r.NetworkTargetNbits,
        BlockHeight        = r.BlockHeight,
        ProtocolVersion    = 2,
        BSeed              = r.BSeed,
        AuditK             = r.AuditK,
    };

    // ----- GPU enumeration ----------------------------------------------------

    internal readonly record struct GpuInfo(
        int Ordinal,
        string Name,
        string Uuid,
        int ComputeMajor,
        int ComputeMinor)
    {
        public GpuInfo(int ordinal, string name, string uuid)
            : this(ordinal, name, uuid, 0, 0) { }

        public string SmName => ComputeMajor > 0
            ? $"sm_{ComputeMajor}{ComputeMinor}"
            : "sm_unknown";
    }

    // Look up the cached autotune profile for the (primary) GPU and apply its
    // NB/MB/SEARCH_M knobs, unless the user already set them. Best-effort: a
    // missing/garbled cache just means we mine with defaults.
    private void ApplyTunedProfile(IReadOnlyList<GpuInfo> gpus)
    {
        if (gpus.Count == 0) return;
        try
        {
            var path = Autotune.TuneCache.PathFor(_opts.Session.FilePath);
            var sku = gpus[0].Name;
            // Cache wins, else the built-in per-SKU default (so a known card mines
            // optimally even on a brand-new install with no autotune run).
            if (Autotune.ResolveTunedConfig(path, sku) is not { } t) return;

            var applied = new List<string>(3);
            ApplyKnob("AKOYA_TGEMM_NB", t.Nb, applied);
            ApplyKnob("AKOYA_TGEMM_MB", t.Mb, applied);
            ApplyKnob("AKOYA_SEARCH_M", t.SearchM, applied);

            if (applied.Count > 0)
                _log.LogInformation("tune: applied autotune profile for \"{Sku}\" — {Knobs}",
                    sku, string.Join(" ", applied));
        }
        catch (Exception e)
        {
            _log.LogDebug("tune: could not apply cached profile — {Err}", e.Message);
        }
    }

    private static void ApplyKnob(string name, int value, List<string> applied)
    {
        // Respect an explicit user value (set in the .bat before start → present
        // in the env block .NET reads). Only fill in what the user left unset.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))) return;
        NativeEnv.Set(name, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        applied.Add($"{name}={value}");
    }

    internal static MineOptions ApplyGpuProfileDefaults(
        MineOptions mine,
        IReadOnlyList<GpuInfo> gpus,
        bool shapeOverridePresent,
        out string profileName)
    {
        profileName = "legacy";

        // The pearl-gemm BUILD PROFILE is authoritative about the kernel's hash tile: the SYCL ("arc")
        // and ROCm ("mi300x") kernels XOR a CONTIGUOUS 16x16 tile, so bind that pattern regardless of
        // whether the GPU appears in the name-keyed shape table below. NVIDIA builds use the H100
        // hash-tile pattern (the MiningConfiguration default). Without this, an Arc/MI300X card not in
        // s_perGpuShapes ships the NVIDIA pattern and every share is rejected (a_merkle_mismatch).
        try
        {
            var buildProfile = PearlGemmNative.BuildProfile();
            if (buildProfile is "arc" or "rocm" or "mi300x")
                ApplyHashTileDefaults(HashTile.Contiguous16x16);
        }
        catch (EntryPointNotFoundException) { /* old lib without profile reporting → leave default */ }

        // Bind the hardware-keyed, KERNEL-COUPLED knobs FIRST — independent of the committed
        // shape. These are NOT shape (M/N/K): the hash tile is consensus-critical (wrong
        // RowsPattern/ColsPattern => every share rejected), and SyclKSub/BM/BN size the kernel's
        // CTA/sub-tiles to the silicon. Since the canonical-shape change makes IsLegacyDefaultShape
        // false for a fresh config, the early return below would otherwise skip this tuning entirely
        // and ship the Arc kernel with the NVIDIA-default BM=128/BN=256/SyclKSub=32. ResolvePerGpuShape
        // keys off GPU name only, so this applies even when M/N/K were overridden to canonical.
        if (ResolvePerGpuShape(gpus) is { } hw)
        {
            ApplyHashTileDefaults(hw.Tile);
            mine = mine with { SyclKSub = hw.SyclKSub, BM = hw.BM, BN = hw.BN };
            profileName = hw.Name;
        }

        // The committed shape (M/N/K) is now network-fixed at canonical and forced downstream in
        // RunAsync, so we no longer pull M/N/K from the per-GPU table — only the tuning above. The
        // shape branches below remain only for the legacy (non-forced) path and explicit overrides.
        if (shapeOverridePresent || !IsLegacyDefaultShape(mine))
            return mine;

        var profile = DetectGpuProfile(gpus);
        switch (profile)
        {
            case GpuProfile.VoltaLegacy:
                profileName = "volta-legacy";
                return mine with { M = 4096, N = 32768, K = 2048 };
            case GpuProfile.TuringLegacy:
                profileName = "turing-legacy";
                return mine with { M = 4096, N = 32768, K = 2048 };
            case GpuProfile.AdaConsumer:
                profileName = "ada-consumer";
                return mine with { M = 8192, N = 262144, K = 4096 };
            case GpuProfile.BlackwellConsumer:
                profileName = "blackwell-consumer";
                return mine with { M = 4096, N = 131072, K = 8192 };
            case GpuProfile.Rtx30Ampere:
                profileName = "rtx-30-ampere";
                return mine;
            case GpuProfile.HopperDatacenter:
                profileName = "hopper-datacenter";
                return mine with { M = 4096, N = 262144, K = 4096 };
            case GpuProfile.IntelArc:
                profileName = "intel-arc";
                return mine with { M = 4096, N = 32768, K = 2048, SyclKSub = 16, BM = 32, BN = 32 };
            default:
                return mine;
        }
    }

    private static bool IsLegacyDefaultShape(MineOptions mine)
        => mine.M == DefaultM
           && mine.N == DefaultN
           && mine.K == DefaultK
           && mine.NoiseRank == DefaultNoiseRank;

    /// <summary>
    /// Per-GPU shape table derived from the akoya-sweep-bench campaign
    /// (see <c>miner/akoya-miner-v2/reports/gpu-hashrate-shapes.md</c>).
    /// Each entry is the (M, N, K) tile that produced the highest
    /// observed hashes/sec for the named card.
    ///
    /// GPU names come from <c>cuDeviceGetName</c>, which returns the
    /// vendor-set marketing name (same string you see in <c>nvidia-smi</c>),
    /// so substring matching is reliable BUT:
    ///
    ///  - Mobile/Laptop variants ("RTX 4090 Laptop GPU") are physically
    ///    different silicon and would silently inherit the desktop shape.
    ///    They are filtered out by <see cref="IsMobileOrLaptop"/> and fall
    ///    back to the family profile instead.
    ///  - Ti / Super / D suffixes typically share the same chip as the
    ///    base SKU, so the base entry is intentionally allowed to catch
    ///    them. Where the suffix variant has its own measured shape it
    ///    MUST appear BEFORE the base entry (e.g. "RTX 3060 Ti" before
    ///    "RTX 3060", "L40S" before "L4").
    ///  - More-specific entries MUST come before more-generic ones; the
    ///    table is consulted top-to-bottom and the first hit wins.
    /// </summary>
    private static readonly (string Match, GpuShape Shape)[] s_perGpuShapes =
    {
        // ── Intel Arc (Battlemage / Alchemist / Generic) ───────────────
        // M=8192 (2× the rows) doubles work/iter → ~32 TH/s. m/n are the miner's choice in
        // Pearl (only k/rank/pattern are pool-validated), so this stays accepted. Revert M→4096
        // if a pool rejects on shape.
        ("B580",                    new GpuShape("intel-arc-b580",          8192,  32768, 2048, HashTile.Contiguous16x16, SyclKSub: 16, BM: 64,  BN: 32)),
        ("A770",                    new GpuShape("intel-arc-a770",          4096,  32768, 2048, HashTile.Contiguous16x16, SyclKSub: 16, BM: 128, BN: 64)),
        ("A750",                    new GpuShape("intel-arc-a750",          4096,  32768, 2048, HashTile.Contiguous16x16, SyclKSub: 16, BM: 64,  BN: 32)),
        ("A580",                    new GpuShape("intel-arc-a580",          4096,  32768, 2048, HashTile.Contiguous16x16, SyclKSub: 16, BM: 32,  BN: 32)),
        ("A380",                    new GpuShape("intel-arc-a380",          4096,  32768, 2048, HashTile.Contiguous16x16, SyclKSub: 16, BM: 32,  BN: 32)),
        ("A310",                    new GpuShape("intel-arc-a310",          4096,  32768, 2048, HashTile.Contiguous16x16, SyclKSub: 16, BM: 32,  BN: 32)),
        ("Arc",                     new GpuShape("intel-arc-generic",       4096,  32768, 2048, HashTile.Contiguous16x16, SyclKSub: 16, BM: 32,  BN: 32)),
        // ── AMD CDNA3 (gfx942 / Instinct MI300X) ───────────────────────
        // Sweep-derived for the ROCm path: K MUST be 2048 (the capi gates the fast hand-MFMA kernel on
        // k==2048; other K silently fall back to slow rocWMMA), N=65536 keeps B in the 256MB Infinity
        // Cache (compute-bound, not bandwidth-bound), M=8192 is on the efficiency plateau. Hash tile is
        // the contiguous 16x16 the transcript kernel computes. Confirm 65536 vs 131072 via SweepBench.
        ("MI300X",                  new GpuShape("cdna3-mi300x",            8192,  65536, 2048, HashTile.Contiguous16x16)),
        // ── Blackwell datacenter (sm_100/sm_103) ───────────────────────
        ("B200",                    new GpuShape("blackwell-b200",          8192,  32768, 4096)),
        // ── Volta / Turing legacy exact path ───────────────────────────
        ("Quadro GV100",            new GpuShape("volta-gv100",             4096,  32768, 2048)),
        ("Tesla V100",              new GpuShape("volta-v100",              4096,  32768, 2048)),
        ("V100",                    new GpuShape("volta-v100",              4096,  32768, 2048)),
        ("Titan V",                 new GpuShape("volta-titan-v",           4096,  32768, 2048)),
        ("Tesla T4",                new GpuShape("turing-t4",               4096,  32768, 2048)),
        ("T4",                      new GpuShape("turing-t4",               4096,  32768, 2048)),
        ("Titan RTX",               new GpuShape("turing-titan-rtx",        4096,  32768, 2048)),
        ("Quadro RTX 8000",         new GpuShape("turing-quadro-rtx-8000",  4096,  32768, 2048)),
        ("Quadro RTX 6000",         new GpuShape("turing-quadro-rtx-6000",  4096,  32768, 2048)),
        ("Quadro RTX 5000",         new GpuShape("turing-quadro-rtx-5000",  4096,  32768, 2048)),
        ("Quadro RTX 4000",         new GpuShape("turing-quadro-rtx-4000",  4096,  32768, 2048)),
        ("RTX 2080 Ti",             new GpuShape("turing-rtx-2080-ti",      4096,  32768, 2048)),
        ("RTX 2080",                new GpuShape("turing-rtx-2080",         4096,  32768, 2048)),
        ("RTX 2070",                new GpuShape("turing-rtx-2070",         4096,  32768, 2048)),
        ("RTX 2060",                new GpuShape("turing-rtx-2060",         4096,  32768, 2048)),
        // ── Blackwell pro (sm_120) ─────────────────────────────────────
        ("RTX PRO 6000 Blackwell Server",      new GpuShape("rtx-pro-6000-blackwell-server",      8192, 131072, 8192)),
        ("RTX PRO 6000 Blackwell Workstation", new GpuShape("rtx-pro-6000-blackwell-workstation", 4096,  32768, 2048)),
        ("RTX PRO 6000 Blackwell Max-Q",       new GpuShape("rtx-pro-6000-blackwell-max-q",       4096,  32768, 2048)),
        ("RTX PRO 5000 Blackwell",             new GpuShape("rtx-pro-5000-blackwell",             4096, 131072, 8192)),
        ("RTX PRO 4000 Blackwell",             new GpuShape("rtx-pro-4000-blackwell",             8192, 262144, 4096)),
        // ── Blackwell consumer (RTX 5000) ──────────────────────────────
        // 5090/5080/5070 base entries also catch the 5090 D and any Super
        // variants released later (same chip).
        ("RTX 5090",                new GpuShape("rtx-5090",                4096, 131072, 8192)),
        ("RTX 5080",                new GpuShape("rtx-5080",                4096, 262144, 4096)),
        ("RTX 5070 Ti",             new GpuShape("rtx-5070-ti",             4096, 131072, 4096)),
        ("RTX 5070",                new GpuShape("rtx-5070",                4096, 131072, 4096)),
        ("RTX 5060 Ti",             new GpuShape("rtx-5060-ti",             4096, 131072, 4096)),
        // ── Hopper datacenter ──────────────────────────────────────────
        ("H200 NVL",                new GpuShape("h200-nvl",                4096, 262144, 4096)),
        ("H200",                    new GpuShape("h200",                    4096, 262144, 4096)),
        ("H100 NVL",                new GpuShape("h100-nvl",                4096, 262144, 2048)),
        ("H100 PCIe",               new GpuShape("h100-pcie",               4096,  65536, 2048)),
        ("H100",                    new GpuShape("h100",                    8192, 131072, 2048)),
        // ── Ada Lovelace (RTX 4000, L4, L40S, RTX 6000 Ada) ────────────
        // 4090 catches "4090 D" (China cut). 4080/4070 base catches the
        // SUPER / Ti SUPER variants (same AD103/AD104 chip).
        ("RTX 4090",                new GpuShape("rtx-4090",                8192, 262144, 4096)),
        ("RTX 4080",                new GpuShape("rtx-4080",                8192, 262144, 4096)),
        ("RTX 4070",                new GpuShape("rtx-4070",                4096, 131072, 4096)),
        ("RTX 4060 Ti",             new GpuShape("rtx-4060-ti",             4096, 131072, 4096)),
        ("L40S",                    new GpuShape("l40s",                   16384, 262144, 2048)),
        // "L40" matches the original L40 (non-S). Must come AFTER L40S.
        ("L40",                     new GpuShape("l40",                    16384, 262144, 2048)),
        // "L4" is short; the L40/L40S entries above absorb the longer
        // names so this only fires for the real L4.
        ("L4",                      new GpuShape("l4",                      8192, 262144, 4096)),
        ("RTX 6000 Ada",            new GpuShape("rtx-6000-ada",            4096,  32768, 2048)),
        ("RTX 4000 Ada",            new GpuShape("rtx-4000-ada",            4096, 262144, 4096)),
        ("RTX 2000 Ada",            new GpuShape("rtx-2000-ada",            4096, 262144, 4096)),
        // ── Ampere datacenter ──────────────────────────────────────────
        // A100 covers PCIe and SXM4 (40 and 80 GB) — same GA100 chip.
        // Must come BEFORE A10 so "A100" doesn't get matched by "A10".
        ("A100",                    new GpuShape("a100",                    4096, 131072, 4096)),
        ("A40",                     new GpuShape("a40",                     4096,  65536, 4096)),
        ("A10",                     new GpuShape("a10",                     4096,  32768, 2048)),
        // ── Ampere workstation ─────────────────────────────────────────
        ("RTX A6000",               new GpuShape("rtx-a6000",               4096,  65536, 4096)),
        ("RTX A5000",               new GpuShape("rtx-a5000",               4096,  32768, 2048)),
        ("RTX A4500",               new GpuShape("rtx-a4500",               4096,  65536, 4096)),
        ("RTX A4000",               new GpuShape("rtx-a4000",               4096,  32768, 2048)),
        // ── Ampere consumer (RTX 3000) ─────────────────────────────────
        // 3090 Ti before 3090; 3060 Ti before 3060.
        // 3080 Ti / 3070 Ti / 3060 Ti aren't independently measured —
        // they share GA102/GA104 with the base SKU so the base shape is
        // a reasonable starting point. The 3080 Ti will fall through to
        // "RTX 3080".
        ("RTX 3090 Ti",             new GpuShape("rtx-3090-ti",             4096,  65536, 4096)),
        ("RTX 3090",                new GpuShape("rtx-3090",                4096,  65536, 4096)),
        ("RTX 3080",                new GpuShape("rtx-3080",                4096,  32768, 2048)),
        ("RTX 3070",                new GpuShape("rtx-3070",                4096, 131072, 4096)),
        ("RTX 3060 Ti",             new GpuShape("rtx-3060-ti",             4096,  32768, 2048)),
        ("RTX 3060",                new GpuShape("rtx-3060",                4096,  32768, 2048)),
    };

    // Consensus hash-tile per profile. The committed RowsPattern/ColsPattern MUST match what the GPU
    // kernel XORs per output tile or every share is rejected. NVIDIA cards use the H100 hash-tile
    // pattern (the env/MiningConfiguration default); the ROCm/MI300X kernel hashes a CONTIGUOUS 16x16
    // tile, so that profile carries Contiguous16x16 and we bind the pattern to the detected hardware
    // instead of relying on AKOYA_ROWS_PATTERN/AKOYA_COLS_PATTERN being set at launch.
    internal enum HashTile { H100, Contiguous16x16 }

    internal readonly record struct GpuShape(string Name, int M, int N, int K, HashTile Tile = HashTile.H100, int SyclKSub = 32, int BM = 128, int BN = 256);

    /// <summary>
    /// Bind the consensus hash-tile pattern to the resolved GPU profile. MiningConfiguration.Default
    /// reads AKOYA_ROWS_PATTERN/AKOYA_COLS_PATTERN; here we set the process default for profiles whose
    /// kernel uses a non-H100 tile (ROCm/MI300X = contiguous 16x16), so the right pattern follows the
    /// detected hardware and a deployment can't ship the NVIDIA pattern and get every share rejected.
    /// An explicit env var still wins (we only set when unset). Must run before the first
    /// MiningConfiguration.Default() call (it does — ApplyGpuProfileDefaults is invoked at orchestrator init).
    /// </summary>
    private static void ApplyHashTileDefaults(HashTile tile)
    {
        if (tile != HashTile.Contiguous16x16) return;
        const string idx = "0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15";   // contiguous 16x16 = the kernel's hash tile
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AKOYA_ROWS_PATTERN")))
            Environment.SetEnvironmentVariable("AKOYA_ROWS_PATTERN", idx);
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AKOYA_COLS_PATTERN")))
            Environment.SetEnvironmentVariable("AKOYA_COLS_PATTERN", idx);
    }

    /// <summary>
    /// Mobile / laptop SKUs share a marketing prefix with the desktop
    /// card (e.g. "NVIDIA GeForce RTX 4090 Laptop GPU") but are entirely
    /// different silicon — usually one tier down (AD103 instead of AD102
    /// for 4090 Laptop) with much less VRAM and far lower TDP. We refuse
    /// to apply the desktop shape to them. Desktop "Max-Q Workstation"
    /// SKUs are intentionally NOT filtered — they are full desktop
    /// silicon with a lower TDP cap, and the shape still applies.
    /// </summary>
    private static bool IsMobileOrLaptop(string name)
        => name.Contains("Laptop", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Mobile", StringComparison.OrdinalIgnoreCase);

    private static GpuShape? ResolvePerGpuShape(IReadOnlyList<GpuInfo> gpus)
    {
        if (gpus.Count == 0)
            return null;

        GpuShape? agreed = null;
        foreach (var gpu in gpus)
        {
            if (IsMobileOrLaptop(gpu.Name))
                return null; // mobile silicon → fall back to family/legacy

            GpuShape? thisCard = null;
            foreach (var (match, shape) in s_perGpuShapes)
            {
                if (gpu.Name.Contains(match, StringComparison.OrdinalIgnoreCase))
                {
                    thisCard = shape;
                    break;
                }
            }

            if (thisCard is null)
                return null; // unknown card in rig → fall back
            if (agreed is null)
                agreed = thisCard;
            else if (!string.Equals(agreed.Value.Name, thisCard.Value.Name, StringComparison.Ordinal))
                return null; // mixed rig → fall back
        }

        return agreed;
    }

    private enum GpuProfile
    {
        Legacy,
        VoltaLegacy,
        TuringLegacy,
        AdaConsumer,
        BlackwellConsumer,
        Rtx30Ampere,
        HopperDatacenter,
        IntelArc,
    }

    private static GpuProfile DetectGpuProfile(IReadOnlyList<GpuInfo> gpus)
    {
        if (gpus.Count == 0)
            return GpuProfile.Legacy;

        var allAdaConsumer = true;
        var allBlackwellConsumer = true;
        var allRtx30Ampere = true;
        var allHopperDatacenter = true;
        var allVolta = true;
        var allTuring = true;
        var allIntelArc = true;
        foreach (var gpu in gpus)
        {
            allAdaConsumer &= gpu.Name.Contains("RTX 40", StringComparison.OrdinalIgnoreCase);
            allBlackwellConsumer &= gpu.Name.Contains("RTX 50", StringComparison.OrdinalIgnoreCase);
            allRtx30Ampere &= gpu.Name.Contains("RTX 30", StringComparison.OrdinalIgnoreCase);
            allHopperDatacenter &= gpu.Name.Contains("H100", StringComparison.OrdinalIgnoreCase)
                                || gpu.Name.Contains("H200", StringComparison.OrdinalIgnoreCase);
            allVolta &= gpu.ComputeMajor == 7 && gpu.ComputeMinor == 0;
            allTuring &= gpu.ComputeMajor == 7 && gpu.ComputeMinor == 5;
            allIntelArc &= gpu.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase);
        }

        if (allVolta) return GpuProfile.VoltaLegacy;
        if (allTuring) return GpuProfile.TuringLegacy;
        if (allAdaConsumer) return GpuProfile.AdaConsumer;
        if (allBlackwellConsumer) return GpuProfile.BlackwellConsumer;
        if (allRtx30Ampere) return GpuProfile.Rtx30Ampere;
        if (allHopperDatacenter) return GpuProfile.HopperDatacenter;
        if (allIntelArc) return GpuProfile.IntelArc;
        return GpuProfile.Legacy;
    }

    private List<GpuInfo> EnumerateGpus()
    {
        CudaDriver.Check(CudaDriver.Init(0), "cuInit");
        CudaDriver.Check(CudaDriver.DeviceGetCount(out var n), "cuDeviceGetCount");

        var wanted = ParseGpuSelection(_opts.Gpus.IndicesRaw, n);
        var result = new List<GpuInfo>(wanted.Count);
        Span<byte> nameBuf = stackalloc byte[128];
        foreach (var ord in wanted)
        {
            CudaDriver.Check(CudaDriver.DeviceGet(out var dev, ord), "cuDeviceGet");
            nameBuf.Clear();
            CudaDriver.Check(CudaDriver.DeviceGetName(nameBuf, nameBuf.Length, dev), "cuDeviceGetName");
            CudaDriver.Check(CudaDriver.DeviceComputeCapability(out var major, out var minor, dev), "cuDeviceComputeCapability");
            var nameLen = nameBuf.IndexOf((byte)0);
            if (nameLen < 0) nameLen = nameBuf.Length;
            var name = System.Text.Encoding.UTF8.GetString(nameBuf[..nameLen]);
            // UUID via cuDeviceGetUuid isn't bound in Akoya.Cuda yet; use a
            // stable synthetic id based on (host, ordinal, name) so the pool
            // can still distinguish cards across restarts.
            var synthetic = $"{Environment.MachineName}:{ord}:{name}";
            result.Add(new GpuInfo(ord, name, synthetic, major, minor));
        }
        return result;
    }

    private static void EnsureNativeGemmSupportsGpus(IReadOnlyList<GpuInfo> gpus)
    {
        string profile;
        try
        {
            profile = PearlGemmNative.BuildProfile();
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new InvalidOperationException(
                "Loaded pearl-gemm CAPI library does not expose build-profile reporting. "
                + "Rebuild libpearl_gemm_capi.so from this miner source tree.",
                ex);
        }

        // Wrong-card guard: AOT builds are compiled for ONE GPU family and would
        // crash on the first kernel if run on another. Reject up front with a
        // clear message instead. "" = JIT (any Arc, no check).
        var targetFamily = PearlGemmNative.TargetFamily();
        if (targetFamily is "acm" or "bmg")
        {
            foreach (var gpu in gpus)
            {
                var devFamily = ArcFamilyFromName(gpu.Name);
                if (devFamily is "acm" or "bmg" && devFamily != targetFamily)
                {
                    string need = targetFamily == "acm" ? "Alchemist (A-series: A580/A750/A770)"
                                                        : "Battlemage (B-series: B570/B580/B70)";
                    string have = devFamily == "acm" ? "an Alchemist (A-series)" : "a Battlemage (B-series)";
                    throw new InvalidOperationException(
                        $"This build is for {need}, but GPU {gpu.Ordinal} is {have} card "
                        + $"(\"{gpu.Name}\"). Use the build that matches your card "
                        + "(Alchemist / Battlemage / Battlemage big ram), or the universal JIT build.");
                }
            }
        }

        foreach (var gpu in gpus)
        {
            if (gpu.ComputeMajor <= 0)
                continue;

            int supported;
            try
            {
                supported = PearlGemmNative.SupportsSm(gpu.ComputeMajor, gpu.ComputeMinor);
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new InvalidOperationException(
                    "Loaded pearl-gemm CAPI library does not expose SM support reporting. "
                    + "Rebuild libpearl_gemm_capi.so from this miner source tree.",
                    ex);
            }

            if (supported != 0)
                continue;

            var suggested = SuggestedGemmArchForSm(gpu.ComputeMajor, gpu.ComputeMinor);
            throw new InvalidOperationException(
                $"Loaded pearl-gemm CAPI build '{profile}' does not support GPU[{gpu.Ordinal}] "
                + $"{gpu.Name} ({gpu.SmName}). Rebuild with PEARL_GEMM_ARCH={suggested}.");
        }
    }

    // Map an Intel Arc device name to its kernel family: A-series (A380/A580/
    // A750/A770…) = "acm" (Alchemist), B-series (B570/B580/Pro B70…) = "bmg"
    // (Battlemage). "unknown" if no model token is found (don't block).
    private static string ArcFamilyFromName(string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(name ?? "", @"\b([AB])\d{2,3}\b");
        if (!m.Success) return "unknown";
        return m.Groups[1].Value == "A" ? "acm" : "bmg";
    }

    private static string SuggestedGemmArchForSm(int major, int minor)
        => (major, minor) switch
        {
            (7, 0) => "volta",
            (7, 5) => "turing",
            (8, 0) or (8, 6) => "ampere",
            (8, 9) => "ada",
            (9, 0) => "h100",
            (10, 0) => "b200",
            (12, 0) => "blackwell",
            _ => "the matching architecture profile",
        };

    private static List<int> ParseGpuSelection(string raw, int deviceCount)
    {
        if (string.IsNullOrEmpty(raw) || raw.Equals("all", StringComparison.OrdinalIgnoreCase))
            return Enumerable.Range(0, deviceCount).ToList();
        var ids = new List<int>();
        foreach (var tok in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(tok, out var i))
                throw new FormatException($"AKOYA_GPU_INDICES: bad token '{tok}'");
            if (i < 0 || i >= deviceCount)
                throw new ArgumentOutOfRangeException(nameof(raw), $"GPU index {i} out of range [0, {deviceCount})");
            ids.Add(i);
        }
        return ids;
    }

    // ----- IShareSink wrapping MiningSession.EnqueueAsync ---------------------

    private sealed class MiningSessionShareSink : IShareSink
    {
        private readonly IPoolSession _session;
        private readonly CancellationToken _ct;
        public MiningSessionShareSink(IPoolSession session, CancellationToken ct)
        { _session = session; _ct = ct; }

        public ValueTask SubmitAsync(ShareSubmission share, CancellationToken ct)
            => _session.SubmitShareAsync(share, ct);
    }

    private sealed class MetricsRttSink : IRttSink
    {
        public void RecordRttMs(double ms) => Metrics.SetPoolLatencyMs(ms);
    }

    private sealed class MetricsHeartbeatSource : IHeartbeatSource
    {
        private readonly IReadOnlyList<GpuInfo> _gpus;
        public MetricsHeartbeatSource(IReadOnlyList<GpuInfo> gpus) { _gpus = gpus; }

        public HeartbeatSnapshot Sample()
        {
            var snap = Metrics.GetSnapshot();
            var per = new List<GpuHashrateSample>(snap.GpuCount);
            double total = 0;
            for (int i = 0; i < snap.GpuCount; i++)
            {
                // Wire unit: H/s. Internal unit: TMADs/s (tera-MADs/s).
                // 1 MAD == 1 hash, so conversion is ×1e12. Must match
                // the Register / GpuCard path or the pool's vardiff
                // model will fight our heartbeat updates.
                var hr = snap.TmadsPerSec[i] * TmadsToHashesPerSec;
                total += hr;
                var uuid = i < _gpus.Count ? _gpus[i].Uuid : $"gpu-{i}";
                per.Add(new GpuHashrateSample(uuid, hr,
                    (uint)Math.Max(0, snap.Accepted[i] + snap.Rejected[i])));
            }
            return new HeartbeatSnapshot(per, total, Metrics.GetPoolLatencyMs());
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
