using Akoya.Miner.Config;
using Xunit;

namespace Akoya.Miner.Tests;

// The flag table. Previously unreachable from a test at all (top-level
// statements in Program.cs), which is how it acquired both dead branches and a
// flag that only worked for one of the three CPU algos.
public class CommandLineTests
{
    private static CommandLineResult P(params string[] args) => CommandLine.Parse(args);

    // ── subcommands ──────────────────────────────────────────────────────────

    [Fact]
    public void DefaultSubcommandIsMineBlocks()
    {
        Assert.Equal("mine-blocks", P().Subcommand);
        Assert.Equal("mine-blocks", P("--pool", "a:1").Subcommand);
    }

    [Theory]
    [InlineData("selftest")]
    [InlineData("--selftest")]
    [InlineData("version")]
    [InlineData("--version")]
    [InlineData("-V")]
    [InlineData("mine-blocks")]
    public void KnownSubcommandsAreRecognised(string arg) => Assert.Equal(arg, P(arg).Subcommand);

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    public void HelpFlagsMapToHelp(string arg) => Assert.Equal("help", P(arg).Subcommand);

    [Fact]
    public void ABareLeadingWordIsAnExternalSubcommand()
    {
        // e.g. "arc-miner autotune --whatever" — flags are that command's problem.
        var r = P("autotune", "--pool", "a:1");
        Assert.Equal("autotune", r.Subcommand);
        Assert.Empty(r.EnvVars);
    }

    [Fact]
    public void AKnownSubcommandStillParsesTheFlagsAfterIt()
    {
        var r = P("selftest", "--algo", "rx");
        Assert.Equal("selftest", r.Subcommand);
        Assert.Equal("rx", r.Get("ARC_ALGO"));
    }

    // ── pool flags ───────────────────────────────────────────────────────────

    [Fact]
    public void PoolSetsHostPortAndSchemeDerivedFlags()
    {
        var r = P("--pool", "stratum+tls://a.pool:3333");
        Assert.Equal("a.pool", r.Get("ARC_POOL_HOST"));
        Assert.Equal("3333", r.Get("ARC_POOL_PORT"));
        Assert.Equal("true", r.Get("ARC_POOL_STRATUM"));
        Assert.Equal("true", r.Get("ARC_POOL_TLS"));
    }

    [Fact]
    public void PoolWithoutATlsSchemeLeavesTlsUnset()
    {
        // stratum:// says nothing about transport — writing "false" here would
        // stomp an explicit --tls or ARC_POOL_TLS.
        Assert.Null(P("--pool", "stratum://a.pool:3333").Get("ARC_POOL_TLS"));
    }

    [Fact]
    public void PoolWithoutAPortDoesNotSetAnEmptyPort()
    {
        var r = P("--pool", "stratum+tcp://a.pool");
        Assert.Equal("a.pool", r.Get("ARC_POOL_HOST"));
        Assert.Null(r.Get("ARC_POOL_PORT"));
    }

    [Theory]
    [InlineData("--wallet")]
    [InlineData("-w")]
    public void WalletAliases(string flag) => Assert.Equal("addr", P(flag, "addr").Get("ARC_POOL_WALLET"));

    [Theory]
    [InlineData("--worker")]
    [InlineData("--workername")]
    [InlineData("-n")]
    public void WorkerAliases(string flag) => Assert.Equal("rig1", P(flag, "rig1").Get("ARC_POOL_WORKER"));

    // ── CPU-side flags (dual mining) ─────────────────────────────────────────

    [Theory]
    [InlineData("--pool-cpu")]
    [InlineData("--cpu-pool")]
    public void PoolCpuAliasesSetTheGenericCpuVariables(string flag)
    {
        var r = P(flag, "stratum+tls://cpu.pool:8029");
        Assert.Equal("cpu.pool", r.Get("ARC_POOL_CPU_HOST"));
        Assert.Equal("8029", r.Get("ARC_POOL_CPU_PORT"));
        Assert.Equal("true", r.Get("ARC_POOL_CPU_STRATUM"));
        Assert.Equal("true", r.Get("ARC_POOL_CPU_TLS"));
    }

