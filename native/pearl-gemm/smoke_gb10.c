/* Smoke test for the pearl_gemm C-ABI (e.g. GB10 / sm_121).
 * dlopen the built .so and exercise the build profile + sm support self-check.
 *
 * Usage: smoke_gb10 <path-to-libpearl_gemm_capi.so>
 *   build: gcc smoke_gb10.c -ldl -o smoke_gb10
 *   run:   LD_LIBRARY_PATH=/usr/local/cuda/lib64 ./smoke_gb10 path/to/libpearl_gemm_capi.so */
#include <stdio.h>
#include <dlfcn.h>

int main(int argc, char **argv) {
    if (argc < 2) {
        fprintf(stderr, "usage: %s <path-to-libpearl_gemm_capi.so>\n", argv[0]);
        return 2;
    }
    const char *so = argv[1];
    void *h = dlopen(so, RTLD_NOW);
    if (!h) { fprintf(stderr, "dlopen failed: %s\n", dlerror()); return 2; }

    const char *(*build_profile)(void) =
        (const char *(*)(void)) dlsym(h, "pearl_capi_build_profile");
    int (*supports_sm)(int, int) =
        (int (*)(int, int)) dlsym(h, "pearl_capi_supports_sm");

    if (!build_profile || !supports_sm) {
        fprintf(stderr, "dlsym failed: %s\n", dlerror());
        return 2;
    }

    const char *profile = build_profile();
    int sm121 = supports_sm(12, 1);   /* GB10 */
    int sm120 = supports_sm(12, 0);   /* nominal blackwell */

    printf("build_profile()        = %s\n", profile ? profile : "(null)");
    printf("supports_sm(12,1) GB10 = %d  -> %s\n", sm121, sm121 ? "PASS" : "(no)");
    printf("supports_sm(12,0)      = %d\n", sm120);

    dlclose(h);
    return sm121 ? 0 : 1;
}
