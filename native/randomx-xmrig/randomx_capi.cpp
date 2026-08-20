// randomx_capi — C ABI over XMRig's RandomX fork (BSD-3), replacing the previous
// thin wrapper over tevador's reference library. XMRig's fork adds the Ryzen-tuned
// JIT, the RANDOMX_FLAG_AMD dataset-prefetch path, and a scratchpad-prefetch mode,
// which together run ~3-4% faster than the reference lib on Zen (measured: tevador
// 12.2 KH/s vs XMRig 12.6 KH/s on a 5900X, both no-MSR).
//
// ABI (unchanged names, version bumped to 3):
//   • No hash_last — XMRig's pipelined API is first/next only; the caller flushes
//     the in-flight hash by issuing the next hash_next (see the C# worker).
//   • create_vm returns an opaque wrapper owning the randomx_vm, its 2 MB
//     scratchpad, and the tempHash[8] the first/next pipeline threads between calls.
//
// Unlike tevador's library, XMRig's randomx does NOT allocate the cache/dataset/
// scratchpad itself — randomx_create_cache/dataset/create_vm take caller-owned
// buffers. So this shim allocates them (large pages when asked) and frees them.

#include <cstdint>
#include <cstring>
#include <string>
#include <vector>
#include <thread>
#include <new>

#if defined(_WIN32)
#include <malloc.h>
#else
#include <sys/mman.h>
#endif

#include "crypto/randomx/randomx.h"
#include "crypto/randomx/configuration.h"
#include "crypto/common/VirtualMemory.h"
#include "backend/cpu/Cpu.h"

#if defined(_WIN32)
#define RX_EXPORT extern "C" __declspec(dllexport)
#else
#define RX_EXPORT extern "C" __attribute__((visibility("default")))
#endif

namespace {

constexpr int kAbiVersion = 3;

std::string      g_last_error;
randomx_cache*   g_cache        = nullptr;
randomx_dataset* g_dataset      = nullptr;
uint8_t*         g_cache_mem    = nullptr;   // caller-owned cache buffer
uint8_t*         g_dataset_mem  = nullptr;   // caller-owned dataset buffer
bool             g_cache_huge   = false;
bool             g_dataset_huge = false;
randomx_flags    g_flags        = RANDOMX_FLAG_DEFAULT;
bool             g_full_mem     = false;
bool             g_large_pages  = false;

struct VmWrap {
    randomx_vm* vm         = nullptr;
    uint8_t*    scratchpad = nullptr;
    bool        spHuge     = false;
    // 16-byte aligned: RandomX's pipelined first/next loads tempHash with aligned
    // SSE instructions, so a merely 8-aligned buffer faults the JIT on Linux.
    alignas(16) uint64_t tempHash[8] = {0};
};

void set_err(const char* msg) { g_last_error = msg ? msg : ""; }

// Allocate `size` bytes, preferring large pages when `useLarge`; falls back to a
// 4 KB-aligned normal allocation. Reports which path was taken so free() matches.
void* alloc_buf(size_t size, bool useLarge, bool& outHuge) {
    if (useLarge) {
        void* p = xmrig::VirtualMemory::allocateLargePagesMemory(size);
        if (p) { outHuge = true; return p; }
    }
    outHuge = false;
#if defined(_WIN32)
    return _aligned_malloc(size, 4096);
#else
    // Align large buffers to 2 MB and request Transparent Huge Pages, so the
    // dataset/scratchpads get 2 MB backing pages without a root-reserved hugetlb
    // pool — this is what keeps Linux fast out-of-the-box (4 KB pages thrash the
    // TLB across many RandomX threads).
    const size_t kThp = 2u * 1024u * 1024u;
    const size_t alignment = size >= kThp ? kThp : 4096;
    void* p = nullptr;
    if (posix_memalign(&p, alignment, size) != 0) p = nullptr;
#if defined(MADV_HUGEPAGE)
    if (p && size >= kThp) madvise(p, size, MADV_HUGEPAGE);
#endif
    return p;
#endif
}

void free_buf(void* p, size_t size, bool huge) {
    if (!p) return;
    if (huge) { xmrig::VirtualMemory::freeLargePagesMemory(p, size); return; }
#if defined(_WIN32)
    _aligned_free(p);
#else
    free(p);
#endif
}

// Flags for VM/dataset creation (memory is caller-managed, so no LARGE_PAGES flag):
// JIT everywhere, hardware AES + the AMD dataset-prefetch path when supported.
int base_flags() {
    int f = (int)RANDOMX_FLAG_JIT;
    auto* cpu = xmrig::Cpu::info();
    if (cpu->hasAES()) f |= (int)RANDOMX_FLAG_HARD_AES;
    if (cpu->vendor() == xmrig::ICpuInfo::VENDOR_AMD) f |= (int)RANDOMX_FLAG_AMD;
    return f;
}

void init_dataset_parallel(int threads) {
    const unsigned long total = randomx_dataset_item_count();
    if (threads < 1) threads = 1;
    if ((unsigned long)threads > total) threads = (int)total;

    std::vector<std::thread> pool;
    pool.reserve(threads);
    const unsigned long per = total / threads;
    for (int t = 0; t < threads; ++t) {
        const unsigned long start = per * (unsigned long)t;
        const unsigned long count = (t == threads - 1) ? (total - start) : per;
        pool.emplace_back([start, count] {
            randomx_init_dataset(g_dataset, g_cache, start, count);
        });
    }
    for (auto& th : pool) th.join();
}

} // namespace

