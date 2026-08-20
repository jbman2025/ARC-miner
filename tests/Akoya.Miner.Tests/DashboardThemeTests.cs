using Akoya.Miner.Observability;
using Akoya.Miner.Observability.Themes;
using Xunit;

namespace Akoya.Miner.Tests;

/// <summary>
/// Contract tests every theme must satisfy. They run against all registered
/// themes rather than a named one, so adding a skin automatically inherits the
/// checks — the point of the seam is that a new theme cannot quietly reintroduce
/// a layout bug we already fixed once.
/// </summary>
public class DashboardThemeTests
{
    // A hidden theme must be exactly that: absent from the documented list that
    // --help prints, still reachable by name, and still enumerated for testing.
    // The last clause is the one worth pinning — the danger with a secret theme
    // is that hiding it from the docs quietly hides it from the safety suite too.
    [Fact]
    public void HiddenThemesAreUndocumentedButReachableAndTested()
    {
        var shown = new List<string>(Dashboard.ThemeNames());
        var all   = new List<string>(Dashboard.AllThemeNames());

        Assert.DoesNotContain("konami", shown);
        Assert.Contains("konami", all);
        Assert.Equal("konami", Dashboard.ResolveTheme("konami").Name);

        // Case-insensitive, like every other --theme value.
        Assert.Equal("konami", Dashboard.ResolveTheme("KONAMI").Name);

        // And every documented theme is still reachable and still in the full list.
        foreach (var n in shown) Assert.Contains(n, all);
    }

    // AllThemeNames, not ThemeNames: the latter hides undocumented themes from
    // --help, and "undocumented" must never leak into "unverified". Every skin
    // that a user can reach by name obeys the same house rules.
    public static TheoryData<string> AllThemes()
    {
        var d = new TheoryData<string>();
        foreach (var n in Dashboard.AllThemeNames()) d.Add(n);
        return d;
    }

    private static Metrics.DashSnapshot Snap(
        int gpus = 2, bool withCpu = false, double heartbeatAge = 0.0, long height = 4_782_193,
        bool sensors = false, int prlForks = 0)
    {
        var rows = new List<Metrics.DashGpu>();
        for (int i = 0; i < gpus; i++)
        {
            rows.Add(new Metrics.DashGpu(
                i, "Intel(R) Arc(TM) B580 Graphics", 19_200_000, 58.2,
                640, 1, heartbeatAge, "500.0K", IsCpu: false,
                TempC: sensors ? 71.0 : null,
                PowerW: sensors ? 168.0 : null,
                FanRpm: sensors ? 1450 : null));
        }
        if (withCpu)
        {
            rows.Add(new Metrics.DashGpu(
                gpus, "AMD Ryzen 9 5900X (24t)", 11_100, 0, 42, 0,
                heartbeatAge, "50.0K", IsCpu: true));
        }
        return new Metrics.DashSnapshot(
            "stratum+tcp://pearl.alphapool.tech:3333", withCpu ? "flockpool.com:4444" : "",
            "rig01-b580", true, withCpu, 42.0,
            1282, 3, 2, 38_400_000, 38_400_000, withCpu ? 11_100 : 0,
            height, withCpu ? 3_733_701 : 0, prlForks, null, rows.ToArray());
    }

    private static ThemeContext Ctx(Metrics.DashSnapshot snap, int inner)
        => new(snap, TimeSpan.FromSeconds(8047), inner, "0.3.0");

    // The invariant the whole in-place redraw rests on: one row wider than the
    // window wraps, which pushes every row below it down and walks the panel up
    // the screen a line per tick. The emitter clips as a backstop, but a theme
    // that needs clipping has already miscomputed its own layout.
    [Theory]
    [MemberData(nameof(AllThemes))]
    public void NoThemeBuildsARowWiderThanTheWindow(string themeName)
    {
        var theme = Dashboard.ResolveTheme(themeName);
        foreach (int inner in new[] { 20, 40, 60, 80, 110 })
        {
            foreach (var snap in new[]
                     {
                         Snap(), Snap(1), Snap(6), Snap(2, withCpu: true),
                         // Sensor columns only appear on Linux, and they widen
                         // the worker table — the layout has to survive both.
                         Snap(sensors: true), Snap(6, sensors: true),
                         Snap(2, withCpu: true, sensors: true),
                         // The fork counter lengthens the title row.
                         Snap(prlForks: 1), Snap(prlForks: 12),
                         Snap(2, withCpu: true, sensors: true, prlForks: 3),
                     })
            {
                foreach (var row in theme.BuildHeader(Ctx(snap, inner)))
                {
                    Assert.True(Panel.DisplayWidth(row) <= inner,
                        $"{themeName}: row of width {Panel.DisplayWidth(row)} exceeded inner={inner}: {row}");
                }
            }
        }
    }

