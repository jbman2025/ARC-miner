# SYCL / Intel Arc build.
#
#   make                           # JIT (works on any Intel GPU at runtime)
#   make SYCL_TARGETS=spir64_gen   # AOT — compiles SPIR-V ahead of time
#   make ARCH=intel_gpu_acm_g10    # AOT for Arc A770/A750 (ACM-G10)
#   make ARCH=intel_gpu_acm_g11    # AOT for Arc A580/A380 (ACM-G11)
#   make ARCH=intel_gpu_dg2_g10    # alternate ACM name used by older toolchains
#   make ARCH=fat FOLD_VIA_MEM=1   # fat AOT with the SLM transcript fold (needed
#                                  # for AOT on Linux — see FOLD_VIA_MEM below)
#
# Produces:
#   libpearl_gemm_capi.so  — SYCL backend for the pearl_capi_* C ABI
#   libcuda.so.1           — CUDA Driver API → SYCL shim

ICPX         ?= icpx
ARCH         ?= intel_gpu_acm_g10
SYCL_TARGETS ?=         # empty = JIT; set to spir64_gen for AOT

SRC   := $(abspath .)
# csrc root = the parent of this sycl/ dir. (Was `$(abspath ../../..)/csrc`,
# which resolved to native/csrc — wrong for the native/pearl-gemm/csrc layout;
# the SYCL build.sh path on Linux had never been exercised. 2026-06-27.)
ROOT  := $(abspath ..)

# AOT flags. ARCH=fat builds ONE fat multi-arch binary carrying BOTH
# generations' AOT-optimized kernels (Xe-HPG sg8 + Xe2 sg16) — no JIT perf hit,
# no per-die builds. -DPEARL_FAT_AOT switches the XMX kernel bodies to the
# if_architecture_is guard (pk::xmx_arch_guard) so the foreign generation's DPAS
# body is elided per target IMAGE, dodging the gen-compiler abort that a naive
# multi-target build would hit; is_xe_hpg() picks the matching kernel to LAUNCH
# at runtime. Override the target list with FAT_TARGETS to add more dies.
# One AOT image PER DIE — the runtime needs an EXACT arch match to resolve
# kernels in a multi-image binary (a bmg_g31-only image does NOT serve a B580
# = bmg_g21). acm_g10=A770/A750, acm_g11=A580/A380, acm_g12=DG2-G12 (mobile ACM),
# bmg_g21=B580/B570, bmg_g31=B770. Keep this list in sync with $fatDies in build.ps1.
FAT_TARGETS ?= intel_gpu_acm_g10,intel_gpu_acm_g11,intel_gpu_acm_g12,intel_gpu_bmg_g21,intel_gpu_bmg_g31
ifeq ($(ARCH),fat)
  AOT_FLAGS  := -fsycl-targets=$(FAT_TARGETS)
  FAT_DEFINE := -DPEARL_FAT_AOT
else ifneq ($(SYCL_TARGETS),)
  AOT_FLAGS  := -fsycl-targets=$(SYCL_TARGETS) \
               -Xsycl-target-backend=$(SYCL_TARGETS) "-device $(ARCH)"
  FAT_DEFINE :=
else
  AOT_FLAGS  :=
  FAT_DEFINE :=
endif

# FOLD_VIA_MEM=1 routes the per-R-block transcript XOR fold through SLM
# (joint_matrix_store + sub-group barrier) instead of joint_matrix_apply element
# access. This is the workaround for the IGC AOT lowering bug documented in
# docs/IGC-BUG-coop-matrix-aot.md, which otherwise blocks AOT on Linux and pins
# us to JIT (~5% slower). The folded transcript is BIT-IDENTICAL either way (same
# multiset of int32 partials, XOR is commutative), so shares are unaffected — but
# the SLM round-trip is not free, so A/B it against the JIT register path before
# adopting: if the fold costs more than the ~5% AOT gain it is a net loss.
FOLD_DEFINE := $(if $(filter 1 yes on,$(FOLD_VIA_MEM)),-DPEARL_XMX_FOLD_VIA_MEM,)

# per_kernel split is REQUIRED: both XMX sub-group variants (Xe-HPG sg8,
# Xe2 sg16) live in one binary with runtime dispatch; an unsplit module
# fails to JIT on whichever generation the other variant targets.
COMMON_FLAGS := -fsycl -fsycl-device-code-split=per_kernel $(AOT_FLAGS) $(FAT_DEFINE) $(FOLD_DEFINE) -O3 -fPIC -I $(ROOT) -I ..

all: libpearl_gemm_capi.so libcuda.so.1

libpearl_gemm_capi.so: pearl_gemm_capi_sycl.cpp pearl_kernels.hpp blake3_device.hpp \
                        blake3_rounds.inc $(ROOT)/capi/pearl_gemm_capi.h
	$(ICPX) $(COMMON_FLAGS) -shared $< -o $@

libcuda.so.1: cuda_sycl_shim.cpp
	$(ICPX) -fsycl $(AOT_FLAGS) -O2 -fPIC -shared $< -o $@

clean:
	rm -f libpearl_gemm_capi.so libcuda.so.1 *.o

.PHONY: all clean
