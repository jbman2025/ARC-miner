# ARC-miner slim plan — Intel-only + structural deduplication

Two goals, one sequence:

1. **Drop the CUDA/ROCm backends.** The product ships SYCL/oneAPI only; the
   shipped Linux bundle contains no NVIDIA or AMD runtime at all. ~18.3k lines
   of native code are compiled for nobody.
2. **Remove the structural duplication that makes each new algo expensive.**
   The registry in `new coin.md` proposes 30 algos against today's 6. At the
   current per-algo cost that is ~24 × 450 lines of hand-rolled pool protocol.

Phases are ordered by risk, lowest first. Each has an explicit verification
gate. Phases 1–2 are independent; Phase 3 is the prerequisite for the coin
expansion; Phases 4–5 are hygiene that can slip.

**Baseline:** 26,080 lines C# (`src/`), 93 removable native files, 222 tests.

---

## Phase 0 — Safety net ✅ DONE 2026-08-03

Prerequisite for Phase 3 only. Skip if Phases 1–2 are all that get done.

**Outcome:** `CsdGoldenVectorTests.cs` + `BtxGoldenVectorTests.cs`, **292 tests
(was 239, +53)**. csd and btx went from zero coverage to covering the whole
host-side pipeline.

No live pool capture was needed — and self-generated expectations would have
been worthless, since a characterization test written from the code under test
only pins whatever that code does today. Instead **every golden value was
cross-checked against an independent Python implementation** (hashlib plus a
from-FIPS-180-4 SHA-256 compressor, itself validated against hashlib) before
being written down:

| Value | Independent anchor |
|---|---|
| midstate of 64 zero bytes | from-spec compressor, validated against hashlib |
| csd coinbase → merkle → 84-byte header → midstate/tail | recomputed end-to-end in Python |
| `CompactToTarget(0x1d00ffff)` | Bitcoin's published difficulty-1 target |
| compact mantissa placement | integer arithmetic from the consensus rule |
| `ShlSaturating` | Python bigint shift + saturation bound |
| merkle roots (1–9 txids) | naive from-spec merkle, in the test itself |
| pdiff targets | closed form 0xFFFF0000/diff |

**The net was then mutation-tested.** Three deliberate bugs — ntime shifted one
byte in the csd header, merkle odd-level duplicate replaced with a zero node,
compact mantissa byte order reversed — produced **10 failures across both
files**. A safety net that cannot detect a break is not a safety net; this one
detects these.

**One accessibility change, no logic change:** `CsdStratumClient.Job` and
`.Rebuild` went `private` → `internal` (the test assembly already has
`InternalsVisibleTo`).

**Known gaps Phase 3 must watch** — the submit-path nonce byte flip and the
share-attribution FIFO live inline in `SolverLoop`/`SubmitAsync` and cannot be
reached without refactoring the code under test. The nonce rule is documented
at the top of `CsdGoldenVectorTests.cs`: the kernel hashes big-endian(w), the
submit hex is the little-endian spelling of the same w.

### Why this phase existed

Phase 3 rewrites `Csd` and `Btx`, and before this phase **neither had a single
test**. The suite covered gr, nm, rx-nonce-layout, config and the shared
transport — nothing touched `CsdHash`, `CsdStratumClient`, `BtxJob`, or
`BtxPoolClient`. Refactoring untested share-building code is how you ship a
reject rate that surfaces hours later on a pool dashboard rather than in a
build log.

### What is covered now

- **csd**: `Rebuild` end to end (coinbase → merkle fold → 84-byte header →
  midstate + tail), extranonce2 width clamping at 2/3/4 bytes, midstate
  determinism and input-sensitivity, `Be32`, `PdiffTarget` closed form plus
  its non-positive-difficulty guard and monotonicity.
- **btx**: `CompactToTarget` across both the `exponent <= 3` and `exponent > 3`
  branches plus all four invalid-nBits rejections, `ShlSaturating` including
  the saturate-don't-wrap consensus rule and the no-aliasing-on-zero-shift
  contract, `MerkleRoot` for 1–9 txids, display/internal byte order and its
  no-mutation contract, `Le256`, and bech32m address rejection paths.