    // The event pane is sized from whatever the header leaves. If the header
    // height varied with unrelated state the panel would oscillate between ticks
    // and stop being an in-place redraw.
    [Theory]
    [MemberData(nameof(AllThemes))]
    public void HeaderHeightDependsOnlyOnWorkerCount(string themeName)
    {
        var theme = Dashboard.ResolveTheme(themeName);
        int a = theme.BuildHeader(Ctx(Snap(2), 100)).Count;
        int b = theme.BuildHeader(Ctx(Snap(2, heartbeatAge: 120), 100)).Count;
        int c = theme.BuildHeader(Ctx(Snap(2, height: 0), 100)).Count;
        Assert.Equal(a, b);
        Assert.Equal(a, c);
    }

    // The house rule: flavour decorates the truth. However the skin dresses it
    // up, a dead worker must be visibly dead — red, and labelled in plain words
    // an operator can act on at 3am.
    [Theory]
    [MemberData(nameof(AllThemes))]
    public void AStalledWorkerIsUnmistakableInEveryTheme(string themeName)
    {
        var theme = Dashboard.ResolveTheme(themeName);
        var rows = theme.BuildHeader(Ctx(Snap(1, heartbeatAge: 300), 100));
        var mentions = rows.FindAll(r => r.Contains("B580", StringComparison.Ordinal));
        Assert.NotEmpty(mentions);

        // AT LEAST ONE row naming the dead worker must report it unmistakably.
        // Originally this took the FIRST such row, which assumed every theme
        // mentions a worker exactly once — true until a theme led with a summary
        // line that also names the card. "Somewhere, unmistakably" is the actual
        // requirement; for a single-mention theme this is the identical check.
        //
        // The alternation is a VOCABULARY ALLOWLIST and its job is to stop a theme
        // signalling failure by colour alone or behind a metaphor — "the third
        // thruster has decoupled" is not something to read at 3am. Widen it only
        // for unambiguous plain English for "this worker is not working", never
        // for in-world flavour. Upper case is deliberate: the failure state should
        // be shouted, and it keeps an incidental lowercase "stalled" in prose from
        // satisfying the rule by accident.
        Assert.True(
            mentions.Exists(r => r.Contains(Panel.Red, StringComparison.Ordinal)
                                 && System.Text.RegularExpressions.Regex.IsMatch(
                                        r, "STALL|DOWNED|SILENT|STOPPED")),
            $"{themeName}: no row reported the dead worker in red with a plain failure word:\n"
            + string.Join("\n", mentions));
    }

    // An unknown temperature must read as unknown. Rendering a null as 0 would
    // put "0°C" next to a mining GPU and send someone hunting a sensor fault.
    [Theory]
    [MemberData(nameof(AllThemes))]
    public void AbsentSensorsRenderAsUnknownNeverAsZero(string themeName)
    {
        var theme = Dashboard.ResolveTheme(themeName);
        foreach (var row in theme.BuildHeader(Ctx(Snap(2, sensors: false), 100)))
        {
            Assert.DoesNotContain("0°C", row, StringComparison.Ordinal);
            Assert.DoesNotContain("0W", row, StringComparison.Ordinal);
        }

        // ...and when they are present they actually show up.
        var hot = theme.BuildHeader(Ctx(Snap(2, sensors: true), 100));
        Assert.Contains(hot, r => r.Contains("71°C", StringComparison.Ordinal));
    }

