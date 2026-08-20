// A mining.notify job in the Bitcoin Stratum V1 dialect, plus the coinbase and
// merkle-branch arithmetic every algo in that family repeats.
//
// The family is gr and csd today; ~14 of the algos proposed in new coin.md
// (kawpow/mewc, firopow, sha256dt, …) speak the same nine-field notify.
//
// (btx used to be the counter-example here — pool-supplied merkle root, no
// coinbase to assemble — but it was removed 2026-08-14 with the dead chain. The
// shape of that exception is worth remembering for the next coin that does the
// same: a notify carrying the merkle root directly does NOT belong in this
// family, however similar the method names look. Historical detail follows.)
// [job_id, version, prevhash, merkleroot, time, bits, share_target, clean,
// matmul_meta] — the POOL supplies the merkle root, so there is no coinbase to
// assemble and no branch to fold — and it fell back to a second "ninja"
// dialect at runtime. It stays bespoke on purpose.
//
// Deliberately NOT here: header assembly. gr builds an 80-byte header with a
// SELECTIVE swab32 (version/prev/ntime/nbits swapped, merkle root NOT — getting
// that wrong made every share the pool recomputed come out wrong), while csd
// builds an 84-byte header with a u64 ntime. Those are genuinely per-algo, and
// pretending otherwise is how a shared layer grows a knob per caller.

using Akoya.Crypto;

namespace Akoya.Miner.Mining.Stratum;

/// <summary>
/// One mining.notify job, held exactly as the pool sent it. Fields are kept in
/// wire form (raw prevhash bytes, the original ntime/nbits hex) because the
/// algos disagree about byte order and the submit frame has to echo the pool's
/// own spelling back.
/// </summary>
internal sealed record BitcoinStratumJob(
    string JobId,
    byte[] PrevHashRaw,
    string Coinb1,
    string Coinb2,
    IReadOnlyList<string> Branch,
    uint Version,
    uint Bits,
    ulong Time,
    string NbitsHex,
    string NtimeHex,
    bool Clean)
{
    /// <summary>
    /// coinbase = coinb1 ‖ extranonce1 ‖ extranonce2 ‖ coinb2, then
    /// root = sha256d(coinbase) folded left-to-right through the branch.
    /// </summary>
    public byte[] MerkleRoot(string extranonce1, string extranonce2Hex)
    {
        var root = Sha2.Sha256d(Hex.Decode(Coinb1 + extranonce1 + extranonce2Hex + Coinb2));

        var pair = new byte[64];
        foreach (var node in Branch)
        {
            root.CopyTo(pair, 0);
            Hex.Decode(node).CopyTo(pair, 32);
            root = Sha2.Sha256d(pair);
        }
        return root;
    }

    /// <summary>
    /// extranonce2 as hex, sized to EXACTLY the width the pool asked for in its
    /// mining.subscribe reply. Too wide and the coinbase is malformed; too
    /// narrow and two devices walk the same coinbase.
    /// </summary>
    /// <remarks>
    /// The width is not advisory. coinb1 ends with a length byte covering
    /// extranonce1 ‖ extranonce2, so a short extranonce2 shifts every byte of
    /// coinb2 and the pool folds a different merkle root than the miner did —
    /// which surfaces as 100% rejects, not as an error.
    /// This used to return a bare 8 hex chars for ANY size ≥ 4, on the
    /// reasoning that a u32 counter cannot fill a wider field anyway. It cannot
    /// fill it, but it still has to OCCUPY it: btc3forge, the BC3 pool, asks
    /// for 8 bytes, and 4 bytes of hex there is a malformed coinbase. Left-pad
    /// instead, which leaves the near-universal 4-byte case untouched.
    /// </remarks>
    public static string FormatExtranonce2(uint counter, int extranonce2Size)
    {
        var hex = counter.ToString("x8");
        return extranonce2Size >= 4 ? hex.PadLeft(extranonce2Size * 2, '0') : hex[^(extranonce2Size * 2)..];
    }
}
