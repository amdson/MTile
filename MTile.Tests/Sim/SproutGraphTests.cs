using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// TileSproutGraph tests — pure ChunkMap mechanics, no body / physics.
// Exercises the multi-parent DAG behaviour added with drag-to-build:
//   • Chain extension off a single Solid anchor: request N cells in sequence,
//     only the first has a Solid neighbour. Verify the rest become Pending
//     and promote one-by-one as each parent finalizes.
//   • Dedup: same cell requested twice in one tick → second request is null.
//   • Out-of-range still rejected (cell has no Solid or Sprout neighbour).
public class SproutGraphTests(ITestOutputHelper output)
{
    // Default SproutLifetime = 0.1s. At dt = 1/30 that's exactly 3 frames; using
    // dt = Lifetime here keeps the test compact (one tick = one promotion step).
    private const float Lifetime = 0.1f;

    [Fact]
    public void Chain_OffSolidAnchor_PromotesOneStepPerLifetime()
    {
        // Wall at col 0 rows 2-3, ground at row 4. Cells (1,2), (2,2), (3,2)
        // form a horizontal chain extending right off the wall's upper block.
        //
        //   01234
        // 0 OOOOO
        // 1 OOOOO
        // 2 XOOOO       ← wall col 0 row 2 (upper)
        // 3 XOOOO       ← wall col 0 row 3 (lower)
        // 4 XXXXX       ← ground
        var terrain = SimTerrain.FromAscii(@"
            OOOOO
            OOOOO
            XOOOO
            XOOOO
            XXXXX", originTileX: 0, originTileY: 0);

        var n1 = terrain.TryRequestTile(1, 2);
        var n2 = terrain.TryRequestTile(2, 2);
        var n3 = terrain.TryRequestTile(3, 2);

        Assert.NotNull(n1);
        Assert.NotNull(n2);
        Assert.NotNull(n3);

        // n1 touches the Solid wall at (0,2) → starts Growing.
        Assert.Equal(TileSproutStatus.Growing, n1.Status);
        // n2 only sees n1 (a sprout, Growing) → Pending with n1 as parent.
        Assert.Equal(TileSproutStatus.Pending, n2.Status);
        Assert.Single(n2.SproutParents);
        Assert.Same(n1, n2.SproutParents[0]);
        // n3 only sees n2 (a sprout, Pending) → Pending with n2 as parent.
        Assert.Equal(TileSproutStatus.Pending, n3.Status);
        Assert.Single(n3.SproutParents);
        Assert.Same(n2, n3.SproutParents[0]);

        Assert.Single(terrain.Graph.Growing);
        Assert.Equal(2, terrain.Graph.Pending.Count);

        // After one Lifetime: n1 finalizes (cell becomes Solid), n2 promotes to Growing.
        terrain.TickSprouts(Lifetime);
        Assert.Equal(TileState.Solid, terrain.GetCellState(1, 2));
        Assert.Equal(TileSproutStatus.Growing, n2.Status);
        Assert.Equal(TileSproutStatus.Pending, n3.Status);
        Assert.Single(terrain.Graph.Growing);
        Assert.Single(terrain.Graph.Pending);

        // Direction check: n2's StartCenter should be n1's cell center (the parent
        // that completed), EndCenter = n2's own cell center → grows rightward.
        Assert.Equal(new Vector2(24f, 40f), n2.StartCenter);   // n1's cell (1,2) → centre (24,40)
        Assert.Equal(new Vector2(40f, 40f), n2.EndCenter);     // n2's cell (2,2) → centre (40,40)

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
    public void Request_DedupesAgainstExistingNode()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOO
            OOOOO
            XOOOO
            XOOOO
            XXXXX", originTileX: 0, originTileY: 0);

        var first  = terrain.TryRequestTile(1, 2);
        var second = terrain.TryRequestTile(1, 2);    // same cell, while Growing
        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public void Request_DedupesAgainstPendingNode()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOO
            OOOOO
            XOOOO
            XOOOO
            XXXXX", originTileX: 0, originTileY: 0);

        terrain.TryRequestTile(1, 2);                  // Growing (has Solid neighbour)
        var pending = terrain.TryRequestTile(2, 2);    // Pending (parent = (1,2))
        Assert.Equal(TileSproutStatus.Pending, pending.Status);

        var dup = terrain.TryRequestTile(2, 2);        // same Pending cell
        Assert.Null(dup);
    }

    [Fact]
    public void Request_NoCandidateParent_Rejected()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOO
            OOOOO
            XOOOO
            XOOOO
            XXXXX", originTileX: 0, originTileY: 0);

        // (3,1) has no 4-neighbour that is Solid or Sprouting → null.
        Assert.Null(terrain.TryRequestTile(3, 1));
    }

    [Fact]
    public void Chain_FromGrowingParent_PendingChildHasParentInList()
    {
        // Smoke test: when the parent is still Growing (not Solid) at child
        // request time, the child registers in the parent's Children list so
        // promotion fires when the parent finalizes.
        var terrain = SimTerrain.FromAscii(@"
            OOOOO
            OOOOO
            XOOOO
            XOOOO
            XXXXX", originTileX: 0, originTileY: 0);

        var n1 = terrain.TryRequestTile(1, 2);
        var n2 = terrain.TryRequestTile(2, 2);
        Assert.Single(n1.Children);
        Assert.Same(n2, n1.Children[0]);
    }
}
