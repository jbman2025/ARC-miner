# Pearl/v1 Connection Challenge — Protocol & Implementation

A technical reference for the BLAKE3 proof-of-work **connection challenge** that
some Pearl stratum pools (e.g. AlphaPool) require before a worker is authorized.
This document is implementation-agnostic: it specifies the wire protocol and the
exact hash construction so the same handshake can be implemented in any client
(miner, proxy, `prl-proxy`, etc.).

Everything here was derived empirically by capturing a live handshake between a
working client and a production pool, then reproducing the accepted nonce
offline against a reference BLAKE3. The captured test vector at the end lets you
self-check any implementation before going near a pool.

---

## 1. Why it exists

The challenge is a connection-admission gate. Before the pool will hand out work,
the client must spend a small, bounded amount of CPU solving a proof-of-work over
a pool-chosen random seed. It deters connection-flooding and trivially-cheap
worker churn. It is **not** a mining share — it is solved once at connect (and
occasionally re-issued mid-session), is CPU-cheap, and carries no reward.

Default difficulty observed in the wild is **32 bits** (~4 billion hashes,
seconds of CPU). Implementations MUST treat the difficulty as attacker-influenced
and cap it (see §6).

---

## 2. Wire protocol

Line-delimited JSON-RPC over the stratum TCP (or TLS) socket, one JSON object per
`\n`-terminated line — identical framing to ordinary stratum.

### 2.1 Handshake ordering — the pool speaks first

The single most important behavioural fact: on a challenge-first pool, **the pool
sends the first line immediately after the TCP/TLS connection is established**,
before the client sends anything. This is the inverse of classic stratum (where
the client opens with `mining.subscribe`/`mining.authorize`). It is also the
detection signal — "the first inbound line is a `pearl.challenge`" is how a
client distinguishes a challenge-first pool from a legacy one with zero
configuration.

```
client                                   pool
  │  ── TCP connect ──────────────────▶   │
  │  ◀──────── pearl.challenge ─────────  │   (pool speaks first)
  │  ──── pearl.challenge_response ────▶  │
  │  ──── mining.configure ───────────▶   │
  │  ──── mining.subscribe ───────────▶   │
  │  ──── mining.authorize ───────────▶   │
  │  ◀──────── results + set_difficulty   │
  │  ◀──────── pearl.set_mining_params    │
  │  ◀──────── mining.notify (jobs) ────  │
```

### 2.2 `pearl.challenge` (pool → client)

```json
{"id":null,"method":"pearl.challenge","params":{"seed":"<64 hex chars>","difficulty":32}}
```

| Field | Type | Meaning |
|---|---|---|
| `params.seed` | string, 64 hex chars | 32 random bytes chosen by the pool. |
| `params.difficulty` | integer | Required number of **leading zero bits** in the hash. |

### 2.3 `pearl.challenge_response` (client → pool)

```json
{"id":1,"method":"pearl.challenge_response","params":{"nonce":"<16 hex chars>","seed":"<same 64 hex>"}}
```

| Field | Type | Meaning |
|---|---|---|
| `params.nonce` | string, 16 hex chars | The winning `u64` nonce, formatted as zero-padded 16-digit hex, most-significant nibble first (e.g. `000000002cc663e5`). |
| `params.seed` | string | Echo of the challenge seed, so the pool can match the response to the challenge it issued. |

The pool replies with a normal JSON-RPC result keyed by the request `id`:
`{"id":1,"result":true,"error":null}` on success, `result:false` (or an `error`)
on rejection (wrong nonce, or the challenge expired — see §6).

### 2.4 Rest of the handshake (standard stratum, for completeness)

After the challenge response the client proceeds with ordinary stratum. From a
captured AlphaPool session:

```
→ {"id":2,"method":"mining.configure","params":[["pearl/v1"],{}]}
→ {"id":3,"method":"mining.subscribe","params":["<agent>/<version>"]}
→ {"id":4,"method":"mining.authorize","params":["<wallet>.<worker>","x;d=250000"]}
← {"id":2,"result":{"pearl/v1":true,"pearl/v1.share_format":"base64", …}}
← {"id":3,"result":[[["mining.set_difficulty","conn-…"],["mining.notify","conn-…"]],"",0]}
← {"method":"pearl.set_mining_params","params":[{"m":131072,"n":131072,"k":4096,"rank":128,"rows_pattern":[…],"cols_pattern":[…],"mma_type":"Int7xInt7ToInt32"}]}
← {"id":4,"result":true}
← {"method":"mining.set_difficulty","params":[250000]}
← {"method":"mining.notify","params":[ … ]}
```

Two pool-specific details worth noting because they differ from object-style
Pearl stratum:

- **Difficulty in the password.** The authorize password field carries the
  requested share difficulty as `x;d=<N>` (the `x` is a conventional throwaway
  username-password value; `;d=250000` requests difficulty 250000). The pool
  honours this directly — no separate RPC.