**Gate: 292/292 pass, and the mutation test above confirms the net bites.**

---

## Phase 1 — Remove CUDA/ROCm ✅ DONE 2026-08-03

Mechanical, high confidence, largest single win. Independent of every other
phase.

**Outcome:** 102 files / 18,155 source lines removed from `csrc/` (117 → 15
files), `build.ps1` 581 → 464, `build.sh` 293 → 218, `GpuWorker.cs` 2,703 →
2,655, `WorkerBuffers.cs` 300 → 287, `ResidentBStateBuffers.cs` 151 → 134,
CUTLASS submodule and `.gitmodules` gone.

All four gates passed: `dotnet build` clean, 222/222 tests, full WSL Linux
build producing artifacts byte-identical in size and export list
(867/49/59/71 symbols) to `out-linux-good`, and **confirmed working on the
BLUE rig**.

Two corrections to what this plan originally said:
- `Find-VsInstall` / `Import-VcVars` had to STAY — the SYCL path uses them for
  the Native AOT linker. Only `Detect-Arch` and `Resolve-Tool` were CUDA-only.
- `SYCL_BACKEND` was NOT defined unconditionally; it was gated on
  `-p:AkoyaBackend=sycl`, which `build.ps1` defaulted to `cuda`. Three further
  guards lived **indented** in `WorkerBuffers.cs` and were missed by the
  original `^#if` survey — they gated real VRAM decisions.

### 1a. Break the ROCm dependency first

`sycl/blake3_device.hpp:27` does `#include "../rocm/blake3_rounds.inc"`. The
SYCL backend reaches into the ROCm tree for BLAKE3 round constants.

- Move `csrc/rocm/blake3_rounds.inc` → `csrc/sycl/blake3_rounds.inc`
- Fix the include to `"blake3_rounds.inc"`
- Drop the `../rocm/blake3_rounds.inc` prerequisite from `csrc/sycl/Makefile:67`

Nothing else builds until this lands.

### 1b. Delete — 18,289 lines / 93 files

Under `native/pearl-gemm/csrc/`:

| Path | Lines | Files |
|---|---:|---:|
| `gemm/` | 6,834 | 41 |
| `tensor_hash/` | 3,094 | 13 |
| `capi/` **minus `pearl_gemm_capi.h`** | 2,708 | 10 |
| `portable/` | 2,292 | 10 |
| `rocm/` **minus `blake3_rounds.inc`** (moved in 1a) | 1,696 | 13 |
| `blackwell/` | 721 | 2 |
| `consumer/` | 702 | 1 |
| `blake3/` | 242 | 3 |

Also:
- CUTLASS submodule — remove from `.gitmodules` and `native/pearl-gemm/third_party/`
- Stale checked-in binaries in `csrc/sycl/`: `pearl_gemm_capi_{dbg,new,v2,v3}.{lib,exp}` (dated Jun 11), `fused_check.exe`

### 1c. What survives — the complete SYCL closure

Verified closed: `pearl_gemm_capi.h` and `blake3_rounds.inc` include only
system headers; nothing under `sycl/` references any deleted directory.

```
sycl/pearl_gemm_capi_sycl.cpp     618
sycl/pearl_kernels.hpp          1,009
sycl/blake3_device.hpp            131
sycl/cuda_sycl_shim.cpp           395
sycl/fused_check.cpp              113   (dev tool)
sycl/blake3_rounds.inc            105   (moved from rocm/)
capi/pearl_gemm_capi.h            379
                                -----
                                2,750
```

### 1d. DO NOT DELETE `src/Akoya.Cuda`

The single most important line in this plan. `Akoya.Cuda/CudaDriver.cs` (160
lines, 34 `[LibraryImport]` entry points) is **load-bearing on Intel**.
`cuda_sycl_shim.cpp` builds `cuda.dll` / `libcuda.so.1` — a drop-in that
implements the CUDA Driver API *on top of SYCL*: `CUstream`→`sycl::queue*`,
`CUevent`→`sycl::event*`, device memory→USM. Every Arc GPU call in
`GpuWorker` goes through it.

