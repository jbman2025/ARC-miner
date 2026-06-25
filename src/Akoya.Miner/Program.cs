// Akoya.Miner v2
//
// Subcommands:
//   mine-blocks               Connect to pool, register/resume, mine.
//   version | --version | -V  Print git sha + miner version.
//
// Runtime native libs:
//   AKOYA_PEARL_GEMM_LIB    absolute path to libpearl_gemm_capi.so
//   AKOYA_PEARL_MINING_LIB  absolute path to libpearl_mining_capi.so
//   (Unset → falls through to the OS loader via LD_LIBRARY_PATH.)
//
// All other configuration is read once at startup by EnvVarBindings.Load.

using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Akoya.Miner.Config;
using Akoya.Miner.Mining;
using Akoya.Cuda;
using Akoya.Miner.Observability;
using Akoya.Mining;
using Akoya.PearlGemm;
using Akoya.Pool;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.DependencyInjection;

// On WSL the kernel-side libcuda lives at /usr/lib/wsl/lib/libcuda.so.1 and the
// stale dpkg-installed libcuda in /usr/lib/x86_64-linux-gnu wins under ldconfig.
// Loading the latter inside WSL returns CUDA_ERROR_NO_DEVICE (100) from cuInit.
// Prefer the WSL stub when it exists. Same logic the test module-initializer
// uses; mirrored here so production miners on WSL don't fail to enumerate GPUs.
NativeLibrary.SetDllImportResolver(typeof(CudaDriver).Assembly, (name, _, _) =>
{
    if (name != "cuda") return 0;
    // Windows: prefer cuda.dll next to the binary (SYCL/Arc shim), then fall
    // back to nvcuda.dll (NVIDIA GPU driver).
    if (OperatingSystem.IsWindows())
    {
        var localCudaDll = Path.Combine(AppContext.BaseDirectory, "cuda.dll");
        if (File.Exists(localCudaDll))
            return NativeLibrary.Load(localCudaDll);
        return NativeLibrary.Load("nvcuda.dll");
    }
    const string wslLibCuda = "/usr/lib/wsl/lib/libcuda.so.1";
    if (OperatingSystem.IsLinux() && File.Exists(wslLibCuda))
    {
        try { return NativeLibrary.Load(wslLibCuda); }
        catch { /* fall through to default */ }
    }
    // The ROCm backend stages a libcuda.so.1 shim next to the binary.
    var localCuda = Path.Combine(AppContext.BaseDirectory, "libcuda.so.1");
    if (File.Exists(localCuda))
    {
        try { return NativeLibrary.Load(localCuda); }
        catch { /* fall through to default */ }
    }
    return NativeLibrary.Load("libcuda.so.1");
});

NativeLibrary.SetDllImportResolver(typeof(PearlGemmNative).Assembly, (name, _, _) =>
    name == PearlGemmNative.Lib
        ? NativeLibs.Load("AKOYA_PEARL_GEMM_LIB", NativeLibs.GemmFile)
        : 0);

NativeLibrary.SetDllImportResolver(typeof(PearlMiningNative).Assembly, (name, _, _) =>
    name == PearlMiningNative.Lib
        ? NativeLibs.Load("AKOYA_PEARL_MINING_LIB", NativeLibs.MiningFile)
        : 0);

// Last-resort crash recorder. The fleet runs without easy log retrieval and
// .NET's createdump needs DOTNET_DbgEnableMiniDump=1 set BEFORE managed code
// starts (we can only warn about it here, not set it). This handler at least
// writes a structured plain-text record on any unhandled exception so an
// operator can mail us a single file. Best-effort only; never throws.
AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
{
    try
    {
        var dir = CrashDumpHelpers.ResolveDumpDir();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "last-fatal.log");
        var sb = new StringBuilder();
        sb.Append("ts=").Append(DateTime.UtcNow.ToString("o")).AppendLine();
        sb.Append("miner_version=").Append(VersionInfo.MinerVersion).AppendLine();
        sb.Append("git_sha=").Append(VersionInfo.GitSha).AppendLine();
        sb.Append("terminating=").Append(ev.IsTerminating).AppendLine();
        sb.AppendLine("---");
        sb.AppendLine(ev.ExceptionObject?.ToString() ?? "(no exception object)");
        File.WriteAllText(path, sb.ToString());
    }
    catch { /* swallow — handler must never throw */ }
};

WindowsConsoleHelper.EnableAnsi();

