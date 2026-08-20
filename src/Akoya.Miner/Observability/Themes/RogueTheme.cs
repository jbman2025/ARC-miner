using Akoya.Miner.Mining;
using static Akoya.Miner.Observability.Themes.Panel;

namespace Akoya.Miner.Observability.Themes;

/// <summary>
/// Roguelike skin. The joke is Intel's: Arc generations are named Alchemist,
/// Battlemage, Celestial and Druid — A, B, C, D, in alphabetical order — so an
/// Arc miner's party classes come straight off the box.
///
/// The mapping is chosen to be *accurate*, not merely cute: block height really
/// is how deep you are, share difficulty really is what you have to beat, and a
/// stale share really is a swing at something that already died. Where flavour
/// and fact would diverge, fact wins — see the house rule on
/// <see cref="IDashboardTheme"/>. Concretely: a stalled worker is red and says
/// STALL in plain English on the same row as its HP bar, because the one thing
/// this panel must never do is make a dead card look like a resting one.
///
/// Everything here is ASCII (bars are '#' and '-'), which is both the genre's
/// native look and the safe choice for our hand-maintained wide-glyph table.
/// </summary>
internal sealed class RogueTheme : IDashboardTheme
{
    public string Name => "rogue";
    public string EventsTitle => "COMBAT LOG";

    /// <summary>Party level from lifetime accepted shares. Deliberately a slow
    /// log-ish curve: early levels arrive fast enough to notice, later ones stay
    /// meaningful over a long session rather than running away to LVL 900.</summary>
    internal static int LevelFor(long accepted)
    {
        if (accepted <= 0) return 1;
        int lvl = 1;
        long need = 8;          // shares for level 2
        long left = accepted;
        while (left >= need && lvl < 99)
        {
            left -= need;
            lvl++;
            need += need / 2 + 2;   // ~1.5x per level
        }
        return lvl;
    }

    /// <summary>Class name from the device string. Intel's own codenames are the
    /// classes; anything unrecognised stays honest rather than inventing lore.</summary>
    internal static string ClassOf(in Metrics.DashGpu g)
    {
        if (g.IsCpu) return "Hireling";
        string n = g.Name ?? "";
        if (n.Contains("Arc", StringComparison.OrdinalIgnoreCase))
        {
            // Model numbers are the reliable signal; the marketing name rarely
            // contains the architecture codename.
            if (n.Contains('B') && HasSeries(n, 'B')) return "Battlemage";
            if (HasSeries(n, 'A')) return "Alchemist";
            if (HasSeries(n, 'C')) return "Celestial";
            if (HasSeries(n, 'D')) return "Druid";
        }
        return "Adventurer";
    }

    // True when the name contains <letter> immediately followed by 3 digits
    // (A770, B580, C310 …) — the Arc model-number pattern.
    private static bool HasSeries(string name, char letter)
    {
        for (int i = 0; i + 3 < name.Length; i++)
        {
            if (char.ToUpperInvariant(name[i]) != letter) continue;
            if (char.IsDigit(name[i + 1]) && char.IsDigit(name[i + 2]) && char.IsDigit(name[i + 3]))
                return true;
        }
        return false;
    }

    /// <summary>HP tracks heartbeat freshness: full while the worker reports,
    /// draining as it goes quiet. Same thresholds as the classic theme's health
    /// column, so the two themes never disagree about who is dying.</summary>
    private static (int Filled, string Colour, string Status) Vitals(double ageSec)
    {
        if (ageSec < 5)  return (8, Green,  "ready");
        if (ageSec < 30) return (4, Yellow, $"wounded {ageSec:F0}s");
        return (0, Red, $"DOWNED {ageSec:F0}s");
    }

