using static Akoya.Miner.Observability.Themes.Panel;

namespace Akoya.Miner.Observability.Themes;

/// <summary>
/// The rig as the front page of a daily paper.
///
/// The other themes share one skeleton — title, pool row, summary row, table —
/// and change the nouns. This one changes the SHAPE. A front page does not lead
/// with a masthead full of statistics; it leads with the single most important
/// thing that happened, in the largest type on the page. So the loudest row here
/// is a generated <see cref="Headline"/> that rewrites itself from the rig's
/// state: a stalled card, a dead wire, editors spiking copy, or — on a good day —
/// the hashrate.
///
/// That is not decoration bolted onto the house rule, it IS the house rule.
/// "Flavour decorates the truth, it never replaces it" is usually a constraint a
/// skin has to work around; here the most prominent element on the panel is, by
/// construction, whatever an operator most needs to know. The metaphor makes the
/// panel MORE honest rather than less, which is the only excuse a joke theme
/// needs.
///
/// The vocabulary is real newsroom jargon and it maps almost too neatly:
///   • shares are FILED, rejects are SPIKED — an editor killing a story used to
///     mean literally impaling it on a metal spike
///   • the pool is THE WIRE, the service you file to
///   • a worker is a DESK, and its iteration time is its DEADLINE
///   • the block height is the ISSUE NUMBER: a paper that has run 98,549 editions
///
/// Layout notes: the masthead is centred, which nothing else in this codebase is,
/// and it degrades to a short form rather than wrapping on a narrow terminal. Row
/// count is fixed for a given context (one extra wire row when dual mining), so
/// the event pane below never resizes.
/// </summary>
internal sealed class BroadsheetTheme : IDashboardTheme
{
    public string Name => "broadsheet";
    public string EventsTitle => "LATE DISPATCHES";

    /// <summary>Event lines get a thin column rule, the way body copy sits beside
    /// a gutter. Deliberately faint — the log line underneath is the fact, and the
    /// prefix must never compete with it.</summary>
    public string FormatEvent(string line) => $" {Dim}▏{Reset} {line}";

    /// <summary>The lead story. Severity order, worst first — this is the row an
    /// operator reads before any other, so it must never be showing the hashrate
    /// while a card is dead. Returns plain text; the caller colours it.</summary>
    private static (string Text, string Colour) Headline(
        in Metrics.DashSnapshot snap, bool anyGpu, bool anyCpu)
    {
        // 1. A dead worker outranks everything. Named, and in the plain word
        //    STALLED — nobody should have to decode a metaphor to find the card
        //    that died.
        foreach (var g in snap.Gpus)
        {
            if (g.HeartbeatAgeSec >= 30)
                // Desk NUMBER first, model second. A rig with two identical B580s
                // learns nothing from "ARC B580 HAS STALLED" — the number is the
                // part that tells you which card to go and look at.
                return ($"PRESS HALTED — DESK {g.Id} ({ShortDeviceName(g.Name).ToUpperInvariant()}) "
                        + $"HAS STALLED, {g.HeartbeatAgeSec:F0}s SILENT", Red);
        }

        // 2. A pool we cannot reach. Shares mined now are shares thrown away.
        bool gpuDown = !string.IsNullOrEmpty(snap.PoolUrl) && !snap.Connected;
        bool cpuDown = !string.IsNullOrEmpty(snap.CpuPoolUrl) && !snap.CpuConnected;
        if (gpuDown || cpuDown)
        {
            string which = gpuDown && cpuDown ? "BOTH WIRES" : gpuDown ? "THE WIRE" : "THE CPU WIRE";
            return ($"{which} IS DOWN — NO CONTACT WITH THE POOL", Red);
        }

        // 3. Shares are landing but the pool is refusing them. Only once there is
        //    enough volume for the rate to mean anything: 1 reject out of 3 is
        //    noise, not a story.
        long total = snap.Accepted + snap.Rejected;
        if (total >= 20)
        {
            double pct = 100.0 * snap.Accepted / total;
            if (pct < 95.0)
                return ($"COPY IS BEING SPIKED — ONLY {pct:F1}% OF SHARES ACCEPTED", Yellow);
        }

        // 4. Nothing wrong. Lead with the rate, and let a block find take the
        //    front page when there is one to report.
        int desks = snap.Gpus.Length;
        if (desks == 0) return ("THE PRESSES ARE WARMING UP", Dim);

        string rate = (anyGpu && anyCpu
                ? $"{DisplayFormat.HashRate(snap.GpuHashesPerSec)} + {DisplayFormat.HashRate(snap.CpuHashesPerSec)}"
                : DisplayFormat.HashRate(snap.TotalHashesPerSec))
            .ToUpperInvariant();

        if (snap.BlockFinds > 0)
            return ($"STOP THE PRESSES — {snap.BlockFinds} BLOCK{(snap.BlockFinds == 1 ? "" : "S")} FOUND, "
                    + $"{rate} STILL ROLLING", Yellow);

        return ($"{rate} HOLDING ACROSS {desks} DESK{(desks == 1 ? "" : "S")}", Cyan);
    }

