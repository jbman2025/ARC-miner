// sha3t_capi.cpp — C ABI around the BitcoinIII SHA3-256t search kernel
// (sha3t_capi.dll). The hash itself lives in sha3t_keccak.hpp and is
// gate-validated on the host by sha3t_host_check.cpp.
//
// Managed contract (see src/Akoya.Miner/Algos/Sha3t/Sha3tNative.cs):
//  - The caller (C#) assembles the 80-byte header and hands it over as ten
//    little-endian u64 lanes; lane 9's high half is the nonce slot and is
//    overwritten per work-item.
//  - The target is four little-endian u64 lanes, lane 3 most significant.
//  - sha3t_capi_search scans [nonce_base, nonce_base+count) and appends every
//    nonce whose sha3t(header) <= target through an atomic counter.
//
// Deliberately mirrors csd_capi.cpp: same thread_local single-device context
// model, same Level-Zero de-duplication, same found/atomic reporting. The two
// differ only in the kernel and in the shape of the per-job constants.
#include <sycl/sycl.hpp>
#include <sycl/ext/intel/experimental/grf_size_properties.hpp>

#include "sha3t_keccak.hpp"

#include <array>
#include <cstdio>
#include <cstdlib>
#include <memory>
#include <string>
#include <vector>

#if defined(_WIN32)
#define SHA3T_EXPORT extern "C" __declspec(dllexport)
#else
#define SHA3T_EXPORT extern "C" __attribute__((visibility("default")))
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
// drives it independently. Every call for a device must happen on the same OS
// thread — the managed side guarantees that with a dedicated Thread per GPU.
thread_local std::unique_ptr<Ctx> g_ctx;
thread_local std::string g_last_error;
thread_local bool g_async_failed{false};
thread_local std::string g_async_error;

void AsyncErrorHandler(const sycl::exception_list& errs) {
    for (const std::exception_ptr& e : errs) {
        try { std::rethrow_exception(e); }
        catch (const std::exception& ex) {
            g_async_failed = true;
            if (!g_async_error.empty()) g_async_error += "; ";
            g_async_error += ex.what();
        }
    }
}

// See the long note in csd_capi.cpp: sycl::get_devices() returns each physical
// GPU once PER BACKEND, and the OpenCLOn12/D3D12 translation layer mirrors them
// again. Prefer Level-Zero, else the native list with translation layers gone.
bool is_translation_layer(const sycl::device& d) {
    auto pn = d.get_platform().get_info<sycl::info::platform::name>();
    return pn.find("OpenCLOn12") != std::string::npos || pn.find("D3D12") != std::string::npos;
}

std::vector<sycl::device> gpu_devices() {
    auto raw = sycl::device::get_devices(sycl::info::device_type::gpu);
    std::vector<sycl::device> lz, real;
    for (const auto& d : raw) {
        if (d.get_backend() == sycl::backend::ext_oneapi_level_zero) lz.push_back(d);
        if (!is_translation_layer(d)) real.push_back(d);
    }
    return !lz.empty() ? lz : (!real.empty() ? real : raw);
}

// getenv is the portable spelling and these are our own tuning knobs, never
// attacker-controlled — MSVC's _dupenv_s deprecation buys nothing here.
const char* env_str(const char* name) {
#if defined(_MSC_VER)
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
#endif
    return std::getenv(name);
#if defined(_MSC_VER)
#pragma clang diagnostic pop
#endif
}

// Nonces each work-item sweeps. Keccak has no memory traffic at all, so the
// only thing >1 buys is amortising dispatch over a longer-lived thread; the
// header lanes also stay in registers across the whole run.
//
// MEASURED on a B580 (2026-08-15, 128 GRF): 1/2/4/8 all land within 0.5% of
// each other at ~214 MH/s while 3/5/6 sag to ~205. It is the power of two that
// matters, not the size — an NPT that does not divide the launch leaves a
// ragged tail of work-items. 4 is the peak; do not "tune" this to an odd value.
uint32_t nonces_per_thread() {
    if (const char* v = env_str("ARC_SHA3T_NPT")) {
        int n = atoi(v);
        if (n >= 1 && n <= 256) return (uint32_t)n;
    }
    return 4;
}