    public List<string> BuildHeader(in ThemeContext ctx)
    {
        var snap = ctx.Snap;
        int inner = ctx.Inner;
        var lines = new List<string>(16);

        long total = snap.Accepted + snap.Rejected;
        double pct = total > 0 ? 100.0 * snap.Accepted / total : 100.0;

        // Fold this tick into the lifetime totals and the moment flags. Done
        // here, once, so everything below renders from a single consistent read.
        var prog = RogueProgress.Shared;
        long now = DateTime.UtcNow.Ticks;
        prog.Observe(snap, now);
        bool blockLit = prog.BlockFindLit(now);

        // ── Title: depth is the block height ───────────────────────────────
        // When dual mining, the GPU and CPU halves follow DIFFERENT chains, so
        // there is no single "depth". The title carries the GPU chain (the
        // headline algo) and each guild row below states its own height —
        // depth belongs to the chain you are mining, not to the party.
        bool dual = !string.IsNullOrEmpty(snap.PoolUrl) && !string.IsNullOrEmpty(snap.CpuPoolUrl);
        long headline = snap.BlockHeight > 0 ? snap.BlockHeight : snap.CpuBlockHeight;
        string depth = headline > 0 ? $"Depth {headline:N0}" : "Depth unknown";
        // A block find is the rarest thing that happens to a miner. The title
        // goes gold for twenty seconds; everything else about the row is
        // unchanged, so nothing an operator needs is displaced by the fanfare.
        string titleCol = blockLit ? Yellow : Cyan;
        string titleTail = blockLit
            ? $"{Yellow}{Bold} ★ LEGENDARY DROP — BLOCK FOUND ★{Reset}"
            : $"{Dim} · 0% Dev Fee FOREVER · [q] flee{Reset}";
        string title = $"{titleCol}{Bold} ARC MINER v{ctx.Version}{Reset}{Dim} · {depth}{Reset}{titleTail}";

        // ── The floor map ──────────────────────────────────────────────────
        // Occupies the right-hand gutter of the first three rows. Three is the
        // floor, not a preference: a single-pool run only HAS three rows up here
        // (title, guild, party), so anything taller would render on a dual-pool
        // rig and vanish on a CPU-only one.
        //
        // Build all three rows' content FIRST, then size the map against the
        // widest of them. Sizing against the title alone looked right until a
        // long pool URL (stratum+tcp://pearl.alphapool.tech:3333) made the guild
        // row the longest, and the map silently ate that row's depth readout.
        // The map is the thing that yields here — it is decoration and the
        // depth is not.
        string runLabel   = $"run {FormatUptime(ctx.Uptime)} ";
        string partyLabel = $"party \"{snap.Worker}\"{WorkerBadge(snap.Worker)} ";
        // Right-hand labels differ in length, so pad them to a common width;
        // otherwise the map starts at a different column on each row and reads
        // as three unrelated strips.
        int labelW = Math.Max(DisplayWidth(runLabel), DisplayWidth(partyLabel));

        // ── Guild (pool) rows ──────────────────────────────────────────────
        string guild1, guild2;
        if (dual)
        {
            // Guild 1 does NOT repeat its depth: the title already carries the
            // GPU chain's height, so printing it again cost ~17 columns of the
            // gutter for nothing — enough to squeeze the map off a 96-column
            // dual-mining panel entirely. Guild 2's height is the CPU chain's
            // and appears nowhere else, so it stays.
            guild1 = GuildRow("Guild  ", snap.PoolUrl, snap.Connected, snap.LatencyMs, 0);
            guild2 = GuildRow("Guild 2", snap.CpuPoolUrl, snap.CpuConnected, 0, snap.CpuBlockHeight);
        }
        else
        {
            bool gpuSide = !string.IsNullOrEmpty(snap.PoolUrl);
            // Height is already in the title on a single-sided run; repeating it
            // on the guild row would just be noise.
            guild1 = GuildRow("Guild  ", gpuSide ? snap.PoolUrl : snap.CpuPoolUrl,
                              gpuSide ? snap.Connected : snap.CpuConnected,
                              gpuSide ? snap.LatencyMs : 0, 0);
            guild2 = "";
        }

        // ── Party summary ──────────────────────────────────────────────────
        bool anyCpu = false, anyGpu = false;
        foreach (var g in snap.Gpus) { if (g.IsCpu) anyCpu = true; else anyGpu = true; }
        string dps = anyCpu && anyGpu
            ? $"party {Bold}{Cyan}{DisplayFormat.HashRate(snap.GpuHashesPerSec)}{Reset} + {Bold}{Cyan}{DisplayFormat.HashRate(snap.CpuHashesPerSec)}{Reset}"
            : $"party {Bold}{Cyan}{DisplayFormat.HashRate(snap.TotalHashesPerSec)}{Reset}";
        string pctCol = pct >= 99 ? Green : pct >= 95 ? Yellow : Red;

        // Moments, each confined to an EXISTING row so the header height never
        // changes — the event pane is sized from what the header leaves, and a
        // header that grew on a block find would make the panel jump.
        string moment =
            prog.FirstBloodLit(now)   ? $"  {Yellow}{Bold}FIRST BLOOD{Reset}"
          : prog.PersonalBestLit(now) ? $"  {Yellow}{Bold}▲ PB{Reset}"
          : prog.LifetimeBlocks > 0   ? $"  {Yellow}★{prog.LifetimeBlocks}{Reset}"
          : "";
        // Forks survived. A consensus fork is the one thing that can end a rig
        // silently — the rank-penalty fork halved reward without a single reject
        // — so surviving one is a real trophy and belongs on the party row next
        // to the other lifetime counts.
        //
        // It lives HERE and not in the title, which is where it reads best,
        // because the title is already the row the floor map is sized against:
        // adding 17 columns there pushed the map below its minimum width and
        // deleted it outright at inner=110, a very ordinary terminal. The party
        // row has ~17 columns of slack before it overtakes the guild row, so the
        // counter is free here. Same reason guild 1 does not repeat its depth.
        // Wording matches the classic theme — flavour decorates the truth, it
        // does not rename it.
        string forks = snap.PrlForks > 0
            ? $"   {Dim}{snap.PrlForks} fork{(snap.PrlForks == 1 ? "" : "s")} survived{Reset}"
            : "";
        string party = $" {dps}   slain {Green}{snap.Accepted}{Reset} / lost {Red}{snap.Rejected}{Reset} ({pctCol}{pct:F1}%{Reset}){moment}{forks}";

        // The three rows the map shares a gutter with: title, guild 1, and then
        // guild 2 when dual-mining or the party summary when not.
        string third = dual ? guild2 : party;
        int widest = Math.Max(DisplayWidth(title),
                     Math.Max(DisplayWidth(guild1), DisplayWidth(third)));

        int mapW = MapWidth(inner, widest, labelW);
        var map = mapW > 0
            ? FloorMap.Render(headline, snap.Accepted, mapW, MapRows)
            : Array.Empty<string>();

        // One right-aligned unit per row: map segment, then the row's label.
        string Gutter(int row, string label) =>
            map.Length > row
                ? FloorMap.Colourise(map[row]) + " " + PadLeftVisible(label, labelW)
                : PadLeftVisible(label, labelW);

        lines.Add(map.Length > 0 ? Line(inner, title, Gutter(0, "")) : title);
        lines.Add(Line(inner, guild1, Gutter(1, runLabel)));
        if (dual)
        {
            lines.Add(Line(inner, guild2, Gutter(2, "")));
            lines.Add(Line(inner, party, partyLabel));
        }
        else
        {
            lines.Add(Line(inner, party, Gutter(2, partyLabel)));
        }

        // ── Party roster ───────────────────────────────────────────────────
        lines.Add(Rule(inner, "PARTY", Dim));
        // "HEAT" is the temperature, plainly labelled. Tempting to call it
        // something in-world, but this is the number an operator acts on.
        bool anySensors = false;
        foreach (var g in snap.Gpus) { if (g.TempC is not null) { anySensors = true; break; } }

        // 44 = leading space + id(2) + name gap + class(11) + lvl(4) + dps(10) +
        // hp bar(10) + their separators. HEAT is 1+6.
        var (nameW, showHeat) = SizeNameColumn(
            inner, fixedW: 44, statusW: DisplayWidth("wounded 9999s"), sensorsW: 7,
            anySensors, minName: 8, maxName: 26);
        lines.Add($" {Dim}{"#",-2} {PadVisible("NAME", nameW)} {"CLASS",-11} {"LVL",-4} {"DPS",-10} {"HP",-10}"
                + (showHeat ? $" {"HEAT",-6}" : "") + $" STATUS{Reset}");

        // Level comes from LIFETIME shares, not this session's. A per-session
        // level resets to 1 on every launch, which made the whole progression
        // idea decorative — a long-running rig would show the same LVL 1 as a
        // fresh install. Party members share the rig's level: they mine the same
        // work, and per-card lifetime counters would need per-card persistence
        // keyed on hardware identity for no real gain.
        int partyLevel = LevelFor(prog.LifetimeShares);
        foreach (var g in snap.Gpus)
        {
            var (filled, colour, status) = Vitals(g.HeartbeatAgeSec);
            bool downed = filled == 0;

            string bar = colour + "[" + new string('#', filled) + new string('-', 8 - filled) + "]" + Reset;
            string hrText = DisplayFormat.HashRate(g.HashesPerSec);
            // Frozen sample on a downed worker — dim it, exactly as classic does.
            string dpsCell = PadVisible(downed ? $"{Dim}{hrText}{Reset}" : $"{Bold}{hrText}{Reset}", 10);
            string name = PadVisible(Clip(ShortDeviceName(g.Name), nameW), nameW);
            string cls  = PadVisible(ClassOf(g), 11);
            string lvl  = PadVisible(partyLevel.ToString(), 4);

            string heat = showHeat ? " " + PadVisible(FormatTemp(g.TempC), 6) : "";
            lines.Add($" {g.Id,-2} {name} {Magenta}{cls}{Reset} {lvl} {dpsCell} {bar}{heat} {colour}{status}{Reset}");
        }

        return Fit(lines, inner);
    }

