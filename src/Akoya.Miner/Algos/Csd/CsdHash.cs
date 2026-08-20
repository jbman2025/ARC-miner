// Host-side hashing + header assembly for CSD (Compute Substrate) sha256d.
// Mirrors the proven standalone (native/csd-sha256d/csd_miner.cpp): 84-byte
// header, midstate over the first 64 bytes, coinbase+merkle from the stratum
// job, and the difficulty->target conversion.

using Akoya.Crypto;

namespace Akoya.Miner.Algos.Csd;

internal static class CsdHash
{
    public static byte[] Unhex(string s) => Akoya.Crypto.Hex.Decode(s);

    public static string Hex(ReadOnlySpan<byte> b) => Akoya.Crypto.Hex.Encode(b);

    public static byte[] Sha256d(ReadOnlySpan<byte> data) => Sha2.Sha256d(data);

    // SHA-256 K constants + one block transform, for the midstate (SHA256 in
    // System.Security.Cryptography does not expose an unfinalized state).
    private static readonly uint[] K = {
        0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,
        0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,
        0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,
        0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,
        0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,
        0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,
        0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,
        0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2};

    private static uint Rotr(uint x, int n) => (x >> n) | (x << (32 - n));

    /// <summary>SHA-256 state (8 words) after compressing the single 64-byte
    /// block, starting from the standard IV. This is the block-0 midstate.</summary>
    public static uint[] Midstate(ReadOnlySpan<byte> block64)
    {
        uint[] st = { 0x6a09e667,0xbb67ae85,0x3c6ef372,0xa54ff53a,0x510e527f,0x9b05688c,0x1f83d9ab,0x5be0cd19 };
        Span<uint> w = stackalloc uint[64];
        for (int i = 0; i < 16; ++i)
            w[i] = ((uint)block64[4 * i] << 24) | ((uint)block64[4 * i + 1] << 16) | ((uint)block64[4 * i + 2] << 8) | block64[4 * i + 3];
        for (int i = 16; i < 64; ++i)
        {
            uint s0 = Rotr(w[i - 15], 7) ^ Rotr(w[i - 15], 18) ^ (w[i - 15] >> 3);
            uint s1 = Rotr(w[i - 2], 17) ^ Rotr(w[i - 2], 19) ^ (w[i - 2] >> 10);
            w[i] = w[i - 16] + s0 + w[i - 7] + s1;
        }
        uint a = st[0], b = st[1], c = st[2], d = st[3], e = st[4], f = st[5], g = st[6], h = st[7];
        for (int i = 0; i < 64; ++i)
        {
            uint S1 = Rotr(e, 6) ^ Rotr(e, 11) ^ Rotr(e, 25);
            uint ch = (e & f) ^ (~e & g);
            uint t1 = h + S1 + ch + K[i] + w[i];
            uint S0 = Rotr(a, 2) ^ Rotr(a, 13) ^ Rotr(a, 22);
            uint maj = (a & b) ^ (a & c) ^ (b & c);
            uint t2 = S0 + maj;
            h = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
        }
        return new[] { st[0]+a, st[1]+b, st[2]+c, st[3]+d, st[4]+e, st[5]+f, st[6]+g, st[7]+h };
    }

    public static uint Be32(ReadOnlySpan<byte> p) => ((uint)p[0] << 24) | ((uint)p[1] << 16) | ((uint)p[2] << 8) | p[3];

    /// <summary>PDIFF target: difficulty-1 is 0xFFFF0000 in BE word 1, rest 0;
    /// the share target is that / diff, low words left 0 (strict-valid).
    ///
    /// NOT interchangeable with <see cref="Gr.GrHash.Diff256Target"/>, which
    /// produces a full 256-bit cpuminer-style target from the same input. Both
    /// used to be called <c>TargetForDifficulty</c>, which made picking the
    /// wrong one for a new algo a matter of autocomplete. Pick by dialect:
    /// pdiff here, cpuminer diff_to_target there.</summary>
    public static uint[] PdiffTarget(double diff)
    {
        var t = new uint[8];
        t[1] = (uint)((double)0xFFFF0000UL / (diff > 0 ? diff : 1.0));
        return t;
    }
}
