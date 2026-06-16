# SYCL / Intel Arc build.
#
#   make                           # JIT (works on any Intel GPU at runtime)
#   make SYCL_TARGETS=spir64_gen   # AOT — compiles SPIR-V ahead of time
#   make ARCH=intel_gpu_acm_g10    # AOT for Arc A770/A750 (ACM-G10)
#   make ARCH=intel_gpu_acm_g11    # AOT for Arc A580/A380 (ACM-G11)
#   make ARCH=intel_gpu_dg2_g10    # alternate ACM name used by older toolchains
#
# Produces:
#   libpearl_gemm_capi.so  — SYCL backend for the pearl_capi_* C ABI
#   libcuda.so.1           — CUDA Driver API → SYCL shim

ICPX         ?= icpx
ARCH         ?= intel_gpu_acm_g10
SYCL_TARGETS ?=         # empty = JIT; set to spir64_gen for AOT

SRC   := $(abspath .)
ROOT  := $(abspath ../../..)/csrc

# AOT flags (applied only when SYCL_TARGETS is non-empty)
ifneq ($(SYCL_TARGETS),)
  AOT_FLAGS := -fsycl-targets=$(SYCL_TARGETS) \
               -Xsycl-target-backend=$(SYCL_TARGETS) "-device $(ARCH)"
else
  AOT_FLAGS :=
endif

# per_kernel split is REQUIRED: both XMX sub-group variants (Xe-HPG sg8,
# Xe2 sg16) live in one binary with runtime dispatch; an unsplit module
# fails to JIT on whichever generation the other variant targets.
COMMON_FLAGS := -fsycl -fsycl-device-code-split=per_kernel $(AOT_FLAGS) -O3 -fPIC -I $(ROOT) -I ..

all: libpearl_gemm_capi.so libcuda.so.1

libpearl_gemm_capi.so: pearl_gemm_capi_sycl.cpp pearl_kernels.hpp blake3_device.hpp \
                        ../rocm/blake3_rounds.inc $(ROOT)/capi/pearl_gemm_capi.h
	$(ICPX) $(COMMON_FLAGS) -shared $< -o $@

libcuda.so.1: cuda_sycl_shim.cpp
	$(ICPX) -fsycl $(AOT_FLAGS) -O2 -fPIC -shared $< -o $@

clean:
	rm -f libpearl_gemm_capi.so libcuda.so.1 *.o

.PHONY: all clean
