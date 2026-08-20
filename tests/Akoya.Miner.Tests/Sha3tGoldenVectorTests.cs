// Golden vectors for BitcoinIII (BC3, --algo sha3t).
//
// PROVENANCE — none of these are self-generated expectations:
//   • the hash vector is REAL MAINNET BLOCK 56000, pulled from a BC3 node's
//     RPC via the ArgfaMining explorer, and reproduced by Python
//     hashlib.sha3_256 applied three times before being written down. It is
//     post-fork on purpose: the sha3t fork activates at height 30240 and
//     genesis is still sha256d, so a pre-fork block would not match.
//   • the prevhash byte order is pinned by a LIVE mining.notify captured from
//     btc3forge.com:3337, whose prevhash field swab32s to the internal form of
//     the real block 56501 — that is what proves swab32 and not csd's
//     whole-32-byte reversal.
//   • the pdiff targets are checked against the closed form 0xFFFF·2^208 / d.
//
// What a failure here means: the header assembly or the digest ordering
// CHANGED. Neither is a crash — both are a pool full of rejects hours later.

using System.Numerics;
using Akoya.Crypto;
using Akoya.Miner.Algos.Sha3t;
using Akoya.Miner.Mining.Stratum;
using Xunit;

namespace Akoya.Miner.Tests;

public class Sha3tGoldenVectorTests
{
    // ------------------------------------------------ the real block ---

    private const string Block56000Hash = "0000000000031c1896744b33c552471dfb51a5b470f90e452a4bc8213311f37a";
    private const string Block56000Prev = "00000000000213a25516a1c0a19bb94e0cba10e7c18c8999b9a73ee029cd4267";
    private const string Block56000Merkle = "8ad442807e25fcb71d29c32045e3633eee02ca4d758615006f6b91aa9b16723c";
    private const uint Block56000Version = 536875008u;   // 0x20001000
    private const uint Block56000Time = 1786738255u;
    private const uint Block56000Bits = 0x1b048245u;
    private const uint Block56000Nonce = 723721353u;

    // A displayed (reversed) hash in the byte order the header stores it.
    private static byte[] Internal(string displayed)
    {
        var b = Hex.Decode(displayed);
        Array.Reverse(b);
        return b;
    }

