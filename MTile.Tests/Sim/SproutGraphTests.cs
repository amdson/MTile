using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// TileSproutGraph tests — pure ChunkMap mechanics, no body / physics.
// Exercises the edgeless shell-growth model:
//   • Chain extension off a single Solid anchor: request N cells in sequence,
//     only the first has a Solid neighbour. The rest become Pending ghosts and
//     promote one ring per Lifetime as their neighbours solidify.
//   • Multi-face support: a cell with several solid neighbours grows a volume
//     out of each, symmetrically — no single "parent" is picked.
//   • Dedup: same cell requested twice in one tick → second request is null.
//   • Out-of-range still rejected (cell has no Solid or Sprout neighbour).
//   • Support loss: breaking a parent clears that face, cancels a sprout that
//     runs out of faces, and prunes the ghosts left stranded behind it.
public class SproutGraphTests(ITestOutputHelper output)
{
    // Default SproutLifetime = 0.1s. At dt = 1/30 that's exactly 3 frames; using
    // dt = Lifetime here keeps the test compact (one tick = one growth ring).
    private const float Lifetime = 0.1f;

    // Wall at col 0 rows 2-3, ground at row 4.
    //
    //   01234
    // 0 OOOOO
    // 1 OOOOO
    // 2 XOOOO       ← wall col 0 row 2 (upper)
    // 3 XOOOO       ← wall col 0 row 3 (lower)
    // 4 XXXXX       ← ground
    private static ChunkMap Wall() => SimTerrain.FromAscii(@"
            OOOOO
            OOOOO
            XOOOO
            XOOOO
            XXXXX", originTileX: 0, originTileY: 0);

    [Fact]
    public void Chain_OffSolidAnchor_PromotesOneRingPerLifetime()
    {
        // Cells (1,2), (2,2), (3,2) form a horizontal chain extending right off
        // the wall's upper block. Only (1,2) touches solid at request time.
        var terrain = Wall();

        var n1 = terrain.TryRequestTile(1, 2);
        var n2 = terrain.TryRequestTile(2, 2);
        var n3 = terrain.TryRequestTile(3, 2);

        Assert.NotNull(n1);
        Assert.NotNull(n2);
        Assert.NotNull(n3);

        // n1 touches the Solid wall at (0,2) → starts Growing, pushing rightward
        // out of its left face.
        Assert.Equal(TileSproutStatus.Growing, n1.Status);
        Assert.Equal(SproutFaces.Left, n1.Faces);
        // n2 and n3 have no solid neighbour, only sprouts → ghosts.
        Assert.Equal(TileSproutStatus.Pending, n2.Status);
        Assert.Equal(TileSproutStatus.Pending, n3.Status);

        Assert.Single(terrain.Graph.Growing);
        Assert.Equal(2, terrain.Graph.Pending.Count);

        // After one Lifetime: n1 finalizes (cell becomes Solid), n2 promotes.
        terrain.TickSprouts(Lifetime);
        Assert.Equal(TileState.Solid, terrain.GetCellState(1, 2));
        Assert.Equal(TileSproutStatus.Growing, n2.Status);
        Assert.Equal(TileSproutStatus.Pending, n3.Status);
        Assert.Single(terrain.Graph.Growing);
        Assert.Single(terrain.Graph.Pending);

        // Direction check: n2's only solid neighbour is (1,2), so it grows out of
        // its left face — the volume starts on n1's cell centre and lands on its own.
        Assert.Equal(SproutFaces.Left, n2.Faces);
        float half = Chunk.TileSize / 2f;
        Assert.Equal(new Vector2(1 * Chunk.TileSize + half, 2 * Chunk.TileSize + half), n2.VolumeStart(SproutFaces.Left));  // cell (1,2)
        Assert.Equal(new Vector2(2 * Chunk.TileSize + half, 2 * Chunk.TileSize + half), n2.CellCenter);                     // cell (2,2)

        // After another Lifetime: n2 finalizes, n3 promotes.
        terrain.TickSprouts(Lifetime);
        Assert.Equal(TileState.Solid, terrain.GetCellState(2, 2));
        Assert.Equal(TileSproutStatus.Growing, n3.Status);
        Assert.Single(terrain.Graph.Growing);
        Assert.Empty(terrain.Graph.Pending);

        // After a third Lifetime: chain fully committed.
        terrain.TickSprouts(Lifetime);
        Assert.Equal(TileState.Solid, terrain.GetCellState(3, 2));
        Assert.Empty(terrain.Graph.Growing);
        Assert.Empty(terrain.Graph.Pending);
    }

    [Fact]
    public void Request_WithTwoSolidNeighbours_GrowsOutOfBothFaces()
    {
        // (1,3) sits in the wall's inside corner: solid to the left at (0,3) and
        // solid below at (1,4). It should take both faces, not pick one.
        var terrain = Wall();

        var n = terrain.TryRequestTile(1, 3);
        Assert.Equal(TileSproutStatus.Growing, n.Status);
        Assert.Equal(SproutFaces.Left | SproutFaces.Below, n.Faces);

        // Two volumes, one out of each parent cell, converging on the target cell.
        float half = Chunk.TileSize / 2f;
        Assert.Equal(new Vector2(0 * Chunk.TileSize + half, 3 * Chunk.TileSize + half), n.VolumeStart(SproutFaces.Left));   // cell (0,3)
        Assert.Equal(new Vector2(1 * Chunk.TileSize + half, 4 * Chunk.TileSize + half), n.VolumeStart(SproutFaces.Below));  // cell (1,4)
        Assert.Equal(new Vector2(1 * Chunk.TileSize + half, 3 * Chunk.TileSize + half), n.CellCenter);                      // cell (1,3)

        // Equal speeds along opposing axes — neither face leads.
        Assert.Equal(Chunk.TileSize / Lifetime, n.VolumeVelocity(SproutFaces.Left).X,  3);
        Assert.Equal(-Chunk.TileSize / Lifetime, n.VolumeVelocity(SproutFaces.Below).Y, 3);
    }

    [Fact]
    public void Ghost_PromotesSymmetrically_WhenTwoNeighboursFinalizeTogether()
    {
        // Wall blocks at (0,2) and (0,4) with a gap at (0,3).
        //
        //   01234
        // 2 XOOOO
        // 3 OOOOO
        // 4 XOOOO
        var terrain = SimTerrain.FromAscii(@"
            OOOOO
            OOOOO
            XOOOO
            OOOOO
            XOOOO", originTileX: 0, originTileY: 0);

        var above = terrain.TryRequestTile(1, 2);   // Growing off (0,2)
        var below = terrain.TryRequestTile(1, 4);   // Growing off (0,4)
        var mid   = terrain.TryRequestTile(1, 3);   // only sprout neighbours → ghost

        Assert.Equal(TileSproutStatus.Growing, above.Status);
        Assert.Equal(TileSproutStatus.Growing, below.Status);
        Assert.Equal(TileSproutStatus.Pending, mid.Status);

        // Both neighbours finalize on the same tick. Because commit and promotion
        // are separate passes, `mid` sees *both* as solid and takes both faces —
        // under the old first-parent-wins model it would have taken only one.
        terrain.TickSprouts(Lifetime);

        Assert.Equal(TileSproutStatus.Growing, mid.Status);
        Assert.Equal(SproutFaces.Above | SproutFaces.Below, mid.Faces);

        // Symmetric about the target cell at every point in the growth.
        mid.Age = mid.Lifetime * 0.5f;
        var c = mid.CellCenter;
        Assert.Equal(c.Y - mid.VolumeCenter(SproutFaces.Above).Y,
                     mid.VolumeCenter(SproutFaces.Below).Y - c.Y, 3);
    }

    [Fact]
    public void Request_DedupesAgainstExistingNode()
    {
        var terrain = Wall();

        var first  = terrain.TryRequestTile(1, 2);
        var second = terrain.TryRequestTile(1, 2);    // same cell, while Growing
        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public void Request_DedupesAgainstPendingNode()
    {
        var terrain = Wall();

        terrain.TryRequestTile(1, 2);                  // Growing (has Solid neighbour)
        var pending = terrain.TryRequestTile(2, 2);    // ghost (only a sprout neighbour)
        Assert.Equal(TileSproutStatus.Pending, pending.Status);

        var dup = terrain.TryRequestTile(2, 2);        // same Pending cell
        Assert.Null(dup);
    }

    [Fact]
    public void Request_NoCandidateParent_Rejected()
    {
        var terrain = Wall();

        // (3,1) has no 4-neighbour that is Solid or Sprouting → null.
        Assert.Null(terrain.TryRequestTile(3, 1));
    }

    [Fact]
    public void BreakingLastParent_CancelsSprout_AndPrunesStrandedGhosts()
    {
        var terrain = Wall();

        var n1 = terrain.TryRequestTile(1, 2);   // Growing off (0,2)
        terrain.TryRequestTile(2, 2);            // ghost
        terrain.TryRequestTile(3, 2);            // ghost, only reachable via (2,2)

        Assert.Single(terrain.Graph.Growing);
        Assert.Equal(2, terrain.Graph.Pending.Count);

        // Destroy the anchor. n1's only face was Left → it loses all support and
        // is cancelled outright, and its cell goes back to Empty.
        Assert.True(terrain.BreakCell(0, 2));
        Assert.Equal(SproutFaces.None, n1.Faces);
        Assert.Empty(terrain.Graph.Growing);
        Assert.Equal(TileState.Empty, terrain.GetCellState(1, 2));

        // The two ghosts can no longer reach anything that could support them.
        // The sweep is deferred to the next tick, then they're gone.
        terrain.TickSprouts(Lifetime);
        Assert.Empty(terrain.Graph.Pending);
    }

    [Fact]
    public void BreakingOneOfTwoParents_KeepsSproutGrowingOnTheOther()
    {
        var terrain = Wall();

        var n = terrain.TryRequestTile(1, 3);    // corner cell: Left | Below
        Assert.Equal(SproutFaces.Left | SproutFaces.Below, n.Faces);

        Assert.True(terrain.BreakCell(0, 3));    // drop the left parent only
        Assert.Equal(SproutFaces.Below, n.Faces);
        Assert.Single(terrain.Graph.Growing);

        // Still finishes normally on its remaining face.
        terrain.TickSprouts(Lifetime);
        Assert.Equal(TileState.Solid, terrain.GetCellState(1, 3));
    }

    [Fact]
    public void GhostsChainedToALiveSprout_SurviveThePrune()
    {
        var terrain = Wall();

        terrain.TryRequestTile(1, 2);            // Growing
        terrain.TryRequestTile(2, 2);            // ghost, adjacent to the sprout
        terrain.TryRequestTile(3, 2);            // ghost, chained behind it

        // An unrelated break arms the sweep; these ghosts are still connected to a
        // Growing sprout, so none of them may be pruned.
        Assert.True(terrain.BreakCell(4, 4));
        terrain.TickSprouts(0f);
        Assert.Equal(2, terrain.Graph.Pending.Count);
    }
}
