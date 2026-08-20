// pearl_gemm_capi_sycl.cpp — Intel Arc / SYCL backend for the pearl_capi_* C ABI.
//
// Mirrors pearl_gemm_capi_rocm.cpp: same ABI surface, same algorithmic steps,
// implemented with SYCL/oneAPI instead of HIP.
//
// Build (oneAPI DPC++ / icpx, JIT compilation — works on any Intel GPU):
//   icpx -fsycl -O3 -fPIC -shared \
//     -I ../../csrc -I .. pearl_gemm_capi_sycl.cpp -o libpearl_gemm_capi.so
//
// For AOT (Intel Arc A-series, e.g. A770):
//   icpx -fsycl -fsycl-targets=spir64_gen \
//     -Xsycl-target-backend=spir64_gen "-device intel_gpu_acm_g10" \
//     -O3 -fPIC -shared -I ../../csrc -I .. pearl_gemm_capi_sycl.cpp \
//     -o libpearl_gemm_capi.so

#include "pearl_kernels.hpp"
#include "capi/pearl_gemm_capi.h"
#include <cstdlib>
#include <cstring>
#include <cstdio>
#include <sycl/sycl.hpp>
#include <mutex>
#include <atomic>
#include <chrono>
#include <unordered_map>
#include <string>

#define HQUEUE(s) static_cast<sycl::queue*>(s)

static std::mutex g_seed_mutex;
static std::unordered_map<sycl::queue*, uint64_t> g_last_base_seed;

// Multi-GPU: NEO's OpenCL USM tracking USED TO corrupt under CONCURRENT
// malloc_device/free from two device contexts in one process (observed with
// 2× B580: SIGSEGV in urUSMFree during simultaneous σ-installs, and an
// enqueue_svm.h abort at the next SVM op). Serialize the rare, alloc-heavy
// entry points (workspace alloc/free, install/noise_B) across workers.
// The per-iter hot path allocates nothing and stays lock-free. Recursive
// because install_B calls noise_B.
//
// STATUS 2026-07-30: multi-GPU OpenCL now runs clean in the field on 2× B580
// (HiveOS/Ubuntu 24.04, intel-opencl-icd 25.18.33578.6) — the original abort is
// no longer reproducible and was presumably fixed in NEO. This mutex is
// therefore probably obsolete.
//
// It is deliberately NOT removed. The failure it guards against was INTERMITTENT
// SILENT CORRUPTION, so "it didn't crash today" is not evidence of safety, and
// the cost is near zero: these are per-job entry points, not the per-iter hot
// path, which never allocates and stays lock-free either way. Removing it buys
// nothing measurable and risks a heisenbug that only shows up as bad shares.
// If you do want it gone, soak 2 GPUs for hours on a NEO you have pinned, and
// remember rigs run whatever NEO their distro ships — including older ones.
static std::recursive_mutex g_usm_heavy_mutex;

static inline int rc_sycl(const char* where) {
    // rc_sycl is only ever called from inside a catch block, so `throw;`
    // re-raises the in-flight exception and lets us surface its .what()
    // instead of an opaque rc=-100.
    try { throw; }
    catch (const std::exception& e) {
        fprintf(stderr, "[pearl_sycl] error in %s: %s\n", where, e.what());
    }
    catch (...) {
        fprintf(stderr, "[pearl_sycl] error in %s: (non-std exception)\n", where);
    }
    return -100;
}

// ── Workspace struct ─────────────────────────────────────────────────────────

struct SyclWorkspace {
    int m, n, k, r, ntiles;
    int sm_cap = 0;          // rows gemmScratch is sized for (search-M window)
    // Device-side buffers
    int*     host_signal;    // device mem, 8 bytes (int + padding)
    uint8_t* dHeader;        // device mem, 640 bytes
    int32_t* gemmScratch;    // sm_cap*k int32 for E_A (only the search rows)
    uint8_t* bseedScratch;   // 32 bytes for bseed expand seed copy
    int8_t*  Bt = nullptr;   // BpEB transposed to [k,n] row-major (for fast XMX B load)
    bool     bt_valid = false;
    // Pre-allocated noise_B scratch buffers (reused across σ installations)
    int      nb_cn_cap = 0;
    int8_t*  nb_EBRt = nullptr;
    int8_t*  nb_Bkn  = nullptr;
    int8_t*  nb_Bnoi = nullptr;
    int32_t* nb_EB   = nullptr;
    PearlCapiWorkspaceParams params;
    bool installed = false;
    sycl::queue* q_alloc = nullptr;  // queue used for allocation (context anchor)
};

// ── C ABI ────────────────────────────────────────────────────────────────────

#ifdef _WIN32
#  define PEARL_EXPORT __declspec(dllexport)
#else
#  define PEARL_EXPORT
#endif

// Search-M window: how many A rows the per-iter E_A/ApEA/tgemm actually sweep.
// Must be computed identically in workspace_alloc (to size gemmScratch) and in
// iter (to drive the kernels). Tile-aligned to 16; clamped to [16, m].
static int compute_search_m(int m) {
    // ARC_SEARCH_M (legacy alias: AKOYA_SEARCH_M). pk::tune_env_int treats an
    // empty or non-numeric value as unset and falls back to the default rather
    // than clamping to 16: an empty ARC_SEARCH_M= (e.g. left behind by a shell
    // that "unsets" by assigning blank) would otherwise silently shrink the
    // search window 256× while the C# side still reports full-window hashrate.
    // The kernel MUST read the same spelling the C# host writes — see the
    // tune_env_int comment in pearl_kernels.hpp for what went wrong before.
    int sm = pk::tune_env_int("SEARCH_M", 4096);
    if (sm > m) sm = m;
    sm = (sm / 16) * 16;
    if (sm < 16) sm = 16;
    return sm;
}


