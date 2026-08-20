// Phase 3 safety net for GhostRider's header assembly.
//
// gr was the first algo migrated onto BitcoinStratumDialect, which means its
// coinbase+merkle now runs through shared code (BitcoinStratumJob.MerkleRoot)
// instead of a private copy. These pin the resulting header bytes so that
// migration — and every later one — either reproduces them or fails here.
//
// The selective swab32 is the fragile part and has already cost a bring-up:
// XMRig swaps 32-bit words only where (i < 36) || (i >= 68) — version,
// prevhash, ntime, nbits — and leaves the merkle root at [36,68) in its natural
// sha256d output order. Swapping the root too made every share the pool
// recomputed come out wrong. A test that only checked "some 80 bytes" would not
// have caught it; this checks the exact bytes.
//
// PROVENANCE: the expected header was computed independently in Python
// (hashlib + struct), not read back out of the C# implementation.

using Akoya.Crypto;
using Akoya.Miner.Algos.Gr;
using Akoya.Miner.Mining.Stratum;
using Xunit;

namespace Akoya.Miner.Tests;

public class GrHeaderGoldenTests
{
    private const string Coinb1 =
        "01000000010000000000000000000000000000000000000000000000000000000000000000ffffffff20";
    private const string Coinb2 =
        "ffffffff0100f2052a010000001976a914aabbccddeeff00112233445566778899aabbccdd88ac00000000";
    private const string Extranonce1 = "deadbeef";
    private const string PrevHex =
        "00000000000000000007c2b6b1e3d9f3a4c8e5d2f1a09b8c7d6e5f4a3b2c1d0e";

    private static BitcoinStratumJob MakeJob() => new(
        JobId: "abcd",
        PrevHashRaw: Hex.Decode(PrevHex),
        Coinb1: Coinb1,
        Coinb2: Coinb2,
        Branch:
        [
            "3b2c1d0e4f5a6b7c8d9e0f1a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e",
            "9e8d7c6b5a4f3e2d1c0b9a8f7e6d5c4b3a2918070605040302010f0e0d0c0b0a",
        ],
        Version: 0x20000000,
        Bits: 0x1a2b3c4d,
        Time: 0x66b1c2d3,
        NbitsHex: "1a2b3c4d",
        NtimeHex: "66b1c2d3",
        Clean: true);

    [Fact]
    public void BuildHeaderProducesTheGoldenBytes()
    {
        var header = new byte[80];
        var en2Hex = GrStratumClient.BuildHeader(MakeJob(), Extranonce1, 4, header);

        Assert.Equal("00000000", en2Hex);
        Assert.Equal(
            "000000200000000000000000b6c20700f3d9e3b1d2e5c8a48c9ba0f14a5f6e7d" +
            "0e1d2c3b40ad1b3bbb2c51f1caef86ba418cc07b3d8880d410376d722a8e9079" +
            "098e9aded3c2b1664d3c2b1a00000000",
            Hex.Encode(header));
    }

    [Fact]
    public void MerkleRootIsWrittenUnswapped()
    {
        var job = MakeJob();
        var header = new byte[80];
        GrStratumClient.BuildHeader(job, Extranonce1, 4, header);

        // The exact bytes sha256d produced, in that order — no word swap.
        var root = job.MerkleRoot(Extranonce1, "00000000");
        Assert.Equal("40ad1b3bbb2c51f1caef86ba418cc07b3d8880d410376d722a8e9079098e9ade",
                     Hex.Encode(root));
        Assert.Equal(root, header[36..68]);
    }

    // The other four fields ARE swapped. Checking them individually means a
    // regression names which field broke instead of just "80 bytes differ".
    [Fact]
    public void VersionPrevNtimeAndBitsAreWordSwapped()
    {
        var job = MakeJob();
        var header = new byte[80];
        GrStratumClient.BuildHeader(job, Extranonce1, 4, header);

        Assert.Equal("00000020", Hex.Encode(header[0..4]));                    // version, LE
        Assert.Equal("d3c2b166", Hex.Encode(header[68..72]));                  // ntime, swabbed
        Assert.Equal("4d3c2b1a", Hex.Encode(header[72..76]));                  // nbits, LE
        Assert.Equal("00000000", Hex.Encode(header[76..80]));                  // nonce slot, empty

        // prevhash: each 32-bit word reversed, words themselves in place.
        var prev = Hex.Decode(PrevHex);
        for (int i = 0; i < 32; i += 4)
        {
            Assert.Equal(prev[i + 3], header[4 + i]);
            Assert.Equal(prev[i + 2], header[4 + i + 1]);
            Assert.Equal(prev[i + 1], header[4 + i + 2]);
            Assert.Equal(prev[i + 0], header[4 + i + 3]);
        }
    }

    // GhostRider partitions work by nonce stride, never by extranonce2, so en2
    // must stay a fixed run of zeros at the pool's width. If a future refactor
    // starts rolling it here, threads would re-search the same nonce space.
    [Theory]
    [InlineData(2, "0000")]
    [InlineData(3, "000000")]
    [InlineData(4, "00000000")]
    [InlineData(8, "0000000000000000")]
    public void ExtranonceTwoIsZeroAtThePoolsWidth(int size, string expected)
    {
        var header = new byte[80];
        Assert.Equal(expected, GrStratumClient.BuildHeader(MakeJob(), Extranonce1, size, header));
    }

    [Fact]
    public void ADifferentExtranonceOneChangesTheMerkleRoot()
    {
        var job = MakeJob();
        Assert.NotEqual(job.MerkleRoot("deadbeef", "00000000"),
                        job.MerkleRoot("deadbeee", "00000000"));
    }
}
