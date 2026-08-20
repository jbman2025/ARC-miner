// 256-bit "does this hash clear the target?" comparisons.
//
// THE BYTE ORDER IS THE WHOLE POINT. A share test that reads the hash with the
// wrong endianness does not fail loudly — it accepts hashes it should reject
// and rejects hashes it should accept, and the only symptom is a reject rate
// on a pool dashboard hours later. This has already cost us once (the gr
// bring-up: big- vs little-endian target comparison).
//
// So there is no default. Callers name the order they mean:
//
//   LeLessOrEqual   rx        — Bitcoin-style, least-significant byte first
//   BeLessOrEqual   nm        — Cereblix/NeuroMorph PROTOCOL.md section 2
//
// gr keeps its own MeetsTarget: its target arrives as uint[8] rather than
// bytes, so it compares little-endian 32-bit WORDS (word 7 most significant).
// Same ordering as LeLessOrEqual, different input shape.

namespace Akoya.Crypto;

public static class Uint256
{
    /// <summary>a &lt;= b, both 32-byte LITTLE-endian integers (byte 31 most
    /// significant). Equality counts as clearing the target.</summary>
    public static bool LeLessOrEqual(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        for (int i = 31; i >= 0; --i)
        {
            if (a[i] != b[i]) return a[i] < b[i];
        }
        return true;
    }

    /// <summary>a &lt;= b, both 32-byte BIG-endian integers (byte 0 most
    /// significant). Equality counts as clearing the target.</summary>
    public static bool BeLessOrEqual(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        for (int i = 0; i < 32; i++)
        {
            if (a[i] != b[i]) return a[i] < b[i];
        }
        return true;
    }
}
