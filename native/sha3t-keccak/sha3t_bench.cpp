// sha3t_bench.cpp — hashrate for the shipping SHA3-256t kernel, with no pool
// and no wallet in the way.
//
// Links against the real sha3t_capi library and calls the same search entry the
// miner does, with a target of zero so nothing is ever "found" and the timing
// is pure kernel. Exists because the only number that settles a tuning question
// is a measured one, and standing up a pool session to get it is a slower loop.
//
//   icpx -fsycl -O3 sha3t_bench.cpp -L. -lsha3t_capi -o sha3t_bench
//   ARC_SHA3T_GRF=256 ARC_SHA3T_NPT=4 ./sha3t_bench [device] [slices]
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <chrono>

extern "C" {
int sha3t_capi_abi_version();
int sha3t_capi_open(int);
int sha3t_capi_device_count();
const char* sha3t_capi_device_name();
const char* sha3t_capi_last_error();
void sha3t_capi_close();
int sha3t_capi_search(const uint64_t*, const uint64_t*, uint32_t, uint32_t,
                      uint32_t*, uint32_t, uint32_t*);
int sha3t_capi_hash_one(const uint64_t*, uint32_t, uint64_t*);
}

int main(int argc, char** argv) {
    const int device = argc > 1 ? atoi(argv[1]) : 0;
    const int slices = argc > 2 ? atoi(argv[2]) : 8;
    // argv[3] = log2 of the nonces per launch. 24 matches
    // Sha3tStratumClient.SliceNonces; sweep it to see how much of the gap
    // between us and a reference miner is per-launch overhead rather than
    // kernel throughput.
    const int shift = argc > 3 ? atoi(argv[3]) : 24;
    const uint32_t kSlice = 1u << (shift < 10 ? 10 : (shift > 30 ? 30 : shift));

    printf("sha3t_capi abi v%d, %d GPU(s)\n", sha3t_capi_abi_version(), sha3t_capi_device_count());
    if (sha3t_capi_open(device) != 0) {
        printf("open(%d) failed: %s\n", device, sha3t_capi_last_error());
        return 1;
    }
    printf("device[%d] %s\n", device, sha3t_capi_device_name());

    // Mainnet block 56000's header as the ten kernel lanes, so the bench also
    // proves the device kernel reproduces the known hash before timing it.
    static const uint64_t hdr[10] = {
        0x29cd426720001000ULL, 0xc18c8999b9a73ee0ULL, 0xa19bb94e0cba10e7ULL,
        0x000213a25516a1c0ULL, 0x9b16723c00000000ULL, 0x758615006f6b91aaULL,
        0x45e3633eee02ca4dULL, 0x7e25fcb71d29c320ULL, 0x6a7f764f8ad44280ULL,
        0x2b231c891b048245ULL};
    // The block's own hash, as the four digest lanes.
    static const uint64_t want[4] = {
        0x2a4bc8213311f37aULL, 0xfb51a5b470f90e45ULL,
        0x96744b33c552471dULL, 0x0000000000031c18ULL};

    uint64_t out[4] = {0, 0, 0, 0};
    if (sha3t_capi_hash_one(hdr, 723721353u, out) != 0) {
        printf("hash_one failed: %s\n", sha3t_capi_last_error());
    } else {
        bool ok = true;
        for (int i = 0; i < 4; ++i) ok = ok && out[i] == want[i];
        printf("device hash of block 56000: %s\n", ok ? "OK" : "MISMATCH");
        if (!ok) {
            printf("  got  %016llx %016llx %016llx %016llx\n",
                   (unsigned long long)out[0], (unsigned long long)out[1],
                   (unsigned long long)out[2], (unsigned long long)out[3]);
            sha3t_capi_close();
            return 1;
        }
    }

    // Target 0: unreachable, so the found path never fires and the atomic never
    // contends. Timing is the permutations and nothing else.
    const uint64_t target[4] = {0, 0, 0, 0};
    uint32_t found[16], total = 0;

    // One untimed slice first — the JIT compile lands there, not in the average.
    if (sha3t_capi_search(hdr, target, 0, kSlice, found, 16, &total) != 0) {
        printf("search failed: %s\n", sha3t_capi_last_error());
        sha3t_capi_close();
        return 1;
    }

    double best = 0;
    for (int i = 0; i < slices; ++i) {
        auto t0 = std::chrono::steady_clock::now();
        int rc = sha3t_capi_search(hdr, target, (uint32_t)i * kSlice, kSlice, found, 16, &total);
        auto t1 = std::chrono::steady_clock::now();
        if (rc != 0) { printf("search failed: %s\n", sha3t_capi_last_error()); break; }
        double secs = std::chrono::duration<double>(t1 - t0).count();
        double mhs = kSlice / secs / 1e6;
        if (mhs > best) best = mhs;
        printf("  slice %2d: %7.2f ms  %8.2f MH/s\n", i, secs * 1e3, mhs);
    }
    printf("best: %.2f MH/s   (found=%u, expected 0)\n", best, total);

    // A target of zero exercises everything EXCEPT the one path that matters on
    // a pool. Run one more slice against a target loose enough to hit, and
    // re-hash each winner to prove the kernel is not just incrementing a
    // counter: a search that never reports is indistinguishable from a fast one
    // until the pool shows zero shares an hour later.
    const uint64_t loose[4] = {~0ULL, ~0ULL, ~0ULL, 0x0000ffffffffffffULL};
    uint32_t hits[16], hit_total = 0;
    if (sha3t_capi_search(hdr, loose, 0, kSlice, hits, 16, &hit_total) != 0) {
        printf("loose search failed: %s\n", sha3t_capi_last_error());
        sha3t_capi_close();
        return 1;
    }
    printf("loose target: %u hit(s) in %u nonces\n", hit_total, kSlice);
    if (hit_total == 0) {
        printf("FAIL: the found path never fired — a miner built on this submits nothing\n");
        sha3t_capi_close();
        return 1;
    }
    uint32_t check = hit_total < 16 ? hit_total : 16;
    for (uint32_t i = 0; i < check; ++i) {
        uint64_t d[4];
        sha3t_capi_hash_one(hdr, hits[i], d);
        bool under = d[3] < loose[3];
        printf("  nonce %08x -> lane3 %016llx  %s\n", hits[i],
               (unsigned long long)d[3], under ? "under target" : "ABOVE TARGET (BUG)");
        if (!under) { sha3t_capi_close(); return 1; }
    }

    sha3t_capi_close();
    return 0;
}
