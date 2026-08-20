using Akoya.Miner.Mining;
using Xunit;

namespace Akoya.Miner.Tests;

// The invariants named in ReconnectBackoff's own doc comment, pinned down. This
// is the code that stands between a flapping pool and a retry storm, and until
// now it had no tests at all — while six other algos quietly ignored it and
// hand-rolled a jitter-free version.
public class ReconnectBackoffTests
{
    private const double NoJitter = 0.0;

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    [InlineData(5, 32)]
    [InlineData(6, 60)]      // 2^6 = 64, capped to 60
    public void DelayGrowsExponentiallyThenCaps(int attempt, double expectedSeconds)
    {
        Assert.Equal(expectedSeconds, ReconnectBackoff.ComputeDelay(attempt, NoJitter).TotalSeconds, 3);
    }

    [Fact]
    public void DelayStaysCappedForeverAfter()
    {
        // A pool down for hours must not push the delay toward infinity, and
        // must not overflow the exponent either.
        foreach (var attempt in new[] { 7, 50, 1000, int.MaxValue })
        {
            Assert.Equal(ReconnectBackoff.CapSeconds,
                ReconnectBackoff.ComputeDelay(attempt, NoJitter).TotalSeconds, 3);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NonPositiveAttemptIsTreatedAsTheFirst(int attempt)
    {
        // Guards against a caller that increments after computing: attempt 0
        // must not mean "no delay" and spin the loop at line speed.
        Assert.Equal(2.0, ReconnectBackoff.ComputeDelay(attempt, NoJitter).TotalSeconds, 3);
    }

    [Fact]
    public void JitterIsSymmetricAndBounded()
    {
        var base3 = ReconnectBackoff.ComputeDelay(3, NoJitter).TotalSeconds;   // 8s
        var low = ReconnectBackoff.ComputeDelay(3, -1.0).TotalSeconds;
        var high = ReconnectBackoff.ComputeDelay(3, +1.0).TotalSeconds;

        Assert.Equal(base3 * (1 - ReconnectBackoff.JitterFraction), low, 3);
        Assert.Equal(base3 * (1 + ReconnectBackoff.JitterFraction), high, 3);
        Assert.True(low < base3 && base3 < high);
    }

    [Fact]
    public void DelayNeverFallsBelowTheFloor()
    {
        // Even maximally negative jitter on the smallest attempt must stay above
        // the floor — otherwise the retry loop becomes a spin.
        for (int attempt = 1; attempt <= 10; attempt++)
        {
            var d = ReconnectBackoff.ComputeDelay(attempt, -1.0).TotalSeconds;
            Assert.True(d >= ReconnectBackoff.FloorSeconds, $"attempt {attempt} gave {d}s");
        }
    }

    [Fact]
    public void NextDelayAppliesRealJitterWithinBounds()
    {
        // The production wrapper: many samples must all land in the jitter band
        // and must not all be identical (that would mean no jitter at all).
        var seen = new HashSet<double>();
        double lo = 8 * (1 - ReconnectBackoff.JitterFraction);
        double hi = 8 * (1 + ReconnectBackoff.JitterFraction);

        for (int i = 0; i < 200; i++)
        {
            var d = ReconnectBackoff.NextDelay(3).TotalSeconds;
            Assert.InRange(d, lo, hi);
            seen.Add(d);
        }
        Assert.True(seen.Count > 50, $"expected spread, saw {seen.Count} distinct values");
    }

    [Fact]
    public void NextDelayIsCappedAndFlooredToo()
    {
        for (int i = 0; i < 100; i++)
        {
            Assert.InRange(ReconnectBackoff.NextDelay(99).TotalSeconds,
                ReconnectBackoff.CapSeconds * (1 - ReconnectBackoff.JitterFraction),
                ReconnectBackoff.CapSeconds * (1 + ReconnectBackoff.JitterFraction));
            Assert.True(ReconnectBackoff.NextDelay(1).TotalSeconds >= ReconnectBackoff.FloorSeconds);
        }
    }

    // ── ReconnectHint ────────────────────────────────────────────────────────

    [Fact]
    public void NoHintReturnsNull()
    {
        Assert.Null(ReconnectBackoff.ApplyHint(0, NoJitter));
        Assert.Null(ReconnectBackoff.ApplyHint(-5, NoJitter));
    }

    [Fact]
    public void HintIsHonouredWhenReasonable()
    {
        Assert.Equal(45.0, ReconnectBackoff.ApplyHint(45, NoJitter)!.Value.TotalSeconds, 3);
    }

    [Fact]
    public void AHostileHintCannotWedgeUsForHours()
    {
        var d = ReconnectBackoff.ApplyHint(86_400, NoJitter)!.Value.TotalSeconds;
        Assert.Equal(ReconnectBackoff.MaxReconnectHintSeconds, d, 3);
        Assert.True(ReconnectBackoff.HintWasClamped(86_400));
        Assert.False(ReconnectBackoff.HintWasClamped(45));
    }

    [Fact]
    public void HintJitterIsNarrowerThanFailureJitter()
    {
        // The pool already coordinated the timing; we only de-synchronise the
        // fleet slightly. ±10% vs ±25%.
        Assert.True(ReconnectBackoff.HintJitterFraction < ReconnectBackoff.JitterFraction);

        var lo = ReconnectBackoff.ApplyHint(100, -1.0)!.Value.TotalSeconds;
        var hi = ReconnectBackoff.ApplyHint(100, +1.0)!.Value.TotalSeconds;
        Assert.Equal(90.0, lo, 3);
        Assert.Equal(110.0, hi, 3);
    }

    [Fact]
    public void HintDelayIsNeverNegative()
    {
        Assert.True(ReconnectBackoff.ApplyHint(0.001, -1.0)!.Value.TotalSeconds >= 0);
    }
}
