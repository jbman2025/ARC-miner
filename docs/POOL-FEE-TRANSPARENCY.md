# Mining Pool Fee Transparency — `pool-info/v1`

**Status:** Draft / Request for Comments
**Author:** ARC-miner project
**Date:** 2026-06-15
**Audience:** pool operators, miner authors

---

## 1. Abstract

This document proposes a small, optional, backwards-compatible mechanism for a
mining pool to **advertise its fee and payout terms to a connecting miner**, and
for the miner to **display and (optionally) verify** those terms at startup.

It defines:

1. A **`PoolInfo` object** — a compact, versioned JSON description of a pool's
   fee, payout scheme, and payout terms.
2. Two **transports** for delivering it: an in-band Stratum capability
   (`pool-info/v1`) and an out-of-band HTTP `.well-known` fallback.
3. A **trust ladder** so a displayed number carries a meaningful confidence
   level rather than blind faith in self-reporting.

The goal is a community standard that any pool and any miner can adopt
independently and incrementally.

## 2. Motivation

Pool fees and payout schemes are commercially material to miners, yet there is
no standard machine-readable way for a pool to state them over the mining
protocol. Today miners learn fees from a pool's website, word of mouth, or
miner-bundled assumptions — none of which the mining client can show at connect
time, and none of which are verifiable.

Two concrete harms follow: (a) miners cannot see, at the moment they point
hashrate at a pool, what that pool charges and under which scheme; and (b) there
is no agreed channel for honest pools to *prove* low fees as a differentiator.

A self-reported number alone is weak (a pool can under-state its fee). This
proposal therefore pairs the transport with a **graduated trust model**, so the
ecosystem can start with simple self-reporting and ratchet toward verifiable
claims without a flag day.

## 3. Terminology

The key words **MUST**, **MUST NOT**, **SHOULD**, **SHOULD NOT**, and **MAY** are
to be interpreted as described in RFC 2119.

- **Pool** — the Stratum server the miner connects to.
- **Miner** — the mining client.
- **PoolInfo** — the JSON object defined in §6.
- **Software dev fee** — a fee taken by the *miner software* (distinct from the
  pool fee). ARC-miner's is 0%. A miner **MUST** display these two fees
  separately and never conflate them.

## 4. Design goals

1. **Optional & backwards-compatible.** A pool or miner that does not implement
   this proposal interoperates unchanged. Absence is a defined state
   (`not advertised`), never an error.
2. **Cheap to adopt.** A pool can comply by serving one static file.
3. **Versioned.** The schema string (`pool-info/v1`) gates all parsing.
4. **Transport-agnostic payload.** The same `PoolInfo` object is delivered
   identically whether in-band or out-of-band.
5. **Honest about confidence.** The miner displays *which* trust level a number
   came from; it never presents an unverified figure as verified.

## 5. Transport

A miner **SHOULD** attempt transports in this order and use the first that
yields a valid `PoolInfo`: (A) Stratum extension, then (B) HTTP `.well-known`.
If neither yields a result, the miner displays `pool fee: not advertised`.

### 5.1 Transport A — Stratum capability `pool-info/v1`

Capability negotiation reuses the existing `configure`/`mining.configure`
handshake. The miner advertises support by including `"pool-info/v1"` in the
configure parameter list; a supporting pool echoes the capability and then
returns or pushes a `pool.info` message carrying the `PoolInfo` object.

#### 5.1.1 pearl/v1 (challenge-first) dialect

In the pearl/v1 handshake the pool speaks first with a `pearl.challenge`; the
miner solves it, then negotiates capabilities. To request pool info the miner
adds the capability token to its `configure`:

```
→  {"id":1,"method":"mining.configure","params":[["pearl/v1","pool-info/v1"]]}
←  {"id":1,"result":{"pearl/v1":true,"pool-info/v1":true},"error":null}
```

A pool that accepted `pool-info/v1` then **MUST** send exactly one `pool.info`
notification before, or interleaved with, the first `mining.notify`:

```
←  {"id":null,"method":"pool.info","params":[ <PoolInfo JSON object> ]}
```

