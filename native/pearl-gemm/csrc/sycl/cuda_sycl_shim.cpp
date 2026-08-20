// cuda_sycl_shim.cpp — libcuda.so.1 drop-in that forwards the CUDA Driver API
// surface used by Akoya.Cuda.CudaDriver to SYCL/oneAPI, so the C# host runs
// unchanged on Intel Arc GPUs.
//
// Build:
//   icpx -fsycl -O2 -fPIC -shared cuda_sycl_shim.cpp -o libcuda.so.1
//
// Design:
//   CUdevice  → integer ordinal into g_dev_states[]
//   CUcontext → opaque handle encoding the device ordinal  (ordinal+1 cast to void*)
//   CUstream  → sycl::queue* (in-order, profiling enabled)
//   CUevent   → sycl::event* (recorded on a queue)
//   Device/host memory → USM pointers from sycl::malloc_device / malloc_host
//
// Only GPU devices are enumerated; they are presented to the caller in the
// order returned by SYCL (typically Intel iGPU first, dGPU/Arc second on a
// typical desktop). Set AKOYA_GPU_INDICES in the miner config to pick a
// specific Arc card.

#include <sycl/sycl.hpp>
#include <cstring>
#define HQUEUE(s) static_cast<sycl::queue*>(s)
#include <cstdio>
#include <cstdlib>
#include <vector>
#include <mutex>
#include <unordered_map>

// Kernel name tag for cuEventRecord no-op marker (must be at namespace scope).
struct RecordTag {};

// ── Global device state ───────────────────────────────────────────────────────

struct DeviceState {
    sycl::device   dev;
    sycl::context  ctx;
    sycl::queue*   default_q = nullptr;  // created on first use
    std::mutex     mu;
};

static std::vector<DeviceState*> g_devs;
static std::once_flag            g_init;

static void init_devices() {
    std::call_once(g_init, []() {
        // A single physical GPU is often exposed by several backends at once
        // (Level-Zero AND OpenCL). Enumerating all of them yields duplicate
        // devices, and the non-Level-Zero entries may not support USM device
        // allocations — using one would throw and crash across the C ABI.
        // Prefer Level-Zero GPUs; fall back to any GPU that supports USM
        // device allocations only if no Level-Zero GPU is present.
        auto collect = [](bool level_zero_only) {
            for (auto& p : sycl::platform::get_platforms()) {
                for (auto& d : p.get_devices()) {
                    if (d.get_info<sycl::info::device::device_type>()
                        != sycl::info::device_type::gpu) continue;
                    if (!d.has(sycl::aspect::usm_device_allocations)) continue;
                    if (level_zero_only &&
                        d.get_backend() != sycl::backend::ext_oneapi_level_zero)
                        continue;
                    g_devs.push_back(new DeviceState{d, sycl::context(d)});
                }
            }
        };
        collect(/*level_zero_only=*/true);
        if (g_devs.empty()) collect(/*level_zero_only=*/false);
    });
}

// Thread-local "current device" (set by cuCtxSetCurrent).
static thread_local int g_cur_dev = 0;

static DeviceState* cur_state() {
    init_devices();
    if (g_devs.empty()) return nullptr;
    int idx = g_cur_dev < (int)g_devs.size() ? g_cur_dev : 0;
    return g_devs[idx];
}

static sycl::queue* ensure_default_queue(DeviceState* s) {
    std::lock_guard<std::mutex> lk(s->mu);
    if (!s->default_q) {
        s->default_q = new sycl::queue(
            s->ctx, s->dev,
            sycl::property_list{
                sycl::property::queue::in_order{},
                sycl::property::queue::enable_profiling{}
            });
    }
    return s->default_q;
}

// Context handle encoding: (void*)(ordinal + 1).
static inline void* encode_ctx(int ordinal) {
    return reinterpret_cast<void*>((uintptr_t)(ordinal + 1));
}
static inline int decode_ctx(void* h) {
    return (int)((uintptr_t)h) - 1;
}

// ── C ABI ─────────────────────────────────────────────────────────────────────

