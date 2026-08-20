using Akoya.Miner.Config;
using Xunit;

namespace Akoya.Miner.Tests;

// The precedence chain that rx, gr and nm used to hand-roll three times over.
// The drift it had already accumulated — rx missing the dual-mining guard — let
// `rx+prl` silently mine RandomX to the Pearl pool. These tests pin the rules
// down so a fourth CPU algo cannot reintroduce it.
//
// Env lookup is injected rather than set on the process, so nothing here
// mutates global state or depends on the developer's shell.
public class CpuAlgoConfigTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return name => map.TryGetValue(name, out var v) ? v : null;
    }

    private static CpuAlgoConfig Load(params (string Key, string Value)[] pairs) =>
        CpuAlgoConfigLoader.Load("RX", env: Env(pairs));

    // ── precedence ───────────────────────────────────────────────────────────

    [Fact]
    public void AlgoSpecificSettingsWinOverEverything()
    {
        var cfg = Load(
            ("ARC_RX_POOL", "algo.pool:1111"), ("ARC_RX_ADDRESS", "algo-wallet"),
            ("ARC_POOL_CPU_HOST", "cpu.pool"), ("ARC_POOL_CPU_PORT", "2222"),
            ("ARC_POOL_CPU_WALLET", "cpu-wallet"),
            ("ARC_POOL_HOST", "shared.pool"), ("ARC_POOL_PORT", "3333"),
            ("ARC_POOL_WALLET", "shared-wallet"));

        Assert.Equal("algo.pool:1111", cfg.PoolUrl);
        Assert.Equal("algo-wallet", cfg.Address);
    }

    [Fact]
    public void CpuSideSettingsWinOverTheSharedOnes()
    {
        var cfg = Load(
            ("ARC_POOL_CPU_HOST", "cpu.pool"), ("ARC_POOL_CPU_PORT", "2222"),
            ("ARC_POOL_CPU_WALLET", "cpu-wallet"),
            ("ARC_POOL_HOST", "shared.pool"), ("ARC_POOL_PORT", "3333"),
            ("ARC_POOL_WALLET", "shared-wallet"));

        Assert.Equal("cpu.pool:2222", cfg.PoolUrl);
        Assert.Equal("cpu-wallet", cfg.Address);
    }

    [Fact]
    public void SharedSettingsAreUsedForASingleAlgoRun()
    {
        var cfg = Load(("ARC_POOL_HOST", "shared.pool"), ("ARC_POOL_PORT", "3333"),
                       ("ARC_POOL_WALLET", "shared-wallet"));

        Assert.False(cfg.IsDual);
        Assert.Equal("shared.pool:3333", cfg.PoolUrl);
        Assert.Equal("shared-wallet", cfg.Address);
        Assert.True(cfg.CanMine);
    }

    [Fact]
    public void CpuSidePortIsOptional()
    {
        var cfg = Load(("ARC_POOL_CPU_HOST", "cpu.pool"));
        Assert.Equal("cpu.pool", cfg.PoolUrl);
    }

    [Fact]
    public void SharedHostWithoutPortIsIgnored()
    {
        // The shared pair is only meaningful together — a lone host would build
        // a portless URL that the algo's own default port cannot be applied to.
        var cfg = Load(("ARC_POOL_HOST", "shared.pool"));
        Assert.Null(cfg.PoolUrl);
    }

    // ── the dual-mining guard (this is item 1's root cause) ──────────────────

    [Fact]
    public void DualMiningDoesNotInheritTheSharedPoolOrWallet()
    {
        var cfg = Load(
            ("ARC_RX_DUAL", "1"),
            ("ARC_POOL_HOST", "pearl.pool"), ("ARC_POOL_PORT", "3333"),
            ("ARC_POOL_WALLET", "pearl-wallet"));

        Assert.True(cfg.IsDual);
        Assert.Null(cfg.PoolUrl);
        Assert.Null(cfg.Address);
        Assert.False(cfg.CanMine);
    }

    [Fact]
    public void DualMiningStillHonoursTheCpuSideFlags()
    {
        var cfg = Load(
            ("ARC_RX_DUAL", "1"),
            ("ARC_POOL_CPU_HOST", "monero.pool"), ("ARC_POOL_CPU_PORT", "3333"),
            ("ARC_POOL_CPU_WALLET", "xmr-wallet"),
            ("ARC_POOL_HOST", "pearl.pool"), ("ARC_POOL_PORT", "9999"),
            ("ARC_POOL_WALLET", "pearl-wallet"));

        Assert.Equal("monero.pool:3333", cfg.PoolUrl);
        Assert.Equal("xmr-wallet", cfg.Address);
        Assert.True(cfg.CanMine);
    }

    [Fact]
    public void DualMiningStillHonoursAlgoSpecificFlags()
    {
        var cfg = Load(
            ("ARC_RX_DUAL", "1"),
            ("ARC_RX_POOL", "monero.pool:3333"), ("ARC_RX_ADDRESS", "xmr-wallet"),
            ("ARC_POOL_HOST", "pearl.pool"), ("ARC_POOL_PORT", "9999"));

        Assert.Equal("monero.pool:3333", cfg.PoolUrl);
        Assert.Equal("xmr-wallet", cfg.Address);
    }

    [Fact]
    public void EveryPrefixGetsTheSameDualGuard()
    {
        // The whole point of the shared loader: gr had the guard, rx did not.
        foreach (var prefix in new[] { "RX", "GR", "NM" })
        {
            var cfg = CpuAlgoConfigLoader.Load(prefix, env: Env(
                ($"ARC_{prefix}_DUAL", "1"),
                ("ARC_POOL_HOST", "gpu.pool"), ("ARC_POOL_PORT", "3333"),
                ("ARC_POOL_WALLET", "gpu-wallet")));

            Assert.Null(cfg.PoolUrl);
            Assert.Null(cfg.Address);
        }
    }

    // ── wallet sentinel ──────────────────────────────────────────────────────

    [Fact]
    public void TheNonPrlWalletSentinelIsNotTreatedAsAnAddress()
    {
        var cfg = Load(("ARC_POOL_WALLET", "unused-non-prl-algo"),
                       ("ARC_POOL_HOST", "p"), ("ARC_POOL_PORT", "1"));
        Assert.Null(cfg.Address);
        Assert.False(cfg.CanMine);
    }

    // ── TLS ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("stratum+tls://a.pool:3333")]
    [InlineData("stratum+ssl://a.pool:3333")]
    [InlineData("ssl://a.pool:3333")]
    [InlineData("tls://a.pool:3333")]
    public void ATlsSchemeOnTheUrlAlwaysImpliesTls(string url)
    {
        Assert.True(Load(("ARC_RX_POOL", url)).UseTls);
    }

    [Fact]
    public void PlainStratumSchemeDoesNotImplyTls()
    {
        Assert.False(Load(("ARC_RX_POOL", "stratum+tcp://a.pool:3333")).UseTls);
    }

    [Fact]
    public void AlgoAndCpuSideTlsFlagsAreHonoured()
    {
        Assert.True(Load(("ARC_RX_TLS", "true")).UseTls);
        Assert.True(Load(("ARC_POOL_CPU_TLS", "true")).UseTls);
    }

    [Fact]
    public void SharedTlsAppliesToSingleAlgoRunsOnly()
    {
        // In a dual pair ARC_POOL_TLS describes the GPU pool, which may well
        // differ — inheriting it wraps the CPU pool socket in TLS it doesn't speak.
        Assert.True(Load(("ARC_POOL_TLS", "true")).UseTls);
        Assert.False(Load(("ARC_RX_DUAL", "1"), ("ARC_POOL_TLS", "true")).UseTls);
    }

    // ── threads ──────────────────────────────────────────────────────────────

    [Fact]
    public void ExplicitThreadCountWinsEvenWhenDualMining()
    {
        var cfg = Load(("ARC_RX_DUAL", "1"), ("ARC_RX_THREADS", "4"));
        Assert.Equal(4, cfg.Threads);
    }

    [Fact]
    public void DefaultIsEveryLogicalCpu()
    {
        Assert.Equal(Environment.ProcessorCount, Load().Threads);
        Assert.False(Load().ThreadsExplicit);
    }

    [Fact]
    public void GenericThreadsCpuAppliesToEveryCpuAlgo()
    {
        // --threads-cpu writes ARC_POOL_CPU_THREADS. It used to write only
        // ARC_RX_THREADS, so gr and nm silently ignored the flag.
        foreach (var prefix in new[] { "RX", "GR", "NM" })
        {
            var cfg = CpuAlgoConfigLoader.Load(prefix, env: Env(("ARC_POOL_CPU_THREADS", "6")));
            Assert.Equal(6, cfg.Threads);
            Assert.True(cfg.ThreadsExplicit);
        }
    }

    [Fact]
    public void AlgoSpecificThreadsBeatTheGenericOne()
    {
        var cfg = Load(("ARC_RX_THREADS", "4"), ("ARC_POOL_CPU_THREADS", "16"));
        Assert.Equal(4, cfg.Threads);
    }

    [Fact]
    public void GenericThreadsAlsoWinOverTheDualReserve()
    {
        // An explicit count opts out of the auto-reserve, whichever flag set it.
        var cfg = Load(("ARC_RX_DUAL", "1"), ("ARC_POOL_CPU_THREADS", "20"));
        Assert.Equal(20, cfg.Threads);
        Assert.True(cfg.ThreadsExplicit);
    }

    [Fact]
    public void GarbageGenericThreadsFallsBackToTheDefault()
    {
        var cfg = Load(("ARC_POOL_CPU_THREADS", "0"));
        Assert.Equal(Environment.ProcessorCount, cfg.Threads);
        Assert.False(cfg.ThreadsExplicit);
    }

    [Fact]
    public void DualMiningReservesCpusForTheGpuHostLoop()
    {
        var cfg = Load(("ARC_RX_DUAL", "1"));
        Assert.Equal(Math.Max(1, Environment.ProcessorCount - 2), cfg.Threads);
    }

    [Fact]
    public void DualReserveIsTunable()
    {
        var cfg = Load(("ARC_RX_DUAL", "1"), ("ARC_RX_DUAL_RESERVE", "4"));
        Assert.Equal(Math.Max(1, Environment.ProcessorCount - 4), cfg.Threads);
    }

    [Fact]
    public void DualReserveOfZeroIsRespected()
    {
        // 0 is meaningfully different from unset — it opts out of the reserve.
        var cfg = Load(("ARC_RX_DUAL", "1"), ("ARC_RX_DUAL_RESERVE", "0"));
        Assert.Equal(Environment.ProcessorCount, cfg.Threads);
    }

    [Fact]
    public void ThreadCountNeverDropsBelowOne()
    {
        var cfg = Load(("ARC_RX_DUAL", "1"), ("ARC_RX_DUAL_RESERVE", "9999"));
        Assert.Equal(1, cfg.Threads);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("banana")]
    public void GarbageThreadCountsFallBackToTheDefault(string value)
    {
        Assert.Equal(Environment.ProcessorCount, Load(("ARC_RX_THREADS", value)).Threads);
    }

    // ── misc scalars ─────────────────────────────────────────────────────────

    [Fact]
    public void PasswordFallsThroughToTheStratumDefaultThenX()
    {
        Assert.Equal("algo-pw", Load(("ARC_RX_PASSWORD", "algo-pw"), ("ARC_STRATUM_PASSWORD", "s")).Password);
        Assert.Equal("cpu-pw", Load(("ARC_POOL_CPU_PASSWORD", "cpu-pw"), ("ARC_STRATUM_PASSWORD", "s")).Password);
        Assert.Equal("s", Load(("ARC_STRATUM_PASSWORD", "s")).Password);
        Assert.Equal("x", Load().Password);
    }

    [Fact]
    public void WorkerDefaultsToTheMachineName()
    {
        Assert.Equal(Environment.MachineName, Load().Worker);
        Assert.Equal("w1", Load(("ARC_RX_WORKER", "w1")).Worker);
        Assert.Equal("w2", Load(("ARC_POOL_CPU_WORKER", "w2")).Worker);
        Assert.Equal("w3", Load(("ARC_POOL_WORKER", "w3")).Worker);
    }

    [Fact]
    public void AffinityIsOptIn()
    {
        Assert.False(Load().Affinity);
        Assert.False(Load(("ARC_RX_AFFINITY", "0")).Affinity);
        Assert.True(Load(("ARC_RX_AFFINITY", "1")).Affinity);
    }

    [Fact]
    public void KeepaliveAndPollDefaultsArePerAlgo()
    {
        var cfg = CpuAlgoConfigLoader.Load("RX", defaultKeepaliveSec: 30, defaultPollSec: 4.0, env: Env());
        Assert.Equal(30, cfg.KeepaliveSec);
        Assert.Equal(4.0, cfg.PollSec);

        var nm = CpuAlgoConfigLoader.Load("NM", defaultKeepaliveSec: 60, env: Env());
        Assert.Equal(60, nm.KeepaliveSec);
    }

    [Fact]
    public void KeepaliveAndPollCanBeOverridden()
    {
        var cfg = Load(("ARC_STRATUM_KEEPALIVE_SEC", "15"), ("ARC_RX_POLL_SEC", "7.5"));
        Assert.Equal(15, cfg.KeepaliveSec);
        Assert.Equal(7.5, cfg.PollSec);
    }

    [Fact]
    public void BlankEnvValuesAreTreatedAsUnset()
    {
        // Program.cs sets these unconditionally in places; "  " must not become
        // a pool host.
        var cfg = Load(("ARC_RX_POOL", "   "), ("ARC_RX_ADDRESS", ""));
        Assert.Null(cfg.PoolUrl);
        Assert.Null(cfg.Address);
    }

    [Fact]
    public void ValuesAreTrimmed()
    {
        Assert.Equal("a.pool:3333", Load(("ARC_RX_POOL", "  a.pool:3333  ")).PoolUrl);
    }

    // ── pool vs solo ─────────────────────────────────────────────────────────
    //
    // --pool and --pool-cpu STRIP the scheme, storing host/port and recording
    // "was it stratum?" in a separate variable. So by the time the algo looks,
    // `stratum+tls://x:8029` is indistinguishable from a bare `x:8029` and the
    // hint is the only evidence left. Reading the hint from the WRONG source is
    // what made `--algo rx --pool-cpu stratum+tls://…` try to solo-mine over HTTP.

    [Theory]
    [InlineData("stratum+tcp://a.pool:3333")]
    [InlineData("stratum+tls://a.pool:3333")]
    [InlineData("stratum://a.pool:3333")]
    [InlineData("tcp://a.pool:3333")]
    [InlineData("ssl://a.pool:3333")]
    public void AnExplicitStratumSchemeIsAlwaysAPool(string url)
    {
        Assert.True(Load(("ARC_RX_POOL", url)).IsStratumPool);
        Assert.True(Load(("ARC_RX_DUAL", "1"), ("ARC_RX_POOL", url)).IsStratumPool);
    }

    [Theory]
    [InlineData("http://node.local:18081")]
    [InlineData("https://node.local:18081")]
    public void HttpUrlsAreAlwaysSoloDaemons(string url)
    {
        // Even with every stratum hint set — an http URL is JSON-RPC.
        var cfg = Load(("ARC_RX_POOL", url), ("ARC_POOL_STRATUM", "true"),
                       ("ARC_POOL_CPU_STRATUM", "true"));
        Assert.False(cfg.IsStratumPool);
    }

    [Fact]
    public void PoolCpuAloneIsRecognisedAsStratum()
    {
        // The regression: `--algo rx --pool-cpu stratum+tls://xmr.kryptex.network:8029`
        // with no --pool at all. Program.cs strips the scheme into
        // ARC_POOL_CPU_HOST/PORT and sets ARC_POOL_CPU_STRATUM. Consulting only
        // the shared ARC_POOL_STRATUM found nothing and rx went down the solo
        // HTTP path, failing with "An error occurred while sending the request".
        var cfg = Load(
            ("ARC_POOL_CPU_HOST", "xmr.kryptex.network"),
            ("ARC_POOL_CPU_PORT", "8029"),
            ("ARC_POOL_CPU_STRATUM", "true"),
            ("ARC_POOL_CPU_TLS", "true"),
            ("ARC_POOL_CPU_WALLET", "krxYZRQZJP"));

        Assert.Equal("xmr.kryptex.network:8029", cfg.PoolUrl);
        Assert.True(cfg.CanMine);
        Assert.True(cfg.UseTls);
        Assert.True(cfg.IsStratumPool);
    }

    [Fact]
    public void PoolCpuStratumHintIsHonouredWhenDualMiningToo()
    {
        var cfg = Load(("ARC_RX_DUAL", "1"),
                       ("ARC_POOL_CPU_HOST", "a.pool"), ("ARC_POOL_CPU_PORT", "3333"),
                       ("ARC_POOL_CPU_STRATUM", "true"));
        Assert.True(cfg.IsStratumPool);
    }

    [Fact]
    public void PoolCpuWithoutTheStratumHintIsASoloDaemon()
    {
        // --pool-cpu http://node:18081 → no ARC_POOL_CPU_STRATUM written.
        var cfg = Load(("ARC_POOL_CPU_HOST", "node.local"), ("ARC_POOL_CPU_PORT", "18081"));
        Assert.False(cfg.IsStratumPool);
    }

    [Fact]
    public void TheHintIsReadFromWhicheverFlagSuppliedThePool()
    {
        // Shared --pool supplied it, so the SHARED hint applies...
        Assert.True(Load(("ARC_POOL_HOST", "a.pool"), ("ARC_POOL_PORT", "3333"),
                         ("ARC_POOL_STRATUM", "true")).IsStratumPool);

        // ...and a shared hint must NOT leak onto a pool that came from
        // --pool-cpu, which is a different pool on a different coin.
        Assert.False(Load(("ARC_POOL_CPU_HOST", "node.local"), ("ARC_POOL_CPU_PORT", "18081"),
                          ("ARC_POOL_STRATUM", "true")).IsStratumPool);
    }

    [Fact]
    public void DualMiningIgnoresTheSharedStratumHintEntirely()
    {
        // The shared flags belong to the GPU algo; with no CPU-side pool there
        // is nothing to mine, so there is nothing to classify either.
        var cfg = Load(("ARC_RX_DUAL", "1"), ("ARC_POOL_HOST", "a.pool"),
                       ("ARC_POOL_PORT", "3333"), ("ARC_POOL_STRATUM", "true"));
        Assert.Null(cfg.PoolUrl);
        Assert.False(cfg.IsStratumPool);
    }

    [Fact]
    public void BareHostPortWithNoHintsIsSolo()
    {
        Assert.False(Load(("ARC_RX_POOL", "node.local:18081")).IsStratumPool);
    }

    [Fact]
    public void NoPoolAtAllIsNotAStratumPool()
    {
        Assert.False(Load().IsStratumPool);
    }

    // ── the benchmark-reason message ─────────────────────────────────────────

    [Fact]
    public void DualPairIsToldToUseTheCpuSideFlags()
    {
        var cfg = Load(("ARC_RX_DUAL", "1"));
        var msg = CpuAlgoConfigLoader.DescribeWhyNotMining(cfg, "rx", "monero address");
        Assert.Contains("--pool-cpu", msg, StringComparison.Ordinal);
        Assert.Contains("--wallet-cpu", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleAlgoRunIsToldToUseTheSharedFlags()
    {
        var msg = CpuAlgoConfigLoader.DescribeWhyNotMining(Load(), "rx", "monero address");
        Assert.Contains("--pool ", msg, StringComparison.Ordinal);
        Assert.DoesNotContain("--pool-cpu", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingHalfIsNamedSpecifically()
    {
        var noWallet = Load(("ARC_RX_POOL", "a.pool:3333"));
        Assert.Contains("no wallet", CpuAlgoConfigLoader.DescribeWhyNotMining(noWallet, "rx", "addr"), StringComparison.Ordinal);

        var noPool = Load(("ARC_RX_ADDRESS", "w"));
        Assert.Contains("no pool", CpuAlgoConfigLoader.DescribeWhyNotMining(noPool, "rx", "addr"), StringComparison.Ordinal);
    }
}
