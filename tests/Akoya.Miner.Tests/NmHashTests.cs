using Akoya.Miner.Algos.Nm;
using Xunit;

namespace Akoya.Miner.Tests;

// NeuroMorph share math. Its target is 256-bit BIG-endian — the opposite of
// RandomX, which is the exact mistake made while adapting the Rx code. Getting
// it wrong does not crash: every share is simply rejected as "low diff".
public class NmHashTests
{
    private static byte[] Bytes32(params (int Index, byte Value)[] set)
    {
        var b = new byte[32];
        foreach (var (i, v) in set) b[i] = v;
        return b;
    }

    private static byte[] Filled(byte v)
    {
        var b = new byte[32];
        Array.Fill(b, v);
        return b;
    }

    [Fact]
    public void ZeroHashMeetsAnyTarget()
    {
        Assert.True(NmHash.MeetsTarget(new byte[32], Bytes32((31, 1))));
    }

    [Fact]
    public void EqualHashAndTargetIsAValidShare()
    {
        var t = Bytes32((3, 0x7F), (17, 0x22));
        Assert.True(NmHash.MeetsTarget(t, t));
    }

    [Fact]
    public void HashAboveTargetIsRejected()
    {
        Assert.False(NmHash.MeetsTarget(Bytes32((31, 2)), Bytes32((31, 1))));
    }

    [Fact]
    public void ComparisonIsBigEndianWithByteZeroMostSignificant()
    {
        // The byte-order trap, mirrored from the RandomX/GhostRider convention.
        // Hash has 0xFF in its LAST byte (least significant, big-endian); target
        // has 1 in its FIRST (most significant). The hash is far smaller.
        Assert.True(NmHash.MeetsTarget(Bytes32((31, 0xFF)), Bytes32((0, 1))));

        // Converse: 0xFF in the FIRST byte is enormous and must lose.
        Assert.False(NmHash.MeetsTarget(Bytes32((0, 0xFF)), Bytes32((0, 1))));
    }

    [Fact]
    public void TheFirstDifferingByteDecidesTheComparison()
    {
        // Hash wins on byte 0 and must be accepted even though every later byte
        // is maximal — a comparison that kept scanning would get this wrong.
        var hash = Filled(0xFF);
        hash[0] = 0x00;
        var target = Filled(0x00);
        target[0] = 0x01;
        Assert.True(NmHash.MeetsTarget(hash, target));
    }

    // ── ParseTarget ──────────────────────────────────────────────────────────

    [Fact]
    public void ParsesAFull64HexTargetUnchanged()
    {
        var hex = string.Concat(Enumerable.Repeat("ab", 32));
        Assert.Equal(Filled(0xAB), NmHash.ParseTarget(hex));
    }

    [Fact]
    public void ShortTargetIsLeftPaddedSoItKeepsItsMagnitude()
    {
        // "ff" is the number 255, not 0xFF00…00 — it must land in the LAST byte.
        // Right-padding instead would turn the hardest possible target into the
        // easiest, and the miner would submit garbage on every hash.
        var t = NmHash.ParseTarget("ff");
        Assert.Equal(Bytes32((31, 0xFF)), t);
    }

    [Fact]
    public void ParsedShortTargetIsHarderThanAFullOne()
    {
        var shortTarget = NmHash.ParseTarget("ff");
        var fullTarget = NmHash.ParseTarget(string.Concat(Enumerable.Repeat("ff", 32)));
        // Anything meeting the short target must also meet the full one.
        Assert.True(NmHash.MeetsTarget(shortTarget, fullTarget));
        Assert.False(NmHash.MeetsTarget(fullTarget, shortTarget));
    }

    [Fact]
    public void OverlongTargetKeepsItsLeastSignificant32Bytes()
    {
        var hex = "ffff" + string.Concat(Enumerable.Repeat("11", 32));
        Assert.Equal(Filled(0x11), NmHash.ParseTarget(hex));
    }

    // ── DifficultyOf (display only) ──────────────────────────────────────────

    [Fact]
    public void DifficultyOfIsTwoTo256OverTheTarget()
    {
        // target = 2^248 (byte 0 == 1) → 2^256 / 2^248 == 256.
        Assert.Equal(256.0, NmHash.DifficultyOf(Bytes32((0, 1))));
    }

    [Fact]
    public void DifficultyOfTheEasiestTargetIsOne()
    {
        Assert.Equal(1.0, NmHash.DifficultyOf(Filled(0xFF)));
    }

    [Fact]
    public void DifficultyOfZeroTargetIsZeroRatherThanDivideByZero()
    {
        Assert.Equal(0.0, NmHash.DifficultyOf(new byte[32]));
    }

    [Fact]
    public void DifficultyRisesAsTheTargetFalls()
    {
        Assert.True(NmHash.DifficultyOf(Bytes32((0, 1))) > NmHash.DifficultyOf(Bytes32((0, 2))));
    }
}