    [Fact]
    public void CpuFlagsNeverWriteRxSpecificVariables()
    {
        // There used to be a second, unreachable set of branches mapping these
        // onto ARC_RX_* — a leftover from when --pool-cpu was rx-only. They must
        // stay generic or gr and nm silently miss the setting.
        var r = P("--pool-cpu", "a:1", "--wallet-cpu", "w", "--worker-cpu", "k", "--password-cpu", "p");
        foreach (var key in new[] { "ARC_RX_NODE", "ARC_RX_ADDRESS", "ARC_RX_WORKER", "ARC_RX_PASSWORD" })
        {
            Assert.Null(r.Get(key));
        }
        Assert.Equal("w", r.Get("ARC_POOL_CPU_WALLET"));
        Assert.Equal("k", r.Get("ARC_POOL_CPU_WORKER"));
        Assert.Equal("p", r.Get("ARC_POOL_CPU_PASSWORD"));
    }

    [Theory]
    [InlineData("--wallet-cpu", "--cpu-wallet", "ARC_POOL_CPU_WALLET")]
    [InlineData("--worker-cpu", "--cpu-worker", "ARC_POOL_CPU_WORKER")]
    [InlineData("--password-cpu", "--cpu-password", "ARC_POOL_CPU_PASSWORD")]
    public void CpuFlagAliasesAgree(string a, string b, string key)
    {
        Assert.Equal("v", P(a, "v").Get(key));
        Assert.Equal("v", P(b, "v").Get(key));
    }

    [Fact]
    public void ThreadsCpuIsGenericNotRxOnly()
    {
        // The bug: --threads-cpu wrote only ARC_RX_THREADS, so gr and nm ignored
        // it — while both name the flag in their auto-reserve log line.
        var r = P("--threads-cpu", "8");
        Assert.Equal("8", r.Get("ARC_POOL_CPU_THREADS"));
        Assert.Null(r.Get("ARC_RX_THREADS"));
    }

    // ── TLS ──────────────────────────────────────────────────────────────────

    [Fact]
    public void TlsFlags()
    {
        Assert.Equal("true", P("--tls").Get("ARC_POOL_TLS"));
        Assert.Equal("false", P("--no-tls").Get("ARC_POOL_TLS"));
        Assert.Equal("true", P("--tls-insecure").Get("ARC_POOL_TLS_INSECURE"));
    }

    [Fact]
    public void LaterFlagsWinOverEarlierOnes()
    {
        Assert.Equal("false", P("--tls", "--no-tls").Get("ARC_POOL_TLS"));
        Assert.Equal("true", P("--no-tls", "--tls").Get("ARC_POOL_TLS"));
    }

    // ── optional-value flags ─────────────────────────────────────────────────

    [Fact]
    public void KeepaliveDefaultsWhenBare()
    {
        Assert.Equal("120", P("--keepalive").Get("ARC_STRATUM_KEEPALIVE_SEC"));
        Assert.Equal("90", P("--keepalive", "90").Get("ARC_STRATUM_KEEPALIVE_SEC"));
    }

    [Fact]
    public void KeepaliveDoesNotSwallowTheNextFlag()
    {
        var r = P("--keepalive", "--algo", "rx");
        Assert.Equal("120", r.Get("ARC_STRATUM_KEEPALIVE_SEC"));
        Assert.Equal("rx", r.Get("ARC_ALGO"));
    }

    [Fact]
    public void DashboardDefaultsWhenBare()
    {
        var bare = P("--dashboard");
        Assert.Equal("1", bare.Get("ARC_DASHBOARD"));
        Assert.Null(bare.Get("ARC_DASHBOARD_REFRESH_MS"));

        var withMs = P("--dashboard", "500");
        Assert.Equal("1", withMs.Get("ARC_DASHBOARD"));
        Assert.Equal("500", withMs.Get("ARC_DASHBOARD_REFRESH_MS"));
    }

    [Fact]
    public void DashboardDoesNotSwallowTheNextFlag()
    {
        var r = P("--dashboard", "--algo", "gr");
        Assert.Equal("1", r.Get("ARC_DASHBOARD"));
        Assert.Null(r.Get("ARC_DASHBOARD_REFRESH_MS"));
        Assert.Equal("gr", r.Get("ARC_ALGO"));
    }

    // The dashboard is on by default (Dashboard.TryEnable treats an unset
    // ARC_DASHBOARD as "1"), so an untouched command line must not set the
    // variable at all — and --dash-off is the way to opt out.
    [Fact]
    public void DashboardIsOnByDefaultAndDashOffDisablesIt()
    {
        Assert.Null(P("--algo", "gr").Get("ARC_DASHBOARD"));
        Assert.Equal("0", P("--dash-off").Get("ARC_DASHBOARD"));

        // Last write wins, so --dashboard after --dash-off re-enables it.
        Assert.Equal("1", P("--dash-off", "--dashboard").Get("ARC_DASHBOARD"));
    }

