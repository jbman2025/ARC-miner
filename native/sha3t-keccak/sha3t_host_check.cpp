// sha3t_host_check.cpp — gate for the SHA3-256t kernel in sha3t_keccak.hpp.
//
// Compiles the SAME header the GPU kernel uses as plain host C++ and hashes a
// real mainnet block, so a typo in a rho offset or the pi cycle fails here in a
// second instead of showing up as a pool full of rejects. Build and run:
//
//   g++ -O2 -I. sha3t_host_check.cpp -o sha3t_host_check && ./sha3t_host_check
//
// Vector: BitcoinIII mainnet block 56000 (post the height-30240 sha3t fork —
// genesis and everything below 30240 is still sha256d and will NOT match).
// Cross-checked against Python hashlib.sha3_256 applied three times.
#include "sha3t_keccak.hpp"

#include <cstdio>
#include <cstring>
#include <cstdlib>

namespace {

constexpr const char* kPrev = "00000000000213a25516a1c0a19bb94e0cba10e7c18c8999b9a73ee029cd4267";
constexpr const char* kMerkle = "8ad442807e25fcb71d29c32045e3633eee02ca4d758615006f6b91aa9b16723c";
constexpr const char* kWant = "0000000000031c1896744b33c552471dfb51a5b470f90e452a4bc8213311f37a";
constexpr uint32_t kVersion = 536875008u;   // 0x20001000
constexpr uint32_t kTime = 1786738255u;
constexpr uint32_t kBits = 0x1b048245u;
constexpr uint32_t kNonce = 723721353u;

void put32(unsigned char* h, int off, uint32_t v) {
    h[off] = (unsigned char)v; h[off + 1] = (unsigned char)(v >> 8);
    h[off + 2] = (unsigned char)(v >> 16); h[off + 3] = (unsigned char)(v >> 24);
}

// A displayed (reversed) 32-byte hash into its header byte order.
void put_reversed(unsigned char* h, int off, const char* hex) {
    for (int i = 0; i < 32; ++i) {
        unsigned b = 0;
        if (sscanf(hex + 2 * i, "%2x", &b) != 1) { fprintf(stderr, "bad hex\n"); exit(2); }
        h[off + 31 - i] = (unsigned char)b;
    }
}

}  // namespace

int main() {
    unsigned char hdr[80];
    put32(hdr, 0, kVersion);
    put_reversed(hdr, 4, kPrev);
    put_reversed(hdr, 36, kMerkle);
    put32(hdr, 68, kTime);
    put32(hdr, 72, kBits);
    put32(hdr, 76, kNonce);

    uint64_t lanes[10];
    memcpy(lanes, hdr, 80);

    const sha3t::Digest4 d = sha3t::sha3t_hash(lanes, kNonce);
    const uint64_t out[4] = {d.l0, d.l1, d.l2, d.l3};
    unsigned char digest[32];
    memcpy(digest, out, 32);

    char got[65];
    for (int i = 0; i < 32; ++i) snprintf(got + 2 * i, 3, "%02x", digest[31 - i]);

    if (strcmp(got, kWant) != 0) {
        printf("FAIL block 56000\n  got  %s\n  want %s\n", got, kWant);
        return 1;
    }

    // The target compare must read the digest little-endian: block 56000 is
    // under its own nBits target and nowhere near a target one bit tighter.
    const uint64_t easy[4] = {~0ULL, ~0ULL, ~0ULL, 0x0000000000040000ULL};
    const uint64_t hard[4] = {0, 0, 0, 0};
    if (!sha3t::le_target(d, easy)) { printf("FAIL le_target: real block rejected\n"); return 1; }
    if (sha3t::le_target(d, hard)) { printf("FAIL le_target: zero target accepted\n"); return 1; }

    printf("OK sha3t %s\n", got);
    return 0;
}
