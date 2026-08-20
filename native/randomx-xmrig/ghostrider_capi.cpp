// ghostrider_capi — a C ABI over XMRig's GhostRider (Raptoreum, --algo gr).
//
// This is a *thin* shim. Unlike the previous version, it does NOT reimplement
// GhostRider's hash loop: it vendors XMRig's own crypto/ghostrider/ghostrider.cpp
// verbatim and calls xmrig::ghostrider::hash_octa(). Rewriting the loop by hand
// is what produced hashes the pools rejected — the loop looked right, but the
// context/scratchpad management around it (8 lanes packed into shared
// scratchpads per the CryptoNight variant's `step`, half-memory variants
// driving ctx->first_half) is part of the algorithm, not boilerplate. Calling
// upstream removes that entire class of divergence.
//
// Build shape (see build_gr_capi.bat/.sh):
//   * WITHOUT XMRIG_FEATURE_HWLOC  -> the simple 8-lane hash_octa, no helper
//     threads, no libuv/hwloc. Stubs for <uv.h>/Log.h/Tags.h let ghostrider.cpp
//     stay byte-identical to upstream (compat/uv.h, base/io/log/*).
//   * XMRIG_FEATURE_ASM is defined by the Windows build and not by the Linux
//     one, but for GhostRider that makes NO DIFFERENCE and both run the
//     portable CryptoNight path. Upstream registers CN_GR_0..5 with ADD_FN
//     only; ADD_FN_ASM (which installs cryptonight_*_hash_asm into the dispatch
//     table) is never called for them, and CnHash::fn is GhostRider's only way
//     in — so it always resolves to data[av][Assembly::NONE]. The patched
//     cn_gr*_mainloop_asm pointers exist but nothing dispatches to them.
//     Measured: enabling the flag + GAS asm on Linux moved 1-thread throughput
//     146.3 -> 145.4 H/s, i.e. not at all. See build_gr_capi.sh for the detail.
//
// GhostRider hashes 8 nonces per call. Callers must batch: fill 8 copies of the
// 80-byte header with consecutive nonces at offset 76 and read back 8x32 bytes.

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cstdlib>
#include <new>
#include <atomic>

#include "base/crypto/Algorithm.h"
#include "crypto/cn/CnCtx.h"
#include "crypto/cn/CryptoNight.h"
#include "crypto/common/VirtualMemory.h"
#include "crypto/ghostrider/ghostrider.h"

#if defined(_WIN32)
#   define GR_EXPORT extern "C" __declspec(dllexport)
#else
#   define GR_EXPORT extern "C" __attribute__((visibility("default")))
#endif

