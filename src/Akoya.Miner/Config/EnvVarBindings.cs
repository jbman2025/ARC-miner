// Bind environment variables to MinerOptions, with deprecation warnings for
// any v1-era AKOYA_* var that has no V2 effect.
//
// Single entry point: EnvVarBindings.Load(log). This is the *only* place
// env vars are read at startup. The rest of the miner takes MinerOptions
// through DI.

using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Akoya.Miner.Config;

internal static class EnvVarBindings
{
    /// <summary>
    /// v1 vars that production configs may still set but which V2 ignores.
    /// Each gets a one-line "[deprecated] X ignored in v2" warning at startup
    /// if present in the env. None block startup.
    /// </summary>
    private static readonly (string Var, string Reason)[] DeprecatedVars =
    {
        ("AKOYA_GATEWAY_HOST",                         "no solo/gateway mode in V2"),
        ("AKOYA_GATEWAY_PORT",                         "no solo/gateway mode in V2"),
        ("AKOYA_POOL_FIRST_JOB_TIMEOUT_SEC",           "gRPC server-push delivers job; no race"),
        ("AKOYA_POOL_FIRST_JOB_GIVE_UP_AFTER",         "gRPC server-push delivers job; no race"),
        ("AKOYA_POOL_RECONNECT_BASE_SEC",              "gRPC channel handles reconnect backoff"),
        ("AKOYA_POOL_RECONNECT_CAP_SEC",               "gRPC channel handles reconnect backoff"),
        ("AKOYA_POOL_RECONNECT_INITIAL_JITTER_SEC",    "gRPC channel handles reconnect backoff"),
        ("AKOYA_POOL_RECONNECT_MAX_WAIT_SEC",          "use ReconnectHint.wait_seconds from server"),
        ("AKOYA_POOL_RECONNECT_MIN_INTERVAL_SEC",      "server-side concern in V2"),
        ("AKOYA_POOL_SHARE_STARVATION_SEC",            "v1 protocol workaround; not needed in V2"),
        ("AKOYA_POOL_SHARE_STARVATION_COOLDOWN_SEC",   "v1 protocol workaround; not needed in V2"),
        ("AKOYA_POOL_SHARE_STARVATION_DISABLE",        "v1 protocol workaround; not needed in V2"),
        ("AKOYA_MINE_MAX_ITERS",                       "per-sigma iteration caps are disabled in V2"),
    };

