using Akoya.Miner.Mining;
using static Akoya.Miner.Observability.Themes.Panel;

namespace Akoya.Miner.Observability.Themes;

/// <summary>
/// Antigravity Zero-G Orbital theme created by Antigravity AI.
/// Visualizes the mining rig as a deep-space orbital station where total hashrate
/// powers zero-gravity thrust vectoring, block finds register as deep space discoveries,
/// and telemetry logs are captured in real-time by the Flight Recorder.
/// </summary>
internal sealed class AntigravityTheme : IDashboardTheme
{
    public string Name => "antigravity";
    public string EventsTitle => "FLIGHT RECORDER";

    private static string HeightTag(long height)
        => height > 0 ? $"  {Dim}orbit {height:N0}{Reset}" : "";

    internal static string ModuleClassOf(in Metrics.DashGpu g)
    {
        if (g.IsCpu) return "Flight-Core";
        string n = g.Name ?? "";
        if (n.Contains("Arc", StringComparison.OrdinalIgnoreCase))
        {
            if (n.Contains('B') && HasSeries(n, 'B')) return "Battlemage";
            if (HasSeries(n, 'A')) return "Alchemist";
            if (HasSeries(n, 'C')) return "Celestial";
            if (HasSeries(n, 'D')) return "Druid";
        }
        return "ZeroG-Thruster";
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

        // ── Orbital Title ───────────────────────────────────────────────────
        long headline = snap.BlockHeight > 0 ? snap.BlockHeight : snap.CpuBlockHeight;
        string orbit = headline > 0 ? $" · orbit {headline:N0}" : "";
        string forks = snap.PrlForks > 0
            ? $" · {snap.PrlForks} fork{(snap.PrlForks == 1 ? "" : "s")} survived"
            : "";
        lines.Add($"{Cyan}{Bold} ✦ ANTIGRAVITY ENGINE v{ctx.Version}{Reset}{Dim}{orbit}{forks} · Zero-G · 0% Fee · [q] escape velocity{Reset}");

        // ── Station Beacon (Pool) ──────────────────────────────────────────
        if (!string.IsNullOrEmpty(snap.PoolUrl) && !string.IsNullOrEmpty(snap.CpuPoolUrl))
        {
            string dotG = (snap.Connected ? Green : Red) + "●" + Reset;
            string connG = snap.Connected ? Green + "STABLE LINK" + Reset : Red + "SIGNAL LOST" + Reset;
            string rttG = snap.LatencyMs > 0 ? $"{snap.LatencyMs:F0}ms" : "—";
            lines.Add(Line(inner,
                $" Beacon GPU ● {snap.PoolUrl}{LoveNote(snap.PoolUrl)}  {connG}  rtt {rttG}{HeightTag(snap.BlockHeight)}",
                $"flight {FormatUptime(ctx.Uptime)} "));

            string dotC = (snap.CpuConnected ? Green : Red) + "●" + Reset;
            string connC = snap.CpuConnected ? Green + "STABLE LINK" + Reset : Red + "SIGNAL LOST" + Reset;
            lines.Add(Line(inner,
                $" Beacon CPU ● {snap.CpuPoolUrl}{LoveNote(snap.CpuPoolUrl)}  {connC}{HeightTag(snap.CpuBlockHeight)}",
                $""));
        }
        else
        {
            string url = !string.IsNullOrEmpty(snap.PoolUrl) ? snap.PoolUrl : snap.CpuPoolUrl;
            bool connected = !string.IsNullOrEmpty(snap.PoolUrl) ? snap.Connected : snap.CpuConnected;
            string dot = (connected ? Green : Red) + "●" + Reset;
            string conn = connected ? Green + "STABLE LINK" + Reset : Red + "SIGNAL LOST" + Reset;
            string rtt = (!string.IsNullOrEmpty(snap.PoolUrl) && snap.LatencyMs > 0) ? $"{snap.LatencyMs:F0}ms" : "—";
            lines.Add(Line(inner,
                $" Beacon     ● {url}{LoveNote(url)}  {conn}  rtt {rtt}",
                $"flight {FormatUptime(ctx.Uptime)} "));
        }

        long total = snap.Accepted + snap.Rejected;
        double pct = total > 0 ? 100.0 * snap.Accepted / total : 100.0;
        string pctCol = pct >= 99 ? Green : pct >= 95 ? Yellow : Red;
        string shares = $"{Green}✓{snap.Accepted}{Reset} / {Red}✗{snap.Rejected}{Reset}  ({pctCol}{pct:F1}%{Reset})";
        string finds = snap.BlockFinds > 0 ? $"  {Yellow}✦{snap.BlockFinds} DISCOVERY{Reset}" : "";

        bool anyCpu = false, anyGpu = false;
        foreach (var g in snap.Gpus) { if (g.IsCpu) anyCpu = true; else anyGpu = true; }
        string rate = anyCpu && anyGpu
            ? $"gpu {Bold}{Cyan}{DisplayFormat.HashRate(snap.GpuHashesPerSec)}{Reset}  cpu {Bold}{Cyan}{DisplayFormat.HashRate(snap.CpuHashesPerSec)}{Reset}"
            : $"{Bold}{Cyan}{DisplayFormat.HashRate(snap.TotalHashesPerSec)}{Reset}";
        lines.Add(Line(inner,
            $" Thrust  {rate}   pulses {shares}{finds}",
            $"station {snap.Worker}{WorkerBadge(snap.Worker)} "));

        // ── Orbital Thrusters Table ───────────────────────────────────────
        lines.Add(Rule(inner, anyCpu ? "ORBITAL MODULES" : "THRUSTER ARRAY", Cyan));

        bool anySensors = false;
        foreach (var g in snap.Gpus) { if (g.TempC is not null || g.PowerW is not null) { anySensors = true; break; } }

        var (nameW, showSensors) = SizeNameColumn(
            inner, fixedW: 38, statusW: DisplayWidth("● STALL 9999s"), sensorsW: 14,
            anySensors, minName: 8, maxName: 24);

        lines.Add($" {Dim}{"#",-2} {PadVisible("MODULE", nameW)} {"CLASS",-11} {"THRUST",-10} {"VECTOR",-10}"
                + (showSensors ? $" {"TEMP",-6} {"POWER",-7}" : "") + $" STATUS{Reset}");
        foreach (var g in snap.Gpus)
        {
            bool stalled = g.HeartbeatAgeSec >= 30;
            string health = g.HeartbeatAgeSec < 5 ? $"{Green}● ZERO-G{Reset}"
                          : g.HeartbeatAgeSec < 30 ? $"{Yellow}● DRIFT {g.HeartbeatAgeSec:F0}s{Reset}"
                          : $"{Red}● STALL {g.HeartbeatAgeSec:F0}s{Reset}";
            string hrText = DisplayFormat.HashRate(g.HashesPerSec);
            string hr = PadVisible(stalled ? $"{Dim}{hrText}{Reset}" : $"{Bold}{hrText}{Reset}", 10);
            string label = g.IsCpu ? $"{Dim}cpu{Reset} " + g.Name : ShortDeviceName(g.Name);
            string name = PadVisible(Clip(label, nameW), nameW);
            string cls = PadVisible(ModuleClassOf(g), 11);

            int filled = g.HeartbeatAgeSec < 5 ? 8 : g.HeartbeatAgeSec < 30 ? 4 : 0;
            string vecCol = g.HeartbeatAgeSec < 5 ? Blue : g.HeartbeatAgeSec < 30 ? Yellow : Red;
            string vector = vecCol + "<" + new string('=', filled) + new string('.', 8 - filled) + ">" + Reset;

            string sensors = showSensors
                ? " " + PadVisible(FormatTemp(g.TempC), 6) + " " + PadVisible(FormatPower(g.PowerW), 7)
                : "";
            lines.Add($" {g.Id,-2} {name} {Cyan}{cls}{Reset} {hr} {vector}{sensors} {health}");
        }

        return Fit(lines, inner);
    }

    public string FormatEvent(string line)
    {
        var (tag, colour) = Verb(line);
        return " " + colour + PadVisible(tag, 14) + Reset + " " + line;
    }

    private static (string Tag, string Colour) Verb(string line)
    {
        if (Has(line, "stalled") || Has(line, "no progress")) return ("✦ ZERO-G LOSS", Red);
        if (Has(line, "block"))          return ("✦ DISCOVERY", Yellow);
        if (Has(line, "stale"))          return ("✦ DRIFTED", Dim);
        if (Has(line, "duplicate"))      return ("✦ ECHO", Dim);
        if (Has(line, "reject") || Has(line, "invalid")) return ("✦ DEFLECTED", Red);
        if (Has(line, "accepted"))       return ("✦ PULSE ACK", Green);
        if (Has(line, "σ install") || Has(line, "new job")) return ("✦ WARP VECTOR", Cyan);
        if (Has(line, "connect"))        return ("✦ DOCKING", Blue);
        if (Has(line, "disconnect") || Has(line, "reconnect")) return ("✦ UNCOUPLED", Yellow);
        return ("✦ TELEMETRY", Reset);
    }

    private static bool Has(string s, string needle)
        => s.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
