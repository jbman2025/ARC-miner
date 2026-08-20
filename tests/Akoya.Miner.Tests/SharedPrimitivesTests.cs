// Covers the primitives Phase 2 pulled out of the per-algo hash classes
// (Akoya.Crypto: Hex, Sha2, Uint256).
//
// The odd-length cases matter more than they look. Before consolidation the
// csd/gr Unhex SILENTLY TRUNCATED odd input (new byte[s.Length/2] dropped the
// last nibble) while Pool's parser left-padded it, and the pool's own
// MiningSession threw. Three answers to one question, none of them tested.
// Hex.Decode now left-pads everywhere; these pin that down.

using System.Numerics;
using Akoya.Crypto;
using Xunit;

namespace Akoya.Miner.Tests;

public class SharedPrimitivesTests
{
    // ------------------------------------------------------------- Hex ---

    [Theory]
    [InlineData("", new byte[0])]
    [InlineData("00", new byte[] { 0x00 })]
    [InlineData("ff", new byte[] { 0xff })]
    [InlineData("DEADBEEF", new byte[] { 0xde, 0xad, 0xbe, 0xef })]
    [InlineData("deadbeef", new byte[] { 0xde, 0xad, 0xbe, 0xef })]
    public void DecodeHandlesEvenLengthAndIsCaseInsensitive(string hex, byte[] expected)
        => Assert.Equal(expected, Hex.Decode(hex));

    [Fact]
    public void DecodeOfNullIsEmptyNotAThrow() => Assert.Empty(Hex.Decode(null));

    // "abc" is the number 0x0abc. The old csd/gr loop returned [0xab] — right
    // length for s.Length/2, wrong VALUE, and off by a factor of 16 in
    // magnitude. That is exactly the class of bug that shows up as a reject
    // rate rather than an exception.
    [Theory]
    [InlineData("abc", new byte[] { 0x0a, 0xbc })]
    [InlineData("1", new byte[] { 0x01 })]
    [InlineData("fff", new byte[] { 0x0f, 0xff })]
    public void DecodeLeftPadsOddLengthPreservingMagnitude(string hex, byte[] expected)
        => Assert.Equal(expected, Hex.Decode(hex));

    [Fact]
    public void DecodeRejectsNonHex() => Assert.Throws<FormatException>(() => Hex.Decode("zz"));

    [Fact]
    public void EncodeIsLowercaseAndRoundTrips()
    {
        var bytes = new byte[] { 0x00, 0x0f, 0xa5, 0xff };
        Assert.Equal("000fa5ff", Hex.Encode(bytes));
        Assert.Equal(bytes, Hex.Decode(Hex.Encode(bytes)));
    }

    // ------------------------------------------------------------ Sha2 ---

    // sha256d("") — the standard double-SHA256 of the empty string.
    [Fact]
    public void Sha256dOfEmptyMatchesTheKnownVector()
        => Assert.Equal(
            "5df6e0e2761359d30a8275058e299fcc0381534545f55cf43e41983f5d4c9456",
            Hex.Encode(Sha2.Sha256d(ReadOnlySpan<byte>.Empty)));

    // sha256d("abc").
    [Fact]
    public void Sha256dOfAbcMatchesTheKnownVector()
        => Assert.Equal(
            "4f8b42c22dd3729b519ba6f68d2da7cc5b2d606d05daed5ad5128cc03e6c6358",
            Hex.Encode(Sha2.Sha256d("abc"u8)));

    // --------------------------------------------------------- Uint256 ---

    [Fact]
    public void EqualClearsTheTargetInBothOrders()
    {
        var v = new byte[32];
        v[5] = 0x42;
        Assert.True(Uint256.LeLessOrEqual(v, v));
        Assert.True(Uint256.BeLessOrEqual(v, v));
    }

    // The two orders must DISAGREE on this pair — that is the whole reason
    // they are separate methods. a has its high byte at index 31 (big in LE,
    // small in BE); b has it at index 0 (small in LE, big in BE).
    [Fact]
    public void LittleAndBigEndianDisagreeOnTheSamePair()
    {
        var a = new byte[32]; a[31] = 0x01;
        var b = new byte[32]; b[0] = 0x01;

        Assert.False(Uint256.LeLessOrEqual(a, b));  // LE: a = 2^248, b = 1
        Assert.True(Uint256.BeLessOrEqual(a, b));   // BE: a = 1, b = 2^248
    }

    [Fact]
    public void LeMatchesBigIntegerOverRandomPairs()
    {
        var rng = new Random(20260803);
        var a = new byte[32];
        var b = new byte[32];
        for (int i = 0; i < 500; i++)
        {
            rng.NextBytes(a);
            rng.NextBytes(b);
            var ba = new BigInteger(a, isUnsigned: true, isBigEndian: false);
            var bb = new BigInteger(b, isUnsigned: true, isBigEndian: false);
            Assert.Equal(ba <= bb, Uint256.LeLessOrEqual(a, b));
        }
    }

    [Fact]
    public void BeMatchesBigIntegerOverRandomPairs()
    {
        var rng = new Random(20260804);
        var a = new byte[32];
        var b = new byte[32];
        for (int i = 0; i < 500; i++)
        {
            rng.NextBytes(a);
            rng.NextBytes(b);
            var ba = new BigInteger(a, isUnsigned: true, isBigEndian: true);
            var bb = new BigInteger(b, isUnsigned: true, isBigEndian: true);
            Assert.Equal(ba <= bb, Uint256.BeLessOrEqual(a, b));
        }
    }
}