RX_EXPORT void randomx_capi_shutdown(void);

RX_EXPORT int randomx_capi_abi_version(void) { return kAbiVersion; }

RX_EXPORT const char* randomx_capi_last_error(void) { return g_last_error.c_str(); }

// Canonical RandomX test vector (flag-independent result). Uses light mode + JIT
// with a caller-owned cache buffer, per XMRig's create_cache/create_vm API.
RX_EXPORT int randomx_capi_selftest(void) {
    static const uint8_t kExpected[32] = {
        0x63,0x91,0x83,0xaa,0xe1,0xbf,0x4c,0x9a,0x35,0x88,0x4c,0xb4,0x6b,0x09,0xca,0xd9,
        0x17,0x5f,0x04,0xef,0xd7,0x68,0x4e,0x72,0x62,0xa0,0xac,0x1c,0x2f,0x0b,0x4e,0x3f
    };
    const char* key   = "test key 000";
    const char* input = "This is a test";

    randomx_apply_config(RandomX_MoneroConfig);

    bool hCache = false, hSp = false;
    uint8_t* cacheMem = (uint8_t*)alloc_buf(RANDOMX_CACHE_MAX_SIZE, false, hCache);
    uint8_t* sp       = (uint8_t*)alloc_buf(RANDOMX_SCRATCHPAD_L3_MAX_SIZE, false, hSp);
    if (!cacheMem || !sp) { free_buf(cacheMem, RANDOMX_CACHE_MAX_SIZE, hCache); free_buf(sp, RANDOMX_SCRATCHPAD_L3_MAX_SIZE, hSp); set_err("selftest: alloc failed"); return -1; }

    randomx_flags f = (randomx_flags)(int)RANDOMX_FLAG_JIT;
    randomx_cache* cache = randomx_create_cache(f, cacheMem);
    if (!cache) { free_buf(cacheMem, RANDOMX_CACHE_MAX_SIZE, hCache); free_buf(sp, RANDOMX_SCRATCHPAD_L3_MAX_SIZE, hSp); set_err("selftest: randomx_create_cache failed"); return -1; }
    randomx_init_cache(cache, key, std::strlen(key));

    randomx_vm* vm = randomx_create_vm(f, cache, nullptr, sp, 0);
    if (!vm) { randomx_release_cache(cache); free_buf(cacheMem, RANDOMX_CACHE_MAX_SIZE, hCache); free_buf(sp, RANDOMX_SCRATCHPAD_L3_MAX_SIZE, hSp); set_err("selftest: randomx_create_vm failed"); return -2; }

    uint8_t out[32];
    randomx_calculate_hash(vm, input, std::strlen(input), out);
    randomx_destroy_vm(vm);
    randomx_release_cache(cache);
    free_buf(cacheMem, RANDOMX_CACHE_MAX_SIZE, hCache);
    free_buf(sp, RANDOMX_SCRATCHPAD_L3_MAX_SIZE, hSp);

    if (std::memcmp(out, kExpected, 32) != 0) { set_err("selftest: hash mismatch vs known vector"); return -3; }
    return 0;
}