    // Dual mining runs two DIFFERENT chains. A single height field showed the
    // Monero height while the GPU party was mining Pearl, because the CPU side
    // pulled jobs more often and kept overwriting it. Each chain's height must
    // stay attached to its own pool.
    [Fact]
    public void RogueKeepsEachChainsDepthWithItsOwnGuild()
    {
        var theme = Dashboard.ResolveTheme("rogue");
        var rows = theme.BuildHeader(Ctx(Snap(2, withCpu: true), 120));

        var gpuGuild = rows.Find(r => r.Contains("alphapool", StringComparison.Ordinal));
        var cpuGuild = rows.Find(r => r.Contains("flockpool", StringComparison.Ordinal));
        Assert.NotNull(gpuGuild);
        Assert.NotNull(cpuGuild);

        // Each chain's height appears exactly once, attached to the right thing:
        // the GPU chain in the TITLE, the CPU chain on its own guild row. Guild 1
        // deliberately does not repeat the title's figure — that duplication cost
        // ~17 columns and squeezed the floor map off a 96-column panel.
        Assert.Contains(rows, r => r.Contains("Depth 4,782,193", StringComparison.Ordinal));
        Assert.DoesNotContain("4,782,193", gpuGuild, StringComparison.Ordinal);

        Assert.Contains("3,733,701", cpuGuild, StringComparison.Ordinal);
        // The CPU chain's height must never leak onto the GPU pool's row.
        Assert.DoesNotContain("3,733,701", gpuGuild, StringComparison.Ordinal);
    }

    // Classic shows the same height information as rogue — same rule, different
    // wording: GPU chain in the title, each pool row carrying its own.
    [Fact]
    public void ClassicShowsHeightPerPoolJustLikeRogue()
    {
        var theme = Dashboard.ResolveTheme("classic");
        var rows = theme.BuildHeader(Ctx(Snap(2, withCpu: true), 120));

        Assert.Contains(rows, r => r.Contains("height 4,782,193", StringComparison.Ordinal)
                                && r.Contains("ARC MINER", StringComparison.Ordinal));

        var gpuPool = rows.Find(r => r.Contains("alphapool", StringComparison.Ordinal));
        var cpuPool = rows.Find(r => r.Contains("flockpool", StringComparison.Ordinal));
        Assert.Contains("4,782,193", gpuPool!, StringComparison.Ordinal);
        Assert.Contains("3,733,701", cpuPool!, StringComparison.Ordinal);
        Assert.DoesNotContain("3,733,701", gpuPool!, StringComparison.Ordinal);
    }

