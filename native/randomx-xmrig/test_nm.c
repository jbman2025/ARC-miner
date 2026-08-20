/* Smoke test for neuromorph_capi. Proves internal consistency only — there is no
 * published NeuroMorph test vector, so consensus correctness is established by a
 * pool accepting shares (see the GhostRider note in build_gr_capi.bat).
 *
 * Build (from native/randomx-xmrig, after build_nm_capi.bat). Note the output
 * paths: build_capi.bat links *.obj, so a test_*.obj left in this directory ends
 * up inside randomx_capi.dll and the link fails on a duplicate main().
 *   cl /nologo /O2 /MD /Fo:tests\ /Fe:tests\ test_nm.c neuromorph_capi.lib
 * or on Linux, after build_nm_capi.sh:
 *   cc -O2 test_nm.c -L. -lneuromorph_capi -Wl,-rpath,. -o tests/test_nm
 */
#include <stdio.h>
#include <stdint.h>
#include <string.h>
extern int         nm_capi_abi_version(void);
extern int         nm_capi_header_len(void);
extern int         nm_capi_nonce_offset(void);
extern int         nm_capi_selftest(void);
extern const char* nm_capi_last_error(void);
extern void*       nm_capi_create_ctx(void);
extern void        nm_capi_destroy_ctx(void*);
extern int         nm_capi_set_seed(void*, const uint8_t*);
extern void        nm_capi_hash(void*, const uint8_t*, uint64_t, uint8_t*);

int main(void){
    printf("abi %d, header %d, nonce offset %d\n",
           nm_capi_abi_version(), nm_capi_header_len(), nm_capi_nonce_offset());
    if (nm_capi_header_len() != 124 || nm_capi_nonce_offset() != 116) {
        printf("FAIL: header/nonce constants do not match PROTOCOL.md\n"); return 1;
    }
    int rc = nm_capi_selftest();
    printf("selftest: %s%s\n", rc==0?"PASS":"FAIL ", rc==0?"":nm_capi_last_error());
    if (rc) return 1;

    /* A different epoch seed must change the hash of the same header. */
    void* c = nm_capi_create_ctx();
    uint8_t s1[32], s2[32], hdr[124], a[32], b[32];
    for (int i=0;i<32;i++){ s1[i]=(uint8_t)(i*7+1); s2[i]=(uint8_t)(i*7+2); }
    for (int i=0;i<124;i++) hdr[i]=(uint8_t)(i*3+5);
    nm_capi_set_seed(c, s1); nm_capi_hash(c, hdr, 100000, a);
    nm_capi_set_seed(c, s2); nm_capi_hash(c, hdr, 100000, b);
    if (!memcmp(a,b,32)) { printf("FAIL: epoch seed change did not alter the hash\n"); return 1; }
    printf("PASS: seed epoch affects the hash\n");

    /* Below NM_DATASET_HEIGHT the memory-hard step is skipped -> different hash. */
    nm_capi_set_seed(c, s1);
    nm_capi_hash(c, hdr, 100, b);
    if (!memcmp(a,b,32)) { printf("FAIL: dataset activation height had no effect\n"); return 1; }
    printf("PASS: dataset activates at height >= 240\n");

    printf("hash(seed1,h=100000): ");
    for (int i=0;i<32;i++) printf("%02x", a[i]);
    printf("\nall checks passed\n");
    nm_capi_destroy_ctx(c);
    return 0;
}