extern "C" {

PEARL_EXPORT int pearl_capi_abi_version(void) { return 4; }
PEARL_EXPORT const char* pearl_capi_build_profile(void) { return "arc"; }

// GPU family this kernel was AOT-compiled for, so the host can reject a
// wrong-card launch with a clear message instead of crashing on the first
// kernel (a single-arch AOT gen binary only runs on its target generation).
// "acm" = Alchemist/Xe-HPG (sg8), "bmg" = Battlemage/Xe2 (sg16), "fat" = one
// binary carrying BOTH generations' AOT kernels (runs on any Arc via runtime
// is_xe_hpg dispatch), "" = JIT (any Arc). The host wrong-card guard only fires
// for "acm"/"bmg"; "fat" and "" fall through so they run on any card.
PEARL_EXPORT const char* pearl_capi_target_family(void) {
#if defined(PEARL_FAT_AOT)
    return "fat";
#elif defined(PEARL_XMX_ONLY_SG8)
    return "acm";
#elif defined(PEARL_XMX_ONLY_SG16)
    return "bmg";
#else
    return "";
#endif
}
PEARL_EXPORT int pearl_capi_supports_sm(int, int) { return 1; }
// Search-M window the per-iter kernels actually sweep (== compute_search_m(m)).
// Exposed so the C# host can size search-window-only device buffers (e.g. ApEA)
// to the same value the kernel uses — reading the same UCRT getenv(ARC_SEARCH_M)
// — so the host allocation can't diverge from the kernel even when autotune sets
// SEARCH_M natively at runtime.
PEARL_EXPORT int pearl_capi_search_m(int m) { return compute_search_m(m); }
PEARL_EXPORT int pearl_capi_get_host_signal_sync_size(void) { return 8; }
PEARL_EXPORT int pearl_capi_get_host_signal_header_size(void) { return 640; }
PEARL_EXPORT int64_t pearl_capi_get_required_scratchpad_bytes(int64_t matrix_bytes, int) {
    int64_t nchunks = (matrix_bytes + 1023) / 1024; if (nchunks < 1) nchunks = 1;
    return 2 * nchunks * 32 + 4096;
}

PEARL_EXPORT int pearl_capi_lcg_int7_fill(void* dst, int64_t n,
                              uint64_t seed_lo, uint64_t seed_hi, void* stream) {
    try {
        auto splitmix = [](uint64_t z) -> uint64_t {
            z += 0x9E3779B97F4A7C15ULL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ULL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBULL;
            return z ^ (z >> 31);
        };
        uint64_t base = splitmix(seed_lo ^ splitmix(seed_hi));
        auto* q = HQUEUE(stream);
        {
            std::lock_guard<std::mutex> lock(g_seed_mutex);
            g_last_base_seed[q] = base;
        }
        pk::launch_lcg_int7_fill(dst, n, base, q);
        return 0;
    } catch (...) { return rc_sycl("lcg_int7_fill"); }
}

PEARL_EXPORT int pearl_capi_lcg_int7_fill_indirect(void*, int64_t, const void*, uint64_t, uint64_t, void*) {
    return -1;
}

PEARL_EXPORT int pearl_capi_tensor_hash(const uint8_t* data, uint32_t data_size, uint8_t* out,
                            const uint8_t* key, uint32_t, uint32_t, uint32_t, uint32_t,
                            uint8_t* roots, int, void* stream) {
    try {
        auto* q = HQUEUE(stream);
        uint64_t base = 0;
        bool has_base = false;
        {
            std::lock_guard<std::mutex> lock(g_seed_mutex);
            auto it = g_last_base_seed.find(q);
            if (it != g_last_base_seed.end()) {
                base = it->second;
                has_base = true;
            }
        }
        if (has_base) {
            pk::launch_lcg_int7_fill(const_cast<uint8_t*>(data), (int64_t)data_size, base, q);
        }
        pk::parallel_tensor_hash(data, (long)data_size, (const u32*)key,
                                 (u32*)roots, out, q);
        return 0;
    } catch (...) { return rc_sycl("tensor_hash"); }
}

PEARL_EXPORT int pearl_capi_tensor_hash_leaf_cvs(const uint8_t* d, uint32_t s, uint8_t* o,
                                     const uint8_t* k, uint32_t, uint32_t, uint32_t, uint32_t,
                                     uint8_t* r, uint8_t* leaf_cvs, int, void* st) {
    try {
        auto* q = HQUEUE(st);
        uint64_t base = 0;
        bool has_base = false;
        {
            std::lock_guard<std::mutex> lock(g_seed_mutex);
            auto it = g_last_base_seed.find(q);
            if (it != g_last_base_seed.end()) {
                base = it->second;
                has_base = true;
            }
        }
        if (has_base) {
            pk::launch_lcg_int7_fill(const_cast<uint8_t*>(d), (int64_t)s, base, q);
        }
        pk::parallel_tensor_hash(d, (long)s, (const u32*)k, (u32*)r, o,
                                 q, leaf_cvs);
        return 0;
    } catch (...) { return rc_sycl("tensor_hash_leaf_cvs"); }
}

// Trigger-path fused A regen + Merkle leaf-CV export. Regenerates A for
// (seed_lo, seed_hi) IN-REGISTER via the fused kernel — writes only the first
// `persist_bytes` (the sm search rows the opened tile lives in) to `a_out`, and
// produces the full-A leaf-CV table (`leaf_cvs`) + Merkle root (`out`) without
// ever materializing full A in DRAM. Lets the host shrink the resident A buffer
// to the search window (the iter only needs sm rows; this covers the trigger).
// base = splitmix(seed_lo ^ splitmix(seed_hi)) — identical to the per-iter fill.
PEARL_EXPORT int pearl_capi_tensor_hash_fused_leaf_cvs(
        uint8_t* a_out, uint64_t seed_lo, uint64_t seed_hi,
        int64_t len, int64_t persist_bytes,
        const uint8_t* key, uint8_t* roots, uint8_t* out, uint8_t* leaf_cvs,
        void* stream) {
    try {
        auto* q = HQUEUE(stream);
        auto splitmix = [](uint64_t z) -> uint64_t {
            z += 0x9E3779B97F4A7C15ULL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ULL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBULL;
            return z ^ (z >> 31);
        };
        uint64_t base = splitmix(seed_lo ^ splitmix(seed_hi));
        pk::parallel_tensor_hash_fused(a_out, (long)len, (const u32*)key,
                                       (u32*)roots, out, q, base,
                                       (long)persist_bytes, leaf_cvs);
        return 0;
    } catch (...) { return rc_sycl("tensor_hash_fused_leaf_cvs"); }
}

// Salted-seed hardfork (pearl PR #280 + #282, mainnet height 99,000).
//
// NOT the mining path. This started life as a setter rather than a params field
// because an added export is additive and keeps the ABI still — but "process
// global" was the wrong shape for the value, whatever it cost to change. A
// batch bakes the flag in when it is ENQUEUED and the host re-derives the same
// seeds when it BUILDS the share; a store landing between those two reads makes
// the miner search one noise field and prove another, and every share of that
// batch dies locally as claimedHash > liveTarget. The mining path therefore
// reads PearlCapiWorkspaceParams::salted_seeds, which is installed per σ with
// both streams drained (ABI v3).
//
// What survives here is the process-wide DEFAULT, for callers that have no
// workspace: the standalone commitment-hash export and diagnostics. Keep it in
// step with what the host installs, so a trace read through this global is not
// misleading.
static std::atomic<int> g_salted_seed{0};

// ARC_PRL_SEED_TRACE one-shot flags. File scope, not function-local statics,
// because workspace_install_params RE-ARMS them: the trace has to be readable
// against a specific σ. Scoped per-process it fires during the startup
// benchmark and never again, so its roots always belong to a different σ than
// any share — which makes it useless for the one comparison it exists to
// support: the device's A/B Merkle roots against the host's, for the SAME σ.
static std::atomic<int> g_seed_trace_done_legacy{0};
static std::atomic<int> g_seed_trace_done_salted{0};

PEARL_EXPORT void pearl_capi_set_salted_seed(int on) {
    g_salted_seed.store(on ? 1 : 0, std::memory_order_relaxed);
}

PEARL_EXPORT int pearl_capi_get_salted_seed(void) {
    return g_salted_seed.load(std::memory_order_relaxed);
}

// Device-side noise-seed derivation, exposed for EQUIVALENCE TESTING.
//
// The host re-derives these same seeds in C# when it builds a share. Nothing
// checks that the two agree — and if they ever stop agreeing the miner searches
// one noise field and submits proofs for another, which costs 100% of shares
// while every dial still reads healthy. This export is how that gets tested:
// same inputs through the GPU path, compared against the C# path, before a fork
// makes it expensive to find out.
//
// Runs the real kernel, not a copy of it, so the test cannot pass against an
// implementation the miner does not use.
PEARL_EXPORT int pearl_capi_derive_noise_seeds(const uint8_t* A_merkle_root,
                                               const uint8_t* B_merkle_root,
                                               const uint8_t* job_key,
                                               int m, int n, int salted,
                                               uint8_t* out_a_seed,
                                               uint8_t* out_b_seed,
                                               void* stream) {
    try {
        // Self-contained: a test harness should not have to build a stream first,
        // and the derivation is pure BLAKE3 so any device gives the same answer.
        sycl::queue local;
        auto* q = stream ? HQUEUE(stream) : &local;
        uint8_t* dA  = sycl::malloc_device<uint8_t>(32, *q);
        uint8_t* dB  = sycl::malloc_device<uint8_t>(32, *q);
        uint8_t* dK  = sycl::malloc_device<uint8_t>(32, *q);
        uint8_t* dCA = sycl::malloc_device<uint8_t>(32, *q);
        uint8_t* dCB = sycl::malloc_device<uint8_t>(32, *q);
        q->memcpy(dA, A_merkle_root, 32);
        q->memcpy(dB, B_merkle_root, 32);
        q->memcpy(dK, job_key, 32);
        q->wait();
        pk::launch_commitment_hash(dA, dB, dK, dCA, dCB, m, n, salted != 0, q);
        q->wait();
        q->memcpy(out_a_seed, dCA, 32);
        q->memcpy(out_b_seed, dCB, 32);
        q->wait();
        sycl::free(dA, *q); sycl::free(dB, *q); sycl::free(dK, *q);
        sycl::free(dCA, *q); sycl::free(dCB, *q);
        return 0;
    } catch (...) { return rc_sycl("derive_noise_seeds"); }
}

// pearl_capi_commitment_hash_from_merkle_roots WAS HERE AND IS DELETED ON PURPOSE.
//
// It wrapped launch_commitment_hash with (m=0, n=0, salted=false) hardcoded,
// and carried a comment asserting it had no caller in this miner. That comment
// was wrong: pearl_capi_install_B called it, which pinned the entire B-side
// noise field to the LEGACY seed derivation from the salted-seed fork onward
// and made every share unprovable. A convenience wrapper that silently
// substitutes defaults for σ state is a trap, and the comment claiming it was
// dead is what let it survive review. Call pk::launch_commitment_hash directly
// with real m/n and the real flag.

PEARL_EXPORT int pearl_capi_noise_gen(int R, int m, int n, int k,
                          void* EAL, void* EAL_fp16, void* EAR_R, void* EAR_K,
                          void* EBL_R, void* EBL_K, void* EBR, void* EBR_fp16,
                          const uint8_t* key_A, const uint8_t* key_B, void* stream) {
    try {
        pk::launch_noise_gen(R, m, n, k, EAL, EAL_fp16, EAR_R, EAR_K,
                             EBL_R, EBL_K, EBR, EBR_fp16, key_A, key_B, HQUEUE(stream));
        return 0;
    } catch (...) { return rc_sycl("noise_gen"); }
}
PEARL_EXPORT int pearl_capi_bseed_expand_raw_device(const uint8_t* bseed, void* dst, int64_t n,
                                        void* stream) {
    try {
        pk::launch_bseed_expand(bseed, dst, n, HQUEUE(stream));
        return 0;
    } catch (...) { return rc_sycl("bseed_expand"); }
}

PEARL_EXPORT int pearl_capi_bseed_expand_range_raw_device(const uint8_t*, uint64_t, void*, int64_t, void*) {
    return -1;
}

// ── Workspace ────────────────────────────────────────────────────────────────

PEARL_EXPORT int pearl_capi_workspace_alloc(int32_t m, int32_t n, int32_t k, int32_t r,
                                int, int, void** out, void* stream) {
    std::lock_guard<std::recursive_mutex> usm_lk(g_usm_heavy_mutex);
    try {
        auto* q = HQUEUE(stream);
        auto* w = new SyclWorkspace();
        w->m = m; w->n = n; w->k = k; w->r = r;
        w->ntiles = (m / 16) * (n / 16);
        w->q_alloc = q;
        // gemmScratch only ever holds E_A for the SEARCH rows ([sm,k]), never the
        // full [m,k]. At canonical m=131072,k=4096 the old full-size alloc was a
        // 2 GiB int32 buffer of which <2% was used; size it to the search window.
        w->sm_cap = compute_search_m(m);
        w->host_signal = sycl::malloc_device<int>(2, *q);      // 8 bytes
        w->dHeader     = sycl::malloc_device<uint8_t>(640, *q);
        w->gemmScratch = sycl::malloc_device<int32_t>((size_t)w->sm_cap * k, *q);
        w->bseedScratch = sycl::malloc_device<uint8_t>(32, *q);
        w->Bt = sycl::malloc_device<int8_t>((size_t)n * k, *q);  // [k,n] transposed B
        // Pre-allocate noise_B block scratch for default cn = 16384 (or n if smaller)
        w->nb_cn_cap = (n < 16384) ? n : 16384;
        w->nb_EBRt = sycl::malloc_device<int8_t>((size_t)r * w->nb_cn_cap, *q);
        w->nb_Bkn  = sycl::malloc_device<int8_t>((size_t)k * w->nb_cn_cap, *q);
        w->nb_Bnoi = sycl::malloc_device<int8_t>((size_t)k * w->nb_cn_cap, *q);
        w->nb_EB   = sycl::malloc_device<int32_t>((size_t)k * w->nb_cn_cap, *q);
        *out = w;
        return 0;
    } catch (...) { return rc_sycl("workspace_alloc"); }
}

PEARL_EXPORT int pearl_capi_workspace_free(void* ws, void*) {
    auto* w = static_cast<SyclWorkspace*>(ws);
    if (!w) return -1;
    std::lock_guard<std::recursive_mutex> usm_lk(g_usm_heavy_mutex);
    try {
        auto& q = *w->q_alloc;
        q.wait();
        sycl::free(w->host_signal,  q);
        sycl::free(w->dHeader,      q);
        sycl::free(w->gemmScratch,  q);
        sycl::free(w->bseedScratch, q);
        if (w->Bt) sycl::free(w->Bt, q);
        if (w->nb_EBRt) sycl::free(w->nb_EBRt, q);
        if (w->nb_Bkn)  sycl::free(w->nb_Bkn, q);
        if (w->nb_Bnoi) sycl::free(w->nb_Bnoi, q);
        if (w->nb_EB)   sycl::free(w->nb_EB, q);
    } catch (...) {}
    delete w;
    return 0;
}

PEARL_EXPORT int pearl_capi_workspace_install_params(void* ws, const PearlCapiWorkspaceParams* p) {
    auto* w = static_cast<SyclWorkspace*>(ws);
    if (!w || !p) return -1;
    w->params   = *p;
    w->installed = true;
    w->bt_valid = false;   // BpEB changed → transposed copy is stale
    // Re-arm the seed trace for this σ (see the flags' comment).
    g_seed_trace_done_legacy.store(0, std::memory_order_relaxed);
    g_seed_trace_done_salted.store(0, std::memory_order_relaxed);
    return 0;
}

// ── iter ─────────────────────────────────────────────────────────────────────

static const bool g_prof = [](){ const char* e = getenv("AKOYA_PROFILE_ITER"); return e && e[0]=='1'; }();

// Diagnostic-build extras (all no-ops unless AKOYA_PROFILE_ITER=1):
//  AKOYA_PROFILE_LOG=<path>   also append the per-step breakdown to a file
//                             (line-buffered + fflush'd → survives Ctrl-C).
//  AKOYA_PROFILE_WINDOWS=<N>  profile only the first N×50 iters, then auto-disable
//                             so the run continues full-speed and the C# steady-state
//                             hashrate line is undistorted by the per-step q->wait()s.
static const int g_prof_win_limit = [](){ const char* e = getenv("AKOYA_PROFILE_WINDOWS");
    return (e && atoi(e) > 0) ? atoi(e) : 0; }();
static FILE* prof_log() {
    static FILE* f = [](){ const char* p = getenv("AKOYA_PROFILE_LOG");
        if (p && p[0]) { FILE* g = fopen(p, "a"); if (g) setvbuf(g, nullptr, _IOLBF, 0); return g; }
        return (FILE*)nullptr; }();
    return f;
}

PEARL_EXPORT int pearl_capi_iter(void* ws, uint64_t seed_lo,
                    void* host_signal_header_pinned, void* stream) {
    auto* w = static_cast<SyclWorkspace*>(ws);
    if (!w || !w->installed) return -3;
    auto* q = HQUEUE(stream);
    const PearlCapiWorkspaceParams& p = w->params;
    int m = w->m, n = w->n, k = w->k, r = w->r;

    // Decouple COMMIT shape (m,n — full canonical, e.g. 131072) from SEARCH window
    // (sm,sn — the sub-grid of tiles we actually compute & sweep). hash_a/hash_b are
    // committed over the full A/B; we only noise/E_A/ApEA/tgemm a small window and
    // open the winning tile (rows < sm) against the full merkle. Env-tunable.
    // Sweep a large window per commitment so the (expensive, full-size) A merkle is
    // amortized over many tiles. Buffers are already allocated full-size, so a big
    // window costs no extra VRAM. Capped so the tgemm stays under the ~2s Windows
    // TDR limit at k=4096 (≈16M tiles ≈ 1.7e13 MACs). sm×sn ≈ 4.3e9 elements.
    int sm = compute_search_m(m);  // identical to the value gemmScratch was sized for
    // Safety: never sweep more rows than gemmScratch can hold (env could change).
    // NOTE: when this clamp bites, the C# throughput mirror (GpuWorker.SyclSearchM)
    // does NOT model it and will over-report by sm_requested/sm_cap. In practice
    // WorkerOrchestrator.ApplyTunedProfile sets the knobs before workspace_alloc,
    // so sm_cap is sized from the same value and the clamp is a no-op.
    if (w->sm_cap > 0 && sm > w->sm_cap) sm = w->sm_cap;
    int sn = pk::tune_env_int("SEARCH_N", 131072);  // ARC_SEARCH_N (legacy: AKOYA_*)
    if (sn > n) sn = n;
    sn = (sn / 64) * 64;            // NB=4 → numTilesN must be %4 (i.e. sn %64)
    if (sn < 64) sn = 64;

    static bool dbg_once = false;
    if (!dbg_once) {
        dbg_once = true;
        fprintf(stderr, "[pearl_sycl dbg] m=%d n=%d k=%d r=%d sm=%d sn=%d sm_cap=%d\n",
                m, n, k, r, sm, sn, w->sm_cap);
        if (FILE* f = prof_log()) {
            std::string dev;
            try { dev = q->get_device().get_info<sycl::info::device::name>(); } catch (...) { dev = "?"; }
            fprintf(f, "==================== ARC-miner A750 diagnostic ====================\n");
            fprintf(f, "device : %s\n", dev.c_str());
            fprintf(f, "shape  : m=%d n=%d k=%d r=%d sm=%d sn=%d sm_cap=%d\n", m, n, k, r, sm, sn, w->sm_cap);
            fprintf(f, "legend : per-step ms averaged over 50 iters; hash = lcg+thash (fused full-A BLAKE3), tgemm = PoW tile search\n");
            fprintf(f, "         >>> the number we need: is the iter HASH-bound (hash%% high) or TGEMM-bound (tgemm%% high)?\n");
            fprintf(f, "===================================================================\n");
            fflush(f);
        }
    }

    // Optional per-step profiling (AKOYA_PROFILE_ITER=1): syncs after each step
    // (so it serializes — diagnostic only) and prints accumulated ms every 50 iters.
    static double acc[10] = {}; static int pn = 0; static int prof_win = 0;
    static bool s_prof_active = g_prof;   // mutable: AKOYA_PROFILE_WINDOWS can auto-disable it
    using clk = std::chrono::high_resolution_clock;
    auto t0 = clk::now();
    auto lap = [&](int i){ if (s_prof_active){ q->wait();
        acc[i] += std::chrono::duration<double,std::milli>(clk::now()-t0).count(); t0 = clk::now(); } };

    // 1 & 2. Fused LCG and Tensor Hash
    auto splitmix = [](uint64_t z) -> uint64_t {
        z += 0x9E3779B97F4A7C15ULL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ULL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBULL;
        return z ^ (z >> 31);
    };
    uint64_t base = splitmix(seed_lo ^ splitmix(p.sigma_seed));
    {
        std::lock_guard<std::mutex> lock(g_seed_mutex);
        g_last_base_seed[q] = base;
    }
    pk::parallel_tensor_hash_fused((uint8_t*)p.A, (long)m * k, (const u32*)p.Key,
                                   (u32*)p.Roots, (uint8_t*)p.AHash, q, base, (long)sm * k);
    lap(0);
    lap(1);

    // 3. commitment — the noise-seed derivation. Post-fork this binds each
    //    Merkle root to its dimension first; see launch_commitment_hash.
    pk::launch_commitment_hash((const uint8_t*)p.AHash, (const uint8_t*)p.BHash,
                               (const uint8_t*)p.Key,
                               (uint8_t*)p.CommitA, (uint8_t*)p.CommitB,
                               m, n, p.salted_seeds != 0, q);
    lap(2);

    // ARC_PRL_SEED_TRACE=1 — one-shot dump of the DEVICE side of the noise-seed
    // derivation: the exact inputs this kernel saw and the seeds it produced.
    // The host logs the same fields, so the two lines can be diffed directly.
    //
    // This exists because verify-seeds proves the FUNCTION agrees on identical
    // inputs — it cannot see whether the miner FEEDS both sides identical
    // inputs, and m/n reach the kernel and the host down completely separate
    // paths. Costs a queue wait, so it is one-shot and env-gated.
    {
        static const bool trace = [](){ const char* v = getenv("ARC_PRL_SEED_TRACE"); return v && atoi(v) > 0; }();
        // One shot PER salted value, not per process. The first mine_iter of a
        // run happens during the startup benchmark — before any job, therefore
        // before the fork gate can fire — so a plain one-shot only ever samples
        // the pre-fork state and says nothing about what mining actually used.
        const bool cur_salted = p.salted_seeds != 0;
        auto& done = cur_salted ? g_seed_trace_done_salted : g_seed_trace_done_legacy;
        int expect = 0;
        if (trace && done.compare_exchange_strong(expect, 1)) {
            uint8_t ah[32], bh[32], ca[32], cb[32];
            q->memcpy(ah, p.AHash, 32);   q->memcpy(bh, p.BHash, 32);
            q->memcpy(ca, p.CommitA, 32); q->memcpy(cb, p.CommitB, 32);
            uint8_t key[32]; q->memcpy(key, p.Key, 32);
            q->wait();
            auto hex = [](const uint8_t* b, char* o) {
                static const char* d = "0123456789ABCDEF";
                for (int i = 0; i < 32; ++i) { o[i*2] = d[b[i] >> 4]; o[i*2+1] = d[b[i] & 15]; }
                o[64] = 0;
            };
            char sah[65], sbh[65], sca[65], scb[65], skey[65];
            hex(ah, sah); hex(bh, sbh); hex(ca, sca); hex(cb, scb); hex(key, skey);
            fprintf(stderr,
                "[seed-trace DEVICE] m=%d n=%d salted=%d\n"
                "[seed-trace DEVICE]   jobKey=%s\n"
                "[seed-trace DEVICE]   A_root=%s\n"
                "[seed-trace DEVICE]   B_root=%s\n"
                "[seed-trace DEVICE]   a_seed=%s\n"
                "[seed-trace DEVICE]   b_seed=%s\n",
                m, n, p.salted_seeds != 0 ? 1 : 0,
                skey, sah, sbh, sca, scb);
            fflush(stderr);
        }
    }

    // 4. noise_gen A-side — only the sm search rows of EAL are needed.
    pk::launch_noise_gen(r, sm, n, k,
                         p.EAL, p.EAL_fp16, p.EAR_R_major, p.EAR_K_major,
                         nullptr, nullptr, nullptr, nullptr,
                         (const uint8_t*)p.CommitA, nullptr, q);
    lap(3);

    // 5. E_A = EAL[sm,r] × EAR_K[r,k] → gemmScratch (search rows only)
    pk::launch_gemm_i8((const int8_t*)p.EAL, (const int8_t*)p.EAR_K_major,
                       w->gemmScratch, sm, k, r, q);
    lap(4);

    // 6. ApEA = A + int8(E_A) for the search rows (A[0..sm) is a prefix of full A)
    pk::launch_add_i8((const int8_t*)p.A, w->gemmScratch, (int8_t*)p.ApEA,
                      sm * k, q);
    lap(5);

    // 6b. Transpose BpEB[n,k] → Bt[k,n] once per Iter (BpEB is constant across the
    //     thousands of iters in a Iter period). The XMX tgemm then loads B with a
    //     fast row-major layout instead of a strided col-major load.
    if (!w->bt_valid) {
        pk::launch_transpose_i8((const int8_t*)p.BpEB, w->Bt, n, k, q);
        w->bt_valid = true;
    }
    lap(6);

    // 7. Zero host_signal + dHeader, then run tgemm_pow
    if (p.host_signal_sync) q->memset(p.host_signal_sync, 0, 8);
    q->memset(w->dHeader, 0, 640);
    q->memset(w->host_signal, 0, 8);

    // Search the sm×sn tile window; Bt's row stride stays the full committed n.
    pk::launch_tgemm_pow((const int8_t*)p.ApEA, (const int8_t*)w->Bt,
                         sm, sn, k, r,
                         (const u32*)p.pow_key, (const u32*)p.pow_target,
                         w->host_signal, w->dHeader, q, n);
    lap(7);
    if (s_prof_active && ++pn % 50 == 0) {
        double a[8]; for (int i=0;i<8;++i) a[i] = acc[i]/50;
        double tot = 0; for (int i=0;i<8;++i) tot += a[i];
        double hashms = a[0] + a[1];   // fused lcg+thash = full-A BLAKE3
        double hashpct  = tot > 0 ? 100.0*hashms/tot : 0.0;
        double tgemmpct = tot > 0 ? 100.0*a[7]/tot   : 0.0;
        char line[640];
        snprintf(line, sizeof line,
            "[prof ms/iter x50] lcg=%.2f thash=%.2f commit=%.2f noise=%.2f egemm=%.2f add=%.2f tpose=%.2f tgemm=%.2f"
            " | total=%.2f  hash=%.0f%%  tgemm=%.0f%%",
            a[0],a[1],a[2],a[3],a[4],a[5],a[6],a[7], tot, hashpct, tgemmpct);
        fprintf(stderr, "%s\n", line);
        if (FILE* f = prof_log()) { fprintf(f, "%s\n", line); fflush(f); }
        for (int i=0;i<10;++i) acc[i]=0;
        if (g_prof_win_limit > 0 && ++prof_win >= g_prof_win_limit) {
            s_prof_active = false;
            const char* msg = "[prof] breakdown complete -> profiling OFF; miner now runs full-speed "
                              "(watch the C# worker line for the true steady-state hashrate)";
            fprintf(stderr, "%s\n", msg);
            if (FILE* f = prof_log()) { fprintf(f, "%s\n", msg); fflush(f); }
        }
    }

    // 8. Copy device header to pinned host memory
    if (host_signal_header_pinned) {
        q->memcpy(host_signal_header_pinned, w->dHeader, 640);
    }

    return 0;
}

PEARL_EXPORT int pearl_capi_iter_batch(void* ws, uint64_t seed_lo_start,
                           void* const* hdrs, int32_t count, void* stream) {
    for (int i = 0; i < count; ++i) {
        int rc = pearl_capi_iter(ws, seed_lo_start + (uint64_t)i,
                                 hdrs ? hdrs[i] : nullptr, stream);
        if (rc) return rc;
    }
    return 0;
}

PEARL_EXPORT int pearl_capi_iter_batch_graph_prepare(void*, void* const*, int32_t, void*) { return -1; }
PEARL_EXPORT int pearl_capi_iter_batch_graph_launch(void*, uint64_t, void*) { return -1; }

// ── noise_B: BpEB[n,k] = (Bᵀ + int8(EBL·EBRᵀ))ᵀ ────────────────────────────

PEARL_EXPORT int pearl_capi_noise_B(const PearlCapiNoiseBParams* p, void* stream) {
    if (!p) return -1;
    std::lock_guard<std::recursive_mutex> usm_lk(g_usm_heavy_mutex);
    auto* q = HQUEUE(stream);
    int n = p->n, k = p->k, r = p->r;

    // N-tiled. The untiled path allocated a full-width int32 EB[k,n] (2 GiB at
    // canonical k=4096,n=131072) plus two k*n int8 temporaries (~512 MiB each)
    // — ~3 GiB of transient σ-install VRAM that pushed a 12 GB card into shared
    // memory. We compute BpEB one N-block at a time. Each slice below is a
    // contiguous sub-array (B/EBR/BpEB are row-major with the n index outermost,
    // so rows [n0,n0+cb) are contiguous), and the per-element int8(EBL·EBRᵀ) add
    // is independent across columns, so the output is bit-identical to untiled.
    int cn = []{ const char* v = getenv("AKOYA_NOISEB_NTILE"); return (v && atoi(v) > 0) ? atoi(v) : 16384; }();
    if (cn > n) cn = n;
    cn = (cn / 128) * 128;          // keep the XMX gemm fast path (N % 128 == 0)
    if (cn < 128) cn = 128;
    if (cn > n) cn = n;

    // Block scratch — reuse workspace resident buffers if available, else allocate.
    auto* ws = static_cast<SyclWorkspace*>(p->workspace);
    bool use_ws = (ws && ws->nb_cn_cap >= cn && ws->nb_EBRt && ws->nb_Bkn && ws->nb_Bnoi && ws->nb_EB);
    int8_t*  EBRt = use_ws ? ws->nb_EBRt : sycl::malloc_device<int8_t>((size_t)r * cn, *q);
    int8_t*  Bkn  = use_ws ? ws->nb_Bkn  : sycl::malloc_device<int8_t>((size_t)k * cn, *q);
    int8_t*  Bnoi = use_ws ? ws->nb_Bnoi : sycl::malloc_device<int8_t>((size_t)k * cn, *q);
    int32_t* EB   = use_ws ? ws->nb_EB   : sycl::malloc_device<int32_t>((size_t)k * cn, *q);
    auto cleanup = [&]{
        if (!use_ws) {
            sycl::free(EBRt, *q); sycl::free(Bkn, *q);
            sycl::free(Bnoi, *q); sycl::free(EB,  *q);
        }
    };

    try {
        const int8_t* B    = (const int8_t*)p->B;             // [n,k]
        const int8_t* EBR  = (const int8_t*)p->EBR;           // [n,r]
        const int8_t* EBLR = (const int8_t*)p->EBL_R_major;   // [k,r]
        int8_t*       BpEB = (int8_t*)p->BpEB;                // [n,k]

        for (int n0 = 0; n0 < n; n0 += cn) {
            int cb = (n0 + cn <= n) ? cn : (n - n0);

            // EBRt[r,cb] = (EBR[n0:n0+cb, r])ᵀ
            pk::launch_transpose_i8(EBR + (size_t)n0 * r, EBRt, cb, r, q);
            // EB[k,cb] = EBL_R[k,r] × EBRt[r,cb]
            pk::launch_gemm_i8(EBLR, EBRt, EB, k, cb, r, q);
            // Bkn[k,cb] = (B[n0:n0+cb, k])ᵀ
            pk::launch_transpose_i8(B + (size_t)n0 * k, Bkn, cb, k, q);
            // Bnoi[k,cb] = Bkn + int8(EB)
            pk::launch_add_i8(Bkn, EB, Bnoi, k * cb, q);
            // BpEB[n0:n0+cb, k] = (Bnoi[k,cb])ᵀ
            pk::launch_transpose_i8(Bnoi, BpEB + (size_t)n0 * k, k, cb, q);
        }
        q->wait();
    } catch (...) {
        cleanup();
        return rc_sycl("noise_B");
    }
    cleanup();
    return 0;
}

PEARL_EXPORT int pearl_capi_install_B(const PearlCapiInstallBParams* p, void* stream) {
    if (!p) return -1;
    std::lock_guard<std::recursive_mutex> usm_lk(g_usm_heavy_mutex);
    auto* q = HQUEUE(stream);

    // 1. Optionally expand BSeed → B, then hash B → BHash
    if (p->expand_bseed && p->bseed) {
        int rc = pearl_capi_bseed_expand_raw_device(
            (const uint8_t*)p->bseed, p->B, (int64_t)p->n * p->k, stream);
        if (rc) return rc;
        q->wait();
    }
    // Also export leaf CVs for the CPU Merkle tree
    pk::parallel_tensor_hash((const uint8_t*)p->B, (long)p->n * p->k,
                             (const u32*)p->Key, (u32*)p->Roots,
                             (uint8_t*)p->BHash, q, (uint8_t*)p->LeafCvs);
    q->wait();

    // 2. commitment_hash — MUST use this σ's dimensions and seed derivation.
    //
    // This used to call pearl_capi_commitment_hash_from_merkle_roots, which
    // hardcodes (m=0, n=0, salted=false). That silently pinned the B-side seed
    // to LEGACY V2: step 3 below then generated EBR/EBL from it and step 4
    // baked it into BpEB, once per σ, with no per-iter path to correct it. The
    // host meanwhile derived the V3 salted b_seed, so from the salted-seed fork
    // onward host and GPU noised B differently and EVERY share missed target by
    // ~30 bits and was dropped pre-submit. Call the kernel directly with the
    // real m/n and this σ's flag; never route σ state through a fixed-argument
    // convenience wrapper.
    pk::launch_commitment_hash(
        (const uint8_t*)p->AHash, (const uint8_t*)p->BHash,
        (const uint8_t*)p->Key, (uint8_t*)p->CommitA, (uint8_t*)p->CommitB,
        p->m, p->n, p->salted_seeds != 0, q);
    q->wait();
    int rc;

    // 3. noise_gen (EAR keyed by CommitA, EBR/EBL keyed by CommitB)
    rc = pearl_capi_noise_gen(p->r, p->m, p->n, p->k,
                              nullptr, nullptr, nullptr, p->EAR_K_major,
                              p->EBL_R_major, p->EBL_K_major,
                              p->EBR, p->EBR_fp16,
                              (const uint8_t*)p->CommitA, (const uint8_t*)p->CommitB,
                              stream);
    if (rc) return rc;
    q->wait();

    // 4. noise_B → BpEB
    PearlCapiNoiseBParams nb{};
    nb.n = p->n; nb.k = p->k; nb.r = p->r;
    nb.B = p->B; nb.EAR_K_major = p->EAR_K_major;
    nb.EBL_R_major = p->EBL_R_major; nb.EBR = p->EBR;
    nb.EARxBpEB = p->EARxBpEB; nb.BpEB = p->BpEB;
    nb.workspace = p->workspace;
    return pearl_capi_noise_B(&nb, stream);
}

PEARL_EXPORT int pearl_capi_noisy_gemm(const PearlCapiNoisyGemmParams*, void*) { return -1; }

} // extern "C"