namespace {

// Bump when the exported signatures change; GrNative.cs checks this.
constexpr int kAbiVersion = 2;

// GhostRider always runs 8 lanes (Algorithm::GHOSTRIDER_RTM min/maxIntensity == 8).
constexpr size_t kLanes = 8;

// Per-lane scratchpad. Algorithm::l3(GHOSTRIDER_RTM) == 2 MiB, sized for the
// largest CryptoNight variant GhostRider can select (cn/fast). Smaller variants
// are packed into these same buffers by hash_octa.
constexpr size_t kScratchpad = 1ULL << 21;

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

// Huge-page allocation with a plain-page fallback. CryptoNight scratchpads are
// the textbook case for large pages: each lane random-walks a 2 MiB buffer, so
// on 4 KiB pages the TLB thrashes. XMRig reports "huge pages 100%" for
// GhostRider for exactly this reason. Large pages need SeLockMemoryPrivilege on
// Windows, so falling back is normal, not an error.
std::atomic<int> g_hugePages{ -1 };   // -1 unknown, 0 fell back, 1 huge pages

uint8_t* alloc_scratchpads(size_t size, bool& huge)
{
    if (void* p = xmrig::VirtualMemory::allocateLargePagesMemory(size)) {
        huge = true;
        g_hugePages.store(1, std::memory_order_relaxed);
        return static_cast<uint8_t*>(p);
    }

    huge = false;
    // Only downgrade the global flag; never upgrade a failure back to success.
    int expected = -1;
    g_hugePages.compare_exchange_strong(expected, 0, std::memory_order_relaxed);
    if (g_hugePages.load(std::memory_order_relaxed) == 1) {
        g_hugePages.store(0, std::memory_order_relaxed);
    }
    return static_cast<uint8_t*>(aligned_alloc_(4096, size));
}

// One worker thread's state: 8 CryptoNight contexts over a 16 MiB scratchpad.
struct GrCtx
{
    uint8_t*          memory = nullptr;
    bool              hugeMemory = false;
    cryptonight_ctx*  cn[kLanes] = { nullptr };
};

GrCtx* ctx_create()
{
    auto* c = new (std::nothrow) GrCtx();
    if (!c) {
        set_error("out of memory allocating GrCtx");
        return nullptr;
    }

    c->memory = alloc_scratchpads(kScratchpad * kLanes, c->hugeMemory);
    if (!c->memory) {
        delete c;
        set_error("out of memory allocating GhostRider scratchpads (16 MiB)");
        return nullptr;
    }

    xmrig::CnCtx::create(c->cn, c->memory, kScratchpad, kLanes);
    if (!c->cn[0]) {
        if (c->hugeMemory) xmrig::VirtualMemory::freeLargePagesMemory(c->memory, kScratchpad * kLanes);
        else               aligned_free_(c->memory);
        delete c;
        set_error("CnCtx::create failed");
        return nullptr;
    }

    return c;
}

void ctx_destroy(GrCtx* c)
{
    if (!c) {
        return;
    }
    xmrig::CnCtx::release(c->cn, kLanes);
    if (c->hugeMemory) xmrig::VirtualMemory::freeLargePagesMemory(c->memory, kScratchpad * kLanes);
    else               aligned_free_(c->memory);
    delete c;
}

// XMRig's GhostRider self-test vector (backend/cpu/CpuWorker.cpp `verify`):
// two 8-lane batches whose algo-selecting seed bytes differ, XORed together.
const uint8_t test_output_gr[256] = {
    0x42,0x17,0x0C,0xC1,0x85,0xE6,0x76,0x3C,0xC7,0xCB,0x27,0xC4,0x17,0x39,0x2D,0xE2,
    0x29,0x6B,0x40,0x66,0x85,0xA4,0xE3,0xD3,0x8C,0xE9,0xA5,0x8F,0x10,0xFC,0x81,0xE4,
    0x90,0x56,0xF2,0x9E,0x00,0xD0,0xF8,0xA1,0x88,0x82,0x86,0xC0,0x86,0x04,0x6B,0x0E,
    0x9A,0xDB,0xDB,0xFD,0x23,0x16,0x77,0x94,0xFE,0x58,0x93,0x05,0x10,0x3F,0x27,0x75,
    0x51,0x44,0xF3,0x5F,0xE2,0xF9,0x61,0xBE,0xC0,0x30,0xB5,0x8E,0xB1,0x1B,0xA1,0xF7,
    0x06,0x4E,0xF1,0x6A,0xFD,0xA5,0x44,0x8E,0x64,0x47,0x8C,0x67,0x51,0xE2,0x5C,0x55,
    0x3E,0x39,0xA6,0xA5,0xF7,0xB8,0xD0,0x5E,0xE2,0xBF,0x92,0x44,0xD9,0xAA,0x76,0x22,
    0xE3,0x3E,0x15,0x96,0xD8,0x6A,0x78,0x2D,0xA9,0x77,0x24,0x1A,0x4B,0xE7,0x5A,0x2E,
    0x89,0x77,0xAE,0x92,0xE4,0xA4,0x2D,0xAF,0x0B,0x27,0x09,0xB2,0x5F,0x95,0x61,0xA9,
    0xA8,0xBE,0x5D,0x39,0xBE,0x41,0x5F,0x9C,0x67,0x28,0x48,0x4F,0xAE,0x2A,0x50,0x2B,
    0xB8,0xC7,0x42,0x73,0x51,0x60,0x59,0xD8,0x9C,0xBA,0x22,0x2F,0x8E,0x34,0xDE,0xC8,
    0x1B,0xAE,0x9E,0xBD,0xF7,0xE8,0xFD,0x8A,0x97,0xBE,0xF0,0x47,0xAC,0x27,0xDD,0x28,
    0xC9,0x28,0xA8,0x7B,0x2A,0xB8,0x90,0x3E,0xCA,0xB4,0x78,0x44,0xCE,0xCD,0x91,0xEC,
    0xC2,0x5A,0x17,0x59,0x7C,0x14,0xF8,0x95,0x28,0x14,0xC3,0xAD,0xC4,0xE1,0x13,0x5A,
    0xC4,0xA7,0xC7,0x77,0xAD,0xF8,0x09,0x61,0x16,0xBB,0xAA,0x7E,0xAB,0xC3,0x00,0x25,
    0xBA,0xA8,0x97,0xC7,0x7D,0x38,0x46,0x0E,0x59,0xAC,0xCB,0xAE,0xFE,0x3C,0x6F,0x01
};

} // namespace

