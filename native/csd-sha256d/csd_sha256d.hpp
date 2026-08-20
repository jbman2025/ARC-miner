// csd_sha256d.hpp — SYCL SHA-256d search kernel for Compute Substrate (CSD).
//
// CSD PoW: sha256d over an 84-byte header (from src/gpu/kernel.cu):
//   0..4  version u32 LE | 4..36 prev(32) | 36..68 merkle(32)
//   68..76 time u64 LE  | 76..80 bits u32 LE | 80..84 nonce u32 LE
// Block 0 = header[0..64] (constant per job -> host midstate). Block 1 =
// header[64..84] + SHA padding, message length 84*8 = 672 bits. Then a second
// SHA over the 32-byte first digest. Winner: digest <= target.
#pragma once
#include <sycl/sycl.hpp>
#include <cstdint>
#include <array>

namespace csd {

constexpr uint32_t kK[64] = {
    0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,
    0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,
    0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,
    0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,
    0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,
    0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,
    0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,
    0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2};

inline uint32_t rotr(uint32_t x, int n) { return (x >> n) | (x << (32 - n)); }

// 64 rounds over a 16-word rolling schedule (matches the BTX ShaRounds form).
inline void rounds(std::array<uint32_t, 8>& st, std::array<uint32_t, 16>& w) {
    uint32_t a=st[0],b=st[1],c=st[2],d=st[3],e=st[4],f=st[5],g=st[6],h=st[7];
#pragma unroll
    for (int i=0;i<64;++i) {
        if (i>=16) {
            uint32_t w15=w[(i+1)&15], w2=w[(i+14)&15];
            uint32_t s0=rotr(w15,7)^rotr(w15,18)^(w15>>3);
            uint32_t s1=rotr(w2,17)^rotr(w2,19)^(w2>>10);
            w[i&15]+=s0+w[(i+9)&15]+s1;
        }
        uint32_t S1=rotr(e,6)^rotr(e,11)^rotr(e,25);
        uint32_t ch=(e&f)^(~e&g);
        uint32_t t1=h+S1+ch+kK[i]+w[i&15];
        uint32_t S0=rotr(a,2)^rotr(a,13)^rotr(a,22);
        uint32_t maj=(a&b)^(a&c)^(b&c);
        uint32_t t2=S0+maj;
        h=g;g=f;f=e;e=d+t1;d=c;c=b;b=a;a=t1+t2;
    }
    st[0]+=a;st[1]+=b;st[2]+=c;st[3]+=d;st[4]+=e;st[5]+=f;st[6]+=g;st[7]+=h;
}

// Full sha256d of the 84-byte header, given the precomputed midstate over
// header[0..64] and the 20 tail bytes header[64..84] with nonce patched in.
// tail5[0..4] = header words 16..19 (merkle tail, time_lo, time_hi, bits);
// word 20 (nonce) is supplied separately so it can vary per work-item.
// Returns digest as 8 big-endian words.
inline std::array<uint32_t, 8> sha256d_tail(const std::array<uint32_t, 8>& mid, const std::array<uint32_t, 5>& tail5, uint32_t nonce) {
    // ---- first block: the 64-byte second half (bytes 64..128) ----
    std::array<uint32_t, 16> w;
    w[0]=tail5[0]; w[1]=tail5[1]; w[2]=tail5[2]; w[3]=tail5[3]; // merkle tail, time_lo, time_hi, bits
    w[4]=nonce;                    // byte 80..84 nonce (big-endian word form)
    w[5]=0x80000000u;              // padding marker at byte 84
    w[6]=0;w[7]=0;w[8]=0;w[9]=0;w[10]=0;w[11]=0;w[12]=0;w[13]=0;w[14]=0;
    w[15]=672u;                    // 84 bytes * 8
    std::array<uint32_t, 8> st = mid;
    rounds(st, w);
    // ---- second SHA over the 32-byte first digest ----
    std::array<uint32_t, 16> w2;
#pragma unroll
    for(int i=0;i<8;++i) w2[i]=st[i];
    w2[8]=0x80000000u;
#pragma unroll
    for(int i=9;i<15;++i) w2[i]=0;
    w2[15]=256u;
    std::array<uint32_t, 8> st2={0x6a09e667,0xbb67ae85,0x3c6ef372,0xa54ff53a,0x510e527f,0x9b05688c,0x1f83d9ab,0x5be0cd19};
    rounds(st2, w2);
    return st2;
}

// digest (8 BE words) <= target (8 BE words) ?
inline bool le_target(const std::array<uint32_t, 8>& h, const std::array<uint32_t, 8>& t) {
#pragma unroll
    for (int i=0;i<8;++i){ if(h[i]<t[i]) return true; if(h[i]>t[i]) return false; }
    return true;
}

// Nonces each work-item sweeps. >1 amortizes launch/dispatch and lifts
// occupancy (fewer, longer-lived threads), the usual win for a cheap kernel
// like a single sha256d. count must be a multiple of this.
constexpr uint32_t kNoncesPerThread = 8;

// Search [nonce_base, nonce_base+count): append winning nonces via an atomic
// counter. found_out holds up to cap winners; found_count[0] is the total.
inline void run_search(sycl::queue& q, const std::array<uint32_t,8>& mid,
                       const std::array<uint32_t,5>& tail5, const std::array<uint32_t,8>& target,
                       uint32_t nonce_base, uint32_t count,
                       uint32_t* found_out, uint32_t cap, uint32_t* found_count) {
    const uint32_t per = kNoncesPerThread;
    const uint32_t threads = (count + per - 1) / per;
    q.parallel_for(sycl::range<1>{threads}, [=](sycl::id<1> gid){
        using Atom = sycl::atomic_ref<uint32_t, sycl::memory_order::relaxed,
                                      sycl::memory_scope::device, sycl::access::address_space::global_space>;
        const uint32_t start = static_cast<uint32_t>(gid[0]) * per;
        const std::array<uint32_t, 8> local_target = target;
        for (uint32_t j = 0; j < per; ++j) {
            const uint32_t idx = start + j;
            if (idx >= count) break;
            const uint32_t nonce = nonce_base + idx;
            std::array<uint32_t, 8> h = sha256d_tail(mid, tail5, nonce);
            if (le_target(h, local_target)) {
                uint32_t slot = Atom(found_count[0]).fetch_add(1u);
                if (slot < cap) found_out[slot] = nonce;
            }
        }
    }).wait();
}

} // namespace csd
