using Akoya.Miner.Mining;
using static Akoya.Miner.Observability.Themes.Panel;

namespace Akoya.Miner.Observability.Themes;

/// <summary>
/// The default panel: rig summary, per-worker table, events. Left-anchored with
/// section rules and no right-hand vertical border.
///
/// Box-drawing with a fixed right edge is fragile: glyphs like ● ✓ ✗ and the ℹ️
/// log emoji render 2 columns wide in most terminals but there is no portable way
/// to know that, so any right border visibly drifts. A rule-and-rows layout
/// sidesteps the whole problem — the only width math left is right-aligning the
/// summary values, where a 1-col drift is invisible.
/// </summary>
internal sealed class ClassicTheme : IDashboardTheme
{
    public string Name => "classic";
    public string EventsTitle => "EVENTS";

    /// <summary>Per-pool chain height, or nothing when the pool's dialect does
    /// not carry one (csd's Bitcoin-stratum notify buries height in the
    /// coinbase, so it genuinely has none to report).</summary>
    private static string HeightTag(long height)
        => height > 0 ? $"  {Dim}height {height:N0}{Reset}" : "";

    public List<string> BuildHeader(in ThemeContext ctx)
    {
        var snap = ctx.Snap;
        int inner = ctx.Inner;
        var lines = new List<string>(16);

        // ── Title ──────────────────────────────────────────────────────────
        // Height in the title mirrors the rogue theme. When dual mining the two
        // halves follow DIFFERENT chains, so the title carries the GPU one and
        // each pool row states its own — a single figure here would show the
        // CPU chain's height against a GPU pool, which is what it used to do.
        long headline = snap.BlockHeight > 0 ? snap.BlockHeight : snap.CpuBlockHeight;
        string height = headline > 0 ? $" · height {headline:N0}" : "";
        // Pearl fork counter. Rides in the existing title row — the header must
        // stay a fixed number of rows or the event pane resizes under it — and is
        // omitted entirely off Pearl rather than shown as 0.
        string forks = snap.PrlForks > 0
            ? $" · {snap.PrlForks} fork{(snap.PrlForks == 1 ? "" : "s")} survived"
            : "";
        lines.Add($"{Cyan}{Bold} ARC MINER v{ctx.Version}{Reset}{Dim}{height}{forks} · 0% Dev Fee FOREVER · [q] quit{Reset}");

        // ── Rig summary ────────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(snap.PoolUrl) && !string.IsNullOrEmpty(snap.CpuPoolUrl))
        {
            string dotG = (snap.Connected ? Green : Red) + "●" + Reset;
            string connG = snap.Connected ? Green + "connected" + Reset : Red + "offline" + Reset;
            string rttG = snap.LatencyMs > 0 ? $"{snap.LatencyMs:F0}ms" : "—";
            lines.Add(Line(inner,
                $" Pool GPU  {dotG} {snap.PoolUrl}{LoveNote(snap.PoolUrl)}  {connG}  rtt {rttG}{HeightTag(snap.BlockHeight)}",
                $"up {FormatUptime(ctx.Uptime)} "));

            string dotC = (snap.CpuConnected ? Green : Red) + "●" + Reset;
            string connC = snap.CpuConnected ? Green + "connected" + Reset : Red + "offline" + Reset;
            lines.Add(Line(inner,
                $" Pool CPU  {dotC} {snap.CpuPoolUrl}{LoveNote(snap.CpuPoolUrl)}  {connC}{HeightTag(snap.CpuBlockHeight)}",
                $""));
        }
        else
        {
            string url = !string.IsNullOrEmpty(snap.PoolUrl) ? snap.PoolUrl : snap.CpuPoolUrl;
            bool connected = !string.IsNullOrEmpty(snap.PoolUrl) ? snap.Connected : snap.CpuConnected;
            string dot = (connected ? Green : Red) + "●" + Reset;
            string conn = connected ? Green + "connected" + Reset : Red + "offline" + Reset;
            string rtt = (!string.IsNullOrEmpty(snap.PoolUrl) && snap.LatencyMs > 0) ? $"{snap.LatencyMs:F0}ms" : "—";
            lines.Add(Line(inner,
                $" Pool      {dot} {url}{LoveNote(url)}  {conn}  rtt {rtt}",
                $"up {FormatUptime(ctx.Uptime)} "));
        }

        long total = snap.Accepted + snap.Rejected;
        double pct = total > 0 ? 100.0 * snap.Accepted / total : 100.0;
        string pctCol = pct >= 99 ? Green : pct >= 95 ? Yellow : Red;
        string shares = $"{Green}✓{snap.Accepted}{Reset} / {Red}✗{snap.Rejected}{Reset}  ({pctCol}{pct:F1}%{Reset})";
        string finds = snap.BlockFinds > 0 ? $"  {Yellow}★{snap.BlockFinds} finds{Reset}" : "";

        // Only when dual mining do the GPU and CPU halves run different
        // algorithms, making one summed hashrate a comparison of numbers that
        // aren't comparable (a pearl MH/s against a RandomX KH/s). Split the
        // rate in that case; a single-sided run keeps the one headline number.
        // Keyed on which rows exist, not on their rates, so the layout doesn't
        // flip back and forth while a half is still spinning up at 0 H/s.
        bool anyCpu = false, anyGpu = false;
        foreach (var g in snap.Gpus) { if (g.IsCpu) anyCpu = true; else anyGpu = true; }
        string rate = anyCpu && anyGpu
            ? $"gpu {Bold}{Cyan}{DisplayFormat.HashRate(snap.GpuHashesPerSec)}{Reset}  cpu {Bold}{Cyan}{DisplayFormat.HashRate(snap.CpuHashesPerSec)}{Reset}"
            : $"{Bold}{Cyan}{DisplayFormat.HashRate(snap.TotalHashesPerSec)}{Reset}";
        lines.Add(Line(inner,
            $" Rig   {rate}   shares {shares}{finds}",
            $"worker {snap.Worker}{WorkerBadge(snap.Worker)} "));

        // ── Per-worker table ───────────────────────────────────────────────
        // "GPUs" only when they really are all GPUs: a CPU algo occupies a row
        // here too (Metrics appends it after the GPU slots), so a gr/rx/nm run
        // was listing its CPU worker under a GPU heading.
        lines.Add(Rule(inner, anyCpu ? "WORKERS" : "GPUs", Dim));
        // Give the name column whatever is left after the fixed-width columns,
        // so the table degrades gracefully in a narrow window instead of wrapping.
        // Sensors are Linux-only (sysfs hwmon), so the columns appear only when
        // something actually reports — a permanently blank TEMP column on
        // Windows would be worse than no column at all.
        bool anySensors = false;
        foreach (var g in snap.Gpus) { if (g.TempC is not null || g.PowerW is not null) { anySensors = true; break; } }

        // 41 = leading space + id(2) + name gap + hashrate(10) + diff(6) +
        // iter(7) + shares(9) + their separators. Sensors are 1+6+1+7.
        var (nameW, showSensors) = SizeNameColumn(
            inner, fixedW: 41, statusW: DisplayWidth("● STALL 9999s"), sensorsW: 15,
            anySensors, minName: 8, maxName: 32);
        lines.Add($" {Dim}{"#",-2} {PadVisible("NAME", nameW)} {"HASHRATE",-10} {"DIFF",-6} {"ITER",-6} {"SHARES",-9}"
                + (showSensors ? $" {"TEMP",-6} {"POWER",-7}" : "") + $" HEALTH{Reset}");
        foreach (var g in snap.Gpus)
        {
            // Heartbeat age resets to ~0 on every progress tick, so a healthy
            // worker sits at 0.0s — show "live" rather than a frozen counter,
            // and only surface the stale age once it actually starts climbing.
            bool stalled = g.HeartbeatAgeSec >= 30;
            string health = g.HeartbeatAgeSec < 5 ? $"{Green}● live{Reset}"
                          : g.HeartbeatAgeSec < 30 ? $"{Yellow}● stale {g.HeartbeatAgeSec:F0}s{Reset}"
                          : $"{Red}● STALL {g.HeartbeatAgeSec:F0}s{Reset}";
            // A stalled worker's last hashrate sample is frozen, not current —
            // dim it so a dead card doesn't read as a producing one.
            string hrText = DisplayFormat.HashRate(g.HashesPerSec);
            string hr = PadVisible(stalled ? $"{Dim}{hrText}{Reset}" : $"{Bold}{hrText}{Reset}", 10);
            string label = g.IsCpu ? $"{Dim}cpu{Reset} " + g.Name : ShortDeviceName(g.Name);
            string name = PadVisible(Clip(label, nameW), nameW);
            string sh = PadVisible($"{Green}✓{g.Accepted}{Reset}/{Red}✗{g.Rejected}{Reset}", 9);
            string sensors = showSensors
                ? " " + PadVisible(FormatTemp(g.TempC), 6) + " " + PadVisible(FormatPower(g.PowerW), 7)
                : "";
            lines.Add($" {g.Id,-2} {name} {hr} {g.Diff,-6} {g.IterMs,5:F1}ms {sh}{sensors} {health}");
        }

        return Fit(lines, inner);
    }
}
