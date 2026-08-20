using System.Diagnostics;
using System.Text;
using Akoya.Miner.Observability.Themes;
using static Akoya.Miner.Observability.Themes.Panel;

namespace Akoya.Miner.Observability;

/// <summary>
/// Live status dashboard, on by default (<c>--dash-off</c> /
/// <c>ARC_DASHBOARD=0</c> turns it off).
///
/// When active, the routine scrolling log (per-worker stats line, session
/// summary) is suppressed and the pretty console formatter diverts every
/// formatted line into a fixed-size ring buffer (<see cref="PushLog"/>) instead
/// of writing it to stdout. The render loop then redraws a single in-place panel
/// each tick — rig summary, a per-GPU table, and the most recent events — using
/// ANSI cursor positioning (home + clear-to-EOL per line) so there is no scroll
/// and no flicker.
///
/// It only runs for an interactive TTY with the pretty (non-JSON) formatter:
/// headless supervisors (HiveOS, systemd, k8s, Docker logs) redirect stdout and
/// so keep the plain scrolling log automatically.
/// </summary>
internal static class Dashboard
{
    // Set once at startup before any worker logs. Read on the hot logging path,
    // so volatile rather than locked.
    private static volatile bool _active;
    public static bool Active => _active;

    // Set by the host (Program) to request a graceful shutdown when the user
    // presses 'q'/Esc in the dashboard — wired to the same path as Ctrl-C.
    public static Action? OnQuit;

    private const int RingCapacity = 256;
    // Ring lines replayed into the scrollback when the panel stands down.
    private const int ReplayOnExit = 15;
    private static readonly Queue<string> _events = new(RingCapacity);
    private static readonly object _gate = new();

    // ANSI codes and the layout helpers now live in Themes/Panel.cs (pulled in
    // by the `using static` above) so both themes share one implementation of
    // the terminal-cell width arithmetic.

    /// <summary>Skins. Registered here rather than discovered by reflection —
    /// the miner ships as Native AOT, where reflection-based discovery is exactly
    /// the pattern that silently returns nothing in the published binary while
    /// working fine under a JIT'd test run.</summary>
    private static readonly IDashboardTheme[] _themes =
    {
        new ClassicTheme(),
        new RogueTheme(),
        new CyberTheme(),
        new BroadsheetTheme(),
        new AntigravityTheme(),
        new PlainlyTheme(),
        new KonamiTheme(),
    };

    private static IDashboardTheme? _theme;

    /// <summary>Active theme, resolved once from ARC_THEME. An unknown name
    /// falls back to classic rather than throwing: a cosmetic setting must never
    /// be able to stop a rig from mining.</summary>
    internal static IDashboardTheme Theme => _theme ??= ResolveTheme(
        Akoya.Crypto.MinerEnv.Get("ARC_THEME"));

    internal static IDashboardTheme ResolveTheme(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            foreach (var t in _themes)
            {
                if (string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                    return t;
            }
        }
        return _themes[0];
    }

    /// <summary>Theme names, for --help and error messages.</summary>
    /// <summary>Names for `--help` and the docs. Hidden themes are omitted.</summary>
    internal static IEnumerable<string> ThemeNames()
    {
        foreach (var t in _themes) if (!t.Hidden) yield return t.Name;
    }

    /// <summary>Every theme, hidden ones included. This is what the test suite
    /// enumerates — being undocumented must never mean being unverified.</summary>
    internal static IEnumerable<string> AllThemeNames()
    {
        foreach (var t in _themes) yield return t.Name;
    }

    /// <summary>Decide whether the dashboard should run, and arm it. Returns
    /// false (leaving the normal scrolling log in place) when stdout is
    /// redirected or JSON logging is on — both want a clean line stream.</summary>
    public static bool TryEnable(bool jsonLogging)
    {
        var env = Akoya.Crypto.MinerEnv.Get("ARC_DASHBOARD") ?? "1";
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
        var refreshMs = int.TryParse(Akoya.Crypto.MinerEnv.Get("ARC_DASHBOARD_REFRESH_MS"), out var r) && r >= 250
            ? r : 1000;
        Console.Write(HideCursor + ClearScreen + Home); // one-time full clear
        try
        {
            // Tick faster than the redraw so 'q'/Esc feels immediate: at the
            // default 1000ms refresh, polling keys only on redraw meant up to a
            // full second between the keypress and the miner starting to shut
            // down, which reads as a wedged dashboard.
            int tickMs = Math.Min(refreshMs, KeyPollMs);
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(tickMs));
            // Absolute deadline advanced by exactly refreshMs, rather than a
            // stopwatch reset per redraw: the poll tick does not divide the
            // refresh interval evenly, and resetting would round every redraw up
            // to the next tick (a 250ms refresh drawing every 300ms). Advancing
            // the deadline keeps the long-run rate at the requested one.
            var loopClock = Stopwatch.StartNew();
            long nextRenderMs = refreshMs;
            SafeRender(clock.Elapsed);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                // 'q' or Esc requests a graceful shutdown (Ctrl-C still works too).
                if (ReadQuitKey()) { OnQuit?.Invoke(); break; }
                long now = loopClock.ElapsedMilliseconds;
                if (now < nextRenderMs) continue;
                // Skip missed deadlines outright (a suspended laptop, a stalled
                // console write) instead of drawing the same panel N times.
                nextRenderMs += refreshMs * (1 + (now - nextRenderMs) / refreshMs);
                SafeRender(clock.Elapsed);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        finally
        {
            Shutdown();
        }
    }

