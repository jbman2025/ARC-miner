# IGC bug: AOT gen-compile fails to lower `joint_matrix_apply` element access on sg16 int32 accumulator

**File at:** https://github.com/intel/intel-graphics-compiler/issues (cross-ref https://github.com/intel/compute-runtime)

## Summary
Ahead-of-time (ocloc / IGC) compilation of a SYCL kernel that reads the elements of a
**sub-group-16** `int32` accumulator `joint_matrix` via `joint_matrix_apply` fails during
the SPIR-V → gen backend step. The same source **compiles fine as JIT** and **compiles fine
AOT on Windows** (oneAPI 2026.0, which bundles a newer IGC). Only the sg8 variant of the
same kernel compiles on Linux; the 16-wide accumulator is rejected.

## Error
```
error: __spirv_AccessChain call 1st argument must be pointer to target extension type
error: in function '__spirv_AccessChain(__spirv_CooperativeMatrixKHR__uint_3_8_16_2 AS1* AS4*, long)'
       called by kernel 'pk::KTgemmPow<1, 1, 16, 256>':
       undefined reference to `_Z19__spirv_AccessChainPU3AS4PU3AS143__spirv_CooperativeMatrixKHR__uint_3_8_16_2l'
error: backend compiler failed build.
Build failed with error code: -11
Command was: /usr/bin/ocloc ... -spirv_input -device bmg_g31
icpx: error: gen compiler command failed with exit code 245
```
`__spirv_CooperativeMatrixKHR__uint_3_8_16_2` = use::accumulator, rows 8, cols 16 (scope=subgroup).

## Environment (reproduces)
- icpx: Intel oneAPI DPC++/C++ Compiler **2026.0.0** and **2026.1.0** (both fail — not an icpx-version issue)
- ocloc / IGC: **intel-ocloc 26.05.37020.3 + libigc2 2.28.4** AND the newest public **ocloc 26.22.38646.4 + IGC v2.36.3** (both fail)
- Target device: `intel_gpu_bmg_g31` (Arc B580, Xe2/Battlemage). OS: Debian 14 (forky), kernel 7.0.13.
- Does NOT reproduce: JIT (`-fsycl` with no `-fsycl-targets`); AOT on Windows oneAPI 2026.0 (bundled IGC).

## Trigger
- Fails: `-fsycl -fsycl-targets=intel_gpu_bmg_g31 -O3` on the full kernel (`KTgemmPow<*,16,256>` and `<*,16,128>`).
- The isolated construct (single 8x16 accumulator + one `joint_matrix_apply`) compiles at -O2/-O3;
  the failure needs the fuller kernel context (array of accumulators folded in nested loops with
  `#pragma unroll` R-blocks), pointing to an optimizer interaction, not the bare API.
- Kernel source: `native/pearl-gemm/csrc/sycl/pearl_kernels.hpp`, `launch_tgemm_pow_templated`,
  the transcript-fold loop calling `joint_matrix_apply(sg, mC[...], [&](int32_t v){ part ^= (uint32_t)v; })`.

## Tried (no effect)
- `-Xspirv-translator --spirv-ext=+SPV_INTEL_joint_matrix` (still emits KHR AccessChain → same failure)
- `-Xspirv-translator --spirv-ext=-SPV_KHR_cooperative_matrix` (llvm-spirv fails, exit 18)
- Newer ocloc/IGC (2.36.3) — same failure.

## Best repro artifact
`icpx ... -save-temps=obj` keeps the per-kernel `*-bmg_g31-*.spv`; feed the one containing
`KTgemmPow<*,16,256>` straight to `ocloc -file X.spv -spirv_input -device bmg_g31` to reproduce
with ocloc alone (no icpx). The JIT path compiling the identical kernel proves the SPIR-V is valid
and the defect is in the AOT gen backend's handling of AccessChain into a CooperativeMatrixKHR
target-extension type.

## Impact / workaround
Blocks all AOT builds of the miner on stock Linux Intel GPU stacks (JIT still works, ~5% slower).

### Workaround: IMPLEMENTED and build-verified 2026-07-29 — `PEARL_XMX_FOLD_VIA_MEM`
`joint_matrix_store` the accumulators to SLM and XOR-reduce from memory instead of reading
elements via `joint_matrix_apply`. The store is a plain distributed block write and emits no
`__spirv_AccessChain`, so it never reaches the broken lowering path.

Both paths XOR the **same multiset** of int32 partials — every element of all `2*NHALF`
fragments exactly once — and XOR is commutative + associative, so the folded transcript is
**bit-identical** and shares are unaffected. In the SLM path lane `lid` takes column `lid` of
each fragment (TM ints per lane, TN lanes = the full TM×TN tile) before the existing
`reduce_over_group` XOR.

Code: `native/pearl-gemm/csrc/sycl/pearl_kernels.hpp`, the fold loop in
`launch_tgemm_pow_templated`, under `#if defined(PEARL_XMX_FOLD_VIA_MEM)`.
Build knobs: `FOLD_VIA_MEM=1` (Makefile / build.sh), `-FoldViaMem` (build.ps1).

