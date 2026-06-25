using System.Diagnostics;
using System.Text;
using Akoya.Miner.Mining;

namespace Akoya.Miner.Observability;

/// <summary>
/// Opt-in live status dashboard (<c>--dashboard</c> / <c>AKOYA_DASHBOARD=1</c>).
///
/// When active, the routine scrolling log (per-worker stats line, session
/// summary) is suppressed and the pretty console formatter diverts every
/// formatted line into a fixed-size ring buffer (<see cref="PushLog"/>) instead
/// of writing it to stdout. The render loop then redraws a single in-place panel
/// each tick — rig summary, a per-GPU table, and the most recent events — using
/// ANSI cursor positioning (home + clear-to-EOL per line) so there is no scroll
/// and no flicker.
///
/// It is off by default: headless supervisors (HiveOS, systemd, k8s, Docker
/// logs) want the plain scrolling log, and it is only enabled for an interactive
/// TTY with the pretty (non-JSON) formatter.
/// </summary>
internal static class Dashboard
{
    // Set once at startup before any worker logs. Read on the hot logging path,
    // so volatile rather than locked.
    private static volatile bool _active;
    public static bool Active => _active;

    private const int RingCapacity = 256;
    private static readonly Queue<string> _events = new(RingCapacity);
    private static readonly object _gate = new();

    // ANSI (ESC = U+001B).
    private const char EscCh = '';
    private const string Reset = "[0m";
    private const string Bold = "[1m";
    private const string Dim = "[90m";
    private const string Cyan = "[96m";
    private const string Green = "[92m";
    private const string Yellow = "[93m";
    private const string Red = "[91m";
    private const string Home = "[H";
    private const string ClearEol = "[K";
    private const string ClearBelow = "[J";
    private const string ClearScreen = "[2J";
    private const string HideCursor = "[?25l";
    private const string ShowCursor = "[?25h";

    /// <summary>Decide whether the dashboard should run, and arm it. Returns
    /// false (leaving the normal scrolling log in place) when stdout is
    /// redirected or JSON logging is on — both want a clean line stream.</summary>
    public static bool TryEnable(bool jsonLogging)
    {
        var env = Akoya.Crypto.MinerEnv.Get("AKOYA_DASHBOARD") ?? "0";
        if (env is not ("1" or "true")) return false;
        if (jsonLogging) return false;
        if (Console.IsOutputRedirected) return false;
        _active = true;
        return true;
    }

    /// <summary>Append one already-formatted log line to the event ring. Called
    /// from <see cref="CustomConsoleFormatter"/> when the dashboard is active.</summary>
    public static void PushLog(string formattedLine)
    {
        formattedLine = formattedLine.TrimEnd('\r', '\n');
        lock (_gate)
        {
            if (_events.Count >= RingCapacity) _events.Dequeue();
            _events.Enqueue(formattedLine);
        }
    }

