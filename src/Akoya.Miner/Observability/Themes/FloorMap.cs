namespace Akoya.Miner.Observability.Themes;

/// <summary>
/// The little dungeon floor in the top-right of the rogue panel.
///
/// Every element maps to something real, which is the whole reason it earns the
/// space it occupies:
///
///   • the LAYOUT is seeded from the block height, so each block is a different
///     floor — and every miner on that block sees the same one;
///   • each lit cell is one accepted share, filling in the order the floor is
///     walked, wrapping when the floor is full;
///   • '@' is the most recently lit cell — where the party has got to.
///
/// A map whose '@' wandered around on a timer would be a screensaver. We have no
/// nonce-progress telemetry, so nothing here pretends to show one.
///
/// Pure ASCII: the panel's width arithmetic hand-maintains a wide-glyph table,
/// and a map is the last place that should need extending.
/// </summary>
internal static class FloorMap
{
    private const char Wall = '#';
    private const char Unlit = '.';
    private const char Lit = '*';
    private const char Party = '@';

    /// <summary>Render the floor as <paramref name="height"/> rows of exactly
    /// <paramref name="width"/> characters. Rows are always the full width so
    /// the caller can right-align them into a column that stays vertically
    /// aligned across the panel.</summary>
    public static string[] Render(long seed, long litCount, int width, int height)
    {
        // Empty, not an array of nulls — a caller indexing those would throw,
        // and "no room for a map" has to be a completely safe answer.
        if (width <= 0 || height <= 0) return Array.Empty<string>();
        var rows = new string[height];

        int cells = width * height;
        var grid = new char[cells];

        // Deterministic layout from the block height. SplitMix64 rather than
        // Random: we want the same floor on every miner at that height, on every
        // platform, without depending on the framework's PRNG staying put.
        ulong s = unchecked((ulong)seed * 0x9E3779B97F4A7C15UL + 0x243F6A8885A308D3UL);
        var floorIdx = new List<int>(cells);
        for (int i = 0; i < cells; i++)
        {
            s = Mix(s);
            // ~22% walls. Enough that floors look distinct from one another,
            // sparse enough that the walkable path never fragments into
            // unreachable specks at this size.
            bool wall = (s >> 40) % 100 < 22;
            grid[i] = wall ? Wall : Unlit;
            if (!wall) floorIdx.Add(i);
        }

        // Nothing walkable (only possible at absurdly small sizes) — bail out
        // rather than divide by zero below.
        if (floorIdx.Count == 0)
        {
            for (int r = 0; r < height; r++) rows[r] = new string(Wall, width);
            return rows;
        }

        // Walk the floor in a serpentine order so filling reads left-to-right,
        // then back — like clearing a room, rather than teleporting about.
        floorIdx.Sort((a, b) =>
        {
            int ra = a / width, rb = b / width;
            if (ra != rb) return ra.CompareTo(rb);
            int ca = a % width, cb = b % width;
            return (ra & 1) == 0 ? ca.CompareTo(cb) : cb.CompareTo(ca);
        });

        if (litCount > 0)
        {
            // Wrap: a full floor starts over rather than pinning at 100%, so the
            // map keeps moving on a long run instead of freezing solid.
            int n = (int)(litCount % floorIdx.Count);
            // A share count that lands exactly on a full floor should show a
            // full floor, not an empty one.
            if (n == 0) n = floorIdx.Count;
            for (int i = 0; i < n; i++) grid[floorIdx[i]] = Lit;
            grid[floorIdx[n - 1]] = Party;
        }

        for (int r = 0; r < height; r++)
            rows[r] = new string(grid, r * width, width);
        return rows;
    }

    private static ulong Mix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Colour one rendered row: walls dim, cleared cells cyan, the
    /// party marker bright. Applied after layout so the ANSI never enters the
    /// width arithmetic.</summary>
    public static string Colourise(string row)
    {
        var sb = new System.Text.StringBuilder(row.Length * 6);
        char cur = '\0';
        foreach (var ch in row)
        {
            if (ch != cur)
            {
                sb.Append(ch switch
                {
                    Wall  => Panel.Dim,
                    Lit   => Panel.Cyan,
                    Party => Panel.Bold + Panel.Yellow,
                    _     => Panel.Dim,
                });
                cur = ch;
            }
            sb.Append(ch);
        }
        return sb.Append(Panel.Reset).ToString();
    }
}
