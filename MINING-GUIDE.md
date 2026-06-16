# ARC-Miner — Pearl (PRL) mining on Intel Arc GPUs (Windows)

ARC-Miner is a high-performance Pearl miner for Intel Arc graphics cards.
It is self-contained: **no SDKs, no oneAPI, no .NET runtime** — the only
requirement is a current Intel Arc GPU driver.

| | |
|---|---|
| **GPUs** | Intel Arc B-series (B580, B570 …) — A-series (A770/A750/A580) in beta |
| **OS** | Windows 10/11 x64 (Linux build coming) |
| **VRAM** | ~5.7 GB used — 8 GB cards OK |
| **Typical speed** | B580: ~28 TH/s |
| **Fee** | none |

---

## Quick start

1. Install the latest [Intel Arc driver](https://www.intel.com/content/www/us/en/download/785597/).
2. Download and extract the ARC-Miner zip anywhere (e.g. `C:\arc-miner`).
3. Open a terminal in that folder and run:

```powershell
.\arc-miner.exe --pool stratum+tcp://POOL_HOST:PORT --wallet YOUR_PRL_ADDRESS --worker rig01
```

That's it. Within a minute you should see the GPU detected, a ~10 s hashrate
benchmark, `✓ connected & authorized`, and then periodic
`worker[0] hashrate=…` lines. Shares log as `✦ trigger` → `submitting Share`.

### Example (Akoya pool, TLS)

```powershell
.\arc-miner.exe --pool pool-v2.akoyapool.com:443 --tls --wallet prl1yourwallet --worker rig01
```

### Example (generic stratum pool)

```powershell
.\arc-miner.exe --pool stratum+tcp://pool.example.com:3360 --wallet prl1yourwallet --worker rig01
```

`stratum+ssl://` / `stratum+tls://` URLs enable TLS automatically.

---

## Command-line options

| Option | Meaning |
|---|---|
| `--pool <host:port>` | Pool address. Schemes: `stratum+tcp://`, `stratum+ssl://`, `tcp://`, `ssl://` |
| `--wallet`, `-w` | Your Pearl payout address (`prl1…`) — **required** |
| `--worker`, `-n` | Worker name shown on the pool (default: machine name) |
| `--tls` / `--no-tls` | Force TLS on/off (TLS certs are accepted pool-style; no CA needed) |
| `--diff <n>` | Request a share difficulty (pools that take `d=` in the password) |
| `--password`, `-p` | Stratum password, e.g. `x;d=250000` (default `x`) |
| `--keepalive [sec]` | Application-layer keepalive for pools that drop idle connections |

Environment variables (optional):

| Variable | Default | Meaning |
|---|---|---|
| `ARC_POOL_WALLET` / `ARC_POOL_WORKER` | — | Same as `--wallet` / `--worker` |
| `ARC_GPU_INDICES` | `all` | Mine on specific GPUs only, e.g. `0,1` |
| `ARC_LOG_LEVEL` | `Information` | Log verbosity |
| `ARC_SUMMARY_INTERVAL_SEC` | `300` | Interval of the one-line session summary |

---

## Verify your setup

Run the built-in self-test (checks config, GPU libraries, and pool reachability,
then exits — no mining):

```powershell
$env:ARC_POOL_WALLET = 'prl1yourwallet'
.\arc-miner.exe selftest      # prints JSON; "overall":"pass" = ready
.\arc-miner.exe version
```

---

## Reading the output

```
benchmark[0]: … 27.97 TMADs/s …           ← startup hashrate sample
✓ connected & authorized — pool=…         ← pool session up
worker[0]: σ install job=…                ← received work
worker[0] hashrate=27.74 TH/s iter_ms=78  ← steady-state, every 30 s
worker[0]: ✦ trigger at iter=…            ← share found
stratum: submitting Share (job=…)         ← share sent to pool
session uptime=… accepted=… rejected=…    ← rollup every 5 min
```

---

## Troubleshooting

**GPU not found / benchmark fails immediately** — update the Intel Arc driver;
that is the only system dependency. Verify the card shows in Device Manager.

**`install_B rc=-100` on Arc A-series (A770/A750/A580)** — you are running a
build older than rev 2. A-series cards use different XMX matrix shapes than
B-series; rev 2+ carries both and selects automatically. Update your download.

**`fast A-proof path unavailable` warning in the log** — harmless to shares
but worth reporting; please send that exact log line to the developer.

**High "Shared GPU memory" in Task Manager** — fixed in current builds
(≤0.1 GB). If you see ~1 GB shared after the first share, update your download.

**Pool shows the worker connecting and dropping** — check the wallet address
format (`prl1…`) and that the port matches the pool's stratum port. Add
`--keepalive 90` for pools behind aggressive NAT/firewalls.

**Challenge-first pools (e.g. AlphaPool)** — connect directly, no proxy or
shim needed. These pools validate new connections with a small proof-of-work
challenge; the miner detects and solves it automatically (a few seconds —
you'll see `solving pearl/v1 connection challenge` then `✓ challenge solved`).
Request your difficulty with `--diff`:
```powershell
.\arc-miner.exe --pool stratum+tcp://us1.alphapool.tech:5566 --wallet prl1yourwallet --worker rig01 --diff 250000
```

**Crash diagnostics** — set `DOTNET_DbgEnableMiniDump=1` before launching and
the miner writes a crash dump on fatal errors; `last-fatal.log` is written
regardless (in `%USERPROFILE%\.arc-miner\dumps`).

---

## Performance notes

- Hashrate is reported over the **swept search window**, the same convention
  the pool uses for share accounting. Tools that count the full committed
  matrix overstate by ~32× — if another miner claims a wildly higher number on
  the same card, check which convention it uses.
- VRAM use is ~5.7 GB regardless of card size; system RAM use is ~0.3 GB.
- Expect the first share within a few minutes at typical pool difficulty;
  the pool-side vardiff then adjusts.

---

## Antivirus note

Mining software is routinely flagged by antivirus heuristics. The miner is a
plain console application — no installer, no services, no system modification.
If your AV quarantines it, add an exclusion for the extracted folder.
