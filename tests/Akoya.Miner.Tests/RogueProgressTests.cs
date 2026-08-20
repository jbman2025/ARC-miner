using Akoya.Miner.Observability;
using Akoya.Miner.Observability.Themes;
using Xunit;

namespace Akoya.Miner.Tests;

/// <summary>
/// Meta-progression: the numbers that survive a restart. The whole point is that
/// a snapshot's counters restart at zero every launch while the lifetime totals
/// must not, so most of these are about deltas and baselines rather than sums.
/// </summary>
public sealed class RogueProgressTests : IDisposable
{
    private readonly string _path;

    public RogueProgressTests()
        => _path = Path.Combine(Path.GetTempPath(), "arc-progress-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    private static Metrics.DashSnapshot Snap(long accepted, long blocks = 0, double hs = 60e12)
        => new("pool:1", "", "rig", true, false, 10, accepted, 0, blocks,
               hs, hs, 0, 96_300, 0, 1, null, Array.Empty<Metrics.DashGpu>());

    private static long T(double seconds) => (long)(seconds * TimeSpan.TicksPerSecond);

    [Fact]
    public void LifetimeSharesSurviveARestart()
    {
        var a = new RogueProgress(_path);
        a.Observe(Snap(0), T(1));
        a.Observe(Snap(40), T(2));
        a.Save();
        Assert.Equal(40, a.LifetimeShares);

        // A new process: the snapshot counter is back at zero, but the lifetime
        // total must continue from where the last run left off.
        var b = new RogueProgress(_path);
        Assert.Equal(40, b.LifetimeShares);
        b.Observe(Snap(0), T(1));
        b.Observe(Snap(10), T(2));
        Assert.Equal(50, b.LifetimeShares);
    }

    [Fact]
    public void ARestartMidSessionDoesNotSubtractIntoNegatives()
    {
        // Metrics can be re-initialised under us (reconnect, algo restart),
        // sending the accepted counter backwards. Naively differencing would
        // decrement the lifetime total.
        var p = new RogueProgress(_path);
        p.Observe(Snap(0), T(1));
        p.Observe(Snap(100), T(2));
        Assert.Equal(100, p.LifetimeShares);

        p.Observe(Snap(3), T(3));      // counter reset
        p.Observe(Snap(9), T(4));
        Assert.Equal(106, p.LifetimeShares);   // 100 + 6, never less than 100
    }

    [Fact]
    public void LevelRisesWithLifetimeNotSessionShares()
    {
        var p = new RogueProgress(_path);
        p.Observe(Snap(0), T(1));
        p.Observe(Snap(5_000), T(2));
        p.Save();

        // A fresh session with almost no shares of its own still shows the
        // earned level — the thing that was broken when level read g.Accepted.
        var next = new RogueProgress(_path);
        Assert.True(RogueTheme.LevelFor(next.LifetimeShares) > 5);
        Assert.Equal(1, RogueTheme.LevelFor(0));
    }

    [Fact]
    public void FirstBloodFiresOnceAndThenExpires()
    {
        var p = new RogueProgress(_path);
        p.Observe(Snap(0), T(1));
        Assert.False(p.FirstBloodLit(T(1)));

        p.Observe(Snap(1), T(2));
        Assert.True(p.FirstBloodLit(T(2)));
        Assert.True(p.FirstBloodLit(T(15)));
        Assert.False(p.FirstBloodLit(T(60)));   // moments do not last forever
    }

    [Fact]
    public void BlockFindLightsUpAndCountsForever()
    {
        var p = new RogueProgress(_path);
        p.Observe(Snap(10), T(1));
        p.Observe(Snap(10, blocks: 1), T(2));

        Assert.True(p.BlockFindLit(T(3)));
        Assert.False(p.BlockFindLit(T(90)));
        Assert.Equal(1, p.LifetimeBlocks);      // the trophy is permanent
    }

    [Fact]
    public void PersonalBestIgnoresTheStartupRampThenCelebratesRealGains()
    {
        var p = new RogueProgress(_path);
        // Hashrate climbing from cold — every sample is a "new high" and none of
        // them deserve a banner.
        for (int i = 1; i <= 5; i++) p.Observe(Snap(0, hs: i * 10e12), T(i));
        Assert.False(p.PersonalBestLit(T(5)));

        // A genuine improvement after it settled.
        p.Observe(Snap(0, hs: 80e12), T(20));
        Assert.True(p.PersonalBestLit(T(20)));
        Assert.Equal(80e12, p.BestHashrate);
    }

    [Fact]
    public void ACorruptProgressFileStartsOverRatherThanThrowing()
    {
        File.WriteAllText(_path, "shares=not-a-number\nblocks=-5\ngarbage\n\nbest_hs=NaN\n");
        var p = new RogueProgress(_path);
        Assert.Equal(0, p.LifetimeShares);
        Assert.Equal(0, p.LifetimeBlocks);
        Assert.Equal(0, p.BestHashrate);
    }

    [Fact]
    public void HistoryIsOldestFirstAndBoundedByTheRing()
    {
        var p = new RogueProgress(_path);
        for (int i = 1; i <= RogueProgress.HistoryLength + 25; i++)
            p.Observe(Snap(0, hs: i * 1e9), T(i));

        var h = p.History();
        Assert.Equal(RogueProgress.HistoryLength, h.Length);
        Assert.True(h[0] < h[^1]);   // oldest first, not reversed
        Assert.Equal((RogueProgress.HistoryLength + 25) * 1e9, h[^1]);
    }
}