Add a header comment to `CudaDriver.cs` stating it targets the SYCL shim and
must not be removed, so this does not get rediscovered the hard way.

### 1e. Build scripts

- `build.ps1` (581 lines): delete `Find-VsInstall`, `Import-VcVars`,
  `Detect-Arch`, `Resolve-Tool`, the cmake/ninja preflight (169–181), the
  `$Backend -eq 'cuda'` build block (233–280). Drop the `-Backend` and `-Arch`
  params; SYCL becomes unconditional. ~150–180 lines.
- `build.sh`: delete `pick_nvcc`, `nvcc_major`, the `cuda` and `rocm` branches.
  Drop `BACKEND` (currently defaults to `cuda`).
- `build-linux-wsl.sh`, `install-deps.sh`: drop CUDA/ROCm prerequisites.

### 1f. Dead conditional compilation

`SYCL_BACKEND` is defined unconditionally in `Akoya.Miner.csproj:24`.

- Delete the unreachable `#if !SYCL_BACKEND` block at `GpuWorker.cs:1353` (10 lines)
- Un-guard the seven `#if SYCL_BACKEND` blocks in `GpuWorker.cs` and
  `ResidentBStateBuffers.cs:68`

**Gate:**
1. `make` in `csrc/sycl/` succeeds (WSL).
2. Full `build-linux-wsl.sh` produces `out-linux/` with the same file list as
   `out-linux-good/`.
3. `libpearl_gemm_capi.so` exports match the pre-change build.
4. **Live run on the BLUE rig** (2× B580, on the LAN) — this box has no
   Intel GPU, so correctness cannot be confirmed locally. Accepted shares on
   `prl` before declaring done.

---

## Phase 2 — Consolidate duplicated helpers ✅ DONE 2026-08-03

Small in lines (~200), but this is precisely where the bring-up bugs have
lived. Do it before Phase 3 so the dialect layer has one correct primitive set
to build on.

**Outcome — the line estimate was wrong.** Net **+62 lines** in `src/`, not
−200. Three new files in `Akoya.Crypto` (`Hex.cs`, `Sha2.cs`, `Uint256.cs`,
104 lines total) replaced ~42 lines of duplicated bodies; the rest is comments
documenting the three-way semantic split and the endianness trap. Plus 125
lines of new tests (`SharedPrimitivesTests.cs`, 222 → 239 tests).

The −200 estimate assumed ~15-line copies; most were 5–10-line bodies. **The
value here was never line count** — it was one correct implementation and
names that stop the next algo picking the wrong one. Judge the phase on that.

**Corrections to what this plan said:**
- Home is `Akoya.Crypto`, NOT `Mining/Stratum/`. `Akoya.Miner` → `Akoya.Pool`
  is the dependency direction, so a helper in `Akoya.Miner` is unreachable from
  the three `Akoya.Pool` call sites. `Akoya.Crypto` is referenced by both.
- The five `Unhex` copies had **three different behaviours**, not one:
  csd/gr silently truncated odd-length input (`new byte[s.Length/2]` drops the
  last nibble); `MiningSession` threw (`Convert.FromHexString`); the two
  `StratumJobParser`/`StratumSession` copies left-padded. `Hex.Decode` adopts
  left-padding — the only semantics correct for a hex-encoded *number* — so
  this is a real behaviour change on odd-length input at 3 of 5 sites. Covered
  by new tests.
- Found a **fourth** `sha256d` site the survey missed:
  `Akoya.Pool/StratumJobParser.cs` had two inline
  `SHA256.HashData(SHA256.HashData(...))` calls.
- `BtxJob.Sha256d` was `SHA256.HashData(SHA256.HashData(data.ToArray()))` —
  the `.ToArray()` copied the whole coinbase on every call. Now stackalloc.

**Renames** (`TargetForDifficulty` was two incompatible functions sharing a
name): `CsdHash.PdiffTarget` and `GrHash.Diff256Target`. This required editing
`GrHashTests.cs` — 10 call sites plus one test method name, **pure symbol
rename, zero assertion changes** (verified by diffing against the backup).

