// Phase 0 safety net for CSD (Compute Substrate, Bitcoin Stratum V1 / sha256d).
//
// WHY THIS FILE EXISTS: Phase 3 of docs/SLIM-PLAN.md moved CsdStratumClient onto
// the shared BitcoinStratumDialect, and before this file csd had ZERO test
// coverage. A share whose header is assembled one byte differently is not a
// crash — it is a reject that shows up on a pool dashboard hours later. These
// pin the exact bytes, and they still hold AFTER that migration: the golden
// midstate and tail below are unchanged from the pre-refactor implementation.
//
// PROVENANCE — these are NOT self-generated expectations. Every value below was
// cross-checked against an independent Python implementation (hashlib + a
// from-spec SHA-256 compressor) before being written down:
//   • the 64-zero-byte midstate matches the from-spec compressor, and that
//     compressor was itself validated against hashlib.sha256 on a 64-byte
//     message (compress-then-pad == full digest);
//   • the coinbase/merkle/header pipeline was recomputed end to end in Python;
//   • pdiff targets match the closed form 0xFFFF0000/diff.
// So a failure here means behaviour CHANGED, not merely that it differs from
// whatever the code happened to do the day the test was written.
//
// STILL NOT COVERED: the submit-path nonce byte flip, which lives inline in
// CsdStratumClient.SolverLoop. The rule is: the kernel hashes big-endian(w) and
// the submit hex is the little-endian spelling of the same w.
//
// The share-attribution FIFO this file used to warn about is GONE — Phase 3
// replaced it with an id-correlated awaited submit, so there is no arrival-order
// invariant left to break.

using Akoya.Crypto;
using Akoya.Miner.Algos.Csd;
using Akoya.Miner.Mining.Stratum;
using Xunit;

namespace Akoya.Miner.Tests;

public class CsdGoldenVectorTests
{
    // A realistic mining.notify, in the shape documented at the top of
    // CsdStratumClient: raw 32-byte prevhash exactly as the pool sends it, two
    // merkle branch elements, u64 ntime.
    private const string Coinb1 =
        "01000000010000000000000000000000000000000000000000000000000000000000000000ffffffff20";
    private const string Coinb2 =
        "ffffffff0100f2052a010000001976a914aabbccddeeff00112233445566778899aabbccdd88ac00000000";
    private const string Extranonce1 = "deadbeef";

    private static BitcoinStratumJob MakeJob() => new(
        JobId: "6a1f",
        // RAW, unreversed: the shared job keeps the pool's bytes and
        // CsdStratumClient.Rebuild does the 32-byte reversal itself.
        PrevHashRaw: Hex.Decode("00000000000000000007c2b6b1e3d9f3a4c8e5d2f1a09b8c7d6e5f4a3b2c1d0e"),
        Coinb1: Coinb1,
        Coinb2: Coinb2,
        Branch:
        [
            "3b2c1d0e4f5a6b7c8d9e0f1a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e",
            "9e8d7c6b5a4f3e2d1c0b9a8f7e6d5c4b3a2918070605040302010f0e0d0c0b0a",
        ],
        Version: 0x20000000,
        Bits: 0x1a2b3c4d,
        Time: 0x0000000066b1c2d3,
        NbitsHex: "1a2b3c4d",
        NtimeHex: "0000000066b1c2d3",
        Clean: true);

    // ------------------------------------------------- the whole pipeline ---

    [Fact]
    public void RebuildProducesTheGoldenMidstateAndTail()
    {
        var (mid, tail, en2Hex) = CsdStratumClient.Rebuild(MakeJob(), Extranonce1, 4, 7u);

        Assert.Equal("00000007", en2Hex);

        Assert.Equal(
            new uint[] { 0xc27cf52b, 0x447b31f9, 0x1f460b22, 0xc6b642dd,
                         0xcff86584, 0x302c7832, 0x4d4f2738, 0xf6373e03 },
            mid);

        // tail[0] = last 4 bytes of the merkle root, [1]/[2] = u64 ntime read as
        // two big-endian words off a little-endian field, [3] = nbits likewise,
        // [4] = the nonce slot the kernel fills.
        Assert.Equal(
            new uint[] { 0xaa422b12, 0xd3c2b166, 0x00000000, 0x4d3c2b1a, 0x00000000 },
            tail);
    }