A pool that does not support the capability simply omits it from the `result`
map (or returns the legacy boolean `result`), and never sends `pool.info`. The
miner treats that as `not advertised` and proceeds normally.

#### 5.1.2 Plain Stratum (client-first) dialect

```
→  {"id":1,"method":"mining.configure","params":[["pool-info/v1"],{}]}
←  {"id":1,"result":{"pool-info/v1":true},"error":null}
←  {"id":null,"method":"pool.info","params":[ <PoolInfo JSON object> ]}
```

The `pool.info` notification **MUST** be sent after a successful
`mining.authorize` and **MAY** be re-sent at any time to signal a change (e.g. a
fee or minimum-payout update); the miner **MUST** treat the most recent
`pool.info` as authoritative and **SHOULD** log a change.

#### 5.1.3 Notification semantics

- `pool.info` is a server→client notification (`id: null`).
- `params` is a single-element array containing one `PoolInfo` object.
- The miner **MUST** ignore a `pool.info` whose `schema` it does not recognise
  and continue mining.

### 5.2 Transport B — HTTP `.well-known` fallback

A pool **MAY** publish the same object at a stable, well-known URL derived from
the Stratum host:

```
https://<pool-host>/.well-known/mining-pool-info.json
```

Requirements:

- The response **MUST** be the `PoolInfo` object (§6), `Content-Type:
  application/json`, served over HTTPS with a valid certificate.
- The miner **SHOULD** request it once at startup with a short timeout (e.g.
  ≤2 s) and **MUST NOT** block mining on it.
- The miner **SHOULD** cache it briefly and **MUST** treat a fetch failure as
  `not advertised`.

This transport lets a pool comply with a single static file and no Stratum-code
changes.

## 6. The `PoolInfo` object

```json
{
  "schema": "pool-info/v1",
  "pool_name": "ExamplePool",
  "fee_percent": 0.9,
  "payout_scheme": "PPLNS",
  "min_payout": "0.1",
  "payout_interval_sec": 7200,
  "tx_fee_paid_by": "pool",
  "operator_url": "https://examplepool.tld",
  "updated": "2026-06-15T00:00:00Z",
  "key_id": "ed25519:9f86d0...",
  "signature": "base64(ed25519-sig over canonical bytes)"
}
```

| Field | Type | Req. | Meaning |
|---|---|---|---|
| `schema` | string | MUST | Exactly `"pool-info/v1"`. Gates parsing. |
| `pool_name` | string | SHOULD | Human-readable pool name. |
| `fee_percent` | number | MUST | Pool fee as a percentage, e.g. `0.9` = 0.9 %. `0` is valid. |
| `payout_scheme` | string | MUST | One of `PPS`, `PPS+`, `PPLNS`, `SOLO`, `PROP`, `OTHER`. |
| `min_payout` | string | SHOULD | Minimum payout in coin units, decimal **string** to avoid float drift. |
| `payout_interval_sec` | number | MAY | Nominal payout cadence in seconds. |
| `tx_fee_paid_by` | string | MAY | `pool` or `miner` — who bears the on-chain payout tx fee. |
| `operator_url` | string | MAY | Pool homepage. |
| `updated` | string | MUST | RFC 3339 timestamp of last change to this object. |
| `key_id` | string | MAY | Identifier of the signing key (see §7.2). Required iff `signature` present. |
| `signature` | string | MAY | Detached signature over the canonical object (see §7.2). |

Rules:

- Numbers **MUST NOT** be used for monetary amounts; use decimal strings
  (`min_payout`). `fee_percent` and `payout_interval_sec` are dimensionless/seconds
  and **MAY** be numbers.
- The miner **MUST** reject and treat as `not advertised` any object missing a
  MUST field, with an out-of-range `fee_percent` (`< 0` or `> 100`), or with an
  unrecognised `schema`.
- `payout_scheme` is displayed alongside `fee_percent` because effective cost
  depends on it (0 % PPS ≠ 0 % PPLNS).

## 7. Trust model

A displayed fee is only as useful as the confidence attached to it. A miner
**MUST** attach exactly one **trust level** to every displayed `PoolInfo` and
**MUST** show it to the user.

### 7.1 Trust levels

