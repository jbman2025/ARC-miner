namespace Akoya.Miner.Observability.Themes;

/// <summary>Everything a theme is allowed to see. Deliberately a snapshot of
/// plain data: a theme never touches the console, never reads a clock, and never
/// mutates anything, so two themes rendering the same context always produce the
/// same panel and either can be unit-tested without a terminal.</summary>
/// <param name="Snap">One consistent read of every live metric.</param>
/// <param name="Uptime">Session clock, for the header.</param>
/// <param name="Inner">Usable width in terminal cells. Never exceeds the window;
/// a theme must not build a row wider than this. The emitter clips as a backstop,
/// but a clipped row means the theme's own layout was wrong.</param>
/// <param name="Version">Miner version string for the title.</param>
internal readonly record struct ThemeContext(
    Metrics.DashSnapshot Snap,
    TimeSpan Uptime,
    int Inner,
    string Version);

/// <summary>
/// A dashboard skin. Themes convert a <see cref="ThemeContext"/> into the fixed
/// rows above the event pane; the surrounding machinery (event ring, pane sizing,
/// key handling, in-place emit, clipping, graceful stand-down) is shared and lives
/// in <see cref="Dashboard"/>.
///
/// The header/event split is not cosmetic: the header must be a fixed number of
/// rows for a given context, because the event pane is sized from whatever is
/// left. If the header grew with the number of events the panel height would
/// oscillate and the redraw would stop being in-place.
///
/// House rule for every theme, however whimsical: <b>flavour decorates the truth,
/// it never replaces it.</b> A failing worker must be unmistakably failing at a
/// glance — an operator woken at 3am should never have to decode a metaphor to
/// find out which card died.
/// </summary>
internal interface IDashboardTheme
{
    /// <summary>Name used by <c>--theme</c> / <c>ARC_THEME</c>. Lower-case.</summary>
    string Name { get; }

    /// <summary>Keep this theme out of <c>--help</c> and the documented list. It
    /// still resolves by name, so it is discoverable rather than disabled.
    ///
    /// Hidden means undocumented, NOT untested: the cross-theme suite enumerates
    /// <see cref="Dashboard.AllThemeNames"/>, which includes these, so a secret
    /// theme is held to exactly the same house rules as a listed one. A skin that
    /// hid a dead card would be a bug whether or not anybody was meant to find
    /// it.</summary>
    bool Hidden => false;

    /// <summary>Title for the event pane's section rule ("EVENTS", "COMBAT LOG").</summary>
    string EventsTitle { get; }

    /// <summary>Rows above the event pane: title, summary, per-worker table.</summary>
    List<string> BuildHeader(in ThemeContext ctx);

    /// <summary>Decorate one already-formatted log line for the event pane.
    /// The line arrives with its own colour; a theme may prefix it but must not
    /// obscure it. Default is a single leading space.</summary>
    string FormatEvent(string line) => " " + line;
}
