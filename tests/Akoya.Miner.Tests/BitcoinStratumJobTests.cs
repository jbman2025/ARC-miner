// The shared Bitcoin-Stratum job model extracted in Phase 3.
//
// MerkleRoot and FormatExtranonce2 replaced per-algo copies in gr and csd, so
// they are now on the path of every share both algos submit — and of every
// future algo in that family. The csd and gr golden-vector suites pin the
// end-to-end results; these cover the shared piece directly, including the
// odd-level and width-clamp cases a single golden value would miss.

using Akoya.Crypto;
using Akoya.Miner.Mining.Stratum;
using Xunit;

namespace Akoya.Miner.Tests;

public class BitcoinStratumJobTests
{
    private const string Coinb1 = "0100000001abcdef";
    private const string Coinb2 = "ffffffff0100f2052a01000000000000";

    private static BitcoinStratumJob Job(params string[] branch) => new(
        JobId: "j", PrevHashRaw: new byte[32], Coinb1: Coinb1, Coinb2: Coinb2,
        Branch: branch, Version: 0x20000000, Bits: 0x1a2b3c4d, Time: 0x66b1c2d3,
        NbitsHex: "1a2b3c4d", NtimeHex: "66b1c2d3", Clean: true);

    // With no branch the root is just sha256d of the assembled coinbase.
    [Fact]
    public void EmptyBranchIsSha256dOfTheCoinbase()
    {
        var expected = Sha2.Sha256d(Hex.Decode(Coinb1 + "aabbccdd" + "00000001" + Coinb2));
        Assert.Equal(expected, Job().MerkleRoot("aabbccdd", "00000001"));
    }

    // Each branch element folds on the RIGHT: root = sha256d(root ‖ node).
    // Folding on the left instead still produces 32 plausible bytes and a 100%
    // reject rate, so the order is worth pinning explicitly.
    [Fact]
    public void BranchFoldsOnTheRight()
    {
        const string node = "3b2c1d0e4f5a6b7c8d9e0f1a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e";

        var coinbaseHash = Sha2.Sha256d(Hex.Decode(Coinb1 + "aabbccdd" + "00000001" + Coinb2));
        var pair = new byte[64];
        coinbaseHash.CopyTo(pair, 0);
        Hex.Decode(node).CopyTo(pair, 32);

        Assert.Equal(Sha2.Sha256d(pair), Job(node).MerkleRoot("aabbccdd", "00000001"));
    }

    [Fact]
    public void BranchOrderMatters()
    {
        const string a = "1111111111111111111111111111111111111111111111111111111111111111";
        const string b = "2222222222222222222222222222222222222222222222222222222222222222";
        Assert.NotEqual(Job(a, b).MerkleRoot("aabbccdd", "00000001"),
                        Job(b, a).MerkleRoot("aabbccdd", "00000001"));
    }

    [Fact]
    public void ExtranonceTwoAndExtranonceOneBothChangeTheRoot()
    {
        var job = Job();
        var baseline = job.MerkleRoot("aabbccdd", "00000001");
        Assert.NotEqual(baseline, job.MerkleRoot("aabbccdd", "00000002"));
        Assert.NotEqual(baseline, job.MerkleRoot("aabbccde", "00000001"));
    }

    // The pool dictates extranonce2_size in its subscribe reply. Emitting a
    // wider value corrupts the coinbase; a narrower one lets two devices
    // collide on the same coinbase.
    [Theory]
    [InlineData(7u, 4, "00000007")]
    [InlineData(7u, 3, "000007")]
    [InlineData(7u, 2, "0007")]
    [InlineData(7u, 1, "07")]
    [InlineData(0xdeadbeefu, 4, "deadbeef")]
    [InlineData(0xdeadbeefu, 2, "beef")]
    [InlineData(0xdeadbeefu, 1, "ef")]
    public void ExtranonceTwoIsClampedToThePoolsWidth(uint counter, int size, string expected)
        => Assert.Equal(expected, BitcoinStratumJob.FormatExtranonce2(counter, size));

    // Some pools ask for more than 4 bytes — btc3forge, the BC3 pool, asks for
    // 8. A u32 counter cannot fill that, but it must still OCCUPY the full
    // width: coinb1's length byte covers extranonce1 ‖ extranonce2, so a short
    // extranonce2 shifts coinb2 and the pool folds a different merkle root.
    // That is 100% rejects, silently. Left-pad to the requested width.
    [Theory]
    [InlineData(0xdeadbeefu, 8, "00000000deadbeef")]
    [InlineData(0xdeadbeefu, 6, "0000deadbeef")]
    [InlineData(1u, 8, "0000000000000001")]
    public void ExtranonceTwoWiderThanFourBytesIsLeftPaddedNotTruncated(uint counter, int size, string expected)
    {
        var hex = BitcoinStratumJob.FormatExtranonce2(counter, size);
        Assert.Equal(size * 2, hex.Length);
        Assert.Equal(expected, hex);
    }

    [Fact]
    public void MerkleRootDoesNotMutateTheJob()
    {
        var job = Job("1111111111111111111111111111111111111111111111111111111111111111");
        var first = job.MerkleRoot("aabbccdd", "00000001");
        var second = job.MerkleRoot("aabbccdd", "00000001");
        Assert.Equal(first, second);
    }
}
