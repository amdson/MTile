using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;

namespace MTile.Tests;

// The block-eater plant's tile-side mechanics (Plans/PLANT_SEEDS_PLAN.md §4.5,
// World/BlockEaterInfection.cs): planting legality, chewing through DamageCell, spread
// through contiguous enemy-owned cells, firebreaks, ownership lapse, cleanup on foreign
// breaks, determinism and snapshot/restore mid-spread. Pure ChunkMap tests — terrain is
// built with EditorSetCell and ownership stamped directly.
public class PlantBlockEaterTests
{
    private const float Dt = 1f / 60f;
    private const byte P1 = BlockOwnership.Player1, P2 = BlockOwnership.Player2;

    private static void Solid(ChunkMap m, int gtx, int gty, TileType type, byte owner, int placedFrame = 0)
    {
        m.EditorSetCell(gtx, gty, TileState.Solid, type);
        if (owner != BlockOwnership.None) m.Ownership.Stamp(gtx, gty, owner, placedFrame);
    }

    private static void Tick(ChunkMap m, int frames)
    {
        for (int i = 0; i < frames; i++) m.TickSprouts(Dt);
    }

    // A row of `n` P2-owned cells at y = 5, x = 0..n-1.
    private static ChunkMap Row(int n, TileType type = TileType.Stone)
    {
        var m = new ChunkMap();
        for (int x = 0; x < n; x++) Solid(m, x, 5, type, P2);
        return m;
    }

    private static string Signature(ChunkMap m)
    {
        var sb = new StringBuilder();
        foreach (var kv in m.BlockEater.Entries.OrderBy(k => k.Key.gtx).ThenBy(k => k.Key.gty))
        {
            var c = kv.Value;
            sb.Append($"{kv.Key}:{c.Team},{c.InfectedFrame},{c.HeartGtx},{c.HeartGty},{c.PlantFrame},{c.From};");
        }
        foreach (var kv in m.Damage.Damaged.OrderBy(k => k.Key.gtx).ThenBy(k => k.Key.gty))
            sb.Append($"d{kv.Key}={kv.Value:R};");
        sb.Append($"h={m.TerrainHash};f={m.TerrainFrame}");
        return sb.ToString();
    }

    // ── Legality ────────────────────────────────────────────────────────────────

    [Fact]
    public void Plant_OnlyOnEnemyOwnedSolid_AnyPlaceableMaterial()
    {
        var m = new ChunkMap();
        Solid(m, 0, 0, TileType.Dirt,  P2);
        Solid(m, 2, 0, TileType.Stone, P2);
        Solid(m, 4, 0, TileType.Sand,  P2);
        Solid(m, 6, 0, TileType.Dirt,  P1);                    // own
        Solid(m, 8, 0, TileType.Dirt,  BlockOwnership.None);   // natural terrain
        Solid(m, 10, 0, TileType.Hardened, P2);                // never food
        // (12, 0) is empty air.

        Assert.True(m.CanPlantBlockEater(0, 0, P1));
        Assert.True(m.CanPlantBlockEater(2, 0, P1));
        Assert.True(m.CanPlantBlockEater(4, 0, P1));
        Assert.False(m.CanPlantBlockEater(6, 0, P1));
        Assert.False(m.CanPlantBlockEater(8, 0, P1));
        Assert.False(m.CanPlantBlockEater(10, 0, P1));
        Assert.False(m.CanPlantBlockEater(12, 0, P1));
        Assert.False(m.CanPlantBlockEater(0, 0, BlockOwnership.None));
        // The other player may plant on P1's block.
        Assert.True(m.CanPlantBlockEater(6, 0, P2));

        foreach (var x in new[] { 6, 8, 10, 12 })
            Assert.False(m.TryPlantBlockEater(x, 0, P1));
        Assert.Equal(0, m.BlockEater.Count);

        Assert.True(m.TryPlantBlockEater(0, 0, P1));
        Assert.True(m.BlockEater.TryGet(0, 0, out var c));
        Assert.True(c.IsHeart(0, 0));
        Assert.Equal(P1, c.Team);
        // Already infected.
        Assert.False(m.TryPlantBlockEater(0, 0, P1));
    }

    [Fact]
    public void Plant_CapIsPerTeam_AndFreesWhenThePatchDies()
    {
        int cap = BlockEaterInfection.MaxLivePatchesPerTeam;
        var m = new ChunkMap();
        for (int i = 0; i <= cap; i++) Solid(m, i * 3, 0, TileType.Dirt, P2);
        Solid(m, 100, 0, TileType.Dirt, P1);
        for (int i = 0; i < cap; i++) Assert.True(m.TryPlantBlockEater(i * 3, 0, P1));
        Assert.Equal(cap, m.BlockEater.LivePatches(P1));

        // One more planting by the same team is refused, with no side effect.
        int extra = cap * 3;
        Assert.False(m.CanPlantBlockEater(extra, 0, P1));
        Assert.False(m.TryPlantBlockEater(extra, 0, P1));
        Assert.Equal(cap, m.BlockEater.Count);
        // The other team has its own cap.
        Assert.True(m.TryPlantBlockEater(100, 0, P2));

        // An isolated patch eats its only cell; the cap frees.
        m.BreakCell(0, 0);
        Assert.Equal(cap - 1, m.BlockEater.LivePatches(P1));
        Assert.True(m.TryPlantBlockEater(extra, 0, P1));
    }

