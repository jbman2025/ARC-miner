using static Akoya.Miner.Observability.Themes.Panel;

namespace Akoya.Miner.Observability.Themes;

/// <summary>
/// Secret theme: <c>--theme konami</c>. Undocumented on purpose — not in
/// <c>--help</c>, not in the README. Hidden, not exempt: the cross-theme suite
/// enumerates <see cref="Dashboard.AllThemeNames"/>, so this obeys every house
/// rule the listed themes do.
///
/// This is not a game-THEMED dashboard. It is an actual game, and the rig plays
/// it. There is no input because there is no player: every piece of game state is
/// a pure function of the mining snapshot, so the board cannot show you a fiction
/// — it is the counters, drawn as a playfield.
///
///   SCORE      accepted shares
///   HI-SCORE   the block height — the chain's score, which you are chasing
///   WAVE       one wave per boardful of shares; clearing it starts the next
///   INVADERS   what is left of the current wave; each accepted share kills one
///   DESCENT    the invaders drop as the rig gets sicker (see Threat)
///   LIVES      cannons still firing — one per healthy worker
///   UFO        a block find, the rare high-value target
///
/// The mechanic that makes it a game rather than a chart is DESCENT. A healthy
/// rig holds the invaders at the top of the screen; rejects, a stalled card and
/// an unreachable pool each push them down a row, and at maximum threat they are
/// level with your cannons. So "am I losing?" is answerable from six feet away,
/// by someone who has never seen a hashrate — which is the same reason the
/// arcade original put the invaders at the top and the cannon at the bottom.
///
/// It is honest by construction: you cannot clear a wave without landing shares,
/// and you cannot stop the descent without fixing the rig. The house rule still
/// binds underneath the arcade dressing — a dead cannon is red and says SILENT
/// with the seconds, because "your ship blew up" is not something to work out
/// from a sprite at 3am.
/// </summary>
internal sealed class KonamiTheme : IDashboardTheme
{
    public string Name => "konami";
    public bool Hidden => true;
    public string EventsTitle => "INSERT COIN";

    /// <summary>Rows of invaders per wave, and the height of the playfield. Both
    /// constant so the header never changes height and the event pane below it
    /// never resizes.</summary>
    private const int InvaderRows = 3;
    private const int BoardRows = 5;          // 3 rows of invaders + 2 of descent

    /// <summary>How far down the invaders have come, 0 (safe) to 2 (level with
    /// the cannons). Every step is a real fault, and they stack — this is the
    /// game's difficulty and the rig's health, which are the same number.</summary>
    private static int Threat(in Metrics.DashSnapshot snap)
    {
        int t = 0;
        long filed = snap.Accepted + snap.Rejected;
        if (filed >= 20 && 100.0 * snap.Rejected / filed > 5.0) t++;
        foreach (var g in snap.Gpus) { if (g.HeartbeatAgeSec >= 30) { t++; break; } }
        bool gpuDown = !string.IsNullOrEmpty(snap.PoolUrl) && !snap.Connected;
        bool cpuDown = !string.IsNullOrEmpty(snap.CpuPoolUrl) && !snap.CpuConnected;
        if (gpuDown || cpuDown) t++;
        return Math.Clamp(t, 0, BoardRows - InvaderRows);
    }

