using Akoya.Miner.Config;
using Xunit;

namespace Akoya.Miner.Tests;

// --pool / --pool-cpu both funnel through PoolUrl.Parse, so a regression here
// silently mis-routes one half of a dual-mining pair.
public class PoolUrlTests
{
    [Theory]
    // scheme                              host              port    stratum tls
    [InlineData("stratum+tcp://a.pool:3333", "a.pool", "3333", true, false)]
    [InlineData("stratum+ssl://a.pool:3333", "a.pool", "3333", true, true)]
    [InlineData("stratum+tls://a.pool:3333", "a.pool", "3333", true, true)]
    [InlineData("tcp://a.pool:3333", "a.pool", "3333", true, false)]
    [InlineData("ssl://a.pool:3333", "a.pool", "3333", true, true)]
    public void RecognisesStratumSchemes(string url, string host, string port, bool stratum, bool tls)
    {
        var r = PoolUrl.Parse(url);
        Assert.Equal(host, r.Host);
        Assert.Equal(port, r.Port);
        Assert.Equal(stratum, r.IsStratum);
        Assert.Equal(tls, r.Tls);
    }

    [Fact]
    public void SchemesAreCaseInsensitive()
    {
        var r = PoolUrl.Parse("STRATUM+SSL://A.Pool:3333");
        Assert.Equal("A.Pool", r.Host);
        Assert.True(r.IsStratum);
        Assert.True(r.Tls);
    }

    [Fact]
    public void BareStratumSchemeSaysNothingAboutTls()
    {
        // stratum:// carries no transport hint — Tls must stay null so an
        // explicit ARC_*_TLS setting is not overwritten with "false".
        var r = PoolUrl.Parse("stratum://a.pool:3333");
        Assert.True(r.IsStratum);
        Assert.Null(r.Tls);
    }

    [Theory]
    [InlineData("http://node.local:18081")]
    [InlineData("https://node.local:18081")]
    public void HttpSchemesAreNotStratum(string url)
    {
        // An http(s) URL is a solo daemon (JSON-RPC), never a pool.
        var r = PoolUrl.Parse(url);
        Assert.Equal("node.local", r.Host);
        Assert.Equal("18081", r.Port);
        Assert.False(r.IsStratum);
        Assert.Null(r.Tls);
    }

    [Fact]
    public void SchemelessHostPortIsPassedThrough()
    {
        var r = PoolUrl.Parse("a.pool:3333");
        Assert.Equal("a.pool", r.Host);
        Assert.Equal("3333", r.Port);
        Assert.False(r.IsStratum);
        Assert.Null(r.Tls);
    }

    [Fact]
    public void MissingPortYieldsNullPortNotEmptyString()
    {
        // The caller distinguishes "no port given" (use the algo default) from a
        // supplied one; an empty string would be parsed as a port and fail.
        var r = PoolUrl.Parse("stratum+tcp://a.pool");
        Assert.Equal("a.pool", r.Host);
        Assert.Null(r.Port);
    }

    [Fact]
    public void HostnameWithoutPortAndNoSchemeYieldsNullPort()
    {
        var r = PoolUrl.Parse("a.pool");
        Assert.Equal("a.pool", r.Host);
        Assert.Null(r.Port);
    }

    [Fact]
    public void SplitsOnTheLastColon()
    {
        // Naive Split(':') would shred anything with more than one colon.
        var r = PoolUrl.Parse("a:b.pool:3333");
        Assert.Equal("a:b.pool", r.Host);
        Assert.Equal("3333", r.Port);
    }

    [Fact]
    public void BracketedIPv6KeepsItsColons()
    {
        var r = PoolUrl.Parse("stratum+tcp://[2001:db8::1]:3333");
        Assert.Equal("2001:db8::1", r.Host);
        Assert.Equal("3333", r.Port);
        Assert.True(r.IsStratum);
    }

    [Fact]
    public void BracketedIPv6KeepsItsZoneId()
    {
        var r = PoolUrl.Parse("[fe80::1%14]:3335");
        Assert.Equal("fe80::1%14", r.Host);
        Assert.Equal("3335", r.Port);
    }

    [Fact]
    public void BracketedIPv6WithoutPortYieldsNullPort()
    {
        var r = PoolUrl.Parse("[2001:db8::1]");
        Assert.Equal("2001:db8::1", r.Host);
        Assert.Null(r.Port);
    }

    [Fact]
    public void UnclosedBracketPassesThroughRatherThanThrowing()
    {
        // Deliberate: a malformed literal should fail at connect time with a
        // legible address, not blow up in the arg parser.
        var r = PoolUrl.Parse("[2001:db8::1:3333");
        Assert.Equal("[2001:db8::1:3333", r.Host);
        Assert.Null(r.Port);
    }

    [Fact]
    public void TrailingPathIsStripped()
    {
        var r = PoolUrl.Parse("stratum+tcp://a.pool:3333/worker1");
        Assert.Equal("a.pool", r.Host);
        Assert.Equal("3333", r.Port);
    }

    [Fact]
    public void PathIsStrippedBeforeThePortSplit()
    {
        // If the path survived, LastIndexOf(':') could pick a colon inside it.
        var r = PoolUrl.Parse("http://node.local:18081/json_rpc:x");
        Assert.Equal("node.local", r.Host);
        Assert.Equal("18081", r.Port);
    }
}
