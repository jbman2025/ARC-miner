# Changelog

All notable changes to ARC-miner are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/), and this project aims to follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- **Searched-rows fused kernel (Intel Arc / SYCL).** Per-iteration matrix-A
  generation and the Merkle leaf hash are fused into a single kernel that
  materializes only the searched (`SEARCH_M`) rows of A in device memory instead
  of all of them — cutting ~97–99% of A's DRAM write/read traffic. The full A is
  transparently regenerated on the share-verification path, so proofs and shares
  are unaffected (validated byte-for-byte against the two-pass path and by live
  pool accepts). Largest gain on A-series cards, where the full-A fill+hash
  dominated each iteration; B580 also rises to **~37 TH/s** and idle VRAM drops to
  **~5 GB**.
- **Built-in per-SKU tuned defaults.** Known cards (A380/A580/A750/A770, B570/B580)
  now mine at their characterized optimum with **no autotune wait** — the profile
  is baked in and applied on first run. Autotune only runs for a card we haven't
  characterized. (B70/BMG-G31 is intentionally left to autotune — its big L2 peaks
  at a higher window.)
- **Arch-aware autotune sweep.** Alchemist (sg8) now probes from a small SEARCH_M
  window and caps the ladder low instead of starting at the B-series 4096 window
  (~16 s/iter on an A750). This cuts an A-series autotune from ~10–50 min to ~1–2
  min and avoids the Windows TDR risk of the slow large windows.
- **`--autotune-deep`.** Exhaustive NB·MB·SEARCH_M grid (for characterizing a new
  card) that prints the full landscape. Note: the GRF axis is covered implicitly
  by MB (MB=2 ⇒ the kernel's large-GRF path), so deep mode confirms the max within
  the runtime-tunable space — going beyond it needs a kernel change.
- **Auto-tune on first run.** `mine-blocks` now runs the autotune sweep
  automatically the first time it sees a GPU with no cached profile, then mines
  with the result (cached for every later launch). A-series cards are fast out of
  the box — previously they mined at the B-series default window (~25× slower)
  unless the user ran `autotune` by hand. Opt out with `--no-autotune` /
  `AKOYA_AUTOTUNE_ON_FIRST_RUN=0`; skipped if you pin
  `AKOYA_TGEMM_NB`/`_MB`/`_SEARCH_M`. A cache hit logs the profile and mines
  immediately; a sweep failure falls through to mining with defaults.

### Changed
- **Templated PoW inner loop.** The transcript-GEMM k-step is now instantiated
  with a compile-time rank (`R = 256`/`128`, dynamic fallback for other ranks),
  letting the compiler fully unroll and pipeline the XMX/DPAS path. Output is
  bit-identical and is dispatched on the runtime `noise_rank`, so every pool's
  committed rank keeps working.
- **Lower idle VRAM on Arc.** The resident B-state no longer allocates the
  noise-B workspace on the SYCL backend — the Arc kernel self-allocates its
  scratch and never read it — freeing headroom on 8 GB A-series cards. The
  CUDA/ROCm backends keep the workspace.
- **Per-device XMX arch detection.** Sub-group selection (`sg8`/`sg16`) now
  queries each queue's device directly, so mixed-Arc rigs always dispatch the
  correct kernel.

## [0.2.0] — 2026-06-16

First public, open-source release (GPL-3.0). A 0%-fee GPU miner for **Pearl
(PRL)**, tuned for Intel Arc with NVIDIA and AMD support.

### GPU backends
- **Intel Arc (SYCL):** dual XMX kernels — Xe-HPG `sg8` and Xe2 `sg16` — with
  runtime dispatch. Per-die **AOT** builds (`acm-g10`, `acm-g11`, `bmg-g21`,
  `bmg-g31`) for top speed, plus a universal **JIT** build for any Intel GPU.
- **NVIDIA (CUDA):** per-architecture kernels — Hopper, Ada, Ampere, Turing,
  Volta, Blackwell, B200 — with a `portable` fallback (sm_70+).
- **AMD (ROCm):** CDNA3 (MI300X).

### Mining & performance
- **Adaptive autotune** (`autotune` subcommand): sweeps the kernel knobs
  (NB/MB/SEARCH_M) for your card, prints a ranked table, and caches the optimum,
  which the miner then applies automatically on subsequent runs.
- **Low-CPU host loop:** sleeps the host through each GPU batch instead of
  busy-polling — ~0.3% of one core while mining at full speed.
- **Wrong-card guard:** an AOT build run on the wrong GPU family exits cleanly
  with a clear message instead of crashing.
- Measured: Arc **B580 ~34.8 TH/s** (AOT `bmg-g21`), Arc **A750 ~3.8 TH/s** (AOT
  `acm-g10`).

### Pools & protocol
- **Stratum** in both dialects — Pearl `pearl/v1` challenge-first (BLAKE3 connect
  challenge) and plain client-first — plus the Akoya **gRPC/V2** protocol.
- TLS and plain TCP (`stratum+tls://` / `stratum+tcp://`).
- Per-pool difficulty via `--diff` / stratum `d=` password.
- Broad pool compatibility (HeroMiners, Kryptex, and more — see `docs/POOLS.md`).
- **Adaptive no-trigger watchdog:** the share-starvation reconnect budget now
  scales with the card's real share rate (`AKOYA_MINE_TRIGGER_WATCHDOG_K`,
  default 20), so slow cards / high difficulty no longer trigger reconnect
  thrash. Set `K=0` for the old fixed-budget behaviour.

### Share correctness
- **Duplicate-share fix:** the per-session winSeed base now folds in a
  process-monotonic epoch, so a reconnect under an unchanged job no longer
  re-walks the same search space and resubmits identical proofs.
- **Below-target fix:** queued shares are re-checked against the *current* pool
  target before submission, so a vardiff increase mid-flight drops the share
  locally instead of incurring a pool rejection.
- **Difficulty seeding:** the requested `--diff` seeds the difficulty prior, so a
  pool that sends a job before its first `set_difficulty` no longer mines against
  a trivially-easy fallback target.

### Observability
- **Stats API** (`--api-port`): JSON at `/api/stats` (and `/`, `/stats`,
  `/summary`) plus Prometheus at `/metrics`, including per-GPU hashrate, iter
  time, accepted/rejected counts, and a live heartbeat age.
- **Share trace** (`AKOYA_SHARE_TRACE=1`): per-submitted-share diagnostic that
  dumps the claimed hash's difficulty math vs. the target — useful for debugging
  pool rejections.

### Build & packaging
- One-shot builds: `build.sh` (Linux x64/ARM64), `build.ps1` (native Windows),
  `build-aot.ps1` (per-die Arc AOT on oneAPI 2026.0).
- **Native AOT**, self-contained `./out` — no .NET runtime required to run.
- `selftest` subcommand validates config, native libraries, and pool
  reachability before mining.

### Misc
- **0% developer fee, forever.** No dev-mining, no telemetry.
- A draft RFC for standardized pool fee transparency (`docs/POOL-FEE-TRANSPARENCY.md`).
- Notes on the upcoming Pearl **MoE hard fork** — dense miners (this one) keep
  working before and after the fork (`docs/MOE-PORT-PLAN.md`).

[0.2.0]: https://github.com/your-org/arc-miner/releases/tag/v0.2.0