GR_EXPORT int ghostrider_capi_abi_version() { return kAbiVersion; }

GR_EXPORT const char* ghostrider_capi_last_error() { return g_error; }

GR_EXPORT int ghostrider_capi_lanes() { return static_cast<int>(kLanes); }

// 1 if the worker scratchpads are backed by huge pages, 0 if they fell back to
// normal pages, -1 if no context has been created yet.
GR_EXPORT int ghostrider_capi_huge_pages() { return g_hugePages.load(std::memory_order_relaxed); }

GR_EXPORT void* ghostrider_capi_create_ctx()
{
    g_error[0] = 0;
    return ctx_create();
}

GR_EXPORT void ghostrider_capi_destroy_ctx(void* handle)
{
    ctx_destroy(static_cast<GrCtx*>(handle));
}

// Hash 8 headers at once. `input` is 8 * `size` bytes (size is 80 for a
// Raptoreum block header); `out` receives 8 * 32 bytes, lane i's hash at i*32.
GR_EXPORT void ghostrider_capi_hash_octa(void* handle, const uint8_t* input, uint32_t size, uint8_t* out)
{
    auto* c = static_cast<GrCtx*>(handle);
    xmrig::ghostrider::hash_octa(input, size, out, c->cn, nullptr, false);
}

// Single-hash convenience for tests and share verification: hashes `input` in
// all 8 lanes and returns lane 0. Costs a full octa call — never use it in the
// mining loop.
GR_EXPORT void ghostrider_capi_hash(void* handle, const uint8_t* input, uint32_t size, uint8_t* out32)
{
    auto* c = static_cast<GrCtx*>(handle);

    uint8_t blob[80 * kLanes];
    uint8_t hash[32 * kLanes];

    if (size > 80) {
        size = 80;
    }
    for (size_t i = 0; i < kLanes; ++i) {
        memcpy(blob + i * size, input, size);
    }

    xmrig::ghostrider::hash_octa(blob, size, hash, c->cn, nullptr, false);
    memcpy(out32, hash, 32);
}

// Reproduces XMRig's CpuWorker<8>::verify() for GHOSTRIDER_RTM.
// Returns 0 on match, <0 otherwise (see ghostrider_capi_last_error).
GR_EXPORT int ghostrider_capi_selftest()
{
    g_error[0] = 0;

    GrCtx* c = ctx_create();
    if (!c) {
        return -1;
    }

    uint8_t blob[80 * kLanes] = { 0 };
    uint8_t hash1[32 * kLanes];
    uint8_t hash2[32 * kLanes];

    for (size_t i = 0; i < kLanes; ++i) {
        blob[i * 80 + 0] = static_cast<uint8_t>(i);
        blob[i * 80 + 4] = 0x10;
        blob[i * 80 + 5] = 0x02;
    }
    xmrig::ghostrider::hash_octa(blob, 80, hash1, c->cn, nullptr, false);

    for (size_t i = 0; i < kLanes; ++i) {
        blob[i * 80 + 0] = static_cast<uint8_t>(i);
        blob[i * 80 + 4] = 0x43;
        blob[i * 80 + 5] = 0x05;
    }
    xmrig::ghostrider::hash_octa(blob, 80, hash2, c->cn, nullptr, false);

    ctx_destroy(c);

    for (size_t i = 0; i < sizeof(test_output_gr); ++i) {
        if ((hash1[i] ^ hash2[i]) != test_output_gr[i]) {
            snprintf(g_error, sizeof(g_error),
                     "GhostRider selftest mismatch at byte %zu: got %02x, want %02x",
                     i, hash1[i] ^ hash2[i], test_output_gr[i]);
            return -2;
        }
    }

    return 0;
}
