using System;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Hardened rock — the bedrock-grade material added alongside Stone/Dirt/Sand/Foam.
// Three promises, each pinned here against the equivalent stone case so a regression
// shows up as "hardened now behaves like stone" rather than as a silent softening:
// it survives damage that shatters stone many times over, it can't be selected or
// placed, and a block grab refuses it outright — both the peel path and the legacy
// drag-rip. The last tests guard the fixed-size per-material arrays that a new enum
// value would otherwise overrun, and the level-ascii char that is its only source.
public class HardenedBlockTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;

    // Floating solid at cell (4,0), floor at rows 3-4 — the exact terrain in which
    // BlockPeelTests.FreeHangingBlock_OneSweep_GrabsAndThrows lifts a block in one
    // sweep. Zero outward edges, so this is the easiest possible grab in the game.
    private static ChunkMap FloatingBlock() => SimTerrain.FromAscii(@"
        OOOOXOOOOOOO
        OOOOOOOOOOOO
        OOOOOOOOOOOO
        XXXXXXXXXXXX
        XXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    private static ChunkMap DeepGround() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXX
        XXXXXXXXXXXXXXXXXXXXXXXX
        XXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    private static SimConfigMulti Build(InputScript script, ChunkMap terrain, Vector2 start, int frames) => new SimConfigMulti
    {
        Terrain = terrain,
        Frames  = frames,
        Dt      = Dt,
        Gravity = new Vector2(0f, 600f),
        Players = new[] { new SimPlayer { StartPosition = start, Script = script } },
    };

    private static int SolidCount(ChunkMap c, int gtx0, int gtx1, int gty0, int gty1)
    {
        int n = 0;
        for (int gtx = gtx0; gtx <= gtx1; gtx++)
        for (int gty = gty0; gty <= gty1; gty++)
            if (c.GetCellState(gtx, gty) == TileState.Solid) n++;
        return n;
    }

    private static void SetType(ChunkMap map, int gtx, int gty, TileType type)
    {
        int cx = gtx >= 0 ? gtx / Chunk.Size : (gtx - Chunk.Size + 1) / Chunk.Size;
        int cy = gty >= 0 ? gty / Chunk.Size : (gty - Chunk.Size + 1) / Chunk.Size;
        Assert.True(map.TryGet(new Point(cx, cy), out var chunk), $"no chunk under ({gtx},{gty})");
        chunk.Tiles[gtx - cx * Chunk.Size, gty - cy * Chunk.Size].Type = type;
    }

    // World-space center of tile (gtx, gty) on whatever Chunk.TileSize is compiled in.
    private static Vector2 CellCenter(int gtx, int gty) =>
        new(gtx * Chunk.TileSize + Chunk.TileSize / 2f, gty * Chunk.TileSize + Chunk.TileSize / 2f);

    // First solid row in every terrain this file builds (row index 3) — floor top in
    // world px, for placing a standing player regardless of Chunk.TileSize.
    private const int FloorRow = 3;
    private static readonly float FloorTopY = FloorRow * Chunk.TileSize;
    // A player parked 8px above the floor top settles onto it within the first
    // physics step (matches the original 16px-grid authoring: floor top 48, start 40).
    private static readonly float StandingY = FloorTopY - PlayerCharacter.Radius + 4f;

    // PullPointEntity.SpringDistanceScale is a fixed 16px normalization for the peel
    // spring, deliberately independent of Chunk.TileSize — so a drag distance
    // authored as "N tiles out" means N*16 px of cursor travel, not N*Chunk.TileSize,
    // on any grid.
    private const float SpringDistanceScaleTile = 16f;

    private static void WithPeel(bool enabled, Action run)
    {
        var cfg   = MovementConfig.Current;
        bool prev = cfg.BlockPeelEnabled;
        cfg.BlockPeelEnabled = enabled;
        try { run(); } finally { cfg.BlockPeelEnabled = prev; }
    }

    // ── Durability ──────────────────────────────────────────────────────────────

    // Damage that breaks a stone tile with room to spare leaves hardened rock standing.
    // Both cells take the same total, applied in stone-sized bites, so this measures the
    // MaxHP gap directly rather than any special-casing on the damage path.
    [Fact]
    public void HardenedRock_SurvivesDamageThatShattersStone()
    {
        var terrain = DeepGround();
        SetType(terrain, 5, 3, TileType.Stone);
        SetType(terrain, 9, 3, TileType.Hardened);

        float stoneHP = TileDamage.MaxHPFor(TileType.Stone);
        float hardHP  = TileDamage.MaxHPFor(TileType.Hardened);
        output.WriteLine($"stone MaxHP = {stoneHP}, hardened MaxHP = {hardHP}");
        Assert.True(hardHP >= 5f * stoneHP,
            "Hardened rock is meant to be off the end of the material scale, not one notch up.");

        // Twice what stone needs, in stone-sized bites.
        bool stoneBroke = false;
        for (int i = 0; i < 2 && !stoneBroke; i++) stoneBroke = terrain.DamageCell(5, 3, stoneHP);
        Assert.True(stoneBroke, "Two stone-HP worth of damage should break a stone tile.");

        for (int i = 0; i < 2; i++)
            Assert.False(terrain.DamageCell(9, 3, stoneHP),
                "The same damage must not break hardened rock.");
        Assert.Equal(TileState.Solid, terrain.GetCellState(9, 3));

        // It is *hard*, not invincible: pour in its full HP and it does go.
        Assert.True(terrain.DamageCell(9, 3, hardHP),
            "Hardened rock should still break once its (much larger) HP is actually paid.");
    }

    // ── Placement ───────────────────────────────────────────────────────────────

    // The block picker is the only way a material reaches the build/paint/deposit verbs,
    // and PlayerCharacter.ActiveBlockType is its one choke point. Assigning hardened —
    // whether from a GameConfig "StartingBlockType" typo or anywhere else — is refused,
    // leaving the previous selection intact rather than silently arming bedrock.
    [Fact]
    public void HardenedRock_IsNotSelectable()
    {
        Assert.False(TileTypes.IsPlaceable(TileType.Hardened));

        var player = new PlayerCharacter(new Vector2(0f, 0f));
        player.ActiveBlockType = TileType.Stone;
        player.ActiveBlockType = TileType.Hardened;
        Assert.Equal(TileType.Stone, player.ActiveBlockType);

        // The other four still assign, so the guard isn't just freezing the field.
        foreach (var t in new[] { TileType.Stone, TileType.Dirt, TileType.Sand, TileType.Foam })
        {
            player.ActiveBlockType = t;
            Assert.Equal(t, player.ActiveBlockType);
        }
    }

    // ── Grab ────────────────────────────────────────────────────────────────────

    // The peel path. This is BlockPeelTests' one-sweep grab, which lifts a free-hanging
    // block (core-only glue — the cheapest grab there is) and throws it. With the block
    // hardened, the same sweep tethers nothing: no orb, no throw, cell still there.
    [Fact]
    public void FreeHangingHardened_OneSweep_TakesNothing()
    {
        WithPeel(true, () =>
        {
            var terrain = FloatingBlock();
            SetType(terrain, 4, 0, TileType.Hardened);
            var onBlock = CellCenter(4, 0);
            var pullTo  = onBlock + new Vector2(3 * SpringDistanceScaleTile, 0f);   // 3 tiles out — enough to lift stone

            var script = new InputScript()
                .For(10, new PlayerInput { MouseWorldPosition = onBlock })
                .For(15, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = onBlock })
                .For(10, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = pullTo })
                .Forever(new PlayerInput { MouseWorldPosition = pullTo });

            bool sawGrab = false, sawOrb = false, sawThrow = false;
            int maxPeel = 0;
            SimRunner.RunMulti(Build(script, terrain, new Vector2(onBlock.X, StandingY), frames: 60),
                onFrame: (f, ps) =>
                {
                    if (ps[0].CurrentActionName == "BlockGrabAction") sawGrab = true;
                    if (ps[0].CurrentActionVars.OrbHeld) sawOrb = true;
                    maxPeel = Math.Max(maxPeel, ps[0].CurrentActionVars.PeelCount);
                },
                onFrameEntities: (f, ps, es) =>
                {
                    foreach (var e in es) if (e is LobbedAreaProjectile) sawThrow = true;
                });

            output.WriteLine($"max group size = {maxPeel}");
            Assert.True(sawGrab, "The gesture should still start — hardened rock reads as terrain.");
            Assert.Equal(0, maxPeel);
            Assert.False(sawOrb,   "Hardened rock must never come loose into a carried orb.");
            Assert.False(sawThrow, "With nothing in hand there is nothing to throw.");
            Assert.Equal(TileState.Solid, terrain.GetCellState(4, 0));
        });
    }

    // Hardened rock doesn't shield the soft material touching it: painting across a
    // seam lifts the free-hanging stone block and leaves the hardened one behind. This
    // is the difference between "refused admission" and "unliftably heavy in the group"
    // — the latter would have anchored the whole sweep.
    [Fact]
    public void HardenedNeighbor_DoesNotProtectAdjacentStone()
    {
        WithPeel(true, () =>
        {
            // Two free-hanging cells side by side at (4,0) and (5,0).
            var terrain = SimTerrain.FromAscii(@"
                OOOOXXOOOOOO
                OOOOOOOOOOOO
                OOOOOOOOOOOO
                XXXXXXXXXXXX
                XXXXXXXXXXXX", originTileX: 0, originTileY: 0);
            SetType(terrain, 4, 0, TileType.Hardened);
            SetType(terrain, 5, 0, TileType.Stone);

            var onPair = CellCenter(5, 0);
            var pullTo = onPair + new Vector2(4 * SpringDistanceScaleTile, 0f);   // 4 tiles out — the seam adds glue

            var script = new InputScript()
                .For(10, new PlayerInput { MouseWorldPosition = onPair })
                .For(15, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = onPair })
                .For(10, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = pullTo })
                .Forever(new PlayerInput { MouseWorldPosition = pullTo });

            bool sawOrb = false;
            SimRunner.RunMulti(Build(script, terrain, new Vector2(onPair.X, StandingY), frames: 60),
                onFrame: (f, ps) => { if (ps[0].CurrentActionVars.OrbHeld) sawOrb = true; });

            Assert.True(sawOrb, "The stone half of the pair should still peel out.");
            Assert.Equal(TileState.Empty, terrain.GetCellState(5, 0));
            Assert.Equal(TileState.Solid, terrain.GetCellState(4, 0));
        });
    }

    // The legacy one-frame drag-rip (BlockPeelEnabled off) takes every solid cell in a
    // disc around the press site. Hardened cells in that disc are skipped, so a rip
    // centered on one harvests strictly fewer blocks and leaves it standing.
    [Fact]
    public void LegacyDragRip_SkipsHardenedRock()
    {
        WithPeel(false, () =>
        {
            var terrain = DeepGround();
            int before  = SolidCount(terrain, 0, 23, 3, 5);
            // x at the cell-7 center; y sits just under the floor top (the same 4px-into-
            // the-tile offset the original 16px-grid authoring used: floor top 48, y 52).
            var onSurf  = new Vector2(CellCenter(7, FloorRow).X, FloorTopY + 4f);   // cell (7,3)
            var dragTo  = onSurf + new Vector2(20f, 0f);   // past the 12px drag threshold (px, tile-size independent)

            // Two of the cells the rip disc would otherwise take, hardened.
            SetType(terrain, 7, 3, TileType.Hardened);
            SetType(terrain, 7, 4, TileType.Hardened);

            var script = new InputScript()
                .For(10, new PlayerInput { MouseWorldPosition = onSurf })
                .For(5,  new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = onSurf })
                .For(10, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = dragTo })
                .Forever(new PlayerInput { MouseWorldPosition = dragTo });

            int atRelease = -1;
            SimRunner.RunMulti(Build(script, terrain, new Vector2(onSurf.X, StandingY), frames: 50),
                onFrame: (f, ps) => { if (f == 25) atRelease = SolidCount(terrain, 0, 23, 3, 5); });

            // The rip disc is PullPointEntity.LegacyRipRadiusPx (25.6px, fixed — tile-size
            // independent) around the press cell (7,3). At TileSize=16 that is 1.6 tiles,
            // covering only row 3 (5 grabbable after hardening (7,3)) and row 4 likewise
            // (4 grabbable) — the "6" the all-stone case takes
            // (BlockPeelTests.FlagOff_LegacyDragRip_StillWorks) minus the 2 hardened,
            // leaving 4. At TileSize=11 the same 25.6px is 2.33 tiles, wide enough to also
            // reach row 5: the disc now covers 5 cols x rows {3,4} (5 each) plus 3 cols x
            // row 5 (13 solid cells total before hardening), and (7,3)/(7,4) are each one
            // of the 5 in their row, so hardening removes exactly 2, leaving 11.
            Assert.Equal(before - 11, atRelease);
            Assert.Equal(TileState.Solid, terrain.GetCellState(7, 3));
            Assert.Equal(TileState.Solid, terrain.GetCellState(7, 4));
        });
    }

    // ── Enum bookkeeping ────────────────────────────────────────────────────────

    // TileTypes.Count sizes the fixed per-material arrays (the peel/rip material tally,
    // the orb texture table). A new TileType that doesn't bump it overruns them, and a
    // non-contiguous enum breaks the (TileType)i casts that index them.
    [Fact]
    public void TileTypesCount_MatchesTheEnum()
    {
        var values = Enum.GetValues<TileType>();
        Assert.Equal(TileTypes.Count, values.Length);
        for (int i = 0; i < values.Length; i++)
            Assert.Equal(i, (int)values[i]);
    }

    // Level ascii is hardened rock's only source — nothing in game generates or places
    // it — so the saver's char has to be the one TerrainLoader reads back.
    [Fact]
    public void HardenedRock_SavesAsItsOwnAsciiChar()
    {
        var t = new Tile { IsSolid = true, Type = TileType.Hardened };
        Assert.Equal('H', StageSaver.TileChar(in t));
    }
}
