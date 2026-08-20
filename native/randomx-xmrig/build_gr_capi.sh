#!/usr/bin/env bash
# Linux build of libghostrider_capi.so — C ABI over XMRig's GhostRider (Raptoreum).
# Companion to build_gr_capi.bat (MSVC). Compiles XMRig's own
# crypto/ghostrider/ghostrider.cpp rather than reimplementing the hash loop.
#
# Built WITHOUT XMRIG_FEATURE_HWLOC, which selects ghostrider.cpp's simple 8-lane
# hash_octa (no helper threads, no hwloc/libuv); the stubs in compat/ and
# base/io/log/ satisfy its unconditional includes so the vendored source needs no
# edits.
#
# Built WITHOUT XMRIG_FEATURE_ASM — and, contrary to what this file used to say,
# that costs GhostRider NOTHING. Measured 2026-07-29, and worth recording so the
# "port the GAS asm for a speed-up" idea isn't attempted again:
#
#   Upstream CnHash.cpp registers the six GhostRider variants with ADD_FN only:
#       ADD_FN(Algorithm::CN_GR_0) ... ADD_FN(Algorithm::CN_GR_5)
#   ADD_FN_ASM — the macro that installs cryptonight_*_hash_asm into the
#   dispatch table — is called for CN_2, CN_HALF, CN_R, CN_RWZ, CN_ZLS,
#   CN_DOUBLE, CN_PICO_*, CN_UPX2 and NEVER for CN_GR_*. GhostRider reaches
#   CryptoNight only through CnHash::fn(), which therefore falls back to
#   data[av][Assembly::NONE], the portable path, on WINDOWS TOO.
#
#   patchAsmVariants() does patch cn_gr{0..5}_*_mainloop_asm at startup, and
#   cryptonight_single_hash_asm<CN_GR_x> does contain the call sites — but
#   nothing ever hands those functions to the dispatcher, so that code is dead
#   in this XMRig version.
#
#   Confirmed empirically: vendoring the SysV cn_main_loop.S and building with
#   -DXMRIG_FEATURE_ASM produced 145.4 H/s vs 146.3 H/s for the portable build
#   (1 thread, mean over 6 trios) — i.e. no change beyond noise. The asm sources
#   were removed again rather than left as dead weight; enabling the flag would
#   also make patchAsmVariants() allocate and rewrite executable memory at
#   static-init time for no benefit, which is a needless hazard on hardened
#   kernels (W^X / SELinux).
#
# So Linux and Windows run the SAME CryptoNight code for gr. If a future XMRig
# adds ADD_FN_ASM(CN_GR_*), revisit: the port itself is easy (asm/cn_main_loop.S
# upstream is a thin SysV wrapper — mov rdi->rcx — around the same cn1/cn2 .inc
# bodies the win64 MASM uses).
#
# The lone VAES translation unit needs -mvaes -mavx2 and is never executed at
# runtime (cn_vaes_enabled stays false).
#
# As in build_capi.sh, C sources build with gcc and C++ with g++ (g++ miscompiles
# the sph/argon2 C), then link together.
set -euo pipefail
cd "$(dirname "$0")"

CXX="${CXX:-g++}"
CC="${CC:-gcc}"
ARCH="-maes -mssse3 -msse4.1"
# HAVE_ROTR: GCC 14+ (ia32intrin.h) defines _rotr as a macro expanding to
# __rord, so soft_aes.h's own `static inline uint32_t _rotr(...)` fallback
# becomes a redeclaration of GCC's __rord and the TU dies. XMRig guards that
# fallback with HAVE_ROTR precisely so a toolchain that already provides _rotr
# can opt out. Without it the build fails with 9 errors on GCC 15 — one real
# collision plus eight cascades ("extra_hashes was not declared", etc.) that
# look unrelated and send you hunting in the wrong file.
DEF="-DNDEBUG -DXMRIG_ALGO_GHOSTRIDER -D_GNU_SOURCE -DHAVE_ROTR"
INC="-I. -Icompat -Icrypto/ghostrider"
CXXFLAGS="-O2 -std=c++17 -fPIC -pthread $ARCH $DEF $INC"
CFLAGS="-O2 -std=c11 -fPIC $ARCH $DEF $INC"

SRC_CPP="
  ghostrider_capi.cpp
  compat/cn_r_stubs.cpp
  crypto/ghostrider/ghostrider.cpp
  crypto/cn/CnHash.cpp
  crypto/cn/CnCtx.cpp
  backend/cpu/Cpu.cpp
  crypto/common/VirtualMemory.cpp
  crypto/common/Assembly.cpp
  base/crypto/keccak.cpp
"
SRC_C="
  crypto/ghostrider/sph_blake.c
  crypto/ghostrider/sph_bmw.c
  crypto/ghostrider/sph_groestl.c
  crypto/ghostrider/sph_jh.c
  crypto/ghostrider/sph_keccak.c
  crypto/ghostrider/sph_skein.c
  crypto/ghostrider/sph_luffa.c
  crypto/ghostrider/sph_cubehash.c
  crypto/ghostrider/sph_shavite.c
  crypto/ghostrider/sph_simd.c
  crypto/ghostrider/sph_echo.c
  crypto/ghostrider/sph_hamsi.c
  crypto/ghostrider/sph_fugue.c
  crypto/ghostrider/sph_shabal.c
  crypto/ghostrider/sph_whirlpool.c
  crypto/ghostrider/sph_sha2.c
  crypto/cn/c_groestl.c
  crypto/cn/c_blake256.c
  crypto/cn/c_jh.c
  crypto/cn/c_skein.c
"

echo "Building libghostrider_capi.so with $($CXX --version | head -1)..."
rm -f ./gr_*.o
OBJS=""
for f in $SRC_CPP; do o="gr_$(echo "$f" | tr '/.' '__').o"; $CXX $CXXFLAGS -c "$f" -o "$o"; OBJS="$OBJS $o"; done
for f in $SRC_C;   do o="gr_$(echo "$f" | tr '/.' '__').o"; $CC  $CFLAGS   -c "$f" -o "$o"; OBJS="$OBJS $o"; done
# VAES TU: needs the wide-AES ISA flags to compile (never executed at runtime).
$CXX $CXXFLAGS -mavx2 -mvaes -c crypto/cn/CryptoNight_x86_vaes.cpp -o gr_vaes.o; OBJS="$OBJS gr_vaes.o"

$CXX -shared -pthread $OBJS -o libghostrider_capi.so
rm -f ./gr_*.o
echo "BUILD OK: $(pwd)/libghostrider_capi.so"