var subcommand = "mine-blocks";
if (args.Length > 0 && !args[0].StartsWith('-') && 
    args[0] != "mine-blocks" && args[0] != "selftest" && args[0] != "--selftest" && 
    args[0] != "version" && args[0] != "--version" && args[0] != "-V")
{
    subcommand = args[0];
}
else
{
    for (int i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (arg == "mine-blocks" || arg == "selftest" || arg == "--selftest" || arg == "version" || arg == "--version" || arg == "-V")
        {
            subcommand = arg;
        }
        else if (arg == "--help" || arg == "-h" || arg == "help")
        {
            subcommand = "help";
        }
        else if (arg == "--pool" && i + 1 < args.Length)
        {
            var val = args[++i];
            bool isStratumFromScheme = false;
            bool? tlsFromScheme = null;
            if (val.StartsWith("stratum+tcp://", StringComparison.OrdinalIgnoreCase))
            {
                val = val["stratum+tcp://".Length..];
                isStratumFromScheme = true;
                tlsFromScheme = false;
            }
            else if (val.StartsWith("stratum+ssl://", StringComparison.OrdinalIgnoreCase))
            {
                val = val["stratum+ssl://".Length..];
                isStratumFromScheme = true;
                tlsFromScheme = true;
            }
            else if (val.StartsWith("stratum+tls://", StringComparison.OrdinalIgnoreCase))
            {
                val = val["stratum+tls://".Length..];
                isStratumFromScheme = true;
                tlsFromScheme = true;
            }
            else if (val.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
            {
                val = val["tcp://".Length..];
                isStratumFromScheme = true;
                tlsFromScheme = false;
            }
            else if (val.StartsWith("ssl://", StringComparison.OrdinalIgnoreCase))
            {
                val = val["ssl://".Length..];
                isStratumFromScheme = true;
                tlsFromScheme = true;
            }
            else if (val.StartsWith("stratum://", StringComparison.OrdinalIgnoreCase))
            {
                val = val["stratum://".Length..];
                isStratumFromScheme = true;
            }
            else if (val.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                val = val["https://".Length..];
            }
            else if (val.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                val = val["http://".Length..];
            }

            if (isStratumFromScheme)
            {
                Environment.SetEnvironmentVariable("AKOYA_POOL_STRATUM", "true");
            }
            if (tlsFromScheme.HasValue)
            {
                Environment.SetEnvironmentVariable("AKOYA_POOL_TLS", tlsFromScheme.Value ? "true" : "false");
            }

            // Split host:port. Must handle bracketed IPv6 literals —
            // [fe80::1%14]:3335 — whose address is full of colons, so a naive
            // Split(':') shreds it. Strip any trailing /path first.
            int slash = val.IndexOf('/');
            if (slash >= 0) val = val[..slash];

            string host;
            string? portStr = null;
            if (val.StartsWith('['))
            {
                // [addr]:port — addr may carry a %zone id (link-local).
                int close = val.IndexOf(']');
                if (close > 0)
                {
                    host = val[1..close];
                    var rest = val[(close + 1)..];
                    if (rest.StartsWith(':') && rest.Length > 1) portStr = rest[1..];
                }
                else
                {
                    host = val; // malformed bracket — pass through, let connect fail clearly
                }
            }
            else
            {
                // host:port — split on the LAST colon so hostnames / IPv4 work.
                // A bare (unbracketed) IPv6 literal isn't supported here; wrap it
                // in [ ] (standard URL form).
                int colon = val.LastIndexOf(':');
                if (colon >= 0)
                {
                    host = val[..colon];
                    portStr = val[(colon + 1)..];
                }
                else
                {
                    host = val;
                }
            }

            Environment.SetEnvironmentVariable("AKOYA_POOL_HOST", host);
            if (!string.IsNullOrEmpty(portStr))
            {
                Environment.SetEnvironmentVariable("AKOYA_POOL_PORT", portStr);
            }
        }
        else if ((arg == "--wallet" || arg == "-w") && i + 1 < args.Length)
        {
            Environment.SetEnvironmentVariable("AKOYA_POOL_WALLET", args[++i]);
        }
        else if ((arg == "--worker" || arg == "-n") && i + 1 < args.Length)
        {
            Environment.SetEnvironmentVariable("AKOYA_POOL_WORKER", args[++i]);
        }
        else if (arg == "--tls")
        {
            Environment.SetEnvironmentVariable("AKOYA_POOL_TLS", "true");
        }
        else if (arg == "--no-tls")
        {
            Environment.SetEnvironmentVariable("AKOYA_POOL_TLS", "false");
        }
        else if (arg == "--tls-insecure")
        {
            Environment.SetEnvironmentVariable("AKOYA_POOL_TLS_INSECURE", "true");
        }
        else if ((arg == "--password" || arg == "-p") && i + 1 < args.Length)
        {
            // Stratum password for challenge-first (pearl/v1) pools. Carries
            // the difficulty request, e.g. "x;d=250000".
            Environment.SetEnvironmentVariable("AKOYA_STRATUM_PASSWORD", args[++i]);
        }
        else if (arg == "--diff" && i + 1 < args.Length)
        {
            // Appends ";d=<n>" to the stratum password if not already present.
            Environment.SetEnvironmentVariable("AKOYA_STRATUM_DIFF", args[++i]);
        }
        else if (arg == "--keepalive")
        {
            // Opt-in application-layer stratum keepalive (off by default).
            // Optional interval in seconds: "--keepalive 90"; bare "--keepalive"
            // uses the 120s default. AKOYA_STRATUM_KEEPALIVE_SEC also works.
            var sec = "120";
            if (i + 1 < args.Length && int.TryParse(args[i + 1], out _))
                sec = args[++i];
            Environment.SetEnvironmentVariable("AKOYA_STRATUM_KEEPALIVE_SEC", sec);
        }
        else if (arg == "--mpp" && i + 1 < args.Length)
        {
            Environment.SetEnvironmentVariable("AKOYA_MINE_MPP_OVERRIDE", args[++i]);
        }
        else if (arg == "--budget" && i + 1 < args.Length)
        {
            Environment.SetEnvironmentVariable("AKOYA_BENCHMARK_BUDGET_MS", args[++i]);
        }
        else if (arg == "--api-port" && i + 1 < args.Length)
        {
            // Local HTTP stats API (JSON at /api/stats, Prometheus at /metrics).
            // Same listener AKOYA_METRICS_PORT configures; the flag exists so
            // bundling launchers (e.g. Kryptex) can enable it per-invocation.
            Environment.SetEnvironmentVariable("AKOYA_METRICS_PORT", args[++i]);
        }
        else if (arg == "--no-autotune")
        {
            // Skip the one-time first-run autotune sweep (mine with defaults /
            // any cached profile). Same as AKOYA_AUTOTUNE_ON_FIRST_RUN=0.
            Environment.SetEnvironmentVariable("AKOYA_AUTOTUNE_ON_FIRST_RUN", "0");
        }
        else if (arg == "--dashboard")
        {
            // Opt-in live in-place TUI dashboard (rig summary + per-GPU table +
            // recent events) instead of the scrolling log. Same as
            // AKOYA_DASHBOARD=1. Ignored when stdout is redirected or JSON
            // logging is on. Optional refresh interval: "--dashboard 500" (ms).
            Environment.SetEnvironmentVariable("AKOYA_DASHBOARD", "1");
            if (i + 1 < args.Length && int.TryParse(args[i + 1], out _))
                Environment.SetEnvironmentVariable("AKOYA_DASHBOARD_REFRESH_MS", args[++i]);
        }
    }
}

return subcommand switch
{
    "mine-blocks"                    => await MineBlocksAsync(args),
    "autotune"                       => RunAutotune(args),
    "selftest" or "--selftest"       => await SelfTestAsync(args),
    "version" or "--version" or "-V" => PrintVersion(),
    "help"                           => Usage(null),
    _                                => Usage(subcommand),
};

static int RunAutotune(string[] args)
{
    using var loggerFactory = BuildLoggerFactory();
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    return Akoya.Miner.Mining.Autotune.Run(args, loggerFactory, cts.Token);
}

static async Task<int> MineBlocksAsync(string[] args)
{
    // Arm the live dashboard before anything prints: when active it owns the
    // screen, so the static ASCII banner is skipped (the panel draws its own
    // title). TryEnable returns false for redirected stdout / JSON logging.
    bool jsonLog = (Akoya.Crypto.MinerEnv.Get("AKOYA_LOG_JSON") ?? "0") is "1" or "true";
    bool dashboard = Akoya.Miner.Observability.Dashboard.TryEnable(jsonLog);
    if (!dashboard) PrintAsciiBanner();
    using var loggerFactory = BuildLoggerFactory();
    var log = loggerFactory.CreateLogger("startup");

    MinerOptions opts;
    try { opts = EnvVarBindings.Load(log); }
    catch (Exception ex)
    {
        // Message only — a config mistake is an operator problem, not a
        // crash; a stack trace here is pure noise (and leaks internals).
        log.LogError("startup: {Message}", ex.Message);
        log.LogInformation("usage: arc-miner --pool <host:port> --wallet <prl1…> [--worker <name>]  (arc-miner --help for all options)");
        return 78; // EX_CONFIG
    }

    log.LogInformation("ARC-miner v{Ver} (git {Sha}) — pool={Host}:{Port} tls={Tls} tls_insecure={Insecure} wallet={Wallet} worker={Worker}",
        VersionInfo.MinerVersion, VersionInfo.GitSha,
        opts.Pool.Host, opts.Pool.Port, opts.Pool.UseTls, opts.Pool.TlsInsecure,
        opts.Pool.WalletAddress, opts.Pool.WorkerName);

    using var cts = new CancellationTokenSource();
    // Cancel-on-disposed-CTS guard: signal handlers and AppDomain.ProcessExit
    // can fire AFTER the `using var cts` scope has already disposed (e.g. when
    // a SIGINT arrives during the last ms of teardown, or when ProcessExit
    // runs as Main is unwinding). Without this, we'd crash with
    // ObjectDisposedException at the very moment we were about to exit
    // cleanly. Static local so all handlers below close over the same CTS.
    static void TryCancel(CancellationTokenSource c)
    {
        try { c.Cancel(); }
        catch (ObjectDisposedException) { /* race with normal shutdown — fine */ }
    }
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        log.LogInformation("startup: Ctrl-C received — initiating graceful shutdown");
        TryCancel(cts);
    };
    // POSIX signal handling (HiveOS, systemd, k8s all send SIGTERM, not SIGINT).
    // PosixSignalRegistration intercepts BEFORE the runtime tears the process
    // down, so we get a real chance to drain. AppDomain.ProcessExit is kept
    // as a last-resort catch — it only fires AFTER unmanaged exit begins,
    // by which time `cts` has already been disposed by its `using` scope,
    // so the cancel call there will routinely race with disposal. Every
    // cancel site below uses TryCancel to tolerate that race instead of
    // bringing the process down with an ObjectDisposedException at the
    // very moment we were about to exit cleanly.
    using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
    {
        ctx.Cancel = true;
        log.LogInformation("startup: SIGTERM received — initiating graceful shutdown");
        TryCancel(cts);
    });
    using var sigHup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx =>
    {
        ctx.Cancel = true;
        log.LogInformation("startup: SIGHUP received — initiating graceful shutdown");
        TryCancel(cts);
    });
    using var sigQuit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, ctx =>
    {
        ctx.Cancel = true;
        log.LogInformation("startup: SIGQUIT received — initiating graceful shutdown");
        TryCancel(cts);
    });
    AppDomain.CurrentDomain.ProcessExit += (_, _) => TryCancel(cts);

    // Shutdown deadline: after cancellation is requested, the rest of the
    // program MUST exit within 30s. If a CUDA handle is wedged or a native
    // teardown is stuck, we'd rather Environment.Exit ourselves than wait
    // for systemd/k8s/HiveOS to SIGKILL us mid-share-submit. Disposed at
    // the end of MineBlocksAsync, so a clean exit cancels the timer.
    //
    // 30s = worker DisposeGrace (10s) + pool channel shutdown (~2s) +
    // an in-flight share-submit allowance + slack. Tuned to land BELOW
    // every supervisor's default kill timer (k8s 30s default is a tight
    // squeeze — operators on k8s should raise terminationGracePeriodSeconds
    // to 60s if they care about clean shutdowns).
    using var shutdownDeadline = ShutdownDeadline.Arm(
        cts.Token,
        TimeSpan.FromSeconds(30),
        () => Environment.Exit(ShutdownDeadline.HardExitCode),
        log);

    if (opts.Observability.MetricsPort is int port)
    {
        Metrics.TryStart(port, loggerFactory.CreateLogger("metrics"), cts.Token);
    }

    // Zero-config tuning: on the first run for this GPU (no cached profile),
    // run the autotune sweep once before mining so A-series cards don't mine at
    // the B-series default window (~25× slower). A cache hit makes this a no-op;
    // the mine path then applies the cached profile. Opt out: --no-autotune.
    Akoya.Miner.Mining.Autotune.EnsureTunedOrSweep(args, opts, loggerFactory, cts.Token);

    // Reconnect loop: any unhandled stream exit (graceful, RpcException,
    // stream-watchdog cancellation, worker-watchdog cancellation) triggers a
    // jittered exponential backoff + Resume attempt. Fatal config errors
    // break out. Clean exits (server hangup, ReconnectHint) reconnect
    // immediately with attempt counter reset.
    int attempt = 0;
    // Construct the orchestrator ONCE per process. Inside, per-attempt
    // resources (PoolConnection, MiningSession, GpuWorkers) live in
    // RunAsync's using/await-using scopes and are recreated each loop.
    // What we deliberately keep across reconnects is orchestrator state
    // such as the cached benchmark result — the GPU rig's hashrate and
    // iter_ms don't change between a stream-end and the Resume that
    // follows, so re-benchmarking is wasted GPU time.
    var orchestrator = new WorkerOrchestrator(opts, loggerFactory);

    // Periodic at-a-glance session rollup (uptime / totals / rig hashrate).
    // Process-level so it spans reconnects; Metrics counters are cumulative.
    var sessionClock = Stopwatch.StartNew();
    var summaryLog = loggerFactory.CreateLogger("session");
    using var summaryCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
    // With the dashboard active, its in-place panel already shows uptime /
    // totals / rig hashrate continuously, so we run the render loop in place of
    // the periodic one-line session rollup.
    var summaryTask = dashboard
        ? Akoya.Miner.Observability.Dashboard.RunAsync(sessionClock, summaryCts.Token)
        : SessionSummaryLoop(sessionClock, summaryLog, summaryCts.Token);

    while (!cts.IsCancellationRequested)
    {
        TimeSpan? hintWait = null;
        try
        {
            await orchestrator.RunAsync(cts.Token).ConfigureAwait(false);
            log.LogInformation("orchestrator: stream ended cleanly — reconnecting");
            attempt = 0;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { break; }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Register rejected"))
        {
            log.LogError(ex, "fatal: server rejected registration — not retrying");
            return 78;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("build that matches your card"))
        {
            // Wrong-card AOT build (acm on a B-series GPU or vice versa). This is
            // permanent — retrying would loop forever, so surface the one-liner
            // and exit. Message-only (no stack trace — it's an operator problem).
            log.LogError("startup: {Message}", ex.Message);
            return 78;
        }
        catch (PoolUnreachableException ex)
        {
            // Translated TaskCanceledException / RpcException(Unavailable|
            // DeadlineExceeded) from Register/Resume — channel never reached
            // ready state. Almost always wrong host/port or firewall. Skip
            // the stack trace (it's all Grpc internals) and surface just the
            // operator-actionable one-liner, then back off and retry like
            // any other transient failure.
            attempt++;
            var backoff = ReconnectBackoff.ComputeDelay(
                attempt, (Random.Shared.NextDouble() * 2) - 1);
            log.LogWarning(
                "orchestrator: {Msg} — retry in {Delay:F1}s (attempt {Attempt})",
                ex.Message, backoff.TotalSeconds, attempt);
            try { await Task.Delay(backoff, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        catch (StreamIdleException ex)
        {
            // Distinct log line: "gateway is alive but silent" is a very
            // different operational signal from a generic RPC failure.
            // We deliberately don't bypass the backoff path — silent stream
            // = treat-as-failure-attempt, same exp backoff applies.
            attempt++;
            var backoff = ReconnectBackoff.ComputeDelay(
                attempt, (Random.Shared.NextDouble() * 2) - 1);
            log.LogWarning(
                "orchestrator: stream went silent ({Msg}) — retry in {Delay:F1}s (attempt {Attempt})",
                ex.Message, backoff.TotalSeconds, attempt);
            try { await Task.Delay(backoff, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        catch (WorkerTripException ex)
        {
            attempt++;
            var backoff = ReconnectBackoff.ComputeDelay(
                attempt, (Random.Shared.NextDouble() * 2) - 1);
            log.LogWarning(ex,
                "orchestrator: local worker trip ({Reason}) — retry in {Delay:F1}s (attempt {Attempt})",
                ex.Reason, backoff.TotalSeconds, attempt);
            try { await Task.Delay(backoff, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        catch (Exception ex)
        {
            attempt++;
            // Exponential cap + ±25% jitter
            var backoff = ReconnectBackoff.ComputeDelay(
                attempt, (Random.Shared.NextDouble() * 2) - 1);
            log.LogWarning(ex, "orchestrator: error — retry in {Delay:F1}s (attempt {Attempt})",
                backoff.TotalSeconds, attempt);
            try { await Task.Delay(backoff, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        finally
        {
            // Capture any server-supplied ReconnectHint before disposing.
            if (orchestrator.LastReconnectHint is { WaitSeconds: > 0 } h)
            {
                if (ReconnectBackoff.HintWasClamped(h.WaitSeconds))
                {
                    log.LogWarning(
                        "orchestrator: ReconnectHint wait={W}s clamped to {C}s",
                        h.WaitSeconds, ReconnectBackoff.MaxReconnectHintSeconds);
                }
                hintWait = ReconnectBackoff.ApplyHint(
                    h.WaitSeconds, (Random.Shared.NextDouble() * 2) - 1);
            }
            await orchestrator.DisposeAsync().ConfigureAwait(false);
        }

        if (hintWait is TimeSpan w && !cts.IsCancellationRequested)
        {
            log.LogInformation("orchestrator: honouring ReconnectHint wait={W:F1}s", w.TotalSeconds);
            try { await Task.Delay(w, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    summaryCts.Cancel();
    try { await summaryTask.ConfigureAwait(false); } catch { /* shutdown */ }
    LogSessionSummary(summaryLog, sessionClock.Elapsed, final: true);
    log.LogInformation("ARC-miner: shutdown complete");
    return 0;
}

// Emit a one-line session rollup every AKOYA_SUMMARY_INTERVAL_SEC (default 300s)
// until cancelled. Process-level: totals are cumulative across reconnects.
static async Task SessionSummaryLoop(Stopwatch clock, ILogger log, CancellationToken ct)
{
    int sec = int.TryParse(Akoya.Crypto.MinerEnv.Get("AKOYA_SUMMARY_INTERVAL_SEC"), out var s) && s > 0
        ? s : 300;
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(sec));
    try
    {
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            LogSessionSummary(log, clock.Elapsed, final: false);
    }
    catch (OperationCanceledException) { /* shutdown */ }
}

static void LogSessionSummary(ILogger log, TimeSpan up, bool final)
{
    var (acc, rej) = Metrics.ShareTotals();
    var hashrate = GpuWorker.FormatHashRate(Metrics.TotalHashesPerSec());
    var connected = Metrics.IsPoolConnected ? "yes" : "no";
    var uptime = $"{(int)up.TotalHours:D2}:{up.Minutes:D2}:{up.Seconds:D2}";
    if (final)
        log.LogInformation(
            "═══ session summary — uptime={Up} accepted={Acc} rejected={Rej} hashrate={Hps}",
            uptime, acc, rej, hashrate);
    else
        log.LogInformation(
            "session uptime={Up} accepted={Acc} rejected={Rej} hashrate={Hps} pool_connected={Conn}",
            uptime, acc, rej, hashrate, connected);
}

static void PrintAsciiBanner()
{
    const string cyan = "\u001b[96m";
    const string blue = "\u001b[94m";
    const string reset = "\u001b[0m";
    const string bold = "\u001b[1m";

    Console.WriteLine(cyan + @"    _   ___  ___    " + blue + @"__  __ ___ _  _ ___ ___ " + reset);
    Console.WriteLine(cyan + @"   /_\ | _ \/ __|   " + blue + @"|  \/  |_ _| \| | __| _ \" + reset);
    Console.WriteLine(cyan + @"  / _ \|   / (__    " + blue + @"| |\/| || || .` | _||   /" + reset);
    Console.WriteLine(cyan + @" /_/ \_\_|_\\___|   " + blue + @"|_|  |_|___|_|\_|___|_|_\ " + reset);
    Console.WriteLine();
    Console.WriteLine(bold + blue + $"    ✦ ARC GPU Miner v{VersionInfo.MinerVersion} | Intel Arc · 0% Dev Fee FOREVER ✦" + reset);
    Console.WriteLine();
}

static int PrintVersion()
{
    Console.WriteLine($"ARC-miner v{VersionInfo.MinerVersion} (git {VersionInfo.GitSha})");
    Console.WriteLine("Pearl stratum pool miner — Intel Arc / SYCL");
    return 0;
}

static int Usage(string? c)
{
    if (c is not null) Console.Error.WriteLine($"unknown subcommand: {c}");
    Console.Error.WriteLine("usage: arc-miner [mine-blocks|selftest|version] [options]");
    Console.Error.WriteLine("  mine-blocks  Connect to pool, register/resume, mine. (default)");
    Console.Error.WriteLine("  autotune     Sweep kernel knobs (NB/MB/SEARCH_M) on this GPU, print + cache the best config.");
    Console.Error.WriteLine("               flags: --autotune-deep (exhaustive grid), --autotune-max-search-m <n>, --autotune-duration <s>");
    Console.Error.WriteLine("  selftest     Validate config + pool + native libs + session store; emit JSON; exit 0/1.");
    Console.Error.WriteLine("  version      Print git sha + miner version.");
    Console.Error.WriteLine("options:");
    Console.Error.WriteLine("  --pool <host:port>     Override pool address");
    Console.Error.WriteLine("  --wallet | -w <addr>   Set wallet address");
    Console.Error.WriteLine("  --worker | -n <name>   Set worker name");
    Console.Error.WriteLine("  --tls | --no-tls       Enable/disable TLS (default: TLS enabled)");
    Console.Error.WriteLine("  --tls-insecure         Enable insecure TLS connection");
    Console.Error.WriteLine("  --password | -p <pw>   Stratum password (pearl/v1 pools; e.g. \"x;d=250000\")");
    Console.Error.WriteLine("  --diff <n>             Request difficulty n via the stratum password");
    Console.Error.WriteLine("  --mpp <count>          Override pipelining MatmulsPerPoll count");
    Console.Error.WriteLine("  --budget <ms>          Override benchmark target trigger budget in ms");
    Console.Error.WriteLine("  --keepalive [sec]      Enable stratum keepalive re-auth (default off; interval 120s)");
    Console.Error.WriteLine("  --api-port <port>      Enable local HTTP stats API (JSON /api/stats, Prometheus /metrics)");
    Console.Error.WriteLine("  --dashboard [ms]       Live in-place TUI dashboard (rig + per-GPU table + events) instead of scrolling log");
    Console.Error.WriteLine("  --no-autotune          Skip the one-time first-run autotune sweep (mine with defaults/cache)");
    Console.Error.WriteLine("note: first run auto-tunes once (cached); especially important on A-series. V2 is pool-only.");
    return c is null ? 0 : 64;   // explicit --help is success; unknown subcommand is EX_USAGE
}

// --selftest: ship-readiness check that an operator can run once after install
// to validate every wire is connected, then bail. Returns 0 if all probes
// pass; 1 if any failed. Always emits a JSON report on stdout so wrappers
// (HiveOS rig checks, k8s initContainers, Docker HEALTHCHECK) can parse.
//
// Probe list:
//   config         — env vars load into MinerOptions without throwing
//   crashdump_env  — DOTNET_DbgEnableMiniDump is set (warn-only, doesn't fail)
//   pearl_gemm_lib — libpearl_gemm_capi.so resolves & loads
//   pearl_mining_lib — libpearl_mining_capi.so resolves & loads
//   session_store  — configured path is writable + readable (round-trip)
//   pool_tcp       — TCP connect to pool host:port within 5s
static async Task<int> SelfTestAsync(string[] _)
{
    var probes = new List<SelfTestProbe>();

    // Use a null logger so the JSON on stdout isn't polluted with prose.
    var log = NullLogger.Instance;

    MinerOptions? opts = null;
    probes.Add(RunProbe("config", () =>
    {
        opts = EnvVarBindings.Load(log);
        return $"host={opts.Pool.Host} port={opts.Pool.Port} tls={opts.Pool.UseTls} wallet_len={opts.Pool.WalletAddress.Length}";
    }));

    probes.Add(RunProbe("crashdump_env", () =>
    {
        var e = Environment.GetEnvironmentVariable("DOTNET_DbgEnableMiniDump");
        if (e != "1")
            throw new InvalidOperationException(
                "DOTNET_DbgEnableMiniDump != '1' — set it in the launcher / Dockerfile / systemd unit. " +
                "Without it, .NET will not write a core dump on fatal exceptions and field diagnosis " +
                "is limited to last-fatal.log (plain text, no native frames).");
        return $"set=1 type={Environment.GetEnvironmentVariable("DOTNET_DbgMiniDumpType") ?? "(unset)"} " +
               $"name={Environment.GetEnvironmentVariable("DOTNET_DbgMiniDumpName") ?? "(unset)"}";
    }, warnOnly: true));

    probes.Add(RunProbe("pearl_gemm_lib", () =>
    {
        NativeLibrary.Free(NativeLibs.Load("AKOYA_PEARL_GEMM_LIB", NativeLibs.GemmFile));
        return Akoya.Crypto.MinerEnv.Get("AKOYA_PEARL_GEMM_LIB") ?? $"{NativeLibs.GemmFile} (resolved)";
    }));

    probes.Add(RunProbe("pearl_mining_lib", () =>
    {
        NativeLibrary.Free(NativeLibs.Load("AKOYA_PEARL_MINING_LIB", NativeLibs.MiningFile));
        return Akoya.Crypto.MinerEnv.Get("AKOYA_PEARL_MINING_LIB") ?? $"{NativeLibs.MiningFile} (resolved)";
    }));

    probes.Add(RunProbe("session_store", () =>
    {
        if (opts is null) throw new InvalidOperationException("config probe failed; session_store not attempted");
        var path = opts.Session.FilePath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var probePath = path + ".selftest";
        var sentinel = $"arc-miner selftest {DateTime.UtcNow:o}";
        File.WriteAllText(probePath, sentinel);
        var read = File.ReadAllText(probePath);
        File.Delete(probePath);
        if (read != sentinel) throw new IOException($"session-store roundtrip mismatch at {probePath}");
        return $"path={path} writable=true";
    }));

    await Task.Run(async () =>
    {
        probes.Add(await RunProbeAsync("pool_tcp", async () =>
        {
            if (opts is null) throw new InvalidOperationException("config probe failed; pool_tcp not attempted");
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync(opts.Pool.Host, opts.Pool.Port, cts.Token).ConfigureAwait(false);
            return $"connected {opts.Pool.Host}:{opts.Pool.Port}";
        }).ConfigureAwait(false));
    }).ConfigureAwait(false);

    // Emit JSON manually — keeps us AOT-clean (no reflection-based serializer).
    var sb = new StringBuilder();
    sb.Append("{\"version\":\"").Append(VersionInfo.MinerVersion).Append("\",");
    sb.Append("\"git_sha\":\"").Append(VersionInfo.GitSha).Append("\",");
    sb.Append("\"timestamp\":\"").Append(DateTime.UtcNow.ToString("o")).Append("\",");
    sb.Append("\"probes\":[");
    for (int i = 0; i < probes.Count; i++)
    {
        if (i > 0) sb.Append(',');
        var p = probes[i];
        sb.Append("{\"name\":\"").Append(p.Name).Append("\",");
        sb.Append("\"status\":\"").Append(p.Status).Append("\",");
        sb.Append("\"detail\":\"").Append(JsonEscape(p.Detail)).Append('"');
        sb.Append('}');
    }
    sb.Append("],");
    bool anyFailed = probes.Any(p => p.Status == "fail");
    sb.Append("\"overall\":\"").Append(anyFailed ? "fail" : "pass").Append("\"}");
    Console.WriteLine(sb.ToString());
    return anyFailed ? 1 : 0;
}

static SelfTestProbe RunProbe(string name, Func<string> fn, bool warnOnly = false)
{
    try { return new SelfTestProbe(name, "pass", fn()); }
    catch (Exception ex) { return new SelfTestProbe(name, warnOnly ? "warn" : "fail", ex.Message); }
}

static async Task<SelfTestProbe> RunProbeAsync(string name, Func<Task<string>> fn, bool warnOnly = false)
{
    try { return new SelfTestProbe(name, "pass", await fn().ConfigureAwait(false)); }
    catch (Exception ex) { return new SelfTestProbe(name, warnOnly ? "warn" : "fail", ex.Message); }
}

static string JsonEscape(string s)
{
    var sb = new StringBuilder(s.Length + 8);
    foreach (var c in s)
    {
        switch (c)
        {
            case '\\': sb.Append("\\\\"); break;
            case '"':  sb.Append("\\\""); break;
            case '\b': sb.Append("\\b"); break;
            case '\f': sb.Append("\\f"); break;
            case '\n': sb.Append("\\n"); break;
            case '\r': sb.Append("\\r"); break;
            case '\t': sb.Append("\\t"); break;
            default:
                if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
                break;
        }
    }
    return sb.ToString();
}

static ILoggerFactory BuildLoggerFactory()
{
    var levelEnv = Akoya.Crypto.MinerEnv.Get("AKOYA_LOG_LEVEL") ?? "Information";
    if (!Enum.TryParse<LogLevel>(levelEnv, ignoreCase: true, out var level))
        level = LogLevel.Information;
    var json = (Akoya.Crypto.MinerEnv.Get("AKOYA_LOG_JSON") ?? "0") is "1" or "true";

    return LoggerFactory.Create(builder =>
    {
        var b = builder.SetMinimumLevel(level);
        if (json)
        {
            b.AddJsonConsole(opts =>
            {
                opts.IncludeScopes      = false;
                opts.UseUtcTimestamp    = true;
                opts.TimestampFormat    = "yyyy-MM-ddTHH:mm:ss.fffZ";
                opts.JsonWriterOptions  = new System.Text.Json.JsonWriterOptions { Indented = false };
            });
        }
        else
        {
            b.AddConsole(opts =>
            {
                opts.FormatterName = "akoya";
            });
            b.Services.AddSingleton<ConsoleFormatter, Akoya.Miner.Observability.CustomConsoleFormatter>();
        }
    });
}

internal static class CrashDumpHelpers
{
    /// <summary>
    /// Resolves the dump directory in priority order:
    /// 1. ARC_DUMP_DIR (legacy AKOYA_DUMP_DIR honoured)
    /// 2. $ARC_HOME/dumps (legacy AKOYA_HOME honoured)
    /// 3. $HOME/.arc-miner/dumps
    /// </summary>
    public static string ResolveDumpDir()
    {
        var d = Akoya.Crypto.MinerEnv.Get("AKOYA_DUMP_DIR");
        if (!string.IsNullOrEmpty(d)) return d;
        var home = Akoya.Crypto.MinerEnv.Get("AKOYA_HOME");
        if (!string.IsNullOrEmpty(home)) return Path.Combine(home, "dumps");
        var userHome = Environment.GetEnvironmentVariable("HOME") ?? "/tmp";
        return Path.Combine(userHome, ".arc-miner", "dumps");
    }
}

internal readonly record struct SelfTestProbe(string Name, string Status, string Detail);

// Native library resolution for the miner's P/Invoke libraries.
//   1. $<envVar> — explicit absolute path override.
//   2. next to the executable (AppContext.BaseDirectory) — the layout build.sh
//      produces, so `./out/akoya-miner` finds `./out/lib*.so` with no env setup.
//   3. the OS loader (LD_LIBRARY_PATH / system paths) as a last resort.
internal static class NativeLibs
{
    // Platform-specific filenames for the two P/Invoke libraries the build
    // stages next to the binary: lib*.so on Linux, *.dll on Windows.
    public static string GemmFile =>
        OperatingSystem.IsWindows() ? "pearl_gemm_capi.dll" : "libpearl_gemm_capi.so";
    public static string MiningFile =>
        OperatingSystem.IsWindows() ? "pearl_mining_capi.dll" : "libpearl_mining_capi.so";

    public static nint Load(string envVar, string fileName)
    {
        var p = Akoya.Crypto.MinerEnv.Get(envVar);
        if (!string.IsNullOrEmpty(p)) return NativeLibrary.Load(p);
        var local = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(local)) return NativeLibrary.Load(local);
        return NativeLibrary.Load(fileName);
    }
}

internal static class WindowsConsoleHelper
{
    private const int STD_OUTPUT_HANDLE = -11;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(nint hConsoleHandle, uint dwMode);

    public static void EnableAnsi()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch { }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        try
        {
            var hOut = GetStdHandle(STD_OUTPUT_HANDLE);
            if (hOut != IntPtr.Zero && GetConsoleMode(hOut, out uint mode))
            {
                mode |= ENABLE_VIRTUAL_TERMINAL_PROCESSING;
                SetConsoleMode(hOut, mode);
            }
        }
        catch
        {
            // Ignore if console is redirected or helper fails
        }
    }
}
