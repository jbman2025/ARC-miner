# pearl/v1 Connection Challenge — Protocol Spec & Implementation Guide

How AlphaPool-style ("pearl/v1") pools gate new stratum connections with a
BLAKE3 proof-of-work challenge, and how ARC-miner implements it natively.
Written for porting the same support into **prl-proxy** (or any other
client). Everything below was captured from live `us1.alphapool.tech:5566`
traffic on 2026-06-10 and verified end-to-end (shares accepted with no shim).

---

## 1. Why the challenge exists

The pool requires each new TCP connection to burn ~2^difficulty hash
evaluations before it will authorize the session — connection-level anti-spam.
The difficulty observed in production is **32** (≈ 4.3 × 10⁹ hashes expected).
The challenge can also be re-issued **mid-session** at the pool's discretion;
a client that cannot re-solve gets dropped.

Key operational fact: **the challenge expires** (observed window ≈ 120 s, and
solves ≥ ~85 s were rejected with `authorized=0`). A solver that averages
worse than ~60 s will intermittently fail the handshake and loop. Size your
solver accordingly (see §5).

---

## 2. Wire protocol

Transport is ordinary line-delimited JSON-RPC stratum. **The pool speaks
first** — immediately after the TCP connect (observed ~150 ms), before the
client has sent anything:

```json
{"id":null,"method":"pearl.challenge","params":{"seed":"c8600c62b2a74520e6775c0cd810e7d8c8f0fa83ad4ac93f2c142cc5172214e1","difficulty":32}}
```

- `seed` — 64 hex chars = **32 bytes**.
- `difficulty` — integer; required number of **leading zero bits** (see §3).

The client replies (any request id; the pool acks it with `{"id":N,"result":true,…}`):

```json
{"id":1,"method":"pearl.challenge_response","params":{"nonce":"000000002cc663e5","seed":"c8600c62b2a74520e6775c0cd810e7d8c8f0fa83ad4ac93f2c142cc5172214e1"}}
```

- `nonce` — the winning u64 rendered as **16 lowercase hex digits,
  most-significant digit first** (i.e. `printf "%016llx"`). NOTE: this is the
  *numeric* big-endian rendering even though the hash input uses the
  little-endian byte order (§3) — don't conflate the two.
- `seed` — echoed back verbatim.

Only after an accepted response does the pool process the normal handshake.
The full working sequence ARC-miner sends (mirroring what the original shim
did) is:

```json
{"id":2,"method":"mining.configure","params":[["pearl/v1"],{}]}
{"id":3,"method":"mining.subscribe","params":["<agent string>"]}
{"id":4,"method":"mining.authorize","params":["<wallet>.<worker>","x;d=250000"]}
```

The pool honours a difficulty request in the **password field** (`x;d=N`)
natively — it answers with `mining.set_difficulty [N]` immediately after the
authorize ack. AlphaPool's max is `d=250000`.

**Ordering hazard:** the pool may emit `pearl.set_mining_params`,
`mining.set_difficulty`, and even `mining.notify` *before* the authorize ack
arrives. Buffer any such notifications you read while waiting for the ack and
replay them to your normal dispatch afterwards, in order.

**Mid-session:** if a `pearl.challenge` arrives on an established session,
solve and respond exactly as at connect time — but do it on a worker thread;
do not block your read loop (jobs keep flowing while you grind). Track the
response request-id so its ack isn't misparsed as a share result.

### Related pearl/v1 quirks you will also hit in prl-proxy

These aren't part of the challenge but are required for a working direct
connection (the old shim translated all of them):

1. `mining.notify` params are **positional**:
   `[job_id, prev_hash, header, height, …]` — index 0/2/3 are what you need.
   There is **no target field**; derive the share target from the last
   `mining.set_difficulty` (classic pdiff: `target = (0xFFFF << 208) / diff`,
   then compact to nbits if your code wants nbits).
2. `mining.submit` params are **positional**:
   `["<wallet>.<worker>", job_id, plain_proof_base64]`.
   Object-form submits are rejected with
   `[25,"bad submit params (need [worker, job_id, plain_proof_b64])",null]`.

---

## 3. The hash construction

A nonce **wins** iff:

```
leading_zero_bits( BLAKE3( seed[0..32] ‖ nonce_le64 ) ) ≥ difficulty
```

Precisely:

- **Input** is exactly **40 bytes**: the 32 seed bytes followed by the nonce
  as **8 little-endian bytes** (`u64.to_le_bytes()`).
- **Hash** is plain, unkeyed BLAKE3, default 32-byte output. No key, no
  derive-key, no domain separation, no XOF beyond the first 32 bytes.
- **Leading zero bits** are counted over the hash's canonical **byte stream**
  (the same byte order `b3sum` prints): 8 per leading `0x00` byte, then the
  high-order zero bits of the first non-zero byte.

Because 40 bytes < 64, the whole input is a **single BLAKE3 block in a single
chunk** — one compression call with:

- chaining value = IV (`6A09E667 BB67AE85 3C6EF372 A54FF53A 510E527F 9B05688C 1F83D9AB 5BE0CD19`)
- block words `m[0..7]` = seed (LE u32s), `m[8..9]` = nonce (LE), `m[10..15]` = 0
- counter = 0, block_len = **40**, flags = `CHUNK_START | CHUNK_END | ROOT` = **0x0B**
- output word `i` = `v[i] ^ v[i+8]` for i in 0..7 (32 bytes, LE-serialized)

That single-compression shape is what makes the fast solver in §5 possible.

### Difficulty semantics

`difficulty` = required leading-zero **bits**, so expected work = `2^difficulty`
hashes. It is NOT the pdiff/vardiff difficulty used for shares — same word,
completely different scale (challenge 32 ≈ 4.3e9 hashes; share diff 250000 is
a pdiff target divisor).

---

## 4. Test vectors

