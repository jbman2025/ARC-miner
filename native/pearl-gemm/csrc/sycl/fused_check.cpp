// fused_check.cpp — offline bit-exact validator for parallel_tensor_hash_fused.
//
// Runs the two-pass reference (launch_lcg_int7_fill -> parallel_tensor_hash) and
// the fused searched-rows kernel on a FIXED seed, then byte-compares:
//   (1) the 32-byte AHash (merkle root),
//   (2) the persisted search rows of A  (fused) vs the full LCG A (reference).
// Any mismatch prints the first divergent index. PASS only if both match.
//
// Adapted to the merged kernel API: launch_lcg_int7_fill / parallel_tensor_hash_fused
// take a precomputed splitmix `base` (the host now derives it from seed_lo/seed_hi).
//
// Build (JIT, runs on whatever Intel GPU is present — no AOT needed):
//   icpx -fsycl -O2 -I ../../csrc -I .. fused_check.cpp -o fused_check.exe
#include "pearl_kernels.hpp"
#include <cstdio>
#include <cstdint>
#include <vector>
#include <cstring>

static inline uint64_t host_splitmix(uint64_t z) {
    z += 0x9E3779B97F4A7C15ULL;
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ULL;
    z = (z ^ (z >> 27)) * 0x94D049BB133111EBULL;
    return z ^ (z >> 31);
}

int main() {
    // Prefer a GPU if present, else CPU; ALWAYS in-order (production stream is
    // in-order; USM ptrs get no auto-dep tracking, so out-of-order would race).
    auto sel = [](const sycl::device& d){ return d.is_gpu() ? 2 : (d.is_cpu() ? 1 : 0); };
    sycl::queue q{sel, {sycl::property::queue::in_order{}}};
    fprintf(stderr, "device: %s\n",
            q.get_device().get_info<sycl::info::device::name>().c_str());

    // Defaults to the EXACT B580 production shape (m=131072, k=4096, sm=4096).
    // Override via env for a quick small run: FC_M / FC_K / FC_SM.
    auto envl = [](const char* n, long d){ const char* v = getenv(n); return (v && atol(v) > 0) ? atol(v) : d; };
    const long m  = envl("FC_M", 131072);
    const long k  = envl("FC_K", 4096);
    long sm = envl("FC_SM", 4096);
    if (sm > m) sm = m;
    const long len = m * k;
    const long persist = sm * k;
    const long nchunks = (len + 1023) / 1024;

    const uint64_t seed_lo = 0x123456789abcdef0ULL;
    const uint64_t seed_hi = 0x0fedcba987654321ULL;
    const uint64_t base = host_splitmix(seed_lo ^ host_splitmix(seed_hi));
    fprintf(stderr, "shape: m=%ld k=%ld sm=%ld  len=%ld MiB  persist=%ld MiB  base=%016llx\n",
            m, k, sm, len >> 20, persist >> 20, (unsigned long long)base);

    u32 keyh[8] = {0x11111111u,0x22222222u,0x33333333u,0x44444444u,
                   0x55555555u,0x66666666u,0x77777777u,0x88888888u};

    u32*     key     = sycl::malloc_device<u32>(8, q);
    u32*     scratch = sycl::malloc_device<u32>((size_t)2 * nchunks * 8 + 16, q);
    uint8_t* A1      = sycl::malloc_device<uint8_t>(len, q);   // reference (full LCG)
    uint8_t* A2      = sycl::malloc_device<uint8_t>(len, q);   // fused (persist window)
    uint8_t* out1    = sycl::malloc_device<uint8_t>(32, q);
    uint8_t* out2    = sycl::malloc_device<uint8_t>(32, q);
    q.memcpy(key, keyh, 32).wait();

    // Reference: lcg fill(base) -> tensor_hash.
    pk::launch_lcg_int7_fill(A1, len, base, &q);
    pk::parallel_tensor_hash(A1, len, key, scratch, out1, &q);
    q.wait();

    // Fused: poison A2 first so a missed write is obvious, then run with base.
    q.memset(A2, 0xCC, len).wait();
    pk::parallel_tensor_hash_fused(A2, len, key, scratch, out2, &q, base, persist);
    q.wait();

    std::vector<uint8_t> h_out1(32), h_out2(32), h_A1(persist), h_A2(persist);
    q.memcpy(h_out1.data(), out1, 32);
    q.memcpy(h_out2.data(), out2, 32);
    q.memcpy(h_A1.data(), A1, persist);
    q.memcpy(h_A2.data(), A2, persist);
    q.wait();

    int fails = 0;
    auto hex = [](const uint8_t* p){ for (int i=0;i<32;++i) fprintf(stderr,"%02x",p[i]); };

    if (memcmp(h_out1.data(), h_out2.data(), 32) != 0) {
        fprintf(stderr, "FAIL AHash:\n  ref  "); hex(h_out1.data());
        fprintf(stderr, "\n  fused "); hex(h_out2.data()); fprintf(stderr, "\n");
        ++fails;
    } else { fprintf(stderr, "OK   AHash "); hex(h_out1.data()); fprintf(stderr, "\n"); }

    long badByte = -1;
    for (long p = 0; p < persist; ++p)
        if (h_A1[p] != h_A2[p]) { badByte = p; break; }
    if (badByte >= 0) {
        fprintf(stderr, "FAIL persisted A first diverges at byte %ld (row %ld col %ld): ref=%02x fused=%02x\n",
                badByte, badByte/k, badByte%k, h_A1[badByte], h_A2[badByte]);
        ++fails;
    } else fprintf(stderr, "OK   persisted A search rows (%ld B) match full LCG\n", persist);

    fprintf(stderr, fails ? "\n=== FUSED CHECK FAILED (%d) ===\n" : "\n=== FUSED CHECK PASSED ===\n", fails);
    return fails ? 1 : 0;
}
