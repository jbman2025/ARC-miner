using static Akoya.Miner.Observability.Themes.Panel;

namespace Akoya.Miner.Observability.Themes;

/// <summary>
/// A dashboard that tells you what the numbers MEAN, instead of showing you the
/// numbers.
///
/// Every other theme here — plain, roguelike, cyberdeck, newspaper, orbital — is
/// the same instrument wearing a different costume: labelled fields in a table,
/// scanned by an operator who already knows what `iter 72.6ms` and `✓1282/✗3`
/// signify. This one is not a costume. It changes what the panel is FOR.
///
/// It writes sentences. It converts the snapshot into the things a person
/// actually wants to know and cannot read directly off a table:
///
///   • how often shares are landing, in human cadence ("about one a minute")
///   • what a dead card is COSTING, as a share of the rig's output
///   • how long "up 02:14:07" actually is
///   • and, first and loudest, whether anything needs doing at all
///
/// The last one is the thesis. A cockpit shows you everything always and leaves
/// the judgement to you; a copilot does the judging and speaks up. On a healthy
/// rig this panel's opening line is "Nothing needs your attention." — which is
/// the single most useful sentence a monitoring tool can say, and which no other
/// theme here can say, because none of them decide anything.
///
/// It is also the only theme legible to somebody who does not mine. That is not
/// a side effect; a UI that requires domain fluency to read is a UI that fails
/// the person most likely to be woken by it.
///
/// House rule, as everywhere: flavour decorates the truth, it never replaces it.
/// Prose is a much easier place to hide a hedge than a table is, so every claim
/// here is derived from the snapshot and the uncomfortable ones are stated
/// bluntly — "Nothing you mine right now will count" is exactly what an
/// unreachable pool means, and softening it would be a lie of tone.
///
/// Row count is fixed for a given context (a verdict, two situation lines, one
/// line per worker, a ledger, and the blanks between them), so the event pane
/// below never resizes.
/// </summary>
internal sealed class PlainlyTheme : IDashboardTheme
{
    public string Name => "plainly";
    public string EventsTitle => "WHAT HAPPENED";

    /// <summary>Events keep a wider indent than the prose, so the briefing reads
    /// as the page and the log reads as a footnote to it.</summary>
    public string FormatEvent(string line) => "   " + line;

    /// <summary>Small counts read better as words in a sentence; past ten the
    /// numeral is clearer than the word. Newspapers settle on the same rule.</summary>
    private static string Spell(int n) => n switch
    {
        1 => "One", 2 => "Two", 3 => "Three", 4 => "Four", 5 => "Five",
        6 => "Six", 7 => "Seven", 8 => "Eight", 9 => "Nine", 10 => "Ten",
        _ => n.ToString("N0"),
    };

    /// <summary>"2 hours 14 minutes" — an operator should not have to parse
    /// 02:14:07 to answer "how long has this been going?".</summary>
    private static string Humanise(TimeSpan t)
    {
        if (t.TotalSeconds < 90) return $"{(int)t.TotalSeconds} seconds";
        if (t.TotalMinutes < 90)
        {
            int m = (int)t.TotalMinutes;
            return $"{m} minute{(m == 1 ? "" : "s")}";
        }
        if (t.TotalHours < 48)
        {
            int h = (int)t.TotalHours, m = t.Minutes;
            return m == 0 ? $"{h} hours" : $"{h} hour{(h == 1 ? "" : "s")} {m} minute{(m == 1 ? "" : "s")}";
        }
        int d = (int)t.TotalDays;
        return $"{d} days {t.Hours} hour{(t.Hours == 1 ? "" : "s")}";
    }

    /// <summary>Share cadence as a human interval. "1282 accepted" says nothing
    /// about whether the rig is doing well; "about one every 6 seconds" does.</summary>
    private static string Cadence(long accepted, TimeSpan up)
    {
        if (accepted <= 0 || up.TotalSeconds < 30) return "";
        double secsEach = up.TotalSeconds / accepted;
        if (secsEach < 1)   return $", about {1 / secsEach:F0} a second";
        if (secsEach < 90)  return $", about one every {secsEach:F0} seconds";
        if (secsEach < 5400) return $", about one every {secsEach / 60:F0} minutes";
        return $", about one every {secsEach / 3600:F1} hours";
    }

