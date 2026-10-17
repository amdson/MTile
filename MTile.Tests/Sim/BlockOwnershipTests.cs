using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Block ownership (Plans/BLOCK_OWNERSHIP_SPIKES.md §1). Player-placed cells are stamped
// with their placer and placed frame; the claim fades at s = 0.5^(age / 60 s), is gone
// below 0.1, clears when the cell breaks, and makes the owner's block grab treat the
// cell's glue as (1 - s) of normal. Pinned here: placement paths stamp, the fade curve,
// break clears, the grab advantage (own wall vs the same wall unowned / enemy-owned),
// and rollback (snapshot/restore + identical checksums on replay).
public class BlockOwnershipTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;
    private const float Ts = Chunk.TileSize;

    private static ChunkMap FlatGround() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    private static ChunkMap DeepGround() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXX
        XXXXXXXXXXXXXXXXXXXXXXXX
        XXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    private static SimConfigMulti Build(InputScript script, ChunkMap terrain, Vector2 start, int frames,
                                        Faction faction = Faction.Player1) => new()
    {
        Terrain = terrain,
        Frames  = frames,
        Dt      = Dt,
        Gravity = new Vector2(0f, 600f),
        Players = new[] { new SimPlayer { StartPosition = start, Script = script, Faction = faction } },
    };

    private static void TickFrames(ChunkMap map, int frames)
    {
        for (int i = 0; i < frames; i++) map.TickSprouts(Dt);
    }

    // ── Placement paths stamp ───────────────────────────────────────────────────

    // Shift+RMB (BlockPlaceAction) by the SECOND player: the one new cell is owned by
    // Player2 at full strength — owner identity is the placer's, not "a player".
    [Fact]
    public void ShiftRmbPlace_StampsPlacerAsOwner()
    {
        var terrain = FlatGround();
        var start   = new Vector2(40f, 3 * Ts - PlayerCharacter.Radius);
        var cursor  = new Vector2(start.X + Ts, 3 * Ts - 4f);
        var script  = new InputScript()
            .For(6, new PlayerInput { MouseWorldPosition = cursor })
            .Forever(new PlayerInput { Shift = true, RightClick = true, MouseWorldPosition = cursor });
        SimRunner.RunMulti(Build(script, terrain, start, frames: 40, faction: Faction.Player2));

        int gtx = (int)MathF.Floor(cursor.X / Ts), gty = (int)MathF.Floor(cursor.Y / Ts);
        Assert.NotEqual(TileState.Empty, terrain.GetCellState(gtx, gty));
        Assert.True(terrain.Ownership.TryGet(gtx, gty, terrain.TerrainFrame, out byte owner, out float s));
        Assert.Equal(BlockOwnership.Player2, owner);
        Assert.True(s > 0.99f, $"fresh placement should be full strength, got {s}");
        // Pre-existing floor is unowned.
        Assert.False(terrain.Ownership.TryGet(gtx, 3, terrain.TerrainFrame, out _, out _));
        Assert.Equal(1, terrain.Ownership.Count);
    }

    // Plain RMB paint (mass cascade, incl. ghosts that promote frames later): every
    // painted cell is owned by the painter, and nothing else is.
    [Fact]
    public void Paint_StampsEveryPaintedCell()
    {
        var terrain = FlatGround();
        var start   = new Vector2(40f, 3 * Ts - PlayerCharacter.Radius);
        var cursor  = new Vector2(start.X + Ts, 3 * Ts - 4f);
        var script  = new InputScript()
            .For(6, new PlayerInput { MouseWorldPosition = cursor })
            .Forever(new PlayerInput { RightClick = true, MouseWorldPosition = cursor });
        SimRunner.RunMulti(Build(script, terrain, start, frames: 60));

        // Rows 0-2 above the floor, and rows below it (the cascade can spill under the
        // one-row floor and commit there, hanging off its underside). Row 3 is the floor.
        int placed = 0;
        for (int gtx = -2; gtx < 22; gtx++)
        for (int gty = -2; gty < 8; gty++)
        {
            if (gty == 3 && gtx >= 0 && gtx < 20) continue;
            if (terrain.GetCellState(gtx, gty) == TileState.Empty) continue;
            placed++;
            Assert.True(terrain.Ownership.TryGet(gtx, gty, terrain.TerrainFrame, out byte owner, out _),
                $"painted cell ({gtx},{gty}) should be owned");
            Assert.Equal(BlockOwnership.Player1, owner);
        }
        output.WriteLine($"painted {placed} cells, {terrain.Ownership.Count} claims");
        Assert.True(placed > 1);
        Assert.Equal(placed, terrain.Ownership.Count);
    }

    // The direct ChunkMap paths: TryRequestTile / ForceSprout / the mass field carry the
    // owner; the defaults (enemy builds, level terrain) stamp nobody; a pending ghost
    // stamps when it promotes, not when requested.
    [Fact]
    public void ChunkMapPaths_CarryOwner_DefaultIsUnowned()
    {
        var map = FlatGround();
        Assert.NotNull(map.TryRequestTile(2, 2, TileType.Stone, owner: BlockOwnership.Player1));
        Assert.NotNull(map.TryRequestTile(4, 2, TileType.Stone));                     // enemy-style: nobody
        Assert.NotNull(map.ForceSprout(8, 0, TileType.Foam, BlockOwnership.Player2));
        var ghost = map.TryRequestTile(2, 1, TileType.Stone, owner: BlockOwnership.Player1);
        Assert.Equal(TileSproutStatus.Pending, ghost.Status);
        Assert.False(map.Ownership.TryGet(2, 1, map.TerrainFrame, out _, out _));   // invisible ghost: not yet

        map.Mass.Deposit(map, 12, 2, 1.5f, TileType.Dirt, owner: BlockOwnership.Player2);

        TickFrames(map, 240);   // everything finalizes; the ghost promoted on the way
        Assert.Equal(TileState.Solid, map.GetCellState(2, 1));
        AssertOwner(map, 2, 2, BlockOwnership.Player1);
        AssertOwner(map, 2, 1, BlockOwnership.Player1);
        AssertOwner(map, 8, 0, BlockOwnership.Player2);
        AssertOwner(map, 12, 2, BlockOwnership.Player2);
        Assert.False(map.Ownership.TryGet(4, 2, map.TerrainFrame, out _, out _));
    }

    private static void AssertOwner(ChunkMap map, int gtx, int gty, byte expected)
    {
        Assert.True(map.Ownership.TryGet(gtx, gty, map.TerrainFrame, out byte owner, out _), $"({gtx},{gty}) unowned");
        Assert.Equal(expected, owner);
    }

    // ── Fade ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Claim_HalvesAtSixtySeconds_GoneAfterAboutThreePointThreeMinutes()
    {
        var map = FlatGround();
        map.Ownership.Stamp(5, 3, BlockOwnership.Player1, map.TerrainFrame);

        TickFrames(map, 60 * 60);
        Assert.True(map.Ownership.TryGet(5, 3, map.TerrainFrame, out _, out float half));
        Assert.InRange(half, 0.49f, 0.51f);

        // 0.5^(t/60) = 0.1 at t ≈ 199.3 s. Still (barely) held at 199 s ...
        TickFrames(map, 139 * 60);
        Assert.True(map.Ownership.TryGet(5, 3, map.TerrainFrame, out _, out float edge));
        Assert.InRange(edge, 0.1f, 0.102f);

        // ... gone by 200 s, and pruned out of the table within one cadence.
        TickFrames(map, 60 + BlockOwnership.PruneCadenceFrames);
        Assert.False(map.Ownership.TryGet(5, 3, map.TerrainFrame, out _, out _));
        Assert.Equal(0, map.Ownership.Count);
    }

    // ── Break clears; re-placing re-stamps ──────────────────────────────────────

    [Fact]
    public void BreakingClearsOwner_ReplacingRestamps()
    {
        var map = FlatGround();
        map.TryRequestTile(6, 2, TileType.Stone, owner: BlockOwnership.Player1);
        TickFrames(map, 120);
        AssertOwner(map, 6, 2, BlockOwnership.Player1);

        Assert.True(map.BreakCell(6, 2));
        Assert.False(map.Ownership.TryGet(6, 2, map.TerrainFrame, out _, out _));
        Assert.Equal(0, map.Ownership.Count);

        map.TryRequestTile(6, 2, TileType.Stone, owner: BlockOwnership.Player2);
        TickFrames(map, 120);
        AssertOwner(map, 6, 2, BlockOwnership.Player2);

        // A sprout broken mid-growth clears too.
        map.TryRequestTile(9, 2, TileType.Stone, owner: BlockOwnership.Player1);
        Assert.True(map.BreakCell(9, 2));
        Assert.False(map.Ownership.TryGet(9, 2, map.TerrainFrame, out _, out _));

        // Damage-to-destruction goes through BreakCell as well.
        while (!map.DamageCell(6, 2, 1f)) { }
        Assert.False(map.Ownership.TryGet(6, 2, map.TerrainFrame, out _, out _));
    }

    // ── The grab ────────────────────────────────────────────────────────────────

    // A 2-wide, 3-tall stone wall built on deep ground, by `builder` (None = the same
    // wall as if it had always been there), then grabbed by Player1: paint the wall,
    // pull away toward the player, release. Returns when it broke out and how much.
    private (int breakFrame, int blocks) GrabWall(byte builder, string label, int width = 2, int height = 3)
    {
        var terrain = DeepGround();
        int x1 = 12 + width, y0 = 4 - height;
        // Bottom-up so each row sprouts off (or ghosts behind) the one under it.
        for (int gty = 3; gty >= y0; gty--)
            for (int gtx = 13; gtx <= x1; gtx++)
                terrain.TryRequestTile(gtx, gty, TileType.Stone, owner: builder);
        TickFrames(terrain, 60 * (height + 1));
        for (int gty = y0; gty <= 3; gty++)
            for (int gtx = 13; gtx <= x1; gtx++)
                Assert.Equal(TileState.Solid, terrain.GetCellState(gtx, gty));

        var start  = new Vector2(9f * Ts + Ts * 0.5f, 4f * Ts - Ts * 0.5f);
        var onWall = new Vector2(13.5f * Ts + Ts * 0.5f, 2f * Ts + Ts * 0.5f);   // wall centre
        var pullTo = new Vector2(onWall.X - 3f * Ts, onWall.Y);                // 3 tiles toward the player

        var script = new InputScript()
            .For(10, new PlayerInput { MouseWorldPosition = onWall })
            .For(20, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = onWall })
            .For(40, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = pullTo })
            .Forever(new PlayerInput { MouseWorldPosition = pullTo });

        int breakFrame = -1, blocks = 0;
        SimRunner.RunMulti(Build(script, terrain, start, frames: 80),
            onFrameEntities: (f, ps, es) =>
            {
                foreach (var e in es)
                    if (e is PullPointEntity pp && pp.HarvestBlocks > 0 && breakFrame < 0)
                    {
                        breakFrame = f;
                        blocks     = pp.HarvestBlocks;
                    }
            });
        output.WriteLine($"{label,-14} {width}x{height} wall: break-out frame {breakFrame,3}, blocks {blocks}");
        return (breakFrame, blocks);
    }

    // Same paint-then-pull gesture on the same wall, three builders. Measured
    // (2026-10-17, stock peel tuning): a 2x3 wall breaks out on frame 30 when it is the
    // grabber's own (the first pull frame) vs frame 69 unowned, both whole (6 blocks);
    // a 3x3 wall, 6 cells painted, comes out on frame 30 when owned and not at all in
    // the 80-frame window unowned. Enemy-owned is identical to unowned in both.
    [Fact]
    public void Grab_OwnFreshWall_BreaksOutSooner()
    {
        var own     = GrabWall(BlockOwnership.Player1, "own (P1)");
        var unowned = GrabWall(BlockOwnership.None,    "unowned");
        var enemy   = GrabWall(BlockOwnership.Player2, "enemy (P2)");

        Assert.True(unowned.breakFrame >= 0, "baseline: the small wall does come out unowned");
        Assert.True(own.breakFrame >= 0 && own.breakFrame < unowned.breakFrame,
            $"own wall should break out sooner (own {own.breakFrame}, unowned {unowned.breakFrame})");
        Assert.True(own.blocks >= unowned.blocks);
        Assert.Equal(unowned, enemy);   // enemy-owned resists exactly as unowned
    }

    [Fact]
    public void Grab_OwnFreshWall_PullsOutMassAnUnownedWallHolds()
    {
        var own     = GrabWall(BlockOwnership.Player1, "own (P1)",   width: 3, height: 3);
        var unowned = GrabWall(BlockOwnership.None,    "unowned",    width: 3, height: 3);
        var enemy   = GrabWall(BlockOwnership.Player2, "enemy (P2)", width: 3, height: 3);

        Assert.True(own.blocks > unowned.blocks,
            $"own wall should yield more mass (own {own.blocks}, unowned {unowned.blocks})");
        Assert.Equal(unowned, enemy);
    }

    // ── Rollback ────────────────────────────────────────────────────────────────

    private static ChunkMap SimFloor() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXX", originTileX: -4, originTileY: 0);

    // Idle, then hold plain RMB over the floor beside the player: painting starts
    // before the snapshot frame and keeps placing (new claims) after it.
    private static PlayerInput PaintInput(int frame)
    {
        var cursor = new Vector2(40f + Ts, 3 * Ts - 4f);
        return new PlayerInput { RightClick = frame >= 6, MouseWorldPosition = cursor };
    }

    private static string Claims(ChunkMap map)
    {
        var rows = new List<string>();
        foreach (var kv in map.Ownership.Entries)
            rows.Add($"{kv.Key.gtx},{kv.Key.gty}:{kv.Value.Owner}@{kv.Value.PlacedFrame}");
        rows.Sort(StringComparer.Ordinal);
        return string.Join(";", rows);
    }

    [Fact]
    public void Ownership_SurvivesSnapshotRestore_AndReplayMatchesChecksums()
    {
        const int K = 20, N = 80;
        var sim = new Simulation(SimFloor(), new Vector2(40f, 3 * Ts - PlayerCharacter.Radius));
        for (int f = 0; f < K; f++) sim.Step(PaintInput(f));

        var snap        = sim.Snapshot();
        string atSnap   = Claims(sim.Chunks);
        int countAtSnap = sim.Chunks.Ownership.Count;

        var liveSums = new List<ulong>();
        for (int f = K; f < N; f++) { sim.Step(PaintInput(f)); liveSums.Add(sim.Checksum()); }
        string liveEnd = Claims(sim.Chunks);
        output.WriteLine($"claims at snapshot {countAtSnap}, at end {sim.Chunks.Ownership.Count}");
        Assert.True(sim.Chunks.Ownership.Count > countAtSnap, "painting after the snapshot should add claims");

        // Restore: exactly the snapshot's claims, at the snapshot's terrain clock.
        sim.Restore(snap);
        Assert.Equal(atSnap, Claims(sim.Chunks));

        var replaySums = new List<ulong>();
        for (int f = K; f < N; f++) { sim.Step(PaintInput(f)); replaySums.Add(sim.Checksum()); }
        Assert.Equal(liveSums, replaySums);
        Assert.Equal(liveEnd, Claims(sim.Chunks));
    }

    // The checksum actually sees ownership: same grid, different owner ⇒ different sum.
    [Fact]
    public void Checksum_FoldsOwnership()
    {
        var sim = new Simulation(SimFloor(), new Vector2(40f, 3 * Ts - PlayerCharacter.Radius));
        ulong bare = sim.Checksum();
        sim.Chunks.Ownership.Stamp(2, 3, BlockOwnership.Player1, sim.Chunks.TerrainFrame);
        ulong p1 = sim.Checksum();
        sim.Chunks.Ownership.Stamp(2, 3, BlockOwnership.Player2, sim.Chunks.TerrainFrame);
        ulong p2 = sim.Checksum();
        Assert.NotEqual(bare, p1);
        Assert.NotEqual(p1, p2);
    }
}
