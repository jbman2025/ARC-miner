// Command-line parsing, lifted out of Program.cs's top-level statements.
//
// The miner's flags are a thin sugar layer over environment variables: every
// option's real home is an ARC_* variable, and the flag just sets it. That is
// deliberate (Hive/launcher integrations set the env directly), but it meant
// the parser could only be exercised by launching the process, so nothing here
// was ever tested — and it had quietly accumulated unreachable branches and a
// flag that only worked for one of the three CPU algos.
//
// Parse() is pure: it returns the settings rather than applying them, so the
// whole table can be unit-tested without mutating process-global state.
// Apply() is the only part that touches the environment.

namespace Akoya.Miner.Config;

internal sealed record CommandLineResult(
    string Subcommand,
    IReadOnlyList<KeyValuePair<string, string>> EnvVars)
{
    /// <summary>Value the parse would set for <paramref name="key"/>, or null.
    /// Last write wins, matching Apply's ordering.</summary>
    public string? Get(string key)
    {
        string? found = null;
        foreach (var kv in EnvVars)
        {
            if (string.Equals(kv.Key, key, StringComparison.Ordinal)) found = kv.Value;
        }
        return found;
    }
}

internal static class CommandLine
{
    private static readonly string[] Subcommands =
        { "mine-blocks", "selftest", "--selftest", "version", "--version", "-V" };

    public static CommandLineResult Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var env = new List<KeyValuePair<string, string>>();
        void Set(string key, string value) => env.Add(new(key, value));

        // A bare leading word that isn't a flag or a known subcommand is taken
        // as the subcommand (e.g. "arc-miner autotune"), and no flags are parsed.
        if (args.Length > 0 && !args[0].StartsWith('-') && !Subcommands.Contains(args[0], StringComparer.Ordinal))
        {
            return new CommandLineResult(args[0], env);
        }

        var subcommand = "mine-blocks";

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            // Does this flag have a value following it?
            bool HasValue() => i + 1 < args.Length;
            string Value() => args[++i];