    // A pool whose dialect carries no height (csd's Bitcoin-stratum notify keeps
    // it in the coinbase) must show nothing rather than "height 0".
    [Theory]
    [MemberData(nameof(AllThemes))]
    public void NoHeightRendersAsNothingNotZero(string themeName)
    {
        var theme = Dashboard.ResolveTheme(themeName);
        foreach (var row in theme.BuildHeader(Ctx(Snap(2, height: 0), 120)))
        {
            Assert.DoesNotContain("height 0", row, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Depth 0", row, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RogueFallsBackToTheCpuChainWhenThereIsNoGpuHalf()
    {
        var theme = Dashboard.ResolveTheme("rogue");
        // CPU-only run (gr/rx/nm): height 0 on the GPU side.
        var snap = Snap(2, withCpu: true, height: 0);
        var rows = theme.BuildHeader(Ctx(snap, 120));
        Assert.Contains(rows, r => r.Contains("Depth 3,733,701", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownThemeNameFallsBackToClassicRatherThanThrowing()
    {
        // A cosmetic setting must never be able to stop a rig from mining.
        Assert.Equal("classic", Dashboard.ResolveTheme("nonsense").Name);
        Assert.Equal("classic", Dashboard.ResolveTheme("").Name);
        Assert.Equal("classic", Dashboard.ResolveTheme(null).Name);
        Assert.Equal("rogue",   Dashboard.ResolveTheme("  RoGuE ").Name);
    }

    [Fact]
    public void RogueMapsArcModelNumbersToIntelsOwnCodenames()
    {
        // Intel names Arc generations Alchemist / Battlemage / Celestial / Druid,
        // so the classes come off the box rather than being invented.
        static Metrics.DashGpu Card(string name, bool cpu = false) =>
            new(0, name, 0, 0, 0, 0, 0, "", cpu);

        Assert.Equal("Battlemage", RogueTheme.ClassOf(Card("Intel(R) Arc(TM) B580 Graphics")));
        Assert.Equal("Alchemist",  RogueTheme.ClassOf(Card("Intel(R) Arc(TM) A770 Graphics")));
        Assert.Equal("Celestial",  RogueTheme.ClassOf(Card("Intel(R) Arc(TM) C310 Graphics")));
        Assert.Equal("Hireling",   RogueTheme.ClassOf(Card("AMD Ryzen 9 5900X", cpu: true)));
        // Unknown silicon stays honest instead of inventing lore.
        Assert.Equal("Adventurer", RogueTheme.ClassOf(Card("Some Other GPU")));
    }

    [Fact]
    public void RogueLevelsRiseAndThenSlowDown()
    {
        Assert.Equal(1, RogueTheme.LevelFor(0));
        Assert.Equal(1, RogueTheme.LevelFor(7));
        Assert.Equal(2, RogueTheme.LevelFor(8));

        // Monotonic, and slow enough that a long session doesn't run to LVL 99.
        int prev = 0;
        for (long shares = 0; shares < 2_000_000; shares = shares * 2 + 1)
        {
            int lvl = RogueTheme.LevelFor(shares);
            Assert.True(lvl >= prev, "level went backwards");
            prev = lvl;
        }
        Assert.InRange(RogueTheme.LevelFor(1_000_000), 10, 40);
    }

    // The fork counter is flavour, so the house rule applies to it too: it may
    // decorate, it may not invent. Off Pearl there is no fork count to show, and
    // showing "0 forks survived" on an rx rig would be a claim about a chain the
    // counter knows nothing about.
    [Theory]
    [MemberData(nameof(AllThemes))]
    public void ForkCounterIsHiddenWhenThereIsNoPearlChain(string themeName)
    {
        var theme = Dashboard.ResolveTheme(themeName);
        var header = string.Join("\n", theme.BuildHeader(Ctx(Snap(prlForks: 0), 110)));
        Assert.DoesNotContain("fork", header, StringComparison.OrdinalIgnoreCase);
    }

    // Every theme must state the count in words an operator can read without
    // decoding the skin — same rule that keeps a stalled worker saying "STALL".
    [Theory]
    [MemberData(nameof(AllThemes))]
    public void ForkCounterIsStatedPlainlyInEveryTheme(string themeName)
    {
        var theme = Dashboard.ResolveTheme(themeName);

        var one = string.Join("\n", theme.BuildHeader(Ctx(Snap(prlForks: 1), 110)));
        Assert.Contains("1 fork survived", one, StringComparison.Ordinal);

        // Plural, because "1 forks survived" is the kind of detail that makes a
        // panel look unmaintained.
        var many = string.Join("\n", theme.BuildHeader(Ctx(Snap(prlForks: 4), 110)));
        Assert.Contains("4 forks survived", many, StringComparison.Ordinal);
    }

    // A dead card must stay visible on an ordinary terminal. STATUS is the last
    // column, so it is the first thing a clip eats — and losing it means the table
    // renders a dead card as a producing one, silently. Themes must let the sensor
    // columns yield instead (Panel.SizeNameColumn).
    //
    // This is a REGRESSION test with real history: measured at 0.3.1, classic lost
    // the status entirely at 80 columns, cyberpunk lost the stall age at 80, and
    // all three original themes lost it at 64. Each had hand-counted its name
    // column against a constant that never included the status text.
    //
    // Asserts on the stall AGE rather than a keyword, because the failure word is
    // deliberately per-theme ("STALL", "DOWNED", "STALLED") while every theme
    // reports how long the worker has been silent.
    [Theory]
    [MemberData(nameof(AllThemesAndWidths))]
    public void AStalledWorkerStaysVisibleAtEveryUsableWidth(string themeName, int inner)
    {
        var theme = Dashboard.ResolveTheme(themeName);
        var rows = theme.BuildHeader(Ctx(Snap(heartbeatAge: 47, sensors: true), inner));
        Assert.True(rows.Exists(r => r.Contains("47s", StringComparison.Ordinal)),
            $"{themeName} at inner={inner}: the stall was clipped away:\n{string.Join("\n", rows)}");
        foreach (var row in rows)
            Assert.True(Panel.DisplayWidth(row) <= inner, $"{themeName} at inner={inner}: row overflowed");
    }

    public static TheoryData<string, int> AllThemesAndWidths()
    {
        var d = new TheoryData<string, int>();
        foreach (var n in Dashboard.AllThemeNames())
            foreach (int w in new[] { 64, 80, 100, 110, 130 })
                d.Add(n, w);
        return d;
    }

    // REGRESSION: the fork counter first went on the rogue TITLE row, which is the
    // row the floor map is sized against — 17 extra columns pushed the map under
    // its minimum width and deleted it outright at inner=110, an entirely ordinary
    // terminal. Decoration must not silently evict other decoration, so the
    // counter moved to the party row, which has slack. Any future addition to the
    // rogue header owes the map the same check.
    [Fact]
    public void ForkCounterDoesNotEvictTheRogueFloorMap()
    {
        var theme = Dashboard.ResolveTheme("rogue");
        static bool HasMap(List<string> rows) =>
            rows.Exists(r => r.Contains('@') || r.Contains('#'));

        foreach (int inner in new[] { 100, 110, 130 })
        {
            var without = theme.BuildHeader(Ctx(Snap(prlForks: 0), inner));
            var with    = theme.BuildHeader(Ctx(Snap(prlForks: 1), inner));
            Assert.Equal(without.Count, with.Count);
            Assert.True(HasMap(without) == HasMap(with),
                $"inner={inner}: fork counter changed whether the floor map renders");
        }
    }

    [Fact]
    public void CyberpunkThemeResolvesAndMapsNodeClasses()
    {
        var theme = Dashboard.ResolveTheme("cyberpunk");
        Assert.Equal("cyberpunk", theme.Name);
        Assert.Equal("NETLOG", theme.EventsTitle);

        static Metrics.DashGpu Card(string name, bool cpu = false) =>
            new(0, name, 0, 0, 0, 0, 0, "", cpu);

        Assert.Equal("Battlemage", CyberTheme.ClassOf(Card("Intel(R) Arc(TM) B580 Graphics")));
        Assert.Equal("Alchemist",  CyberTheme.ClassOf(Card("Intel(R) Arc(TM) A770 Graphics")));
        Assert.Equal("Cyber-CPU",  CyberTheme.ClassOf(Card("AMD Ryzen 9 5900X", cpu: true)));
        Assert.Equal("Cyber-GPU",  CyberTheme.ClassOf(Card("NVIDIA GeForce RTX 4090")));

        string blockEv = theme.FormatEvent("share accepted for block");
        Assert.Contains("[JACKPOT]", blockEv, StringComparison.Ordinal);

        string ackEv = theme.FormatEvent("share accepted (diff 500.0K)");
        Assert.Contains("[ACK]", ackEv, StringComparison.Ordinal);
    }

    [Fact]
    public void AntigravityThemeResolvesAndFormatsEvents()
    {
        var theme = Dashboard.ResolveTheme("antigravity");
        Assert.Equal("antigravity", theme.Name);
        Assert.Equal("FLIGHT RECORDER", theme.EventsTitle);

        static Metrics.DashGpu Card(string name, bool cpu = false) =>
            new(0, name, 0, 0, 0, 0, 0, "", cpu);

        Assert.Equal("Battlemage", AntigravityTheme.ModuleClassOf(Card("Intel(R) Arc(TM) B580 Graphics")));
        Assert.Equal("Alchemist",  AntigravityTheme.ModuleClassOf(Card("Intel(R) Arc(TM) A770 Graphics")));
        Assert.Equal("Flight-Core", AntigravityTheme.ModuleClassOf(Card("AMD Ryzen 9 5900X", cpu: true)));
        Assert.Equal("ZeroG-Thruster", AntigravityTheme.ModuleClassOf(Card("NVIDIA GeForce RTX 4090")));

        string blockEv = theme.FormatEvent("share accepted for block");
        Assert.Contains("✦ DISCOVERY", blockEv, StringComparison.Ordinal);

        string ackEv = theme.FormatEvent("share accepted (diff 500.0K)");
        Assert.Contains("✦ PULSE ACK", ackEv, StringComparison.Ordinal);
    }
}


