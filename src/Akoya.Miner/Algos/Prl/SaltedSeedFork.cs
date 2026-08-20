using Microsoft.Extensions.Logging;

namespace Akoya.Miner.Algos.Prl;

/// <summary>
/// The salted noise-seed hardfork (pearl PR #280).
///
/// From the activation height, the noise seeds are no longer chained off the raw
/// Merkle roots. Each root is first BOUND to the dimension it was built from,
/// under a domain-separated keyed BLAKE3:
///
///   bound_a = blake3_keyed(SALT_A, A_root || m_le32 || 0^28)
///   bound_b = blake3_keyed(SALT_B, B_root || n_le32 || 0^28)
///   b_seed  = blake3(job_key || bound_b)
///   a_seed  = blake3(b_seed  || bound_a)
///
/// where SALT_x = blake3("pearl/cert-v3/noise-seed/{A,B}").
///
/// <b>Why this one is easier than the rank fork.</b> The rank decides how the
/// worker buffers are SIZED, so it is fixed for the life of the process and
/// getting it wrong needed a relaunch. The salt flag only changes a hash — no
/// allocation depends on it — so it can be flipped on the very job that crosses
/// the height, with no restart and no lost work.
///
/// <b>And why it is less dangerous.</b> The rank fork's failure mode was a
/// SILENT halving: perfectly valid shares worth half as much. This one cannot do
/// that. Seeds that disagree with the network produce a hash the pool rejects,
/// in either direction, so a mistake here is loud within seconds.
/// </summary>
internal static class SaltedSeedFork
{
    // Heights from node/chaincfg/params.go (SaltedSeedForkHeight) in the PR diff,
    // not from the announcement — that is the lesson from PR #275, where the
    // height mattered and was silent.
    //
    // MAINNET WAS MOVED AFTER #280 LANDED. PR #282 ("delay mainnet salted seed
    // fork height by 100 blocks", 2026-08-11) pushed 98,900 -> 99,000; the
    // testnet heights were left alone. Reading only #280 leaves this build
    // deriving V3 seeds for the whole 100-block window while the network is
    // still on V2 — every share in that window rejected. Verified against
    // node/chaincfg/params.go on master, not against the #280 diff.
    public const long MainnetActivationHeight  = 99_000;
    public const long TestnetActivationHeight  = 38_648;
    public const long Testnet2ActivationHeight = 83_109;

    /// <summary>Activation height in force. Overridable so a testnet rig, or a
    /// schedule change before deployment, needs no rebuild.</summary>
    public static long ActivationHeight =>
        long.TryParse(Akoya.Crypto.MinerEnv.Get("ARC_PRL_SALTED_SEED_HEIGHT"), out var h) && h > 0
            ? h : MainnetActivationHeight;

    /// <summary>Force the post-fork derivation regardless of observed height.
    /// Escape hatch for the window where a pool has already moved but this
    /// process has not yet seen a job at the activation height.</summary>
    private static bool ForcedActive =>
        Akoya.Crypto.MinerEnv.Get("ARC_PRL_SALTED_SEED_ACTIVE") is "1" or "true";

    /// <summary>Has the fork activated, as far as we can tell? Height 0 means "no
    /// job seen yet", which is NOT evidence either way — answer false and correct
    /// once a real height arrives.</summary>
    public static bool IsActive(long height)
        => ForcedActive || (height > 0 && height >= ActivationHeight);

    private static int _active;

    /// <summary>One-shot latch for the unknown-height warning: Apply runs on every
    /// job, and this must be loud once rather than a per-job wall of text.</summary>
    private static int _zeroHeightWarned;

    /// <summary>What the miner is currently deriving with. Read by the share
    /// builder so the host and the GPU cannot disagree.</summary>
    public static bool Active => Volatile.Read(ref _active) != 0;

    /// <summary>Set the derivation from an observed chain height, and push it to
    /// the GPU. Idempotent and cheap, so it is safe to call on every job.
    ///
    /// Host and device MUST agree: the GPU derives the seeds that shape the
    /// search, and the host re-derives them when building the share. If those
    /// two ever diverge, the miner finds candidates against one noise field and
    /// submits proofs for another — every share rejected, GPU still busy. So the
    /// native setter is updated FIRST and the local flag only after it succeeds.
    /// </summary>
    /// <returns>True if the setting changed.</returns>
    public static bool Apply(long height, ILogger? log = null)
    {
        // A height we could not learn is NOT the same as a pre-fork height, but
        // the decision below cannot tell them apart — both answer "inactive". That
        // silence is what made the StratumJobParser BlockHeight=0 bug invisible:
        // the early return under this block fires before the state-change warning
        // whenever the answer does not change, and from a cold start it never
        // does. Post-fork that is every share proved against the wrong noise field
        // with nothing in the log to explain it. Say so once.
        if (height <= 0 && !ForcedActive
            && Interlocked.Exchange(ref _zeroHeightWarned, 1) == 0)
        {
            log?.LogWarning(
                "salted-seed fork: no chain height available (height={H}) — assuming INACTIVE "
                + "and deriving noise seeds from RAW Merkle roots. If this chain is past {A}, "
                + "every share will be rejected. Check that the pool's mining.notify carries a "
                + "height (a Bitcoin-style notify recovers it from the coinbase via BIP34), or "
                + "force it with ARC_PRL_SALTED_SEED_ACTIVE=1.",
                height, ActivationHeight);
        }

        int want = IsActive(height) ? 1 : 0;
        if (Volatile.Read(ref _active) == want) return false;

        try
        {
            Akoya.PearlGemm.PearlGemmNative.SetSaltedSeed(want);
        }
        catch (Exception e)
        {
            // An older pearl_gemm without the export. Do NOT flip the local flag:
            // mining on with a matched (if wrong) pair is recoverable, but a host
            // and device that disagree burn the GPU for nothing.
            log?.LogCritical(
                "salted-seed fork at height {H}: this pearl_gemm library has no "
                + "salted-seed support ({Err}) — REBUILD the native library, or every "
                + "share after height {A} will be rejected", height, e.Message, ActivationHeight);
            return false;
        }

        Volatile.Write(ref _active, want);
        log?.LogWarning(
            "salted-seed fork {State} at height {H} (activates at {A}) — noise seeds now derive "
            + "from {Mode} Merkle roots", want == 1 ? "ACTIVE" : "inactive", height, ActivationHeight,
            want == 1 ? "dimension-bound" : "raw");
        return true;
    }
}
