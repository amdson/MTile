using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Direct coverage of the deposition cascade, with no FSM or input in the way. Two
// behaviours here are load-bearing and both were bugs once, so they get pinned at the
// source rather than only through a painter test:
//   • a trickle of sub-Threshold deposits must still grow a mound (the bucket brigade),
//   • a single lump deposit must pay out ALL its units, not just one (the drain loop).
public class TileMassFieldTests(ITestOutputHelper output)
{
    // Floor across the bottom; open air above. Cell (3,2) sits directly on it.
    private static ChunkMap Floor() => SimTerrain.FromAscii(@"
        OOOOOOOO
        OOOOOOOO
        OOOOOOOO
        XXXXXXXX", originTileX: 0, originTileY: 0);

    private static int SolidAbove(ChunkMap c)
    {
        int n = 0;
        for (int gtx = 0; gtx < 8; gtx++)
        for (int gty = 0; gty < 3; gty++)
            if (c.GetCellState(gtx, gty) == TileState.Solid) n++;
        return n;
    }

    private static void Settle(ChunkMap c, int frames = 90)
    {
        for (int f = 0; f < frames; f++) c.TickSprouts(1f / 60f);
    }

    // The painter's shape: ~0.23 mass per frame into one cell for a second.
    [Fact]
    public void Trickle_GrowsAMound()
    {
        var c = Floor();
        int committed = 0;
        for (int f = 0; f < 60; f++)
        {
            committed += c.Mass.Deposit(c, 3, 2, 14f / 60f, TileType.Dirt);
            c.TickSprouts(1f / 60f);
        }
        Settle(c);
        output.WriteLine($"trickle: {committed} committed, {SolidAbove(c)} solid above floor");
        Assert.True(SolidAbove(c) > 1, $"expected a mound, got {SolidAbove(c)}");
    }

    // A lump has to spend every unit it carries. Before the drain loop, one Deposit call
    // advanced the cascade by a single Threshold and silently banked the rest.
    [Fact]
    public void LumpDeposit_SpendsEveryUnit()
    {
        var c = Floor();
        int committed = c.Mass.Deposit(c, 3, 2, 6f, TileType.Dirt);
        Settle(c);
        output.WriteLine($"lump of 6: {committed} committed, {SolidAbove(c)} solid above floor");
        Assert.True(committed >= 4, $"a 6-unit lump should commit several tiles, got {committed}");
    }

    // Mass dropped where nothing can support it must not commit anything.
    [Fact]
    public void UnsupportedAir_CommitsNothing()
    {
        var c = Floor();
        int committed = c.Mass.Deposit(c, 40, -40, 6f, TileType.Dirt);
        Settle(c);
        Assert.Equal(0, committed);
    }
}
