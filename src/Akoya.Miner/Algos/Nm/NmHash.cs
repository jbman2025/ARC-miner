// Target/difficulty helpers for NeuroMorph (nm/1, Cereblix).
//
// NOTE the byte-order difference from RandomX, which is the easiest thing to get
// wrong when adapting the Rx code: NeuroMorph's target is a **256-bit BIG-endian**
// threshold, and a hash is valid iff `memcmp(hash, target) <= 0` — byte 0 is the
// most significant. RandomX/Monero compares little-endian (byte 31 most
// significant) against a compact target. Using the Rx comparison here silently
// accepts the wrong hashes and every share is rejected as "low diff share".

using System.Numerics;
using Akoya.Crypto;

namespace Akoya.Miner.Algos.Nm;

internal static class NmHash
{
    /// <summary>True iff <paramref name="hash"/> &lt;= <paramref name="target"/>,
    /// both read as 256-bit big-endian integers (PROTOCOL.md section 2).</summary>
    public static bool MeetsTarget(ReadOnlySpan<byte> hash, ReadOnlySpan<byte> target)
        => Uint256.BeLessOrEqual(hash, target);

    /// <summary>Difficulty implied by a 256-bit big-endian target: 2^256 / target.
    /// Display only — share validity always goes through <see cref="MeetsTarget"/>.</summary>
    public static double DifficultyOf(ReadOnlySpan<byte> target)
    {
        var t = new BigInteger(target, isUnsigned: true, isBigEndian: true);
        if (t.IsZero) return 0;

        // 2^256 as an unsigned big-endian value.
        Span<byte> dividendBytes = stackalloc byte[33];
        dividendBytes.Clear();
        dividendBytes[0] = 0x01;
        var dividend = new BigInteger(dividendBytes, isUnsigned: true, isBigEndian: true);

        return (double)(dividend / t);
    }

    /// <summary>Parse the pool's hex target into 32 big-endian bytes. Cereblix
    /// sends a full 64-hex target; a shorter one is left-padded with zeros so it
    /// keeps its magnitude (a short target means a SMALLER number, i.e. harder).</summary>
    public static byte[] ParseTarget(string targetHex)
    {
        var bytes = Convert.FromHexString(targetHex);
        if (bytes.Length == 32) return bytes;

        var target = new byte[32];
        if (bytes.Length < 32)
        {
            Array.Copy(bytes, 0, target, 32 - bytes.Length, bytes.Length);
        }
        else
        {
            Array.Copy(bytes, bytes.Length - 32, target, 0, 32);
        }
        return target;
    }
}
