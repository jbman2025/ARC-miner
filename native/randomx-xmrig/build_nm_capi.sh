#!/usr/bin/env bash
# Linux build of libneuromorph_capi.so — C ABI over NeuroMorph (nm/1, Cereblix).
# Companion to build_nm_capi.bat (MSVC).
#
# crypto/nm/* is vendored from the xmrig-cereblix fork (GPLv3). On GCC/Clang the
# two "ARC PATCH" blocks in nm_neuromorph.c / nm_aes.h compile out entirely, so
# this build sees the upstream source exactly as upstream builds it.
#
# The flags match the fork's cmake/nm.cmake: NeuroMorph is consensus-bound to
# plain IEEE-754 float64 with no fused operations, so -fno-fast-math and
# -ffp-contract=off are mandatory, not optional. -maes enables the AES-NI path.
#
# As in build_gr_capi.sh, C sources build with gcc and C++ with g++, then link.
set -euo pipefail
cd "$(dirname "$0")"

CXX="${CXX:-g++}"
CC="${CC:-gcc}"
ARCH="-maes -mssse3 -msse4.1"
FP="-fno-fast-math -ffp-contract=off"
DEF="-DNDEBUG -D_GNU_SOURCE"
INC="-I."
CXXFLAGS="-O3 -std=c++17 -fPIC -pthread $ARCH $FP $DEF $INC"
CFLAGS="-O3 -std=c11 -fPIC $ARCH $FP $DEF $INC"

echo "Building libneuromorph_capi.so with $($CXX --version | head -1)..."
rm -f ./nm_*.o

$CXX $CXXFLAGS -c neuromorph_capi.cpp        -o nm_capi.o
# The shim's huge-page allocator calls xmrig::VirtualMemory. This TU was missing
# here (the Windows .bat always had it), so the .so linked without complaint —
# a shared object may leave symbols undefined — and then failed at dlopen with
# "undefined symbol: _ZN5xmrig13VirtualMemory20freeLargePagesMemoryEPvm",
# i.e. --algo nm was completely dead on Linux. VirtualMemory.cpp has a full
# POSIX branch (mmap/MAP_HUGETLB), so it just needed compiling.
$CXX $CXXFLAGS -c crypto/common/VirtualMemory.cpp -o nm_vm.o
$CC  $CFLAGS   -c crypto/nm/nm_neuromorph.c  -o nm_neuromorph.o
$CC  $CFLAGS   -c crypto/nm/nm_params.c      -o nm_params.o

# -Wl,--no-undefined so a missing symbol fails the BUILD instead of surfacing as
# a dlopen error at runtime on the rig.
$CXX -shared -pthread -Wl,--no-undefined \
     nm_capi.o nm_vm.o nm_neuromorph.o nm_params.o -o libneuromorph_capi.so
rm -f ./nm_capi.o ./nm_vm.o ./nm_neuromorph.o ./nm_params.o
echo "BUILD OK: $(pwd)/libneuromorph_capi.so"
