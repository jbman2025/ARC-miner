// blake3_device.hpp — BLAKE3 core for SYCL device code (Intel Arc / oneAPI).
//
// Direct port of rocm/blake3_device.cuh: same arithmetic, same byte layout.
// All functions are plain inline C++ — the SYCL runtime inlines them into
// kernel lambdas automatically (no __device__ qualifier needed).
#pragma once
#include <cstdint>
#include <sycl/sycl.hpp>

using u32 = uint32_t;
using u64 = uint64_t;

namespace b3 {

constexpr u32 IV0=0x6A09E667, IV1=0xBB67AE85, IV2=0x3C6EF372, IV3=0xA54FF53A,
              IV4=0x510e527f, IV5=0x9b05688c, IV6=0x1f83d9ab, IV7=0x5be0cd19;
constexpr u32 CHUNK_START=1<<0, CHUNK_END=1<<1, PARENT=1<<2, ROOT=1<<3, KEYED_HASH=1<<4;
constexpr u32 MSG_BLOCK_SIZE=64, CHUNK_SIZE=1024, BLOCKS_PER_CHUNK=16;

inline u32 add32(u32 x, u32 y) { return x + y; }
inline u32 rightrotate32(u32 x, u32 n) { return (x << (32 - n)) | (x >> n); }
inline u32 rotl32(u32 x, int n) { return (x << n) | (x >> (32 - n)); }

#define rState(i)     state[i]
#define rBlock(i)     block[i]
#define rOrigBlock(i) origblock[i]
#include "../rocm/blake3_rounds.inc"

inline void compress(u32 cv[8], const u32 block_in[16], u64 counter,
                     u32 block_len, u32 flags) {
    u32 state[16], block[16], origblock[16];
    for (int i = 0; i < 16; ++i) block[i] = block_in[i];
    for (int i = 0; i < 8; ++i) state[i] = cv[i];
    state[8]=IV0; state[9]=IV1; state[10]=IV2; state[11]=IV3;
    state[12]=(u32)counter; state[13]=(u32)(counter>>32);
    state[14]=block_len; state[15]=flags;
    for (int i = 0; i < 6; ++i) { BLAKE3_ROUND(); BLAKE3_PERMUTE(); }
    BLAKE3_ROUND();
    for (int i = 0; i < 8; ++i) cv[i] = state[i] ^ state[i+8];
}

inline void compress_full(const u32 cv_in[8], const u32 block_in[16],
                          u64 counter, u32 block_len, u32 flags, u32 out16[16]) {
    u32 state[16], block[16], origblock[16];
    for (int i = 0; i < 16; ++i) block[i] = block_in[i];
    for (int i = 0; i < 8; ++i) state[i] = cv_in[i];
    state[8]=IV0; state[9]=IV1; state[10]=IV2; state[11]=IV3;
    state[12]=(u32)counter; state[13]=(u32)(counter>>32);
    state[14]=block_len; state[15]=flags;
    for (int i = 0; i < 6; ++i) { BLAKE3_ROUND(); BLAKE3_PERMUTE(); }
    BLAKE3_ROUND();
    for (int i = 0; i < 8; ++i) { out16[i]=state[i]^state[i+8]; out16[i+8]=state[i+8]^cv_in[i]; }
}

inline u32 load_le32(const uint8_t* p) {
    // Direct aligned 32-bit read (Gemini fix #1). Intel Arc is little-endian, so
    // this is bit-identical to the byte-assembled form and skips the shift/OR
    // sequence. REQUIRES p to be 4-byte aligned — all call sites read from
    // alignas(4) stack buffers (bf/tb) or 4-aligned device buffers.
    return *reinterpret_cast<const u32*>(p);
}
inline void init_cv(u32 cv[8], const u32* key) {
    if (key) { for (int i = 0; i < 8; ++i) cv[i] = key[i]; }
    else { cv[0]=IV0;cv[1]=IV1;cv[2]=IV2;cv[3]=IV3;cv[4]=IV4;cv[5]=IV5;cv[6]=IV6;cv[7]=IV7; }
}
inline void cv_to_bytes(const u32 cv[8], uint8_t out[32]) {
    for (int i = 0; i < 8; ++i) {
        out[i*4]=(uint8_t)(cv[i]); out[i*4+1]=(uint8_t)(cv[i]>>8);
        out[i*4+2]=(uint8_t)(cv[i]>>16); out[i*4+3]=(uint8_t)(cv[i]>>24);
    }
}

// Single-chunk (≤1024 B) keyed/unkeyed BLAKE3 ROOT hash.
inline void hash_small(const uint8_t* data, int len, const u32* key, uint8_t out[32]) {
    u32 cv[8]; init_cv(cv, key);
    int nb = (len + 63) / 64; if (!nb) nb = 1;
    for (int b = 0; b < nb; ++b) {
        u32 bl[16]; alignas(4) uint8_t bf[64];   // alignas for aligned load_le32 (Gemini fix #1)
        for (int j = 0; j < 64; ++j) bf[j] = (b*64+j < len) ? data[b*64+j] : 0;
        for (int j = 0; j < 16; ++j) bl[j] = load_le32(bf + j*4);
        int blen = len - b*64; if (blen > 64) blen = 64; if (blen < 0) blen = 0;
        u32 f = (key ? KEYED_HASH : (u32)0);
        if (b == 0) f |= CHUNK_START;
        if (b == nb-1) f |= CHUNK_END | ROOT;
        compress(cv, bl, 0ULL, (u32)blen, f);
    }
    cv_to_bytes(cv, out);
}

// Single-chunk (≤1024 B) keyed/unkeyed BLAKE3 ROOT hash returning u32[8] directly.
inline void hash_small_u32(const uint8_t* data, int len, const u32* key, u32 out[8]) {
    u32 cv[8]; init_cv(cv, key);
    int nb = (len + 63) / 64; if (!nb) nb = 1;
    for (int b = 0; b < nb; ++b) {
        u32 bl[16]; alignas(4) uint8_t bf[64];
        for (int j = 0; j < 64; ++j) bf[j] = (b*64+j < len) ? data[b*64+j] : 0;
        for (int j = 0; j < 16; ++j) bl[j] = load_le32(bf + j*4);
        int blen = len - b*64; if (blen > 64) blen = 64; if (blen < 0) blen = 0;
        u32 f = (key ? KEYED_HASH : (u32)0);
        if (b == 0) f |= CHUNK_START;
        if (b == nb-1) f |= CHUNK_END | ROOT;
        compress(cv, bl, 0ULL, (u32)blen, f);
    }
    for (int i = 0; i < 8; ++i) out[i] = cv[i];
}

// Hash one full 1024-byte chunk (counter = chunk index).
inline void chunk_cv(const uint8_t* data, u64 idx, const u32* key, bool root, u32 cv_out[8]) {
    u32 cv[8]; init_cv(cv, key);
    for (int b = 0; b < BLOCKS_PER_CHUNK; ++b) {
        u32 bl[16];
        for (int j = 0; j < 16; ++j) bl[j] = load_le32(data + b*64 + j*4);
        u32 f = (key ? KEYED_HASH : (u32)0);
        if (b == 0) f |= CHUNK_START;
        if (b == BLOCKS_PER_CHUNK-1) { f |= CHUNK_END; if (root) f |= ROOT; }
        compress(cv, bl, idx, 64, f);
    }
    for (int i = 0; i < 8; ++i) cv_out[i] = cv[i];
}

// Combine two CVs into a parent CV.
inline void parent_cv(const u32* left, const u32* right, const u32* key, bool root, u32 out[8]) {
    u32 cv[8]; init_cv(cv, key);
    u32 bl[16];
    for (int i = 0; i < 8; ++i) { bl[i] = left[i]; bl[8+i] = right[i]; }
    u32 f = PARENT | (key ? KEYED_HASH : (u32)0) | (root ? ROOT : (u32)0);
    compress(cv, bl, 0ULL, MSG_BLOCK_SIZE, f);
    for (int i = 0; i < 8; ++i) out[i] = cv[i];
}

} // namespace b3
