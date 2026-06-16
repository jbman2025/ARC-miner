// pearl_kernels.hpp — GPU kernels for the Pearl miner, SYCL/oneAPI backend.
//
// Port of rocm/pearl_kernels.cuh to SYCL.  Each kernel is a free function
// that accepts a sycl::queue* and enqueues work asynchronously; the caller
// must wait on the queue before reading results.
//
// The tgemm_pow kernel uses a tile-based int8 GEMM with DP4A-style
// accumulation (four int8 multiplies per step) which Intel's IGC lowers to
// XMX (XE Matrix Extensions) on Arc hardware.
//
// Sub-group size for XMX kernels is generation-specific (Xe-HPG: 8, Xe2: 16);
// see is_xe_hpg() below. Non-XMX kernels use the compiler default.
#pragma once
#include "blake3_device.hpp"
#include <sycl/sycl.hpp>
#include <sycl/ext/oneapi/matrix/matrix.hpp>
#include <sycl/ext/intel/experimental/grf_size_properties.hpp>
#include <cstdint>
#include <cstdlib>
#include <cstring>

#define HSYCL(s) static_cast<sycl::queue*>(s)

namespace pk {

namespace xmx = sycl::ext::oneapi::experimental::matrix;

// ── XMX sub-group / tile-shape selection ────────────────────────────────────
// Intel XMX int8 joint_matrix shapes are GENERATION-SPECIFIC:
//   Xe-HPG (Arc A-series, ACM/DG2): sub-group 8,  tile N = 8
//   Xe2    (Arc B-series, BMG) +:   sub-group 16, tile N = 16
// Launching the sg16/N16 kernels on an A-series card makes the driver JIT
// throw (observed in the field as install_B rc=-100 in noise_B on an A750).
// Every XMX launch below dispatches on the queue's device architecture.
// AKOYA_XMX_SG=8|16 overrides detection (diagnostics only).
inline bool is_xe_hpg(sycl::queue* q) {
    static const int forced = []{
        const char* v = getenv("AKOYA_XMX_SG");
        return v ? atoi(v) : 0;
    }();
    if (forced == 8)  return true;
    if (forced == 16) return false;
    try {
        namespace sex = sycl::ext::oneapi::experimental;
        auto arch = q->get_device().get_info<sex::info::device::architecture>();
        return arch == sex::architecture::intel_gpu_acm_g10
            || arch == sex::architecture::intel_gpu_acm_g11
            || arch == sex::architecture::intel_gpu_acm_g12;
    } catch (...) {
        return false;   // unknown arch → keep the historical sg16 behaviour
    }
}

// ── Unique kernel name tags (required by SYCL) ──────────────────────────────
struct KLcg{};
struct KTensorHash{};
struct KTensorHashLeafCvs{};
struct KCommitment{};
struct KUniformA{};
struct KUniformB{};
struct KPerm{};
struct KAddI8{};
struct KTransposeI8{};
struct KBseed{};
struct KBlakeLeaves{};
struct KBlakeReduce{};
struct KGemmI8{};
template<int SG> struct KGemmI8X{};
template<int MB, int NB, int SG> struct KTgemmPow{};
struct KPowCheck{};

// ── LCG int7 fill ─────────────────────────────────────────────────────────
inline void launch_lcg_int7_fill(void* dst, int64_t n,
                                  uint64_t seed_lo, uint64_t seed_hi,
                                  sycl::queue* q) {
    auto* out = static_cast<int8_t*>(dst);
    int64_t n8 = n / 8;
    int blk = (int)((n8 + 63) / 64); if (blk < 1) blk = 1;

    q->parallel_for<KLcg>(sycl::range<1>((size_t)blk * 64), [=](sycl::id<1> id) {
        auto splitmix = [](uint64_t z) -> uint64_t {
            z += 0x9E3779B97F4A7C15ULL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ULL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBULL;
            return z ^ (z >> 31);
        };
        uint64_t base = splitmix(seed_lo ^ splitmix(seed_hi));
        int64_t i = (int64_t)id[0];
        if (i < n8) {
            uint64_t z = splitmix(base + (uint64_t)i);
            for (int b = 0; b < 8; ++b) {
                uint32_t v = (uint32_t)((z >> (b * 8)) & 0xFF);
                out[i * 8 + b] = (int8_t)((int)(v % 127) - 63);
            }
        }
        if (i == 0 && (n % 8)) {
            uint64_t z = splitmix(base + (uint64_t)n8);
            int64_t t = n - n8 * 8;
            for (int b = 0; b < (int)t; ++b) {
                uint32_t v = (uint32_t)((z >> (b * 8)) & 0xFF);
                out[n8 * 8 + b] = (int8_t)((int)(v % 127) - 63);
            }
        }
    });
}

// ── Parallel Merkle tensor_hash ────────────────────────────────────────────
inline void parallel_tensor_hash(const uint8_t* data, long len, const u32* key,
                                  u32* scratch, uint8_t* out,
                                  sycl::queue* q, uint8_t* leaf_cvs = nullptr) {
    long nchunks = (len + 1023) / 1024; if (nchunks < 1) nchunks = 1;
    u32* bufA = scratch;
    u32* bufB = scratch + nchunks * 8;
    const int tpb = 256;

    // Leaf hashes
    q->parallel_for<KBlakeLeaves>(
        sycl::range<1>((size_t)((nchunks + tpb - 1) / tpb) * tpb),
        [=](sycl::id<1> id) {
            long i = (long)id[0]; if (i >= nchunks) return;
            long off = i * b3::CHUNK_SIZE, rem = len - off;
            u32 cv[8];
            if (rem >= (long)b3::CHUNK_SIZE) {
                b3::chunk_cv(data + off, (u64)i, key, nchunks == 1, cv);
            } else {
                b3::init_cv(cv, key);
                int nb = (int)((rem + 63) / 64); if (nb < 1) nb = 1;
                for (int b = 0; b < nb; ++b) {
                    u32 bl[16]; uint8_t bf[64];
                    for (int j = 0; j < 64; ++j)
                        bf[j] = (off + b*64 + j < len) ? data[off + b*64 + j] : 0;
                    for (int j = 0; j < 16; ++j) bl[j] = b3::load_le32(bf + j*4);
                    int blen = (int)rem - b*64; if (blen > 64) blen = 64; if (blen < 0) blen = 0;
                    u32 f = b3::KEYED_HASH;
                    if (b == 0) f |= b3::CHUNK_START;
                    if (b == nb-1) f |= b3::CHUNK_END;
                    b3::compress(cv, bl, (u64)i, (u32)blen, f);
                }
            }
            for (int j = 0; j < 8; ++j) bufA[i * 8 + j] = cv[j];
        });

    if (leaf_cvs) {
        q->memcpy(leaf_cvs, bufA, (size_t)nchunks * 32);
    }

    if (nchunks == 1) {
        q->memcpy(out, bufA, 32);
        return;
    }

    u32* in_buf = bufA;
    u32* ob_buf = bufB;
    long npairs = nchunks / 2;
    while (true) {
        long cur_pairs = npairs;
        u32* cur_in = in_buf;
        u32* cur_ob = ob_buf;
        bool is_root = (npairs == 1);
        q->parallel_for<KBlakeReduce>(
            sycl::range<1>((size_t)((npairs + tpb - 1) / tpb) * tpb),
            [=](sycl::id<1> id) {
                long i = (long)id[0]; if (i >= cur_pairs) return;
                u32 o[8];
                b3::parent_cv(cur_in + (2*i)*8, cur_in + (2*i+1)*8, key, is_root && cur_pairs == 1, o);
                for (int j = 0; j < 8; ++j) cur_ob[i*8+j] = o[j];
            });
        if (npairs == 1) { q->memcpy(out, ob_buf, 32); break; }
        u32* t = in_buf; in_buf = ob_buf; ob_buf = t;
        npairs /= 2;
    }
}

// ── commitment_hash ────────────────────────────────────────────────────────
inline void launch_commitment_hash(const uint8_t* AHash, const uint8_t* BHash,
                                    const uint8_t* Key,
                                    uint8_t* CommitA, uint8_t* CommitB,
                                    sycl::queue* q) {
    q->single_task<KCommitment>([=]() {
        uint8_t buf[64];
        for (int i = 0; i < 32; ++i) { buf[i] = Key[i]; buf[32+i] = BHash[i]; }
        b3::hash_small(buf, 64, nullptr, CommitB);
        for (int i = 0; i < 32; ++i) { buf[i] = CommitB[i]; buf[32+i] = AHash[i]; }
        b3::hash_small(buf, 64, nullptr, CommitA);
    });
}

// ── noise_gen: uniform + permutation ──────────────────────────────────────
inline void launch_noise_gen(int R, int m, int n, int k,
                              void* EAL, void* EAL_fp16,
                              void* EAR_R, void* EAR_K,
                              void* EBL_R, void* EBL_K,
                              void* EBR, void* EBR_fp16,
                              const uint8_t* key_A, const uint8_t* key_B,
                              sycl::queue* q) {
    const int tpb = 64;

    if (EAL) {
        auto* out = static_cast<int8_t*>(EAL);
        auto* outf = static_cast<sycl::half*>(EAL_fp16);
        int nb = m * R, nh = (nb + 31) / 32;
        constexpr int scale = -1;  // kEAL
        q->parallel_for<KUniformA>(sycl::range<1>((size_t)((nh+tpb-1)/tpb)*tpb),
            [=](sycl::id<1> id) {
                int i = (int)id[0]; if (i >= nh) return;
                uint8_t msg[64] = {};
                msg[0] = (uint8_t)((1+i)    ); msg[1] = (uint8_t)((1+i)>> 8);
                msg[2] = (uint8_t)((1+i)>>16); msg[3] = (uint8_t)((1+i)>>24);
                // seed = "A_tensor" at msg[32..39]
                msg[32]='A'; msg[33]='_'; msg[34]='t'; msg[35]='e';
                msg[36]='n'; msg[37]='s'; msg[38]='o'; msg[39]='r';
                uint8_t h[32]; b3::hash_small(msg, 64, (const u32*)key_A, h);
                for (int j = 0; j < 32; ++j) {
                    int idx = i*32+j;
                    if (idx < nb) {
                        int8_t v = (int8_t)(((int)(h[j] & 63)) - 32);
                        out[idx] = v;
                        outf[idx] = sycl::half((float)((int)v * scale));
                    }
                }
            });
    }

    // Helper: launch permutation matrix kernel.
    // seed_bytes[8] = first 8 bytes of the seed tag ("A_tensor" or "B_tensor").
    auto launch_perm = [&](void* Km_v, void* Rm_v, const uint8_t* perm_key,
                           uint8_t s0, uint8_t s1, uint8_t s2, uint8_t s3,
                           uint8_t s4, uint8_t s5, uint8_t s6, uint8_t s7) {
        if (!Km_v && !Rm_v) return;
        auto* Km = static_cast<int8_t*>(Km_v);
        auto* Rm = static_cast<int8_t*>(Rm_v);
        if (Km) q->memset(Km, 0, (size_t)R * k);
        if (Rm) q->memset(Rm, 0, (size_t)k * R);
        int req = k, draws = (req*4 + 31) / 32;
        q->parallel_for<KPerm>(sycl::range<1>((size_t)((draws+tpb-1)/tpb)*tpb),
            [=](sycl::id<1> id) {
                int i = (int)id[0]; if (i >= draws) return;
                uint8_t msg[64] = {};
                msg[4]  = (uint8_t)((1+i)     ); msg[5] = (uint8_t)((1+i)>> 8);
                msg[6]  = (uint8_t)((1+i)>>16 ); msg[7] = (uint8_t)((1+i)>>24);
                // seed tag in [32..39]; rest zero
                msg[32]=s0; msg[33]=s1; msg[34]=s2; msg[35]=s3;
                msg[36]=s4; msg[37]=s5; msg[38]=s6; msg[39]=s7;
                uint8_t h[32]; b3::hash_small(msg, 64, (const u32*)perm_key, h);
                for (int kk = 0; kk < 8; ++kk) {
                    int L = i*8 + kk; if (L >= req) break;
                    u32 u = b3::load_le32(h + kk*4);
                    int fi = (int)(u & (uint32_t)(R-1));
                    uint64_t prod = (uint64_t)(uint32_t)(R-1) * (uint64_t)u;
                    int si = fi ^ (1 + (int)(uint32_t)(prod >> 32));
                    if (Km) { Km[(size_t)fi*req + L] = 1; Km[(size_t)si*req + L] = -1; }
                    if (Rm) { Rm[(size_t)L*R + fi] = 1; Rm[(size_t)L*R + si] = -1; }
                }
            });
    };

    if (EAR_K || EAR_R)
        launch_perm(EAR_K, EAR_R, key_A,
                    'A','_','t','e','n','s','o','r');

    if (EBR) {
        auto* out = static_cast<int8_t*>(EBR);
        auto* outf = static_cast<sycl::half*>(EBR_fp16);
        int nb = n * R, nh = (nb + 31) / 32;
        constexpr int scale = -4;  // kEBR
        q->parallel_for<KUniformB>(sycl::range<1>((size_t)((nh+tpb-1)/tpb)*tpb),
            [=](sycl::id<1> id) {
                int i = (int)id[0]; if (i >= nh) return;
                uint8_t msg[64] = {};
                msg[0] = (uint8_t)((1+i)    ); msg[1] = (uint8_t)((1+i)>> 8);
                msg[2] = (uint8_t)((1+i)>>16); msg[3] = (uint8_t)((1+i)>>24);
                msg[32]='B'; msg[33]='_'; msg[34]='t'; msg[35]='e';
                msg[36]='n'; msg[37]='s'; msg[38]='o'; msg[39]='r';
                uint8_t h[32]; b3::hash_small(msg, 64, (const u32*)key_B, h);
                for (int j = 0; j < 32; ++j) {
                    int idx = i*32+j;
                    if (idx < nb) {
                        int8_t v = (int8_t)(((int)(h[j] & 63)) - 32);
                        out[idx] = v;
                        outf[idx] = sycl::half((float)((int)v * scale));
                    }
                }
            });
    }

    if (EBL_K || EBL_R)
        launch_perm(EBL_K, EBL_R, key_B,
                    'B','_','t','e','n','s','o','r');
}

// ── bseed_expand: BLAKE3 XOF → int7 ───────────────────────────────────────
inline void launch_bseed_expand(const uint8_t* bseed, void* dst, int64_t n,
                                 sycl::queue* q) {
    // Copy seed words to device.  bseed is host memory, so use a captured copy.
    u32 sw[8];
    for (int i = 0; i < 8; ++i)
        sw[i] = (u32)bseed[i*4] | ((u32)bseed[i*4+1]<<8) |
                ((u32)bseed[i*4+2]<<16) | ((u32)bseed[i*4+3]<<24);
    u32 sw0=sw[0],sw1=sw[1],sw2=sw[2],sw3=sw[3],sw4=sw[4],sw5=sw[5],sw6=sw[6],sw7=sw[7];

    auto* out = static_cast<int8_t*>(dst);
    long nblk = (n + 63) / 64;
    const int tpb = 64;

    q->parallel_for<KBseed>(sycl::range<1>((size_t)((nblk+tpb-1)/tpb)*tpb),
        [=](sycl::id<1> id) {
            long j = (long)id[0]; if (j >= nblk) return;
            u32 seed_w[8] = {sw0,sw1,sw2,sw3,sw4,sw5,sw6,sw7};
            u32 cv[8]; b3::init_cv(cv, nullptr);
            u32 msg[16] = {};
            for (int i = 0; i < 8; ++i) msg[i] = seed_w[i];
            u32 o[16];
            b3::compress_full(cv, msg, (u64)j, 32,
                              b3::CHUNK_START | b3::CHUNK_END | b3::ROOT, o);
            long base = j * 64;
            for (int w = 0; w < 16; ++w) {
                u32 v = o[w];
                for (int b = 0; b < 4; ++b) {
                    long idx = base + w*4 + b;
                    if (idx < n) {
                        uint32_t by = (v >> (b*8)) & 0xFF;
                        out[idx] = (int8_t)((int)(by % 127) - 63);
                    }
                }
            }
        });
}

// ── k_add_i8 ──────────────────────────────────────────────────────────────
inline void launch_add_i8(const int8_t* A, const int32_t* E, int8_t* o, int n,
                           sycl::queue* q) {
    q->parallel_for<KAddI8>(sycl::range<1>((size_t)((n+255)/256)*256),
        [=](sycl::id<1> id) {
            int i = (int)id[0]; if (i >= n) return;
            o[i] = (int8_t)((int)A[i] + (int)(int8_t)E[i]);
        });
}

// ── k_transpose_i8 ────────────────────────────────────────────────────────
inline void launch_transpose_i8(const int8_t* in, int8_t* out, int rows, int cols,
                                  sycl::queue* q) {
    q->parallel_for<KTransposeI8>(sycl::range<1>((size_t)((rows*cols+255)/256)*256),
        [=](sycl::id<1> id) {
            int i = (int)id[0]; if (i >= rows*cols) return;
            int r = i / cols, c = i % cols;
            out[(size_t)c * rows + r] = in[i];
        });
}

// ── int8 GEMM: C[M,N] = A[M,K] × B[K,N] row-major, result int32 ──────────
// Used for E_A = EAL[m,R] × EAR_K[R,k].  B is [K,N] (B is EAR_K row-major).
// XMX fast path (A & B row-major → ideal for joint_matrix). One sub-group
// computes XM=8 rows × XNB N-tiles; column-major traversal keeps B in L2.
// Templated on the sub-group size: the XMX int8 B-tile width equals the
// sub-group size on Intel hardware (Xe-HPG: 8, Xe2: 16).
template<int SG>
inline void gemm_i8_xmx(const int8_t* A, const int8_t* B, int32_t* C,
                          int M, int N, int K, sycl::queue* q) {
    constexpr int XM = 8, XN = SG, XK = 32, XNB = 8;
    int rowBlocks = M / XM, nGroups = N / (XNB * XN);
    q->submit([&](sycl::handler& cgh) {
        cgh.parallel_for<KGemmI8X<SG>>(
            sycl::nd_range<2>({(size_t)nGroups, (size_t)rowBlocks * SG}, {1, SG}),
            [=](sycl::nd_item<2> item) [[sycl::reqd_sub_group_size(SG)]] {
                auto sg = item.get_sub_group();
                int grp_col = (int)item.get_group(0);
                int grp_row = (int)item.get_group(1);
                int gr0 = grp_row * XM;
                int gc0 = grp_col * XNB * XN;
                xmx::joint_matrix<sycl::sub_group, int8_t, xmx::use::a, XM, XK, xmx::layout::row_major> a;
                xmx::joint_matrix<sycl::sub_group, int8_t, xmx::use::b, XK, XN, xmx::layout::row_major> b;
                xmx::joint_matrix<sycl::sub_group, int32_t, xmx::use::accumulator, XM, XN> c[XNB];
                for (int t = 0; t < XNB; ++t) xmx::joint_matrix_fill(sg, c[t], 0);
                for (int k0 = 0; k0 < K; k0 += XK) {
                    xmx::joint_matrix_load(sg, a,
                        sycl::multi_ptr<const int8_t, sycl::access::address_space::global_space>(
                            A + (size_t)gr0 * K + k0), (size_t)K);
                    for (int t = 0; t < XNB; ++t) {
                        xmx::joint_matrix_load(sg, b,
                            sycl::multi_ptr<const int8_t, sycl::access::address_space::global_space>(
                                B + (size_t)k0 * N + gc0 + t * XN), (size_t)N);
                        xmx::joint_matrix_mad(sg, c[t], a, b, c[t]);
                    }
                }
                for (int t = 0; t < XNB; ++t)
                    xmx::joint_matrix_store(sg, c[t],
                        sycl::multi_ptr<int32_t, sycl::access::address_space::global_space>(
                            C + (size_t)gr0 * N + gc0 + t * XN), (size_t)N, xmx::layout::row_major);
            });
    });
}

inline void launch_gemm_i8(const int8_t* A, const int8_t* B, int32_t* C,
                             int M, int N, int K, sycl::queue* q) {
    constexpr int XM = 8, XK = 32, XNB = 8;
    // PEARL_XMX_ONLY_SG8 / _SG16 pin the variant at compile time (single-arch
    // AOT builds; also lets the offline compiler validate one generation's
    // DPAS shapes without the other's kernels in the module).
#if defined(PEARL_XMX_ONLY_SG8)
    bool hpg = true;
#elif defined(PEARL_XMX_ONLY_SG16)
    bool hpg = false;
#else
    bool hpg = is_xe_hpg(q);
#endif
    int xn = hpg ? 8 : 16;
    if (M % XM == 0 && N % (XNB * xn) == 0 && K % XK == 0) {
#if defined(PEARL_XMX_ONLY_SG8)
        gemm_i8_xmx<8>(A, B, C, M, N, K, q);
#elif defined(PEARL_XMX_ONLY_SG16)
        gemm_i8_xmx<16>(A, B, C, M, N, K, q);
#else
        if (hpg) gemm_i8_xmx<8>(A, B, C, M, N, K, q);
        else     gemm_i8_xmx<16>(A, B, C, M, N, K, q);
#endif
        return;
    }

    constexpr int TILE = 16;
    int gM = ((M + TILE-1) / TILE) * TILE;
    int gN = ((N + TILE-1) / TILE) * TILE;

    q->submit([&](sycl::handler& cgh) {
        sycl::local_accessor<int8_t, 1> lA(TILE * TILE, cgh);
        sycl::local_accessor<int8_t, 1> lB(TILE * TILE, cgh);
        cgh.parallel_for<KGemmI8>(
            sycl::nd_range<2>({(size_t)gM, (size_t)gN}, {TILE, TILE}),
            [=](sycl::nd_item<2> item) {
                int gr = (int)item.get_global_id(0);
                int gc = (int)item.get_global_id(1);
                int lr = (int)item.get_local_id(0);
                int lc = (int)item.get_local_id(1);
                int32_t acc = 0;
                for (int k0 = 0; k0 < K; k0 += TILE) {
                    lA[lr * TILE + lc] = (gr < M && k0+lc < K) ? A[gr * K + k0 + lc] : 0;
                    lB[lr * TILE + lc] = (k0+lr < K && gc < N) ? B[(k0+lr) * N + gc] : 0;
                    item.barrier(sycl::access::fence_space::local_space);
                    for (int p = 0; p < TILE; p += 4) {
                        acc += (int)lA[lr*TILE+p]   * (int)lB[p*TILE+lc]
                             + (int)lA[lr*TILE+p+1] * (int)lB[(p+1)*TILE+lc]
                             + (int)lA[lr*TILE+p+2] * (int)lB[(p+2)*TILE+lc]
                             + (int)lA[lr*TILE+p+3] * (int)lB[(p+3)*TILE+lc];
                    }
                    item.barrier(sycl::access::fence_space::local_space);
                }
                if (gr < M && gc < N) C[gr * N + gc] = acc;
            });
    });
}

// ── transcript-GEMM + fused PoW ────────────────────────────────────────────
//
// C = ApEA[m,k] × BpEB[n,k]^T  (BpEB is [n,k] row-major, so C[m,n] = ΣkApEA[m,k]*BpEB[n,k])
// Every R k-steps: XOR-fold per-tile partial sums into transcript[snap%16].
// At the end: BLAKE3(transcript, pow_key), compare to pow_target.
// On a hit: atomically set host_signal[0]=1 and fill the HostSignalHeader.
//
// Grid  : one work-group per (16×16) output tile
// WG    : 16×16 = 256 threads
// k-tile: BK = 32 (shared mem staging)
//
// XMX (Intel Xe Matrix Extensions) implementation. One sub-group of 16
// work-items computes NB adjacent 16x16 output tiles via int8 joint_matrix
// DPAS, reusing the loaded A fragment across all NB tiles (the kernel is
// memory-bound on redundant A reads, so N-blocking is the main throughput
// lever). Each tile keeps its own 16-word transcript; the XOR-fold and fused
// BLAKE3 PoW check are bit-identical to the scalar reference (validated offline
// against a scalar transcript). Requires m%16==0, n%(16*NB)==0, k%TK==0 and R a
// multiple of TK (true for the production shapes: m=4096,n=32768,k=2048,R=128).
inline void launch_tgemm_pow(const int8_t* ApEA, const int8_t* Bt,
                              int m, int n, int k, int R,
                              const u32* pow_key, const u32* pow_target,
                              int* host_signal, uint8_t* hdr,
                              sycl::queue* q, int nStride = 0) {
    // m,n here are the SEARCH grid dims (how many tiles to sweep). The committed
    // matrices may be larger: Bt's row stride is nStride (full committed N) so we
    // can search a sub-window [0,m)×[0,n) of a full-size [k, nStride] B matrix.
    if (nStride <= 0) nStride = n;
    // Bt is BpEB transposed to [k,n] row-major (fast XMX use::b row_major load).
    constexpr int TILE = 16;
    constexpr int TM = 8, TK = 32;            // XMX int8 tile: M=8, K=32 on every Xe generation
    constexpr int MB = 1;                     // M-block per sub-group.
    // TN (the XMX B-tile width) equals the sub-group size and is GENERATION-
    // SPECIFIC: Xe2 (B-series) = 16 → one N-half per 16-wide tile; Xe-HPG
    // (A-series) = 8 → two N-halves per tile, mirroring the existing two
    // TM=8 M-halves. The transcript fold is a pure XOR over the tile's
    // partial sums, and XOR is commutative — so folding 2×NHALF fragments
    // instead of 2 produces a BIT-IDENTICAL transcript (and thus identical
    // shares) regardless of which path ran.

    int numTilesM = (m + TILE - 1) / TILE;
    int numTilesN = (n + TILE - 1) / TILE;

    auto run = [&](auto MBC, auto NBC, auto SGC) {
        constexpr int RM = decltype(MBC)::value;
        constexpr int RN = decltype(NBC)::value;
        constexpr int SG = decltype(SGC)::value;
        constexpr int TN = SG;                // XMX B-tile width == sub-group size
        constexpr int NHALF = TILE / TN;      // N-fragments per 16-wide tile (1 or 2)
        int numGroupsM = numTilesM / RM;
        int numGroupsN = numTilesN / RN;
        static bool dbg_once = false;
        if (!dbg_once) {
            dbg_once = true;
            fprintf(stderr, "[pearl_sycl dbg] tgemm RM=%d RN=%d SG=%d groups=%dx%d m=%d n=%d k=%d R=%d nStride=%d\n",
                    RM, RN, SG, numGroupsM, numGroupsN, m, n, k, R, nStride);
        }
        q->submit([&](sycl::handler& cgh) {
            sycl::local_accessor<uint32_t, 1> trSlm(RM * RN * 16, cgh);
            // Column-major traversal: M is the FAST grid dimension (dim1) so
            // work-groups dispatched together share an N-group and keep its B
            // columns hot in L2 across all M-tiles (~8× less GDDR6 B traffic).
            sycl::nd_range<2> ndr({(size_t)numGroupsN, (size_t)numGroupsM * SG}, {1, SG});
            // RM>=2 doubles the mA/mC fragment count past the default 128-GRF
            // budget; request the large (256-reg) GRF mode for just these
            // instantiations so they don't spill (measured: MB=2+NB=4 spills to
            // 12.8 TMADs/s at 128 GRF, runs 35.9 at 256 on a B580). RM==1
            // kernels keep the default GRF and full occupancy.
            auto kfn = [=](sycl::nd_item<2> item) [[sycl::reqd_sub_group_size(SG)]] {
                    auto sg = item.get_sub_group();
                    int grp_col = (int)item.get_group(0);   // N-group (slow)
                    int grp_row = (int)item.get_group(1);   // M-group (fast)
                    int lid = (int)item.get_local_id(1);

                    xmx::joint_matrix<sycl::sub_group, int8_t, xmx::use::a, TM, TK, xmx::layout::row_major> mA[2 * RM];
                    xmx::joint_matrix<sycl::sub_group, int8_t, xmx::use::b, TK, TN, xmx::layout::row_major> mB;
                    xmx::joint_matrix<sycl::sub_group, int32_t, xmx::use::accumulator, TM, TN> mC[2 * RM][RN * NHALF];
                    for (int r = 0; r < 2 * RM; ++r)
                        for (int t = 0; t < RN * NHALF; ++t) xmx::joint_matrix_fill(sg, mC[r][t], 0);
                    if (lid == 0) for (int e = 0; e < RM * RN * 16; ++e) trSlm[e] = 0;

                    int snap = 0;

                    // Outer loop over R-blocks; inner DPAS k-steps fully unrolled (no
                    // conditional inside, so the compiler pipelines the DPAS cleanly).
                    for (int kb = 0; kb < k; kb += R) {
                        #pragma unroll
                        for (int ks = 0; ks < R; ks += TK) {
                            int k0 = kb + ks;
                            // Load A row-block halves once (reused across all RN N-tiles).
                            for (int r = 0; r < 2 * RM; ++r) {
                                int row = (grp_row * RM + r / 2) * TILE + (r % 2) * TM;
                                xmx::joint_matrix_load(sg, mA[r],
                                    sycl::multi_ptr<const int8_t, sycl::access::address_space::global_space>(
                                        ApEA + (size_t)row * k + k0), (size_t)k);
                            }
                            // For each N-fragment: load B once (reused across all MB row-blocks).
                            // NHALF==1 (Xe2) keeps the original one-load-per-tile shape;
                            // NHALF==2 (Xe-HPG) covers each 16-wide tile in two TN=8 halves.
                            for (int t = 0; t < RN * NHALF; ++t) {
                                int col = (grp_col * RN + t / NHALF) * TILE + (t % NHALF) * TN;
                                xmx::joint_matrix_load(sg, mB,
                                    sycl::multi_ptr<const int8_t, sycl::access::address_space::global_space>(
                                        Bt + (size_t)k0 * nStride + col), (size_t)nStride);
                                for (int r = 0; r < 2 * RM; ++r)
                                    xmx::joint_matrix_mad(sg, mC[r][t], mA[r], mB, mC[r][t]);
                            }
                        }

                        // --- Transcript snap once per R-block (barrier-free XOR) ---
                        // Every int32 partial of the 16×16 tile is XORed exactly
                        // once, across however many fragments cover the tile —
                        // bit-identical for NHALF==1 and NHALF==2.
                        for (int mb = 0; mb < RM; ++mb)
                            for (int t = 0; t < RN; ++t) {
                                uint32_t part = 0;
                                for (int h = 0; h < NHALF; ++h) {
                                    xmx::joint_matrix_apply(sg, mC[2*mb][t*NHALF+h],   [&](int32_t v) { part ^= (uint32_t)v; });
                                    xmx::joint_matrix_apply(sg, mC[2*mb+1][t*NHALF+h], [&](int32_t v) { part ^= (uint32_t)v; });
                                }
                                uint32_t xv = sycl::reduce_over_group(sg, part, sycl::bit_xor<uint32_t>());
                                if (lid == 0) {
                                    int idx = (mb * RN + t) * 16 + snap % 16;
                                    trSlm[idx] = b3::rotl32(trSlm[idx], 13) ^ xv;
                                }
                            }
                        ++snap;
                    }

                    // --- PoW check: sub-group leader hashes each tile's transcript ---
                    if (lid == 0) {
                        for (int mb = 0; mb < RM; ++mb)
                            for (int t = 0; t < RN; ++t) {
                                uint8_t tb[64];
                                for (int e = 0; e < 16; ++e) {
                                    uint32_t tw = trSlm[(mb * RN + t) * 16 + e];
                                    tb[e*4]   = (uint8_t)(tw);
                                    tb[e*4+1] = (uint8_t)(tw >> 8);
                                    tb[e*4+2] = (uint8_t)(tw >> 16);
                                    tb[e*4+3] = (uint8_t)(tw >> 24);
                                }
                                uint8_t hh[32];
                                b3::hash_small(tb, 64, pow_key, hh);
                                u32 hw[8];
                                for (int e = 0; e < 8; ++e)
                                    hw[e] = (u32)hh[e*4] | ((u32)hh[e*4+1]<<8) |
                                            ((u32)hh[e*4+2]<<16) | ((u32)hh[e*4+3]<<24);
                                int fnd = 1;
                                for (int e = 7; e >= 0; --e) {
                                    if (hw[e] > pow_target[e]) { fnd = 0; break; }
                                    if (hw[e] < pow_target[e]) break;
                                }
                                if (fnd) {
                                    sycl::atomic_ref<int,
                                        sycl::memory_order::relaxed,
                                        sycl::memory_scope::device,
                                        sycl::access::address_space::global_space> sig(*host_signal);
                                    int expected = 0;
                                    if (sig.compare_exchange_strong(expected, 1)) {
                                        int tile_row = grp_row * RM + mb;
                                        int tile_col = grp_col * RN + t;
                                        *(int*)(hdr + 0) = 1;  // status = kSignalTriggered
                                        ((unsigned*)(hdr + 40))[0] = (unsigned)tile_row;
                                        ((unsigned*)(hdr + 40))[1] = (unsigned)tile_col;
                                        ((unsigned*)(hdr + 40))[2] = 0;
                                        *(unsigned short*)(hdr + 64) = 16;
                                        for (int e = 0; e < 16; ++e) {
                                            hdr[66  + e] = (uint8_t)e;
                                            hdr[322 + e] = (uint8_t)e;
                                        }
                                        ((int*)(hdr + 592))[0] = 16;
                                        ((int*)(hdr + 592))[1] = 16;
                                        ((int*)(hdr + 592))[2] = 0;
                                    }
                                }
                            }
                    }
                };
            if constexpr (RM >= 2) {
                sycl::ext::oneapi::experimental::properties props{
                    sycl::ext::intel::experimental::grf_size<256>};
                cgh.parallel_for<KTgemmPow<RM, RN, SG>>(ndr, props, kfn);
            } else {
                cgh.parallel_for<KTgemmPow<RM, RN, SG>>(ndr, kfn);
            }
        });
    };

    // N-blocking (NB) is both the main throughput lever (B-reuse per A-load)
    // AND the dominant accumulator-register consumer: each sub-group holds
    // mC[2*MB][NB*NHALF] joint_matrix fragments.
    //   • Xe2  (sg16, NHALF=1): NB=4 → 8 fragments → ~64 int32/lane. Fits; the
    //     measured-good B-series default.
    //   • Xe-HPG (sg8, NHALF=2): NB=4 → 16 fragments → ~128 int32/lane, which
    //     SPILLS the smaller Xe-HPG GRF and craters throughput (~60× under
    //     potential). NB=2 → 8 fragments → matches the Xe2 footprint.
    // So the per-arch default is NB=2 on sg8, NB=4 on sg16. AKOYA_TGEMM_NB
    // overrides it to sweep {1,2,4} on a tester. Re-blocking N only changes the
    // grid decomposition; each output tile's transcript/PoW is computed from its
    // own accumulators, so results are BIT-IDENTICAL across NB — shares unchanged.
    //
    // AKOYA_TGEMM_MB (default 1) blocks M the same way: MB=2 reuses each loaded
    // B fragment across 4 A-mads instead of 2 (loads/mad 0.75 → 0.5) at the cost
    // of doubling the mA/mC register footprint — MB=2×NB=4 only fits in
    // large-GRF mode (SYCL_PROGRAM_COMPILE_OPTIONS=-cl-intel-256-GRF-per-thread).
    // Same bit-identical argument as NB: pure grid re-tiling.
    auto dispatchNB = [&](auto SGC, auto MBC, int nb) {
        if      (nb >= 4 && numTilesN % 4 == 0) run(MBC, std::integral_constant<int,4>{}, SGC);
        else if (nb >= 2 && numTilesN % 2 == 0) run(MBC, std::integral_constant<int,2>{}, SGC);
        else                                    run(std::integral_constant<int,1>{},  std::integral_constant<int,1>{},  SGC);
    };
    auto dispatchMB = [&](auto SGC, int nb, int mb) {
        if (mb >= 2 && numTilesM % 2 == 0) dispatchNB(SGC, std::integral_constant<int,2>{},  nb);
        else                               dispatchNB(SGC, std::integral_constant<int,MB>{}, nb);
    };
    // Defaults: Xe2 (sg16) → MB=2 (+8% on B580: 33.3 → 35.9 TMADs/s, with the
    // grf_size<256> property baked into the RM>=2 kernels above); Xe-HPG (sg8)
    // → MB=1 until validated on A-series hardware. AKOYA_TGEMM_MB=1|2 overrides.
    int nbEnv = []{ const char* v = getenv("AKOYA_TGEMM_NB"); return (v && atoi(v) > 0) ? atoi(v) : 0; }();
    int mbEnv = []{ const char* v = getenv("AKOYA_TGEMM_MB"); return (v && atoi(v) > 0) ? atoi(v) : 0; }();
#if defined(PEARL_XMX_ONLY_SG8)
    dispatchMB(std::integral_constant<int, 8>{},  nbEnv > 0 ? nbEnv : 2, mbEnv > 0 ? mbEnv : 1);
#elif defined(PEARL_XMX_ONLY_SG16)
    dispatchMB(std::integral_constant<int, 16>{}, nbEnv > 0 ? nbEnv : 4, mbEnv > 0 ? mbEnv : 2);
#else
    if (is_xe_hpg(q)) dispatchMB(std::integral_constant<int, 8>{},  nbEnv > 0 ? nbEnv : 2, mbEnv > 0 ? mbEnv : 1);
    else              dispatchMB(std::integral_constant<int, 16>{}, nbEnv > 0 ? nbEnv : 4, mbEnv > 0 ? mbEnv : 2);
#endif
}

} // namespace pk