RX_EXPORT int randomx_capi_init(const uint8_t* key, uint32_t key_len,
                                int full_mem, int large_pages, int init_threads) {
    randomx_capi_shutdown();

    // Monero consensus config + XMRig's default scratchpad prefetch (mode 1).
    randomx_apply_config(RandomX_MoneroConfig);
    randomx_set_scratchpad_prefetch_mode(1);
    // Disable the AVX2 dataset-init path: it JIT-executes self-modifying code that
    // hangs on Zen without XMRig's RxFix workaround (which we don't vendor). This
    // only affects one-time dataset build speed, not hashrate. Must be set before
    // the cache's JitCompiler is constructed (in randomx_create_cache below).
    randomx_set_optimized_dataset_init(0);

    g_large_pages = large_pages != 0;
    g_full_mem    = full_mem != 0;
    g_flags       = (randomx_flags)(base_flags() | (g_full_mem ? (int)RANDOMX_FLAG_FULL_MEM : 0));

    // Cache: caller-owned buffer (large pages when requested), JIT create.
    g_cache_mem = (uint8_t*)alloc_buf(RANDOMX_CACHE_MAX_SIZE, g_large_pages, g_cache_huge);
    if (!g_cache_mem) { set_err("init: cache buffer allocation failed"); return -1; }

    g_cache = randomx_create_cache(RANDOMX_FLAG_JIT, g_cache_mem);
    if (!g_cache) g_cache = randomx_create_cache(RANDOMX_FLAG_DEFAULT, g_cache_mem);
    if (!g_cache) { set_err("init: randomx_create_cache failed"); randomx_capi_shutdown(); return -1; }
    randomx_init_cache(g_cache, key, key_len);

    if (g_full_mem) {
        g_dataset_mem = (uint8_t*)alloc_buf(RANDOMX_DATASET_MAX_SIZE, g_large_pages, g_dataset_huge);
        if (!g_dataset_mem) { set_err("init: dataset buffer allocation failed (need ~2.1 GB; try light mode)"); randomx_capi_shutdown(); return -2; }
        g_dataset = randomx_create_dataset(g_dataset_mem);
        if (!g_dataset) { set_err("init: randomx_create_dataset failed"); randomx_capi_shutdown(); return -2; }
        init_dataset_parallel(init_threads);
    }
    return 0;
}

RX_EXPORT uint64_t randomx_capi_dataset_item_count(void) {
    return (uint64_t)randomx_dataset_item_count();
}

RX_EXPORT void* randomx_capi_create_vm(void) {
    if (!g_cache) { set_err("create_vm: not initialized"); return nullptr; }
    auto* w = new (std::nothrow) VmWrap();
    if (!w) { set_err("create_vm: out of memory"); return nullptr; }

    // Each VM needs its own 2 MB scratchpad; a large page keeps it in one TLB entry.
    w->scratchpad = (uint8_t*)alloc_buf(RANDOMX_SCRATCHPAD_L3_MAX_SIZE, g_large_pages, w->spHuge);
    if (!w->scratchpad) { set_err("create_vm: scratchpad allocation failed"); delete w; return nullptr; }

    w->vm = randomx_create_vm(g_flags, g_full_mem ? nullptr : g_cache,
                              g_full_mem ? g_dataset : nullptr, w->scratchpad, 0);
    if (!w->vm) {
        set_err("create_vm: randomx_create_vm failed");
        free_buf(w->scratchpad, RANDOMX_SCRATCHPAD_L3_MAX_SIZE, w->spHuge);
        delete w;
        return nullptr;
    }
    return w;
}

RX_EXPORT void randomx_capi_destroy_vm(void* vmw) {
    if (!vmw) return;
    auto* w = static_cast<VmWrap*>(vmw);
    if (w->vm) randomx_destroy_vm(w->vm);
    free_buf(w->scratchpad, RANDOMX_SCRATCHPAD_L3_MAX_SIZE, w->spHuge);
    delete w;
}

RX_EXPORT void randomx_capi_hash(void* vmw, const void* input, uint32_t in_len, uint8_t* out32) {
    auto* w = static_cast<VmWrap*>(vmw);
    randomx_calculate_hash(w->vm, input, in_len, out32);
}

// Pipelined hashing (XMRig first/next). hash_first primes; each hash_next emits the
// hash of the PREVIOUS input while starting the next. No hash_last: the caller
// flushes the in-flight hash with a subsequent hash_next.
RX_EXPORT void randomx_capi_hash_first(void* vmw, const void* input, uint32_t in_len) {
    auto* w = static_cast<VmWrap*>(vmw);
    randomx_calculate_hash_first(w->vm, w->tempHash, input, in_len);
}

RX_EXPORT void randomx_capi_hash_next(void* vmw, const void* next_input, uint32_t next_len, uint8_t* out32) {
    auto* w = static_cast<VmWrap*>(vmw);
    randomx_calculate_hash_next(w->vm, w->tempHash, next_input, next_len, out32);
}

RX_EXPORT void randomx_capi_shutdown(void) {
    if (g_dataset) { randomx_release_dataset(g_dataset); g_dataset = nullptr; }
    if (g_cache)   { randomx_release_cache(g_cache);     g_cache   = nullptr; }
    if (g_dataset_mem) { free_buf(g_dataset_mem, RANDOMX_DATASET_MAX_SIZE, g_dataset_huge); g_dataset_mem = nullptr; }
    if (g_cache_mem)   { free_buf(g_cache_mem, RANDOMX_CACHE_MAX_SIZE, g_cache_huge);       g_cache_mem   = nullptr; }
    g_full_mem = false;
}
