namespace Akoya.Miner.Observability.Themes;

/// <summary>
/// Shared drawing primitives for dashboard themes: ANSI codes, terminal-cell
/// width arithmetic, and the row helpers every theme builds its lines from.
///
/// These were private to <see cref="Dashboard"/> until themes arrived. They live
/// here now because the width math is the part that is genuinely hard to get
/// right — wide glyphs, ANSI escapes that occupy no cells, surrogate pairs — and
/// a second theme reimplementing any of it would reintroduce the wrapping bug
/// that makes the whole in-place panel walk up the screen.
/// </summary>
internal static class Panel
{
    // ANSI. Written as  escapes rather than literal control characters so
    // the source stays copy-pasteable and diffable.
    public const char   Esc         = '';
    public const string Reset       = "[0m";
    public const string Bold        = "[1m";
    public const string Dim         = "[90m";
    public const string Cyan        = "[96m";
    public const string Green       = "[92m";
    public const string Yellow      = "[93m";
    public const string Red         = "[91m";
    public const string Magenta     = "[95m";
    public const string Blue        = "[94m";
    public const string White       = "[97m";
    public const string Home        = "[H";
    public const string ClearEol    = "[K";
    public const string ClearBelow  = "[J";
    public const string ClearScreen = "[2J";
    public const string HideCursor  = "[?25l";
    public const string ShowCursor  = "[?25h";

    /// <summary>A full-width section rule: "── TITLE ─────…" to the panel width.</summary>
    public static string Rule(int inner, string title, string color)
    {
        string head = $"── {title} ";
        int fill = inner - DisplayWidth(head);
        return color + head + new string('─', Math.Max(0, fill)) + Reset;
    }

    /// <summary>A content row with optional right-aligned text and no vertical
    /// borders. Both sides may carry ANSI; spacing is computed on display width.</summary>
    public static string Line(int inner, string left, string right)
    {
        int rightW = DisplayWidth(right);
        // A one-column gap is mandatory, not incidental. Sizing the left side to
        // exactly the space the right side leaves lets the two run together into
        // one unreadable word ("*2 LEGENDARY" + "party \"rig01\"" rendering as
        // "*2 LEGENDARYparty \"rig01\""), which happens whenever the row lands at
        // exactly the panel width — not only when it overflows.
        int gap = rightW > 0 ? 1 : 0;
        int maxLeft = Math.Max(0, inner - rightW - gap);
        if (DisplayWidth(left) > maxLeft) left = Clip(left, maxLeft);
        int pad = Math.Max(gap, inner - DisplayWidth(left) - rightW);
        return left + new string(' ', pad) + right;
    }

    /// <summary>Clip every row to the panel width, in place. Themes call this
    /// on the way out.
    ///
    /// The emitter clips too, but that is a backstop against a layout bug — this
    /// is the theme meeting its contract. Keeping it here rather than leaving it
    /// to the emitter is what lets a theme be unit-tested for width without a
    /// terminal, and rows like the title or a long pool URL are variable-length
    /// by nature, so something has to bound them.</summary>
    public static List<string> Fit(List<string> rows, int inner)
    {
        for (int i = 0; i < rows.Count; i++) rows[i] = Clip(rows[i], inner);
        return rows;
    }

    /// <summary>Pad a (possibly ANSI-coloured) cell with trailing spaces to a
    /// display width.</summary>
    public static string PadVisible(string s, int width)
        => s + new string(' ', Math.Max(0, width - DisplayWidth(s)));

    /// <summary>Pad on the LEFT, so a set of right-hand labels of differing
    /// lengths still start at the same column — which is what keeps a
    /// multi-row block drawn beside them vertically aligned.</summary>
    public static string PadLeftVisible(string s, int width)
        => new string(' ', Math.Max(0, width - DisplayWidth(s))) + s;