- **Positional `mining.notify` and `mining.submit`.** Jobs arrive as a JSON
  array `["job_id","prev_hash","header_hex",height, …]` (the share target is NOT
  in the notify — derive it from the most recent `mining.set_difficulty`).
  Share submissions are positional too: `params:["<wallet>.<worker>","<job_id>","<proof_b64>"]`.

---

## 3. The hash construction (the core of it)

A `u64` nonce **wins** when:

```
BLAKE3( seed[0..32]  ‖  nonce_as_8_bytes_LITTLE_ENDIAN )   has  ≥ difficulty leading-zero BITS
```

Spelled out precisely:

1. **Input message** = 40 bytes: the 32 raw seed bytes (decoded from the
   challenge's hex), followed by the nonce written as 8 bytes **little-endian**
   (least-significant byte first).
2. **Hash** = plain BLAKE3 (the default hash mode — *not* keyed, *not* derive-key,
   no XOF; the standard 32-byte output).
3. **Leading-zero bits** are counted over the BLAKE3 output as a **byte stream**:
   the first output byte is the most significant. Count zero bits from the top:
   a fully-zero leading byte is 8 zero bits, then `clz` of the first non-zero
   byte. A nonce passes iff that count is `≥ difficulty`.

### 3.1 Endianness — the two traps

These are the two places a re-implementation will silently fail:

- **Nonce → hash bytes is LITTLE-ENDIAN.** Nonce `0x2cc663e5` becomes bytes
  `e5 63 c6 2c 00 00 00 00`. Big-endian here yields a completely different
  (losing) hash. (Confirmed: of the eight candidate constructions tried during
  reverse-engineering, only `seed ‖ nonce_LE` reproduced the pool's accepted
  nonce.)
- **Nonce → wire string is BIG-ENDIAN hex.** The *same* nonce is transmitted in
  `pearl.challenge_response` as `"000000002cc663e5"` — i.e. the u64 printed as
  16-digit hex, most-significant first. So the byte order fed to the hash and the
  text order put on the wire are *opposite*. Keep them separate in your code.

- **Leading-zero counting is over the BYTE stream**, equivalently the first
  output `u32` word **byte-reversed** then `clz`. If you read the output as
  native-endian `u32`s and `clz` directly, you'll be wrong on little-endian
  machines.

### 3.2 Reference pseudocode

```text
function solve(seed_bytes[32], difficulty):
    for nonce in 0, 1, 2, …:
        msg = seed_bytes ++ u64_to_le_bytes(nonce)     # 40 bytes
        h   = blake3(msg)                              # 32 bytes, default mode
        if leading_zero_bits(h) >= difficulty:
            return nonce                               # send as %016x hex

function leading_zero_bits(h[32]):
    n = 0
    for b in h:                                        # h[0] is most significant
        if b == 0: n += 8; continue
        return n + clz8(b)                             # clz of an 8-bit value
    return n                                           # all-zero (degenerate)

function verify(seed_bytes, nonce, difficulty):
    return leading_zero_bits(blake3(seed_bytes ++ u64_to_le_bytes(nonce))) >= difficulty
```

---

## 4. How we added it to the miner

The reference implementation lives in two files:

- **`src/Akoya.Crypto/ChallengeSolver.cs`** — the solver and verifier.
- **`src/Akoya.Pool/StratumSession.cs`** — protocol detection and the handshake.

### 4.1 Reverse-engineering method (reproduce this for any pool)

We did not disassemble anything. We owned both ends of an existing working
client's conversation, so we put a **line-logging TCP relay** between the client
and the pool and captured the full handshake JSON, including a known-good
`(seed, nonce, difficulty)` triple. Then a tiny harness tried the plausible hash
constructions against that triple — `seed‖nonce_le`, `seed‖nonce_be`,
`nonce‖seed`, ASCII-hex concatenations, keyed variants — and exactly one
(`seed ‖ nonce_le`, default BLAKE3) reproduced the accepted nonce's 33 leading
zero bits. That promoted the construction from "guess" to "confirmed", and the
captured triple became the permanent regression vector in §7.

A minimal relay is ~60 lines: accept on a local port, dial the pool, copy bytes
both ways, and append each `\n`-delimited line to a log with a direction tag.

### 4.2 Solver

`ChallengeSolver.Solve(seed, difficulty, ct)`:

- Spawns one thread per logical core; thread *t* scans the strided nonce
  sequence `t, t+T, t+2T, …` so no two threads test the same nonce.
- Each thread runs a **fully unrolled, register-resident BLAKE3 compression**
  (`TryNonce`). Because the whole input is a single 64-byte block where only
  message words 8 and 9 (the nonce) change between attempts, the 7 rounds are
  emitted as straight-line `G`-function calls with the message schedule baked in
  as literals, and words 10–15 (constant zero) folded out. This is ~10× the
  throughput of calling a general-purpose `Blake3.Hash` per nonce.
- An early-out checks only the first output word for difficulties ≤ 32 (the
  common case) before computing the rest.
- A relaxed `foundFlag`/cancellation check every 8192 hashes keeps the hot loop
  free of cross-core cache traffic. First finder wins via `CompareExchange`.

Measured throughput: ~175 MH/s across 24 threads on a desktop CPU → difficulty
32 in a few seconds. (For reference, this is faster than the CPU solver in the
shim it replaced.) `Verify(seed, nonce, difficulty)` re-hashes via the plain
`Blake3.Hash` path — deliberately a *different* code path than the unrolled
solver, so a bug in one is caught by the other.

A GPU solver is unnecessary at difficulty 32 but would be the escalation path if
a pool ever set the challenge into the 2^40+ range: the device-side BLAKE3 used
by the mining kernels already exists and a nonce-grid kernel would solve in
milliseconds.

### 4.3 Protocol integration

In `StratumSession.ConnectAsync`, immediately after the socket/TLS is up:

1. **Detect.** Read one line with a short timeout (`ARC_CHALLENGE_WAIT_MS`,
   default 1500 ms). If it deserializes to `method == "pearl.challenge"`, take
   the challenge path; otherwise (timeout or any other line) fall through to the
   legacy "client speaks first" path, buffering that already-read line so the
   read loop doesn't lose it. No config flag needed.
2. **Solve & respond.** Parse `seed`/`difficulty`, enforce the difficulty cap
   (§6), solve on a worker thread, send `pearl.challenge_response`.
3. **Finish the handshake.** `mining.configure(["pearl/v1"])` →
   `mining.subscribe(<agent>)` → `mining.authorize(["<wallet>.<worker>", "x;d=<diff>"])`.
   Lines that arrive before the authorize ack (set_difficulty, set_mining_params,
   first notify) are queued and replayed to the read loop in order.
4. **Mid-session re-challenge.** The read loop also handles `pearl.challenge`
   arriving during mining: it solves on a background thread (so job processing
   keeps flowing) and sends a fresh response. Response-ack request ids are tracked
   so the read loop doesn't mistake a challenge ack for a share result.

Two wire-format adaptations were needed alongside the challenge because
challenge-first pools use the positional forms: array-style `mining.notify`
(target synthesized from the last `set_difficulty`, since the array omits it) and
positional `mining.submit`. These are independent of the challenge but travel
with it.

---

## 5. Relevant knobs

| Variable / flag | Default | Purpose |
|---|---|---|
| `ARC_CHALLENGE_MAX_DIFF` | 40 | Refuse to solve a challenge above this difficulty (DoS guard, §6). |
| `ARC_CHALLENGE_WAIT_MS` | 1500 | How long to wait for the pool's first line before assuming a legacy (non-challenge) pool. |
| `--diff <n>` / `ARC_STRATUM_DIFF` | — | Requested share difficulty, appended to the password as `;d=<n>`. |
| `--password` / `ARC_STRATUM_PASSWORD` | `x` | Full stratum password override. |

---

## 6. Security considerations

- **Cap the difficulty.** Expected work is `2^difficulty` hashes. A malicious or
  buggy pool that sends `difficulty: 60` would pin every core for hours. The
  client refuses anything above `ARC_CHALLENGE_MAX_DIFF` (default 40, ≈ minutes
  on CPU) and disconnects instead of grinding. **Any re-implementation MUST cap
  this** — it is the one place the protocol hands an attacker a CPU-DoS lever.
- **Challenge expiry / reconnect loop.** Pools expire a challenge after a short
  window (observed ~60 s). A solver slower than that window submits a stale nonce,
  gets `result:false`, and the connection is reset — producing a connect/retry
  loop. This was the dominant failure mode of the slow CPU shim this replaced;
  a fast solver (seconds) avoids it. If you implement this in `prl-proxy`, make
  the solver fast enough to beat the expiry with margin, and on `result:false`
  treat it as "re-solve a fresh challenge", not "retry the same nonce".
- **Seed must be echoed, not assumed.** Always send back the exact seed from the
  challenge; do not cache a prior seed across reconnects.

---

## 7. Test vector

Verified against a live AlphaPool challenge that the pool accepted:

```
seed        = c8600c62b2a74520e6775c0cd810e7d8c8f0fa83ad4ac93f2c142cc5172214e1   (32 bytes, hex)
difficulty  = 32
nonce       = 0x2cc663e5                      (u64)
  → hash input  = seed ‖ e5 63 c6 2c 00 00 00 00         (40 bytes; nonce little-endian)
  → wire nonce  = "000000002cc663e5"                     (u64 as 16-hex, big-endian text)
  → BLAKE3(input) begins 00 00 00 00 …                   (33 leading zero bits ≥ 32 ✓)
```

A correct implementation must:
1. `verify(seed, 0x2cc663e5, 32) == true`, and
2. `solve(seed, 32)` return a nonce (not necessarily this one — any nonce with
   ≥32 leading zero bits is valid) that itself verifies.

If your `verify` returns false on the triple above, check (in order): the nonce
is hashed little-endian; the hash is default-mode BLAKE3 (unkeyed); leading
zeros are counted over the byte stream (top byte first).
```