    public static MinerOptions Load(ILogger log)
    {
        WarnOnDeprecated(log);

        // ---- Pool ---------------------------------------------------------
        // Default to the production V2 gateway. Operators can override via
        // AKOYA_POOL_HOST (e.g. a self-hosted gateway). Port defaults to 443
        // when TLS is on (standard), 50052 plaintext otherwise (local dev).
        var host = Env("AKOYA_POOL_HOST") ?? "pool-v2.akoyapool.com";
        // AKOYA_POOL_USE_TLS was used by the original packages and some
        // HiveOS flight sheets. Keep it as a compatibility alias while the
        // canonical current name remains AKOYA_POOL_TLS.
        var tls = ParseBool(Env("AKOYA_POOL_TLS") ?? Env("AKOYA_POOL_USE_TLS"), defaultValue: true);
        int port = ParseInt(Env("AKOYA_POOL_PORT"), defaultValue: tls ? 443 : 50052);

        var wallet = Env("AKOYA_POOL_WALLET")
                     ?? throw new InvalidOperationException(
                         "wallet address is required — pass --wallet <prl1…> or set ARC_POOL_WALLET");
        var worker = Env("AKOYA_POOL_WORKER") ?? Environment.MachineName;

        var pool = new PoolOptions(
            Host: host,
            Port: port,
            UseTls: tls,
            TlsInsecure: ParseBool(Env("AKOYA_POOL_TLS_INSECURE"), defaultValue: false),
            WalletAddress: wallet,
            WorkerName: worker,
            PingIntervalSec:      ParseInt(Env("AKOYA_POOL_PING_INTERVAL_SEC"), 15),
            HeartbeatIntervalSec: ParseInt(Env("AKOYA_POOL_HEARTBEAT_INTERVAL_SEC"), 30),
            // Inbound-idle watchdog: forces a reconnect when the POOL sends no
            // inbound traffic for this long. DISABLED by default (0) — it was
            // tripping spuriously even while the pool was actively delivering
            // jobs, forcing reconnects that reset the σ window and delayed the
            // next share. The OS-level TCP keepalive (30s/10s×4 ≈ 70s) still
            // catches a genuinely dead socket. Set AKOYA_POOL_STREAM_WATCHDOG_SEC
            // to a positive value (e.g. 300) to re-enable the app-layer guard.
            StreamWatchdogSec:    ParseInt(Env("AKOYA_POOL_STREAM_WATCHDOG_SEC"), 0),
            KeepAlivePingSec:     Math.Max(1, ParseInt(Env("AKOYA_POOL_KEEPALIVE_PING_SEC"), 10)),
            KeepAliveTimeoutSec:  Math.Max(1, ParseInt(Env("AKOYA_POOL_KEEPALIVE_TIMEOUT_SEC"), 10)),
            PongTimeoutSec:       ParseInt(Env("AKOYA_POOL_PONG_TIMEOUT_SEC"), 20),
            OutboundDepthTrip:    ParseInt(Env("AKOYA_POOL_OUTBOUND_DEPTH_TRIP"), 16));

        // ---- Mining loop (defaults match v1 1:1) --------------------------
        //
        // NOTE: MatmulsPerPoll is no longer user-configurable. It's derived
        // at startup from the hashrate benchmark so the trigger-detection
        // latency is bounded to ~10ms regardless of GPU class
        // (see WorkerOrchestrator). We seed it here with a probe value
        // (10) which the benchmark uses internally and then overwrites.
        //
        // Similarly the benchmark is mandatory — there is no AKOYA_BENCH_DISABLE.
        // The pool's vardiff depends on us reporting a real hashrate, and we
        // need iter_ms to size the batch.
        // Per-pool default share shape. Some Pearl-stratum pools enforce a
        // fixed share size (M×N×K) on their verifier and reject any proof of a
        // different shape — e.g. HeroMiners requires 131072×131072×4096. We pick
        // the shape from the pool host so the right pool "just works"; an
        // explicit AKOYA_MINE_M/N/K still overrides this everywhere.
        var (defM, defN, defK) = DefaultShareShape(host);
        var mine = new MineOptions(
            M:                 ParseInt(Env("AKOYA_MINE_M"), defM),
            N:                 ParseInt(Env("AKOYA_MINE_N"), defN),
            K:                 ParseInt(Env("AKOYA_MINE_K"), defK),
            NoiseRank:         ParseInt(Env("AKOYA_MINE_NOISE_RANK"), 256),
            MatmulsPerPoll:    10, // probe value; overwritten post-benchmark
            MaxBlocks:         ParseInt(Env("AKOYA_MINE_MAX_BLOCKS"), 0),
            StatsIntervalSec:  ParseDouble(Env("AKOYA_MINE_STATS_INTERVAL_SEC"), 30.0),
            WatchdogTimeoutSec:ParseInt(Env("AKOYA_MINE_WATCHDOG_TIMEOUT_SEC"), 300),
            // No-trigger watchdog: forces a reconnect when the rig produces no
            // shares for this long ("bad vardiff" guard). 300s was far too
            // aggressive — at realistic per-rig share rates a 5-minute dry spell
            // is normal variance, and reconnecting does NOT lower the target, it
            // just drops the σ and thrashes. 1800s (30 min) still eventually
            // catches a genuinely impossible target. Set to 0 to fully disable.
            TriggerWatchdogSec:ParseInt(Env("AKOYA_MINE_TRIGGER_WATCHDOG_SEC"), 1800),
            // Adaptive no-trigger budget: the effective budget is
            //   max(TriggerWatchdogSec, K / Σ expected_opens_per_sec)
            // so the fixed value above is a FLOOR and slow cards (A750/B70) or a
            // high pool diff — where shares are legitimately minutes apart — get
            // the budget extended to ~K expected share intervals instead of
            // being reconnect-thrashed. K is a Poisson safety multiple: at K=20
            // the chance a healthy card stays share-silent past the budget is
            // e^-20 (negligible). Set K=0 to restore the old fixed-budget
            // behaviour (no adaptive extension).
            TriggerWatchdogK:  ParseDouble(Env("AKOYA_MINE_TRIGGER_WATCHDOG_K"), 20.0),
            FakeTarget:        Env("AKOYA_FAKE_TARGET") == "1",
            BenchmarkDurationSec:ParseInt(Env("AKOYA_BENCH_DURATION_SEC"), 10),
            // Emergency switch: force single-stream (V1-equivalent) mining
            // when set. Disables the Ping/Pong concurrent-stream double-buffer
            // so every batch runs sequentially on one CUDA stream. Use this
            // if you observe bursty `claimedHash > liveTarget` pre-submit
            // skips clustered in a single σ window — a symptom that points
            // to a concurrent-stream state hazard. Defaults off (full perf).
            DisablePong:       ParseBool(Env("AKOYA_DISABLE_PONG"), defaultValue: false),
            ShapeOverridePresent: MineShapeOverridePresent(),
            CudaGraphIter:     ParseBool(Env("AKOYA_CUDA_GRAPH_ITER"), defaultValue: false),
            CudaGraphRequired: ParseBool(Env("AKOYA_CUDA_GRAPH_REQUIRED"), defaultValue: false),
            BM:                ParseInt(Env("AKOYA_MINE_BM"), 128),
            BN:                ParseInt(Env("AKOYA_MINE_BN"), 256));

        // ---- GPUs ---------------------------------------------------------
        var gpus = new GpuOptions(
            IndicesRaw: Env("AKOYA_GPU_INDICES")
                        ?? Env("AKOYA_GPU_INDEX")   // legacy single-GPU fallback
                        ?? "all");

        // ---- Observability ------------------------------------------------
        var obs = new ObservabilityOptions(
            LogLevel:        Env("AKOYA_LOG_LEVEL") ?? "Information",
            LogJson:         ParseBool(Env("AKOYA_LOG_JSON"), defaultValue: false),
            MetricsPort:     int.TryParse(Env("AKOYA_METRICS_PORT"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mp) ? mp : null);

        // ---- Session ------------------------------------------------------
        var sess = new SessionOptions(
            FilePath: Env("AKOYA_SESSION_FILE") ?? DefaultSessionPath());

        return new MinerOptions(pool, mine, gpus, obs, sess);
    }

    private static void WarnOnDeprecated(ILogger log)
    {
        foreach (var (name, reason) in DeprecatedVars)
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
            {
                log.LogWarning("[deprecated] {Var} ignored in v2 ({Reason})", name, reason);
            }
        }
    }