// 25 u64 lanes of live keccak state is 50 32-bit registers per work-item before
// any temporaries, which is a lot of a 128-GRF Xe thread. Xe2 can run threads
// in 256-GRF mode instead: half the threads in flight, but no spill.
//
// MEASURED on a B580 (2026-08-15), and the answer is not the intuitive one:
// 128 GRF wins by 57% — 214.1 MH/s against 136.7. The row-at-a-time chi and the
// displacement-cycle rho/pi keep the working set inside 128 registers already,
// so 256-GRF mode buys no spill relief and simply halves the threads available
// to hide latency. Both kernels still ship: ARC_SHA3T_GRF=256 selects the other
// one, which is how this was measured and how it gets re-measured on a new die.
bool want_large_grf() {
    if (const char* v = env_str("ARC_SHA3T_GRF")) return atoi(v) >= 256;
    return false;
}

struct SearchArgs {
    std::array<uint64_t, 10> hdr;
    std::array<uint64_t, 4> target;
    uint32_t nonce_base;
    uint32_t count;
    uint32_t per;
    uint32_t* found;
    uint32_t cap;
    uint32_t* found_count;
};

// The kernel body, shared by both GRF variants; the two instantiations differ
// only in the GRF property attached to the functor below.
//
// The header and target are BY-VALUE kernel arguments. That is 14 u64 = 28
// registers whose live ranges span all three permutations, and ocloc duly warns
// "SIMD16 allocated 128 regs and spilled around 36". Passing them behind a
// POINTER instead removes the spill completely (and takes the SIMD32/256-GRF
// variant from 86 spills to 9). It is the obvious fix and it is the WRONG one:
//
//   B580, by-value -> pointer:  Windows AOT  175.6 -> 175.3  (neutral)
//                               Linux JIT    214.1 -> 176    (-18%)
//
// Measured 2026-08-15 — and the Linux half only after the pointer version had
// already shipped, which is exactly how a change validated as "neutral" on one
// platform became an 18% regression on the other. The driver's JIT evidently
// keeps by-value arguments somewhere better than the GRF; the offline AOT
// compiler does not, and its spill is hidden behind an ALU bottleneck anyway.
// So the spill warning is cosmetic on both platforms while this shape is
// load-bearing on one. Do not "fix" it, and do not trust a kernel measurement
// taken on one platform to carry to the other.
struct Sha3tBody {
    std::array<uint64_t, 10> hdr;
    std::array<uint64_t, 4> target;
    uint32_t nonce_base, count, per, cap;
    uint32_t* found;
    uint32_t* found_count;

    void operator()(sycl::id<1> gid) const {
        using Atom = sycl::atomic_ref<uint32_t, sycl::memory_order::relaxed,
                                      sycl::memory_scope::device,
                                      sycl::access::address_space::global_space>;
        const uint32_t start = static_cast<uint32_t>(gid[0]) * per;
        for (uint32_t j = 0; j < per; ++j) {
            const uint32_t idx = start + j;
            if (idx >= count) break;
            const uint32_t nonce = nonce_base + idx;
            const sha3t::Digest4 h = sha3t::sha3t_hash(hdr.data(), nonce);
            if (sha3t::le_target(h, target.data())) {
                uint32_t slot = Atom(found_count[0]).fetch_add(1u);
                if (slot < cap) found[slot] = nonce;
            }
        }
    }
};

// Same body, but asking the backend for 256-GRF threads. The property rides on
// the functor (the parallel_for overload that took a property list is
// deprecated), which is also why this is a type and not a launch argument.
struct Sha3tBodyLargeGrf : Sha3tBody {
    using Sha3tBody::operator();
    auto get(sycl::ext::oneapi::experimental::properties_tag) const {
        return sycl::ext::oneapi::experimental::properties{
            sycl::ext::intel::experimental::grf_size<256>};
    }
};

// Distinct kernel names so device-code-split keeps them as separate images.
class Sha3tSearchDefaultGrf;
class Sha3tSearchLargeGrf;

void run_search(sycl::queue& q, const SearchArgs& s, bool large_grf) {
    const uint32_t threads = (s.count + s.per - 1) / s.per;
    const Sha3tBody body{s.hdr, s.target, s.nonce_base, s.count, s.per, s.cap,
                         s.found, s.found_count};
    if (large_grf) {
        q.parallel_for<Sha3tSearchLargeGrf>(sycl::range<1>{threads}, Sha3tBodyLargeGrf{body});
    } else {
        q.parallel_for<Sha3tSearchDefaultGrf>(sycl::range<1>{threads}, body);
    }
}

}  // namespace

SHA3T_EXPORT int sha3t_capi_abi_version() { return kAbiVersion; }
SHA3T_EXPORT const char* sha3t_capi_last_error() { return g_last_error.c_str(); }

