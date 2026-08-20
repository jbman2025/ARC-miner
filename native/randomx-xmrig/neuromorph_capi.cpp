// neuromorph_capi — a C ABI over NeuroMorph (`nm/1`, Cereblix / CRB).
//
// Like ghostrider_capi, this is a thin shim over the upstream implementation
// rather than a reimplementation: crypto/nm/* is vendored from the
// xmrig-cereblix fork (GPLv3) and called as-is. Only two lines needed patching
// for MSVC — `unsigned __int128` -> __umulh, and the AES-NI feature gate — both
// marked "ARC PATCH" in the vendored sources. The hash itself is untouched.
//
// What this shim owns is the DATASET SHARING that XMRig's NmShared.cpp provides
// via hwloc/VirtualMemory/std::map. NeuroMorph needs one read-only 64 MiB
// dataset per epoch, shared by every worker thread; building it per thread would
// waste 64 MiB and a rebuild each. Here a single process-wide dataset is built
// on demand, keyed by the epoch's dataset_key, and handed to every context.
//
// Threading contract:
//   * ghostrider_capi hashes 8 nonces per call; NeuroMorph hashes ONE.
//   * Call nm_capi_set_seed on every context whenever the pool's seed_hash
//     changes. The first caller for a new epoch builds the dataset (~tens of ms)
//     while the others block; afterwards it is read-only and lock-free.
//   * Contexts are per worker thread and must not be shared between threads.
//   * IMPORTANT: a new epoch rebuilds the shared dataset IN PLACE, so NO OTHER
//     THREAD MAY BE HASHING while nm_capi_set_seed rebuilds — an in-flight hash
//     would read a half-rewritten dataset and produce a rejected share. Callers
//     must quiesce their workers across a seed change (NmPoolClient stops its
//     solver threads, mirroring what RxPoolClient does for RandomX). XMRig's
//     NmShared.cpp instead keeps the previous 64 MiB buffer alive for one
//     generation; quiescing is simpler and costs nothing at a ~2.8-day cadence.

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cstdlib>
#include <mutex>
#include <new>

#include "crypto/common/VirtualMemory.h"

extern "C" {
#include "crypto/nm/nm_neuromorph.h"
#include "crypto/nm/nm_params.h"
}

#if defined(_WIN32)
#   define NM_EXPORT extern "C" __declspec(dllexport)
#else
#   define NM_EXPORT extern "C" __attribute__((visibility("default")))
#endif

