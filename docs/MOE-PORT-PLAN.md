# ARC-miner — MoE Hard Fork: Assessment & Optional Port Plan

**Status:** Plan only — not implemented. Created 2026-06-15.
**Source of truth:** upstream `docs/moe-fork-upgrade-guide.md` (pearl-research-labs/pearl).

---

## 0. TL;DR (read this first)

- **ARC-miner needs NO changes to survive the fork.** Per the upstream guide:
  "Your hashing miners do not need to change. Old dense proofs work before and
  after the fork." There is **no port-or-die deadline** for our dense miner.
- The things that **must** upgrade before the fork height are the **full node**
  (→ v1.1.0) and the **ZK-proving code** that turns a plain-proof share into a
  block certificate. That is the **pool/gateway's** responsibility, not the
  hashing miner's. HeroMiners reports it is ready.
- Mining **MoE models** (this plan) is **optional and opportunistic** — worth it
  only if MoE mining turns out to be more rewarding/competitive after the fork.
  Both dense and MoE shares are valid post-fork.
- **An MoE share is invalid before the fork** ("wasted work, your pool must
  reject it"). So an MoE-capable build MUST be gated and **never deployed before
  the fork height**.

This document scopes the optional port so the decision and the work are ready if
we choose to pursue it. It does not commit us to building it.

## 1. What the fork actually is

At a fixed **fork height** (`MoEForkHeight`, currently TBD on testnet/mainnet),
blocks switch from the **V1 (dense) ZK certificate** to the **V2 (MoE) ZK
certificate**. The V2 prover accepts **both** dense (old) and MoE (new) plain
proofs, so dense miners keep working indefinitely after the fork; MoE is an added
capability, not a replacement.

Node API change: `getblocktemplate` gains `requiredcertversion` (`1` = dense
before fork, `2` = MoE at/after fork). Pools read this to pick the certificate
version — **do not hardcode the fork height.**

### What this means for our share path
- We submit **plain-proof shares** to a pool; the pool does the ZK proving.
- `PlainProof.from_base64` / `deserialize_compat` accepts both old and new
  formats, so our **existing dense shares parse and certify unchanged** post-fork.
- The V2 wire/commitment changes (variable-length public data; proof-commitment
  prefix `2`) live in the **certificate** the pool builds — not in our share.

**Conclusion:** dense ARC-miner is unaffected. The rest of this plan is the
optional MoE capability.

## 2. Anatomy of MoE mining (what's new vs. our dense path)

Upstream entry points (`py-pearl-mining/src/lib.rs`): dense `mine` vs.
`mine_moe` (GROUPED_GEMM). MoE is selected by passing a `MiningConfiguration`
whose `moe` field is set (carries `e` = num_experts and `top_k`).

Pipeline delta, dense → MoE:

| Stage | Dense (today) | MoE (new) |
|---|---|---|
| Input A | derived from winSeed/σ | same |
| Routing | — | `topk_ids` (m × top_k expert assignments) derived deterministically; **`build_routing_data`** stable counting-sort by expert (≤256 experts, 8-bit key) → `slot_indices`, `routing_data` (=slot/top_k), per-expert exclusive-end `routing_offsets` |
| B / weights | single B (from BSeed) | **E expert weight matrices**; tokens gathered per expert |
| GEMM | one noisy int7 GEMM | **grouped GEMM**: per-expert segment (variable M per expert) × that expert's B → our existing int7 XMX `tgemm` per group |
| Hash/commit | tensor-hash → jackpot → target | commitment now **includes the routing data**; plain proof gains variable-length MoE public data (`MoEProofParams`, `MIN_MOE_WIRE_SIZE`) |
| Compare | hash ≤ target×DAF | same target compare |

**Reused as-is:** the int7×int7→int32 XMX DPAS core (`pearl_kernels.hpp`
`tgemm`), BLAKE3 keyed-merkle (`pearl_mining_capi`), noise generation, the
tensor-hash/target compare.

**Genuinely new work:** routing derivation + the counting-sort, the grouped/
segmented GEMM launch (per-expert offsets drive tiling + gather/scatter), the E
expert-weight residency (VRAM), and the V2 plain-proof serialization.

## 3. Work breakdown (if we pursue it)

1. **Dependency bump.** `pearl_mining` Rust crate / `pearl_mining_capi.dll` →
   **v0.2.0** (adds `MoEConfig`, `mine_moe`, versioned provers, V2 plain-proof
   format). Our merkle/commitment/serialization keys off it. *Low risk, do first.*
2. **Config plumbing.** Thread `MoEConfig {e, top_k, …}` through our
   `MiningConfiguration`/job parsing; the pool job now carries MoE params. Add a
   `requiredcertversion`-aware path (we only *emit* MoE when the pool/template
   says V2).
3. **Routing kernel (SYCL).** Port `build_routing_data` (`csrc/moe/
   build_routing_data.cuh`): a **deterministic stable counting-sort** by expert.
   Must be **bit-exact** with upstream (it feeds the commitment). Small, well-
   specified; the determinism requirement is the catch.
4. **Grouped GEMM scheduling.** Drive per-expert segments off `routing_offsets`
   into our existing `tgemm` (variable M per expert). New: the segmented tile
   scheduler + gather of routed tokens / scatter of outputs. The DPAS inner loop
   is unchanged.
5. **Expert-weight residency.** E expert B-matrices instead of one. **VRAM
   impact is the big unknown** — could be E× the B-side; must fit target cards
   (A-series 8 GB is the tight case; B70 32 GB is fine). May need streaming.
6. **V2 plain-proof serialization.** Emit the MoE plain-proof (routing public
   data + per-expert merkle) so the pool's V2 prover accepts it. Mirror
   `MoEProofParams` / `MIN_MOE_WIRE_SIZE`.
7. **Pre-fork guard.** Hard gate: never emit MoE shares unless the template's
   `requiredcertversion == 2`. Default dense. (Upstream: pre-fork MoE shares are
   rejected as wasted work.)
8. **Validation.** Known-answer tests against the upstream reference: routing
   sort determinism, grouped-GEMM output, commitment bytes; cross-check with the
   `zk-pow/fixures/v2_stark_proof_moe.bin` fixture and `mine_moe` on a reference
   GPU.

## 4. Open questions to resolve before committing

- **Economics:** is MoE mining actually more rewarding than dense post-fork, or
  do they pay equivalently? (Decides whether to build at all — ask the pool /
  Pearl team.) Dense remains valid indefinitely, so "do nothing" is viable.
- **Fork height/date:** still TBD on both networks. No work is deployable until
  it's set, and MoE must not ship before it.
- **`MoEConfig` specifics:** exact fields, how `topk_ids` are derived from the
  header/seed, expert count `E`, `top_k`, and per-expert weight dimensions
  (drives the VRAM math). Pull from `pearl_mining` v0.2.0 + the Rust `zk_pow`
  `MoEProofParams`.
- **VRAM:** E expert-weight matrices vs. our current ~3.8–5.7 GiB. Does it fit
  A-series 8 GB? Streaming needed?
- **Determinism on Arc:** the routing counting-sort and grouped-GEMM reductions
  must match the reference bit-for-bit across the XOR-fold transcript (we already
  rely on commutative folds for dense — verify it holds with grouped segments).

## 5. Recommendation & sequencing

No urgency. Suggested order, all behind a feature branch and **never deployed
pre-fork**:

1. **Now (cheap, de-risking):** bump `pearl_mining` to v0.2.0, confirm our dense
   path still builds/serializes against it (the V2 package must still accept our
   dense shares), and read `MoEConfig` / `MoEProofParams` to close the §4
   unknowns. This also future-proofs the dense path against the package change.
2. **Decision gate:** get the economics answer + fork date from the pool/Pearl
   team. If MoE isn't worth more than dense, **stop here** — dense keeps working.
3. **If yes:** spike the routing kernel + a known-answer test (item 3) to prove
   bit-exact determinism on Arc — the riskiest piece — before building the
   grouped GEMM and V2 serialization.
4. **Integrate** items 4–8, validate against the reference, ship gated on
   `requiredcertversion == 2`.

**Bottom line:** this is a "should we add MoE to compete" investment with no
deadline pressure on the existing release, not a survival port. The dense
production build is safe across the fork.