SHA3T_EXPORT int sha3t_capi_device_count() {
    try { return (int)gpu_devices().size(); }
    catch (...) { return 0; }
}

SHA3T_EXPORT int sha3t_capi_open(int device_index) {
    try {
        auto gpus = gpu_devices();
        if (gpus.empty()) { g_last_error = "no SYCL GPU devices"; return -1; }
        if (device_index < 0 || (size_t)device_index >= gpus.size()) {
            g_last_error = "device index out of range (" + std::to_string(gpus.size()) + " GPUs)";
            return -2;
        }
        g_ctx = std::make_unique<Ctx>();
        // Explicit SINGLE-device context per queue — the implicit default
        // context spans every GPU in the platform and its shared-USM
        // allocations fail on the later devices of a multi-GPU rig.
        sycl::device dev = gpus[(size_t)device_index];
        sycl::context ctx{dev};
        g_ctx->q = sycl::queue{ctx, dev, AsyncErrorHandler, sycl::property::queue::in_order{}};
        g_ctx->device_name = dev.get_info<sycl::info::device::name>();
        return 0;
    } catch (const std::exception& ex) { g_last_error = ex.what(); g_ctx.reset(); return -3; }
}

SHA3T_EXPORT const char* sha3t_capi_device_name() { return g_ctx ? g_ctx->device_name.c_str() : ""; }

// Name of the GPU at `index` without opening it — lets the host enumerate and
// filter (e.g. skip integrated GPUs) before assigning threads to devices.
SHA3T_EXPORT const char* sha3t_capi_device_name_at(int index) {
    static thread_local std::string name;
    name.clear();
    try {
        auto gpus = gpu_devices();
        if (index >= 0 && index < (int)gpus.size())
            name = gpus[(size_t)index].get_info<sycl::info::device::name>();
    } catch (...) { name.clear(); }
    return name.c_str();
}

SHA3T_EXPORT void sha3t_capi_close() { if (g_ctx) { g_ctx->free_all(); g_ctx.reset(); } }

// Scan [nonce_base, nonce_base+count). hdr10 = the 80-byte header as ten LE
// u64 lanes (lane 9's high half is the nonce slot); target4 = four LE u64
// lanes, index 3 most significant. Winning nonces land in found_out (up to
// found_cap); *found_total is the full count and may exceed the cap.
SHA3T_EXPORT int sha3t_capi_search(
    const uint64_t* hdr10, const uint64_t* target4,
    uint32_t nonce_base, uint32_t count,
    uint32_t* found_out, uint32_t found_cap, uint32_t* found_total)
{
    *found_total = 0;
    if (!g_ctx) { g_last_error = "sha3t_capi_open not called"; return -1; }
    if (count == 0) return 0;
    try {
        Ctx& c = *g_ctx;
        c.ensure();
        g_async_failed = false; g_async_error.clear();

        SearchArgs s{};
        for (int i = 0; i < 10; ++i) s.hdr[i] = hdr10[i];
        for (int i = 0; i < 4; ++i) s.target[i] = target4[i];
        s.nonce_base = nonce_base;
        s.count = count;
        s.per = nonces_per_thread();
        s.found = c.d_found;
        s.cap = Ctx::kFoundCap;
        s.found_count = c.d_count;

        c.d_count[0] = 0;
        run_search(c.q, s, want_large_grf());
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

// Hash ONE header on the GPU and return the four digest lanes. Not a mining
// entry point — it exists so the managed test suite can prove the device
// kernel agrees with the host implementation on a real block, which is the one
// thing sha3t_host_check.cpp cannot cover.
SHA3T_EXPORT int sha3t_capi_hash_one(const uint64_t* hdr10, uint32_t nonce, uint64_t* out4) {
    if (!g_ctx) { g_last_error = "sha3t_capi_open not called"; return -1; }
    try {
        Ctx& c = *g_ctx;
        uint64_t* buf = sycl::malloc_shared<uint64_t>(14, c.q);
        for (int i = 0; i < 10; ++i) buf[i] = hdr10[i];
        c.q.single_task([=]() {
            const sha3t::Digest4 h = sha3t::sha3t_hash(buf, nonce);
            buf[10] = h.l0; buf[11] = h.l1; buf[12] = h.l2; buf[13] = h.l3;
        }).wait();
        for (int i = 0; i < 4; ++i) out4[i] = buf[10 + i];
        sycl::free(buf, c.q);
        return 0;
    } catch (const std::exception& ex) { g_last_error = ex.what(); return -3; }
}