    // ── Damage over time ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(TileType.Dirt)]
    [InlineData(TileType.Stone)]
    public void Infection_BreaksTheCellThroughTheNormalPath_OnSchedule(TileType type)
    {
        var m = new ChunkMap();
        Solid(m, 0, 0, type, P2);
        int broken = 0;
        m.OnTileBroken = (_, t) => { if (t == type) broken++; };
        Assert.True(m.TryPlantBlockEater(0, 0, P1));

        int frames = (int)System.MathF.Round(BlockEaterInfection.EatSecondsFor(type) * 60f);
        Tick(m, frames - 1);
        Assert.Equal(TileState.Solid, m.GetCellState(0, 0));
        Assert.True(m.Damage.Get(0, 0) > 0f);
        Tick(m, 1);
        Assert.Equal(TileState.Empty, m.GetCellState(0, 0));
        Assert.Equal(1, broken);
        Assert.False(m.BlockEater.Has(0, 0));
        Assert.False(m.Ownership.TryGet(0, 0, m.TerrainFrame, out _, out _));
    }

    // ── Spread and firebreaks ───────────────────────────────────────────────────

    [Fact]
    public void Spread_WalksContiguousEnemyCells_OneCellPerInterval()
    {
        var m = Row(5);
        Assert.True(m.TryPlantBlockEater(0, 5, P1));
        int I = BlockEaterInfection.SpreadIntervalFrames;

        Tick(m, I - 1);
        Assert.False(m.BlockEater.Has(1, 5));
        Tick(m, 1);
        Assert.True(m.BlockEater.Has(1, 5));
        Assert.False(m.BlockEater.Has(2, 5));
        Assert.True(m.BlockEater.TryGet(1, 5, out var c1));
        Assert.Equal(BlockEaterInfection.FromLeft, c1.From);
        Assert.Equal((0, 0), (c1.HeartGtx, c1.HeartGty - 5));

        Tick(m, 3 * I);
        for (int x = 0; x < 5; x++) Assert.True(m.BlockEater.Has(x, 5), $"cell {x}");
        Assert.Equal(1, m.BlockEater.LivePatches(P1));
    }

    [Fact]
    public void Spread_StopsAtUnownedOwnAndHardenedCells_AndAtAGap()
    {
        var m = new ChunkMap();
        Solid(m, 0, 5, TileType.Stone, P2);                       // heart
        Solid(m, 1, 5, TileType.Stone, BlockOwnership.None);      // natural: firebreak
        Solid(m, 2, 5, TileType.Stone, P2);                       //   enemy beyond it
        Solid(m, 0, 4, TileType.Stone, P1);                       // eater's own: firebreak
        Solid(m, 0, 3, TileType.Stone, P2);                       //   enemy beyond it
        Solid(m, 0, 6, TileType.Hardened, P2);                    // Hardened: never
        // (-1, 5) empty: gap
        Solid(m, -2, 5, TileType.Stone, P2);                      //   enemy beyond it
        Assert.True(m.TryPlantBlockEater(0, 5, P1));

        Tick(m, 5 * BlockEaterInfection.SpreadIntervalFrames);
        Assert.Equal(1, m.BlockEater.Count);
        foreach (var (x, y) in new[] { (1, 5), (2, 5), (0, 4), (0, 3), (0, 6), (-2, 5) })
        {
            Assert.False(m.BlockEater.Has(x, y), $"({x},{y}) infected");
            Assert.Equal(TileState.Solid, m.GetCellState(x, y));
            Assert.Equal(0f, m.Damage.Get(x, y));
        }
    }

    [Fact]
    public void Spread_ReachesEnemyBlocksPlacedNextToTheInfectionLater()
    {
        var m = Row(1);
        Assert.True(m.TryPlantBlockEater(0, 5, P1));
        Tick(m, 10);
        Solid(m, 1, 5, TileType.Stone, P2, placedFrame: m.TerrainFrame);   // a patch-up block
        Tick(m, BlockEaterInfection.SpreadIntervalFrames - 10);
        Assert.True(m.BlockEater.Has(1, 5));
    }

    // ── The defender's answers ──────────────────────────────────────────────────

