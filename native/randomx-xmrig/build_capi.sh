#!/usr/bin/env bash
# Linux build of librandomx_capi.so (XMRig RandomX backend). Mirror of
# build_capi.bat (MSVC). g++ compiles the GAS .S JIT stub directly (capital-S =
# preprocessed assembly), so no separate assembler step is needed.
#
#   XMRIG_FEATURE_ASM  — REQUIRED, else JitCompiler resolves to the fallback stub.
#   -maes/-mssse3/-msse4.1 — the AES scratchpad fill + SSE4.1 blake2 need these
#     (gated at runtime by Cpu::info()->hasAES(); safe to compile in).
set -euo pipefail
cd "$(dirname "$0")"

# NOTE: g++ compiles .c files as C++ (which breaks argon2's C), so C sources are
# built with gcc and C++ sources with g++, then linked together.
CXX="${CXX:-g++}"
CC="${CC:-gcc}"
ARCH="-maes -mssse3 -msse4.1"
DEF="-DXMRIG_FEATURE_ASM -DNDEBUG -D_GNU_SOURCE"
INC="-I. -I3rdparty/argon2/lib -I3rdparty/argon2/include"
CXXFLAGS="-O2 -std=c++17 -fPIC -pthread $ARCH $DEF $INC"
CFLAGS="-O2 -std=c11 -fPIC $ARCH $DEF $INC"

SRC_CPP="
  randomx_capi.cpp
  capi_support.cpp
  backend/cpu/Cpu.cpp
  crypto/common/VirtualMemory.cpp
  crypto/randomx/aes_hash.cpp
  crypto/randomx/allocator.cpp
  crypto/randomx/blake2_generator.cpp
  crypto/randomx/bytecode_machine.cpp
  crypto/randomx/dataset.cpp
  crypto/randomx/instructions_portable.cpp
  crypto/randomx/jit_compiler_x86.cpp
  crypto/randomx/randomx.cpp
  crypto/randomx/soft_aes.cpp
  crypto/randomx/superscalar.cpp
  crypto/randomx/virtual_machine.cpp
  crypto/randomx/virtual_memory.cpp
  crypto/randomx/vm_compiled.cpp
  crypto/randomx/vm_compiled_light.cpp
  crypto/randomx/vm_interpreted.cpp
  crypto/randomx/vm_interpreted_light.cpp
"
SRC_C="
  crypto/randomx/reciprocal.c
  crypto/randomx/blake2/blake2b.c
  crypto/randomx/blake2/blake2b_sse41.c
  3rdparty/argon2/lib/argon2.c
  3rdparty/argon2/lib/core.c
  3rdparty/argon2/lib/encoding.c
  3rdparty/argon2/lib/genkat.c
  3rdparty/argon2/lib/impl-select.c
  3rdparty/argon2/lib/blake2/blake2.c
  3rdparty/argon2/arch/generic/lib/argon2-arch.c
"
ASM="crypto/randomx/jit_compiler_x86_static.S"

echo "Building librandomx_capi.so with $($CXX --version | head -1)..."
rm -f ./*.o
OBJS=""
for f in $SRC_CPP; do o="$(echo "$f" | tr '/.' '__').o"; $CXX $CXXFLAGS -c "$f" -o "$o"; OBJS="$OBJS $o"; done
for f in $SRC_C;   do o="$(echo "$f" | tr '/.' '__').o"; $CC  $CFLAGS   -c "$f" -o "$o"; OBJS="$OBJS $o"; done
# .S JIT stub (gcc handles the C-preprocessor #includes; capital-S = preprocessed asm)
$CC $CFLAGS -c "$ASM" -o jit_static.o; OBJS="$OBJS jit_static.o"

$CXX -shared -pthread $OBJS -o librandomx_capi.so
rm -f ./*.o
echo "BUILD OK: $(pwd)/librandomx_capi.so"
