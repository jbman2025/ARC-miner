// csd_capi.cpp — C ABI around the CSD SHA-256d search kernel (csd_capi.dll).
// Kernel lives in csd_sha256d.hpp and is gate-validated by csd_fused_check.cpp.
//
// Managed contract (see src/Akoya.Miner/Algos/Csd/CsdNative.cs):
//  - The caller (C#) builds the header, computes the block-0 midstate and the
//    tail words on the host, and derives the 8-word BE share target.
//  - csd_capi_search scans [nonce_base, nonce_base+count) and reports every
//    nonce whose sha256d(header) <= target, appended via an atomic counter.
//    Nonces are the kernel's W[4] value; the caller formats the submit hex.
#include <sycl/sycl.hpp>
#include "csd_sha256d.hpp"
#include <memory>
#include <string>
#include <array>
#include <vector>
#include <cstdio>

#if defined(_WIN32)
#define CSD_EXPORT extern "C" __declspec(dllexport)
#else
#define CSD_EXPORT extern "C" __attribute__((visibility("default")))
#endif

namespace {
constexpr int kAbiVersion = 1;

struct Ctx {
    sycl::queue q;
    std::string device_name;
    uint32_t* d_found{nullptr};
    uint32_t* d_count{nullptr};
    static constexpr uint32_t kFoundCap = 4096;
    void ensure() {
        if (d_found) return;
        d_found = sycl::malloc_shared<uint32_t>(kFoundCap, q);
        d_count = sycl::malloc_shared<uint32_t>(1, q);
    }
    void free_all() {
        if (d_found) { sycl::free(d_found, q); d_found = nullptr; }
        if (d_count) { sycl::free(d_count, q); d_count = nullptr; }
    }
};

// thread_local for multi-GPU: each C# device thread opens its own device and
// drives it independently — N threads mine N GPUs with no shared mutable state.
// The only rule is that every call for a device happens on the same OS thread.
thread_local std::unique_ptr<Ctx> g_ctx;
thread_local std::string g_last_error;
thread_local bool g_async_failed{false};
thread_local std::string g_async_error;

void AsyncErrorHandler(const sycl::exception_list& errs) {
    for (const std::exception_ptr& e : errs) {
        try { std::rethrow_exception(e); }
        catch (const std::exception& ex) { g_async_failed = true; if(!g_async_error.empty()) g_async_error+="; "; g_async_error += ex.what(); }
    }
}
// GPU devices, de-duplicated to the Level-Zero backend. sycl::get_devices()
// returns each physical GPU once PER BACKEND (Level-Zero AND OpenCL), so a
// 2-card rig otherwise shows 4 devices — and the OpenCL duplicates don't support
// shared USM the way the Level-Zero ones do. Prefer Level-Zero; fall back to the
// raw list only if no Level-Zero platform exists.
// The OpenCLOn12 / D3D12 platform is Microsoft's OpenCL-over-Direct3D12 software
// translation layer: it mirrors every real GPU (so an N-card rig shows 2N
// devices) and can't do shared USM. Never mine on it.
static bool is_translation_layer(const sycl::device& d) {
    auto pn = d.get_platform().get_info<sycl::info::platform::name>();
    return pn.find("OpenCLOn12") != std::string::npos || pn.find("D3D12") != std::string::npos;
}

static std::vector<sycl::device> gpu_devices() {
    auto raw = sycl::device::get_devices(sycl::info::device_type::gpu);
    std::vector<sycl::device> lz, real;
    for (const auto& d : raw) {
        if (d.get_backend() == sycl::backend::ext_oneapi_level_zero) lz.push_back(d);
        if (!is_translation_layer(d)) real.push_back(d);
    }
    // Prefer Level-Zero (fastest); else native GPUs with the OpenCLOn12/D3D12
    // translation-layer duplicates removed; else whatever we have.
    return !lz.empty() ? lz : (!real.empty() ? real : raw);
}

} // namespace

CSD_EXPORT int csd_capi_abi_version() { return kAbiVersion; }
CSD_EXPORT const char* csd_capi_last_error() { return g_last_error.c_str(); }