    /// <summary>Size a worker table's flexible name column, and decide whether the
    /// sensor columns fit.
    ///
    /// Every theme's table ends with STATUS, which makes it the first thing a clip
    /// eats — and STATUS is the one column that must never disappear, because it
    /// is where a dead card is reported. Each theme used to size its name column
    /// against a hand-counted constant that did not include the status text at
    /// all, so the column ran off the right edge on ordinary terminals: measured
    /// at 0.3.1, classic lost it at 80 columns, cyberpunk lost the stall age at
    /// 80, and all three lost it entirely at 64.
    ///
    /// The rule this encodes: <b>sensors are decoration, status is not.</b> Below
    /// the width where both fit, temp/power yield and the status stays. Name is
    /// the flexible column and absorbs whatever is left.
    /// </summary>
    /// <param name="fixedW">Every non-name, non-sensor, non-status column plus the
    /// separators between them.</param>
    /// <param name="statusW">Display width of the theme's widest status string.</param>
    /// <param name="sensorsW">Display width of the sensor columns, separators
    /// included; 0 if the theme has none.</param>
    public static (int NameW, bool ShowSensors) SizeNameColumn(
        int inner, int fixedW, int statusW, int sensorsW, bool anySensors,
        int minName, int maxName)
    {
        bool show = anySensors && inner - fixedW - statusW - sensorsW >= minName;
        int budget = inner - fixedW - statusW - (show ? sensorsW : 0);
        return (Math.Clamp(budget, minName, maxName), show);
    }

    public static string FormatUptime(TimeSpan up)
        => $"{(int)up.TotalHours:D2}:{up.Minutes:D2}:{up.Seconds:D2}";

    public static int SafeWidth()
    {
        try { int w = Console.WindowWidth; return w > 0 ? w : 100; }
        catch { return 100; }
    }

    public static int SafeHeight()
    {
        try { int h = Console.WindowHeight; return h > 0 ? h : 30; }
        catch { return 30; }
    }

    // Display width, ignoring ANSI SGR escapes (ESC [ … final-letter) and
    // approximating terminal cell width for non-ASCII glyphs (wide CJK/emoji
    // count 2, combining/variation-selector count 0). Good enough to keep the
    // per-worker columns aligned in the common terminals (Windows Terminal etc.).
    public static int DisplayWidth(string s)
    {
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == Esc)
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

    /// <summary>Truncate to a display width, preserving ANSI codes and closing
    /// with Reset so a clipped colour cannot leak onto the rest of the terminal.</summary>
    public static string Clip(string s, int maxWidth)
    {
        if (maxWidth <= 0) return "";
        if (DisplayWidth(s) <= maxWidth) return s;
        var sb = new System.Text.StringBuilder();
        int n = 0;
        bool sawEsc = false;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == Esc)
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

    /// <summary>Trim vendor boilerplate off a device string for table display:
    /// "Intel(R) Arc(TM) B580 Graphics" → "Arc B580".
    ///
    /// The driver name is mostly trademark furniture, and it is all prefix — so
    /// a truncated column shows "Intel(R) Arc(TM) B580 Gra…" and hides the only
    /// token an operator actually reads. Non-Intel names (CPU model strings,
    /// other vendors' GPUs) are left alone rather than guessed at.</summary>
    public static string ShortDeviceName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var s = name.Replace("(R)", "", StringComparison.Ordinal)
                    .Replace("(TM)", "", StringComparison.Ordinal)
                    .Replace("(C)", "", StringComparison.Ordinal);
        // Only strip "Intel"/"Graphics" when this really is an Intel GPU name;
        // "Graphics" could be meaningful on some other vendor's part.
        if (s.Contains("Arc", StringComparison.OrdinalIgnoreCase))
        {
            s = s.Replace("Intel", "", StringComparison.OrdinalIgnoreCase);
            int g = s.LastIndexOf("Graphics", StringComparison.OrdinalIgnoreCase);
            if (g >= 0) s = s.Remove(g, "Graphics".Length);
        }
        // Collapse the runs of whitespace the removals leave behind.
        return string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>GPU temperature, coloured by how worried to be. Null renders as
    /// "—": unknown is not the same as cold, and a 0 °C card would send an
    /// operator chasing a sensor fault that isn't there.</summary>
    public static string FormatTemp(double? c)
    {
        if (c is null) return Dim + "—" + Reset;
        // Arc throttles around the high 90s; 85 is "look at your airflow".
        string col = c >= 90 ? Red : c >= 80 ? Yellow : Green;
        return $"{col}{c.Value:F0}°C{Reset}";
    }

