# Changelog

All notable changes to ARC-miner are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/), and this project aims to follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Removed

- **BTX (`--algo btx`) support, entirely.** The chain is broken and pools have
  dropped it. Gone: `src/Akoya.Miner/Algos/Btx/`, `native/btx-matmul/`,
  `docs/BTX-POW-SPEC.md`, the golden-vector tests, the `btx_capi` build steps in
  `build.ps1`/`build.sh`, the `ARC_BTX_*` env vars, and the BTX launchers. The
  dual-mining combos are derived from `AlgoRegistry`, so `btx+rx` / `btx+gr` /
  `btx+nm` disappeared with the registry entry — the test matrix drops from
  6 singles + 9 duals to 5 + 6.

  Note for anyone reading the build scripts: `$btxAotFlags` was **shared with
  CSD**, not BTX-only. It is now `$syclAotFlags`; CSD's AOT/JIT behaviour is
  unchanged. `out-btx-linux` remains the default `OUT_NAME` in
  `build-linux-wsl.sh` — that is a legacy directory name, not a BTX artifact.

- **Rank-256 support.** Mainnet is long past the rank-penalty softfork (PR #275,
  height 96,251), which put a consensus floor at rank 128 and scales the jackpot
  bound by `128/rank` — so every rank above 128 does proportionally more work for
  the same reward and nobody mines 256 by choice. Gone with it:
  - `RankFork` and the height-sniffing that chose between rank 128 and 256, plus
    the mid-session penalty warning and the one-shot self-relaunch. The stratum
    profile now pins `noise_rank=128` unconditionally.
  - The `ARC_PRL_RANK_FORK_HEIGHT` / `_ACTIVE` / `_AUTORESTART` overrides.
  - The `launch_tgemm_pow_templated<256>` instantiation in the SYCL kernel. R=256
    still runs correctly through the `R_const=0` dynamic loop (bit-identical
    math), just without the unroll — this costs speed, never validity.

  Pearl height persistence survives the removal as `PrlHeightStore`
  (`ARC_PRL_HEIGHT_FILE` is unchanged): the dashboard's forks-survived counter
  needs it to report correctly during a cold start, and it was never rank code.

### Fixed

- **`ARC_MINE_NOISE_RANK` defaulted to 256** — a silent half-reward trap.
  `ShapeOverridePresent` trips on *any* `ARC_MINE_*` variable, so setting only
  e.g. `ARC_MINE_M` took the override branch and carried rank 256 to the wire.
  Such shares are **accepted**, just worth `128/256`, so it never surfaced as an
  error. Default is now 128.

## [0.3.1] — 2026-08-11

Everything after the rank-128 fork went live: the fixes that make a rig recover
from it unattended, the rogue theme's persistent progression, and the forks-
survived counter.

> ### ⚠ Upgrade before mainnet height 98,900
>
> Pearl's salted noise-seed hardfork (PR #280) activates at **mainnet height
> 98,900**. It changes how the proof-of-work noise seeds are derived, in the GPU
> kernel as well as the host, so **any build older than 0.3.1 will have every
> share rejected from that height onward.** There is no partial-credit failure
> here and no config flag that rescues an old binary — the kernel itself has to
> be the new one.
>
> Upgrading is the whole fix; the switch is height-gated and flips itself live on
> the job that crosses 98,900, with no restart.
>
> To confirm a build is safe before the fork reaches you:
>
> ```bash
> arc-miner verify-seeds
> ```
>
> It runs the real mining kernel and compares its noise seeds against the host's
> for both the pre- and post-fork rules. Anything other than `PASS` means do not
> mine across the fork with that build. Note that on Linux this needs GPU access,
> so run it the same way you run the miner (`sudo`, or as a member of `render`).