| Level | Name | Basis | Display label |
|---|---|---|---|
| L0 | Self-reported | Object received, no signature | `(advertised)` |
| L1 | Signed | Valid `signature` from a known `key_id` | `(signed)` |
| L2 | Registry-checked | Matches a community registry entry (§7.3) | `(verified: registry)` / `(⚠ mismatch)` |
| L3 | Audited | Effective fee derived from observed payouts (§7.4) | `(effective: N%)` |

L0 is the floor. Higher levels are additive and optional; a miner that
implements only L0 is conformant.

### 7.2 L1 — signed advertisements

- The canonical form is the `PoolInfo` object **excluding** the `signature`
  field, serialised per RFC 8785 (JSON Canonicalization Scheme).
- `signature` is the base64 detached signature of those canonical bytes.
- The recommended algorithm is **ed25519**; `key_id` is `ed25519:` followed by
  the lowercase hex SHA-256 of the public key.
- The miner verifies `signature` against the public key resolved from `key_id`
  (bundled set and/or community registry, §7.3). Verification proves the value
  is **authentic and committed** by the keyholder; it does **not** prove the
  pool's *behaviour* matches — see §8.

### 7.3 L2 — community registry

A community-maintained, openly hosted JSON (e.g. a Git repository) maps a
Stratum endpoint to its expected terms and signing key:

```json
{
  "us1.examplepool.tld:5566": {
    "fee_percent": 0.9,
    "payout_scheme": "PPLNS",
    "pubkey": "base64(ed25519 public key)",
    "source": "https://examplepool.tld/fees",
    "updated": "2026-06-15"
  }
}
```

- The miner **MAY** bundle a registry snapshot and **MAY** refresh it.
- The miner cross-checks the advertised `PoolInfo` against the registry entry
  and displays `verified: registry` on match or a prominent `⚠ mismatch` (with
  both values) on disagreement.
- The registry also covers pools that advertise nothing in-band: the miner can
  still show a community-sourced figure labelled `(registry)`.

### 7.4 L3 — effective-fee audit (informative, future work)

A miner knows the difficulty of every accepted share and can therefore compute
an **expected** reward over a window; comparing that against **observed on-chain
payouts** to the configured address yields the *effective* fee actually taken.
This is the only level that verifies behaviour rather than claims. It is a
longer-running, statistical feature (out of scope for a startup banner) and is
described here only to anchor the trust ladder's endpoint.

## 8. Security considerations

- **Self-reporting is unverifiable behaviourally.** L0/L1 prove what a pool
  *says/commits*, not what it *does*. The UI labels exist precisely so users do
  not over-trust L0.
- **Signature scope.** A signature binds the advertised values to a key; it does
  not bind the key to honest operation. Key compromise or a dishonest operator
  defeats L1; L2/L3 mitigate by external corroboration.
- **Transport integrity.** Transport A inherits the security of the Stratum
  connection (use TLS). Transport B **MUST** use HTTPS with certificate
  validation; a miner **MUST NOT** accept `.well-known` info over plaintext HTTP.
- **Downgrade.** An attacker who can strip the capability or block the
  `.well-known` fetch forces `not advertised`; L2 (registry) resists this by
  supplying a value independent of the live connection.
- **Resource use.** `pool.info` is small and infrequent; miners **SHOULD** rate-
  limit and bound the size of accepted objects.

## 9. Miner behaviour & display

At startup, after the trust level is resolved, the miner **MUST** present the
pool fee and the software dev fee distinctly, e.g.:

```
Software dev fee : 0%
Pool fee         : 0.9%  PPLNS  (signed)   min payout 0.1
```

- On `not advertised`: `Pool fee : not advertised`.
- On registry mismatch: a single conspicuous line, e.g.
  `Pool fee : ⚠ advertised 0.5% but registry says 1.5% — see <source>`.
- The miner **SHOULD** also expose the resolved `PoolInfo` and trust level in any
  machine-readable stats output (e.g. a stats API), so dashboards can surface it.
- The miner **MAY** offer a policy knob (e.g. warn or refuse to start if
  `fee_percent` exceeds a user threshold, or if trust level is below a
  configured minimum).

## 10. Backwards compatibility

