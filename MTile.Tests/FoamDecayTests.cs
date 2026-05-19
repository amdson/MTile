using Xunit;

namespace MTile.Tests;

// Foam tiles register a decay timer on finalize and BreakCell themselves when
// the timer expires. These tests use the FoamDecay class directly (lifecycle)
// and ChunkMap.TickSprouts (integration) to confirm both ends are wired.
public class FoamDecayTests
{
    // Direct lifecycle: register a cell, tick past its lifetime, verify the
    // expiry callback fires.
    [Fact]
    public void FoamDecay_Tick_ExpiresAfterLifetime()
    {
        var foam = new FoamDecay();
        foam.Register(5, 7, lifetime: 1.0f);

        int? expiredX = null, expiredY = null;
        // Two ticks of 0.4s = 0.8s; still alive.
        foam.Tick(0.4f, (x, y) => { expiredX = x; expiredY = y; });
        foam.Tick(0.4f, (x, y) => { expiredX = x; expiredY = y; });
        Assert.Null(expiredX);

        // 0.4 + 0.4 + 0.4 = 1.2s, crosses 1.0s → expires.
        foam.Tick(0.4f, (x, y) => { expiredX = x; expiredY = y; });
        Assert.Equal(5, expiredX);
        Assert.Equal(7, expiredY);
    }

    // Integration: foam sprout placed on a solid parent finalizes, becomes solid,
    // and then auto-breaks after FoamDecay.DefaultLifetime seconds.
    [Fact]
    public void FoamSprout_FinalizesThenAutoBreaks()
    {
        var chunks = new ChunkMap();
        // Stone floor at (0..3, 5): one row of solid tiles so the foam sprout above
        // has a solid parent to anchor to.
        for (int gtx = 0; gtx <= 3; gtx++)
        {
            var node = chunks.TryRequestTile(gtx, 5, TileType.Stone);
            Assert.Null(node);  // No solid neighbor exists yet → first cell has no parent.
        }
        // Seed the floor directly via the chunk store so the foam test has solid
        // ground to grow off of. (TryRequestTile rejects cells with no parent.)
        for (int gtx = 0; gtx <= 3; gtx++)
        {
            var (cpos, tx, ty) = WorldCoords.Global(gtx, 5);
            if (!chunks.TryGet(cpos, out var chunk))
            {
                chunk = new Chunk { ChunkPos = cpos };
                chunks[cpos] = chunk;
            }
            chunk.Tiles[tx, ty].State = TileState.Solid;
            chunk.Tiles[tx, ty].Type  = TileType.Stone;
        }

        // Request a foam tile directly above the floor. Should anchor to the
        // solid parent below and start growing.
        var foamNode = chunks.TryRequestTile(2, 4, TileType.Foam);
        Assert.NotNull(foamNode);

        // Tick the sprout-grow lifetime so the cell finalizes (becomes Solid).
        // MovementConfig.Current.SproutLifetime is the grow duration; advance
        // past it with chunky steps. Use 0.5s × enough to clear most realistic
        // sprout lifetimes (~0.3-0.4s).
        for (int i = 0; i < 4; i++) chunks.TickSprouts(0.5f);
        Assert.Equal(TileState.Solid, chunks.GetCellState(2, 4));

        // Now tick past FoamDecay.DefaultLifetime (4s) so the auto-break fires.
        for (int i = 0; i < 10; i++) chunks.TickSprouts(0.5f);
        Assert.Equal(TileState.Empty, chunks.GetCellState(2, 4));

        // The stone floor underneath is untouched.
        Assert.Equal(TileState.Solid, chunks.GetCellState(2, 5));
    }
}

// Tiny helper to convert global cell coords to chunk-local without re-implementing
// ChunkMap's private logic. Mirrors GlobalCellToChunkLocal.
internal static class WorldCoords
{
    public static (Microsoft.Xna.Framework.Point chunkPos, int tx, int ty) Global(int gtx, int gty)
    {
        int cx = FloorDiv(gtx, Chunk.Size);
        int cy = FloorDiv(gty, Chunk.Size);
        return (new Microsoft.Xna.Framework.Point(cx, cy),
                gtx - cx * Chunk.Size,
                gty - cy * Chunk.Size);
    }
    private static int FloorDiv(int n, int d) { int q = n / d; if ((n ^ d) < 0 && q * d != n) q--; return q; }
}
