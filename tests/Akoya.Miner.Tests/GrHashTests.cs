using System.Numerics;
using Akoya.Miner.Algos.Gr;
using Xunit;

namespace Akoya.Miner.Tests;

// GhostRider share math. Two of this module's shipped bugs lived right here:
// an inverted diff_to_target (normalisation multiplying instead of dividing,
// landing ~2^64 too tight so the miner found no shares at all), and header
// word-swapping applied where it must not be. Both are pure functions.
public class GrHashTests
{
    // The eight little-endian words as one 256-bit integer (word 7 = most significant).
    private static BigInteger ToBig(uint[] target)
    {
        BigInteger v = 0;
        for (int i = 7; i >= 0; i--) v = (v << 32) | target[i];
        return v;
    }

    private static BigInteger Pow2(int n) => BigInteger.One << n;

    [Fact]
    public void Diff256TargetMatchesTheClosedForm()
    {
        // cpuminer-gr's diff_to_target on diff/65536 works out to
        //   target ≈ (2^240 - 2^224) / diff
        // i.e. 2^256/(diff * 65536) less the customary (1 - 2^-16) slack.
        foreach (double diff in new[] { 1.0, 100.0, 65536.0, 1e6, 1e9 })
        {
            var expected = (Pow2(240) - Pow2(224)) / new BigInteger(diff);
            var actual = ToBig(GrHash.Diff256Target(diff));

            // Allow the last word of rounding slop from the double division.
            var slop = BigInteger.Abs(expected - actual);
            Assert.True(slop <= expected / 1000000,
                $"diff={diff}: expected ~{expected}, got {actual}");
        }
    }

    [Fact]
    public void TargetAtDiff65536LandsInWordSix()
    {
        // diff/65536 == 1 exactly: no normalisation steps, k stays 6.
        // This is the anchor case — an inverted normalisation loop moves the
        // non-zero word and the whole scale goes with it.
        var t = GrHash.Diff256Target(65536.0);
        Assert.Equal(0xFFFF0000u, t[6]);
        Assert.Equal(0u, t[7]);
        for (int i = 0; i < 6; i++) Assert.Equal(0u, t[i]);
    }

    [Fact]
    public void TargetIsMonotonicallyDecreasingInDifficulty()
    {
        // The single property that catches an inverted diff_to_target outright:
        // harder must mean a smaller target, at every scale.
        double[] diffs = { 1, 10, 1000, 65536, 1e5, 1e6, 1e8, 1e10, 1e12 };
        for (int i = 1; i < diffs.Length; i++)
        {
            var lo = ToBig(GrHash.Diff256Target(diffs[i - 1]));
            var hi = ToBig(GrHash.Diff256Target(diffs[i]));
            Assert.True(hi < lo, $"target for diff={diffs[i]} should be below diff={diffs[i - 1]}");
        }
    }

    [Fact]
    public void TargetNeverExceedsTheFull256BitRange()
    {
        foreach (double diff in new[] { 0.0, -5.0, 1.0, 1e-6 })
            Assert.True(ToBig(GrHash.Diff256Target(diff)) < Pow2(256));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void NonPositiveDifficultyIsTreatedAsOne(double diff)
    {
        Assert.Equal(GrHash.Diff256Target(1.0), GrHash.Diff256Target(diff));
    }

    // ── MeetsTarget (cpuminer fulltest) ──────────────────────────────────────

    private static byte[] Hash32(params (int Index, byte Value)[] bytes)
    {
        var h = new byte[32];
        foreach (var (i, v) in bytes) h[i] = v;
        return h;
    }

    [Fact]
    public void ZeroHashMeetsAnyTarget()
    {
        Assert.True(GrHash.MeetsTarget(new byte[32], GrHash.Diff256Target(1e12)));
    }

    [Fact]
    public void HashEqualToTargetIsAValidShare()
    {
        var target = GrHash.Diff256Target(65536.0);
        var hash = new byte[32];
        for (int w = 0; w < 8; w++)
            BitConverter.GetBytes(target[w]).CopyTo(hash, w * 4);
        Assert.True(GrHash.MeetsTarget(hash, target));
    }

    [Fact]
    public void HashOneAboveTargetIsRejected()
    {
        var target = GrHash.Diff256Target(65536.0);
        var hash = new byte[32];
        for (int w = 0; w < 8; w++)
            BitConverter.GetBytes(w == 6 ? target[w] + 1 : target[w]).CopyTo(hash, w * 4);
        Assert.False(GrHash.MeetsTarget(hash, target));
    }

    [Fact]
    public void ComparisonIsLittleEndianWithWordSevenMostSignificant()
    {
        // The byte-order trap. Hash has 0xFF in its LEAST significant byte and
        // nothing else; the target has 1 in its MOST significant word. The hash
        // is far smaller, so this is a share. An implementation that treats
        // byte 0 as most significant reads the hash as enormous and rejects.
        var target = new uint[8];
        target[7] = 1;
        Assert.True(GrHash.MeetsTarget(Hash32((0, 0xFF)), target));

        // ...and the converse: 0xFF in the MOST significant byte must lose.
        Assert.False(GrHash.MeetsTarget(Hash32((31, 0xFF)), target));
    }

    // ── Swab32 ───────────────────────────────────────────────────────────────

    [Fact]
    public void Swab32ReversesEachFourByteWordIndependently()
    {
        var buf = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        GrHash.Swab32(buf);
        Assert.Equal(new byte[] { 4, 3, 2, 1, 8, 7, 6, 5 }, buf);
    }

    [Fact]
    public void Swab32IsItsOwnInverse()
    {
        var original = new byte[32];
        new Random(1234).NextBytes(original);
        var buf = (byte[])original.Clone();
        GrHash.Swab32(buf);
        GrHash.Swab32(buf);
        Assert.Equal(original, buf);
    }

    [Fact]
    public void Swab32LeavesATrailingPartialWordAlone()
    {
        // The loop guard is `i + 4 <= length`; a 6-byte buffer must not have its
        // last two bytes touched (or read past the end).
        var buf = new byte[] { 1, 2, 3, 4, 5, 6 };
        GrHash.Swab32(buf);
        Assert.Equal(new byte[] { 4, 3, 2, 1, 5, 6 }, buf);
    }

    // ── hex + sha256d ────────────────────────────────────────────────────────

    [Fact]
    public void UnhexAndHexRoundTrip()
    {
        const string hex = "00ff107ecafebabe0123456789abcdef";
        Assert.Equal(hex, GrHash.Hex(GrHash.Unhex(hex)));
    }

    [Fact]
    public void Sha256dIsSha256AppliedTwice()
    {
        // Known vector: SHA256d("") — the value Bitcoin/Raptoreum tooling reports.
        Assert.Equal(
            "5df6e0e2761359d30a8275058e299fcc0381534545f55cf43e41983f5d4c9456",
            GrHash.Hex(GrHash.Sha256d(Array.Empty<byte>())));
    }
}
