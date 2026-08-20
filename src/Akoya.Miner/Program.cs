// Akoya.Miner v2
//
// Subcommands:
//   mine-blocks               Connect to pool, register/resume, mine.
//   version | --version | -V  Print git sha + miner version.
//
// Runtime native libs:
//   ARC_PEARL_GEMM_LIB    absolute path to libpearl_gemm_capi.so
//   ARC_PEARL_MINING_LIB  absolute path to libpearl_mining_capi.so
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
    // back to the extracted temp folder, then to nvcuda.dll (NVIDIA GPU driver).
    if (OperatingSystem.IsWindows())
    {
        var localCudaDll = Path.Combine(AppContext.BaseDirectory, "cuda.dll");
        if (File.Exists(localCudaDll))
            return NativeLibrary.Load(localCudaDll);

        var extPath = NativeLibs.ExtractedPath;
        if (extPath != null)
        {
            var extractedCuda = Path.Combine(extPath, "cuda.dll");
            if (File.Exists(extractedCuda))
            {
                NativeLibs.PreloadDependencies();
                return NativeLibrary.Load(extractedCuda);
            }
        }
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

    var extPathLinux = NativeLibs.ExtractedPath;
    if (extPathLinux != null)
    {
        var extractedCuda = Path.Combine(extPathLinux, "libcuda.so.1");
        if (File.Exists(extractedCuda))
        {
            try { return NativeLibrary.Load(extractedCuda); }
            catch { }
        }
    }

    return NativeLibrary.Load("libcuda.so.1");
});

NativeLibrary.SetDllImportResolver(typeof(PearlGemmNative).Assembly, (name, _, _) =>
    name == PearlGemmNative.Lib
        ? NativeLibs.Load("ARC_PEARL_GEMM_LIB", NativeLibs.GemmFile)
        : 0);

NativeLibrary.SetDllImportResolver(typeof(PearlMiningNative).Assembly, (name, _, _) =>
    name == PearlMiningNative.Lib
        ? NativeLibs.Load("ARC_PEARL_MINING_LIB", NativeLibs.MiningFile)
        : 0);

// Resolvers for the executing assembly (Akoya.Miner) to catch the per-algo
// *_capi libraries.
//
// The list lives in NativeLibs.AlgoCapiLibs because it is LOAD-BEARING AND EASY
// TO MISS — see the note there. AlgoCapiLibResolutionTests pins it against the
// algo registry so the next algo cannot ship without it.
NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), (name, _, _) =>
{
    if (Array.IndexOf(NativeLibs.AlgoCapiLibs, name) >= 0)
    {
        var fileName = name + (OperatingSystem.IsWindows() ? ".dll" : ".so");
        return NativeLibs.Load($"ARC_{name.ToUpper()}_LIB", fileName);
    }
    return 0;
});

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

// Flags are sugar over ARC_* environment variables; the table lives in
// Config/CommandLine.cs so it can be unit-tested without launching a process.
var cli = Akoya.Miner.Config.CommandLine.Parse(args);
Akoya.Miner.Config.CommandLine.Apply(cli);
var subcommand = cli.Subcommand;

return subcommand switch
{
    "mine-blocks"                    => await MineBlocksAsync(args),
    "autotune"                       => RunAutotune(args),
    "selftest" or "--selftest"       => await SelfTestAsync(args),
    "version" or "--version" or "-V" => PrintVersion(),
    "verify-seeds"                   => VerifyNoiseSeeds(),
    "help"                           => Usage(null),
    _                                => Usage(subcommand),
};

static int RunAutotune(string[] args)
{
    using var loggerFactory = BuildLoggerFactory();
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    return Akoya.Miner.Algos.Prl.Autotune.Run(args, loggerFactory, cts.Token);
}

