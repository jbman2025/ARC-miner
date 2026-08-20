// Host-side header assembly and target arithmetic for BitcoinIII (BC3),
// --algo sha3t. The PoW is SHA3-256 applied three times to the canonical
// 80-byte Bitcoin header; nothing else about the chain differs from Bitcoin,
// so everything here is stock apart from the digest comparison order.
//
// Verified against mainnet block 56000 (the sha3t fork is at height 30240 —
// genesis and every block below it are still sha256d and will NOT match):
//   version 0x20001000 time 1786738255 bits 0x1b048245 nonce 723721353
//   -> 0000000000031c1896744b33c552471dfb51a5b470f90e452a4bc8213311f37a

using System.Numerics;
using System.Security.Cryptography;

namespace Akoya.Miner.Algos.Sha3t;

internal static class Sha3tHash
{
    public static byte[] Unhex(string s) => Akoya.Crypto.Hex.Decode(s);

    public static string Hex(ReadOnlySpan<byte> b) => Akoya.Crypto.Hex.Encode(b);

    /// <summary>Per-32-bit-word byte swap, the order a Bitcoin Stratum pool
    /// sends <c>prevhash</c> in.</summary>
    /// <remarks>
    /// This is the gr convention, NOT the csd one. csd reverses all 32 bytes;
    /// applied to a BC3 prevhash that produces a header pointing at a hash with
    /// its zero bytes at the wrong end, and every share is rejected. Confirmed
    /// against the live pool: notify prevhash
    /// 681693bb…000032f900000000 swab32s to the internal form of block 56501,
    /// 00000000000032f9…681693bb.
    /// A deliberate copy of <see cref="Gr.GrHash.Swab32"/>: each --algo module
    /// stays self-contained rather than growing a cross-plugin dependency for
    /// six lines.
    /// </remarks>
    public static void Swab32(Span<byte> buf)
    {
        for (int i = 0; i + 4 <= buf.Length; i += 4)
        {
            (buf[i], buf[i + 3]) = (buf[i + 3], buf[i]);
            (buf[i + 1], buf[i + 2]) = (buf[i + 2], buf[i + 1]);
        }
    }

    /// <summary>The 80-byte header as the ten little-endian u64 lanes the
    /// kernel absorbs. Lane 9 carries nbits in its low half and the nonce slot
    /// in its high half.</summary>
    public static ulong[] HeaderLanes(ReadOnlySpan<byte> header80)
    {
        if (header80.Length != 80) throw new ArgumentException("header must be 80 bytes", nameof(header80));
        var lanes = new ulong[10];
        for (int i = 0; i < 10; ++i)
            lanes[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(header80.Slice(8 * i, 8));
        return lanes;
    }

    /// <summary>SHA3-256 three times over an 80-byte header, as the four
    /// little-endian u64 lanes of the digest (index 3 most significant). The
    /// reference for the GPU kernel and for the golden tests.</summary>
    public static ulong[] Sha3t(ReadOnlySpan<byte> header80)
    {
        Span<byte> d = stackalloc byte[32];
        SHA3_256.HashData(header80, d);
        SHA3_256.HashData(d, d);
        SHA3_256.HashData(d, d);

        var lanes = new ulong[4];
        for (int i = 0; i < 4; ++i)
            lanes[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(d.Slice(8 * i, 8));
        return lanes;
    }

    /// <summary>True if the digest is at or under the target, both read as
    /// little-endian 256-bit integers — the ordering Bitcoin Core uses. Lane 3
    /// is the most significant end, which is why a winning hash DISPLAYS with
    /// leading zeros but is stored with trailing ones.</summary>
    public static bool MeetsTarget(ulong[] digest4, ulong[] target4)
    {
        for (int i = 3; i >= 1; --i)
        {
            if (digest4[i] != target4[i]) return digest4[i] < target4[i];
        }
        return digest4[0] <= target4[0];
    }

    // pdiff difficulty-1 target: 0x00000000FFFF0000…0000, i.e. 0xFFFF · 2^208.
    private static readonly BigInteger Diff1 = new BigInteger(0xFFFF) << 208;

    /// <summary>Share target for a stratum difficulty, as four little-endian
    /// u64 lanes (index 3 most significant).</summary>
    /// <remarks>
    /// Full-width division, unlike <see cref="Csd.CsdHash.PdiffTarget"/>, which
    /// only fills one 32-bit word and throws the remaining 224 bits of
    /// precision away. That is harmless at csd's integer difficulties but this
    /// pool runs vardiff and hands out fractional ones; a truncated target is
    /// silently HARDER than the pool asked for, so the miner would quietly
    /// discard shares it was owed credit for.
    /// </remarks>
    public static ulong[] PdiffTarget(double difficulty)
    {
        if (!(difficulty > 0) || double.IsNaN(difficulty)) difficulty = 1.0;

        // difficulty is a double; scale by 2^32 so fractional vardiff values
        // survive the integer division instead of truncating toward the
        // next-lower whole difficulty.
        var scaled = new BigInteger(Math.Round(difficulty * 4294967296.0));
        if (scaled <= 0) scaled = BigInteger.One;
        BigInteger t = (Diff1 << 32) / scaled;

        var max = (BigInteger.One << 256) - 1;
        if (t > max) t = max;

        var lanes = new ulong[4];
        for (int i = 0; i < 4; ++i) lanes[i] = (ulong)((t >> (64 * i)) & ulong.MaxValue);
        return lanes;
    }
}