    [Fact]
    public void ThemeFlagSetsTheSkin()
    {
        Assert.Equal("rogue", P("--theme", "rogue").Get("ARC_THEME"));
        Assert.Null(P("--algo", "gr").Get("ARC_THEME"));

        // --theme takes a mandatory value and consumes the next token blindly,
        // exactly like --pool/--wallet/--algo. Only the optional-value flags
        // (--dashboard, --keepalive) guard against swallowing the next flag.
        Assert.Equal("--algo", P("--theme", "--algo", "gr").Get("ARC_THEME"));
    }

    // ── misc ─────────────────────────────────────────────────────────────────

    [Fact]
    public void SimpleValueFlags()
    {
        Assert.Equal("rx", P("--algo", "rx").Get("ARC_ALGO"));
        Assert.Equal("x;d=250000", P("--password", "x;d=250000").Get("ARC_STRATUM_PASSWORD"));
        Assert.Equal("x", P("-p", "x").Get("ARC_STRATUM_PASSWORD"));
        Assert.Equal("250000", P("--diff", "250000").Get("ARC_STRATUM_DIFF"));
        Assert.Equal("4028", P("--api-port", "4028").Get("ARC_METRICS_PORT"));
        Assert.Equal("s3cret", P("--api-password", "s3cret").Get("ARC_API_PASSWORD"));
        Assert.Equal("12", P("--mpp", "12").Get("ARC_MINE_MPP_OVERRIDE"));
        Assert.Equal("5000", P("--budget", "5000").Get("ARC_BENCHMARK_BUDGET_MS"));
    }

    [Fact]
    public void BooleanFlags()
    {
        Assert.Equal("0", P("--no-autotune").Get("ARC_AUTOTUNE_ON_FIRST_RUN"));
        Assert.Equal("1", P("--igpu").Get("ARC_IGPU"));
    }

    // ── robustness ───────────────────────────────────────────────────────────

    [Fact]
    public void AValueFlagAtTheEndWithNoValueIsIgnoredNotCrashed()
    {
        foreach (var flag in new[] { "--pool", "--wallet", "--algo", "--pool-cpu", "--threads-cpu", "--api-port" })
        {
            var r = CommandLine.Parse(new[] { flag });
            Assert.Empty(r.EnvVars);
            Assert.Equal("mine-blocks", r.Subcommand);
        }
    }

    [Fact]
    public void UnknownFlagsAreIgnored()
    {
        var r = P("--not-a-real-flag", "--algo", "rx");
        Assert.Equal("rx", r.Get("ARC_ALGO"));
    }

    [Fact]
    public void ParseDoesNotTouchTheEnvironment()
    {
        // The whole reason this is split from Apply.
        const string key = "ARC_ALGO";
        var before = Environment.GetEnvironmentVariable(key);
        _ = P("--algo", "definitely-not-a-real-algo");
        Assert.Equal(before, Environment.GetEnvironmentVariable(key));
    }

    [Fact]
    public void AFullDualMiningCommandLineParsesAsExpected()
    {
        // The real invocation from the docs, end to end.
        var r = P("--algo", "prl+gr",
                  "--pool", "stratum+tls://prl.kryptex.network:8048", "--wallet", "krx.worker1",
                  "--pool-cpu", "stratum+tcp://us-east.flockpool.com:4444", "--wallet-cpu", "RLRF",
                  "--threads-cpu", "22", "--dashboard");

        Assert.Equal("prl+gr", r.Get("ARC_ALGO"));
        Assert.Equal("prl.kryptex.network", r.Get("ARC_POOL_HOST"));
        Assert.Equal("8048", r.Get("ARC_POOL_PORT"));
        Assert.Equal("true", r.Get("ARC_POOL_TLS"));
        Assert.Equal("krx.worker1", r.Get("ARC_POOL_WALLET"));
        Assert.Equal("us-east.flockpool.com", r.Get("ARC_POOL_CPU_HOST"));
        Assert.Equal("4444", r.Get("ARC_POOL_CPU_PORT"));
        Assert.Equal("false", r.Get("ARC_POOL_CPU_TLS"));
        Assert.Equal("RLRF", r.Get("ARC_POOL_CPU_WALLET"));
        Assert.Equal("22", r.Get("ARC_POOL_CPU_THREADS"));
        Assert.Equal("1", r.Get("ARC_DASHBOARD"));
    }
}
