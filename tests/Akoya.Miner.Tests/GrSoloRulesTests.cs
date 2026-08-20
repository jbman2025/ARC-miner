using System.Text.Json;
using Akoya.Miner.Algos.Gr;
using Xunit;

namespace Akoya.Miner.Tests;

// gr's solo builder makes a STANDARD Bitcoin coinbase. Chains that require
// smartnode/founder payouts or a cbTx payload (Raptoreum mainnet) reject every
// block it produces — previously only visible as a "solo block rejected" line
// after the work was already wasted.
//
// Keyed on the template's fields rather than the chain name: what breaks us is
// the rules, not the network.
public class GrSoloRulesTests
{
    private static string? Reason(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return GrSolo.DescribeUnsupportedRules(doc.RootElement);
    }

    [Fact]
    public void APlainTemplateIsAccepted()
    {
        Assert.Null(Reason("""
            {"version":536870912,"height":100,"bits":"1d00ffff","coinbasevalue":5000000000}
            """));
    }

    [Fact]
    public void ACbTxPayloadIsRefused()
    {
        var r = Reason("""{"height":1,"coinbase_payload":"0200a1b2c3"}""");
        Assert.NotNull(r);
        Assert.Contains("coinbase_payload", r, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyCbTxPayloadIsNotARefusal()
    {
        // Some nodes always emit the field; empty means "nothing required".
        Assert.Null(Reason("""{"height":1,"coinbase_payload":""}"""));
    }

    [Theory]
    [InlineData("smartnode")]
    [InlineData("masternode")]
    public void RequiredPayeesAreRefusedUnderEitherSpelling(string field)
    {
        var arr = Reason($$"""{"height":1,"{{field}}":[{"payee":"RXyz","amount":1}]}""");
        Assert.NotNull(arr);
        Assert.Contains(field, arr, StringComparison.Ordinal);

        // Raptoreum sends a single object rather than an array on some versions.
        var obj = Reason("{\"height\":1,\"" + field + "\":{\"payee\":\"RXyz\",\"amount\":1}}");
        Assert.NotNull(obj);
        Assert.Contains(field, obj, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("smartnode")]
    [InlineData("masternode")]
    public void AnEmptyPayeeListIsNotARefusal(string field)
    {
        Assert.Null(Reason($$"""{"height":1,"{{field}}":[]}"""));
        Assert.Null(Reason("{\"height\":1,\"" + field + "\":{}}"));
    }

    [Theory]
    [InlineData("smartnode")]
    [InlineData("masternode")]
    public void EnforcedPaymentsAreRefusedEvenWithNoPayeeThisBlock(string field)
    {
        // The payee list can be empty for a given block while the rule is still
        // enforced — mining it would still produce a reject.
        var r = Reason($$"""{"height":1,"{{field}}_payments_enforced":true}""");
        Assert.NotNull(r);
        Assert.Contains(field, r, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("smartnode")]
    [InlineData("masternode")]
    public void PaymentsNotEnforcedIsFine(string field)
    {
        Assert.Null(Reason($$"""{"height":1,"{{field}}_payments_enforced":false}"""));
    }

    [Fact]
    public void SuperblockPayoutsAreRefused()
    {
        var r = Reason("""{"height":1,"superblock":[{"payee":"RXyz","amount":9}]}""");
        Assert.NotNull(r);
        Assert.Contains("superblock", r, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptySuperblockIsNotARefusal()
    {
        // Present-but-empty on every ordinary block of a governance chain.
        Assert.Null(Reason("""{"height":1,"superblock":[]}"""));
    }

    [Fact]
    public void ARealisticRaptoreumMainnetTemplateIsRefused()
    {
        Assert.NotNull(Reason("""
            {
              "version": 536870912,
              "previousblockhash": "0000000000000000000000000000000000000000000000000000000000000001",
              "height": 1234567,
              "bits": "1d0578be",
              "curtime": 1785000000,
              "coinbasevalue": 500000000000,
              "coinbase_payload": "0200e1f505000000000000",
              "smartnode": [{"payee":"RBcd","script":"76a914","amount":200000000000}],
              "smartnode_payments_started": true,
              "smartnode_payments_enforced": true,
              "superblock": [],
              "transactions": []
            }
            """));
    }

    // ── benchmark seed rotation (item 7) ─────────────────────────────────────

    [Fact]
    public void EachBenchmarkGenerationGivesADifferentPrevHash()
    {
        // GhostRider derives its CryptoNight trio from the header's prev-hash,
        // so a benchmark that never changes it measures one trio and reports it
        // as the machine's GhostRider rate. Distinct seeds are the whole point.
        var seeds = Enumerable.Range(0, 32)
            .Select(g => Convert.ToHexString(GrAlgo.BenchSeedFor(g)))
            .ToList();
        Assert.Equal(seeds.Count, seeds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void BenchmarkSeedIsDeterministicAndFullWidth()
    {
        // Deterministic so two runs are comparable; 32 bytes so it fills the
        // whole prev-hash field rather than leaving a constant tail.
        Assert.Equal(GrAlgo.BenchSeedFor(7), GrAlgo.BenchSeedFor(7));
        Assert.Equal(32, GrAlgo.BenchSeedFor(0).Length);
        // ...and not a constant fill, which would bias the variant selection.
        Assert.True(GrAlgo.BenchSeedFor(0).Distinct().Count() > 1);
    }

    [Fact]
    public void ARegtestStyleTemplateWithoutThoseRulesIsAccepted()
    {
        // The case solo mining is actually for: a bare GhostRider chain.
        Assert.Null(Reason("""
            {
              "version": 536870912,
              "previousblockhash": "00000000000000000000000000000000000000000000000000000000000000ff",
              "height": 42,
              "bits": "207fffff",
              "curtime": 1785000000,
              "coinbasevalue": 500000000000,
              "smartnode": [],
              "smartnode_payments_enforced": false,
              "superblock": [],
              "transactions": []
            }
            """));
    }
}