    public static async Task RunAsync(Stopwatch clock, CancellationToken ct)
    {
        if (!_active) return;
        var refreshMs = int.TryParse(Akoya.Crypto.MinerEnv.Get("AKOYA_DASHBOARD_REFRESH_MS"), out var r) && r >= 250
            ? r : 1000;
        Console.Write(HideCursor + ClearScreen + Home); // one-time full clear
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(refreshMs));
            Render(clock.Elapsed);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                Render(clock.Elapsed);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        finally
        {
            // Stand the dashboard down so any final shutdown / session-summary
            // lines scroll to the screen again instead of the (now unread) ring.
            _active = false;
            // Leave the cursor below the panel and restore it so the final
            // session summary / shutdown lines print cleanly underneath.
            Console.Write(ClearBelow + ShowCursor + "\n");
        }
    }

    private static void Render(TimeSpan up)
    {
        int width = SafeWidth();
        int inner = Math.Clamp(width - 1, 60, 110);

        var snap = Metrics.GetDashboardSnapshot();
        var lines = new List<string>(32);

        // Left-anchored layout (section rules, no right-hand vertical border).
        // Box-drawing with a fixed right edge is fragile: glyphs like ● ✓ ✗ and
        // the ℹ️ log emoji render 2 columns wide in most terminals but there is
        // no portable way to know that, so any right border visibly drifts. A
        // rule-and-rows layout sidesteps the whole problem — the only width math
        // left is right-aligning the summary values, where a 1-col drift is
        // invisible. DisplayWidth still approximates wide glyphs so the per-GPU
        // columns stay tidy.

        // ── Title ──────────────────────────────────────────────────────────
        lines.Add($"{Cyan}{Bold} ARC MINER v{VersionInfo.MinerVersion}{Reset}{Dim} · 0% Dev Fee FOREVER{Reset}");

        // ── Rig summary ────────────────────────────────────────────────────
        string dot = (snap.Connected ? Green : Red) + "●" + Reset;
        string conn = snap.Connected ? Green + "connected" + Reset : Red + "offline" + Reset;
        string rtt = snap.LatencyMs > 0 ? $"{snap.LatencyMs:F0}ms" : "—";
        lines.Add(Line(inner,
            $" Pool  {dot} {snap.PoolUrl}{LoveNote(snap.PoolUrl)}  {conn}  rtt {rtt}",
            $"up {FormatUptime(up)} "));

        long total = snap.Accepted + snap.Rejected;
        double pct = total > 0 ? 100.0 * snap.Accepted / total : 100.0;
        string pctCol = pct >= 99 ? Green : pct >= 95 ? Yellow : Red;
        string shares = $"{Green}✓{snap.Accepted}{Reset} / {Red}✗{snap.Rejected}{Reset}  ({pctCol}{pct:F1}%{Reset})";
        string finds = snap.BlockFinds > 0 ? $"  {Yellow}★{snap.BlockFinds} finds{Reset}" : "";
        lines.Add(Line(inner,
            $" Rig   {Bold}{Cyan}{GpuWorker.FormatHashRate(snap.TotalHashesPerSec)}{Reset}   shares {shares}{finds}",
            $"worker {snap.Worker}{WorkerBadge(snap.Worker)} "));

        // ── Per-GPU table ──────────────────────────────────────────────────
        lines.Add(Rule(inner, "GPUs", Dim));
        lines.Add($" {Dim}{"#",-2} {"NAME",-22} {"HASHRATE",-11} {"ITER",-6} {"SHARES",-10} HEALTH{Reset}");
        foreach (var g in snap.Gpus)
        {
            // Heartbeat age resets to ~0 on every progress tick, so a healthy
            // worker sits at 0.0s — show "live" rather than a frozen counter,
            // and only surface the stale age once it actually starts climbing.
            string health = g.HeartbeatAgeSec < 5 ? $"{Green}● live{Reset}"
                          : g.HeartbeatAgeSec < 30 ? $"{Yellow}● stale {g.HeartbeatAgeSec:F0}s{Reset}"
                          : $"{Red}● STALL {g.HeartbeatAgeSec:F0}s{Reset}";
            string name = PadVisible(Clip(g.Name, 22), 22);
            string hr = PadVisible($"{Bold}{GpuWorker.FormatHashRate(g.HashesPerSec)}{Reset}", 11);
            string sh = PadVisible($"{Green}✓{g.Accepted}{Reset}/{Red}✗{g.Rejected}{Reset}", 10);
            lines.Add($" {g.Id,-2} {name} {hr} {g.IterMs,5:F1}ms {sh} {health}");
        }

        // ── Events ─────────────────────────────────────────────────────────
        // The header (title + rig + GPU table) is fixed; only this pane grows.
        // Size it to exactly fill the remaining rows so the whole panel never
        // exceeds the window — see the no-trailing-newline note in the emit.
        lines.Add(Rule(inner, "EVENTS", Dim));
        int height = SafeHeight();
        int eventRows = Math.Max(1, height - 1 - lines.Count);
        foreach (var e in RecentEvents(eventRows))
            lines.Add(" " + Clip(e, inner - 1));

        // ── Emit (in place) ────────────────────────────────────────────────
        // Anchor at the home cell and clear each line to EOL. Crucially we do
        // NOT print a newline after the LAST line: when the cursor sits on the
        // bottom screen row, that newline scrolls the terminal up by one and the
        // fixed header creeps off the top a row per tick. Joining with '\n'
        // between lines (but not after the last) keeps the panel pinned. We also
        // cap the line count to the window height for the same reason.
        if (lines.Count > height) lines = lines.GetRange(0, height);
        var sb = new StringBuilder(inner * lines.Count + 64);
        sb.Append(Home);
        for (int i = 0; i < lines.Count; i++)
        {
            sb.Append(lines[i]).Append(ClearEol);
            if (i < lines.Count - 1) sb.Append('\n');
        }
        sb.Append(ClearBelow);
        Console.Write(sb.ToString());
    }

    private static List<string> RecentEvents(int count)
    {
        lock (_gate)
        {
            int skip = Math.Max(0, _events.Count - count);
            return _events.Skip(skip).ToList();
        }
    }

    // ── Layout helpers ──────────────────────────────────────────────────────

    // A full-width section rule: "── TITLE ─────…" to the panel width.
    private static string Rule(int inner, string title, string color)
    {
        string head = $"── {title} ";
        int fill = inner - DisplayWidth(head);
        return color + head + new string('─', Math.Max(0, fill)) + Reset;
    }

    // A content line with optional right-aligned text, no vertical borders.
    // `left`/`right` may carry ANSI; spacing is computed on display width.
    private static string Line(int inner, string left, string right)
    {
        int used = DisplayWidth(left) + DisplayWidth(right);
        if (used > inner)
        {
            left = Clip(left, Math.Max(0, inner - DisplayWidth(right)));
            used = DisplayWidth(left) + DisplayWidth(right);
        }
        int pad = Math.Max(0, inner - used);
        return left + new string(' ', pad) + right;
    }

    // Pad a (possibly ANSI-coloured) cell with trailing spaces to a display width.
    private static string PadVisible(string s, int width)
        => s + new string(' ', Math.Max(0, width - DisplayWidth(s)));

    private static string FormatUptime(TimeSpan up)
        => $"{(int)up.TotalHours:D2}:{up.Minutes:D2}:{up.Seconds:D2}";

    // Easter egg: a little love for the pools that have worked with us on the
    // 0%-fee / fee-transparency / BLAKE3-challenge front. Matched on host so a
    // port or stratum+tcp:// prefix doesn't matter.
    private static string LoveNote(string poolUrl)
    {
        if (string.IsNullOrEmpty(poolUrl)) return "";
        // Match base domains so regional subdomains (prl-us., prl-eu., …) and
        // ports all qualify.
        foreach (var host in new[] { "alphapool.tech", "kryptex.network" })
            if (poolUrl.Contains(host, StringComparison.OrdinalIgnoreCase))
                return " ❤️";
        return "";
    }

    // Worker-name badges: a little flair per card. Matched as a case-insensitive
    // substring of the worker name (so "rig1-B580" still gets the pick). Icons
    // are built from code points to keep raw emoji out of the source; "️"
    // (VS16, U+FE0F) forces emoji presentation for the BMP pick symbol. Add rows
    // here to extend.
    private static readonly (string Key, string Icon)[] _badges =
    {
        // Custom shout-outs first so they win over the card-model matches below.
        ("morbidarc", char.ConvertFromUtf32(0x1FA7B)),      // x-ray 🩻 — for Jbones81's A750
        ("b770", char.ConvertFromUtf32(0x1F525)),           // fire
        ("b580", char.ConvertFromUtf32(0x26CF) + "️"), // pick ⛏️
        ("a770", char.ConvertFromUtf32(0x1F680)),           // rocket
        ("a750", char.ConvertFromUtf32(0x1F409)),           // dragon
        ("a580", char.ConvertFromUtf32(0x1F98A)),           // fox
        ("a380", char.ConvertFromUtf32(0x1F331)),           // seedling
    };

    private static string WorkerBadge(string worker)
    {
        if (string.IsNullOrEmpty(worker)) return "";
        foreach (var (key, icon) in _badges)
            if (worker.Contains(key, StringComparison.OrdinalIgnoreCase))
                return " " + icon;
        return "";
    }

    private static int SafeWidth()
    {
        try { int w = Console.WindowWidth; return w > 0 ? w : 100; }
        catch { return 100; }
    }

    private static int SafeHeight()
    {
        try { int h = Console.WindowHeight; return h > 0 ? h : 30; }
        catch { return 30; }
    }

    // Display width, ignoring ANSI SGR escapes (ESC [ … final-letter) and
    // approximating terminal cell width for non-ASCII glyphs (wide CJK/emoji
    // count 2, combining/variation-selector count 0). Good enough to keep the
    // per-GPU columns aligned in the common terminals (Windows Terminal etc.).
    private static int DisplayWidth(string s)
    {
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == EscCh)
            {
                int j = i + 1;
                if (j < s.Length && s[j] == '[')
                {
                    j++;
                    while (j < s.Length && !char.IsLetter(s[j])) j++;
                    i = j; // skip the final letter
                }
                continue;
            }
            n += CharWidth(s, ref i);
        }
        return n;
    }

    // Columns occupied by the character at index i (may advance i over a
    // surrogate pair). 0 = combining/zero-width, 2 = wide/emoji, else 1.
    private static int CharWidth(string s, ref int i)
    {
        char c = s[i];
        if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
        {
            int cp = char.ConvertToUtf32(c, s[i + 1]);
            i++; // consume the low surrogate
            // Emoji & symbol planes render 2-wide.
            if (cp >= 0x1F000 || (cp >= 0x20000 && cp <= 0x3FFFD)) return 2;
            return 1;
        }
        int o = (int)c;
        if (o == 0xFE0F) return 0;                       // emoji variation selector
        if (o >= 0x0300 && o <= 0x036F) return 0;        // combining marks
        // A BMP symbol followed by VS16 is emoji-presented → 2 cells (⛏️ ❤️ ℹ️).
        if (i + 1 < s.Length && s[i + 1] == '️') return 2;
        if (IsWide(o)) return 2;
        return 1;
    }

    // True for code points that occupy two terminal cells (CJK, Hangul,
    // fullwidth forms, and the emoji-presented info glyph U+2139).
    private static bool IsWide(int c) =>
        c == 0x2139 ||                       // ℹ info
        c == 0x2764 ||                       // ❤ heart (emoji-presented)
        (c >= 0x1100 && c <= 0x115F) ||      // Hangul Jamo
        (c >= 0x2E80 && c <= 0xA4CF) ||      // CJK radicals … Yi
        (c >= 0xAC00 && c <= 0xD7A3) ||      // Hangul syllables
        (c >= 0xF900 && c <= 0xFAFF) ||      // CJK compatibility ideographs
        (c >= 0xFE30 && c <= 0xFE4F) ||      // CJK compatibility forms
        (c >= 0xFF00 && c <= 0xFF60) ||      // fullwidth forms
        (c >= 0xFFE0 && c <= 0xFFE6);        // fullwidth signs

    // Truncate to a display width, preserving ANSI codes and closing with Reset.
    private static string Clip(string s, int maxWidth)
    {
        if (maxWidth <= 0) return "";
        if (DisplayWidth(s) <= maxWidth) return s;
        var sb = new StringBuilder();
        int n = 0;
        bool sawEsc = false;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == EscCh)
            {
                sawEsc = true;
                int start = i, j = i + 1;
                if (j < s.Length && s[j] == '[')
                {
                    j++;
                    while (j < s.Length && !char.IsLetter(s[j])) j++;
                }
                sb.Append(s, start, j - start + 1);
                i = j;
                continue;
            }
            int start2 = i;
            int w = CharWidth(s, ref i); // may advance i over a surrogate pair
            if (n + w > maxWidth - 1) { sb.Append('…'); break; }
            sb.Append(s, start2, i - start2 + 1);
            n += w;
        }
        if (sawEsc) sb.Append(Reset);
        return sb.ToString();
    }
}