**Gate: `dotnet build` 0 errors, 239/239 tests pass.**

| Helper | Copies today |
|---|---|
| `Unhex` | **5** — `CsdHash`, `GrHash`, `Pool/MiningSession`, `Pool/StratumJobParser`, `Pool/StratumSession` |
| `Hex`, `Sha256d` | 2 — byte-identical in `CsdHash` and `GrHash` |
| `MeetsTarget` / `Le256` / target compare | 6 variants across `Btx`, `Gr`, `Nm`, `Rx` |
| `TargetForDifficulty` | 2 **incompatible** implementations under one name |

The `TargetForDifficulty` split is not a cleanup — it is a correctness
question. `CsdHash` uses a 3-line `0xFFFF0000 / diff`; `GrHash` uses a 20-line
loop with a `/65536` prescale and word shifting. Both are "right" for their
pool. Keep both, but **name them for their dialect** (`PdiffTarget`,
`Bdiff256Target`) so the next algo picks deliberately instead of by autocomplete.

**Gate:** `dotnet test` green. `GrHashTests` and `NmHashTests` must pass
unmodified against the relocated helpers — if they need edits, the extraction
changed behavior.

---

## Phase 3 — Stratum dialect layer ✅ DONE 2026-08-03 (all four algos live-verified)

**The family taxonomy in this plan was WRONG.** It assumed one
`ClassicStratumClient` would serve btx + csd + gr. btx shares only the *method
names*: its `mining.notify` is `[job_id, version, prevhash, merkleroot, time,
bits, share_target, clean, matmul_meta]` — the POOL supplies the merkle root, so
there is no coinbase to assemble and no branch to fold — plus a second "ninja"
dialect it falls back to at runtime and a 64-bit pool-assigned nonce window.
Real families:

| Family | Members | Status |
|---|---|---|
| Bitcoin-stratum (coinbase+branch) | **gr ✅**, csd | gr migrated + live-verified |
| CryptoNote (login/job/submit) | rx, nm | not started |
| Bespoke | **btx** | **excluded on purpose** |

btx stays as-is. Folding it in would mean a configuration knob per caller,
which is the failure mode this phase exists to prevent.

### Shipped

`Mining/Stratum/BitcoinStratumJob.cs` + `BitcoinStratumDialect.cs` (295 lines):
handshake, extranonce/difficulty state, mining.notify parsing, coinbase+merkle,
and **id-correlated awaited submit**. `GrStratumClient` 433 → 324 lines.

The submit change is the substantive one. Three clients had three answers to
"whose share did the pool just ack?" — csd a FIFO paired by arrival order, gr a
dict on request id but fire-and-forget, rx an awaited `CallAsync`. The dialect
uses a unique id per submit and awaits the verdict: no FIFO, no pending table,
no ordering invariant. That also removes the reason csd's `id=4` hack existed,
which this plan had flagged as the blocker for migrating csd.

### Verified — gr

- 313 tests (was 292). The gr header golden vector was computed **independently
  in Python**, not read back from the C#, and matches: the migration reproduces
  the exact 80 bytes including the selective swab32.
- Mutation test: folding the merkle branch left instead of right → 3 failures.
- **Live A/B on `us-east.flockpool.com:4444`**, 2 × 180 s per binary:

  | | mean H/s | accepted | rejected |
  |---|---:|---:|---:|
  | pre-Phase-3 | 583.1 | 19 | 0 |
  | Phase 3 | 585.6 | 21 | 0 |

  Run-to-run spread within each binary (22 %, 31 %) exceeds the gap between
  them — GhostRider's rate depends on which 6-of-15 CN variants a job selects.
  Mean delta **+0.4 %**. A single pair of runs showed −27 % and reversed on the
  second round; do not trust one sample of this algo.

### Behaviour changes from the awaited handshake

1. A pool that never answers `mining.subscribe` now times out at 30 s and
   reconnects (verified in WSL) instead of stalling.
2. A rejected `mining.authorize` now fails the session into backoff instead of
   being silently ignored.
