// Support definitions that XMRig normally provides in crypto/rx/Rx.cpp and via
// libuv, but which this standalone shim must supply itself:
//
//  • The two runtime-selected blake2b function pointers. XMRig upgrades these to
//    SSE4.1/AVX2 based on CPU features; blake2b is a negligible fraction of RandomX
//    hashing, so we bind the SSE4.1 compress (universal on x86-64) and the portable
//    rx_blake2b — correct and effectively as fast, without the AVX2 blake2 sources.
//
//  • uv_hrtime(): argon2's impl-select benchmarks candidate implementations at init
//    using libuv's high-res clock. We provide a monotonic-nanoseconds equivalent so
//    argon2 (used only during cache init, off the hash hot path) links and runs.

#include <cstdint>
#include <cstddef>

#include "crypto/randomx/blake2/blake2.h"

extern "C" {

void (*rx_blake2b_compress)(blake2b_state *S, const uint8_t *block) = rx_blake2b_compress_sse41;
int  (*rx_blake2b)(void *out, size_t outlen, const void *in, size_t inlen) = rx_blake2b_default;

}

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

extern "C" uint64_t uv_hrtime(void)
{
    static LARGE_INTEGER freq = { };
    if (freq.QuadPart == 0) QueryPerformanceFrequency(&freq);
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    return (uint64_t)((now.QuadPart * 1000000000ULL) / (uint64_t)freq.QuadPart);
}
#else
#include <time.h>
extern "C" uint64_t uv_hrtime(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint64_t)ts.tv_sec * 1000000000ULL + (uint64_t)ts.tv_nsec;
}
#endif