    /// <summary>The opening line: what, if anything, the operator should do. This
    /// is the theme's whole reason to exist, so it is ordered by consequence and
    /// it never softens bad news.</summary>
    private static (string Text, string Colour) Verdict(
        in Metrics.DashSnapshot snap, TimeSpan up)
    {
        // A dead card first, named, with what it is costing. The age is stated
        // because "has stopped" without a duration is not actionable — a card
        // silent for 31 seconds and one silent for an hour are different problems.
        double lost = 0, total = 0;
        Metrics.DashGpu? dead = null;
        int deadCount = 0;
        foreach (var g in snap.Gpus)
        {
            total += g.HashesPerSec;
            if (g.HeartbeatAgeSec >= 30)
            {
                lost += g.HashesPerSec;
                deadCount++;
                dead ??= g;
            }
        }
        if (dead is { } d)
        {
            string who = deadCount > 1
                ? $"{deadCount} of your {snap.Gpus.Length} workers have stopped"
                : $"Worker {d.Id} ({ShortDeviceName(d.Name)}) has stopped";
            string cost = total > 0 && lost > 0
                ? $" That is roughly {100.0 * lost / total:F0}% of your output."
                : "";
            return ($"{who} — silent for {d.HeartbeatAgeSec:F0}s.{cost}", Red);
        }

        // A pool we cannot reach. Blunt on purpose: this is the failure people
        // most often watch tick along without realising it is happening.
        bool gpuDown = !string.IsNullOrEmpty(snap.PoolUrl) && !snap.Connected;
        bool cpuDown = !string.IsNullOrEmpty(snap.CpuPoolUrl) && !snap.CpuConnected;
        if (gpuDown && cpuDown)
            return ("Neither pool is answering. Nothing you mine right now will count.", Red);
        if (gpuDown || cpuDown)
            return ($"The {(gpuDown ? "GPU" : "CPU")} pool is not answering. "
                    + "Nothing that half mines right now will count.", Red);

        long filed = snap.Accepted + snap.Rejected;
        if (filed >= 20)
        {
            double reject = 100.0 * snap.Rejected / filed;
            if (reject > 5.0)
                return ($"The pool is turning away {reject:F1}% of your shares — worth looking into.", Yellow);
        }

        // Connected and healthy but nothing has landed yet. Only worth saying
        // once enough time has passed that silence is informative; on a high
        // difficulty a few quiet minutes are normal, not a fault.
        if (snap.Accepted == 0 && up.TotalMinutes >= 10 && snap.Gpus.Length > 0)
            return ("Running, but no shares accepted yet. Give it longer, or check the difficulty.", Yellow);

        if (snap.Gpus.Length == 0)
            return ("Starting up.", Dim);

        return ("Nothing needs your attention.", Green);
    }