static async Task<int> MineBlocksAsync(string[] args)
{
    // Arm the live dashboard before anything prints: when active it owns the
    // screen, so the static ASCII banner is skipped (the panel draws its own
    // title). TryEnable returns false for redirected stdout / JSON logging.
    bool jsonLog = (Akoya.Crypto.MinerEnv.Get("ARC_LOG_JSON") ?? "0") is "1" or "true";
    bool dashboard = Akoya.Miner.Observability.Dashboard.TryEnable(jsonLog);
    if (!dashboard) PrintAsciiBanner();
    using var loggerFactory = BuildLoggerFactory();
    var log = loggerFactory.CreateLogger("startup");

    // Apply control-API-saved settings (pool/wallet/worker/algo) BEFORE anything
    // reads them: when present, ~/.arc-miner/control.json overrides the matching
    // CLI flags/env so a change made in the dashboard survives the restart it
    // triggers. Must precede algo resolution below (it reads ARC_ALGO).
    Akoya.Miner.Config.ControlConfig.ApplyToEnvironment(log);

    // Resolve the algorithm FIRST: non-PRL modules (e.g. btx) own their whole
    // config surface (ARC_BTX_*), so the PRL pool-wallet requirement and the
    // Pearl autotune below must not apply to them. PRL keeps the original
    // order-of-operations byte-identical.
    var algoName = Akoya.Crypto.MinerEnv.Get("ARC_ALGO") ?? "prl";
    var algo = Akoya.Miner.Algos.AlgoRegistry.Resolve(algoName);
    if (algo is null)
    {
        log.LogError("startup: unknown --algo '{Algo}' — registered: {Names}",
            algoName, Akoya.Miner.Algos.AlgoRegistry.RegisteredNames);
        return 78; // EX_CONFIG
    }
    bool isPrl = algo.Name == "prl";
    // PRL as either half of a dual pair (prl+gr, prl+rx) still needs the Pearl
    // wallet and the GEMM autotune sweep — those are properties of the PRL
    // module, not of it running alone.
    bool hasPrl = isPrl || algo.Name.Split('+').Contains("prl");
    // Report the active algorithm in the stats API. "prl" stays "pearl" for
    // backward compat with existing /api/stats consumers; other algos report
    // their own name.
    Akoya.Miner.Observability.Metrics.SetAlgorithm(isPrl ? "pearl" : algo.Name);
    if (!hasPrl && Akoya.Crypto.MinerEnv.Get("ARC_POOL_WALLET") is null)
    {
        // Satisfy the PRL-shaped options loader; the value is never used by
        // non-PRL algos (they don't talk to the Pearl pool at all).
        Environment.SetEnvironmentVariable("ARC_POOL_WALLET", "unused-non-prl-algo");
    }

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

    if (hasPrl)
        log.LogInformation("ARC-miner v{Ver} (git {Sha}) — pool={Host}:{Port} tls={Tls} tls_insecure={Insecure} wallet={Wallet} worker={Worker}",
            VersionInfo.MinerVersion, VersionInfo.GitSha,
            opts.Pool.Host, opts.Pool.Port, opts.Pool.UseTls, opts.Pool.TlsInsecure,
            opts.Pool.WalletAddress, opts.Pool.WorkerName);
    else
        log.LogInformation("ARC-miner v{Ver} (git {Sha}) — algo={Algo}",
            VersionInfo.MinerVersion, VersionInfo.GitSha, algo.Name);

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

    // (algo was resolved before options load — see above.)

    // Wire the restart hook UNCONDITIONALLY. It used to live inside the
    // --api-port branch, which meant the rank-penalty fork could not relaunch
    // itself on the overwhelming majority of rigs — they run without the stats
    // API. The password still gates the control ENDPOINTS; this only gives the
    // process a way to ask for its own graceful relaunch.
    var apiPassword = Akoya.Crypto.MinerEnv.Get("ARC_API_PASSWORD");
    Metrics.ConfigureControl(apiPassword, () => TryCancel(cts));

    if (opts.Observability.MetricsPort is int port)
    {
        Metrics.TryStart(port, loggerFactory.CreateLogger("metrics"), cts.Token);
        if (!string.IsNullOrEmpty(apiPassword))
            log.LogInformation("metrics: control API enabled (localhost-only, password-protected)");
    }

    // Zero-config tuning: on the first run for this GPU (no cached profile),
    // run the autotune sweep once before mining so A-series cards don't mine at
    // the B-series default window (~25× slower). A cache hit makes this a no-op;
    // the mine path then applies the cached profile. Opt out: --no-autotune.
    // PRL-only: the sweep drives the Pearl GEMM kernel; other algos own their
    // native tuning (or have none yet).
    if (hasPrl)
        Akoya.Miner.Algos.Prl.Autotune.EnsureTunedOrSweep(args, opts, loggerFactory, cts.Token);

    // Periodic at-a-glance session rollup (uptime / totals / rig hashrate).
    // Process-level so it spans reconnects; Metrics counters are cumulative.
    var sessionClock = Stopwatch.StartNew();
    var summaryLog = loggerFactory.CreateLogger("session");
    using var summaryCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
    // With the dashboard active, its in-place panel already shows uptime /
    // totals / rig hashrate continuously, so we run the render loop in place of
    // the periodic one-line session rollup.
    // Let the dashboard's 'q'/Esc key request a graceful shutdown (same as Ctrl-C).
    if (dashboard)
        Akoya.Miner.Observability.Dashboard.OnQuit = () => TryCancel(cts);
    var summaryTask = dashboard
        ? Akoya.Miner.Observability.Dashboard.RunAsync(sessionClock, summaryCts.Token)
        : SessionSummaryLoop(sessionClock, summaryLog, summaryCts.Token);

    // The selected algorithm module owns the orchestrator + reconnect loop.
    // PRL is the default (Algos/Prl/PrlAlgo.cs); --algo picks another.
    int exit = await algo.RunAsync(opts, loggerFactory, cts.Token).ConfigureAwait(false);
    // Fatal exits (e.g. 78) skip the summary epilogue, exactly as the pre-seam
    // `return 78` paths did. Stand the dashboard down first: this path never
    // cancels summaryCts, so the render loop's own cleanup would not run, and
    // the process would exit with the cursor hidden and the error that killed
    // the run still sitting unread in the panel's event ring.
    if (exit != 0)
    {
        Akoya.Miner.Observability.Dashboard.Shutdown();
        return exit;
    }

    summaryCts.Cancel();
    try { await summaryTask.ConfigureAwait(false); } catch { /* shutdown */ }
    LogSessionSummary(summaryLog, sessionClock.Elapsed, final: true);

    // A control-API config change requests shutdown-then-restart so the new
    // control.json takes effect. The GPU + pool pipeline is already torn down
    // here; relaunch a fresh process (or, under a supervisor, exit with a
    // restart code and let it handle the respawn).
    if (Metrics.RestartRequested)
        return RestartForControlChange(args, log);

    log.LogInformation("ARC-miner: shutdown complete");
    return 0;
}