namespace {

// Bump when the exported signatures change; NmNative.cs checks this.
constexpr int kAbiVersion = 1;

thread_local char g_error[256] = { 0 };

void set_error(const char* msg)
{
    snprintf(g_error, sizeof(g_error), "%s", msg);
}

void* aligned_alloc_(size_t align, size_t size)
{
#if defined(_WIN32)
    return _aligned_malloc(size, align);
#else
    void* p = nullptr;
    if (posix_memalign(&p, align, size) != 0) p = nullptr;
    return p;
#endif
}

void aligned_free_(void* p)
{
#if defined(_WIN32)
    _aligned_free(p);
#else
    free(p);
#endif
}

// Huge-page allocation with a plain-page fallback. NeuroMorph is deliberately
// bound to DRAM latency — the dataset read chain is data-dependent and cannot be
// prefetched — so TLB misses cost real hashrate: on a 5900X, 4 KiB pages give
// ~8.0 KH/s at 24T where huge pages give ~11 KH/s, matching upstream XMRig.
// Large pages need SeLockMemoryPrivilege on Windows, so the fallback is normal.
struct Buffer
{
    void*  ptr   = nullptr;
    size_t size  = 0;
    bool   huge  = false;
};

Buffer alloc_buffer(size_t size)
{
    Buffer b;
    b.size = size;

    if (void* p = xmrig::VirtualMemory::allocateLargePagesMemory(size)) {
        b.ptr  = p;
        b.huge = true;
        return b;
    }

    b.ptr  = aligned_alloc_(4096, size);
    b.huge = false;
    return b;
}

void free_buffer(Buffer& b)
{
    if (!b.ptr) {
        return;
    }
    if (b.huge) {
        xmrig::VirtualMemory::freeLargePagesMemory(b.ptr, b.size);
    }
    else {
        aligned_free_(b.ptr);
    }
    b.ptr = nullptr;
}

// ── Process-wide shared dataset ───────────────────────────────────────────────
// One 64 MiB buffer per epoch. Rebuilt only when the epoch's dataset_key
// changes, which happens every NM_EPOCH_LENGTH (4096) blocks — roughly every
// 2.8 days at 60 s blocks, so rebuilds are rare and the cost is amortised.
std::mutex  g_dsMutex;
Buffer      g_datasetBuf;
uint8_t     g_datasetKey[16] = { 0 };
bool        g_datasetValid   = false;
bool        g_datasetHuge    = false;

// Returns the shared dataset for `key`, building it if this is a new epoch.
// Caller must hold no locks. Returns nullptr only on allocation failure.
const uint64_t* shared_dataset(const uint8_t key[16])
{
    std::lock_guard<std::mutex> lock(g_dsMutex);

    if (!g_datasetBuf.ptr) {
        g_datasetBuf = alloc_buffer(NM_DATASET_BYTES);
        if (!g_datasetBuf.ptr) {
            return nullptr;
        }
        g_datasetHuge  = g_datasetBuf.huge;
        g_datasetValid = false;
    }

    auto* ds = static_cast<uint64_t*>(g_datasetBuf.ptr);

    if (!g_datasetValid || memcmp(g_datasetKey, key, 16) != 0) {
        nm_build_dataset(ds, key);
        memcpy(g_datasetKey, key, 16);
        g_datasetValid = true;
    }

    return ds;
}

// One worker thread's state: the upstream context plus the scratchpad we own on
// its behalf (so it can come from huge pages rather than upstream's malloc).
struct Worker
{
    nm_ctx ctx;
    Buffer scratch;
};

} // namespace

NM_EXPORT int nm_capi_abi_version() { return kAbiVersion; }

// 1 if the shared 64 MiB dataset is backed by huge pages, 0 if it fell back to
// normal pages (no SeLockMemoryPrivilege), -1 if it has not been built yet.
// Reported at startup so a big hashrate drop has an obvious explanation.
NM_EXPORT int nm_capi_huge_pages()
{
    std::lock_guard<std::mutex> lock(g_dsMutex);
    if (!g_datasetBuf.ptr) return -1;
    return g_datasetHuge ? 1 : 0;
}

NM_EXPORT const char* nm_capi_last_error() { return g_error; }

// Header length and nonce offset, so the managed side never hardcodes them.
NM_EXPORT int nm_capi_header_len()   { return NM_HEADER_LEN; }    // 124
NM_EXPORT int nm_capi_nonce_offset() { return NM_NONCE_OFFSET; }  // 116

// Allocate one worker context: a 2 MiB scratchpad plus program buffers. The
// 64 MiB dataset is NOT per-context — it is attached by nm_capi_set_seed.
NM_EXPORT void* nm_capi_create_ctx()
{
    g_error[0] = 0;

    auto* w = static_cast<Worker*>(calloc(1, sizeof(Worker)));
    if (!w) {
        set_error("out of memory allocating the NeuroMorph worker context");
        return nullptr;
    }
    new (&w->scratch) Buffer();

    if (nm_ctx_init_shared(&w->ctx) != 0) {
        free(w);
        set_error("nm_ctx_init_shared failed (program buffer allocation)");
        return nullptr;
    }

    // Swap upstream's malloc'd scratchpad for a huge-page one where possible.
    // nm_ctx_attach_scratch frees the buffer it owned and takes ours unowned.
    w->scratch = alloc_buffer(NM_SCRATCH_BYTES);
    if (!w->scratch.ptr) {
        nm_ctx_free(&w->ctx);
        free(w);
        set_error("out of memory allocating the 2 MiB NeuroMorph scratchpad");
        return nullptr;
    }
    nm_ctx_attach_scratch(&w->ctx, w->scratch.ptr);

    return w;
}

