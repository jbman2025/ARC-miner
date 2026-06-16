# ARC-miner — User Guide

ARC-miner is a GPU miner for **Pearl (PRL)** built for **Intel Arc** graphics
(B-series / Battlemage and A-series / Alchemist) using the SYCL/XMX backend. It
connects to any Pearl stratum pool, runs the proof-of-work GEMM on the GPU, and
submits shares. **0% dev fee, forever.**

- Proof of work: a low-rank-noised integer matrix-multiply (GEMM). Each
  candidate is a tile of `A·Bᵀ` that is hashed (BLAKE3) and checked against the
  pool's difficulty target.
- Pool-only: there is no solo/direct mining mode.

---

## Quick Start

1. **Unzip** the package anywhere (e.g. `C:\arc-miner`). Keep `arc-miner.exe`
   and all the `.dll` files together in one folder.

2. **Edit the start script** — open `A1start.bat` (or
   `A1start-<POOLNAME>.bat`, e.g. `A1start-alphapool.bat`) in Notepad and set
   **your wallet** and **worker name**:

   ```bat
   set "WALLET=prl1youraddresshere"
   set "WORKER=rig01"
   ```

   If the script is a single command line instead of `set` variables, edit it
   directly:

   ```bat
   arc-miner.exe --pool stratum+tls://ca.pearl.herominers.com:1200 --wallet prl1youraddresshere --worker rig01
   ```

3. **Double-click the .bat** to start mining. Within a minute or two you should
   see a banner, a one-time benchmark, then per-GPU `hashrate=` lines. A B580
   does ~32 TH/s.

That's it. The rest of this guide is reference.

> **First-share delay is normal.** Some pools (challenge-first / pearl/v1, like
> AlphaPool) require a connection challenge to be solved before the first job,
> and vardiff means your first accepted share can take a few minutes. The miner
> may disconnect/reconnect once during this — let it settle.

---

## Command line

```
arc-miner.exe [subcommand] [options]
```

### Subcommands

| Subcommand    | Purpose                                                                 |
|---------------|-------------------------------------------------------------------------|
| `mine-blocks` | Connect to the pool and mine. **This is the default** — you can omit it.|
| `selftest`    | Validate config + pool + native libs, print a JSON report, exit 0/1.    |
| `version`     | Print miner version + git sha and exit.                                 |
| `help`        | Print usage and exit.                                                   |

### Options

| Option | Description |
|--------|-------------|
| `--pool <host:port>` | Pool address. Prefix with `stratum+tls://` (TLS), `stratum+ssl://` (TLS), or `stratum+tcp://` (plaintext). Wrap IPv6 literals in `[ ]`. |
| `--wallet`, `-w <addr>` | Your Pearl wallet address (`prl1…`). **Required.** |
| `--worker`, `-n <name>` | Worker/rig name shown on the pool. Defaults to the machine name. |
| `--tls` / `--no-tls` | Force TLS on/off (default: on). Usually inferred from the `--pool` scheme. |
| `--tls-insecure` | Skip TLS certificate validation (self-signed pools only). |
| `--password`, `-p <pw>` | Stratum password (challenge-first / pearl/v1 pools). Carries the difficulty request, e.g. `x;d=250000`. |
| `--diff <n>` | Request a fixed difficulty `n` (appends `;d=<n>` to the password). |
| `--mpp <count>` | Override the pipelining MatmulsPerPoll. Advanced; normally auto-sized by the benchmark. |
| `--budget <ms>` | Override the startup benchmark target-trigger budget in ms. |
| `--keepalive [sec]` | Enable application-layer stratum keepalive re-auth (off by default; default interval 120s). |
| `--api-port <port>` | Enable the local HTTP stats API (see below). Off by default. |

### Examples