    public List<string> BuildHeader(in ThemeContext ctx)
    {
        var snap = ctx.Snap;
        int inner = ctx.Inner;
        var lines = new List<string>(16);

        // Playfield width in invader columns; each invader takes a glyph + a gap.
        int cols = Math.Clamp((inner - 6) / 2, 4, 26);
        int perWave = InvaderRows * cols;

        // The wave, and what is left of it. Shares kill invaders in reading order,
        // so the block visibly shrinks as the rig produces — and refills when a
        // wave is cleared, which is the reward loop.
        long wave = snap.Accepted / perWave + 1;
        int remaining = perWave - (int)(snap.Accepted % perWave);

        int lives = 0;
        foreach (var g in snap.Gpus) if (g.HeartbeatAgeSec < 30) lives++;

        // ── HUD ────────────────────────────────────────────────────────────
        long hi = snap.BlockHeight > 0 ? snap.BlockHeight : snap.CpuBlockHeight;
        string livesCell = snap.Gpus.Length == 0
            ? $"{Dim}—{Reset}"
            : lives == 0
                ? $"{Red}{Bold}GAME OVER{Reset}"
                : Green + new string('^', lives) + Reset;
        var hud = new System.Text.StringBuilder();
        hud.Append($" {Cyan}SCORE{Reset} {Bold}{snap.Accepted:D6}{Reset}");
        hud.Append($"   {Cyan}WAVE{Reset} {Bold}{wave:D2}{Reset}");
        if (hi > 0) hud.Append($"   {Cyan}HI{Reset} {Bold}{hi:D6}{Reset}");
        hud.Append($"   {Cyan}LIVES{Reset} {livesCell}");
        // Lifetime provenance, in the corner where an arcade cabinet puts credits.
        if (snap.PrlForks > 0)
            hud.Append($"   {Dim}{snap.PrlForks} fork{(snap.PrlForks == 1 ? "" : "s")} survived{Reset}");
        lines.Add(hud.ToString());

        // ── Playfield ──────────────────────────────────────────────────────
        int descent = Threat(snap);
        // The block empties from the bottom up as shares land, so its lowest
        // OCCUPIED row is what matters for a breach — marking the lowest possible
        // row put "<-- BREACH" against empty sky on a nearly-cleared wave.
        int lowestBand = remaining > 0 ? (remaining - 1) / cols : -1;
        // Rank colours, top row worth most — the arcade convention.
        string[] rankColour = { Magenta, Cyan, Green };
        for (int r = 0; r < BoardRows; r++)
        {
            // The UFO flies across the top row whenever this rig has found a
            // block — the rare, high-value target, and it actually landed. Drawn
            // independently of the invaders: it used to render only on empty sky,
            // so it vanished exactly when the wave was full.
            string ufo = snap.BlockFinds > 0 && r == 0
                ? $"  {Yellow}{Bold}<=>{Reset} {Yellow}x{snap.BlockFinds}{Reset}"
                : "";

            int band = r - descent;
            if (band < 0 || band >= InvaderRows)
            {
                lines.Add(ufo);
                continue;
            }

            var row = new System.Text.StringBuilder("  ");
            row.Append(rankColour[band]);
            for (int c = 0; c < cols; c++)
            {
                int slot = band * cols + c;
                row.Append(slot < remaining ? "#" : " ").Append(' ');
            }
            row.Append(Reset);
            // Invaders actually touching the ground row. Said in words as well as
            // position, because a sprite one line lower is not a 3am diagnosis.
            if (band == lowestBand && r == BoardRows - 1)
                row.Append($" {Red}{Bold}<-- BREACH{Reset}");
            row.Append(ufo);
            lines.Add(row.ToString());
        }

        // Ground line, then the cannons.
        lines.Add($"{Dim}{new string('=', Math.Max(0, inner))}{Reset}");

        // ── Cannons ────────────────────────────────────────────────────────
        // One per worker. This is where the arcade dressing stops and the panel
        // goes back to plain words: a destroyed cannon is red, named, and states
        // how long it has been silent.
        // Budgeted the same way every other table here is: reserve the status
        // first, let the temperature yield, and give the name what is left. The
        // arcade framing does not buy an exemption — a 64-column terminal must
        // still be able to read "SILENT 47s".
        // 20 = leading space + sprite + Pn + rate(11) + their separators.
        bool anyTemp = false;
        foreach (var g in snap.Gpus) if (g.TempC is not null) { anyTemp = true; break; }
        var (labelW, showTemp) = SizeNameColumn(
            inner, fixedW: 20, statusW: DisplayWidth("DESTROYED — SILENT 9999s"), sensorsW: 8,
            anyTemp, minName: 8, maxName: 22);

        int p = 1;
        foreach (var g in snap.Gpus)
        {
            bool dead = g.HeartbeatAgeSec >= 30;
            string label = g.IsCpu ? $"{Dim}cpu{Reset} " + g.Name : ShortDeviceName(g.Name);
            string sprite = dead ? $"{Red}x{Reset}" : $"{Green}^{Reset}";
            string rate = dead ? $"{Dim}{DisplayFormat.HashRate(g.HashesPerSec)}{Reset}"
                               : $"{Bold}{DisplayFormat.HashRate(g.HashesPerSec)}{Reset}";
            string temp = showTemp && g.TempC is not null ? $"  {FormatTemp(g.TempC)}" : "";
            string state = dead
                ? $"{Red}{Bold}DESTROYED — SILENT {g.HeartbeatAgeSec:F0}s{Reset}"
                : g.HeartbeatAgeSec >= 5
                    ? $"{Yellow}HIT ({g.HeartbeatAgeSec:F0}s){Reset}"
                    : $"{Green}FIRING{Reset}";
            lines.Add($" {sprite} {Dim}P{p}{Reset} {PadVisible(Clip(label, labelW), labelW)} "
                      + $"{PadVisible(rate, 11)}{PadVisible(temp, showTemp ? 8 : 0)}  {state}");
            p++;
        }

        return Fit(lines, inner);
    }
}
