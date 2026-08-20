using Akoya.Miner.Mining;
using static Akoya.Miner.Observability.Themes.Panel;

namespace Akoya.Miner.Observability.Themes;

/// <summary>
/// Cyberpunk / Cyberdeck skin. Neon-hued telemetry console with cybernetic
/// node classifications, pulse bars, and stream event tags.
/// </summary>
internal sealed class CyberTheme : IDashboardTheme
{
    public string Name => "cyberpunk";
    public string EventsTitle => "NETLOG";

    private static string HeightTag(long height)
        => height > 0 ? $"  {Dim}node {height:N0}{Reset}" : "";

    internal static string ClassOf(in Metrics.DashGpu g)
    {
        if (g.IsCpu) return "Cyber-CPU";
        string n = g.Name ?? "";
        if (n.Contains("Arc", StringComparison.OrdinalIgnoreCase))
        {
            if (n.Contains('B') && HasSeries(n, 'B')) return "Battlemage";
            if (HasSeries(n, 'A')) return "Alchemist";
            if (HasSeries(n, 'C')) return "Celestial";
            if (HasSeries(n, 'D')) return "Druid";
        }
        return "Cyber-GPU";
    }

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

    public List<string> BuildHeader(in ThemeContext ctx)
    {
        var snap = ctx.Snap;
        int inner = ctx.Inner;
        var lines = new List<string>(16);

        // ── Title ──────────────────────────────────────────────────────────
        long headline = snap.BlockHeight > 0 ? snap.BlockHeight : snap.CpuBlockHeight;
        string height = headline > 0 ? $" · node {headline:N0}" : "";
        string forks = snap.PrlForks > 0
            ? $" · {snap.PrlForks} fork{(snap.PrlForks == 1 ? "" : "s")} survived"
            : "";
        lines.Add($"{Magenta}${Bold} ARC CYBERDECK v{ctx.Version}{Reset}{Dim}{height}{forks} · 0% Dev Fee FOREVER · [q] disconnect{Reset}");

        // ── Uplink summary ────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(snap.PoolUrl) && !string.IsNullOrEmpty(snap.CpuPoolUrl))
        {
            string dotG = (snap.Connected ? Green : Red) + "●" + Reset;
            string connG = snap.Connected ? Green + "CONNECTED" + Reset : Red + "OFFLINE" + Reset;
            string rttG = snap.LatencyMs > 0 ? $"{snap.LatencyMs:F0}ms" : "—";
            lines.Add(Line(inner,
                $" Uplink GPU ● {snap.PoolUrl}{LoveNote(snap.PoolUrl)}  {connG}  rtt {rttG}{HeightTag(snap.BlockHeight)}",
                $"up {FormatUptime(ctx.Uptime)} "));

            string dotC = (snap.CpuConnected ? Green : Red) + "●" + Reset;
            string connC = snap.CpuConnected ? Green + "CONNECTED" + Reset : Red + "OFFLINE" + Reset;
            lines.Add(Line(inner,
                $" Uplink CPU ● {snap.CpuPoolUrl}{LoveNote(snap.CpuPoolUrl)}  {connC}{HeightTag(snap.CpuBlockHeight)}",
                $""));
        }
        else
        {
            string url = !string.IsNullOrEmpty(snap.PoolUrl) ? snap.PoolUrl : snap.CpuPoolUrl;
            bool connected = !string.IsNullOrEmpty(snap.PoolUrl) ? snap.Connected : snap.CpuConnected;
            string dot = (connected ? Green : Red) + "●" + Reset;
            string conn = connected ? Green + "CONNECTED" + Reset : Red + "OFFLINE" + Reset;
            string rtt = (!string.IsNullOrEmpty(snap.PoolUrl) && snap.LatencyMs > 0) ? $"{snap.LatencyMs:F0}ms" : "—";
            lines.Add(Line(inner,
                $" Uplink     ● {url}{LoveNote(url)}  {conn}  rtt {rtt}",
                $"up {FormatUptime(ctx.Uptime)} "));
        }

        long total = snap.Accepted + snap.Rejected;
        double pct = total > 0 ? 100.0 * snap.Accepted / total : 100.0;
        string pctCol = pct >= 99 ? Green : pct >= 95 ? Yellow : Red;
        string shares = $"{Green}✓{snap.Accepted}{Reset} / {Red}✗{snap.Rejected}{Reset}  ({pctCol}{pct:F1}%{Reset})";
        string finds = snap.BlockFinds > 0 ? $"  {Yellow}★{snap.BlockFinds} jackpot{Reset}" : "";

        bool anyCpu = false, anyGpu = false;
        foreach (var g in snap.Gpus) { if (g.IsCpu) anyCpu = true; else anyGpu = true; }
        string rate = anyCpu && anyGpu
            ? $"gpu {Bold}{Cyan}{DisplayFormat.HashRate(snap.GpuHashesPerSec)}{Reset}  cpu {Bold}{Cyan}{DisplayFormat.HashRate(snap.CpuHashesPerSec)}{Reset}"
            : $"{Bold}{Cyan}{DisplayFormat.HashRate(snap.TotalHashesPerSec)}{Reset}";
        lines.Add(Line(inner,
            $" Matrix  {rate}   packets {shares}{finds}",
            $"node {snap.Worker}{WorkerBadge(snap.Worker)} "));

        // ── Hardware Nodes Table ──────────────────────────────────────────
        lines.Add(Rule(inner, anyCpu ? "HARDWARE NODES" : "GPU NODES", Magenta));

        bool anySensors = false;
        foreach (var g in snap.Gpus) { if (g.TempC is not null || g.PowerW is not null) { anySensors = true; break; } }

        // 39 = leading space + id(2) + name gap + class(11) + throughput(10) +
        // pulse bar(10) + their separators. Sensors are 1+6+1+7.
        var (nameW, showSensors) = SizeNameColumn(
            inner, fixedW: 39, statusW: DisplayWidth("● STALL 9999s"), sensorsW: 15,
            anySensors, minName: 8, maxName: 28);
        lines.Add($" {Dim}{"#",-2} {PadVisible("NODE", nameW)} {"CLASS",-11} {"THROUGHPUT",-10} {"PULSE",-10}"
                + (showSensors ? $" {"TEMP",-6} {"POWER",-7}" : "") + $" STATUS{Reset}");
        foreach (var g in snap.Gpus)
        {
            bool stalled = g.HeartbeatAgeSec >= 30;
            string health = g.HeartbeatAgeSec < 5 ? $"{Green}● ONLINE{Reset}"
                          : g.HeartbeatAgeSec < 30 ? $"{Yellow}● LAG {g.HeartbeatAgeSec:F0}s{Reset}"
                          : $"{Red}● STALL {g.HeartbeatAgeSec:F0}s{Reset}";
            string hrText = DisplayFormat.HashRate(g.HashesPerSec);
            string hr = PadVisible(stalled ? $"{Dim}{hrText}{Reset}" : $"{Bold}{hrText}{Reset}", 10);
            string label = g.IsCpu ? $"{Dim}cpu{Reset} " + g.Name : ShortDeviceName(g.Name);
            string name = PadVisible(Clip(label, nameW), nameW);
            string cls = PadVisible(ClassOf(g), 11);

            int filled = g.HeartbeatAgeSec < 5 ? 8 : g.HeartbeatAgeSec < 30 ? 4 : 0;
            string pulseCol = g.HeartbeatAgeSec < 5 ? Cyan : g.HeartbeatAgeSec < 30 ? Yellow : Red;
            string pulse = pulseCol + "[" + new string('=', filled) + new string('-', 8 - filled) + "]" + Reset;

            string sensors = showSensors
                ? " " + PadVisible(FormatTemp(g.TempC), 6) + " " + PadVisible(FormatPower(g.PowerW), 7)
                : "";
            lines.Add($" {g.Id,-2} {name} {Magenta}{cls}{Reset} {hr} {pulse}{sensors} {health}");
        }

        return Fit(lines, inner);
    }

    public string FormatEvent(string line)
    {
        var (tag, colour) = Verb(line);
        return " " + colour + PadVisible(tag, 10) + Reset + " " + line;
    }

    private static (string Tag, string Colour) Verb(string line)
    {
        if (Has(line, "stalled") || Has(line, "no progress")) return ("[OFFLINE]", Red);
        if (Has(line, "block"))          return ("[JACKPOT]", Yellow);
        if (Has(line, "stale"))          return ("[CORRUPT]", Dim);
        if (Has(line, "duplicate"))      return ("[REDUNDANT]", Dim);
        if (Has(line, "reject") || Has(line, "invalid")) return ("[DROP]", Red);
        if (Has(line, "accepted"))       return ("[ACK]", Green);
        if (Has(line, "σ install") || Has(line, "new job")) return ("[SYNC]", Cyan);
        if (Has(line, "connect"))        return ("[UPLINK]", Cyan);
        if (Has(line, "disconnect") || Has(line, "reconnect")) return ("[DISCONN]", Yellow);
        return ("[NET]", Reset);
    }

    private static bool Has(string s, string needle)
        => s.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