    /// <summary>Rows the floor map occupies. Three, because a single-pool run
    /// only has three rows above the party table.</summary>
    private const int MapRows = 3;

    /// <summary>How wide the map may be, given what the widest of the three rows
    /// already uses. Returns 0 — draw nothing — rather than squeezing out a
    /// two-column sliver on a narrow terminal.
    ///
    /// The title is the binding constraint: the guild and party rows are
    /// shorter, so a width that fits the title fits all three.</summary>
    private static int MapWidth(int inner, int titleWidth, int labelWidth)
    {
        int room = inner - titleWidth - labelWidth - 2;
        if (room < 12) return 0;
        return Math.Min(room, 28);
    }

    private static string GuildRow(string label, string url, bool connected, double rttMs, long height)
    {
        string dot = (connected ? Green : Red) + "*" + Reset;
        string state = connected ? Green + "allied" + Reset : Red + "UNREACHABLE" + Reset;
        string rtt = rttMs > 0 ? $"  {rttMs:F0}ms" : "";
        string depth = height > 0 ? $"  {Dim}depth {height:N0}{Reset}" : "";
        return $" {label} {dot} {url}{LoveNote(url)}  {state}{rtt}{depth}";
    }

    /// <summary>Combat-log voice: a short tag in front of the line the miner
    /// actually logged, which is preserved VERBATIM after it.
    ///
    /// The tag is the flavour; the line is the fact. An operator reading a
    /// reject reason must still get the reject reason, so nothing here rewrites
    /// or paraphrases the original — worst case the tag is wrong and the truth
    /// is still sitting right next to it.
    ///
    /// Tags are padded to a fixed width so the log stays a column rather than a
    /// ragged left edge, and every one is ASCII.</summary>
    public string FormatEvent(string line)
    {
        var (tag, colour) = Verb(line);
        // Pad on the visible text, then colour — colouring first would make the
        // ANSI codes count toward the width and shred the alignment.
        return " " + colour + PadVisible(tag, 9) + Reset + " " + line;
    }

    private static (string Tag, string Colour) Verb(string line)
    {
        // Order matters: "stale" must be tested before "accepted", because a
        // stale-share line often mentions both. "stalled" (a dead worker) is a
        // different event from "stale" (a dead job) and must not borrow its tag.
        if (Has(line, "stalled") || Has(line, "no progress")) return ("DOWNED", Red);
        if (Has(line, "block"))          return ("LEGENDARY", Yellow);
        if (Has(line, "stale"))          return ("corpse", Dim);
        if (Has(line, "duplicate"))      return ("echo", Dim);
        if (Has(line, "reject") || Has(line, "invalid")) return ("parried", Red);
        if (Has(line, "accepted"))       return ("hit", Green);
        if (Has(line, "σ install") || Has(line, "new job")) return ("descend", Blue);
        if (Has(line, "connect"))        return ("guild", Cyan);
        if (Has(line, "disconnect") || Has(line, "reconnect")) return ("fled", Yellow);
        return ("", Reset);
    }

    private static bool Has(string s, string needle)
        => s.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
