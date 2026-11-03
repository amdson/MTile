using System;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// todo #2 / #5: per-source sprout speed (avalanche < paint < Shift placement), a
// continuous build front at speeds that don't divide the frame, and unlimited build
// reach when chaining off your own live sprout.
public class SproutSpeedTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;

    // Floor along row 20, open above.
    private static ChunkMap Floor() => SimTerrain.FromAscii(
        string.Join("\n", System.Linq.Enumerable.Repeat(new string('O', 40), 20)) + "\n" + new string('X', 40),
        originTileX: 0, originTileY: 0);

    [Fact]
    public void Lifetime_IsPerSource_AvalancheFastest_PlaceSlowest()
    {
        var c = MovementConfig.Current;
        var map = Floor();
        var paint = map.TryRequestTile(5, 19, TileType.Dirt);
        var place = map.TryRequestTile(7, 19, TileType.Dirt, source: SproutSource.Place);
        Assert.Equal(c.SproutLifetime,      paint.Lifetime);
        Assert.Equal(c.PlaceSproutLifetime, place.Lifetime);
        Assert.Equal(c.AvalancheSproutLifetime, ChunkMap.LifetimeFor(new EntityId(3), SproutSource.Paint));
        Assert.True(c.AvalancheSproutLifetime < c.SproutLifetime && c.SproutLifetime < c.PlaceSproutLifetime);
    }

    // A column of N ghosts chained off the floor: the front must advance at exactly
    // one cell per Lifetime even though Lifetime isn't a whole number of frames —
    // each finished cell hands its leftover progress to the next (the overshoot carry).
    // Without the carry every cell would round up to whole frames.
    [Theory]
    [InlineData(SproutSource.Paint)]
    [InlineData(SproutSource.Place)]
    public void ChainedColumn_FrontIsContinuous_NoPerCellRounding(SproutSource source)
    {
        const int N = 12;
        var map = Floor();
        for (int k = 0; k < N; k++)
            Assert.NotNull(map.TryRequestTile(10, 19 - k, TileType.Dirt, source: source));
        float life = map.Graph.Growing[0].Lifetime;

        int frames = 0;
        while (map.GetCellState(10, 19 - (N - 1)) != TileState.Solid && frames < 1000)
        {
            map.TickSprouts(Dt);
            frames++;
        }
        float ideal   = N * life / Dt;
        float rounded = N * MathF.Ceiling(life / Dt - 1e-4f);
        output.WriteLine($"{source}: {frames} frames for {N} cells (ideal {ideal:F1}, per-cell rounding would be {rounded})");
        Assert.InRange(frames, ideal - 1f, ideal + 1.5f);
    }

    [Fact]
    public void SnapshotRestore_KeepsAGhostsSource()
    {
        var map = Floor();
        map.TryRequestTile(10, 19, TileType.Dirt, source: SproutSource.Place);
        map.TryRequestTile(10, 18, TileType.Dirt, source: SproutSource.Place);   // ghost
        var snap = map.CaptureTerrain();
        var copy = Floor();
        copy.RestoreTerrain(snap);
        Assert.True(copy.Graph.TryGet(10, 18, out var ghost));
        Assert.Equal(SproutSource.Place, ghost.Source);
        // It grows at the placement rate once promoted.
        for (int f = 0; f < 60 && ghost.Status == TileSproutStatus.Pending; f++) copy.TickSprouts(Dt);
        Assert.Equal(MovementConfig.Current.PlaceSproutLifetime, ghost.Lifetime);
    }

    // Shift+RMB at a cell far outside BuildReach, but bordering the player's OWN live
    // sprout column: placement takes. The same cell next to a stranger's sprout doesn't.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChainReach_IsUnlimited_OffYourOwnSprout(bool own)
    {
        var sim = new Simulation(Floor(), new Vector2(40f, 20 * Chunk.TileSize - 22f));
        byte owner = own ? BlockOwnership.OwnerFor(sim.Player.Faction) : BlockOwnership.None;
        // A tall column 30 tiles away from the player (~330 px, BuildReach is 64).
        const int col = 33;
        for (int k = 0; k < 15; k++) sim.Chunks.TryRequestTile(col, 19 - k, TileType.Dirt, owner: owner);

        // Cursor on the cell beside the column's third row (a ghost by then, still live).
        var cursor = new Vector2((col + 1) * Chunk.TileSize + 5f, 17 * Chunk.TileSize + 5f);
        for (int f = 0; f < 6; f++)
            sim.Step(new PlayerInput { Shift = true, RightClick = f >= 2, MouseWorldPosition = cursor });

        bool placed = sim.Chunks.Graph.TryGet(col + 1, 17, out _)
                   || sim.Chunks.GetCellState(col + 1, 17) != TileState.Empty;
        output.WriteLine($"own={own}: placed={placed}, action {sim.Player.CurrentActionName}");
        Assert.Equal(own, placed);
    }
}