    /// <summary>Stand the dashboard down and hand the terminal back: the event
    /// ring stops swallowing log lines and the cursor is restored. Idempotent,
    /// and safe to call from a path that never started the render loop — the
    /// fatal-exit path in Program does exactly that, since otherwise the error
    /// that killed the run stays trapped in the (never-drained) ring and the
    /// process exits leaving the cursor hidden.</summary>
    public static void Shutdown()
    {
        if (!_active) return;
        _active = false;
        // Flush meta-progression before the process goes away. It saves on a
        // timer too, so a hard kill loses at most a minute of shares rather than
        // the session — but a graceful exit should lose nothing.
        try { Themes.RogueProgress.Shared.Save(); } catch { /* decoration */ }
        // Leave the cursor below the panel and restore it so the final session
        // summary / shutdown lines print cleanly underneath.
        try { Console.Write(ClearBelow + ShowCursor + "\n"); } catch { /* console gone */ }
        // Replay the tail of the event ring into the scrollback. The panel is
        // an in-place redraw, so without this a run that ends abruptly leaves
        // nothing behind at all — including the error that ended it. The last
        // few lines are the useful part; the full 256-line ring would just bury
        // the shutdown output that follows.
        try
        {
            foreach (var line in DrainEvents(ReplayOnExit)) Console.WriteLine(line);
        }
        catch { /* console gone */ }
    }

    private const int KeyPollMs = 100;

    private static bool ReadQuitKey()
    {
        try
        {
            if (Console.IsInputRedirected) return false;
            while (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true).Key;
                if (key is ConsoleKey.Q or ConsoleKey.Escape) return true;
            }
        }
        catch { /* no console input (service, detached) — Ctrl-C still works */ }
        return false;
    }

    /// <summary>Render, swallowing console failures. A transient write error
    /// (window closed, resized mid-write, redirected stdout going away) must not
    /// tear down the render loop and take the miner with it — the mining
    /// pipeline is what matters and it is entirely independent of this panel.</summary>
    private static void SafeRender(TimeSpan up)
    {
        try { Render(up); }
        catch (IOException) { }
        catch (ArgumentOutOfRangeException) { }   // console geometry raced a resize
    }

    /// <summary>Build the panel with the active theme and paint it in place.
    ///
    /// The theme owns the header rows; everything here is theme-independent —
    /// which is the point of the split. Sizing, clipping and the in-place emit
    /// are the parts that are subtly hard to get right, so they are written once
    /// and every theme inherits them.</summary>
    private static void Render(TimeSpan up)
    {
        int width = SafeWidth();
        // Cap the panel width, but never exceed the window: a floor wider than
        // the terminal makes every rule and right-aligned value wrap onto the
        // next row, which turns the in-place redraw into scrolling garbage. A
        // narrow window gets a cramped panel; it does not get a broken one.
        int inner = Math.Clamp(width - 1, 1, 110);
        int height = SafeHeight();

        var theme = Theme;
        var ctx = new ThemeContext(
            Metrics.GetDashboardSnapshot(), up, inner, VersionInfo.MinerVersion);

        var lines = theme.BuildHeader(ctx);

        // ── Events ─────────────────────────────────────────────────────────
        // The theme's header is fixed; only this pane grows. Size it to exactly
        // fill the remaining rows so the whole panel never exceeds the window —
        // see the no-trailing-newline note in the emit.
        lines.Add(Rule(inner, theme.EventsTitle, Dim));
        int eventRows = Math.Max(1, height - 1 - lines.Count);
        foreach (var e in RecentEvents(eventRows))
            lines.Add(theme.FormatEvent(Clip(e, inner - 3)));

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
            // Clip at the emit as a backstop: any row wider than the window
            // wraps, and one wrapped row pushes every row below it down by one,
            // so the whole in-place panel walks up the screen a line per tick.
            sb.Append(Clip(lines[i], inner)).Append(ClearEol);
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
            var outp = new List<string>(Math.Min(count, _events.Count));
            int i = 0;
            foreach (var e in _events)
            {
                if (i++ >= skip) outp.Add(e);
            }
            return outp;
        }
    }

    /// <summary>Take the last <paramref name="count"/> ring entries and empty
    /// the ring. Used once, at stand-down, to replay the tail into the
    /// scrollback.</summary>
    private static List<string> DrainEvents(int count)
    {
        var outp = RecentEvents(count);
        lock (_gate) { _events.Clear(); }
        return outp;
    }
}