    private static string DefaultSessionPath()
    {
        // Prefer $HOME; fall back to /root in containers where HOME may be unset.
        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home))
        {
            home = Environment.OSVersion.Platform == PlatformID.Win32NT
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : "/root";
        }
        // User-facing default is .arc-miner; a pre-existing legacy .akoya
        // session keeps being used so upgrades don't lose session state.
        var legacy = Path.Combine(home, ".akoya", "session.json");
        if (File.Exists(legacy)) return legacy;
        return Path.Combine(home, ".arc-miner", "session.json");
    }

    // All config vars resolve through MinerEnv: the user-facing ARC_* name
    // first, then the legacy AKOYA_* name silently (existing rigs keep
    // working; users only ever see ARC_*).
    private static string? Env(string name) => Akoya.Crypto.MinerEnv.Get(name);

    // The canonical network share shape. Every pool now validates the FULL
    // shape — m, n, k AND noise_rank — against the network mining params and
    // rejects code 23 on any mismatch, so m/n are no longer a free working-set
    // choice. WorkerOrchestrator forces these (plus NoiseRank=256) whenever no
    // explicit AKOYA_MINE_* override is present.
    private static readonly (int M, int N, int K) DefaultShape = (131072, 131072, 4096);

    /// <summary>
    /// Default GEMM share shape (M, N, K) for a given pool host.
    ///
    /// The full shape (m, n, k) plus noise_rank is committed into the jobKey
    /// (BLAKE3(σ ‖ configBytes)) that the pool and block-find consensus
    /// re-derive, so any mismatch fails the share (code 23 / audit_proof_merkle
    /// _mismatch). There is no longer a per-host shape: the canonical shape is
    /// network-wide. Host is retained only for a possible future pool with
    /// divergent consensus params. Explicit AKOYA_MINE_M/N/K/NOISE_RANK env
    /// vars always override the value returned here.
    /// </summary>
    private static (int M, int N, int K) DefaultShareShape(string host)
    {
        return DefaultShape;
    }

    private static bool MineShapeOverridePresent()
        => Env("AKOYA_MINE_M") is not null
           || Env("AKOYA_MINE_N") is not null
           || Env("AKOYA_MINE_K") is not null
           || Env("AKOYA_MINE_NOISE_RANK") is not null;

    private static int ParseInt(string? raw, int defaultValue)
        => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : defaultValue;

    private static double ParseDouble(string? raw, double defaultValue)
        => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : defaultValue;

    private static bool ParseBool(string? raw, bool defaultValue) => raw switch
    {
        null      => defaultValue,
        "1"       => true,
        "0"       => false,
        var s when s.Equals("true",  StringComparison.OrdinalIgnoreCase) => true,
        var s when s.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
        _         => defaultValue,
    };
}