**A/B measured on the reporting environment** (WSL Ubuntu 26.04, icpx 2026.1.0, ocloc
26.22.38646.4, `-fsycl-targets=intel_gpu_bmg_g31 -DPEARL_XMX_ONLY_SG16 -O3`):

| build | result |
|---|---|
| register fold (control) | **FAILS** — reproduces the AccessChain error above verbatim, no `.so` |
| `-DPEARL_XMX_FOLD_VIA_MEM` | **exit 0**, 2.03 MB `.so` |

Also verified in the real `out-linux` shipping config — fat multi-arch AOT over all five dies
(`acm_g10,acm_g11,acm_g12,bmg_g21,bmg_g31` + `-DPEARL_FAT_AOT`), which gen-compiles the sg8/ACM
images too: control fails identically, fold path builds clean (9.17 MB, ~79 s, all five die
images present). Windows JIT and Windows `bmg_g31` AOT both still compile with the macro on, and
the macro-off register path is byte-for-byte the historical code.

### Verdict: MEASURED 28% SLOWER — do not enable (2026-07-30)
Measured on the 2x Arc B580 rig, fat AOT + SLM fold vs plain JIT:

| build | throughput |
|---|---|
| JIT (register fold, `joint_matrix_apply`) | **36 TH/s** |
| fat AOT (`PEARL_XMX_FOLD_VIA_MEM`) | **26 TH/s** |

Identical on BOTH `level_zero` and `opencl`, so it is the kernel, not the adapter. That is
**-28%**, against an AOT gain of only ~5% — the SLM round-trip costs roughly five times what
AOT buys. The workaround compiles and is bit-correct, but it is a large net loss.

**Linux therefore stays JIT.** `FOLD_VIA_MEM` remains in the tree as the only known way to
produce a Linux AOT build at all — useful if IGC is ever fixed, or for isolating the fold's cost
via `AKOYA_TGEMM_PROBE_NOFOLD=1` — but do not ship it. Don't re-litigate this without a reason to
think the barrier cost changed.

### The real cost is SLM OCCUPANCY, not barriers (2026-07-30, measured)
Measured on the rig, OpenCL, NB=4 MB=2, same IGC/runtime/binary otherwise:

| build | `foldSlm` per work-group | throughput |
|---|---|---|
| JIT, register fold | 0 (only the 512 B `trSlm`) | **37.5** |
| AOT, per-tile fold | 1 KiB | ~26 |
| AOT, whole-R-block fold | 8 KiB | **12.3** |

Each work-group is ONE sub-group — 16 work-items, a single hardware thread — so
every thread reserves the full `foldSlm`. At 8 KiB only ~7 work-groups stay
resident per subslice instead of dozens, and latency hiding collapses. The
allocation scales with `RM*RN`, which is why the config ordering INVERTS: under
JIT, NB=4/MB=2 is fastest; with an 8 KiB fold it is the SLOWEST (12.3) while
NB=2/MB=1 (2 KiB) degrades least. Same reason NEO's dispatch encoder aborts on
the Level Zero path — too much SLM for this dispatch shape.

**Rule for anyone touching this: minimise `foldSlm`, do not minimise barriers.**

### The barrier-count theory was TESTED and is WRONG (2026-07-30)
The obvious suspect was sub-group barriers: the per-tile fold pays `2*RM*RN` of them per R-block
(16 at RM=2/RN=4, ~256 per launch at k=4096/R=256), and each one drains the deliberately
software-pipelined DPAS k-loop. So the fold was restructured to stage every fragment of an
R-block into a larger SLM buffer and fold after **one** barrier pair — a clean 8x reduction,
16 barriers per R-block down to 2.

**Result: it made things TWICE AS BAD — 26 -> 12.3 TMADs/s.** (An earlier revision of this
doc claimed "zero improvement"; that was inferred from an ambiguous field report before the
build was measured directly, and was wrong.) Cutting barriers 8x while growing SLM 8x produced
a 2x regression, which is the cleanest possible evidence that **barriers are not the cost and
SLM occupancy is** — see the table above.

**Worse, it broke Level Zero.** The bigger SLM request made NEO abort in the dispatch encoder:

```
Abort was called at 655 line in file:
../../neo/shared/source/command_container/command_encoder_xehp_and_later.inl
```

OpenCL still ran. A single-sub-group work-group (`nd_range {1,SG}`) asking for 8-16 KiB of SLM,
alongside the `grf_size<256>` request on the RM>=2 kernels, trips an unrecoverable check on the
L0 path. **Keep `foldSlm` at the small per-tile size** (`2*NHALF*TM*TN`, 1-2 KiB) — the change
was reverted for this reason and the size limit is noted in the code.

Net: the fold costs ~28% no matter how it is arranged, and the ceiling for fixing it is the ~5%
AOT gain. Don't spend more time here.

The upstream IGC bug is still the real blocker; this section documents a workaround that works
but doesn't pay.
