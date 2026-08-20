using System.Text.Json;
using Akoya.Pool;
using Xunit;

namespace Akoya.Miner.Tests;

/// <summary>
/// BIP34 block-height recovery from the coinbase.
///
/// Guards a bug that was live and SILENT: StratumJobParser hard-coded
/// BlockHeight=0, which fed SaltedSeedFork.IsActive() and therefore pinned the
/// miner to legacy V2 noise-seed derivation on every Bitcoin-style stratum
/// notify. Past the salted-seed activation height that means every share is
/// proved against the wrong noise field — with nothing in the log, because
/// SaltedSeedFork.Apply early-returns before its warning when the state does not
/// change (and from a cold start it never does).
/// </summary>
public class Bip34HeightTests
{
    // A coinbase prefix through to the scriptSig:
    //   version(4) | in-count(1) | prev txid(32 zero) | prev index(4) | scriptSig-len(1)
    private const string Prefix =
        "01000000" + "01" + "0000000000000000000000000000000000000000000000000000000000000000" + "ffffffff";

    private static byte[] Coinb1(string scriptSigHex)
    {
        int len = scriptSigHex.Length / 2;
        return Convert.FromHexString(Prefix + len.ToString("x2") + scriptSigHex);
    }

    [Fact]
    public void ExtractsTheHeightFromARealisticCoinbase()
    {
        // 99,000 = 0x0182B8 → minimal little-endian push of 3 bytes: B8 82 01.
        // That is the salted-seed activation height, so it is the number this
        // whole mechanism exists to get right.
        var coinb1 = Coinb1("03b8820100000000");
        Assert.Equal(99_000, StratumJobParser.TryParseBip34Height(coinb1));
    }

    [Theory]
    [InlineData("03b8820100000000", 99_000)]   // 3-byte push
    [InlineData("0201000000000000", 1)]         // 2-byte push, height 1
    [InlineData("04b882010000000000", 99_000)]  // 4-byte push (minimal-encoding zero pad)
    public void HandlesEveryPushWidthBip34Uses(string scriptSig, long expected)
        => Assert.Equal(expected, StratumJobParser.TryParseBip34Height(Coinb1(scriptSig)));

    // Everything below must degrade to 0 = "unknown". 0 is what every caller
    // already treats as "not evidence of a fork state", so an unreadable or
    // hostile coinbase lands on the old behaviour rather than asserting a wrong
    // height — a wrong height is worse than none, because it could flip a gate.
    [Fact]
    public void ShortBufferIsUnknownNotAGuess()
    {
        Assert.Equal(0, StratumJobParser.TryParseBip34Height(Array.Empty<byte>()));
        Assert.Equal(0, StratumJobParser.TryParseBip34Height(new byte[41]));
    }

    [Fact]
    public void NonBip34OpcodesAreUnknown()
    {
        // 0x4c = OP_PUSHDATA1, not the direct push BIP34 mandates.
        Assert.Equal(0, StratumJobParser.TryParseBip34Height(Coinb1("4c0301020300000000")));
        // A zero-length push carries no height.
        Assert.Equal(0, StratumJobParser.TryParseBip34Height(Coinb1("0000000000000000")));
    }

    [Fact]
    public void TruncatedPushDoesNotReadPastTheBuffer()
    {
        // Claims a 4-byte push but only 2 bytes follow.
        Assert.Equal(0, StratumJobParser.TryParseBip34Height(Coinb1("04b882")));
    }

    [Fact]
    public void ImplausibleHeightIsRejected()
    {
        // 0xFFFFFFFF-ish — far beyond any real chain. Must not be allowed to trip
        // a fork gate that compares against ~99k.
        Assert.Equal(0, StratumJobParser.TryParseBip34Height(Coinb1("04ffffff7f00000000")));
    }

    /// <summary>
    /// End-to-end through the real notify parser — the actual regression guard.
    /// Before the fix this asserted 0 no matter what the coinbase said.
    /// </summary>
    [Fact]
    public void ParseNotificationPopulatesBlockHeight()
    {
        string coinb1Hex = Convert.ToHexString(Coinb1("03b8820100000000")).ToLowerInvariant();
        var arr = JsonDocument.Parse($$"""
        [
          "deadbeef",
          "{{new string('0', 64)}}",
          "{{coinb1Hex}}",
          "00000000",
          [],
          "20000000",
          "1d00ffff",
          "65000000",
          true
        ]
        """).RootElement;

        var job = StratumJobParser.ParseNotification(arr, new byte[4], new byte[4]);
        Assert.Equal(99_000, job.BlockHeight);
    }
}
