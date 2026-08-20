// sha3t_keccak.hpp — SHA3-256t (BitcoinIII / BC3) search kernel.
//
// BC3 PoW: three SHA3-256 iterations over the canonical 80-byte Bitcoin header
//   0..4 version LE | 4..36 prev | 36..68 merkle | 68..72 time LE
//   72..76 bits LE  | 76..80 nonce LE
//   hash = SHA3_256(SHA3_256(SHA3_256(header)))
// and the digest is compared to the target as a LITTLE-ENDIAN 256-bit integer
// (the usual Bitcoin convention — the "displayed" hash is the digest reversed,
// which is why a valid one ends, not starts, in zero bytes).
//
// Verified against mainnet block 56000 (post-fork; the fork is at height 30240
// and genesis is still sha256d):
//   version 0x20001000, time 1786738255, bits 0x1b048245, nonce 723721353
//   -> 0000000000031c1896744b33c552471dfb51a5b470f90e452a4bc8213311f37a
// SHA3-256 is Keccak[512] with the 0x06 domain suffix and rate 136, so an
// 80-byte header and a 32-byte digest each absorb in ONE block: the whole PoW
// is exactly three keccak-f[1600] permutations and no absorb loop.
#pragma once
#include <cstdint>

namespace sha3t {

// --------------------------------------------------------------- keccak ---

constexpr uint64_t kRC[24] = {
    0x0000000000000001ULL, 0x0000000000008082ULL, 0x800000000000808aULL,
    0x8000000080008000ULL, 0x000000000000808bULL, 0x0000000080000001ULL,
    0x8000000080008081ULL, 0x8000000000008009ULL, 0x000000000000008aULL,
    0x0000000000000088ULL, 0x0000000080008009ULL, 0x000000008000000aULL,
    0x000000008000808bULL, 0x800000000000008bULL, 0x8000000000008089ULL,
    0x8000000000008003ULL, 0x8000000000008002ULL, 0x8000000000000080ULL,
    0x000000000000800aULL, 0x800000008000000aULL, 0x8000000080008081ULL,
    0x8000000000008080ULL, 0x0000000080000001ULL, 0x8000000080008008ULL};

// The rho/pi step as the classic 24-long displacement cycle (tiny_keccak form):
// lane 1 walks the cycle, each hop rotating by kRotc and landing on kPiln.
//
// Every hop reads the lane the previous hop wrote, so on paper this is a
// 24-long serial dependency chain and the scattered B[25] form — whose 24
// rotations are mutually independent — should expose far more ILP. MEASURED on
// a B580 (bmg_g21 AOT, 2026-08-15), it does not: 175.4 MH/s scattered against
// 175.2 compact, i.e. identical. The round loop is fully unrolled, so the
// compiler already sees through the chain and schedules the rotations itself.
// Kept compact because it costs 25 fewer live u64. Do not re-try the scatter
// expecting a win.
constexpr int kRotc[24] = {1, 3, 6, 10, 15, 21, 28, 36, 45, 55, 2, 14,
                           27, 41, 56, 8, 25, 43, 62, 18, 39, 61, 20, 44};
constexpr int kPiln[24] = {10, 7, 11, 17, 18, 3, 5, 16, 8, 21, 24, 4,
                           15, 23, 19, 13, 12, 2, 20, 14, 22, 9, 6, 1};

inline uint64_t rol(uint64_t x, int n) { return (x << n) | (x >> (64 - n)); }

// chi, in the spec's own spelling. Do not "optimise" this — it was tried.
//
// Xe has `bfn`, which evaluates an arbitrary 3-input boolean function from an
// 8-bit truth table in ONE instruction, and chi is exactly such a function, so
// the obvious idea is to spell it so IGC emits a single bfn. In an isolated
// probe (scratchpad/isa/chi3.cl, one output per kernel, bmg_g21) that works —
// `(a & b) | ((a ^ c) & ~b)` compiles to 4 bfn and zero xor, while this form
// compiles to 0 bfn and 12 boolean ops.
//
// It is still slower, on both platforms, MEASURED 2026-08-15:
//
//                          Linux JIT      Windows AOT
//   a ^ (~b & c)           214.0          175.5
//   (a&b)|((a^c)&~b)       179.0 (-16%)   170.5 (-2.8%)
//
// The Linux figure is a back-to-back A/B, three alternating rounds on an idle
// rig, ±0.01 MH/s. So the ISA-level instruction count is NOT the thing that
// governs this kernel, and a form that looks strictly better in isolation loses
// badly in situ — plausibly because the compilers have a pattern for the
// canonical andnot and lose it when the expression is rearranged.
inline uint64_t chi(uint64_t a, uint64_t b, uint64_t c) {
    return a ^ (~b & c);
}

// In-place keccak-f[1600]. Fully unrolled so the round constants, rotate
// amounts and lane indices all constant-fold away.
inline void keccakf(uint64_t a[25]) {
#pragma unroll
    for (int r = 0; r < 24; ++r) {
        // theta
        uint64_t c0 = a[0] ^ a[5] ^ a[10] ^ a[15] ^ a[20];
        uint64_t c1 = a[1] ^ a[6] ^ a[11] ^ a[16] ^ a[21];
        uint64_t c2 = a[2] ^ a[7] ^ a[12] ^ a[17] ^ a[22];
        uint64_t c3 = a[3] ^ a[8] ^ a[13] ^ a[18] ^ a[23];
        uint64_t c4 = a[4] ^ a[9] ^ a[14] ^ a[19] ^ a[24];
        uint64_t d0 = c4 ^ rol(c1, 1), d1 = c0 ^ rol(c2, 1), d2 = c1 ^ rol(c3, 1),
                 d3 = c2 ^ rol(c4, 1), d4 = c3 ^ rol(c0, 1);
#pragma unroll
        for (int i = 0; i < 25; i += 5) {
            a[i + 0] ^= d0; a[i + 1] ^= d1; a[i + 2] ^= d2;
            a[i + 3] ^= d3; a[i + 4] ^= d4;
        }

        // rho + pi
        uint64_t last = a[1];
#pragma unroll
        for (int t = 0; t < 24; ++t) {
            const int j = kPiln[t];
            uint64_t tmp = a[j];
            a[j] = rol(last, kRotc[t]);
            last = tmp;
        }

        // chi, one row at a time (5 live temporaries, not 25)
#pragma unroll
        for (int y = 0; y < 25; y += 5) {
            uint64_t b0 = a[y + 0], b1 = a[y + 1], b2 = a[y + 2],
                     b3 = a[y + 3], b4 = a[y + 4];
            a[y + 0] = chi(b0, b1, b2);
            a[y + 1] = chi(b1, b2, b3);
            a[y + 2] = chi(b2, b3, b4);
            a[y + 3] = chi(b3, b4, b0);
            a[y + 4] = chi(b4, b0, b1);
        }

        // iota
        a[0] ^= kRC[r];
    }
}

// SHA3-256 padding for a message shorter than the 136-byte rate: the 0x06
// domain-separation suffix right after the message and 0x80 in the last rate
// byte (lane 16, top bit).
constexpr uint64_t kRateEndBit = 0x8000000000000000ULL;

// --------------------------------------------------------------- sha3t ---

// hdr10 = the 80-byte header as ten little-endian lanes; lane 9's HIGH half is
// the nonce and is overwritten here. Returns the four digest lanes (lane 3 is
// the most significant end of the 256-bit value).
struct Digest4 { uint64_t l0, l1, l2, l3; };

inline Digest4 sha3t_hash(const uint64_t hdr10[10], uint32_t nonce) {
    uint64_t a[25];

    // ---- pass 1: absorb the 80-byte header ----
#pragma unroll
    for (int i = 0; i < 9; ++i) a[i] = hdr10[i];
    // bytes 72..79 = bits (low) | nonce (high)
    a[9] = (hdr10[9] & 0x00000000ffffffffULL) | (static_cast<uint64_t>(nonce) << 32);
    a[10] = 0x06ULL;            // byte 80: SHA-3 domain suffix
#pragma unroll
    for (int i = 11; i < 25; ++i) a[i] = 0;
    a[16] |= kRateEndBit;       // byte 135: end-of-rate marker
    keccakf(a);

    // ---- passes 2 and 3: absorb the previous 32-byte digest ----
    // The digest IS lanes 0..3 of the state we just permuted, so re-absorbing
    // it is only a matter of resetting the capacity and the padding lanes.
#pragma unroll
    for (int pass = 0; pass < 2; ++pass) {
        a[4] = 0x06ULL;         // byte 32: domain suffix
#pragma unroll
        for (int i = 5; i < 25; ++i) a[i] = 0;
        a[16] = kRateEndBit;
        keccakf(a);
    }

    return Digest4{a[0], a[1], a[2], a[3]};
}

// digest <= target, both read as little-endian 256-bit integers (lane 3 most
// significant). This is the same ordering Bitcoin Core uses when it compares
// UintToArith256(block.GetHash()) against the nBits target.
inline bool le_target(const Digest4& h, const uint64_t t[4]) {
    if (h.l3 != t[3]) return h.l3 < t[3];
    if (h.l2 != t[2]) return h.l2 < t[2];
    if (h.l1 != t[1]) return h.l1 < t[1];
    return h.l0 <= t[0];
}

}  // namespace sha3t
