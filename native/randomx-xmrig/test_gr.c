/* Standalone smoke test for ghostrider_capi.
 *
 * Build (from native/randomx-xmrig, after build_gr_capi.bat). Note the output
 * paths: build_capi.bat links *.obj, so a test_*.obj left in this directory ends
 * up inside randomx_capi.dll and the link fails on a duplicate main().
 *   cl /nologo /O2 /MD /Fo:tests\ /Fe:tests\ test_gr.c ghostrider_capi.lib
 * or on Linux, after build_gr_capi.sh:
 *   cc -O2 test_gr.c -L. -lghostrider_capi -Wl,-rpath,. -o tests/test_gr
 *
 * Checks:
 *   1. XMRig's canonical GhostRider self-test vector (test_output_gr).
 *   2. All 8 lanes agree when fed identical headers — catches scratchpad
 *      packing/aliasing bugs in the octa path that (1) would not.
 *   3. Hashing is deterministic across calls and across contexts.
 *
 * Deliberately NOT checked here: whether a specific header hashes below a
 * specific target. An earlier version of this file asserted against a header
 * reconstructed by hand from a cpuminer protocol dump; the reconstruction was
 * wrong, and the failing assertion sent this port chasing a nonexistent bug in
 * the hash for a long time. The only trustworthy end-to-end check is a live
 * pool accepting shares — run the miner against one.
 */
#include <stdio.h>
#include <string.h>
#include <stdint.h>

extern int         ghostrider_capi_abi_version(void);
extern int         ghostrider_capi_lanes(void);
extern int         ghostrider_capi_selftest(void);
extern const char* ghostrider_capi_last_error(void);
extern void*       ghostrider_capi_create_ctx(void);
extern void        ghostrider_capi_destroy_ctx(void*);
extern void        ghostrider_capi_hash_octa(void*, const uint8_t*, uint32_t, uint8_t*);

#define LANES 8

static void fill_header(uint8_t* h, uint32_t nonce, uint8_t seed)
{
    memset(h, 0, 80);
    h[0] = 0x20;
    for (int i = 4; i < 36; ++i) h[i] = (uint8_t)(i * 7 + seed);  /* algo-selecting seed */
    for (int i = 36; i < 76; ++i) h[i] = (uint8_t)(i * 13 + seed);
    memcpy(h + 76, &nonce, 4);
}

int main(void)
{
    int failures = 0;

    printf("abi %d, lanes %d\n", ghostrider_capi_abi_version(), ghostrider_capi_lanes());
    if (ghostrider_capi_lanes() != LANES) {
        printf("FAIL: native reports %d lanes, expected %d\n", ghostrider_capi_lanes(), LANES);
        return 1;
    }

    int rc = ghostrider_capi_selftest();
    if (rc == 0) {
        printf("PASS: XMRig GhostRider self-test vector\n");
    }
    else {
        printf("FAIL: self-test (%d) - %s\n", rc, ghostrider_capi_last_error());
        ++failures;
    }

    void* ctx = ghostrider_capi_create_ctx();
    if (!ctx) {
        printf("FAIL: create_ctx - %s\n", ghostrider_capi_last_error());
        return 1;
    }

    /* 2. Identical headers in every lane must produce identical hashes. */
    {
        uint8_t hdr[80], blob[80 * LANES], out[32 * LANES];
        fill_header(hdr, 0x12345678u, 0x5a);
        for (int l = 0; l < LANES; ++l) memcpy(blob + l * 80, hdr, 80);

        ghostrider_capi_hash_octa(ctx, blob, 80, out);

        int bad = 0;
        for (int l = 1; l < LANES; ++l) {
            if (memcmp(out, out + l * 32, 32) != 0) { bad = l; break; }
        }
        if (bad) {
            printf("FAIL: lane %d disagrees with lane 0 on an identical header\n", bad);
            ++failures;
        }
        else {
            printf("PASS: all %d lanes agree (", LANES);
            for (int i = 0; i < 8; ++i) printf("%02x", out[i]);
            printf("...)\n");
        }
    }

    /* 3. Determinism: same input, same output, across calls and contexts. */
    {
        uint8_t blob[80 * LANES], a[32 * LANES], b[32 * LANES], c[32 * LANES];
        for (int l = 0; l < LANES; ++l) fill_header(blob + l * 80, 1000u + l, (uint8_t)l);

        ghostrider_capi_hash_octa(ctx, blob, 80, a);
        ghostrider_capi_hash_octa(ctx, blob, 80, b);

        void* ctx2 = ghostrider_capi_create_ctx();
        if (!ctx2) {
            printf("FAIL: second create_ctx - %s\n", ghostrider_capi_last_error());
            ++failures;
        }
        else {
            ghostrider_capi_hash_octa(ctx2, blob, 80, c);
            ghostrider_capi_destroy_ctx(ctx2);
        }

        if (memcmp(a, b, sizeof(a)) != 0) {
            printf("FAIL: repeated call on the same context differs\n");
            ++failures;
        }
        else if (memcmp(a, c, sizeof(a)) != 0) {
            printf("FAIL: a fresh context hashes the same input differently\n");
            ++failures;
        }
        else {
            printf("PASS: deterministic across calls and contexts\n");
        }

        /* Different nonces must not collide. */
        if (memcmp(a, a + 32, 32) == 0) {
            printf("FAIL: lanes with different nonces produced the same hash\n");
            ++failures;
        }
    }

    ghostrider_capi_destroy_ctx(ctx);

    printf("%s\n", failures ? "FAILED" : "all checks passed");
    return failures ? 1 : 0;
}