    private static byte[] Block56000Header(uint nonce = Block56000Nonce)
    {
        var h = new byte[80];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(0, 4), Block56000Version);
        Internal(Block56000Prev).CopyTo(h, 4);
        Internal(Block56000Merkle).CopyTo(h, 36);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(68, 4), Block56000Time);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(72, 4), Block56000Bits);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(76, 4), nonce);
        return h;
    }

    // The digest lanes rendered the way a block explorer shows the hash.
    private static string Displayed(ulong[] lanes4)
    {
        var b = new byte[32];
        for (int i = 0; i < 4; ++i)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8 * i, 8), lanes4[i]);
        Array.Reverse(b);
        return Hex.Encode(b);
    }

    [Fact]
    public void Sha3tReproducesMainnetBlock56000()
    {
        Assert.Equal(Block56000Hash, Displayed(Sha3tHash.Sha3t(Block56000Header())));
    }

    // Three iterations, not two and not SHA3d. Getting the count wrong still
    // produces a plausible-looking 32-byte digest, which is exactly why the
    // check has to be against a real block rather than against itself.
    [Fact]
    public void OneAndTwoIterationsDoNotMatchTheBlock()
    {
        var hdr = Block56000Header();
        var once = System.Security.Cryptography.SHA3_256.HashData(hdr);
        var twice = System.Security.Cryptography.SHA3_256.HashData(once);
        Array.Reverse(once);
        Array.Reverse(twice);
        Assert.NotEqual(Block56000Hash, Hex.Encode(once));
        Assert.NotEqual(Block56000Hash, Hex.Encode(twice));
    }

    [Fact]
    public void AChangedNonceChangesTheHash()
    {
        Assert.NotEqual(
            Displayed(Sha3tHash.Sha3t(Block56000Header())),
            Displayed(Sha3tHash.Sha3t(Block56000Header(Block56000Nonce + 1))));
    }

    // ------------------------------------------------- header lanes ---

    // The kernel absorbs the header as ten little-endian u64s and patches the
    // nonce into lane 9's HIGH half; nbits keeps the low half. If those two
    // ever swap, every hash is wrong and nothing else notices.
    [Fact]
    public void HeaderLaneNineIsBitsLowAndNonceHigh()
    {
        var lanes = Sha3tHash.HeaderLanes(Block56000Header());
        Assert.Equal(10, lanes.Length);
        Assert.Equal(Block56000Bits, (uint)lanes[9]);
        Assert.Equal(Block56000Nonce, (uint)(lanes[9] >> 32));
        // lane 0 is version | first 4 bytes of prevhash
        Assert.Equal(Block56000Version, (uint)lanes[0]);
    }

    [Fact]
    public void HeaderLanesRejectsAWrongLength()
        => Assert.Throws<ArgumentException>(() => Sha3tHash.HeaderLanes(new byte[84]));

    // ------------------------------------------------ target ordering ---

    // The digest is a LITTLE-endian 256-bit integer: lane 3 is the most
    // significant end. Compare from the other end and every hash looks like a
    // winner, which is the classic way a new Bitcoin-family algo floods a pool
    // with rejects on its first run.
    [Fact]
    public void MeetsTargetComparesLaneThreeFirst()
    {
        ulong[] target = [0, 0, 0, 0x0000000000040000UL];
        ulong[] under = [ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, 0x000000000003ffffUL];
        ulong[] over = [0, 0, 0, 0x0000000000040001UL];

        Assert.True(Sha3tHash.MeetsTarget(under, target));
        Assert.False(Sha3tHash.MeetsTarget(over, target));
        Assert.True(Sha3tHash.MeetsTarget(target, target));   // equality wins
    }

    [Fact]
    public void MeetsTargetFallsThroughToTheLowerLanes()
    {
        ulong[] target = [100, 0, 0, 5];
        Assert.True(Sha3tHash.MeetsTarget([99, 0, 0, 5], target));
        Assert.False(Sha3tHash.MeetsTarget([101, 0, 0, 5], target));
        Assert.False(Sha3tHash.MeetsTarget([0, 1, 0, 5], target));
    }

    [Fact]
    public void TheRealBlockMeetsItsOwnNbitsTarget()
    {
        // nBits 0x1b048245 -> 0x048245 · 2^(8·(0x1b-3)), which the explorer
        // renders as target 0000000000048245000000…
        var target = new ulong[4];
        BigInteger t = new BigInteger(0x048245) << (8 * (0x1b - 3));
        for (int i = 0; i < 4; ++i) target[i] = (ulong)((t >> (64 * i)) & ulong.MaxValue);

        Assert.True(Sha3tHash.MeetsTarget(Sha3tHash.Sha3t(Block56000Header()), target));
        // ... and a nonce one higher does not, so the check is not vacuous.
        Assert.False(Sha3tHash.MeetsTarget(Sha3tHash.Sha3t(Block56000Header(Block56000Nonce + 1)), target));
    }

    // -------------------------------------------------- pdiff target ---

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.7)]      // vardiff hands out fractional difficulties
    [InlineData(2.0)]
    [InlineData(256.0)]
    [InlineData(65536.0)]
    [InlineData(1e6)]
    public void PdiffTargetMatchesTheClosedForm(double diff)
    {
        BigInteger want = ((new BigInteger(0xFFFF) << 208) << 32) / new BigInteger(Math.Round(diff * 4294967296.0));
        var lanes = Sha3tHash.PdiffTarget(diff);

        BigInteger got = 0;
        for (int i = 3; i >= 0; --i) got = (got << 64) | lanes[i];
        Assert.Equal(want, got);
    }

    // Difficulty 1 is the pdiff-1 target: 0x00000000FFFF0000 in the top lane
    // and zeros below.
    [Fact]
    public void PdiffTargetOfOneIsTheClassicDiffOneTarget()
    {
        var t = Sha3tHash.PdiffTarget(1.0);
        Assert.Equal(0x00000000FFFF0000UL, t[3]);
        Assert.Equal(0UL, t[2]);
        Assert.Equal(0UL, t[1]);
        Assert.Equal(0UL, t[0]);
    }

    // The csd sibling fills ONE 32-bit word and drops the other 224 bits. This
    // pool runs vardiff and hands out fractional difficulties, where that
    // rounding makes the target quietly HARDER than the pool asked for and the
    // miner throws away shares it was owed credit for.
    [Fact]
    public void PdiffTargetKeepsPrecisionOnFractionalDifficulty()
    {
        var a = Sha3tHash.PdiffTarget(1.7);

        // 1.7 sits strictly between the diff-1 and diff-2 targets rather than
        // collapsing onto either...
        Assert.True(a[3] < Sha3tHash.PdiffTarget(1.0)[3]);
        Assert.True(a[3] > Sha3tHash.PdiffTarget(2.0)[3]);
        // ...and all three lanes below the top actually carry the remainder,
        // which is precisely what a 32-bit-word-only target throws away.
        Assert.NotEqual(0UL, a[2]);
        Assert.NotEqual(0UL, a[1]);
        Assert.NotEqual(0UL, a[0]);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void PdiffTargetTreatsNonPositiveDifficultyAsOne(double diff)
        => Assert.Equal(Sha3tHash.PdiffTarget(1.0), Sha3tHash.PdiffTarget(diff));

    [Fact]
    public void PdiffTargetShrinksAsDifficultyRises()
    {
        double[] diffs = [1, 4, 64, 1024, 65536, 1e6];
        for (int i = 1; i < diffs.Length; i++)
            Assert.True(Sha3tHash.PdiffTarget(diffs[i])[3] < Sha3tHash.PdiffTarget(diffs[i - 1])[3],
                $"target did not shrink from diff {diffs[i - 1]} to {diffs[i]}");
    }

    // ------------------------------------------------------- prevhash ---

    // Captured live from btc3forge.com:3337. The pool's prevhash field must
    // swab32 (per 32-bit word) into the internal form of block 56501 — the
    // whole-32-byte reversal csd uses puts the zero bytes at the wrong end and
    // rejects every share.
    private const string LiveNotifyPrevHash =
        "681693bb1af11522db4e5d26c2d5e477d5a4bb1fc3206185000032f900000000";
    private const string Block56501Hash =
        "00000000000032f9c3206185d5a4bb1fc2d5e477db4e5d261af11522681693bb";

    [Fact]
    public void SwabThirtyTwoTurnsTheLiveNotifyPrevhashIntoBlock56501()
    {
        var prev = Hex.Decode(LiveNotifyPrevHash);
        Sha3tHash.Swab32(prev);
        Array.Reverse(prev);                       // internal -> displayed
        Assert.Equal(Block56501Hash, Hex.Encode(prev));
    }

    [Fact]
    public void WholeArrayReversalIsTheWrongConventionHere()
    {
        var prev = Hex.Decode(LiveNotifyPrevHash);
        Array.Reverse(prev);
        Array.Reverse(prev);                       // csd's convention, undone
        Assert.NotEqual(Block56501Hash, Hex.Encode(prev));
    }

    [Fact]
    public void SwabThirtyTwoIsItsOwnInverse()
    {
        var a = Hex.Decode(LiveNotifyPrevHash);
        var b = (byte[])a.Clone();
        Sha3tHash.Swab32(b);
        Sha3tHash.Swab32(b);
        Assert.Equal(a, b);
    }

    // -------------------------------------------------------- rebuild ---

    // A realistic mining.notify in the shape btc3forge sends: 8-byte
    // extranonce2, u32 ntime, one merkle branch element.
    private const string Coinb1 =
        "01000000010000000000000000000000000000000000000000000000000000000000000000ffffffff3503b6dc000004f29e806a045ef916220c";
    private const string Coinb2 =
        "0a636b706f6f6c112f42433320466f7267652050504c4e532fffffffff0122040062a010000001600143d0556e715d988b02c2833d90ad8e19371d0639600000000";
    private const string Extranonce1 = "685d806a";

    private static BitcoinStratumJob LiveJob() => new(
        JobId: "6a805c250000026f",
        PrevHashRaw: Hex.Decode(LiveNotifyPrevHash),
        Coinb1: Coinb1,
        Coinb2: Coinb2,
        Branch: ["c81417e36f6dd6351f7dd077f9a86ad6dc50da95aa0514df29b4580f4426a667"],
        Version: 0x20001000,
        Bits: 0x1b01a936,
        Time: 0x6a809ef2,
        NbitsHex: "1b01a936",
        NtimeHex: "6a809ef2",
        Clean: true);

    [Fact]
    public void RebuildPlacesEveryHeaderFieldAtItsBitcoinOffset()
    {
        var (hdr, lanes, en2Hex) = Sha3tStratumClient.Rebuild(LiveJob(), Extranonce1, 8, 7u);

        // 80 bytes, not csd's 84 — BC3 is a stock Bitcoin header.
        Assert.Equal(80, hdr.Length);
        Assert.Equal(10, lanes.Length);

        Assert.Equal("00100020", Hex.Encode(hdr.AsSpan(0, 4)));           // version 0x20001000 LE
        var prev = Hex.Decode(LiveNotifyPrevHash);
        Sha3tHash.Swab32(prev);
        Assert.Equal(Hex.Encode(prev), Hex.Encode(hdr.AsSpan(4, 32)));
        Assert.Equal(Hex.Encode(LiveJob().MerkleRoot(Extranonce1, en2Hex)), Hex.Encode(hdr.AsSpan(36, 32)));
        Assert.Equal("f29e806a", Hex.Encode(hdr.AsSpan(68, 4)));           // ntime LE
        Assert.Equal("36a9011b", Hex.Encode(hdr.AsSpan(72, 4)));           // nbits LE
        Assert.Equal("00000000", Hex.Encode(hdr.AsSpan(76, 4)));           // nonce slot left clear
    }

    // The pool asks for 8 bytes of extranonce2 and coinb1's length byte counts
    // on getting them; 4 would shift coinb2 and change the merkle root.
    [Fact]
    public void RebuildFillsTheFullEightByteExtranonceTwo()
    {
        var (_, _, en2Hex) = Sha3tStratumClient.Rebuild(LiveJob(), Extranonce1, 8, 7u);
        Assert.Equal("0000000000000007", en2Hex);
    }

    [Fact]
    public void RebuildDoesNotMutateTheJobsPrevhash()
    {
        var job = LiveJob();
        var before = (byte[])job.PrevHashRaw.Clone();
        Sha3tStratumClient.Rebuild(job, Extranonce1, 8, 1u);
        Sha3tStratumClient.Rebuild(job, Extranonce1, 8, 2u);
        Assert.Equal(before, job.PrevHashRaw);
    }

    [Fact]
    public void ADifferentExtranonceTwoChangesTheMerkleRootAndSoTheHeader()
    {
        var (a, _, _) = Sha3tStratumClient.Rebuild(LiveJob(), Extranonce1, 8, 7u);
        var (b, _, _) = Sha3tStratumClient.Rebuild(LiveJob(), Extranonce1, 8, 8u);
        Assert.NotEqual(Hex.Encode(a.AsSpan(36, 32)), Hex.Encode(b.AsSpan(36, 32)));
    }

    [Fact]
    public void RebuildIsDeterministic()
    {
        var (a, la, ea) = Sha3tStratumClient.Rebuild(LiveJob(), Extranonce1, 8, 7u);
        var (b, lb, eb) = Sha3tStratumClient.Rebuild(LiveJob(), Extranonce1, 8, 7u);
        Assert.Equal(a, b);
        Assert.Equal(la, lb);
        Assert.Equal(ea, eb);
    }
}
