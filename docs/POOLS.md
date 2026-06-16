# Connecting to a Pool

ARC-miner speaks **Stratum** (both the plain client-first dialect and Pearl's
challenge-first `pearl/v1` dialect) and the Akoya **gRPC/V2** protocol. It works
with any Pearl (PRL) pool that exposes one of those.

## General form

```powershell
arc-miner.exe --pool <scheme>://<host>:<port> --wallet <prl1…> --worker <name> [--diff <n>]
```

Schemes:

| Scheme | Transport |
|---|---|
| `stratum+tcp://` , `tcp://` | plain TCP |
| `stratum+ssl://` , `stratum+tls://` , `ssl://` | TLS (certs accepted pool-style; no CA needed) |

- `--wallet` (`-w`) is your Pearl payout address (`prl1…`) — **required**.
- `--worker` (`-n`) is the label the pool shows for this rig (default: machine name).
- `--diff <n>` requests a fixed share difficulty on pools that honour `d=` in the
  stratum password (equivalent to `--password "x;d=<n>"`).

## Confirmed-working examples

> Pools come and go and change their endpoints — always confirm the current host
> and port on the pool's own site. These are starting points, not endorsements.

**HeroMiners** (TLS, challenge-first):
```powershell
arc-miner.exe --pool stratum+tls://ca.pearl.herominers.com:1200 --wallet prl1yourwallet --worker rig01
```

**Kryptex** (TLS):
```powershell
arc-miner.exe --pool stratum+tls://prl-us.kryptex.network:7777 --wallet prl1yourwallet --worker rig01
```

**Generic stratum pool** (plain TCP):
```powershell
arc-miner.exe --pool stratum+tcp://pool.example.com:3360 --wallet prl1yourwallet --worker rig01
```

## Notes & quirks

- **First share latency.** Challenge-first pools (`pearl/v1`) require solving a
  short BLAKE3 connect challenge; the first share can take 20–60 s and you may
  see one reconnect before hashing settles. This is expected.
- **Difficulty.** Most pools manage difficulty automatically (vardiff). If a pool
  starts you too low/high, set `--diff <n>` to request a fixed share difficulty
  (within the pool's allowed range).
- **AlphaPool.** AlphaPool currently validates shares against a per-job target
  that diverges from the standard stratum difficulty path, so a direct connection
  is rejected as "below target". Use a local AlphaPool stratum proxy (e.g. an
  SRBMiner-compatible bridge) and point ARC-miner at the proxy's local port until
  the pool aligns with the standard. See the project notes for details.
- **Akoya pool** is gRPC/V2 (RPC-only), not stratum — connect with the pool host
  and the miner negotiates the V2 protocol automatically.

## Building your own miner / pool integration

The wire protocols are documented so you can build a compatible miner or pool:

- gRPC/V2: [`proto/v2/miner.proto`](../proto/v2/miner.proto)
- Pearl `pearl/v1` challenge handshake: [`docs/PEARL-V1-CHALLENGE.md`](PEARL-V1-CHALLENGE.md)