CSD_EXPORT int csd_capi_device_count() {
    try { return (int)gpu_devices().size(); }
    catch (...) { return 0; }
}

CSD_EXPORT int csd_capi_open(int device_index) {
    try {
        auto gpus = gpu_devices();
        if (gpus.empty()) { g_last_error = "no SYCL GPU devices"; return -1; }
        if (device_index < 0 || (size_t)device_index >= gpus.size()) {
            g_last_error = "device index out of range (" + std::to_string(gpus.size()) + " GPUs)"; return -2;
        }
        g_ctx = std::make_unique<Ctx>();
        // Explicit SINGLE-device context per queue. The implicit default context
        // spans every GPU in the platform, and on a multi-GPU rig shared-USM
        // allocation in that shared context fails on the later devices ("Device
        // does not support Shared USM allocations!"); a per-device context also
        // avoids racing the default context's lazy init across threads.
        sycl::device dev = gpus[(size_t)device_index];
        sycl::context ctx{dev};
        g_ctx->q = sycl::queue{ctx, dev, AsyncErrorHandler, sycl::property::queue::in_order{}};
        g_ctx->device_name = dev.get_info<sycl::info::device::name>();
        return 0;
    } catch (const std::exception& ex) { g_last_error = ex.what(); g_ctx.reset(); return -3; }
}

CSD_EXPORT const char* csd_capi_device_name() { return g_ctx ? g_ctx->device_name.c_str() : ""; }

// Name of GPU at `index` without opening it — lets the host enumerate/filter
// (e.g. skip integrated GPUs) before assigning threads to devices.
CSD_EXPORT const char* csd_capi_device_name_at(int index) {
    static thread_local std::string name;
    name.clear();
    try {
        auto gpus = gpu_devices();
        if (index >= 0 && index < (int)gpus.size())
            name = gpus[(size_t)index].get_info<sycl::info::device::name>();
    } catch (...) { name.clear(); }
    return name.c_str();
}

CSD_EXPORT void csd_capi_close() { if (g_ctx) { g_ctx->free_all(); g_ctx.reset(); } }

// Scan [nonce_base, nonce_base+count). mid=block-0 midstate (8), tail=header
// words 16..19 (4 used; index 4 reserved), target=8 BE words. Winning nonces
// (kernel W[4] values) land in found_out (up to found_cap); *found_total is the
// full count (may exceed cap).
CSD_EXPORT int csd_capi_search(
    const uint32_t* mid8, const uint32_t* tail5, const uint32_t* target8,
    uint32_t nonce_base, uint32_t count,
    uint32_t* found_out, uint32_t found_cap, uint32_t* found_total)
{
    *found_total = 0;
    if (!g_ctx) { g_last_error = "csd_capi_open not called"; return -1; }
    if (count == 0) { return 0; }
    try {
        Ctx& c = *g_ctx;
        c.ensure();
        g_async_failed = false; g_async_error.clear();
        std::array<uint32_t,8> mid; for (int i=0;i<8;++i) mid[i]=mid8[i];
        std::array<uint32_t,5> tail; for (int i=0;i<5;++i) tail[i]=tail5[i];
        std::array<uint32_t,8> tgt; for (int i=0;i<8;++i) tgt[i]=target8[i];
        c.d_count[0] = 0;
        csd::run_search(c.q, mid, tail, tgt, nonce_base, count, c.d_found, Ctx::kFoundCap, c.d_count);
        c.q.wait();
        if (g_async_failed) { g_last_error = "device kernel error: " + g_async_error; return -3; }
        uint32_t total = c.d_count[0];
        *found_total = total;
        uint32_t n = total < found_cap ? total : found_cap;
        uint32_t kept = n < Ctx::kFoundCap ? n : Ctx::kFoundCap;
        for (uint32_t i = 0; i < kept; ++i) found_out[i] = c.d_found[i];
        return 0;
    } catch (const std::exception& ex) { g_last_error = ex.what(); return -3; }
}