3. `Clean` parsing is lenient — the old code threw when `params[8]` was absent,
   which dropped the whole job.

### Pre-existing bug found, NOT introduced here

Against a **refused** port, gr logs nothing at all — no error, no retry. The
pre-Phase-3 binary does the same, so this predates the refactor. A mistyped pool
host currently fails silently. Undiagnosed.

### rx + nm — DONE 2026-08-03

`Mining/Stratum/CryptoNoteStratumDialect.cs` (198 lines): login, job pushes,
keepalived, submit, session id, accepted/rejected. Both members already used
awaited `CallAsync`, so unlike the Bitcoin family there was no attribution model
to fix — this was pure de-duplication.

`RxPoolClient` 572 -> 449, `NmPoolClient` 463 -> 363.

Left with the algo on purpose: login params (nm adds an `algo` ARRAY and uses
`address.worker`), job parsing (rx sniffs the nonce offset from blob length,
Monero@39 vs Bitcoin-header@76, and right-aligns a compact target; nm is fixed
124-byte/nonce@116/BIG-endian), and nonce width (rx submits 4 bytes, nm 8).

### A REAL REGRESSION THIS PHASE INTRODUCED — and how it was caught

The first `Snapshot()` implementation took a **lock**. Solvers call it inside
the inner grind loop, so 12 nm threads serialised on one mutex:

| nm | runs | mean |
|---|---|---|
| baseline | 6.43, 6.66 | 6.55 kH/s |
| migrated, LOCKED | 5.84, 5.26 | **5.55 kH/s (-15.2%)** |
| migrated, LOCK-FREE | 6.66, 6.72 | 6.69 kH/s (+2.1%) |

The old hand-rolled `JobBox` was lock-free (`Volatile.Read` + `Interlocked.Read`)
and that was load-bearing, not incidental. Both dialects now publish an
immutable snapshot through a single volatile reference; writes take a lock,
reads never do. **`BitcoinStratumDialect` had the identical defect** — gr only
hid it because GhostRider is ~10x slower per hash, so the lock was hit ~5
times/sec/thread instead of hundreds.

This was found only because a single sample was distrusted. One nm run showed
-9.2%, which looked like the same job-mix noise that had produced a false alarm
on gr; re-running showed both migrated runs below both baseline runs, which
noise cannot do.

### Live results — final build, all three CPU algos

| algo / pool | before | after | rejected |
|---|---|---|---|
| gr / flockpool | 583.1 H/s, 19 acc | 585.6 H/s, 21 acc; final run 1.64 kH/s, 7 acc | 0 |
| rx / kryptex | 6.49 kH/s, 4 acc | 6.48 kH/s; re-run 6.41 kH/s, 6 acc | 1 then 0 (see below) |
| nm / cereblix | 6.55 kH/s mean | 6.69 kH/s mean | 0 |

**One unexplained rx reject.** A single run returned 1 rejected; the immediate
re-run and the baseline both returned 0. The stale-target guard by design only
covers vardiff that REUSES the job_id — a pool that issues a NEW job_id when it
tightens will still see a share submitted against the old one. That logic was
preserved verbatim from before the refactor, so this is very likely
pre-existing rather than introduced, but it is not proven either way.

### csd — MIGRATED + LIVE-VERIFIED 2026-08-03

Onto `BitcoinStratumDialect`. `CsdStratumClient` 374 -> 239 lines, and the
deletions are the point:

- the hardcoded `id=4` on every submit — the reason acks could not be correlated;
- the FIFO `List<int>` of GPU ordinals paired by ARRIVAL ORDER;
- the extra `SemaphoreSlim` guarding it, and the rule that a failed send must
  remove the TAIL or every later share is credited to the wrong device.

All three are gone. `ReportShareAsync` captures `gpuOrd` in a closure and awaits
an id-correlated verdict.

Kept with the algo: the 84-byte header with u64 ntime, the midstate/tail split,
`PdiffTarget`, the per-GPU extranonce2 stride, and the 32-byte prevhash reversal
(gr does a per-word swab32 instead, which is exactly why the dialect keeps
`PrevHashRaw` raw).