// Restart the miner to apply a control-API config change. Default is a built-in
// self-relaunch so it works standalone; set ARC_API_RESTART_MODE=exit under a
// process supervisor (systemd/HiveOS/launcher) to avoid a double-spawn — the
// miner then exits 75 (EX_TEMPFAIL) and the supervisor respawns it.
static int RestartForControlChange(string[] args, ILogger log)
{
    var mode = (Akoya.Crypto.MinerEnv.Get("ARC_API_RESTART_MODE") ?? "self").Trim().ToLowerInvariant();
    if (mode == "exit")
    {
        log.LogInformation("control: exiting 75 for supervisor restart (ARC_API_RESTART_MODE=exit)");
        return 75; // EX_TEMPFAIL — supervisor restarts us
    }

    try
    {
        // Free the stats/control port so the child can rebind immediately.
        Metrics.Stop();
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            log.LogError("control: cannot determine executable path — exiting 75 for supervisor restart");
            return 75;
        }
        var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi);
        log.LogInformation("control: relaunched miner to apply new config");
        return 0;
    }
    catch (Exception e)
    {
        log.LogError("control: self-relaunch failed ({Err}) — exiting 75 for supervisor restart", e.Message);
        return 75;
    }
}

// Emit a one-line session rollup every ARC_SUMMARY_INTERVAL_SEC (default 300s)
// until cancelled. Process-level: totals are cumulative across reconnects.
static async Task SessionSummaryLoop(Stopwatch clock, ILogger log, CancellationToken ct)
{
    int sec = int.TryParse(Akoya.Crypto.MinerEnv.Get("ARC_SUMMARY_INTERVAL_SEC"), out var s) && s > 0
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
    var hashrate = DisplayFormat.HashRate(Metrics.TotalHashesPerSec());
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
    Console.WriteLine(blue + "    Multi-algo miner | Arc A/B-series | --help for options | --dash-off for plain logs" + reset);
    Console.WriteLine();
}