NM_EXPORT void nm_capi_destroy_ctx(void* handle)
{
    if (!handle) {
        return;
    }
    auto* w = static_cast<Worker*>(handle);

    // The dataset is process-wide and outlives every context, and the scratchpad
    // is ours (attached unowned) — make sure nm_ctx_free frees neither.
    w->ctx.dataset       = nullptr;
    w->ctx.dataset_valid = 0;
    w->ctx.scratch       = nullptr;
    w->ctx.owns_scratch  = 0;
    nm_ctx_free(&w->ctx);

    free_buffer(w->scratch);
    free(w);
}

// Point a context at an epoch. `seed32` is the pool's seed_hash. Derives the VM
// parameters and attaches the shared dataset, building it if the epoch changed.
// Returns 0 on success, <0 on failure (see nm_capi_last_error).
NM_EXPORT int nm_capi_set_seed(void* handle, const uint8_t* seed32)
{
    g_error[0] = 0;

    auto* c = static_cast<nm_ctx*>(handle);
    nm_ctx_set_params(c, seed32);

    const uint64_t* ds = shared_dataset(c->params.dataset_key);
    if (!ds) {
        set_error("out of memory allocating the 64 MiB NeuroMorph dataset");
        return -1;
    }

    nm_ctx_attach_dataset(c, const_cast<uint64_t*>(ds), c->params.dataset_key);
    return 0;
}

// One NeuroMorph hash of the 124-byte `header` into `out32`. `height` selects
// whether the memory-hard dataset step runs (active at height >= 240).
// nm_capi_set_seed must have been called on this context first.
NM_EXPORT void nm_capi_hash(void* handle, const uint8_t* header, uint64_t height, uint8_t* out32)
{
    nm_hash(static_cast<nm_ctx*>(handle), header, height, out32);
}

// Self-check: hashing must be deterministic, sensitive to the nonce, and
// independent of which context does the work. There is no published NeuroMorph
// test vector to check against, so this proves internal consistency only —
// correctness against consensus is established by a pool accepting shares.
NM_EXPORT int nm_capi_selftest()
{
    g_error[0] = 0;

    uint8_t seed[32];
    for (int i = 0; i < 32; ++i) seed[i] = static_cast<uint8_t>(i * 7 + 1);

    void* a = nm_capi_create_ctx();
    void* b = nm_capi_create_ctx();
    if (!a || !b) {
        if (a) nm_capi_destroy_ctx(a);
        if (b) nm_capi_destroy_ctx(b);
        return -1;
    }
    if (nm_capi_set_seed(a, seed) != 0 || nm_capi_set_seed(b, seed) != 0) {
        nm_capi_destroy_ctx(a);
        nm_capi_destroy_ctx(b);
        return -2;
    }

    uint8_t header[NM_HEADER_LEN];
    for (int i = 0; i < NM_HEADER_LEN; ++i) header[i] = static_cast<uint8_t>(i * 3 + 5);

    // Use a height above NM_DATASET_HEIGHT so the dataset path is exercised.
    const uint64_t height = 100000;

    uint8_t h1[32], h2[32], h3[32], h4[32];
    nm_capi_hash(a, header, height, h1);
    nm_capi_hash(a, header, height, h2);   // repeat, same context
    nm_capi_hash(b, header, height, h3);   // same input, different context

    header[NM_NONCE_OFFSET] ^= 0x01;       // flip one nonce bit
    nm_capi_hash(a, header, height, h4);

    nm_capi_destroy_ctx(a);
    nm_capi_destroy_ctx(b);

    if (memcmp(h1, h2, 32) != 0) {
        set_error("selftest: repeated hash on one context differs");
        return -3;
    }
    if (memcmp(h1, h3, 32) != 0) {
        set_error("selftest: a second context hashes the same input differently");
        return -4;
    }
    if (memcmp(h1, h4, 32) == 0) {
        set_error("selftest: flipping a nonce bit did not change the hash");
        return -5;
    }

    return 0;
}