**Verified as far as is possible off-rig:** `RebuildProducesTheGoldenMidstateAndTail`
still asserts the SAME midstate and tail as before the migration, so the header
pipeline is byte-identical. Mutation-tested: dropping the prevhash reversal
fails that test. 313 tests green.

**Live-verified on the BLUE rig: no rejects.** That closes the last gate. It
also exercises the part unit tests could not reach — csd aborts at device
enumeration before opening a socket, so its handshake and the awaited submit
that replaced the id=4 + FIFO attribution had no off-rig coverage at all.
Zero rejects on real hardware against a real pool is the evidence that the
per-GPU attribution rewrite is correct.

### Phase 3 accounting

| client | before | after | |
|---|---:|---:|---|
| `GrStratumClient` | 433 | 324 | |
| `CsdStratumClient` | 374 | 239 | |
| `RxPoolClient` | 572 | 449 | |
| `NmPoolClient` | 463 | 363 | |
| `BtxPoolClient` | 767 | 767 | bespoke, untouched |
| **total** | **2,609** | **2,142** | **−467** |

Shared layer added: 490 lines (`BitcoinStratumJob` 70, `BitcoinStratumDialect`
222, `CryptoNoteStratumDialect` 198). **Net +23 lines.**

So Phase 3 did NOT reduce line count either — the plan's −1,200 estimate was
wrong for the same reason Phase 2's was. The payoff is per NEW algo: a
Bitcoin-stratum or CryptoNote coin now needs header assembly, target math and a
solver loop, not a pool client. That is the ~80-vs-450 line difference the coin
expansion depends on, and it only shows up from algo 7 onward.

### FULL MATRIX VERIFIED ON THE RIG — 2026-08-04

**All 15 combinations passed with accepted shares** on the BLUE rig
(2x B580, HiveOS, JIT + OpenCL): 6 singles (prl btx csd rx gr nm) and all 9
GPU+CPU dual pairs. Run with `test-all-algos.sh`.

This is the definitive check on the whole plan, and it covers the parts nothing
else could:

- **csd's socket path.** Its header pipeline was proven byte-identical by golden
  vectors, but the awaited handshake and the submit that replaced the id=4 +
  FIFO attribution had ZERO off-rig coverage — csd aborts at device enumeration
  before it opens a connection.
- **btx.** Never migrated (bespoke dialect), but Phase 2 rerouted its
  `Sha256d`, `Le256` and merkle through `Akoya.Crypto`. Accepted shares confirm
  that reroute did not disturb it.
- **prl.** Phase 4 moved its entire engine (`GpuWorker`, `WorkerOrchestrator`,
  `SigmaContext`, `BSeed*`, 16 files) to a new namespace.
- **All 9 dual pairs**, which exercise both dialects concurrently in one process
  against two pools at once — the case with the most room for shared-state bugs.

### Live verification — the four migrations, in isolation

| algo | pool | result |
|---|---|---|
| gr | flockpool | 583.1 → 585.6 H/s mean, 21 accepted, **0 rejected** |
| rx | kryptex | 6.49 → 6.41 kH/s, 6 accepted, 0 rejected (1 transient, see above) |
| nm | cereblix | 6.55 → 6.69 kH/s mean, **0 rejected** |
| csd | rig (BLUE) | **0 rejected** |

btx untouched and therefore unaffected.

### Next

Nothing in Phase 3. Remaining plan work is Phase 4 (relocate Pearl-only code)
and Phase 5 (Metrics), both optional hygiene. The coin expansion in
`new coin.md` is now unblocked: a Bitcoin-stratum or CryptoNote algo needs
header assembly, target math and a solver loop — not a pool client.

---

## Phase 3 — original plan (superseded above)

**The prerequisite for the 24-coin expansion.** Everything above is hygiene;
this is the one that changes the marginal cost of a new algo.

### Today

`StratumSession` (207 lines) already owns TCP+TLS, newline framing, the write
mutex, id allocation, id→response correlation, timeouts, and error mapping —
a clean extraction. Above it sit **2,609 lines** across five clients:

```
BtxPoolClient      767
RxPoolClient       572
NmPoolClient       463
GrStratumClient    433
CsdStratumClient   374
```

Duplicated in that layer:

- **Handshake, two dialects, six implementations.** Classic
  (`subscribe`/`authorize`/`set_difficulty`/`notify`) longhand in btx, csd, gr.
  CryptoNote (`login`) in btx, nm, rx.
- **Share attribution, three mechanisms.** Csd: FIFO `List<int>` + outer
  `_submitLock`, with a 6-line comment explaining that wrong lock ordering
  mis-credits every later share. Gr: `ConcurrentDictionary<long,long>` on
  request id. Rx: `ConcurrentQueue` + awaited `CallAsync`.
- **Stale-target handling.** Rx drops shares when vardiff tightened; Csd
  recomputes target per slice to dodge reject code-23; Gr does neither.
- **Solver loop + metrics.** The stats-timer / `SetThroughput` /
  `TouchHeartbeat` block is copy-pasted across 10 files.

### Target

Two classes in `Mining/Stratum/`, each owning handshake, difficulty/target,
job-change generation, submit-with-attribution, and the metrics block:

- `ClassicStratumClient` — btx, csd, gr, and ~14 of the proposed GPU algos
- `CryptoNoteStratumClient` — nm, rx, and the CPU/CryptoNote algos

Each algo is then left with only what is genuinely its own: header assembly,
target math, kernel invocation. Estimated ~80 lines per new algo instead of
~450.

### Order

1. **Adopt Rx's submit model as the standard.** It is the only one of the three
   that structurally cannot mis-attribute, because the ack is awaited rather
   than paired by position.
2. **Fix `CsdStratumClient`'s fixed ids first.** It hardcodes ids 1/2/4 and
   routes everything through `onUnmatchedResponse`, with a comment that these
   "must NOT move to the session's id counter." That design means Csd *cannot*
   call `CallAsync`. This must be undone before Csd can adopt the shared
   dialect — it is the single largest piece of risk in this phase.
3. Extract `ClassicStratumClient` against gr (best test coverage), then migrate
   csd, then btx.
4. Extract `CryptoNoteStratumClient` against rx, then migrate nm.

### Risk

Highest in the plan, and the only phase that can silently cost money — a
mis-attributed or mis-targeted share is a rejected share, and rejects show up
hours later on a pool dashboard, not in a build log. Phase 0's golden vectors
are not optional here.

**Gate:** per-algo live pool run with accepted shares before the next algo is
migrated. Do not batch the migrations.

---

## Phase 4 — Relocate Pearl-only code ✅ DONE 2026-08-03

4,295 lines wearing a generic hat: `Mining/GpuWorker.cs` (2,703 lines, 142
Pearl/Sigma/BSeed references) and `Mining/WorkerOrchestrator.cs` (1,592). They
sit in `Mining/` as if shared, but only `PrlAlgo` uses them — btx and csd each
wrote their own solver-thread loop and stats block instead.

The cost is paid twice: it looks like infrastructure so it never gets
generalized, and it isn't infrastructure so every GPU algo reimplements it.

**Outcome:** took option (a). 16 files moved `Mining/` → `Algos/Prl/`, namespace
`Akoya.Miner.Mining` → `Akoya.Miner.Algos.Prl`: `GpuWorker`, `WorkerOrchestrator`,
`Autotune`, `ShareBuilder`, `ShareFinalizer`, `ShareTargetGuard`, `SigmaContext`,
`JobBus`, `WorkerBuffers`, `WorkerLivenessWatchdog`, `ILivenessTarget`,
`ResidentBStateBuffers`, and the four `BSeed*` files.

`Mining/` now holds only genuinely shared infrastructure — `GpuSelection`,
`ReconnectBackoff`, `NativeEnv`, `Stratum/` — with **zero** Pearl references.

