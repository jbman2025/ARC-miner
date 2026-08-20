// Monero solo-mining job helpers: block-hashing-blob layout, nonce injection,
// and the consensus difficulty check. Kept separate from the mining loop so the
// consensus-critical CheckHash is unit-testable without a GPU/CPU miner or node.

using System.Buffers.Binary;

namespace Akoya.Miner.Algos.Rx;

/// <summary>A unit of Monero solo work derived from get_block_template.</summary>
internal sealed record RxJobData(
    byte[] HashingBlob,   // per-nonce RandomX input (nonce lives at NonceOffset)
    byte[] TemplateBlob,  // full block blob submitted on a hit (nonce at NonceOffset)
    ulong  Difficulty,
    long   Height,
    string PrevHash,
    byte[] SeedHash);     // 32-byte RandomX key for this epoch

internal static class RxJob
{
    // The nonce is a 4-byte LE field in the block header, after
    // major(1)+minor(1)+timestamp(varint=5 for present-day epochs)+prev_id(32).
    // Monero miners rely on this fixed offset for both the hashing blob and the
    // template blob (same header prefix); holds while timestamps are 5-byte
    // varints (through ~2035).
    public const int NonceOffset = 39;

    public static void WriteNonce(byte[] blob, uint nonce)
        => BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(NonceOffset, 4), nonce);

    /// <summary>Monero consensus difficulty test — a faithful port of cryptonote
    /// <c>check_hash</c>: the 256-bit little-endian hash multiplied by the 64-bit
    /// difficulty must not overflow 256 bits (i.e. hash · difficulty &lt; 2^256).</summary>
    public static bool CheckHash(ReadOnlySpan<byte> hash32, ulong difficulty)
    {
        if (difficulty <= 1) return true;   // every hash passes at difficulty 0/1

        ulong w0 = BinaryPrimitives.ReadUInt64LittleEndian(hash32[..8]);
        ulong w1 = BinaryPrimitives.ReadUInt64LittleEndian(hash32.Slice(8, 8));
        ulong w2 = BinaryPrimitives.ReadUInt64LittleEndian(hash32.Slice(16, 8));
        ulong w3 = BinaryPrimitives.ReadUInt64LittleEndian(hash32.Slice(24, 8));

        UInt128 p = (UInt128)w3 * difficulty;
        if ((ulong)(p >> 64) != 0) return false;      // high word of top product must be 0
        ulong top = (ulong)p;

        p = (UInt128)w0 * difficulty;
        ulong cur = (ulong)(p >> 64);

        p = (UInt128)w1 * difficulty;
        ulong low = (ulong)p, high = (ulong)(p >> 64);
        bool carry = Cadc(cur, low, false);
        cur = high;

        p = (UInt128)w2 * difficulty;
        low = (ulong)p; high = (ulong)(p >> 64);
        carry = Cadc(cur, low, carry);
        carry = Cadc(high, top, carry);
        return !carry;
    }

    // Carry out of a + b + carryIn (does not need the sum itself).
    private static bool Cadc(ulong a, ulong b, bool carryIn)
        => (UInt128)a + b + (carryIn ? 1UL : 0UL) > ulong.MaxValue;
}