    /// <summary>Board power draw, or "—" when the platform publishes no energy
    /// counter to differentiate.</summary>
    public static string FormatPower(double? w)
        => w is null ? Dim + "—" + Reset : $"{w.Value:F0}W";

    // Eighth-block glyphs. All width 1 and outside every range in IsWide, which
    // is why a sparkline cannot drift the columns beside it — same reason the
    // rogue theme otherwise sticks to ASCII.
    private const string SparkGlyphs = "▁▂▃▄▅▆▇█";

    /// <summary>A one-row sparkline of the most recent samples.
    ///
    /// Scaled between the window's own min and max rather than from zero: a
    /// mining rig sits at a near-constant rate, so a zero-based line is a flat
    /// bar that hides exactly the dips an operator wants to see. A flat series
    /// renders as a flat mid-line rather than dividing by zero.</summary>
    public static string Spark(IReadOnlyList<double> samples, int width)
    {
        if (width <= 0 || samples.Count == 0) return "";
        int n = Math.Min(width, samples.Count);
        int skip = samples.Count - n;

        double min = double.MaxValue, max = double.MinValue;
        for (int i = skip; i < samples.Count; i++)
        {
            double v = samples[i];
            if (!double.IsFinite(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
        }
        if (min > max) return "";

        // Floor the range at 2% of the peak. Pure min-max scaling amplifies
        // whatever variation exists to the full glyph range, so a rock-steady
        // rig jittering by half a percent would draw a wildly oscillating line
        // and read as a problem. With a floor, ordinary noise stays visually
        // flat and only a real dip — a stalled card, a pool reconnect — moves
        // the line. Same house rule as the rest of the panel: the picture must
        // not imply something the numbers do not say.
        double span = Math.Max(max - min, max * 0.02);

        var sb = new System.Text.StringBuilder(n);
        for (int i = skip; i < samples.Count; i++)
        {
            double v = samples[i];
            if (!double.IsFinite(v)) { sb.Append(' '); continue; }
            int idx = span <= 0
                ? SparkGlyphs.Length / 2
                : (int)Math.Round((v - min) / span * (SparkGlyphs.Length - 1));
            sb.Append(SparkGlyphs[Math.Clamp(idx, 0, SparkGlyphs.Length - 1)]);
        }
        return sb.ToString();
    }

    // ── Shared flavour ──────────────────────────────────────────────────────

    /// <summary>Easter egg: a little love for the pools that have worked with us
    /// on the 0%-fee / fee-transparency / BLAKE3-challenge front. Matched on base
    /// domain so regional subdomains (prl-us., prl-eu., …) and ports qualify.</summary>
    public static string LoveNote(string poolUrl)
    {
        if (string.IsNullOrEmpty(poolUrl)) return "";
        foreach (var host in new[] { "alphapool.tech", "kryptex.network" })
            if (poolUrl.Contains(host, StringComparison.OrdinalIgnoreCase))
                return " ❤️";
        return "";
    }

    // Worker-name badges: a little flair per card. Matched as a case-insensitive
    // substring of the worker name (so "rig1-B580" still gets the pick). Icons
    // are built from code points to keep raw emoji out of the source; U+FE0F
    // forces emoji presentation for the BMP pick symbol. Add rows here to extend.
    private static readonly (string Key, string Icon)[] _badges =
    {
        // Custom shout-outs first so they win over the card-model matches below.
        ("morbidarc", "\U0001FA7B"),            // x-ray 🩻 — for Jbones81's A750
        ("b770", "\U0001F525"),                 // fire
        ("b580", "⛏️"),               // pick ⛏️
        ("a770", "\U0001F680"),                 // rocket
        ("a750", "\U0001F409"),                 // dragon
        ("a580", "\U0001F98A"),                 // fox
        ("a380", "\U0001F331"),                 // seedling
    };

    public static string WorkerBadge(string worker)
    {
        if (string.IsNullOrEmpty(worker)) return "";
        foreach (var (key, icon) in _badges)
            if (worker.Contains(key, StringComparison.OrdinalIgnoreCase))
                return " " + icon;
        return "";
    }
}
