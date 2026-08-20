using System.Reflection;
using Akoya.Miner.Observability;
using Akoya.Miner.Observability.Themes;
using Xunit;

namespace Akoya.Miner.Tests;

/// <summary>
/// The dashboard redraws in place: it homes the cursor and overwrites each row.
/// That only holds while every emitted row fits the window — one row wider than
/// the terminal wraps, pushing everything below it down a line, and the "fixed"
/// header walks off the top of the screen a row per tick. Clip is the backstop
/// that guarantees it, so its width contract is worth pinning down.
///
/// Clip now lives in Themes/Panel so every theme shares one implementation —
/// which also means one bug here would break every skin at once.
/// </summary>
public class DashboardLayoutTests
{
    private static string Clip(string s, int max) => Panel.Clip(s, max);
    private static int Width(string s) => Panel.DisplayWidth(s);

    private const string Esc = "";

    [Theory]
    [InlineData("plain ascii row")]
    [InlineData("wide CJK 日本語テキスト row")]
    [InlineData("emoji ⛏️ ❤️ ℹ️ badges")]
    public void ClipNeverExceedsTheRequestedWidth(string text)
    {
        // Repeat so the input is comfortably wider than every width we try.
        var long_ = string.Concat(Enumerable.Repeat(text + " ", 12));
        for (int max = 1; max <= 60; max++)
        {
            Assert.True(Width(Clip(long_, max)) <= max,
                $"width {Width(Clip(long_, max))} exceeded max {max}");
        }
    }

    [Fact]
    public void ClipCountsColourCodesAsZeroWidth()
    {
        // ANSI SGR sequences move no cursor, so a heavily coloured row must not
        // be clipped as though the escapes were visible characters.
        var coloured = $"{Esc}[92m✓12{Esc}[0m/{Esc}[91m✗0{Esc}[0m";
        Assert.Equal(Width("✓12/✗0"), Width(coloured));
        Assert.Equal(coloured, Clip(coloured, 40));   // fits — returned untouched
    }

    [Fact]
    public void ClipClosesAnOpenColourWhenItTruncates()
    {
        // Truncating mid-sequence would otherwise leak the colour onto the rest
        // of the terminal for the remainder of the session.
        var truncated = Clip($"{Esc}[92maaaaaaaaaaaaaaaaaaaa{Esc}[0m", 8);
        Assert.EndsWith($"{Esc}[0m", truncated, StringComparison.Ordinal);
        Assert.Contains("…", truncated, StringComparison.Ordinal);
    }

    // A right-aligned value must never touch the text it is aligned against.
    // This bit us when a row landed at exactly the panel width: no clip fired,
    // the pad computed to zero, and "*2 LEGENDARY" + "party" rendered as
    // "*2 LEGENDARYparty".
    [Theory]
    [InlineData(96)]
    [InlineData(60)]
    [InlineData(24)]
    public void LineAlwaysLeavesAGapBeforeRightAlignedText(int inner)
    {
        const string right = "worker rig01-b580 ";
        for (int leftLen = 1; leftLen < inner + 20; leftLen++)
        {
            var row = Panel.Line(inner, new string('L', leftLen), right);
            Assert.True(Width(row) <= inner, $"row width {Width(row)} exceeded {inner}");
            Assert.DoesNotContain("Lworker", row, StringComparison.Ordinal);
            Assert.DoesNotContain("…worker", row, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ShortDeviceNameKeepsTheModelAndDropsTheTrademarks()
    {
        Assert.Equal("Arc B580", Panel.ShortDeviceName("Intel(R) Arc(TM) B580 Graphics"));
        Assert.Equal("Arc A770", Panel.ShortDeviceName("Intel(R) Arc(TM) A770 Graphics"));
        // Non-Intel strings are left alone rather than guessed at.
        Assert.Equal("AMD Ryzen 9 5900X (24t)", Panel.ShortDeviceName("AMD Ryzen 9 5900X (24t)"));
        Assert.Equal("NVIDIA GeForce RTX 4090", Panel.ShortDeviceName("NVIDIA GeForce RTX 4090"));
        Assert.Equal("", Panel.ShortDeviceName(null));
    }

    // The sparkline sits inline next to other columns, so a glyph that measured
    // 2 cells would drift everything beside it — the exact bug class the width
    // helpers exist to prevent.
    [Fact]
    public void SparkIsExactlyOneCellPerSampleAndFitsItsWidth()
    {
        var samples = new double[] { 1, 5, 3, 9, 2, 7, 4, 8 };
        for (int w = 1; w <= 12; w++)
        {
            var s = Panel.Spark(samples, w);
            Assert.Equal(Math.Min(w, samples.Length), Width(s));
        }
        Assert.Equal("", Panel.Spark(samples, 0));
        Assert.Equal("", Panel.Spark(Array.Empty<double>(), 10));
    }

    [Fact]
    public void SparkScalesToTheWindowNotToZero()
    {
        // A real dip must be unmistakable: scaling from zero would flatten a
        // 25% drop into nothing on a rig running at a constant rate.
        var s = Panel.Spark(new double[] { 100, 100, 75, 100, 100 }, 5);
        Assert.Contains('█', s);
        Assert.Contains('▁', s);
    }

    [Fact]
    public void SparkDoesNotAmplifyOrdinaryJitterIntoAlarm()
    {
        // A steady rig wobbles by a fraction of a percent. Pure min-max scaling
        // would render that as a full-range sawtooth and read as a fault.
        var steady = Panel.Spark(new double[] { 64.20, 64.26, 64.21, 64.25, 64.22, 64.24 }, 6);
        Assert.Equal(6, Width(steady));
        Assert.True(steady.Distinct().Count() <= 2,
            $"half-percent jitter should look flat, got '{steady}'");

        // A perfectly flat series must not divide by zero either.
        var flat = Panel.Spark(new double[] { 7, 7, 7, 7 }, 4);
        Assert.Equal(4, Width(flat));
        Assert.Single(flat.Distinct());
    }

    [Fact]
    public void ClipLeavesShortRowsAlone()
    {
        Assert.Equal("short", Clip("short", 20));
        Assert.Equal("", Clip("anything", 0));
    }
}