**The enabler was an extraction, not the move.** `GpuWorker` had two internal
statics, `FormatDiffValue` and `FormatHashRate`, that btx/csd/gr/nm/rx and the
dashboard all called. Every non-Pearl algo therefore referenced Pearl's
2,600-line mining engine to format a number. Those are now
`Observability/DisplayFormat.cs`; without that, moving `GpuWorker` would have
dragged five algos into `Algos.Prl`.

Two options were considered:

- **(a) Be honest** — move the Sigma/BSeed-bound parts to `Algos/Prl/`. Cheap,
  removes the misleading signal, zero behavior change.
- **(b) Generalize** — extract the reusable spine (device-thread management,
  slice loop, heartbeat/throughput reporting) that btx and csd already proved
  they need. More work, but it compounds with Phase 3 for each new GPU algo.

Recommended **(a) now, (b) after the first two new GPU algos land** — by then
there are four data points on what the spine actually needs, instead of two.
(a) is what shipped. Build clean, 313 tests, 4 pre-existing warnings unchanged.

Note the naming trap while here: two classes named `StratumSession`
(`Akoya.Pool` at 1,230 lines for the Pearl protocol, `Mining.Stratum` at 207
for generic transport), both in scope in `WorkerOrchestrator.cs`.

---

## Phase 5 — Metrics ✅ DONE 2026-08-03 (scaled down — see below)

**THE PREMISE OF THIS PHASE WAS WRONG, so it was rescoped.**

The plan claimed `Metrics` "grows without bound at 30 algos". It does not. All
11 non-Pearl `SetThroughput` call sites pass `(slot, x, 0, hps, y)` — hash rate
and iteration timing only. A new algo adds no arrays. The file did not grow per
algo; it grew ONCE, for Pearl: **11 of its 19 per-GPU arrays** (`_tmadsPerSec`,
`_tilesPerSec`, `_expectedOpensPerSec`, and 8 × `_sigmaRotation*`) are written
exclusively by `Algos/Prl/GpuWorker`.

So the defect is the same one Phase 4 fixed — Pearl state presented as global —
not unbounded growth. Redesigning a working, tested 911-line file into a
"per-algo counter bag" would have been speculative generality against a
dashboard + JSON + Prometheus contract, for no caller that exists.

**What shipped instead:**
- `Metrics.SetHashRate(slot, hashesPerSec, iterMs)` — the entry point for an
  algo that just has a hash rate, which is all of them except Pearl. Eight call
  sites stopped spelling out `SetThroughput(slot, hs, 0, hs, …)`, where the zero
  is Pearl's tgemm counter and the duplicated `hs` was easy to get wrong.
- XML docs marking `SetThroughput`'s `tmadsPerSec`/`tilesPerSec`/
  `expectedOpensPerSec` parameters and all of `RecordSigmaRotation` as
  **PEARL-ONLY**.
- No change to the stored values, the JSON shape, or the Prometheus gauges.

**A bug caught during this phase:** the first pass also migrated `csd`, but csd
deliberately reports `itersPerSec = 0` (it has no iteration concept) while
`SetHashRate` sets iters = hashes. That would have silently changed the exported
`arc_miner_iters_per_second` gauge for csd. Reverted to an explicit
`SetThroughput` with a comment. The other eight sites already had
`iters == hashes`, so they are exact.

---

## Summary

| Phase | What | Lines | Risk | Blocks coin expansion? | Status |
|---|---|---:|---|---|---|
| 0 | csd/btx golden vectors | +390 | none | no (blocks Phase 3) | ✅ **done** |
| 1 | CUDA/ROCm removal | −18,155 | low | no | ✅ **done** |
| 2 | Helper consolidation | +62 | low | no | ✅ **done** |
| 3 | Stratum dialect layer | +23 | **high** | **yes** | ✅ **done** |
| 4 | Pearl relocation | 0 | low | no | ✅ **done** |
| 5 | Metrics | +40 | low | no | ✅ **done** (rescoped) |

**Net ≈ −19,400 lines.** (Phase 2 came in at +62, not −200 — see its
section; the win there was correctness, not size.)

Phase 1 was the biggest number and the easiest call. Phase 3 is the one that
decides whether 24 more coins is a quarter or a year.
