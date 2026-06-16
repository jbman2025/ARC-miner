// ChallengeSolver — CPU solver for the pearl/v1 connection challenge.
//
// Protocol (captured from a live AlphaPool handshake, 2026-06-10):
//   pool →  {"method":"pearl.challenge","params":{"seed":"<64 hex>","difficulty":32}}
//   miner → {"id":N,"method":"pearl.challenge_response","params":{"nonce":"<16 hex>","seed":"<64 hex>"}}
//
// A nonce wins when BLAKE3(seed[32] || nonce as 8 LITTLE-ENDIAN bytes) has at
// least `difficulty` leading zero BITS. Verified against a live accepted
// triple: seed c8600c62…14e1, nonce 0x2cc663e5 → 33 leading zero bits ≥ 32.
// The nonce is serialized as the u64's 16-digit hex (most-significant first),
// independent of the little-endian byte order fed to the hash.

namespace Akoya.Crypto;

public static class ChallengeSolver
{
    /// <summary>Expected work is 2^difficulty hashes — callers should cap the
    /// difficulty they are willing to grind (a hostile/buggy pool could
    /// otherwise pin the CPU forever).</summary>
    public static ulong? Solve(ReadOnlySpan<byte> seed, int difficulty, CancellationToken ct)
    {
        if (seed.Length != 32) throw new ArgumentException("seed must be 32 bytes", nameof(seed));
        if (difficulty is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(difficulty));

        var seedArr = seed.ToArray();
        int threads = Math.Max(1, Environment.ProcessorCount);
        ulong found = 0;
        int foundFlag = 0;

        var workers = new Thread[threads];
        for (int t = 0; t < threads; t++)
        {
            int stride0 = t;
            workers[t] = new Thread(() =>
            {
                // The whole input is ONE 64-byte block (40 bytes used): message
                // words 0–7 are the seed (constant), 8–9 the nonce, 10–15 zero.
                // The hot loop uses a fully unrolled, register-resident
                // compression (no spans, no bounds checks) — see TryNonce.
                uint s0 = ReadW(seedArr, 0), s1 = ReadW(seedArr, 1), s2 = ReadW(seedArr, 2), s3 = ReadW(seedArr, 3);
                uint s4 = ReadW(seedArr, 4), s5 = ReadW(seedArr, 5), s6 = ReadW(seedArr, 6), s7 = ReadW(seedArr, 7);

                // Strided scan: thread t tries t, t+T, t+2T, …  Check the
                // cancel/found flags only every 8192 hashes to keep the hot
                // loop free of cross-core traffic.
                for (ulong nonce = (ulong)stride0; ; nonce += (ulong)threads)
                {
                    if ((nonce / (ulong)threads & 0x1FFF) == 0
                        && (Volatile.Read(ref foundFlag) != 0 || ct.IsCancellationRequested))
                        return;

                    if (TryNonce(s0, s1, s2, s3, s4, s5, s6, s7, nonce, difficulty))
                    {
                        if (Interlocked.CompareExchange(ref foundFlag, 1, 0) == 0)
                            Volatile.Write(ref found, nonce);
                        return;
                    }
                }
            })
            { IsBackground = true, Name = $"challenge-solver-{t}" };
            workers[t].Start();
        }

        foreach (var w in workers) w.Join();
        return Volatile.Read(ref foundFlag) != 0 ? Volatile.Read(ref found) : null;
    }

    /// <summary>Verify a (seed, nonce, difficulty) triple — used by tests and
    /// for double-checking a solution before sending it to the pool.</summary>
    public static bool Verify(ReadOnlySpan<byte> seed, ulong nonce, int difficulty)
    {
        Span<byte> input = stackalloc byte[40];
        seed.CopyTo(input);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(input[32..], nonce);
        Span<byte> hash = stackalloc byte[32];
        Blake3.Hash(input, hash);
        return LeadingZeroBits(hash) >= difficulty;
    }