    [Fact]
    public void CoinbaseIsCoinb1PlusExtranonce1PlusExtranonce2PlusCoinb2()
    {
        var (_, _, en2Hex) = CsdStratumClient.Rebuild(MakeJob(), Extranonce1, 4, 7u);
        var coinbase = Hex.Decode(Coinb1 + Extranonce1 + en2Hex + Coinb2);

        Assert.Equal(
            "7f50f7db760f9b157446079b866367e2e38d53c951676d96153a8e20769d0533",
            Hex.Encode(Sha2.Sha256d(coinbase)));
    }

    // The extranonce2 counter is what partitions work between GPUs, so its hex
    // width has to track the pool's extranonce2_size exactly — too wide and the
    // coinbase is malformed, too narrow and two devices collide.
    [Theory]
    [InlineData(4, 7u, "00000007")]
    [InlineData(3, 7u, "000007")]
    [InlineData(2, 7u, "0007")]
    [InlineData(4, 0xdeadbeefu, "deadbeef")]
    [InlineData(2, 0xdeadbeefu, "beef")]
    public void ExtranonceTwoIsClampedToThePoolsWidth(int size, uint en2, string expected)
    {
        var (_, _, en2Hex) = CsdStratumClient.Rebuild(MakeJob(), Extranonce1, size, en2);
        Assert.Equal(expected, en2Hex);
    }

    [Fact]
    public void ADifferentExtranonceTwoChangesTheMidstate()
    {
        var (midA, _, _) = CsdStratumClient.Rebuild(MakeJob(), Extranonce1, 4, 7u);
        var (midB, _, _) = CsdStratumClient.Rebuild(MakeJob(), Extranonce1, 4, 8u);
        Assert.NotEqual(midA, midB);
    }

    // --------------------------------------------------------- midstate ---

    // The most-cited SHA-256 midstate constant: the state after compressing 64
    // zero bytes. Recomputed from FIPS 180-4 in Python, not copied from a blog.
    [Fact]
    public void MidstateOfSixtyFourZeroBytesMatchesTheSpec()
        => Assert.Equal(
            new uint[] { 0xda5698be, 0x17b9b469, 0x62335799, 0x779fbeca,
                         0x8ce5d491, 0xc0d26243, 0xbafef9ea, 0x1837a9d8 },
            CsdHash.Midstate(new byte[64]));

    [Fact]
    public void MidstateIsDeterministicAndInputSensitive()
    {
        var block = new byte[64];
        for (int i = 0; i < 64; i++) block[i] = (byte)i;

        Assert.Equal(CsdHash.Midstate(block), CsdHash.Midstate(block));

        block[63] ^= 0x01;
        Assert.NotEqual(CsdHash.Midstate(new byte[64]), CsdHash.Midstate(block));
    }

    [Fact]
    public void Be32ReadsBigEndian()
        => Assert.Equal(0x01020304u, CsdHash.Be32(new byte[] { 0x01, 0x02, 0x03, 0x04 }));

    // ----------------------------------------------------- pdiff target ---

    // pdiff: difficulty-1 is 0xFFFF0000 in word 1, everything else zero.
    [Theory]
    [InlineData(1.0, 0xffff0000u)]
    [InlineData(2.0, 0x7fff8000u)]
    [InlineData(1024.0, 0x003fffc0u)]
    [InlineData(65536.0, 0x0000ffffu)]
    public void PdiffTargetMatchesTheClosedForm(double diff, uint expectedWord1)
    {
        var t = CsdHash.PdiffTarget(diff);
        Assert.Equal(expectedWord1, t[1]);
        Assert.Equal(0u, t[0]);
        for (int i = 2; i < 8; i++) Assert.Equal(0u, t[i]);
    }

    // A pool that sends difficulty 0 (or negative, which some do on reconnect)
    // must not produce an all-zero target — that rejects every share found.
    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void PdiffTargetTreatsNonPositiveDifficultyAsOne(double diff)
        => Assert.Equal(CsdHash.PdiffTarget(1.0), CsdHash.PdiffTarget(diff));

    [Fact]
    public void PdiffTargetShrinksAsDifficultyRises()
    {
        double[] diffs = [1, 4, 64, 1024, 65536, 1e6];
        for (int i = 1; i < diffs.Length; i++)
        {
            Assert.True(CsdHash.PdiffTarget(diffs[i])[1] < CsdHash.PdiffTarget(diffs[i - 1])[1],
                $"target did not shrink from diff {diffs[i - 1]} to {diffs[i]}");
        }
    }

    // ------------------------------------------------------------ hex ---

    [Fact]
    public void UnhexAndHexRoundTrip()
    {
        const string h = "00ff10a5deadbeef";
        Assert.Equal(h, CsdHash.Hex(CsdHash.Unhex(h)));
    }

}