    public List<string> BuildHeader(in ThemeContext ctx)
    {
        var snap = ctx.Snap;
        int inner = ctx.Inner;
        var lines = new List<string>(16);

        bool anyCpu = false, anyGpu = false;
        foreach (var g in snap.Gpus) { if (g.IsCpu) anyCpu = true; else anyGpu = true; }
        bool dual = !string.IsNullOrEmpty(snap.PoolUrl) && !string.IsNullOrEmpty(snap.CpuPoolUrl);

        // ── Masthead ───────────────────────────────────────────────────────
        // Centred, which nothing else here is, and letter-spaced the way a paper
        // sets its own name. Falls back to the tight form before it would need to
        // wrap; Fit() clips as a backstop but a clipped masthead reads as a bug.
        string paper = inner >= 64 ? "T H E   A R C   H E R A L D" : "THE ARC HERALD";
        lines.Add(new string(' ', Math.Max(0, (inner - DisplayWidth(paper)) / 2))
                  + Bold + White + paper + Reset);

        // ── Edition line ───────────────────────────────────────────────────
        // The block height is the issue number — the count of editions this chain
        // has published. Fork count rides here too: it is provenance, which is
        // exactly what a masthead is for. Omitted entirely off Pearl.
        long headline = snap.BlockHeight > 0 ? snap.BlockHeight : snap.CpuBlockHeight;
        var edition = new System.Text.StringBuilder($"Vol. {ctx.Version}");
        if (headline > 0) edition.Append($" · No. {headline:N0}");
        if (snap.PrlForks > 0)
            edition.Append($" · {snap.PrlForks} fork{(snap.PrlForks == 1 ? "" : "s")} survived");
        edition.Append(" · 0% dev fee, forever · [q] put it to bed");
        lines.Add(Rule(inner, edition.ToString(), Dim));

        // ── The lead ───────────────────────────────────────────────────────
        var (leadText, leadColour) = Headline(snap, anyGpu, anyCpu);
        lines.Add($" {leadColour}{Bold}{leadText}{Reset}");

        // ── The wire(s) ────────────────────────────────────────────────────
        lines.Add(WireRow(inner, dual ? "WIRE GPU" : "WIRE",
            !string.IsNullOrEmpty(snap.PoolUrl) ? snap.PoolUrl : snap.CpuPoolUrl,
            !string.IsNullOrEmpty(snap.PoolUrl) ? snap.Connected : snap.CpuConnected,
            !string.IsNullOrEmpty(snap.PoolUrl) ? snap.LatencyMs : 0,
            $"press run {FormatUptime(ctx.Uptime)} "));
        if (dual)
            lines.Add(WireRow(inner, "WIRE CPU", snap.CpuPoolUrl, snap.CpuConnected, 0, ""));

        // ── Dateline: who filed, and how much of it stuck ──────────────────
        long filedTotal = snap.Accepted + snap.Rejected;
        double acceptPct = filedTotal > 0 ? 100.0 * snap.Accepted / filedTotal : 100.0;
        string pctCol = acceptPct >= 99 ? Green : acceptPct >= 95 ? Yellow : Red;
        string rate = anyCpu && anyGpu
            ? $"gpu {Bold}{Cyan}{DisplayFormat.HashRate(snap.GpuHashesPerSec)}{Reset}  cpu {Bold}{Cyan}{DisplayFormat.HashRate(snap.CpuHashesPerSec)}{Reset}"
            : $"{Bold}{Cyan}{DisplayFormat.HashRate(snap.TotalHashesPerSec)}{Reset}";
        string scoops = snap.BlockFinds > 0 ? $"  {Yellow}★{snap.BlockFinds} scoop{(snap.BlockFinds == 1 ? "" : "s")}{Reset}" : "";
        lines.Add(Line(inner,
            $" {Dim}DATELINE{Reset}  {rate}   filed {Green}{snap.Accepted:N0}{Reset}"
            + $"  spiked {Red}{snap.Rejected:N0}{Reset} ({pctCol}{acceptPct:F1}%{Reset}){scoops}",
            $"{snap.Worker}{WorkerBadge(snap.Worker)} desk "));

        // ── The desks ──────────────────────────────────────────────────────
        lines.Add(Rule(inner, "THE DESKS", Dim));

        // Sensors are Linux-only, so their columns appear only when something
        // actually reports — a permanently blank TEMP column on Windows is worse
        // than no column at all.
        bool anySensors = false;
        foreach (var g in snap.Gpus) { if (g.TempC is not null || g.PowerW is not null) { anySensors = true; break; } }

        // 40 = leading space + No.(3) + name gap + output(10) + deadline(8) +
        // filed/spiked(13) + their separators. Sensors are 1+6+1+7. See
        // Panel.SizeNameColumn for why STATUS gets reserved before either.
        var (nameW, showSensors) = SizeNameColumn(
            inner, fixedW: 40, statusW: DisplayWidth("STALLED 9999s"), sensorsW: 15,
            anySensors, minName: 8, maxName: 30);
        lines.Add($" {Dim}{"No.",-3} {PadVisible("DESK", nameW)} {"OUTPUT",-10} {"DEADLINE",-8} {"FILED/SPIKED",-13}"
                + (showSensors ? $" {"TEMP",-6} {"POWER",-7}" : "") + $" STATUS{Reset}");

        foreach (var g in snap.Gpus)
        {
            bool stalled = g.HeartbeatAgeSec >= 30;
            // "running late" is flavour; "STALLED" is not. The failure state keeps
            // the plain word and the red, exactly as every other theme does.
            // "late" rather than "running late": the status column is width-budgeted
            // against its longest string, so a wordier warning would have cost the
            // name column three characters on every row, forever.
            string status = g.HeartbeatAgeSec < 5 ? $"{Green}on deadline{Reset}"
                          : !stalled ? $"{Yellow}late {g.HeartbeatAgeSec:F0}s{Reset}"
                          : $"{Red}STALLED {g.HeartbeatAgeSec:F0}s{Reset}";
            // A stalled desk's last sample is frozen, not current — dim it so a
            // dead card cannot read as a producing one.
            string outText = DisplayFormat.HashRate(g.HashesPerSec);
            string output = PadVisible(stalled ? $"{Dim}{outText}{Reset}" : $"{Bold}{outText}{Reset}", 10);
            string label = g.IsCpu ? $"{Dim}cpu{Reset} " + g.Name : ShortDeviceName(g.Name);
            string desk = PadVisible(Clip(label, nameW), nameW);
            string filed = PadVisible($"{Green}✓{g.Accepted}{Reset}/{Red}✗{g.Rejected}{Reset}", 13);
            string sensors = showSensors
                ? " " + PadVisible(FormatTemp(g.TempC), 6) + " " + PadVisible(FormatPower(g.PowerW), 7)
                : "";
            lines.Add($" {g.Id,-3} {desk} {output} {g.IterMs,6:F1}ms {filed}{sensors} {status}");
        }

        return Fit(lines, inner);
    }

    /// <summary>One wire row: who we file to, and whether they are answering.</summary>
    private static string WireRow(int inner, string label, string url, bool connected, double rttMs, string right)
    {
        string dot = (connected ? Green : Red) + "●" + Reset;
        // "no contact" rather than "offline": on a front page the question is
        // whether the wire is carrying, and it keeps the metaphor from lying.
        string state = connected ? $"{Green}carrying{Reset}" : $"{Red}NO CONTACT{Reset}";
        string rtt = connected && rttMs > 0 ? $"  {Dim}{rttMs:F0}ms{Reset}" : "";
        return Line(inner, $" {Dim}{label,-8}{Reset} {dot} {url}{LoveNote(url)}  {state}{rtt}", right);
    }
}