```bat
REM HeroMiners (TLS)
arc-miner.exe --pool stratum+tls://ca.pearl.herominers.com:1200 --wallet prl1... --worker rig01

REM AlphaPool with a fixed difficulty
arc-miner.exe --pool stratum+tcp://us1.alphapool.tech:5566 --wallet prl1... --worker rig01 --diff 250000

REM Mine and expose stats on port 4068
arc-miner.exe --pool stratum+tls://ca.pearl.herominers.com:1200 --wallet prl1... --worker rig01 --api-port 4068

REM Validate the install without mining (exits 0 = OK)
arc-miner.exe selftest
```

---

## Stats API

Pass `--api-port <port>` to expose a local HTTP
server. It binds to localhost and needs no admin rights.

| Endpoint | Format | Notes |
|----------|--------|-------|
| `/api/stats` (also `/`, `/stats`, `/summary`) | JSON | At-a-glance miner/pool/GPU stats. |
| `/metrics` | Prometheus text | Same data plus deeper diagnostics, for Grafana etc. |

Example `GET http://127.0.0.1:4068/api/stats`:

```json
{
  "miner": "arc-miner", "version": "0.2.0", "algorithm": "pearl",
  "uptime_seconds": 129,
  "pool": { "url": "ca.pearl.herominers.com:1200", "worker": "rig01",
            "connected": true, "latency_ms": 0.0 },
  "hashrate_total_hs": 32000000000000.0,
  "shares": { "accepted": 4, "rejected": 0, "block_finds": 0 },
  "gpus": [ { "id": 0, "name": "Intel(R) Arc(TM) B580 Graphics",
              "hashrate_hs": 32000000000000.0, "iter_ms": 79.8,
              "accepted": 4, "rejected": 0, "heartbeat_age_seconds": 0.1 } ]
}
```

- `hashrate_hs` is hashes/sec in the Pearl protocol unit — the same number the
  console prints as `TH/s` (32 TH/s = `3.2e13`).
- `heartbeat_age_seconds` is seconds since that GPU last made progress. Normal
  is under 1s; a growing value means the GPU/driver is wedged.
- Schema is additive-only (fields may be added, never renamed/removed).

---

## Reading the console

```
worker[0] hashrate=32.24 TH/s diff=250.0K iter_ms=68.2 iters/s=14.7 Shares=4 triggers=4 σ_age=30s
```

| Field | Meaning |
|-------|---------|
| `hashrate` | Per-GPU hashrate (the headline number). |
| `diff` | Current pool difficulty (vardiff adjusts this). |
| `iter_ms` | Mean GPU iteration time. Lower is better. |
| `iters/s` | Mining iterations per second. |
| `Shares` | Pool-accepted shares this session. |
| `triggers` | GPU proof-tile hits found. |
| `σ_age` | Seconds since the last σ (job) rotation. |

A periodic `session summary` line rolls up uptime, accepted/rejected, and rig
hashrate.

---



## Troubleshooting

| Symptom | Fix |
|---------|-----|
| `wallet address is required` | Pass `--wallet prl1…`. |
| Exits immediately, DLL load error | Keep `arc-miner.exe` and all `.dll` files in the same folder; install the latest Intel Arc GPU driver. |
| No shares for a few minutes | Normal — vardiff + challenge handshake. Wait; watch for the first `Shares=1`. |
| Connects then drops once at startup | Expected on challenge-first pools (pearl/v1) — let it reconnect. |
| Want to confirm the install works | Run `arc-miner.exe selftest` (exit 0 = healthy; prints JSON). |
| Hashrate shown but pool sees nothing | Check `pool.connected` in `/api/stats`; verify wallet/worker and that the pool's share shape matches (most enforce 131072×131072×4096). |

---

## Supported pools

ARC-miner speaks both Pearl stratum dialects — challenge-first (pearl/v1 BLAKE3
challenge, e.g. AlphaPool) and client-first (plain authorize, e.g. HeroMiners,
suprnova). Point `--pool` at any Pearl pool's stratum endpoint. Use the pool's
own host/port and TLS setting; copy them into your `A1start-<POOLNAME>.bat`.