            if (Subcommands.Contains(arg, StringComparer.Ordinal))
            {
                subcommand = arg;
            }
            else if (arg is "--help" or "-h" or "help")
            {
                subcommand = "help";
            }
            // ── GPU-side / shared pool ─────────────────────────────────────────
            else if (arg == "--pool" && HasValue())
            {
                var (host, port, isStratum, tls) = PoolUrl.Parse(Value());
                if (isStratum) Set("ARC_POOL_STRATUM", "true");
                if (tls.HasValue) Set("ARC_POOL_TLS", tls.Value ? "true" : "false");
                Set("ARC_POOL_HOST", host);
                if (!string.IsNullOrEmpty(port)) Set("ARC_POOL_PORT", port);
            }
            else if ((arg is "--wallet" or "-w") && HasValue()) Set("ARC_POOL_WALLET", Value());
            else if ((arg is "--worker" or "--workername" or "-n") && HasValue()) Set("ARC_POOL_WORKER", Value());
            // ── CPU-side pool (dual mining) ────────────────────────────────────
            // When dual-mining (prl+gr, csd+rx, ...) the two halves mine different
            // coins on different pools. --pool/--wallet stay with the GPU algo;
            // these name the CPU algo's pool. They set generic ARC_POOL_CPU_*
            // variables that ANY CPU algo reads (see CpuAlgoConfig), so the same
            // flags work for rx, gr and nm alike.
            else if ((arg is "--pool-cpu" or "--cpu-pool") && HasValue())
            {
                var (host, port, isStratum, tls) = PoolUrl.Parse(Value());
                if (isStratum) Set("ARC_POOL_CPU_STRATUM", "true");
                if (tls.HasValue) Set("ARC_POOL_CPU_TLS", tls.Value ? "true" : "false");
                Set("ARC_POOL_CPU_HOST", host);
                if (!string.IsNullOrEmpty(port)) Set("ARC_POOL_CPU_PORT", port);
            }
            else if ((arg is "--wallet-cpu" or "--cpu-wallet") && HasValue()) Set("ARC_POOL_CPU_WALLET", Value());
            else if ((arg is "--worker-cpu" or "--cpu-worker") && HasValue()) Set("ARC_POOL_CPU_WORKER", Value());
            else if ((arg is "--password-cpu" or "--cpu-password") && HasValue()) Set("ARC_POOL_CPU_PASSWORD", Value());
            // Generic, NOT ARC_RX_THREADS. Setting only the rx variable meant
            // --threads-cpu was silently ignored by gr and nm, even though both
            // log "override with --threads-cpu" when they auto-reserve cores.
            else if (arg == "--threads-cpu" && HasValue()) Set("ARC_POOL_CPU_THREADS", Value());
            // ── TLS ────────────────────────────────────────────────────────────
            else if (arg == "--tls") Set("ARC_POOL_TLS", "true");
            else if (arg == "--no-tls") Set("ARC_POOL_TLS", "false");
            else if (arg == "--tls-insecure") Set("ARC_POOL_TLS_INSECURE", "true");
            // ── stratum ────────────────────────────────────────────────────────
            else if ((arg is "--password" or "-p") && HasValue())
            {
                // Stratum password for challenge-first (pearl/v1) pools. Carries
                // the difficulty request, e.g. "x;d=250000".
                Set("ARC_STRATUM_PASSWORD", Value());
            }
            else if (arg == "--diff" && HasValue())
            {
                // Appends ";d=<n>" to the stratum password if not already present.
                Set("ARC_STRATUM_DIFF", Value());
            }
            else if (arg == "--keepalive")
            {
                // Optional interval in seconds: "--keepalive 90"; bare
                // "--keepalive" uses the 120s default.
                var sec = "120";
                if (HasValue() && int.TryParse(args[i + 1], out _)) sec = Value();
                Set("ARC_STRATUM_KEEPALIVE_SEC", sec);
            }
            // ── misc ───────────────────────────────────────────────────────────
            else if (arg == "--mpp" && HasValue()) Set("ARC_MINE_MPP_OVERRIDE", Value());
            else if (arg == "--budget" && HasValue()) Set("ARC_BENCHMARK_BUDGET_MS", Value());
            else if (arg == "--api-port" && HasValue())
            {
                // Local HTTP stats API (JSON at /api/stats, Prometheus at
                // /metrics). Same listener ARC_METRICS_PORT configures; the flag
                // exists so bundling launchers (e.g. Kryptex) can enable it
                // per-invocation.
                Set("ARC_METRICS_PORT", Value());
            }
            else if (arg == "--api-password" && HasValue())
            {
                // Enables the control API (change pool/wallet/worker/algo).
                // Without it /api/control/config is disabled and the stats API
                // is read-only. Control is additionally localhost-only.
                Set("ARC_API_PASSWORD", Value());
            }
            else if (arg == "--no-autotune")
            {
                // Skip the one-time first-run autotune sweep.
                Set("ARC_AUTOTUNE_ON_FIRST_RUN", "0");
            }
            else if (arg == "--igpu")
            {
                // Allow mining on integrated GPUs. Off by default — an iGPU is
                // far slower than a discrete card.
                Set("ARC_IGPU", "1");
            }
            else if (arg == "--dashboard")
            {
                // The live in-place TUI dashboard is on by default; the flag is
                // kept so it can be re-enabled after an earlier --dash-off, and
                // to set the refresh interval in milliseconds: "--dashboard 500".
                // Ignored when stdout is redirected or JSON logging is on.
                Set("ARC_DASHBOARD", "1");
                if (HasValue() && int.TryParse(args[i + 1], out _)) Set("ARC_DASHBOARD_REFRESH_MS", Value());
            }
            else if (arg == "--dash-off")
            {
                // Opt out of the TUI dashboard and keep the plain scrolling log.
                Set("ARC_DASHBOARD", "0");
            }
            else if (arg == "--theme" && HasValue())
            {
                // Dashboard skin ("classic", "rogue"). Unknown names fall back to
                // classic at render time rather than erroring — a cosmetic
                // setting must never stop a rig from mining.
                Set("ARC_THEME", Value());
            }
            else if (arg == "--algo" && HasValue())
            {
                // Mining algorithm module (default "prl"). Unknown names error at
                // startup listing the registered algos. See Algos/AlgoRegistry.cs.
                Set("ARC_ALGO", Value());
            }
        }

        return new CommandLineResult(subcommand, env);
    }

    /// <summary>Push a parse result into the process environment. Split from
    /// <see cref="Parse"/> so the flag table can be tested without side effects.</summary>
    public static void Apply(CommandLineResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        foreach (var kv in result.EnvVars)
        {
            Environment.SetEnvironmentVariable(kv.Key, kv.Value);
        }
    }
}