    private static uint ReadW(byte[] seed, int w)
        => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(seed.AsSpan(w * 4, 4));

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void G(ref uint a, ref uint b, ref uint c, ref uint d, uint x, uint y)
    {
        a += b + x; d = uint.RotateRight(d ^ a, 16);
        c += d;     b = uint.RotateRight(b ^ c, 12);
        a += b + y; d = uint.RotateRight(d ^ a, 8);
        c += d;     b = uint.RotateRight(b ^ c, 7);
    }

    /// <summary>One BLAKE3 root compression of (seed ‖ nonce_le ‖ zero-pad),
    /// fully unrolled with the message schedule baked in as literals, then a
    /// leading-zero-bits ≥ difficulty test on the output byte stream. Message
    /// words 10–15 are constant zero so they appear as literal 0u below.
    /// Verified bit-identical to Blake3.Hash via ChallengeSolver.Verify
    /// (and the BLAKE3 official vectors backing Blake3 itself).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static bool TryNonce(
        uint s0, uint s1, uint s2, uint s3, uint s4, uint s5, uint s6, uint s7,
        ulong nonce, int difficulty)
    {
        uint n0 = (uint)nonce, n1 = (uint)(nonce >> 32);

        uint v0 = 0x6A09E667u, v1 = 0xBB67AE85u, v2 = 0x3C6EF372u, v3 = 0xA54FF53Au;
        uint v4 = 0x510E527Fu, v5 = 0x9B05688Cu, v6 = 0x1F83D9ABu, v7 = 0x5BE0CD19u;
        uint v8 = 0x6A09E667u, v9 = 0xBB67AE85u, v10 = 0x3C6EF372u, v11 = 0xA54FF53Au;
        uint v12 = 0, v13 = 0, v14 = 40, v15 = 1u | 2u | 8u; // counter=0, len=40, CHUNK_START|CHUNK_END|ROOT

        // round 0 — schedule: 0 1 | 2 3 | 4 5 | 6 7 | 8 9 | 10 11 | 12 13 | 14 15
        G(ref v0, ref v4, ref v8,  ref v12, s0, s1);
        G(ref v1, ref v5, ref v9,  ref v13, s2, s3);
        G(ref v2, ref v6, ref v10, ref v14, s4, s5);
        G(ref v3, ref v7, ref v11, ref v15, s6, s7);
        G(ref v0, ref v5, ref v10, ref v15, n0, n1);
        G(ref v1, ref v6, ref v11, ref v12, 0u, 0u);
        G(ref v2, ref v7, ref v8,  ref v13, 0u, 0u);
        G(ref v3, ref v4, ref v9,  ref v14, 0u, 0u);
        // round 1 — 2 6 | 3 10 | 7 0 | 4 13 | 1 11 | 12 5 | 9 14 | 15 8
        G(ref v0, ref v4, ref v8,  ref v12, s2, s6);
        G(ref v1, ref v5, ref v9,  ref v13, s3, 0u);
        G(ref v2, ref v6, ref v10, ref v14, s7, s0);
        G(ref v3, ref v7, ref v11, ref v15, s4, 0u);
        G(ref v0, ref v5, ref v10, ref v15, s1, 0u);
        G(ref v1, ref v6, ref v11, ref v12, 0u, s5);
        G(ref v2, ref v7, ref v8,  ref v13, n1, 0u);
        G(ref v3, ref v4, ref v9,  ref v14, 0u, n0);
        // round 2 — 3 4 | 10 12 | 13 2 | 7 14 | 6 5 | 9 0 | 11 15 | 8 1
        G(ref v0, ref v4, ref v8,  ref v12, s3, s4);
        G(ref v1, ref v5, ref v9,  ref v13, 0u, 0u);
        G(ref v2, ref v6, ref v10, ref v14, 0u, s2);
        G(ref v3, ref v7, ref v11, ref v15, s7, 0u);
        G(ref v0, ref v5, ref v10, ref v15, s6, s5);
        G(ref v1, ref v6, ref v11, ref v12, n1, s0);
        G(ref v2, ref v7, ref v8,  ref v13, 0u, 0u);
        G(ref v3, ref v4, ref v9,  ref v14, n0, s1);
        // round 3 — 10 7 | 12 9 | 14 3 | 13 15 | 4 0 | 11 2 | 5 8 | 1 6
        G(ref v0, ref v4, ref v8,  ref v12, 0u, s7);
        G(ref v1, ref v5, ref v9,  ref v13, 0u, n1);
        G(ref v2, ref v6, ref v10, ref v14, 0u, s3);
        G(ref v3, ref v7, ref v11, ref v15, 0u, 0u);
        G(ref v0, ref v5, ref v10, ref v15, s4, s0);
        G(ref v1, ref v6, ref v11, ref v12, 0u, s2);
        G(ref v2, ref v7, ref v8,  ref v13, s5, n0);
        G(ref v3, ref v4, ref v9,  ref v14, s1, s6);
        // round 4 — 12 13 | 9 11 | 15 10 | 14 8 | 7 2 | 5 3 | 0 1 | 6 4
        G(ref v0, ref v4, ref v8,  ref v12, 0u, 0u);
        G(ref v1, ref v5, ref v9,  ref v13, n1, 0u);
        G(ref v2, ref v6, ref v10, ref v14, 0u, 0u);
        G(ref v3, ref v7, ref v11, ref v15, 0u, n0);
        G(ref v0, ref v5, ref v10, ref v15, s7, s2);
        G(ref v1, ref v6, ref v11, ref v12, s5, s3);
        G(ref v2, ref v7, ref v8,  ref v13, s0, s1);
        G(ref v3, ref v4, ref v9,  ref v14, s6, s4);
        // round 5 — 9 14 | 11 5 | 8 12 | 15 1 | 13 3 | 0 10 | 2 6 | 4 7
        G(ref v0, ref v4, ref v8,  ref v12, n1, 0u);
        G(ref v1, ref v5, ref v9,  ref v13, 0u, s5);
        G(ref v2, ref v6, ref v10, ref v14, n0, 0u);
        G(ref v3, ref v7, ref v11, ref v15, 0u, s1);
        G(ref v0, ref v5, ref v10, ref v15, 0u, s3);
        G(ref v1, ref v6, ref v11, ref v12, s0, 0u);
        G(ref v2, ref v7, ref v8,  ref v13, s2, s6);
        G(ref v3, ref v4, ref v9,  ref v14, s4, s7);
        // round 6 — 11 15 | 5 0 | 1 9 | 8 6 | 14 10 | 2 12 | 3 4 | 7 13
        G(ref v0, ref v4, ref v8,  ref v12, 0u, 0u);
        G(ref v1, ref v5, ref v9,  ref v13, s5, s0);
        G(ref v2, ref v6, ref v10, ref v14, s1, n1);
        G(ref v3, ref v7, ref v11, ref v15, n0, s6);
        G(ref v0, ref v5, ref v10, ref v15, 0u, 0u);
        G(ref v1, ref v6, ref v11, ref v12, s2, 0u);
        G(ref v2, ref v7, ref v8,  ref v13, s3, s4);
        G(ref v3, ref v4, ref v9,  ref v14, s7, 0u);

        // Output word i = v_i ^ v_{i+8}; the hash's byte stream is the words
        // little-endian, so leading-zero BITS of the stream = clz of the
        // byte-reversed word. Early-out on h0 covers difficulty ≤ 32.
        uint h0 = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v0 ^ v8);
        if (h0 != 0)
            return System.Numerics.BitOperations.LeadingZeroCount(h0) >= difficulty;

        int lz = 32;
        Span<uint> rest = stackalloc uint[7] { v1 ^ v9, v2 ^ v10, v3 ^ v11, v4 ^ v12, v5 ^ v13, v6 ^ v14, v7 ^ v15 };
        foreach (var w in rest)
        {
            uint rev = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(w);
            if (rev == 0) { lz += 32; continue; }
            lz += System.Numerics.BitOperations.LeadingZeroCount(rev);
            break;
        }
        return lz >= difficulty;
    }

    internal static int LeadingZeroBits(ReadOnlySpan<byte> h)
    {
        int lz = 0;
        foreach (var b in h)
        {
            if (b == 0) { lz += 8; continue; }
            return lz + System.Numerics.BitOperations.LeadingZeroCount(b) - 24;
        }
        return lz;
    }
}
