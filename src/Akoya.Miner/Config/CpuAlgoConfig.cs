// The config-precedence chain shared by every CPU algo (rx, gr, nm).
//
// All three used to hand-roll this, and it had ALREADY drifted: rx was missing
// the dual-mining guard entirely, so `rx+prl` without --pool-cpu silently
// pointed RandomX at the Pearl pool with a Pearl address. That is exactly the
// bug class this type exists to make impossible — there is now one chain, and
// adding a fourth CPU algo cannot reintroduce it.
//
// Precedence, highest first:
//   1. ARC_<PREFIX>_*   — algo-specific (ARC_RX_POOL, ARC_GR_ADDRESS, …)
//   2. ARC_POOL_CPU_*   — the CPU side of a dual pair (--pool-cpu/--wallet-cpu)
//   3. ARC_POOL_*       — the shared --pool/--wallet
//
// Step 3 applies ONLY to single-algo runs. When DualMiningAlgo has paired the
// CPU algo with a GPU algo (ARC_<PREFIX>_DUAL=1), the shared flags belong to the
// GPU side, and inheriting them means mining the wrong coin to the wrong
// address. In that case the CPU algo must be told its own pool explicitly.

namespace Akoya.Miner.Config;

/// <summary>Everything the CPU algos configure identically. Algo-specific knobs
/// (rx's light mode, nm's keepalive tuning, …) stay in the algo's own record and
/// compose with this one.</summary>
internal sealed record CpuAlgoConfig(
    int Threads,
    string Worker,
    string? PoolUrl,
    string? Address,
    string Password,
    bool UseTls,
    bool Affinity,
    int KeepaliveSec,
    double PollSec,
    bool IsDual,
    bool StratumHint,
    bool ThreadsExplicit)
{
    /// <summary>True once both a pool/node and a wallet are known.</summary>
    public bool CanMine => PoolUrl is not null && Address is not null;

    /// <summary>Stratum pool (vs a solo daemon's JSON-RPC). The URL scheme is
    /// authoritative when it has one; otherwise we fall back to the hint
    /// recorded from whichever flag supplied the pool.
    ///
    /// The hint matters because --pool/--pool-cpu STRIP the scheme before
    /// storing host and port, so by the time we get here `stratum+tls://x:8029`
    /// looks exactly like a bare `x:8029`. Consulting only the shared
    /// ARC_POOL_STRATUM (as all three algos used to) meant `--pool-cpu` on its
    /// own lost the signal entirely and the CPU algo tried to solo-mine against
    /// a stratum pool over HTTP.</summary>
    public bool IsStratumPool =>
        PoolUrl is not null && (CpuAlgoConfigLoader.SchemeSaysPool(PoolUrl) ?? StratumHint);
}

internal static class CpuAlgoConfigLoader
{
    /// <summary>Sentinel written by Program.cs for --algo values that take no
    /// wallet; it must never be treated as a real address.</summary>
    private const string WalletSentinel = "unused-non-prl-algo";

    private static string? FromEnvironment(string name) => Environment.GetEnvironmentVariable(name);

    /// <summary>Blank-is-unset + trim, applied to whatever lookup is in use so an
    /// injected one goes through exactly the same normalisation as the real
    /// environment. Program.cs sets some of these unconditionally, so "  " must
    /// not become a pool host.</summary>
    private static Func<string, string?> Normalize(Func<string, string?> lookup) =>
        name =>
        {
            var v = lookup(name);
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        };

