using Akoya.Miner.Observability.Themes;
using Xunit;

namespace Akoya.Miner.Tests;

/// <summary>
/// The floor map sits in a right-hand gutter spanning three rows, so its output
/// has to be rectangular to the character or the rows below it stagger. It also
/// has to be deterministic from the block height — that is what makes it a
/// visualization of something rather than decoration.
/// </summary>
public class FloorMapTests
{
    [Theory]
    [InlineData(12, 3)]
    [InlineData(28, 3)]
    [InlineData(1, 1)]
    public void RowsAreExactlyTheRequestedRectangle(int w, int h)
    {
        var rows = FloorMap.Render(96_263, 40, w, h);
        Assert.Equal(h, rows.Length);
        foreach (var r in rows)
        {
            Assert.Equal(w, r.Length);
            // Width must equal LENGTH here: any glyph the width helper counts as
            // 2 cells would shear the gutter, so the map is ASCII by contract.
            Assert.Equal(w, Panel.DisplayWidth(r));
        }
    }

    [Fact]
    public void TheSameBlockAlwaysDrawsTheSameFloor()
    {
        // Every miner on a given height should see the same dungeon — that only
        // works if the layout comes from the height and nothing else.
        var a = FloorMap.Render(96_263, 10, 20, 3);
        var b = FloorMap.Render(96_263, 10, 20, 3);
        Assert.Equal(a, b);

        var next = FloorMap.Render(96_264, 10, 20, 3);
        Assert.NotEqual(a, next);   // ...and descending gives you a new one
    }

    [Fact]
    public void LitCellsGrowWithSharesAndCarryThePartyMarker()
    {
        static (int lit, int party) Count(string[] rows)
        {
            int lit = 0, party = 0;
            foreach (var r in rows)
                foreach (var c in r)
                {
                    if (c == '*') lit++;
                    if (c == '@') party++;
                }
            return (lit, party);
        }

        var none = Count(FloorMap.Render(1234, 0, 20, 3));
        Assert.Equal(0, none.lit);
        Assert.Equal(0, none.party);      // no shares yet, no party on the floor

        var few = Count(FloorMap.Render(1234, 5, 20, 3));
        Assert.Equal(4, few.lit);         // 5 cells cleared, the last one is '@'
        Assert.Equal(1, few.party);

        var more = Count(FloorMap.Render(1234, 12, 20, 3));
        Assert.True(more.lit > few.lit);
        Assert.Equal(1, more.party);      // exactly one party marker, always
    }

    [Fact]
    public void AFullFloorWrapsRatherThanFreezingSolid()
    {
        // A long run must keep the map moving instead of pinning it at 100%.
        var rows = FloorMap.Render(99, 100_000, 20, 3);
        int lit = 0;
        foreach (var r in rows) foreach (var c in r) if (c is '*' or '@') lit++;
        int walkable = 0;
        foreach (var r in FloorMap.Render(99, 0, 20, 3)) foreach (var c in r) if (c == '.') walkable++;
        Assert.InRange(lit, 1, walkable);
    }

    [Fact]
    public void ColourisingDoesNotChangeTheMeasuredWidth()
    {
        // The gutter is laid out on the uncoloured text; if colour changed the
        // measured width the map would drift away from its labels.
        foreach (var row in FloorMap.Render(96_263, 33, 24, 3))
            Assert.Equal(Panel.DisplayWidth(row), Panel.DisplayWidth(FloorMap.Colourise(row)));
    }

    [Fact]
    public void DegenerateSizesReturnSomethingUsableRatherThanThrowing()
    {
        Assert.Empty(FloorMap.Render(1, 1, 0, 3));
        Assert.Empty(FloorMap.Render(1, 1, 10, 0));
        foreach (var r in FloorMap.Render(1, 999, 2, 1)) Assert.Equal(2, r.Length);
    }
}
