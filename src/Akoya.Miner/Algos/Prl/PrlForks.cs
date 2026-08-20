namespace Akoya.Miner.Algos.Prl;

/// <summary>
/// Pearl consensus forks this build knows the activation height of, and how many
/// of them a given chain height is past.
///
/// This exists for the dashboard's "forks survived" counter, which is flavour —
/// but flavour that has to be honest, so read what the number actually means:
///
/// <b>It is a FLOOR, not a total.</b> A fork counts from the lowest height at
/// which we can PROVE the chain is past it. Usually that is its exact activation
/// height. Where we never learned one, a fork can still count from a height that
/// implies it — see the MoE entry, whose height was never published to us but
/// which provably precedes the rank-penalty fork. What we never do is guess: a
/// guessed height would over-count for every rig below the guess, and the whole
/// point of the counter is that it cannot claim a fork you did not actually mine
/// through.
///
/// Adding a fork is one line in <see cref="Known"/>. Take the height from the
/// node's <c>chaincfg/params.go</c> diff, never from an announcement summary —
/// that is the lesson from PR #275, where the height mattered and was silent.
/// </summary>
internal static class PrlForks
{
    /// <param name="Name">Short identifier, for diagnostics.</param>
    /// <param name="CountFromHeight">Lowest height at which this fork is known to
    /// be in the past.</param>
    /// <param name="HeightIsExact">True when <paramref name="CountFromHeight"/> is
    /// the fork's own activation height; false when it is an upper bound borrowed
    /// from a later fork that could not have preceded it. An inexact entry counts
    /// correctly for any chain past the bound and simply stays uncounted below it.</param>
    internal readonly record struct Fork(string Name, long CountFromHeight, bool HeightIsExact);

    /// <summary>Mainnet forks, oldest first.</summary>
    private static readonly Fork[] Known =
    {
        // MoE (V1 dense -> V2 certificate). This used to borrow the rank-penalty
        // height as an upper bound because no number had been published to us.
        // The real one is now in node/chaincfg/params.go on master
        // (MainNetParams.MoEForkHeight), so the bound is retired.
        new Fork("moe", 71_935, HeightIsExact: true),

        // NOT LISTED: the dense-only softfork (pearl PR #260/#261,
        // DenseOnlyForkHeight 91,630). It only rejects MoE certificates, and
        // this miner has only ever submitted dense proofs — so nothing about it
        // ever applied to us, and counting it would inflate the number with a
        // fork we did not mine through in any meaningful sense. The counter
        // tracks forks that changed what this miner had to do.

        // pearl PR #275 — node/chaincfg/params.go RankPenaltyForkHeight.
        // Went live 2026-08-06 ~02:00 UTC; this miner mined through it. The
        // height is inlined because the fork is now historical: rank-256 support
        // was removed once mainnet was past it, so there is no longer any
        // RankFork type to hold the constant. It stays in this table because the
        // counter reports forks the rig mined THROUGH, which does not stop being
        // true when the code that handled the transition is deleted.
        // (Testnet 36,761 / Testnet2 80,627, if this ever needs a testnet table.)
        new Fork("rank-penalty", 96_251, HeightIsExact: true),

        // pearl PR #280 — node/chaincfg/params.go SaltedSeedForkHeight. Noise
        // seeds derive from dimension-bound Merkle roots from this height; see
        // SaltedSeedFork. Height read from the PR's params.go diff.
        new Fork("salted-seed", SaltedSeedFork.MainnetActivationHeight, HeightIsExact: true),
    };

    /// <summary>How many known forks this height is at or past. 0 when the height
    /// is unknown (no job seen yet) — absence of a height is not evidence of a
    /// pre-fork chain, so we say nothing rather than say zero confidently.</summary>
    public static int CrossedAt(long height)
    {
        if (height <= 0) return 0;
        int n = 0;
        foreach (var f in Known)
            if (height >= f.CountFromHeight) n++;
        return n;
    }

    /// <summary>Total number of forks in the table — the ceiling
    /// <see cref="CrossedAt"/> can currently report.</summary>
    public static int KnownCount => Known.Length;

    /// <summary>The table itself, for tests and diagnostics.</summary>
    public static IReadOnlyList<Fork> All => Known;
}