    /// <param name="prefix">Algo prefix without decoration — "RX", "GR", "NM".</param>
    /// <param name="defaultKeepaliveSec">Algo's stratum keepalive default.</param>
    /// <param name="defaultPollSec">Algo's job-poll default (solo paths).</param>
    /// <param name="defaultDualReserve">Logical CPUs held back for the GPU host
    /// loop when dual-mining and no explicit thread count was given.</param>
    /// <param name="env">Environment lookup. Injectable so the precedence rules
    /// can be unit-tested without mutating process-global state.</param>
    public static CpuAlgoConfig Load(
        string prefix,
        int defaultKeepaliveSec = 30,
        double defaultPollSec = 2.0,
        int defaultDualReserve = 2,
        Func<string, string?>? env = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var e = Normalize(env ?? FromEnvironment);
        string P(string suffix) => $"ARC_{prefix}_{suffix}";

        bool isDual = e(P("DUAL")) == "1";

        // Threads: an explicit thread count always wins, algo-specific first
        // (ARC_<P>_THREADS) then the generic CPU-side one that --threads-cpu
        // sets. Otherwise every logical CPU, less the dual-mining reserve so the
        // GPU host loop isn't starved.
        //
        // The generic fallback matters: --threads-cpu used to write only
        // ARC_RX_THREADS, so it was silently ignored by gr and nm even though
        // both name that flag in their auto-reserve log line.
        int threads;
        bool threadsExplicit = (int.TryParse(e(P("THREADS")), out var t) && t > 0) ||
                               (int.TryParse(e("ARC_POOL_CPU_THREADS"), out t) && t > 0);
        if (threadsExplicit)
        {
            threads = t;
        }
        else
        {
            threads = Math.Max(1, Environment.ProcessorCount);
            if (isDual)
            {
                int reserve = int.TryParse(e(P("DUAL_RESERVE")), out var r) && r >= 0 ? r : defaultDualReserve;
                threads = Math.Max(1, threads - reserve);
            }
        }

        // Wallet. The shared --wallet is inherited only outside a dual pair.
        var address = e(P("ADDRESS")) ?? e("ARC_POOL_CPU_WALLET");
        if (address is null && !isDual)
        {
            var shared = e("ARC_POOL_WALLET");
            if (shared is not null && shared != WalletSentinel) address = shared;
        }

        // Pool/node. ARC_<P>_POOL and ARC_<P>_NODE are two spellings of the same
        // setting (the algos disagreed on which they checked first; they are
        // unified here — setting both to different values was never meaningful).
        //
        // Track WHICH flag supplied the pool, because --pool/--pool-cpu strip the
        // scheme and record "was it stratum?" in a separate variable. The hint has
        // to come from the same source as the URL: reading the shared
        // ARC_POOL_STRATUM for a pool that came from --pool-cpu is how this got
        // broken in the first place.
        bool stratumHint = false;
        var pool = e(P("POOL")) ?? e(P("NODE"));
        if (pool is null)
        {
            var cpuHost = e("ARC_POOL_CPU_HOST");
            var cpuPort = e("ARC_POOL_CPU_PORT");
            if (cpuHost is not null)
            {
                pool = cpuPort is not null ? $"{cpuHost}:{cpuPort}" : cpuHost;
                stratumHint = e("ARC_POOL_CPU_STRATUM") == "true";
            }
        }
        if (pool is null && !isDual)
        {
            var host = e("ARC_POOL_HOST");
            var port = e("ARC_POOL_PORT");
            if (host is not null && port is not null)
            {
                pool = $"{host}:{port}";
                stratumHint = e("ARC_POOL_STRATUM") == "true";
            }
        }

        // TLS. A tls/ssl scheme on the URL always counts; otherwise an explicit
        // algo- or CPU-side setting wins, and the shared ARC_POOL_TLS applies
        // only to single-algo runs (in a pair it describes the GPU pool, which
        // may well differ).
        bool tls = SchemeImpliesTls(pool)
                   || e(P("TLS")) == "true"
                   || e("ARC_POOL_CPU_TLS") == "true"
                   || (!isDual && e("ARC_POOL_TLS") == "true");

        return new CpuAlgoConfig(
            Threads: threads,
            Worker: e(P("WORKER")) ?? e("ARC_POOL_CPU_WORKER") ?? e("ARC_POOL_WORKER") ?? Environment.MachineName,
            PoolUrl: pool,
            Address: address,
            Password: e(P("PASSWORD")) ?? e("ARC_POOL_CPU_PASSWORD") ?? e("ARC_STRATUM_PASSWORD") ?? "x",
            UseTls: tls,
            Affinity: e(P("AFFINITY")) == "1",
            KeepaliveSec: int.TryParse(e("ARC_STRATUM_KEEPALIVE_SEC"), out var k) && k > 0 ? k : defaultKeepaliveSec,
            PollSec: double.TryParse(e(P("POLL_SEC")), out var p) && p > 0 ? p : defaultPollSec,
            IsDual: isDual,
            StratumHint: stratumHint,
            ThreadsExplicit: threadsExplicit);
    }

    private static bool SchemeImpliesTls(string? url) =>
        url is not null &&
        (url.StartsWith("stratum+tls://", StringComparison.OrdinalIgnoreCase) ||
         url.StartsWith("stratum+ssl://", StringComparison.OrdinalIgnoreCase) ||
         url.StartsWith("ssl://", StringComparison.OrdinalIgnoreCase) ||
         url.StartsWith("tls://", StringComparison.OrdinalIgnoreCase));

    /// <summary>What the URL's scheme says about pool-vs-solo: true for a
    /// stratum scheme, false for http(s) (a daemon's JSON-RPC), null when the
    /// URL carries no scheme and the caller must fall back to the hint.</summary>
    public static bool? SchemeSaysPool(string url)
    {
        if (url.StartsWith("stratum+", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("stratum://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("ssl://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("tls://", StringComparison.OrdinalIgnoreCase))
            return true;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return false;
        return null;
    }

    /// <summary>The "why am I benchmarking?" message. Dual pairs get told to use
    /// the CPU-side flags, because --pool/--wallet drive the GPU algo there.</summary>
    public static string DescribeWhyNotMining(CpuAlgoConfig cfg, string algo, string walletHint)
    {
        if (cfg.IsDual)
            return $"{algo}: dual-mining needs its own pool and wallet — pass --pool-cpu <url> and --wallet-cpu <{walletHint}> (--pool/--wallet belong to the GPU algo). Running benchmark instead.";
        if (cfg.PoolUrl is null && cfg.Address is null)
            return $"{algo}: no pool/address configured — running BENCHMARK. To mine, pass --pool <url> and --wallet <{walletHint}>.";
        if (cfg.Address is null)
            return $"{algo}: pool set but no wallet address — pass --wallet <{walletHint}>. Running benchmark instead.";
        return $"{algo}: address set but no pool — pass --pool <url>. Running benchmark instead.";
    }
}