// Proves the GPU and the C# host derive IDENTICAL noise seeds, on both sides of
// the salted-seed hardfork (pearl PR #280).
//
// This is the failure nothing else catches. The GPU derives the seeds that shape
// its search; the host re-derives them to build the share. If they ever disagree
// the miner hunts one noise field and submits proofs for another — every share
// rejected, every dial still green, and no log line to say why. Pre-fork it is
// invisible because both sides were legacy; the fork is exactly the moment the
// two implementations can drift apart.
//
// Runs the real mining kernel via pearl_capi_derive_noise_seeds, not a copy, so
// it cannot pass against code the miner does not use. Lives in the miner because
// the SYCL runtime needs a real process to initialise in — it takes down an
// xunit test host.
static int VerifyNoiseSeeds()
{
    Console.WriteLine("noise-seed derivation: GPU vs host");
    Console.WriteLine();

    static byte[] Fill(byte s)
    {
        var b = new byte[32];
        for (int i = 0; i < 32; i++) b[i] = (byte)(s + i * 7);
        return b;
    }

    var aRoot = Fill(0x11);
    var bRoot = Fill(0x40);
    var jobKey = Fill(0x90);
    const int m = 131072, n = 131072;
    bool allOk = true;

    foreach (int salted in new[] { 0, 1 })
    {
        var (hostB, hostA) = Akoya.Crypto.CommitmentHasher.DeriveNoiseSeeds(
            jobKey, aRoot, bRoot, m, n, salted != 0);

        var devA = new byte[32];
        var devB = new byte[32];
        int rc;
        try
        {
            rc = Akoya.PearlGemm.PearlGemmNative.DeriveNoiseSeedsDevice(
                ref aRoot[0], ref bRoot[0], ref jobKey[0], m, n, salted,
                ref devA[0], ref devB[0], 0);
        }
        catch (Exception e)
        {
            Console.WriteLine($"  {(salted == 1 ? "V3 salted" : "V2 legacy")}: FAILED to call the GPU — {e.Message}");
            Console.WriteLine("    (a pearl_gemm built before the fork has no such export — rebuild it)");
            allOk = false;
            continue;
        }

        bool okA = hostA.AsSpan().SequenceEqual(devA);
        bool okB = hostB.AsSpan().SequenceEqual(devB);
        allOk &= rc == 0 && okA && okB;

        Console.WriteLine($"  {(salted == 1 ? "V3 salted" : "V2 legacy")}  (rc={rc})");
        Console.WriteLine($"    a_seed  host {Convert.ToHexString(hostA)}");
        Console.WriteLine($"    a_seed  gpu  {Convert.ToHexString(devA)}   {(okA ? "MATCH" : "*** MISMATCH ***")}");
        Console.WriteLine($"    b_seed  host {Convert.ToHexString(hostB)}");
        Console.WriteLine($"    b_seed  gpu  {Convert.ToHexString(devB)}   {(okB ? "MATCH" : "*** MISMATCH ***")}");
    }

    // A pair that agreed but never changed would pass the check above and still
    // be wrong: it would mean the salted flag reached neither side.
    var (lb, la) = Akoya.Crypto.CommitmentHasher.DeriveNoiseSeeds(jobKey, aRoot, bRoot, m, n, false);
    var (sb2, sa) = Akoya.Crypto.CommitmentHasher.DeriveNoiseSeeds(jobKey, aRoot, bRoot, m, n, true);
    bool differ = !la.AsSpan().SequenceEqual(sa) && !lb.AsSpan().SequenceEqual(sb2);
    Console.WriteLine();
    Console.WriteLine($"  V2 and V3 differ: {(differ ? "yes" : "*** NO — the fork switch is doing nothing ***")}");
    allOk &= differ;

    Console.WriteLine();
    Console.WriteLine(allOk
        ? "PASS — host and GPU agree on both sides of the fork."
        : "FAIL — do not mine across the fork with this build.");
    return allOk ? 0 : 1;
}

