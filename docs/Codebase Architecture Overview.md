# ARC Source Gold — Architecture & Optimization Report

## 1. Codebase Architecture Overview

### Compute Engine (pearl_kernels.hpp)
* **PoW Algorithm**: Signed 8-bit integer GEMM ( = A' \cdot B'^T = (A + E_A) (B + E_B)^T$), XOR-folded per $-block into a 16-word transcript and hashed with BLAKE3.
* **XMX Hardware Acceleration**: Leverages SYCL ext::oneapi::experimental::matrix (joint_matrix) to execute hardware DPAS operations on Intel Xe matrix engines.
* **Architecture-Specific Generation Paths**:
  * **Xe-HPG** (Alchemist, A-series: A770, A750, A580, A380, A310): Uses Sub-group 8 (sg8, tile N=8).
  * **Xe2** (Battlemage, B-series: B580, B570, B70): Uses Sub-group 16 (sg16, tile N=16).

### Host-Device Coordination (GpuWorker.cs & pearl_gemm_capi_sycl.cpp)
* **Adaptive Host Sleep**: Uses PreSyncSleep heuristic timing to minimize host CPU utilization to ~0.3% of one core without delaying kernel completions.
* **Fused Tree-Hashing**: Fuses LCG pseudo-random initialization with BLAKE3 tree-hashing in global device memory (parallel_tensor_hash_fused), eliminating redundant DRAM round-trips for matrix A.

---

## 2. Completed Optimizations & Improvements

### A. Native SYCL BLAKE3 Acceleration (blake3_device.hpp)
* **Direct Word-Level Block Hashing (b3::hash_block, b3::hash_block_u32)**:
  * Added single-block 16-word direct hashing functions without stack-buffer allocation or scalar copy loops.
  * Fast-path single-block loading in hash_small and hash_small_u32 for 64-byte 4-byte-aligned inputs, avoiding 64-iteration byte loops.

### B. SYCL Kernel Zero-Copy Input Staging (pearl_kernels.hpp)
* **launch_commitment_hash**: Direct 16-word u32 msg[16] memory packing replacing byte-by-byte copies into intermediate stack buffers.
* **launch_noise_gen (KUniformA, KUniformB, KPerm)**: Eliminated intermediate stack buffers and scalar bounds-checked copy loops; loads 16 words directly into registers for BLAKE3 block processing.
* **tgemm_pow Sub-group Leader PoW Validation**: Removed alignas(4) uint8_t tb[64] stack buffer allocation and the 64-iteration byte loop; loads directly from sub-group local memory (trSlm) into u32 bl[16] registers and invokes b3::hash_block_u32.

### C. Device Memory Management & USM Pre-Pooling (pearl_gemm_capi_sycl.cpp)
* **Persistent Scratch Buffers in SyclWorkspace**:
  * Added pre-allocated resident scratch buffers (nb_EBRt, nb_Bkn, nb_Bnoi, nb_EB) sized for standard chunk capacity (cn = 16,384) in pearl_capi_workspace_alloc and deallocated in pearl_capi_workspace_free.
  * pearl_capi_noise_B reuses pre-allocated resident workspace scratch buffers across sigma job rotations, avoiding 4 runtime USM device allocations/frees and preventing contention under g_usm_heavy_mutex.

### D. Multi-Dimensional Autotuning Engine (Autotune.cs)
* **SEARCH_N Tuning & Multi-Parameter Sweeps**:
  * Added SearchN parameter tracking to Config, SkuDefaults, and ResolveTunedConfig.
  * Included environment variable management (ARC_SEARCH_N) across sweep runs, ensuring non-destructive backup and restoration.
  * Enhanced TuneCache format to version 2 (v2 | sku|nb|mb|search_m|search_n|tmads|version|utc) with backward compatibility for legacy caches.
  * Updated WorkerOrchestrator.ApplyTunedProfile to apply tuned ARC_SEARCH_N parameters when non-default.

### E. Code Quality & Roslyn Warning Elimination
* **Program.cs**:
  * Fixed CA1805 by removing redundant explicit field default initialization on _dependenciesPreloaded.
  * Handled IL3000 single-file publish analysis warning on assembly.Location with pragma suppression and comments.
* **PoolTls.cs**:
  * Wrapped the self-signed certificate validation callback with #pragma warning disable CA5359 and added explanatory documentation regarding mining pool TLS certificate requirements.
* **Build Verification**: Clean build with 0 Warning(s) and 0 Error(s), and 487 / 487 unit tests passing cleanly under .NET 10.
