using Akoya.Miner.Algos.Prl;
using Xunit;

namespace Akoya.Miner.Tests;

public class PrlForksTests
{
    [Fact]
    public void HeightBeforeTheFirstForkCountsNone()
        => Assert.Equal(0, PrlForks.CrossedAt(PrlForks.All[0].CountFromHeight - 1));

    // Inclusive: the fork's rules bind AT its activation height, not after it, or
    // the dashboard would say "0 forks survived" on the very block that changed
    // the rules.
    //
    // Two, not one: MoE (71,935) precedes the rank-penalty height, so a chain
    // standing on 96,251 has provably crossed both. Mainnet is well past this,
    // so live rigs read 3.
    private const long RankPenaltyHeight = 96_251;

    [Fact]
    public void ActivationHeightCountsEveryEarlierFork()
        => Assert.Equal(2, PrlForks.CrossedAt(RankPenaltyHeight));

    [Fact]
    public void LaterHeightsStillCountThem()
        => Assert.Equal(3, PrlForks.CrossedAt(RankPenaltyHeight + 500_000));

    // The counter must not claim the salted-seed fork before the chain reaches
    // it. This is the whole contract: a floor on what the rig actually mined
    // through, never a list of forks that exist.
    [Fact]
    public void SaltedSeedForkOnlyCountsOnceCrossed()
    {
        Assert.Equal(2, PrlForks.CrossedAt(SaltedSeedFork.MainnetActivationHeight - 1));
        Assert.Equal(3, PrlForks.CrossedAt(SaltedSeedFork.MainnetActivationHeight));
    }

    // The table is ordered oldest-first, which is what makes
    // HeightBeforeTheFirstForkCountsNone meaningful.
    [Fact]
    public void TableIsOrderedOldestFirst()
    {
        for (int i = 1; i < PrlForks.All.Count; i++)
            Assert.True(PrlForks.All[i].CountFromHeight >= PrlForks.All[i - 1].CountFromHeight);
    }

    // An inexact entry is a BOUND, not a guess: it must borrow its height from a
    // fork we actually verified, so "we are past X" genuinely proves "we are past
    // this too". A bound invented from thin air would let the counter claim a fork
    // on a chain that never reached it — the one thing this counter must not do.
    [Fact]
    public void EveryInexactForkBorrowsAVerifiedForksHeight()
    {
        foreach (var f in PrlForks.All)
        {
            if (f.HeightIsExact) continue;
            Assert.Contains(PrlForks.All,
                e => e.HeightIsExact && e.CountFromHeight == f.CountFromHeight);
        }
    }

    // An unknown height is not evidence of a pre-fork chain. 0 here means "say
    // nothing", which is why the dashboard hides the field rather than showing 0.
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void UnknownHeightCountsNothing(long height)
        => Assert.Equal(0, PrlForks.CrossedAt(height));

    // The count can never exceed what the table knows; it is a floor on reality,
    // because forks whose height was never published to us are deliberately absent.
    [Fact]
    public void CountNeverExceedsTheKnownTable()
        => Assert.Equal(PrlForks.KnownCount, PrlForks.CrossedAt(long.MaxValue));

    // Guards the trap this feature nearly shipped with: the counter cannot key off
    // a height alone, because PrlHeightStore.BestKnown falls back to the persisted
    // last-height file — which is PEARL's. Without the MarkPrlActive gate, an rx or
    // btx run would inherit a previous Pearl session's count and display it against
    // a chain that has never forked.
    [Fact]
    public void ForkCountIsZeroUntilPearlIsMarkedActive()
    {
        // A fresh process has not marked Pearl active, so even a persisted Pearl
        // height must not produce a count.
        Assert.Equal(0, Akoya.Miner.Observability.Metrics.PrlForksCrossed);
    }
}