static int PrintVersion()
{
    Console.WriteLine($"ARC-miner v{VersionInfo.MinerVersion} (git {VersionInfo.GitSha})");
    Console.WriteLine("Multi-algo miner — Intel Arc / SYCL");
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
    Console.Error.WriteLine("  --worker | --workername | -n <name>   Set worker name");
    Console.Error.WriteLine("dual mining (--algo <gpu>+<cpu>, e.g. prl+gr) — the CPU algo mines its own coin");
    Console.Error.WriteLine("on its own pool, so --pool/--wallet above stay with the GPU algo:");
    Console.Error.WriteLine("  --pool-cpu <url>       CPU algo's pool (same URL schemes as --pool)");
    Console.Error.WriteLine("  --wallet-cpu <addr>    CPU algo's wallet address");
    Console.Error.WriteLine("  --worker-cpu <name>    CPU algo's worker name (default: same as --worker)");
    Console.Error.WriteLine("  --password-cpu <pw>    CPU algo's stratum password (default: x)");
    Console.Error.WriteLine("  --threads-cpu <n>      CPU algo thread count (default: all cores minus 2 when dual-mining)");
    Console.Error.WriteLine("options (cont.):");
    Console.Error.WriteLine("  --tls | --no-tls       Enable/disable TLS (default: TLS enabled)");
    Console.Error.WriteLine("  --tls-insecure         Enable insecure TLS connection");
    Console.Error.WriteLine("  --password | -p <pw>   Stratum password (pearl/v1 pools; e.g. \"x;d=250000\")");
    Console.Error.WriteLine("  --diff <n>             Request difficulty n via the stratum password");
    Console.Error.WriteLine("  --mpp <count>          Override pipelining MatmulsPerPoll count");
    Console.Error.WriteLine("  --budget <ms>          Override benchmark target trigger budget in ms");
    Console.Error.WriteLine("  --keepalive [sec]      Enable stratum keepalive re-auth (default off; interval 120s)");
    Console.Error.WriteLine("  --api-port <port>      Enable local HTTP stats API (JSON /api/stats, Prometheus /metrics)");
    Console.Error.WriteLine("  --dashboard [ms]       Set the live TUI dashboard refresh interval in ms (dashboard is on by default)");
    Console.Error.WriteLine("  --dash-off             Disable the TUI dashboard and use the plain scrolling log");
    Console.Error.WriteLine("  --theme <name>         Dashboard skin: "
        + string.Join(" | ", Akoya.Miner.Observability.Dashboard.ThemeNames()) + " (default: classic)");
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
        NativeLibrary.Free(NativeLibs.Load("ARC_PEARL_GEMM_LIB", NativeLibs.GemmFile));
        return Akoya.Crypto.MinerEnv.Get("ARC_PEARL_GEMM_LIB") ?? $"{NativeLibs.GemmFile} (resolved)";
    }));

    probes.Add(RunProbe("pearl_mining_lib", () =>
    {
        NativeLibrary.Free(NativeLibs.Load("ARC_PEARL_MINING_LIB", NativeLibs.MiningFile));
        return Akoya.Crypto.MinerEnv.Get("ARC_PEARL_MINING_LIB") ?? $"{NativeLibs.MiningFile} (resolved)";
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
    var levelEnv = Akoya.Crypto.MinerEnv.Get("ARC_LOG_LEVEL") ?? "Information";
    if (!Enum.TryParse<LogLevel>(levelEnv, ignoreCase: true, out var level))
        level = LogLevel.Information;
    var json = (Akoya.Crypto.MinerEnv.Get("ARC_LOG_JSON") ?? "0") is "1" or "true";

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
    /// 1. ARC_DUMP_DIR (legacy ARC_DUMP_DIR honoured)
    /// 2. $ARC_HOME/dumps (legacy ARC_HOME honoured)
    /// 3. $HOME/.arc-miner/dumps
    /// </summary>
    public static string ResolveDumpDir()
    {
        var d = Akoya.Crypto.MinerEnv.Get("ARC_DUMP_DIR");
        if (!string.IsNullOrEmpty(d)) return d;
        var home = Akoya.Crypto.MinerEnv.Get("ARC_HOME");
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
//   3. extracted temporary folder (embedded resources).
//   4. the OS loader (LD_LIBRARY_PATH / system paths) as a last resort.
internal static class NativeLibs
{
    // Platform-specific filenames for the two P/Invoke libraries the build
    // stages next to the binary: lib*.so on Linux, *.dll on Windows.
    public static string GemmFile =>
        OperatingSystem.IsWindows() ? "pearl_gemm_capi.dll" : "libpearl_gemm_capi.so";
    public static string MiningFile =>
        OperatingSystem.IsWindows() ? "pearl_mining_capi.dll" : "libpearl_mining_capi.so";

    private static string? _extractedPath;
    // volatile + published LAST: see the ordering note in EnsureExtracted. A
    // concurrent caller that observes this true must be able to trust that
    // _extractedPath is final.
    private static volatile bool _extracted;
    private static readonly object _extractLock = new();

    public static string? ExtractedPath
    {
        get
        {
            EnsureExtracted();
            return _extractedPath;
        }
    }

    /// <summary>Every per-algo native library the executing assembly's
    /// DllImport resolver claims.</summary>
    /// <remarks>
    /// A name missing from here never reaches <see cref="Load"/>, so it gets no
    /// ARC_*_LIB override, no lookup in the extracted-resource directory, and no
    /// <see cref="PreloadDependencies"/>. .NET's default probing takes over and
    /// a single-file build reports the library as missing wherever the file
    /// actually is — next to the exe AND in the extract folder both fail. That
    /// reads like a packaging problem rather than one absent string, which is
    /// exactly how sha3t_capi cost an afternoon on 2026-08-15.
    /// Adding an algo means adding its library here.
    /// </remarks>
    public static readonly string[] AlgoCapiLibs = new[]
    {
        "csd_capi",
        "sha3t_capi",
        "randomx_capi",
        "ghostrider_capi",
        "neuromorph_capi",
    };

    private static readonly string[] PreloadList = new[]
    {
        "libmmd.dll",
        "OpenCL.dll",
        "ur_win_proxy_loader.dll",
        "ur_loader.dll",
        "ur_adapter_opencl.dll"
    };

    private static bool _dependenciesPreloaded;

    public static void PreloadDependencies()
    {
        if (_dependenciesPreloaded) return;
        lock (_extractLock)
        {
            if (_dependenciesPreloaded) return;
            _dependenciesPreloaded = true;

            if (!OperatingSystem.IsWindows() || _extractedPath == null) return;

            foreach (var libName in PreloadList)
            {
                var path = Path.Combine(_extractedPath, libName);
                if (File.Exists(path))
                {
                    try { NativeLibrary.Load(path); } catch { }
                }
            }

            // Load sycl*.dll
            try
            {
                var syclDlls = Directory.GetFiles(_extractedPath, "sycl*.dll");
                foreach (var path in syclDlls)
                {
                    var fileName = Path.GetFileName(path);
                    if (fileName.StartsWith("sycl", StringComparison.OrdinalIgnoreCase) &&
                        char.IsDigit(fileName.Replace("sycl", "").FirstOrDefault()))
                    {
                        try { NativeLibrary.Load(path); break; } catch { }
                    }
                }
            }
            catch { }
        }
    }

    /// <summary>Short, stable-per-build id for the binary carrying the embedded
    /// libs, used to scope the extraction cache. Derived from that file's size
    /// and last-write time rather than its contents: both change on every build,
    /// and unlike hashing a 30 MB self-contained binary it costs nothing at
    /// startup.
    ///
    /// Prefers the assembly's own path over <see cref="Environment.ProcessPath"/>:
    /// under AOT they are the same file, but on a framework-dependent run
    /// ProcessPath is dotnet.exe — whose timestamp never changes, which would
    /// silently restore the one-shared-directory bug this is here to fix.
    /// Falls back to a constant only when neither path is available.</summary>
    private static string BuildFingerprint(Assembly assembly)
    {
        try
        {
            string? exe = null;
#pragma warning disable IL3000 // Assembly.Location returns empty string for single-file binaries; fallback to ProcessPath handles it
            try { exe = assembly.Location; } catch { }
#pragma warning restore IL3000
            if (string.IsNullOrEmpty(exe)) exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return "nopath";
            var fi = new FileInfo(exe);
            ulong h = 1469598103934665603UL;               // FNV-1a 64
            foreach (var b in BitConverter.GetBytes(fi.Length)) { h ^= b; h *= 1099511628211UL; }
            foreach (var b in BitConverter.GetBytes(fi.LastWriteTimeUtc.Ticks)) { h ^= b; h *= 1099511628211UL; }
            return h.ToString("x16", System.Globalization.CultureInfo.InvariantCulture)[..12];
        }
        catch
        {
            return "nopath";
        }
    }

    private static void EnsureExtracted()
    {
        if (_extracted) return;
        lock (_extractLock)
        {
            if (_extracted) return;
            try { ExtractCore(); }
            finally
            {
                // Publish ONLY here, once _extractedPath is final. This used to
                // be set at the TOP of the critical section, which made dual
                // mining fail: both algos load their native libs concurrently,
                // and the second one hit the `if (_extracted) return` fast path
                // while the first was still extracting. It returned without ever
                // taking the lock, read _extractedPath as null, skipped the
                // extracted-directory probe and reported
                // "<algo>_capi not found next to the miner binary".
                //
                // That is why `btx+rx`, `csd+rx`, `btx+gr`, `btx+nm` … all failed
                // to load the CPU library on Linux while `prl+rx` happened to
                // serialise differently and worked. Reproduced 5/5 before the
                // fix. In a `finally` so a failed extraction does not retry
                // forever on every subsequent resolve.
                _extracted = true;
            }
        }
    }

    private static void ExtractCore()
    {
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resources = assembly.GetManifestResourceNames();
            var prefix = "Akoya.Miner.EmbeddedLibs.";

            var hasEmbedded = resources.Any(r => r.StartsWith(prefix));
            if (!hasEmbedded) return;

            var gitSha = "unknown";
            try { gitSha = VersionInfo.GitSha; } catch { }

            // The cache directory MUST change whenever the embedded payload does,
            // or a rebuild silently reuses the previous build's DLLs and the miner
            // dies later with DllNotFound / EntryPointNotFound against a lib that
            // looks present. Keying on the git sha alone is not enough: a working
            // copy with no git repo reports "unknown" forever (the csproj's
            // _EmbedGitSha target falls back to it), so EVERY build shared one
            // directory. Mix in this executable's own identity — size and
            // timestamp both change on every publish.
            var baseTempDir = Path.Combine(Path.GetTempPath(), $"arc_miner_{gitSha}_{BuildFingerprint(assembly)}");
            bool success = true;

            try
            {
                Directory.CreateDirectory(baseTempDir);
                foreach (var res in resources)
                {
                    if (!res.StartsWith(prefix)) continue;
                    var fileName = res.Substring(prefix.Length);
                    var targetPath = Path.Combine(baseTempDir, fileName);

                    if (File.Exists(targetPath))
                    {
                        try
                        {
                            using (var stream = assembly.GetManifestResourceStream(res))
                            using (var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                            {
                                stream?.CopyTo(fileStream);
                            }
                        }
                        catch (IOException)
                        {
                            // Locked, which normally means another instance of THIS
                            // build is running and the file on disk is already the
                            // right bytes. Verify that rather than assuming it:
                            // accepting a stale lib here is how a rebuilt native
                            // library turns into an EntryPointNotFoundException at
                            // the first P/Invoke. On a mismatch, fail out to the
                            // fresh-directory fallback below.
                            using var expected = assembly.GetManifestResourceStream(res);
                            if (expected is not null && new FileInfo(targetPath).Length != expected.Length)
                                throw;
                        }
                    }
                    else
                    {
                        using (var stream = assembly.GetManifestResourceStream(res))
                        using (var fileStream = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                        {
                            stream?.CopyTo(fileStream);
                        }
                    }
                }
            }
            catch
            {
                success = false;
            }

            if (!success)
            {
                baseTempDir = Path.Combine(Path.GetTempPath(), $"arc_miner_{Guid.NewGuid():N}");
                try
                {
                    Directory.CreateDirectory(baseTempDir);
                    foreach (var res in resources)
                    {
                        if (!res.StartsWith(prefix)) continue;
                        var fileName = res.Substring(prefix.Length);
                        var targetPath = Path.Combine(baseTempDir, fileName);

                        using (var stream = assembly.GetManifestResourceStream(res))
                        using (var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                        {
                            stream?.CopyTo(fileStream);
                        }
                    }
                }
                catch { }
            }

            _extractedPath = baseTempDir;
        }
    }

    /// <summary>Filenames to try, in order, for a P/Invoke library name.
    ///
    /// The P/Invoke names are unprefixed ("randomx_capi"), but the Linux build
    /// scripts emit the platform convention — <c>librandomx_capi.so</c>. Probing
    /// only the unprefixed spelling made every *_capi library unresolvable on
    /// Linux: the file sat right next to the binary, loaded fine under a manual
    /// dlopen, and the miner still reported "randomx_capi not found ... this
    /// build has no RandomX backend". dlopen does not add the prefix for us, and
    /// because this resolver throws rather than returning 0, .NET's own probing
    /// (which does try lib-prefixed names) never got a turn.</summary>
    private static string[] CandidateFileNames(string fileName) =>
        OperatingSystem.IsWindows() || fileName.StartsWith("lib", StringComparison.Ordinal)
            ? new[] { fileName }
            : new[] { fileName, "lib" + fileName };

    public static nint Load(string envVar, string fileName)
    {
        var p = Akoya.Crypto.MinerEnv.Get(envVar);
        if (!string.IsNullOrEmpty(p)) return NativeLibrary.Load(p);

        var candidates = CandidateFileNames(fileName);

        foreach (var candidate in candidates)
        {
            var local = Path.Combine(AppContext.BaseDirectory, candidate);
            if (File.Exists(local)) return NativeLibrary.Load(local);
        }

        var extPath = ExtractedPath;
        if (extPath != null)
        {
            foreach (var candidate in candidates)
            {
                var extractedFile = Path.Combine(extPath, candidate);
                if (File.Exists(extractedFile))
                {
                    PreloadDependencies();
                    return NativeLibrary.Load(extractedFile);
                }
            }
        }

        // Nothing staged next to us: let the OS loader search its own paths for
        // each spelling. Report the failure against the name the caller asked
        // for, not the last variant tried.
        for (int i = 0; i < candidates.Length; i++)
        {
            try { return NativeLibrary.Load(candidates[i]); }
            catch (DllNotFoundException) when (i < candidates.Length - 1) { }
        }
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