#ifdef _WIN32
#  define CUDA_EXPORT __declspec(dllexport)
#else
#  define CUDA_EXPORT
#endif

extern "C" {

CUDA_EXPORT int cuInit(unsigned int) { init_devices(); return 0; }

CUDA_EXPORT int cuDriverGetVersion(int* v) { *v = 12000; return 0; }

CUDA_EXPORT int cuDeviceGetCount(int* c) {
    init_devices();
    *c = (int)g_devs.size();
    return 0;
}

CUDA_EXPORT int cuDeviceGet(int* dev, int ordinal) {
    *dev = ordinal;
    return 0;
}

CUDA_EXPORT int cuDeviceGetName(char* name, int len, int dev) {
    init_devices();
    if (dev < 0 || dev >= (int)g_devs.size()) { snprintf(name, len, "Unknown"); return 0; }
    auto s = g_devs[dev]->dev.get_info<sycl::info::device::name>();
    snprintf(name, len, "%s", s.c_str());
    return 0;
}

CUDA_EXPORT int cuDeviceTotalMem_v2(size_t* b, int dev) {
    init_devices();
    if (dev < 0 || dev >= (int)g_devs.size()) { *b = 0; return 0; }
    *b = g_devs[dev]->dev.get_info<sycl::info::device::global_mem_size>();
    return 0;
}

// Report the device's REAL PCI address. This used to return a hard-coded
// "0000:00:00.0" for every device, which made it useless for telling cards
// apart — and the sysfs hwmon sensors (temperature, fan, energy) are keyed by
// exactly this string, so without it a multi-GPU rig can only guess which
// reading belongs to which card. Guessing wrong reports card A's temperature
// against card B, which is worse than showing nothing at all.
//
// ext::intel::info::device::pci_address is available on the OpenCL backend, not
// just Level Zero — verified on a 2x Arc B580 rig, which matters because a stock
// HiveOS install has no Level Zero at all.
CUDA_EXPORT int cuDeviceGetPCIBusId(char* s, int len, int dev) {
    if (!s || len <= 0) return 1;   // CUDA_ERROR_INVALID_VALUE
    init_devices();
    if (dev >= 0 && dev < (int)g_devs.size()) {
        try {
            auto addr = g_devs[dev]->dev
                .get_info<sycl::ext::intel::info::device::pci_address>();
            snprintf(s, len, "%s", addr.c_str());
            return 0;
        } catch (...) {
            // Older runtimes gate this behind SYCL_ENABLE_PCI; fall through to
            // the placeholder rather than failing the call.
        }
    }
    snprintf(s, len, "0000:00:00.0");
    return 0;
}

CUDA_EXPORT int cuDeviceComputeCapability(int* major, int* minor, int) {
    // Report a plausible synthetic SM value; the miner uses this only for logging.
    *major = 12; *minor = 0;
    return 0;
}

CUDA_EXPORT int cuDeviceGetAttribute(int* pi, int attr, int dev) {
    init_devices();
    *pi = 0;
    if (dev < 0 || dev >= (int)g_devs.size()) return 0;
    auto& d = g_devs[dev]->dev;
    switch (attr) {
        case 16: // MULTIPROCESSOR_COUNT
            *pi = (int)d.get_info<sycl::info::device::max_compute_units>();
            break;
        case 13: // CLOCK_RATE (kHz)
            *pi = (int)d.get_info<sycl::info::device::max_clock_frequency>() * 1000;
            break;
        case 36: // MEMORY_CLOCK_RATE (kHz) — not directly available
            *pi = 0;
            break;
        case 37: // GLOBAL_MEMORY_BUS_WIDTH — not available
            *pi = 0;
            break;
        default: *pi = 0; break;
    }
    return 0;
}

// ── Context management ────────────────────────────────────────────────────────

CUDA_EXPORT int cuDevicePrimaryCtxRetain(void** ctx, int dev) {
    init_devices();
    if (dev < 0 || dev >= (int)g_devs.size()) return -1;
    *ctx = encode_ctx(dev);
    // Ensure a default queue exists for the device
    ensure_default_queue(g_devs[dev]);
    return 0;
}

CUDA_EXPORT int cuDevicePrimaryCtxRelease_v2(int) { return 0; }
CUDA_EXPORT int cuDevicePrimaryCtxSetFlags_v2(int, unsigned int) { return 0; }

CUDA_EXPORT int cuCtxCreate_v2(void** ctx, unsigned int, int dev) {
    return cuDevicePrimaryCtxRetain(ctx, dev);
}

CUDA_EXPORT int cuCtxDestroy_v2(void*) { return 0; }

CUDA_EXPORT int cuCtxSetCurrent(void* ctx) {
    if (!ctx) return 0;
    int dev = decode_ctx(ctx);
    init_devices();
    if (dev >= 0 && dev < (int)g_devs.size()) g_cur_dev = dev;
    return 0;
}

// ── Memory ────────────────────────────────────────────────────────────────────
//
// Multi-GPU correctness: USM pointers are CONTEXT-bound. The original frees
// used cur_state() — the calling THREAD's current device — so a pointer
// allocated for device 1 but freed from a thread parked on device 0 (GC,
// orchestrator, finalizer) was released against the wrong sycl::context.
// NEO's OpenCL USM tracking corrupted on that (observed with 2× B580:
// SIGSEGV in urUSMFree / abort in enqueue_svm.h). Record the owning device
// per allocation and always free against it; serialize alloc/free since
// NEO also raced on concurrent multi-context alloc/free.
//
// STATUS 2026-07-30: multi-GPU OpenCL now runs clean in the field (2× B580,
// intel-opencl-icd 25.18.33578.6), so the NEO race appears fixed.
//
// The owner map STAYS REGARDLESS, and not because of NEO: freeing a USM pointer
// against a different sycl::context than it was allocated from is invalid by the
// SYCL spec, full stop. NEO's corruption was the symptom that exposed the bug,
// not the cause of it — a better driver just makes the same misuse fail more
// quietly. Only the alloc/free SERIALIZATION here is a NEO workaround and thus
// arguably obsolete; the g_ptr_owner lookup is plain correctness.
static std::mutex g_mem_mu;
static std::unordered_map<void*, DeviceState*> g_ptr_owner;

static void record_owner(void* p, DeviceState* s) {
    std::lock_guard<std::mutex> lk(g_mem_mu);
    g_ptr_owner[p] = s;
}
static DeviceState* take_owner(void* p) {
    std::lock_guard<std::mutex> lk(g_mem_mu);
    auto it = g_ptr_owner.find(p);
    if (it == g_ptr_owner.end()) return nullptr;
    DeviceState* s = it->second;
    g_ptr_owner.erase(it);
    return s;
}

CUDA_EXPORT int cuMemAlloc_v2(void** dptr, size_t n) {
    auto* s = cur_state();
    if (!s) return -1;
    auto* q = ensure_default_queue(s);
    *dptr = sycl::malloc_device(n, *q);
    if (*dptr) record_owner(*dptr, s);
    return *dptr ? 0 : -1;
}

CUDA_EXPORT int cuMemFree_v2(void* dptr) {
    if (!dptr) return -1;
    DeviceState* s = take_owner(dptr);
    if (!s) s = cur_state();          // pre-tracking pointer: old behaviour
    if (!s) return -1;
    auto* q = ensure_default_queue(s);
    sycl::free(dptr, *q);
    return 0;
}

CUDA_EXPORT int cuMemGetInfo_v2(size_t* fr, size_t* tot) {
    auto* s = cur_state();
    if (!s) { *fr = *tot = 0; return 0; }
    *tot = s->dev.get_info<sycl::info::device::global_mem_size>();
    // Free memory is not exposed by SYCL; report 50% as a conservative estimate.
    *fr  = *tot / 2;
    return 0;
}

CUDA_EXPORT int cuMemHostAlloc(void** pp, size_t n, unsigned int) {
    auto* s = cur_state();
    if (!s) return -1;
    auto* q = ensure_default_queue(s);
    *pp = sycl::malloc_host(n, *q);
    if (*pp) record_owner(*pp, s);
    return *pp ? 0 : -1;
}

CUDA_EXPORT int cuMemFreeHost(void* p) {
    if (!p) return -1;
    DeviceState* s = take_owner(p);
    if (!s) s = cur_state();          // pre-tracking pointer: old behaviour
    if (!s) return -1;
    auto* q = ensure_default_queue(s);
    sycl::free(p, *q);
    return 0;
}

CUDA_EXPORT int cuMemcpyHtoD_v2(void* dst, const void* src, size_t n) {
    auto* s = cur_state();
    if (!s) return -1;
    auto* q = ensure_default_queue(s);
    q->memcpy(dst, src, n).wait();
    return 0;
}

CUDA_EXPORT int cuMemcpyDtoH_v2(void* dst, const void* src, size_t n) {
    auto* s = cur_state();
    if (!s) return -1;
    auto* q = ensure_default_queue(s);
    q->memcpy(dst, src, n).wait();
    return 0;
}

CUDA_EXPORT int cuMemcpyHtoDAsync_v2(void* dst, const void* src, size_t n, void* stream) {
    HQUEUE(stream)->memcpy(dst, src, n);
    return 0;
}

CUDA_EXPORT int cuMemcpyDtoHAsync_v2(void* dst, const void* src, size_t n, void* stream) {
    HQUEUE(stream)->memcpy(dst, src, n);
    return 0;
}

CUDA_EXPORT int cuMemsetD8_v2(void* dst, unsigned char v, size_t n) {
    auto* s = cur_state();
    if (!s) return -1;
    auto* q = ensure_default_queue(s);
    q->memset(dst, v, n).wait();
    return 0;
}

CUDA_EXPORT int cuMemsetD8Async(void* dst, unsigned char v, size_t n, void* stream) {
    HQUEUE(stream)->memset(dst, v, n);
    return 0;
}

// ── Streams ───────────────────────────────────────────────────────────────────

CUDA_EXPORT int cuStreamCreate(void** s, unsigned int) {
    auto* ds = cur_state();
    if (!ds) return -1;
    try {
        *s = new sycl::queue(
            ds->ctx, ds->dev,
            sycl::property_list{
                sycl::property::queue::in_order{},
                sycl::property::queue::enable_profiling{}
            });
        return 0;
    } catch (...) { return -1; }
}

CUDA_EXPORT int cuStreamSynchronize(void* s) {
    try { HQUEUE(s)->wait_and_throw(); return 0; }
    catch (...) { return -1; }
}

CUDA_EXPORT int cuStreamDestroy_v2(void* s) {
    delete HQUEUE(s);
    return 0;
}

// ── Events ────────────────────────────────────────────────────────────────────

struct SyclEvent {
    sycl::event ev;
    bool        recorded = false;
};

CUDA_EXPORT int cuEventCreate(void** e, unsigned int) {
    *e = new SyclEvent();
    return 0;
}

CUDA_EXPORT int cuEventRecord(void* e, void* stream) {
    auto* se = static_cast<SyclEvent*>(e);
    // Submit a lightweight no-op marker to capture the queue timestamp.
    se->ev = HQUEUE(stream)->single_task<RecordTag>([](){});
    se->recorded = true;
    return 0;
}

CUDA_EXPORT int cuEventSynchronize(void* e) {
    auto* se = static_cast<SyclEvent*>(e);
    if (se->recorded) se->ev.wait();
    return 0;
}

CUDA_EXPORT int cuEventElapsedTime(float* ms, void* start, void* end) {
    auto* s = static_cast<SyclEvent*>(start);
    auto* f = static_cast<SyclEvent*>(end);
    *ms = 0.0f;
    if (!s->recorded || !f->recorded) return 0;
    try {
        auto t0 = s->ev.get_profiling_info<sycl::info::event_profiling::command_end>();
        auto t1 = f->ev.get_profiling_info<sycl::info::event_profiling::command_end>();
        *ms = (float)((double)(t1 - t0) * 1e-6);
    } catch (...) { *ms = 0.0f; }
    return 0;
}

CUDA_EXPORT int cuEventDestroy_v2(void* e) {
    delete static_cast<SyclEvent*>(e);
    return 0;
}

} // extern "C"