- Legacy pools: never send the capability/file → miners show `not advertised`.
- Legacy miners: never request the capability → pools simply do not send
  `pool.info`; an unsupported notification is ignored per existing Stratum
  practice.
- No existing message semantics change; `pool.info` and the capability token are
  purely additive.

## 11. Adoption & rollout

1. **Publish** this spec and a reference parser (open source) in ARC-miner.
2. **L0 first:** pools opt in with the `.well-known` file (zero protocol work);
   miners display `(advertised)`.
3. **Seed the registry** (L2) from public pool fee pages, so the feature is
   useful even before pools adopt the in-band transport.
4. **Add signing** (L1) and the in-band Stratum capability as pools engage.
5. **L3** as a later, opt-in analytics feature.

Incentive alignment: low-fee pools gain a verifiable marketing signal by
adopting; pools that decline simply read as `not advertised`, and the registry
fills the gap — so the floor of useful information does not depend on universal
adoption.

## 12. Open questions

- Registry governance: who curates it, and how are pool signing keys vetted /
  rotated / revoked?
- Multi-coin / multi-port pools: one object per endpoint, or a list?
- Should `fee_percent` decompose (e.g. pool fee vs. solo-luck vs. tx-fee
  handling) for PPS+ style schemes, or stay a single headline number with
  `payout_scheme` as the qualifier?
- Canonicalization choice (RFC 8785 vs. a simpler sorted-minified form) for
  signing — pick the one with the widest small-footprint library support.

## Appendix A — end-to-end pearl/v1 example

```
←  {"method":"pearl.challenge","params":{"seed":"<64hex>","difficulty":32}}
→  {"id":1,"method":"mining.configure","params":[["pearl/v1","pool-info/v1"]]}
←  {"id":1,"result":{"pearl/v1":true,"pool-info/v1":true},"error":null}
→  {"id":2,"method":"pearl.challenge_response","params":{"nonce":"<016x>","seed":"<64hex>"}}
→  {"id":3,"method":"mining.subscribe","params":["ARC-miner/0.2.0"]}
←  {"id":3,"result":[...],"error":null}
→  {"id":4,"method":"mining.authorize","params":["wallet.worker","x;d=250000"]}
←  {"id":4,"result":true,"error":null}
←  {"id":null,"method":"pool.info","params":[{
      "schema":"pool-info/v1","pool_name":"ExamplePool","fee_percent":0.9,
      "payout_scheme":"PPLNS","min_payout":"0.1","updated":"2026-06-15T00:00:00Z",
      "key_id":"ed25519:9f86d0...","signature":"<base64>"}]}
←  {"id":null,"method":"mining.notify","params":[ ... ]}
```

Resulting banner (signature verified against a known key):

```
Software dev fee : 0%
Pool fee         : 0.9%  PPLNS  (signed)   min payout 0.1
```

## Appendix B — Reference implementation sketch (ARC-miner)

Illustrative, not drop-in. File anchors refer to ARC-miner's current layout so an
implementer can see *where* each piece lands. The same shape applies to any
Stratum client.

### B.1 Data model

```csharp
public enum PoolInfoTrust { NotAdvertised, SelfReported, Signed, RegistryVerified, RegistryMismatch }

public sealed record PoolInfo(
    string  Schema,            // must be "pool-info/v1"
    string? PoolName,
    double  FeePercent,
    string  PayoutScheme,      // PPS | PPS+ | PPLNS | SOLO | PROP | OTHER
    string? MinPayout,         // decimal STRING, never a float
    long?   PayoutIntervalSec,
    string? TxFeePaidBy,
    string? OperatorUrl,
    string  Updated,
    string? KeyId,
    string? Signature)
{
    public PoolInfoTrust Trust { get; init; } = PoolInfoTrust.SelfReported;

    public static bool TryParse(ReadOnlySpan<byte> json, out PoolInfo? info)
    {
        // 1. Deserialize. 2. Reject if Schema != "pool-info/v1".
        // 3. Reject if any MUST field missing or FeePercent < 0 || > 100.
        // On any failure: info = null, return false (caller → NotAdvertised).
        info = null; return false; // sketch
    }
}
```