### Added
- **Salted noise-seed hardfork support (pearl PR #280).** From mainnet height
  **98,900** the noise seeds no longer chain off the raw Merkle roots. Each root
  is first bound to the dimension it was built from, under a domain-separated
  keyed BLAKE3 — A to the row count `m`, B to the column count `n`:

  ```
  bound_a = blake3_keyed(SALT_A, A_root || m_le32 || 0^28)
  bound_b = blake3_keyed(SALT_B, B_root || n_le32 || 0^28)
  b_seed  = blake3(job_key || bound_b)
  a_seed  = blake3(b_seed  || bound_a)
  ```

  This is a **proof-of-work change, not a parameter change**: the seeds decide
  the noise the GEMM searches over, so it had to land in the SYCL kernel
  (`launch_commitment_hash`) as well as the host, and both had to agree exactly.
  The bound message is one 64-byte block — the 28 zero bytes are part of it, not
  padding to skip, because BLAKE3 mixes the input length into the final block.

  Unlike the rank-penalty fork, this flag sizes nothing, so it **flips live on
  the job that crosses the height** — no relaunch, no lost work. Selected via a
  new `pearl_capi_set_salted_seed` export, which is additive and therefore does
  not move the pearl_capi ABI version. Overrides: `ARC_PRL_SALTED_SEED_HEIGHT`
  (testnets) and `ARC_PRL_SALTED_SEED_ACTIVE=1`.

  Its failure mode is also better than the rank fork's. That one produced valid
  shares worth half as much — silent. Seeds that disagree with the network
  produce a hash the pool rejects, in either direction, so a mistake here is
  loud within seconds.
- **`arc-miner verify-seeds`.** Proves the GPU and the C# host derive identical
  noise seeds, on both sides of the fork, by running the real mining kernel
  rather than a copy of it.

  This exists because of the one failure nothing else catches: the GPU derives
  the seeds that shape its search and the host re-derives them to build the
  share, and if those ever drift apart the miner hunts one noise field and
  submits proofs for another — every share rejected, every dial still green, no
  log line explaining it. A fork is exactly when two implementations of the same
  function can diverge. Verified PASS on Windows (AOT) and Linux (JIT), which
  also produce byte-identical seeds to each other.
- **`plainly` theme — a dashboard that tells you what the numbers mean.** Every
  other theme, however it dresses up, is the same instrument: labelled fields in
  a table, scanned by someone who already knows what `iter 72.6ms` signifies.
  This one is not a costume, it changes what the panel is for. It writes
  sentences, and it computes the things a table cannot state — share cadence in
  human terms ("about one every 6 seconds"), what a dead card is costing as a
  share of output, how long "up 02:14:07" actually is, and first of all whether
  anything needs doing. On a healthy rig its opening line is "Nothing needs your
  attention", which is the most useful sentence a monitor can say and which no
  other theme here can say, because none of them decide anything.

  Prose makes a quiet untruth much easier to write than a table does, so the
  awkward cases are stated bluntly rather than softened — an unreachable pool
  says "Nothing you mine right now will count" — and dual mining gets its own
  line for the CPU pool, because one pool name above three workers reads as
  though they all file to the same place.
- **`broadsheet` theme.** The rig as the front page of a daily paper. Shares are
  *filed*, rejects are *spiked* (an editor killing a story used to mean literally
  impaling it on a metal spike), the pool is *the wire*, each worker is a *desk*
  with a *deadline*, and the block height is the *issue number*.

  Where the other skins change the nouns, this one changes the shape: instead of
  a fixed title it leads with a **generated headline** rewritten from the rig's
  state, worst news first — a stalled desk, a dead wire, a pool spiking copy, or
  on a good day the hashrate. That makes the house rule the theme's mechanic
  rather than a constraint it works around: the largest text on the panel is, by
  construction, whatever an operator most needs to know. The headline names the
  **desk number**, not just the model, because "Arc B580 has stalled" tells a
  two-B580 rig nothing.

  Its sensor columns also yield to keep STATUS on screen when the terminal is
  narrow, so a dead card is reported in two independent places.
- **Forks survived.** Both themes now count the Pearl consensus forks this rig
  has mined past — classic puts it in the title next to the height, rogue on the
  party row beside the other lifetime tallies. Reads **2** on mainnet today: MoE
  and the rank-penalty softfork.

  A fork counts from the lowest height at which the chain is PROVABLY past it,
  which is usually its own activation height from the node's `chaincfg/params.go`
  — never an announcement summary, which is the PR #275 lesson. MoE is the
  exception that shows the rule: its `MoEForkHeight` was never published to us,
  so it counts from the rank-penalty height instead. That is sound rather than a
  fudge, because only V2 certificates carry a noise rank, so the rank-penalty
  softfork cannot have activated before MoE — a chain past 96,251 is necessarily
  past both. The counter under-reports below that bound and never over-reports,
  so it cannot claim a fork you did not mine through.

  Two things it will not do. It is hidden entirely off Pearl rather than shown
  as `0` — and that needs a real gate, not just a height check, because the
  persisted `last-height` file is Pearl's and an `rx` or `btx` run would
  otherwise inherit a previous Pearl session's count and show it against a chain
  that has never forked. And in the rogue theme it does **not** sit in the title:
  that is the row the floor map is sized against, and the 17 extra columns
  deleted the map outright at a 110-column terminal. Both are covered by tests.
- **The rogue theme grew a memory.** Levels now come from LIFETIME accepted
  shares rather than the session's, persisted alongside block-find trophies and
  the best hashrate ever seen (`~/.arc-miner/progress`, override
  `ARC_PROGRESS_FILE`). A per-session level reset to 1 on every launch, which
  made the whole progression idea decorative.
- **Moments.** First accepted share lights FIRST BLOOD, a block find turns the
  title gold for twenty seconds, and beating the best-ever hashrate shows a PB
  marker. All confined to existing rows, so the header height never changes and
  the panel cannot jump.
- **Combat-log voice.** Events carry a short tag — `hit`, `corpse`, `parried`,
  `LEGENDARY`, `DOWNED`, `descend`, `guild` — in a fixed-width column, with the
  line the miner actually logged preserved verbatim after it. The tag is the
  flavour; the log line is still the fact.
- **Hashrate sparkline** on the title row, showing the last minute. Scaled with
  a floor at 2%% of peak so a rig wobbling half a percent reads as flat and only
  a genuine dip moves the line — a pure min-max scale turns healthy jitter into
  an alarming sawtooth.

### Fixed
- **Every theme dropped a dead card's STATUS on an ordinary terminal.** STATUS is
  the last column in the worker table, so it is the first thing clipping eats —
  and losing it means the panel renders a stalled card as a producing one, with
  no indication anything is wrong. Measured: `classic` lost it entirely at **80
  columns**, `cyberpunk` lost the stall age at 80, and all three lost it at 64.

  Cause was the same in each: the name column was sized against a hand-counted
  constant that never included the status text, so the table was always a few
  columns wider than the theme believed. Replaced with a shared
  `Panel.SizeNameColumn`, which reserves the status width first and then lets the
  **sensor columns yield** — temp and power are decoration, status is not. A
  regression test now asserts a stalled worker stays visible in every theme at
  64/80/100/110/130 columns, keyed on the stall age rather than a keyword since
  each theme words the failure differently (`STALL`, `DOWNED`, `STALLED`).
- **The miner now recovers from the rank fork on its own.** A post-fork cold
  start still cannot know the chain height in time — on stratum `ConnectAsync`
  returns no job and the mining shape is fixed before the first notify arrives —
  so it now records the height, relaunches once, and comes back up at rank 128
  without anyone watching. The relaunch is gated on the height having ACTUALLY
  reached disk, which is what stops it being a boot loop: if persistence failed,
  it logs and keeps mining instead. Disable with
  `ARC_PRL_RANK_FORK_AUTORESTART=0`. Verified live: cold start at 256 → self
  restart 16s later → every subsequent start at 128 with no warning.
- **The restart hook was only wired when `--api-port` was set**, so the majority
  of rigs had no way to relaunch themselves. It is now always wired; the API
  password still gates the control endpoints.
- **The rank switch never fired on stratum (shipped in 0.3.0).** `ConnectAsync`
  returns no initial job on the stratum path — it Registers and waits for a
  notify — so the post-connect correction could not run, and a post-fork cold
  start resolved rank 256, logged "restart the miner", and the restart repeated
  it forever. Verified against a live pool with the activation height forced to
  1: run 1 came up at 256, run 2 at 128 with no warning. The observed height is
  now persisted to `~/.arc-miner/last-height` (override `ARC_PRL_HEIGHT_FILE`),
  so the first post-fork session is penalised and every session after it is
  correct. Writing the hint file can never fail a rig.

## [0.3.0] — 2026-08-05

The multi-algo release: `rx`, `gr` and `nm` joined `prl`/`btx`/`csd`, dual-mining
landed, the TUI dashboard became the default, the embedded web UI was removed,
and the project got its first unit tests. Also the release that added
rank-penalty softfork support — shipped before the fork activated, and with the
stratum bug that 0.3.1 fixes.

### Added
- **Rank-penalty softfork support (pearl PR #275).** From mainnet height
  96,251 a certificate must declare noise rank >= 128, and the jackpot bound is
  scaled by `128/rank` — which exactly cancels the advantage a higher rank used
  to buy. Our stratum profile pinned rank 256, so post-fork it would have kept
  producing perfectly VALID shares worth half as much: a silent halving, not a
  reject. The stratum rank is now chosen from the observed chain height, with
  two correction points — once the pool reports a height at session start
  (before worker buffers are sized), and a one-shot CRITICAL log if the
  activation height is crossed mid-session, since the shape is fixed for the
  life of the process. Overridable via `ARC_PRL_RANK_FORK_HEIGHT` (testnets) and
  `ARC_PRL_RANK_FORK_ACTIVE=1` (pool already moved, no post-fork job seen yet).
  The Akoya gRPC profile was already at 128 and is unaffected.

### Added
- **GPU temperature, power and fan on Linux (sysfs hwmon).** The Intel `xe`
  driver publishes one hwmon node per card — package and VRAM temperature, fan
  RPM, and a monotonic energy counter — and, unlike the render nodes needed for
  compute, it is readable without root. Both themes gain a TEMP/POWER column
  (rogue calls temperature HEAT) that appears only when something actually
  reports, so Windows does not get a permanently blank column.

  Notes: there is no instantaneous-power file on this driver, so watts are
  differentiated from the energy counter between samples, with a counter reset
  reporting nothing rather than a spike. Readings are keyed by PCI address and
  shown **only** when a card's address is known and matches a discovered node —
  a guessed mapping would attribute one card's temperature to another, which is
  worse than showing none. Absent sensors render as "—", never 0.
- **`cuDeviceGetPCIBusId` in the SYCL shim now returns the real PCI address.**
  It previously returned a hard-coded `0000:00:00.0` for every device, making it
  impossible to tell cards apart. Requires a native rebuild to take effect; until
  then the placeholder is detected and sensors stay hidden rather than wrong.
- **Dashboard themes (`--theme <name>` / `ARC_THEME`).** The panel's layout now
  sits behind a small seam: a theme is a pure function from the metrics snapshot
  to a list of rows, while the machinery that is genuinely hard to get right
  (terminal-cell width arithmetic, wrap prevention, event-pane sizing, the
  in-place redraw, graceful stand-down) is shared and written once.
  - `classic` (default) — unchanged behaviour.
  - `rogue` — a roguelike skin. The joke is Intel's: Arc generations are named
    Alchemist, Battlemage, Celestial, Druid, so an Arc rig's party classes come
    straight off the box. Block height is dungeon depth, workers are party
    members with levels and HP bars, block finds are legendary drops. All ASCII.

  Every theme is bound by one rule, enforced by a test that runs against all of
  them: **flavour decorates the truth, it never replaces it.** A stalled worker
  is red and says so in plain words in every skin — nobody should have to decode
  a metaphor at 3am to find out which card died.
- **Block height in the dashboard snapshot.** Both transports already carried it
  and both funnel through the orchestrator's job handler, so it is recorded in
  one place.

### Fixed
- **Block height was only recorded on the `prl` path**, so every CPU algo showed
  "Depth unknown" in the rogue theme despite the height being right there in the
  job (`rx-pool: new job=… height=3733288`). Now wired in the `rx` and `nm` job
  handlers too.
- **A healthy `csd` rig rendered as permanently "wounded".** The heartbeat was
  touched inside the 10-second stats block rather than per completed nonce
  slice, so two B580s hashing at 1.33 GH/s each sat at a 7-second heartbeat age
  against a 5-second "stale" threshold. Now beats on every slice.
- **Sensors and block height reached only some algos.** The first cut wired PCI
  capture into the `prl` orchestrator and height into `prl`/`rx`/`nm`, so a
  `csd` or `btx` run silently showed neither. PCI capture is now shared
  (`GpuIdentity`) and called from all three GPU algos; `btx` reports height from
  its slice. (`csd` genuinely cannot — Bitcoin-stratum notify carries no height,
  it is buried in the coinbase — so it correctly shows "Depth unknown".)
- **`csd` and `btx` also flooded the dashboard's log pane**, same as the CPU
  algos below — one line per GPU every 10 seconds.
- **CPU algos flooded the dashboard's log pane with their periodic stats line.**
  `GpuWorker` has always skipped that line when the dashboard is active — it is
  redundant with the per-worker table — but `rx`/`nm`/`gr` did not, so a CPU run
  buried every real event under a hashrate line every few seconds. All six sites
  (pool and solo) now share `GpuWorker`'s guard; `Metrics` still feeds the table.
- **Right-aligned values could collide with the text beside them.** The gap was
  a side effect of overflow handling rather than a rule, so a row landing at
  exactly the panel width rendered `*2 LEGENDARYparty "rig01"` with no space.
- **Device names were truncated to trademarks.** `Intel(R) Arc(TM) B580 Graphics`
  clipped to `Intel(R) Arc(TM) B580 Gra…`, hiding the only token that matters.
  Table rows now show `Arc B580`; non-Intel names are left alone.
- **Dashboard panel corrupted itself in a window narrower than 60 columns.** The
  layout clamped its width to a 60-column *floor*, so in a smaller terminal every
  row was built wider than the window, wrapped, and pushed the rows below it
  down — the "fixed" header walked off the top of the screen a line per tick.
  The panel now never draws wider than the window (with the per-worker name
  column giving up space first), and every emitted row is clipped as a backstop.
- **A fatal exit left the terminal wedged.** On a non-zero exit the render loop's
  cleanup never ran: the cursor stayed hidden and, worse, the dashboard was still
  swallowing log lines into its in-memory ring, so the error that ended the run
  was never printed anywhere. The panel now stands down on that path too, and
  replays the tail of its event ring into the scrollback on any exit.
- **A console write error could take the miner down with it.** The render loop
  only caught cancellation, so an `IOException` from a closed or resizing window
  propagated out of a task nobody awaited. Rendering is now failure-tolerant —
  the panel is cosmetic and must never affect the mining pipeline.
- **`q`/Esc took up to a full refresh interval to register.** Keys were polled
  only on redraw. The loop now polls at 100 ms regardless of refresh rate, and
  drains the whole key buffer rather than one key per tick.
- **The CPU worker was listed under a "GPUs" heading** when dual mining (or when
  running a CPU-only algo like `gr`/`rx`/`nm`). The table is now headed "WORKERS"
  whenever a CPU row is present, and that row is tagged `cpu`.
- **Dual-mining showed one meaningless summed hashrate.** The two halves run
  different algorithms, so adding a pearl MH/s to a RandomX KH/s produced a
  number that described nothing. Dual runs now show `gpu <rate>  cpu <rate>`
  side by side; single-sided runs are unchanged. The JSON API's
  `hashrate_total_hs` still carries the combined figure.
- **A stalled worker's frozen hashrate read as a live one.** The last sample
  keeps being displayed after a worker stops reporting; it is now dimmed once
  the row goes STALL, so a dead card looks dead.
- **Redraws drifted off the requested interval** when it was not a multiple of
  the poll tick (a 250 ms refresh actually redrew every 300 ms).

### Changed
- **The TUI dashboard is now on by default.** It was opt-in behind
  `--dashboard`; the panel is the better default for an interactive run, and the
  cases that need a plain line stream (redirected stdout, JSON logging) already
  turn it off on their own. `--dash-off` (or `ARC_DASHBOARD=0`) opts out;
  `--dashboard [ms]` still sets the refresh interval.

### Removed
- **The embedded web UI.** `--api-port` no longer serves an HTML page: `/ui`,
  `/dashboard` and `/index.html` are gone, and `/` always returns the stats JSON
  instead of content-negotiating on `Accept: text/html`. The API itself is
  unchanged — `/api/stats`, `/stats`, `/summary`, `/metrics` and the
  `/api/control/*` endpoints all behave exactly as before, so Kryptex-style
  pollers and Prometheus scrapes are unaffected. Pool/wallet/worker/algo changes
  are still available by POSTing to `/api/control/config` with `--api-password`.

### Added
- **Unit tests (`tests/Akoya.Miner.Tests`).** The project had eight `.csproj`
  and zero tests. Every bug found while bringing up `gr` and `nm` was a pure
  function bug — merkle byte order, an inverted `diff_to_target`, big- vs
  little-endian target comparison, `Metrics` slot collisions — the kind unit
  tests catch instantly and live pool runs catch over hours. 57 tests, no GPU, no
  native backend, no network; `dotnet test` runs anywhere in ~50 ms.

  Coverage: `GrHash` target math (checked against the closed form
  `(2^240 - 2^224)/diff`, plus monotonicity, which is what an inverted
  `diff_to_target` violates), `GrHash.MeetsTarget` word order, `Swab32`,
  `NmHash` big-endian target comparison and `ParseTarget` padding,
  `PoolUrl.Parse` (schemes, IPv6 brackets and zone ids, missing port, trailing
  path), `CpuAlgoConfig` precedence (see Changed), and `Metrics` CPU/GPU slot
  registration in **both** dual-mining startup orders — the race that produced
  the index-0 collisions below. All were confirmed to fail when the historical
  bugs are reintroduced.

  The suite lives outside `src/` so `src/Directory.Build.props`'s strict
  analyzer baseline does not apply to test code. `ParsePoolUrl` moved out of
  `Program.cs`'s top-level statements into `Config/PoolUrl.cs`, since a local
  function cannot be tested.

- **CPU mining: NeuroMorph (`--algo nm`, Cereblix / CRB).** Third CPU algo,
  verified end to end against `stratum.cereblix.com:3333` (accepted shares, 0
  rejected). A self-mutating register-VM proof of work: every 4096 blocks the VM
  rebuilds its own semantics — opcode weights, program length, constants, AES
  keys — from chain entropy, on top of a 2 MiB per-thread scratchpad and a 64 MiB
  per-epoch dataset shared by all threads.

  `native/randomx-xmrig/neuromorph_capi.cpp` (built by `build_nm_capi.{bat,sh}`)
  vendors `crypto/nm/*` from the xmrig-cereblix fork (GPLv3) and calls it as-is,
  the same discipline as GhostRider. Upstream only ever builds with MinGW-GCC, so
  exactly two lines needed MSVC equivalents — `unsigned __int128` → `__umulh`,
  and the AES-NI feature gate, which would otherwise have silently fallen back to
  software AES. Both are marked `ARC PATCH` in the vendored sources. The shim
  itself owns only the shared-dataset lifetime that XMRig's `NmShared.cpp` gets
  from hwloc/VirtualMemory.

  Pool support is the Monero/XMRig login dialect with `seed_hash`, so
  `NmPoolClient` is closely modelled on `RxPoolClient`. NeuroMorph-specific
  details: 124-byte header, 8-byte nonce at offset 116 (the miner iterates only
  the low 4 bytes; the high 4 are the pool's extranonce1), and a 256-bit
  **big-endian** target compared with `memcmp` — the opposite order from
  RandomX's, which is the easiest thing to get wrong when adapting that code.
  Dual mining (`prl+nm`, `--pool-cpu`/`--wallet-cpu`) and `--dashboard` stats
  work out of the box, since both are generic. Solo is not implemented: Cereblix
  solo uses the coin's own getwork/submitwork HTTP API, a different protocol.

  The 64 MiB dataset and the 2 MiB scratchpads are allocated from huge pages
  where the OS allows it, falling back to normal pages otherwise. This is not a
  minor tuning knob for NeuroMorph: its dataset read chain is deliberately
  data-dependent and unprefetchable, so TLB pressure dominates — 8.0 KH/s on
  normal pages vs 11.1 KH/s with huge pages at 24T on a 5900X (upstream XMRig
  gets 11.2 KH/s). The miner warns at startup when it falls back.

- **CPU mining: GhostRider (`--algo gr`, Raptoreum).** Second CPU algo, now
  mining for real — verified end to end against flockpool (accepted shares, 0
  rejected, pool vardiff climbing). Binds XMRig's GhostRider (GPLv3): a rotation
  of 15 classic 512-bit hashes (blake, bmw, groestl, jh, keccak, skein, luffa,
  cubehash, shavite, simd, echo, hamsi, fugue, shabal, whirlpool) interleaved
  with 6 CryptoNight variants, ordered per-block from the prev-hash.

  The shim (`native/randomx-xmrig/ghostrider_capi.cpp`, built by
  `build_gr_capi.{bat,sh}`) compiles **XMRig's own `crypto/ghostrider/ghostrider.cpp`
  verbatim** and calls `ghostrider::hash_octa`, rather than reimplementing the
  hash loop as the first cut did — the 8-lane scratchpad packing around the loop
  is part of the algorithm, and hand-rolling it is a standing source of
  consensus divergence. Hashing is therefore 8 nonces per call; all mining loops
  batch accordingly (also ~35% faster than the old single-hash path).

  Pool support is Bitcoin/Yiimp Stratum V1 (`GrStratumClient`), which is what
  every GhostRider pool speaks — XMRig routes this algo to its `EthStratumClient`
  and has no Monero-style dialect for it, so the unverifiable `GrPoolClient`
  login path and `ARC_GR_STRATUM` were removed. Config via `ARC_GR_*` / shared
  `--pool` / `--wallet`. Solo (getblocktemplate) remains a scaffold: it builds a
  standard Bitcoin coinbase only, and Raptoreum mainnet needs smartnode/founder
  and cbTx rules, so mainnet solo blocks would be rejected.

  On Windows the shim JITs XMRig's asm CryptoNight mainloops; Linux uses the
  portable path (bit-identical output, slower — the GAS asm isn't vendored yet).

- **GhostRider dual-mining (`--algo prl+gr`, `gr+btx`, `gr+csd`).** GhostRider now
  runs alongside any GPU algo, mirroring `rx`. It reserves logical CPUs for the
  GPU host loop (2 by default, `ARC_GR_DUAL_RESERVE`, or set `--threads-cpu` to
  opt out), and it no longer falls back to the shared `--pool` / `--wallet` when
  dual-mining — those belong to the GPU algo, and silently pointing GhostRider at
  a Pearl pool with a Pearl address only produced errors.
- **CPU-side pool flags: `--pool-cpu`, `--wallet-cpu`, `--worker-cpu`,
  `--password-cpu`.** The two halves of a dual pair mine different coins on
  different pools, so the CPU algo needs its own connection details. `--pool-cpu`
  accepts the same URL schemes as `--pool` (`stratum+tls://`, `stratum+tcp://`,
  `ssl://`, bare `host:port`, IPv6 in brackets) — the parser is now shared rather
  than duplicated. These set generic `ARC_POOL_CPU_*` variables that both CPU
  algos read, so the flags work for `gr` and `rx` alike, and per-algo `ARC_GR_*` /
  `ARC_RX_*` settings still take precedence. Full example:

  ```
  arc-miner --algo prl+gr \
    --pool stratum+tls://prl.kryptex.network:8048 --wallet krx….worker1 \
    --pool-cpu stratum+tls://us-east.flockpool.com:5555 --wallet-cpu R…
  ```
- **GhostRider stats on `--dashboard`.** `GrStratumClient` now reports hashrate,
  accepted/rejected shares, pool difficulty, connection state and worker
  heartbeat, so GhostRider appears as its own device row ("CPU · 12T GhostRider")
  with a live health indicator instead of a blank line. The JSON stats API gained
  an additive `cpu_pool` object (url/worker/connected) — the existing `pool`
  object describes the GPU algo's pool, which is empty on a CPU-only run.

### Performance
- **GhostRider more than doubled: huge pages for the CryptoNight scratchpads.**
  `ghostrider_capi` was allocating each worker's 16 MiB of scratchpads with plain
  4 KiB pages. Every CryptoNight lane random-walks a 2 MiB buffer, so the TLB
  thrashed and hashrate was pinned by page-walk latency rather than by cores.
  Same-seed benchmark on a 5900X:

  | threads | before | after |
  |---|---|---|
  | 12 | 986 H/s | 1335 H/s |
  | 16 | 1076 H/s | 1569 H/s |
  | 24 | 1025 H/s | **2162 H/s** (+111%) |

  Note the shape change, not just the level: gr previously looked flat past 12
  threads, which had been recorded as "GhostRider is L3-bound, thread count
  barely matters". That was wrong — it was the TLB, and gr now scales with cores.
  Falls back to normal pages when SeLockMemoryPrivilege is unavailable, warning
  at startup so the halved hashrate has an obvious cause.
- **GhostRider workers moved from `Task.Run` to dedicated threads.** N infinite
  CPU-bound loops on the thread pool occupied every pool thread and starved the
  stratum reader, share submitter and reporter that share it — the pool only
  grows by one thread per second once saturated. They are now dedicated,
  named, per-session OS threads, reaped on disconnect (a flapping pool was also
  leaking 16 MiB of scratchpads per thread per reconnect).
- **MSR tweaks now applied for GhostRider.** XMRig applies its Ryzen/Intel MSR
  preset for `RANDOM_X`, `CN_HEAVY` **and** `GHOSTRIDER` (`crypto/rx/Rx.cpp`), so
  `gr` now calls the same `MsrTweaker` as `rx`, and restores on exit. Deliberately
  NOT applied to `nm`: NeuroMorph is not in that list and the cereblix fork never
  routes it through `Rx::init`. Requires Administrator; warns and continues
  otherwise. *Unmeasured here — this box is not elevated.*
- **Opt-in worker pinning for `gr` and `nm`** (`ARC_GR_AFFINITY=1`,
  `ARC_NM_AFFINITY=1`), reusing rx's `CpuAffinity`. `CpuAffinity` and
  `MsrTweaker` moved from `Algos/Rx/` to a shared `Algos/Cpu/` namespace since
  three algos now use them.

- **`gr --algo gr` benchmark now rotates the CryptoNight trio (item 7).**
  GhostRider selects its trio of six CryptoNight variants from the block
  header's previous-hash field, and the benchmark filled that field with a fixed
  pattern. It therefore measured **one** trio for the whole run and reported it
  as the machine's GhostRider hashrate.

  Measured on this box (5900X, 8 threads, 10 s rotations): the per-trio rate
  ranges from **589.5 to 2860.0 H/s — a 4.85× spread**. The old figure was
  whichever trio the fixed seed happened to select, so it could overstate or
  understate real-world hashrate by nearly 5×.

  The prev-hash is now re-seeded on a timer (all threads share one seed per
  rotation, as they would share one block), and the headline is the mean across
  rotations with the observed range alongside. `ARC_GR_BENCH_ROTATE_SEC` tunes
  the interval; `0` pins the seed for A/B runs where a stable number matters
  more than a representative one. Note the mean needs a few minutes to settle —
  ten rotations is not enough to converge.

- **`gr` solo refuses chains whose coinbase rules it does not implement
  (item 14).** It built a standard Bitcoin coinbase and merely *warned* that
  Raptoreum mainnet needs smartnode/founder payouts and a cbTx payload — so it
  would mine happily and have every block rejected at submit, which showed up
  only as a "solo block rejected" line after the work was wasted.

  It now inspects the template and refuses up front, keyed on the **rules the
  node asks for** rather than the chain name: a `coinbase_payload`, a non-empty
  `smartnode`/`masternode` payee list (array or object form), enforced
  smartnode/masternode payments, or a non-empty `superblock`. That is the right
  discriminator — a Raptoreum *testnet* with smartnodes enforced would reject us
  too, and a bare GhostRider regtest chain is fine. The refusal is fatal
  (exit 78) rather than retried, since backing off cannot change a consensus
  rule. `ARC_GR_SOLO_FORCE=1` overrides.

### Verified (no code change)
- **GhostRider does not use the CryptoNight asm mainloops on *either* platform
  (item 6 — premise disproved).** The backlog item read "gr on Linux uses the
  portable CryptoNight path; the asm mainloops are MASM/win64 only, so Linux is
  slower — porting the GAS asm is the perf follow-up", and the build script and
  shim both said the same. That is wrong, and the comments have been corrected.

  Upstream `CnHash.cpp` registers the six GhostRider variants with `ADD_FN`
  only. `ADD_FN_ASM` — the macro that installs `cryptonight_*_hash_asm` into the
  dispatch table — is called for `CN_2`, `CN_HALF`, `CN_R`, `CN_RWZ`, `CN_ZLS`,
  `CN_DOUBLE`, `CN_PICO_*` and `CN_UPX2`, and **never** for `CN_GR_*`.
  GhostRider reaches CryptoNight only via `CnHash::fn()`, which therefore falls
  back to `data[av][Assembly::NONE]` — the portable path — on Windows as well.
  `patchAsmVariants()` does patch `cn_gr{0..5}_*_mainloop_asm` at startup and
  the call sites exist in `cryptonight_single_hash_asm<CN_GR_x>`, but nothing
  hands those to the dispatcher, so that code is dead in this XMRig version.
  (Our `CnHash.cpp` is byte-identical to upstream, so this is not local drift.)

  Confirmed by doing the port rather than only reading the code: the SysV
  `cn_main_loop.S` and its `cn1`/`cn2` bodies were vendored and the library
  rebuilt with `-DXMRIG_FEATURE_ASM`. Throughput went **146.3 → 145.4 H/s**
  (1 thread, mean over 6 identical trios) — no change beyond noise. The asm
  sources were then removed again: they would be dead weight, and enabling the
  flag also makes `patchAsmVariants()` allocate and rewrite executable memory at
  static-init time, a needless hazard on W^X/SELinux kernels for zero gain.

  So there is no Windows/Linux GhostRider performance gap to close. If a future
  XMRig adds `ADD_FN_ASM(CN_GR_*)`, the port is easy and `build_gr_capi.sh`
  records how.

- **Duplicated native translation units are intentional (item 17).**
  `crypto/common/VirtualMemory.cpp` is compiled by all three shim builds and
  `backend/cpu/Cpu.cpp` by two, which looked like an obvious consolidation. It
  is not worth doing, and the three build scripts now say so:
  - The flag sets genuinely differ — `rx` defines `XMRIG_FEATURE_ASM`, `gr` adds
    `XMRIG_ALGO_GHOSTRIDER`, and `nm` defines neither and compiles `/fp:strict`
    (required by the NeuroMorph port). A shared object would bind all three to
    whichever flags built it first.
  - Each shim links into its own DLL, so there is no ODR concern — only repeated
    work: 3 redundant compiles out of ~68 TUs, of the two smallest files in the
    tree (323 lines combined, ~4%).
  - Consolidating would make three independent builds order-dependent to save
    that 4%.

  `build_nm_capi.bat` was re-run after the edit to confirm the scripts still
  parse (all three remain pure ASCII, the constraint that actually bit us
  before): BUILD OK, byte-identical 38,912-byte DLL.

- **`gr`'s reconnect path, against a real mid-session disconnect (item 3).** Its
  worker lifecycle was restructured earlier (`Task.Run` → dedicated per-session
  threads reaped in a `finally`) and had never been exercised against an actual
  socket drop. Tested by proxying `us-east.flockpool.com:4444` through a local
  forwarder that hard-closes the client socket mid-session and keeps listening,
  so the reconnect can genuinely resume — which pointing at a dead port cannot
  show.

  Two drop/recover cycles, 4 threads: each detected as
  `pool closed connection`, retried after ~2.2–2.5 s (attempt 1 with jitter,
  confirming the attempt counter resets on a successful connect), resubscribed,
  and **accepted a share afterwards** (a/r 1→2→3, 0 rejected). Hashrate was
  ~305 H/s continuously across both.

  No scratchpad leak: private bytes stayed 74–80 MB for the whole run and the
  thread count returned to its baseline of 18. With 4 workers × 16 MiB of
  CryptoNight scratchpads, a session whose workers were not reaped would have
  added ~64 MB per reconnect.

### Changed
- **`btx` solo takes its dashboard slot as a parameter (item 2).** The solo path
  hardcoded metric index `0` at three call sites. That happened to be correct —
  metric slots are indexed by position in the device list and solo mines
  `devices[0]` — but nothing said so, and it is exactly the shape of the rx
  dual-mining bug fixed earlier in this release. The ordinal is now threaded
  through explicitly, so multi-GPU solo (still outstanding) only has to vary it
  rather than hunt down literals.

- **Documented why every solver loop owns a dedicated OS thread (item 4).** An
  audit for `gr`'s original thread-pool-starvation problem found **no other
  instances**: rx, nm, gr, csd, btx and prl all already use `new Thread(...)`
  for their never-yielding hash loops, and every remaining `Task.Run` in the
  miner yields properly (`Task.Delay` loops, `await foreach` over a channel, or
  one-shot probes). The invariant was only written down in `gr`, `csd`, `btx`
  and the orchestrator, so `RxPoolClient`, `RxAlgo` and `NmPoolClient` — which
  were correct but silent — now carry it too. A future "modernise this to
  `Task.Run`" cleanup is how the bug comes back.

- **Command-line parsing extracted to `Config/CommandLine.cs`.** The flag table
  lived in `Program.cs`'s top-level statements, so it could not be referenced
  from a test — the only way to exercise it was to launch the process. It had
  accordingly grown two defects that no one could see (both listed under Fixed).

  `Parse` is now pure: it returns the settings it would apply rather than
  writing them, so the whole table is unit-tested (40 tests) without mutating
  process-global state. `Apply` is the only part that touches the environment.
  `Program.cs`: 1,102 → 931 lines.

- **`ReconnectBackoff` is now used by every algo (item 10).** It existed, was
  documented, and was tested by nothing — while `rx`, `gr`, `nm`, `btx` and
  `csd` each inlined `Math.Min(60, Math.Pow(2, Math.Min(attempt, 6)))`. That is
  the same curve and cap **but with no jitter**, so a pool restart made every
  worker in a fleet retry in lockstep, repeatedly — precisely the thundering
  herd the helper's jitter exists to prevent. `GrStratumClient` had a third
  variant again (2s start, ×2, 30s cap).

  All eight sites now call `ReconnectBackoff.NextDelay(attempt)`, a new wrapper
  that supplies real jitter — which also collapses the
  `(Random.Shared.NextDouble() * 2) - 1` incantation `prl` repeated five times.
  The helper finally has tests (14), covering the exponential ramp, the cap, the
  floor, jitter symmetry and the `ReconnectHint` clamp. Verified live against a
  closed port: 2 → 5 → 9 → 17 → 36s, i.e. the 2/4/8/16/32 base with jitter.

- **Shared stratum transport (`Mining/Stratum/StratumSession.cs`).** Five
  hand-rolled pool clients each reimplemented TCP+TLS connect, newline-delimited
  JSON framing, a write mutex, request-id allocation, id→response correlation,
  timeouts and error mapping — ~70% of 2,829 lines, and the layer that hosted
  every per-algo pool bug found while bringing up `gr` and `nm`. That mechanical
  part now lives in one tested type; each algo keeps only its own job parsing,
  target math and share payloads.

  `StratumJson` replaces four per-algo copies of the JSON helpers and their four
  separate `JsonSerializerContext` declarations — the duplication behind the
  NativeAOT `JsonSerializer` throw, which only ever surfaced at runtime on the
  first share submit against a live pool.

  **All five clients migrated** — `rx`, `nm`, `gr`, `csd`, `btx`. The session
  supports both dialects: the Monero/XMRig login style (`CallAsync`, awaited
  responses) and classic stratum (`SendAsync` plus an unmatched-response
  callback, which is how `gr`/`csd` track fire-and-forget submits). `btx` opts
  out of the `"jsonrpc":"2.0"` member via `jsonRpcVersion: false`, preserving its
  exact wire format — its ninja (LuckyPool) dialect was never sent one, and a
  refactor is the wrong place to find out whether that pool cares.

  Client code: 2,829 → 2,511 lines, against 269 lines of shared transport, so
  the net is only −49. The duplication, not the line count, was the problem: one
  tested implementation now replaces five that had already drifted apart.

  Five latent bugs fell out of the consolidation:
  - `NmPoolClient.CallAsync` never removed its pending-request entry on timeout,
    leaking one dictionary entry per timed-out keepalive for the life of a
    session. The shared version removes it in a `finally`.
  - `GrStratumClient` built `mining.submit`, `mining.subscribe` and
    `mining.authorize` params by raw string interpolation, so a worker name or
    password containing a quote or backslash produced a frame the pool would
    reject or mis-parse. All params now go through `StratumJson`.
  - A `gr` submit that failed to send left its id in `_pendingSubmits` forever,
    since the response it was waiting for could never arrive.
  - `csd` credits shares to a GPU by FIFO, because the pool's ack carries no
    device id. A submit whose send threw left its entry in that queue, shifting
    every later ack by one and mis-crediting shares to the wrong device for the
    rest of the session. The queue became a list so the failed entry (the tail,
    not the head) can be removed.
  - `csd` recorded the submitting GPU inside the stream write lock. With that
    lock now owned by the session, a dedicated submit lock keeps the enqueue and
    the write atomic — otherwise two GPUs submitting concurrently could enqueue
    in one order and write in the other.

  Tested against a real loopback socket rather than a mocked stream — 22 tests
  covering framing, out-of-order correlation, `error` mapping, `"error":null`,
  result-after-document-disposal, malformed frames, timeouts, disconnects and
  the `jsonrpc` opt-out.

  Every client was then verified against its live pool, with a before/after
  baseline for the two GPU algos:
  - `rx` — `xmr.kryptex.network:8029`, share accepted.
  - `nm` — `stratum.cereblix.com:3333`, 9 accepted / 0 rejected.
  - `gr` — `us-east.flockpool.com:4444`, 2 accepted / 0 rejected, exercising
    subscribe → extranonce1, set_difficulty, notify and submit.
  - `csd` — `csd-us-east.lproute.com:8760`, share accepted on 2×Arc B580.
  - `btx` — `btx-us-east.lproute.com:8660`, ninja-dialect fallback still
    triggers, login and job parsing intact, ~72 Mnonce/s per GPU matching the
    pre-migration baseline.

- **One config-precedence chain for all CPU algos (`Config/CpuAlgoConfig.cs`).**
  `rx`, `gr` and `nm` each hand-rolled the same
  `ARC_<ALGO>_*` → `ARC_POOL_CPU_*` → `ARC_POOL_*` cascade, plus TLS-scheme
  sniffing, the dual-mining guard, the thread reserve and the wallet sentinel —
  roughly 80 lines apiece. It had **already drifted**: rx was missing the dual
  guard entirely, which is the third bug listed under Fixed below. There is now
  one implementation, so a fourth CPU algo cannot reintroduce it.

  The three `LoadConfig` methods are now one line each; the algo records keep
  only genuinely algo-specific knobs (rx's `LightMode`/`LargePages`) and expose
  the rest through the shared `CpuAlgoConfig`. `LooksLikePool` and the
  "why am I benchmarking?" message were duplicated too and moved with it.

  The loader takes an injectable environment lookup purely so the precedence
  rules can be unit-tested without mutating process-global state — 40 tests now
  pin them down, including one asserting that *every* prefix gets the dual guard.
  Verified live on all three algos: `ARC_<X>_DUAL=1` with only a shared
  `--pool`/`--wallet` set now refuses and names `--pool-cpu`/`--wallet-cpu`,
  while a single-algo run still inherits the shared flags as before.

### Fixed
- **Dual mining could not load the CPU algo's native library — a race in the
  embedded-lib extraction.** `btx+rx`, `csd+rx`, `btx+gr`, `btx+nm` and friends
  all failed with `<algo>_capi not found next to the miner binary`, while
  `prl+rx` worked. Confirmed on a rig.

  `NativeLibs.EnsureExtracted` published its "done" flag at the *top* of the
  critical section rather than the bottom:

      if (_extracted) return;              // fast path
      lock (_extractLock) {
          if (_extracted) return;
          _extracted = true;               // published BEFORE the work
          ...extract...
          _extractedPath = baseTempDir;    // set only at the end
      }

  Dual mining starts both algos concurrently. The first to need a native library
  took the lock and began extracting; the second arrived a moment later, saw
  `_extracted == true`, returned from the fast path **without taking the lock**,
  read `_extractedPath` as still-null, skipped the extracted-directory probe and
  reported the library missing. The give-away in the logs was the failure landing
  ~1 ms after "dual: starting" — far too fast for a real load attempt.

  `prl` escaped only by luck: its libraries resolve through resolvers registered
  for other assemblies, so its loads serialised differently and missed the
  window. The flag is now `volatile` and published in a `finally` once
  `_extractedPath` is final (the `finally` also keeps a failed extraction from
  retrying forever on every later resolve).

  Verified with an isolated binary and a cold cache each run: `btx+rx` went
  **0/5 → 5/5**, and `csd+rx`, `btx+gr`, `btx+nm` all recovered while `rx` alone
  and `prl+rx` stayed working.

- **No `*_capi` native library could be resolved on Linux.** The P/Invoke names
  are unprefixed (`randomx_capi`), but the Linux build scripts emit the platform
  convention `librandomx_capi.so`. `NativeLibs.Load` probed only the exact
  spelling, so every one of `btx_capi`, `csd_capi`, `randomx_capi`,
  `ghostrider_capi` and `neuromorph_capi` failed — the file sat next to the
  binary, loaded fine under a manual `dlopen`, and the miner still reported
  "randomx_capi not found — this build has no RandomX backend". `dlopen` does
  not add the prefix, and because the resolver throws rather than returning 0,
  .NET's own probing (which *does* try lib-prefixed names) never got a turn.
  `Load` now tries both spellings at each stage. Verified from a clean
  `out-linux` with no symlinks and no `LD_LIBRARY_PATH`: `rx`, `gr` and `nm` all
  pass selftest and benchmark.

- **`--algo nm` was completely dead on Linux.** `build_nm_capi.sh` never
  compiled `crypto/common/VirtualMemory.cpp`, which the shim's huge-page
  allocator calls. A shared object may leave symbols undefined, so the link
  succeeded and the failure only appeared at load time:
  `undefined symbol: _ZN5xmrig13VirtualMemory20freeLargePagesMemoryEPvm`. The TU
  has a full POSIX branch and simply needed compiling (the Windows `.bat` always
  had it). The link now also passes `-Wl,--no-undefined`, so a missing symbol
  fails the build instead of the rig.

- **The Linux `gr` native build was broken on GCC 14+ and could not compile at
  all.** GCC's `ia32intrin.h` defines `_rotr` as a macro expanding to `__rord`,
  so XMRig's own `static inline uint32_t _rotr(...)` fallback in `soft_aes.h`
  became a redeclaration of GCC's `__rord`. On GCC 15 that produced 9 errors —
  one real collision plus eight cascades (`extra_hashes was not declared`,
  `soft_aeskeygenassist was not declared`) that point at the wrong files
  entirely. XMRig guards that fallback with `HAVE_ROTR` precisely so a toolchain
  which already provides `_rotr` can opt out, so `build_gr_capi.sh` now defines
  it. Verified: builds clean on Ubuntu 26.04 / GCC 15.2, selftest OK.

- **`btx` solo never reported worker liveness.** The solo mining loop never
  called `Metrics.TouchHeartbeat`, so its heartbeat stayed at 0 — which the
  dashboard renders as a permanent green "● live". The stall detector was
  therefore inert for solo: a wedged GPU looked perfectly healthy. The loop now
  touches the heartbeat once per completed slice, matching the pool path.

- **`rx` rejected every share on RandomX coins that hash a Bitcoin-style header
  (e.g. Blockzero / `rx/blockzero`).** The Monero/XMRig stratum dialect carries
  two different blob layouts, and `RxPoolClient` assumed the CryptoNote one:

  - Monero and friends send a variable-length hashing blob with the 4-byte
    nonce at offset **39**, of which only the low 3 bytes may be searched (the
    top byte is a NiceHash/proxy extranonce).
  - Blockzero sends an exactly-80-byte Bitcoin header —
    `version|prevhash|merkle|ntime|nbits|nonce` — with the nonce **last, at
    offset 76**, and the full 32 bits available.

  Writing the nonce at 39 into an 80-byte header lands in the middle of the
  merkle root. The miner therefore hashed a corrupted header while the real
  nonce field stayed zero, and the pool — which reconstructs the header with
  the submitted nonce at 76 — computed a completely different hash. Every share
  came back `{"code":23,"message":"low-difficulty"}`, which reads like a
  difficulty or target bug and is not one. The target parsing was correct
  throughout.

  The nonce offset and search width are now derived per job (80-byte blob →
  offset 76, full width; otherwise offset 39, 24-bit), the layout is logged
  with each new job (`blob=80B nonce@76/32`), and `ARC_RX_NONCE_OFFSET` forces
  it if a coin turns up that the heuristic does not cover. A blob too short to
  hold a nonce is now rejected with a clear message instead of indexing out of
  bounds.

  Verified live on `bloz.suprnova.cc:7305`: 9 accepted, 0 rejected over three
  minutes at 7.1 KH/s. Monero re-verified unchanged on
  `xmr.kryptex.network:8029` (`blob=76B nonce@39/24`, 2 accepted, 0 rejected).
  Tests pin both layouts against a *captured* job, not a hand-built header.

- **`rx` submitted shares that vardiff had already invalidated.** Pools may
  tighten the target mid-job, and some (Blockzero/suprnova) reuse the `job_id`
  when they do, so a hash found a moment earlier against the easier target is a
  guaranteed `low-difficulty` reject. Such shares are now dropped before submit,
  which keeps the reject counter meaningful for real faults.

- **`--threads-cpu` was silently ignored by `gr` and `nm`.** It wrote only
  `ARC_RX_THREADS`, a leftover from when the CPU-side flags were RandomX-only,
  but each CPU algo reads its own `ARC_<ALGO>_THREADS`. So the flag worked for
  `rx` and did nothing for the other two — while both of them log
  "override with --threads-cpu" when they auto-reserve cores for the GPU host.

  It now sets a generic `ARC_POOL_CPU_THREADS`, which `CpuAlgoConfig` reads as a
  fallback after the algo-specific variable (same precedence shape as every
  other CPU-side flag). Verified live: `--algo gr … --threads-cpu 3` now mines
  on 3 threads; previously it used all 24.

  `CpuAlgoConfig` also gained `ThreadsExplicit`, so the "reserving N cores"
  message is suppressed when the operator set the count themselves rather than
  claiming a reserve that did not happen.

- **Dead command-line branches.** `--pool-cpu`, `--wallet-cpu`, `--worker-cpu`
  and `--password-cpu` each appeared twice in the parser: once mapping to the
  generic `ARC_POOL_CPU_*` variables, then again — unreachable, behind the same
  `else if` chain — mapping to `ARC_RX_*`. The second set has been removed, and
  a test now asserts the CPU flags never write RandomX-specific variables.

- **Embedded native libs were extracted to one shared cache directory forever,
  so a rebuild silently kept the previous build's DLLs.** The symptom is a
  `DllNotFoundException` or `EntryPointNotFoundException` against a library that
  is plainly present — e.g. `Unable to find an entry point named
  'ghostrider_capi_huge_pages'` after adding that export.

  `NativeLibs.EnsureExtracted` unpacked to `%TEMP%\arc_miner_<gitSha>`, and
  `gitSha` is `unknown` in any working copy without a git repo (the csproj's
  `_EmbedGitSha` target falls back to it), so every build ever produced shared
  a single directory. Two failure modes compounded:
  - Files left behind by an older build with a different lib set were never
    removed, so the miner could load a DLL that build no longer ships.
  - When a target file was locked (another miner instance running — including
    one left over from a previous session), the `IOException` was swallowed with
    "Assume it is fine", keeping the stale bytes.

  The cache directory is now additionally keyed on a cheap fingerprint of the
  binary carrying the resources (its size + last-write time, FNV-1a), so each
  build gets its own. The lock path now verifies the on-disk length against the
  embedded resource and falls through to the fresh-directory fallback on a
  mismatch instead of accepting stale content.

  The fingerprint reads the *assembly's* path rather than
  `Environment.ProcessPath`: under AOT they are the same file, but on a
  framework-dependent run ProcessPath is `dotnet.exe`, whose timestamp never
  changes — which would have quietly reinstated the shared directory.

  Verified by building twice with different embedded payloads and confirming
  each run extracted into its own directory with only its own libs.

- **`--pool-cpu` on its own was not recognised as a stratum pool.** Any CPU algo
  given a pool solely through `--pool-cpu` (no `--pool`) tried to **solo-mine
  against it over HTTP** and died with "An error occurred while sending the
  request" — e.g.
  `--algo rx --pool-cpu stratum+tls://xmr.kryptex.network:8029 --wallet-cpu <addr>`.

  `--pool`/`--pool-cpu` strip the URL scheme, storing host and port separately
  and recording "was it stratum?" in `ARC_POOL_STRATUM` / `ARC_POOL_CPU_STRATUM`.
  By the time the algo classifies the pool, `stratum+tls://host:8029` is
  indistinguishable from a bare `host:8029`, so that flag is the only surviving
  evidence — and all three algos consulted only the **shared** one. With
  `--pool-cpu` alone it was never set, so the URL looked like a bare host:port
  and fell through to the daemon path.

  The pool classification now travels with the pool: the loader records which
  flag supplied the URL and reads the matching hint, so a shared hint can no
  longer leak onto a `--pool-cpu` pool (a different coin on a different pool)
  and vice versa. An explicit scheme still wins over any hint. Verified live
  against `xmr.kryptex.network:8029`: TLS handshake, login, seed init, and
  vardiff job flow all work.

- **`rx` dual-mining: the three bugs already fixed in `gr`.** Under `rx+prl` (or
  `rx+btx`, `rx+csd`) the RandomX side was corrupting the GPU's dashboard row and
  could silently mine to the wrong pool:
  - The SOLO and BENCHMARK paths hardcoded metric index `0` (`SetThroughput`,
    `TouchHeartbeat`, `IncShareAccepted`, `IncShareRejected`). Index 0 is GPU 0
    when dual-mining, so rx and the GPU overwrote each other's hashrate and share
    counts. `RxPoolClient` already used `Metrics.CpuIndex`; the other two paths
    were missed. All three now agree.
  - Solo mining called `Metrics.SetPoolConnected(true)` — the **GPU** pool flag —
    lighting up the GPU row while rx solo-mined. Now `SetCpuPoolConnected`, as
    already fixed in `GrSolo`.
  - `LoadConfig` still fell back to the shared `ARC_POOL_HOST`/`ARC_POOL_WALLET`
    when dual-mining, so `rx+prl` without `--pool-cpu` silently pointed RandomX
    at the Pearl pool with a Pearl address. It now refuses and names the CPU-side
    flags, matching `gr`/`nm`. The same guard was extended to the TLS setting
    (`ARC_RX_TLS`/`ARC_POOL_CPU_TLS` now win; the shared `ARC_POOL_TLS` applies
    only to single-algo runs) and to pool-vs-solo detection, which previously
    read a bare `ARC_POOL_HOST` set by the GPU side as evidence that rx should
    use stratum. Pool detection also now shares `gr`'s rule that an `http(s)://`
    URL is always a solo daemon.
- **Pearl autotune was skipped when PRL was dual-mined.** The `isPrl` gate matched
  only the exact algo name `"prl"`, so `--algo prl+gr` skipped the one-time GEMM
  sweep (and the Pearl wallet requirement) and ran the GPU on default kernel
  knobs. PRL as either half of a pair now counts.
- **Dual-mining device slots could collide.** The CPU algo (`gr`, `rx`) and the
  GPU algo register their dashboard slots concurrently, and `Metrics.Init`
  reallocated every array without relocating an already-registered CPU slot —
  so depending on which algo won the race, the CPU row and GPU 0 wrote over each
  other's hashrate and share counts. `Init` now relocates the CPU slot after the
  GPUs, `SetGpuNames` preserves it instead of truncating the name array, and both
  are guarded by a dedicated lock object rather than a field that gets reassigned
  mid-registration. GhostRider's own metric calls also stopped hardcoding index
  0, which is a real GPU when dual-mining.
- **GhostRider produced hashes no pool would accept.** Two independent bugs, both
  outside the hash itself:
  - *Header byte order.* Bitcoin Stratum sends header fields as big-endian hex
    and the hashed header needs 32-bit words byte-swapped — but not uniformly.
    XMRig swaps only bytes `[0,36)` and `[68,80)` (version, prevhash, ntime,
    nbits, nonce); the merkle root at `[36,68)` stays in its natural sha256d
    order. We were swapping the whole header, so every share the pool recomputed
    came out different.
  - *Share target.* `TargetForDifficulty` inverted cpuminer's `diff_to_target`
    normalization loop (multiplying while `diff < 1` instead of dividing while
    `diff > 1`), yielding a target ~2^64 too tight. The miner found essentially
    no shares at all.
- **Multi-GPU CSD mining.** `--algo csd` now mines on every discrete GPU at once:
  the `csd_capi` device context is `thread_local`, and one dedicated OS thread per
  GPU opens its own device and searches a disjoint slice of the work (partitioned
  by extranonce2). Per-GPU hashrate + accepted/rejected land on the dashboard
  (shares are FIFO-attributed to the submitting GPU). Requires a rebuilt
  `csd_capi` (new `device_name_at` enumeration + `thread_local` context); an old
  lib falls back to single-GPU with a warning. **BTX multi-GPU is not done yet** —
  its solve loops (solo RPC + pool stratum) interleave `await` with the native
  calls, so they must be moved onto dedicated per-GPU threads first; that's the
  next step. BTX remains single-GPU (with the iGPU skip below).
- **Integrated GPUs skipped by default (`--igpu` to include).** Every algo now
  passes over integrated GPUs when auto-selecting a device — PRL/`all`
  enumeration, BTX, CSD, and the PRL autotune sweep — so a rig no longer defaults
  to (or wastes the autotune on) a slow iGPU that happens to be device 0. An
  explicit index (`ARC_GPU_INDICES`, `ARC_BTX_GPU_INDEX`, `ARC_CSD_GPU_INDEX`)
  is always honored; `--igpu` / `ARC_IGPU=1` re-enables iGPUs. If a machine has
  only an iGPU, mining errors with a clear "pass --igpu" message. Detection is by
  device name (discrete Arc A/B-series, GeForce, Radeon vs Intel UHD/Iris/model-less
  Graphics) — no native rebuild required.
- **CPU mining: RandomX (`--algo rx`).** First CPU algo. Binds the upstream
  **tevador/RandomX** library (BSD-3-Clause) through a new `randomx_capi` shim
  (`native/randomx`) and an `RxAlgo` CPU-worker plugin — no XMRig dependency, no
  donation fee.
  - **Solo mining** against a **monerod** node (`--algo rx --pool <host:port>
    --wallet <monero address>`): `get_block_template` → RandomX hash with the
    template's `seed_hash` as the key → cryptonote difficulty check → `submit_block`.
    The worker pool rebuilds RandomX on a seed-epoch change; jobs refresh on new
    blocks. Fast (dataset) or `ARC_RX_LIGHT=1` (cache-only) mode, optional
    `ARC_RX_LARGE_PAGES=1`, `ARC_RX_THREADS`, `ARC_RX_POLL_SEC`.
  - **Benchmark/selftest** when no node/address is set — validates the binding
    against RandomX's canonical vector and reports CPU hashrate on the dashboard.
  - **Verified:** the consensus difficulty check (3M-case fuzz vs a BigInteger
    reference) and the Monero JSON-RPC client (live testnet node). **Not yet
    run end-to-end** (needs the built native lib + a wallet address), and the
    systems tuning that drives competitive hashrate (huge pages beyond the flag,
    MSR, NUMA/affinity) is still to come. Difficulty is read as a `uint64` — fine
    for testnet; mainnet's `wide_difficulty` isn't handled yet. Stratum pool mode
    is future work. The native lib is not vendored; build per
    `native/randomx/README.md` (without it, `--algo rx` exits cleanly).
- **BTX slice (occupancy) calibration.** On the first solo run, BTX now sweeps
  its slice size — the one host-controlled lever, since the BTX kernels have no
  runtime shape knobs — on the first real work item and picks the throughput
  "knee" (smallest slice that reaches the plateau), replacing the fixed
  `1<<18`. The native scan is internally chunked at `1<<20`, so slice size trades
  fixed-cost amortization + K2 pipeline fill against tip-staleness rather than
  TDR risk; the knee balances both. Result is cached per GPU model in
  `btx-tune.conf` (delete to re-calibrate). `maxSolves` now scales with the slice
  so a larger slice can't skip a survivor. Pin `ARC_BTX_SLICE_NONCES` or set
  `ARC_BTX_NO_CALIBRATE=1` to skip; `ARC_BTX_CALIBRATE_MAX_MS` /
  `ARC_BTX_CALIBRATE_KNEE` tune the sweep. Solo path only for now (pool mode
  keeps its difficulty-driven slice tuner).
- **Web-UI control (change pool/wallet/worker/algo).** With `--api-port` **and**
  `--api-password <pw>`, the dashboard gains a settings panel that changes the
  pool, wallet, worker, and algorithm. Since the runtime config is immutable, a
  change is persisted to `~/.arc-miner/control.json` (which thereafter overrides
  the matching CLI flags; delete to revert) and the miner **restarts** to apply
  it — the only path that can switch algorithm. Restart is a built-in self-relaunch
  by default; set `ARC_API_RESTART_MODE=exit` to defer to a supervisor. The
  control endpoint is disabled unless a password is set, **localhost-only** (even
  when the stats API is LAN-visible), password-checked in constant time on every
  change, and CSRF-guarded by a custom auth header; the password is never written
  to disk. The stats API algorithm field is now also reported correctly for btx/csd.
- **Web dashboard.** `--api-port <p>` now serves a self-contained live dashboard
  at `http://localhost:<p>/` (and `/ui`, `/dashboard`) alongside the existing JSON
  and Prometheus endpoints. The whole UI is embedded in the binary — no extra
  install, no build step, no internet — and polls `/api/stats` to show total and
  per-GPU hashrate (with a live sparkline), share accept/reject rate, block finds,
  uptime, pool status/latency, and per-worker heartbeat health. The root `/` is
  content-negotiated: browsers get the dashboard, JSON pollers (Kryptex etc.) that
  request `/` keep receiving JSON, so the stats schema and paths are unchanged.
- **New algorithm: Compute Substrate (`--algo csd`).** A 0%-fee Intel Arc miner
  for CSD (SHA-256d PoW over canonical Bitcoin **Stratum V1**) — the official CSD
  miner is CUDA-only, so this is the first Intel-GPU option. Pool config via the
  shared flags: `--pool [stratum+tcp://|stratum+ssl://]host:port`, `--wallet`
  (40-hex addr20), `--worker`/`--workername`; **TLS** supported. The kernel is a
  straight SYCL SHA-256d search (no XMX, no matrix engine, no sub-group-size
  requirement), so it runs on **any** Intel GPU the driver exposes — the widest
  reach of the three algos. `csd_capi.dll` ships fat (AOT for the four discrete
  dies + a generic `spir64` JIT image); Linux ships JIT-only (`libcsd_capi.so`).

  | Tier | GPUs | Build | Notes |
  |---|---|---|---|
  | **AOT, fast** | Arc B580 / B570 (`bmg-g21`), Arc Pro B60 / B70 (`bmg-g31`) | AOT | **B580 measured 1.58 GH/s** (matches an RTX 3060 Ti's 1.6 GH/s). |
  | **AOT, slower** | Arc A770 / A750 (`acm-g10`), A580 / A380 (`acm-g11`) | AOT | Alchemist. |
  | **Discrete-not-listed / future** | any other discrete Arc, Celestial, etc. | JIT (`spir64`) | Runs via driver JIT. |
  | **iGPU** | Lunar Lake / Meteor & Arrow Lake / Iris Xe … down to UHD 620/630 (Gen9.5) | JIT | **UHD 630 (i7-10750H) measured 12 MH/s.** Novelty only. |
  | **Unsupported** | pre-Skylake (Gen8 and older) | — | No SYCL GPU device. |

  **Reality check:** SHA-256d is ASIC territory — a GPU is orders of magnitude
  off an ASIC. The value here is being first-on-Intel while the coin is young and
  ASIC-free, not competing on efficiency. **Verify any card with
  `csd_fused_check.exe`** (`FUSED CHECK OK (3/3)` = SHA-256d is bit-exact on that
  silicon); update the Intel driver first if a supported card appears to crash.

- **BTX (`--algo btx`) Intel GPU compatibility.** The BTX MatMul-PoW kernels are
  pure integer math (no XMX/DPAS dependency) and the shipped `btx_capi.dll` is a
  fat build — AOT images for the discrete dies **plus a generic `spir64` image**,
  so any Intel GPU the driver exposes to SYCL runs it (by JIT if it's not an AOT
  target). The tuned sub-group GEMM self-guards via `DeviceSupportsSubGroupSize`,
  falling back to a tiled path on any device lacking SIMD16 rather than failing
  to launch. Support tiers (BTX only; contrast Pearl, which is XMX-bound and thus
  discrete-Arc-only):

  | Tier | GPUs | Build | Notes |
  |---|---|---|---|
  | **Tuned, best** | Arc B580 / B570 (`bmg-g21`), Arc Pro B60 / B70 (`bmg-g31`) | AOT | Xe2/Battlemage. **B580 measured ~1.06 KH/s.** |
  | **Full speed, slower** | Arc A770 / A750 (`acm-g10`), A580 / A380 (`acm-g11`) | AOT | Alchemist; ~half the memory bandwidth of Battlemage. |
  | **Discrete-not-listed / future** | any other discrete Arc, Celestial, etc. | JIT (`spir64`) | Runs via driver JIT; a few-second cold-start compile on first launch. |
  | **iGPU — worthwhile** | Lunar Lake (Arc 140V / 130V, Xe2-LPG) | JIT | Best iGPU by a wide margin; the only one likely worth running. |
  | **iGPU — runs, weak** | Meteor/Arrow Lake (Xe-LPG), Iris Xe (Tiger/Alder/Raptor Lake, Xe-LP) | JIT | GEMM-bound on few EUs sharing system RAM; tens of H/s. |
  | **iGPU — novelty** | down to UHD 620/630 (Gen9.5, Skylake–Comet Lake) | JIT | **UHD 630 measured 18–24 H/s** (desktop / i7-10750H). Needs a current Intel driver. |
  | **Unsupported** | pre-Skylake (Gen8 and older) | — | No SYCL GPU device in the Intel compute runtime. |

  Hashrates other than the two measured figures are projections from
  architecture, not benchmarks. **Verify any card with `btx_fused_check.exe`**:
  `FUSED CHECK OK (30/30)` attests the math is element-exact on that silicon (a
  JIT/sub-group problem surfaces as a `FAIL`, never as silent bad shares), and
  the perf probe reports real throughput. If a supported card appears to crash
  at startup, **update the Intel graphics driver first** — a stale OEM driver's
  compute runtime can fail to JIT the kernels.

- **Stratum TLS for BTX and CSD.** Both algos now support `stratum+ssl://` /
  `stratum+tls://` pool URLs (or `--tls`), via a shared wrapper that accepts the
  self-signed / name-mismatched certs mining pools serve, logs the cert SHA-256,
  and bounds the handshake to 15s. Tested against two TLS pools each.
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
  `ARC_AUTOTUNE_ON_FIRST_RUN=0`; skipped if you pin
  `ARC_TGEMM_NB`/`_MB`/`_SEARCH_M`. A cache hit logs the profile and mines
  immediately; a sweep failure falls through to mining with defaults.

### Changed
- **Multi-algo branding.** The banner and `version` output no longer say
  "Pearl (PRL) stratum miner" — now "Multi-algo GPU miner (PRL · BTX · CSD)".
  `--workername` is accepted as an alias for `--worker`.
- **Dashboard: BTX/CSD difficulty and CSD hashrate.** CSD hashrate was reported
  in the wrong throughput slot (dashboard showed 0); both BTX and CSD now publish
  their pool share difficulty to the dashboard difficulty column.

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
  scales with the card's real share rate (`ARC_MINE_TRIGGER_WATCHDOG_K`,
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
- **Share trace** (`ARC_SHARE_TRACE=1`): per-submitted-share diagnostic that
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
