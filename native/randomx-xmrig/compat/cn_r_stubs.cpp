// Unreachable CryptoNight-R JIT symbols.
//
// XMRig's CnHash.cpp registers every CryptoNight variant, including cn/r, whose
// per-height random-math mainloop is JITed by v4_*_compile_code() in
// crypto/cn/r/CryptonightR_gen.cpp (which in turn needs the CryptonightR asm
// templates). GhostRider never selects cn/r — its six variants are
// cn/dark, cn/dark-lite, cn/fast, cn/lite, cn/turtle and cn/turtle-lite — so
// linking that whole toolchain in would be dead weight.
//
// These stubs satisfy the linker instead. They abort rather than return, so a
// future caller that does reach cn/r fails loudly instead of running a
// half-initialized code buffer.

#include <cstdio>
#include <cstdlib>

#include "crypto/common/Assembly.h"

struct V4_Instruction;

static void unreachable(const char* fn)
{
    fprintf(stderr,
            "ghostrider_capi: %s called, but cn/r is not supported in this build. "
            "GhostRider never selects cn/r; this indicates a bug.\n", fn);
    abort();
}

void v4_compile_code(const V4_Instruction*, int, void*, xmrig::Assembly)
{
    unreachable("v4_compile_code");
}

void v4_compile_code_double(const V4_Instruction*, int, void*, xmrig::Assembly)
{
    unreachable("v4_compile_code_double");
}

void v4_soft_aes_compile_code(const V4_Instruction*, int, void*, xmrig::Assembly)
{
    unreachable("v4_soft_aes_compile_code");
}