### B.2 Capability negotiation (`StratumSession.ConnectAsync`)

The configure step already exists for pearl/v1. Add the token and remember
whether the pool echoed it:

```csharp
// was: params [["pearl/v1"]]
var configure = """{"id":1,"method":"mining.configure","params":[["pearl/v1","pool-info/v1"]]}""";
await WriteLineAsync(configure, ct);

// In the configure-result handler:
//   _poolInfoCapable = result.TryGetProperty("pool-info/v1", out var v) && v.GetBoolean();
// Nothing else required at handshake time — the pool will PUSH pool.info.
```

For client-first pools, send the same capability in `mining.configure` before
`mining.authorize`; the rest is identical.

### B.3 Inbound handler (`StratumSession` read loop)

Mirror the existing `else if (msg.Method == "mining.set_difficulty")` arm:

```csharp
else if (msg.Method == "pool.info" && msg.Params is JsonElement pi
         && pi.ValueKind == JsonValueKind.Array && pi.GetArrayLength() >= 1)
{
    if (PoolInfo.TryParse(pi[0].GetRawText().AsBytes(), out var info) && info is not null)
    {
        info = ResolveTrust(info);              // §B.5
        Volatile.Write(ref _poolInfo, info);    // latest wins; re-send = update
        if (_callbacks?.OnPoolInfo is not null)
            await _callbacks.OnPoolInfo(info).ConfigureAwait(false);
    }
    // Unrecognised schema / parse failure: ignore and keep mining.
}
```

### B.4 `.well-known` fallback

Run once after connect *only if* `!_poolInfoCapable`, bounded and non-blocking:

```csharp
// host from the stratum endpoint; HTTPS + cert validation REQUIRED.
var url = $"https://{_host}/.well-known/mining-pool-info.json";
using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
cts.CancelAfter(TimeSpan.FromSeconds(2));
try {
    var bytes = await _http.GetByteArrayAsync(url, cts.Token);
    if (PoolInfo.TryParse(bytes, out var info) && info is not null)
        await _callbacks!.OnPoolInfo(ResolveTrust(info));
} catch { /* fetch failure → NotAdvertised; never block mining */ }
```

### B.5 Trust resolution

```csharp
private PoolInfo ResolveTrust(PoolInfo info)
{
    var trust = PoolInfoTrust.SelfReported;

    // L1: verify ed25519 signature over the RFC 8785 canonical form
    //     (object minus "signature"), key resolved from info.KeyId.
    if (info.Signature is not null && info.KeyId is not null
        && Ed25519Verify(info.KeyId, Canonicalize(info), info.Signature))
        trust = PoolInfoTrust.Signed;

    // L2: cross-check against bundled/refreshed community registry by endpoint.
    if (Registry.TryGet($"{_host}:{_port}", out var reg))
        trust = Matches(info, reg) ? PoolInfoTrust.RegistryVerified
                                   : PoolInfoTrust.RegistryMismatch;

    return info with { Trust = trust };
}
```

### B.6 Callback + display wiring

Extend the session callback record (alongside `OnJob` / `OnShareResult` /
`OnVardiff`) and surface it where the startup banner is logged:

```csharp
// MiningSessionCallbacks
public Func<PoolInfo, ValueTask>? OnPoolInfo { get; init; }

// WorkerOrchestrator — next to the existing "0% dev fee" banner:
OnPoolInfo = info =>
{
    Metrics.SetPoolInfo(info);          // → exposed in the --api-port stats JSON
    _log.LogInformation(
        "Pool fee : {Fee:0.##}%  {Scheme}  ({Trust}){Min}",
        info.FeePercent, info.PayoutScheme, TrustLabel(info.Trust),
        info.MinPayout is null ? "" : $"   min payout {info.MinPayout}");
    if (info.Trust == PoolInfoTrust.RegistryMismatch)
        _log.LogWarning("Pool fee : ⚠ advertised value disagrees with community registry — verify with the pool.");
    return ValueTask.CompletedTask;
};
```

The software dev-fee line stays exactly as today; the pool line is purely
additive, so a non-advertising pool yields the unchanged banner plus
`Pool fee : not advertised`.
