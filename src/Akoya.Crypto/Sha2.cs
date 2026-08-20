// Double-SHA256, the coinbase/merkle primitive of every Bitcoin-derived
// stratum dialect (csd, gr today; kawpow/firopow/sha256dt later).
//
// Was three copies: CsdHash.Sha256d and GrHash.Sha256d were byte-identical,
// and BtxJob.Sha256d was SHA256.HashData(SHA256.HashData(data.ToArray())) —
// same result, but the .ToArray() copied the whole coinbase on every call for
// no reason. This is the stackalloc version.

using System.Security.Cryptography;

namespace Akoya.Crypto;

public static class Sha2
{
    /// <summary>SHA-256 applied twice — sha256d(data).</summary>
    public static byte[] Sha256d(ReadOnlySpan<byte> data)
    {
        Span<byte> once = stackalloc byte[32];
        SHA256.HashData(data, once);
        return SHA256.HashData(once);
    }
}