    public List<string> BuildHeader(in ThemeContext ctx)
    {
        var snap = ctx.Snap;
        int inner = ctx.Inner;
        var lines = new List<string>(16);

        bool anyCpu = false, anyGpu = false;
        foreach (var g in snap.Gpus) { if (g.IsCpu) anyCpu = true; else anyGpu = true; }

        // ── The verdict ────────────────────────────────────────────────────
        // Full width, with nothing right-aligned beside it. The version and quit
        // hint used to sit here and truncated the verdict mid-sentence — the one
        // line on the panel that must never be clipped was being clipped by the
        // least important text on it. They now live in the footer.
        var (verdict, colour) = Verdict(snap, ctx.Uptime);
        lines.Add($" {colour}{Bold}{verdict}{Reset}");
        lines.Add("");

        // ── The situation ──────────────────────────────────────────────────
        // Group identical cards so a homogeneous rig reads as "Two Arc B580s"
        // rather than enumerating the same model twice.
        string fleet;
        if (snap.Gpus.Length == 0) fleet = "No workers yet";
        else
        {
            string first = snap.Gpus[0].IsCpu ? snap.Gpus[0].Name : ShortDeviceName(snap.Gpus[0].Name);
            bool uniform = true;
            foreach (var g in snap.Gpus)
            {
                string n = g.IsCpu ? g.Name : ShortDeviceName(g.Name);
                if (!string.Equals(n, first, StringComparison.Ordinal)) { uniform = false; break; }
            }
            fleet = uniform
                ? $"{Spell(snap.Gpus.Length)} {first}{(snap.Gpus.Length == 1 ? "" : "s")}"
                : $"{Spell(snap.Gpus.Length)} workers";
        }

        bool dual = !string.IsNullOrEmpty(snap.PoolUrl) && !string.IsNullOrEmpty(snap.CpuPoolUrl);
        string where = !string.IsNullOrEmpty(snap.PoolUrl) ? snap.PoolUrl : snap.CpuPoolUrl;
        if (string.IsNullOrEmpty(where)) where = "no pool configured";
        lines.Add($" {fleet}, mining at {Cyan}{where}{Reset} for {Humanise(ctx.Uptime)}.");
        // When dual mining the halves are on DIFFERENT pools and different coins.
        // One pool name against three workers reads as though they all file to the
        // same place, which is exactly the kind of quiet untruth prose makes easy.
        if (dual)
            lines.Add($" The CPU half files separately, to {Cyan}{snap.CpuPoolUrl}{Reset}.");

        // ── What that is producing, and whether it is landing ───────────────
        string rate = anyCpu && anyGpu
            ? $"{Bold}{DisplayFormat.HashRate(snap.GpuHashesPerSec)}{Reset} on the GPUs and "
              + $"{Bold}{DisplayFormat.HashRate(snap.CpuHashesPerSec)}{Reset} on the CPU"
            : $"{Bold}{DisplayFormat.HashRate(snap.TotalHashesPerSec)}{Reset}";
        long filed = snap.Accepted + snap.Rejected;
        // The cadence clause is the first thing to go when dual mining, where the
        // rate half of the sentence is already twice as long — it is the least
        // load-bearing phrase here, and losing it beats clipping the accept rate.
        string cadence = dual ? "" : Cadence(snap.Accepted, ctx.Uptime);
        string landing = filed == 0
            ? "Nothing has been submitted yet."
            : snap.Rejected == 0
                ? $"The pool has taken all {snap.Accepted:N0} of them{cadence}."
                : $"The pool has taken {snap.Accepted:N0} and turned away {snap.Rejected:N0}"
                  + $" ({100.0 * snap.Accepted / filed:F1}%){cadence}.";
        // Two sentences, one line — except when dual mining, where the rate clause
        // alone names two figures and the pair no longer fits. Row count stays
        // deterministic because it keys off the context, not off string lengths.
        if (dual)
        {
            lines.Add($" That is {rate}.");
            lines.Add($" {landing}");
        }
        else
        {
            lines.Add($" That is {rate}. {landing}");
        }
        lines.Add("");

        // ── One sentence per worker ────────────────────────────────────────
        // Not a table: the point is that each line is readable on its own, and a
        // failing one says so in words rather than by a column turning red.
        foreach (var g in snap.Gpus)
        {
            string who = g.IsCpu ? g.Name : ShortDeviceName(g.Name);
            string temp = g.TempC is { } t ? $", {t:F0}°C" : "";
            string body = g.HeartbeatAgeSec >= 30
                ? $"{Red}SILENT for {g.HeartbeatAgeSec:F0}s{Reset} — last seen at {DisplayFormat.HashRate(g.HashesPerSec)}{temp}"
                : g.HeartbeatAgeSec >= 5
                    ? $"{Yellow}slow to report ({g.HeartbeatAgeSec:F0}s){Reset} at {DisplayFormat.HashRate(g.HashesPerSec)}{temp}"
                    : $"{Green}healthy{Reset} at {Bold}{DisplayFormat.HashRate(g.HashesPerSec)}{Reset}{temp}";
            lines.Add($"   {Dim}Worker {g.Id}{Reset} {who} — {body}.");
        }
        lines.Add("");

        // ── The ledger ─────────────────────────────────────────────────────
        // Lifetime facts, stated once, at the bottom where a footer belongs.
        var record = new List<string>(3);
        if (snap.PrlForks > 0)
            record.Add($"{snap.PrlForks} fork{(snap.PrlForks == 1 ? "" : "s")} survived");
        if (snap.BlockFinds > 0)
            record.Add($"{snap.BlockFinds} block{(snap.BlockFinds == 1 ? "" : "s")} found");
        long height = snap.BlockHeight > 0 ? snap.BlockHeight : snap.CpuBlockHeight;
        if (height > 0) record.Add($"chain at block {height:N0}");
        lines.Add(Line(inner,
            record.Count > 0
                ? $" {Dim}For the record: {string.Join(", ", record)}.{Reset}"
                : $" {Dim}For the record: nothing to report yet.{Reset}",
            $"{Dim}arc-miner {ctx.Version} · [q] quit{Reset} "));

        return Fit(lines, inner);
    }
}
