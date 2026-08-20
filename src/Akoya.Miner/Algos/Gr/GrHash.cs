// Host-side helpers for GhostRider (Raptoreum) stratum: hex, sha256d (for the
// coinbase + merkle fold), stratum difficulty->target conversion (diff_to_target),
// swab32 word swapping, and the 256-bit share test.

using System.Buffers.Binary;
using Akoya.Crypto;

namespace Akoya.Miner.Algos.Gr;

internal static class GrHash
{
    public static byte[] Unhex(string s) => Akoya.Crypto.Hex.Decode(s);

    public static string Hex(ReadOnlySpan<byte> b) => Akoya.Crypto.Hex.Encode(b);

    public static byte[] Sha256d(ReadOnlySpan<byte> data) => Sha2.Sha256d(data);

    /// <summary>Per-32-bit word byte swap (swab32 / bswap32) for GhostRider block headers.</summary>
    public static void Swab32(Span<byte> buf)
    {
        for (int i = 0; i + 4 <= buf.Length; i += 4)
        {
            (buf[i], buf[i + 3]) = (buf[i + 3], buf[i]);
            (buf[i + 1], buf[i + 2]) = (buf[i + 2], buf[i + 1]);
        }
    }

    /// <summary>Share target for a GhostRider stratum difficulty, as eight
    /// little-endian 32-bit words.</summary>
    /// <remarks>
    /// A transcription of cpuminer-gr's <c>diff_to_target</c>, applied to
    /// <c>diff / opt_target_factor</c> with <c>opt_target_factor = 65536</c>.
    /// GhostRider pools quote difficulty on a scale 65536x finer than Bitcoin's;
    /// XMRig expresses the same thing as <c>ceil(diff * 65536)</c> against a
    /// 2^256-based target (EthStratumClient.cpp), which works out identical.
    ///
    /// The normalization loop DIVIDES while diff > 1 — it is scaling a large
    /// difficulty down into a single word and recording the word index in k.
    /// Multiplying while diff &lt; 1 instead (the shape this had before) lands on
    /// a k roughly 2 words too low, i.e. a target ~2^64 too tight, and the miner
    /// then finds essentially no shares at all.
    /// </remarks>
    /// <summary>Full 256-bit cpuminer <c>diff_to_target</c>: the closed form is
    /// (2^240 - 2^224)/diff, spread across the uint[8] by the /65536 prescale
    /// and the word walk below.
    ///
    /// NOT interchangeable with <see cref="Csd.CsdHash.PdiffTarget"/>, which
    /// puts 0xFFFF0000/diff in word 1 and leaves the rest zero. Both used to be
    /// called <c>TargetForDifficulty</c>, which made picking the wrong one for a
    /// new algo a matter of autocomplete. Pick by dialect.</summary>
    public static uint[] Diff256Target(double diff)
    {
        if (diff <= 0) diff = 1.0;
        diff /= 65536.0;

        var target = new uint[8];
        int k;
        for (k = 6; k > 0 && diff > 1.0; k--) diff /= 4294967296.0;

        ulong m = (ulong)(4294901760.0 / diff);
        if (m == 0 && k == 6)
        {
            Array.Fill(target, 0xFFFFFFFFu);
        }
        else
        {
            target[k] = (uint)m;
            if (k + 1 < 8) target[k + 1] = (uint)(m >> 32);
        }
        return target;
    }

    /// <summary>cpuminer fulltest: the 32-byte GhostRider output, read as
    /// eight little-endian 32-bit words (word 7 most significant), is a valid
    /// share iff it is <= target.</summary>
    public static bool MeetsTarget(ReadOnlySpan<byte> hash32, uint[] target)
    {
        for (int i = 7; i >= 0; --i)
        {
            uint h = BinaryPrimitives.ReadUInt32LittleEndian(hash32.Slice(i * 4, 4));
            if (h > target[i]) return false;
            if (h < target[i]) return true;
        }
        return true;
    }
}