    [Fact]
    public void BreakingAnInfectedCell_StopsSpreadPastIt()
    {
        var m = Row(4);
        Assert.True(m.TryPlantBlockEater(0, 5, P1));
        int I = BlockEaterInfection.SpreadIntervalFrames;
        Tick(m, I + 1);
        Assert.True(m.BlockEater.Has(1, 5));
        // The defender slashes the freshly infected block out.
        Assert.True(m.BreakCell(1, 5));
        Assert.False(m.BlockEater.Has(1, 5));

        Tick(m, 4 * I);
        Assert.False(m.BlockEater.Has(2, 5));
        Assert.False(m.BlockEater.Has(3, 5));
        Assert.Equal(0f, m.Damage.Get(2, 5));
    }

    [Fact]
    public void ForeignBreaks_ClearTheEntry_EveryPath()
    {
        var m = Row(3, TileType.Dirt);
        m.BlockEater.Restore(new Dictionary<(int, int), BlockEaterInfection.Cell>
        {
            [(0, 5)] = new() { Team = P1, HeartGtx = 0, HeartGty = 5, From = BlockEaterInfection.FromHeart },
            [(1, 5)] = new() { Team = P1, HeartGtx = 0, HeartGty = 5, From = BlockEaterInfection.FromLeft },
            [(2, 5)] = new() { Team = P1, HeartGtx = 0, HeartGty = 5, From = BlockEaterInfection.FromLeft },
        });
        Assert.True(m.DamageCell(0, 5, 100f));                      // a hit
        Assert.True(m.HarvestCell(1, 5, out _));                     // a grab peel
        Assert.True(m.EditorSetCell(2, 5, TileState.Empty, TileType.Dirt));   // an edit
        Assert.Equal(0, m.BlockEater.Count);
    }

    // ── Ownership lapse ─────────────────────────────────────────────────────────

    [Fact]
    public void OwnershipLapse_KillsTheInfectionOnThatCell_BlockSurvives()
    {
        var m = new ChunkMap();
        // A claim ~10 frames from fading below MinStrength (age 11959 frames).
        Solid(m, 0, 0, TileType.Stone, P2, placedFrame: -11950);
        Solid(m, 1, 0, TileType.Stone, P2, placedFrame: 0);
        Assert.True(m.TryPlantBlockEater(0, 0, P1));
        Tick(m, 20);
        Assert.False(m.BlockEater.Has(0, 0));
        Assert.Equal(TileState.Solid, m.GetCellState(0, 0));
        float dealt = m.Damage.Get(0, 0);
        Assert.True(dealt > 0f);

        Tick(m, 3 * BlockEaterInfection.SpreadIntervalFrames);
        Assert.Equal(dealt, m.Damage.Get(0, 0));    // no further chewing
        Assert.False(m.BlockEater.Has(1, 0));       // died before its first spread
        Assert.Equal(0, m.BlockEater.LivePatches(P1));
    }

    // ── Determinism and rollback ────────────────────────────────────────────────

    // Two builds of the same wall in different insertion orders, with BOTH teams'
    // eaters racing into the shared middle, must evolve identically.
    [Fact]
    public void Determinism_IndependentOfInsertionOrder()
    {
        ChunkMap Build(bool reverse)
        {
            var m = new ChunkMap();
            var cells = new List<(int x, int y, byte owner)>();
            for (int x = 0; x < 6; x++)
                for (int y = 0; y < 3; y++)
                    cells.Add((x, y, x < 3 ? P2 : P1));
            if (reverse) cells.Reverse();
            foreach (var (x, y, o) in cells) Solid(m, x, y, TileType.Stone, o);
            return m;
        }
        var a = Build(false);
        var b = Build(true);
        foreach (var m in new[] { a, b })
        {
            Assert.True(m.TryPlantBlockEater(0, 1, P1));
            Assert.True(m.TryPlantBlockEater(5, 1, P2));
        }
        for (int i = 0; i < 600; i++)
        {
            a.TickSprouts(Dt); b.TickSprouts(Dt);
            if (i % 37 == 0) Assert.Equal(Signature(a), Signature(b));
        }
        Assert.Equal(Signature(a), Signature(b));
    }

    [Fact]
    public void SnapshotRestore_MidSpread_ReplaysIdentically()
    {
        var m = new ChunkMap();
        for (int x = 0; x < 6; x++)
            for (int y = 0; y < 3; y++)
                Solid(m, x, y, x == 3 ? TileType.Dirt : TileType.Stone, P2);
        Assert.True(m.TryPlantBlockEater(0, 1, P1));
        Tick(m, 2 * BlockEaterInfection.SpreadIntervalFrames + 10);
        Assert.True(m.BlockEater.Count > 1);

        var snap = m.CaptureTerrain();
        string atCapture = Signature(m);
        Tick(m, 400);
        string first = Signature(m);
        Assert.NotEqual(atCapture, first);

        m.RestoreTerrain(snap);
        Assert.Equal(atCapture, Signature(m));
        Tick(m, 400);
        Assert.Equal(first, Signature(m));
    }
}
