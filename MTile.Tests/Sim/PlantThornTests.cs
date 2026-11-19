using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Thorns (Plans/PLANT_SEEDS_PLAN.md §4.3): a planted patch of spike tiles that spreads
// one cell per ~1.5 s along surfaces, under per-patch and per-team caps, stops when its
// seed is knocked off, and reinforces the blocks its thorns cling to. Tile-side only —
// the planting hook (seed bar) is built elsewhere and calls ChunkMap.TryPlantThornPatch.
public class PlantThornTileTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;
    private const byte P1 = (byte)Teams.Player;
    private const byte P2 = (byte)Teams.Player2;
    private const int Interval = ThornPatches.SpreadIntervalFrames;

    // A wide flat floor: open rows 0..7, solid rows 8..9. FloorRow is the solid surface.
    private const int FloorRow = 8;
    private static ChunkMap Flat(int width = 60, bool dirt = true)
    {
        var rows = new List<string>();
        for (int r = 0; r < FloorRow; r++) rows.Add(new string('O', width));
        rows.Add(new string('X', width));
        rows.Add(new string('X', width));
        var map = SimTerrain.FromAscii(string.Join("\n", rows));
        if (dirt) for (int x = 0; x < width; x++) map.EditorSetCell(x, FloorRow, TileState.Solid, TileType.Dirt);
        return map;
    }

    // A two-high tunnel: ceiling row 4, open rows 5..6, dirt floor row 7 — so each patch
    // has far more than MaxCellsPerPatch candidate cells within MaxRadius of its seed.
    private const int TunnelFloor = 7;
    private static ChunkMap Tunnel(int width = 80)
    {
        var rows = new List<string>();
        for (int r = 0; r < 4; r++) rows.Add(new string('O', width));
        rows.Add(new string('X', width));
        rows.Add(new string('O', width));
        rows.Add(new string('O', width));
        rows.Add(new string('X', width));
        rows.Add(new string('X', width));
        var map = SimTerrain.FromAscii(string.Join("\n", rows));
        for (int x = 0; x < width; x++) map.EditorSetCell(x, TunnelFloor, TileState.Solid, TileType.Dirt);
        return map;
    }

    private static void Tick(ChunkMap map, int frames)
    {
        for (int i = 0; i < frames; i++) map.TickSprouts(Dt);
    }

    private static List<(int, int)> CellsOf(ChunkMap map)
        => map.Thorns.CellEntries.Select(kv => kv.Key).OrderBy(c => c.gty).ThenBy(c => c.gtx)
              .Select(c => (c.gtx, c.gty)).ToList();

    // ── Legality ────────────────────────────────────────────────────────────────

    [Fact]
    public void Plant_NeedsAnOpenCellAgainstDirt_AndIllegalHasNoSideEffects()
    {
        var stone = Flat(dirt: false);
        ulong h0 = stone.TerrainHash;
        Assert.False(stone.CanPlantThornPatch(10, FloorRow - 1, P1));
        Assert.False(stone.TryPlantThornPatch(10, FloorRow - 1, P1));      // stone face: plants root in dirt only
        Assert.Equal(TileState.Empty, stone.GetCellState(10, FloorRow - 1));
        Assert.Equal(0, stone.Thorns.CellCount);
        Assert.Equal(0, stone.Thorns.PatchCount);
        Assert.Equal(h0, stone.TerrainHash);

        var map = Flat();
        Assert.False(map.TryPlantThornPatch(10, FloorRow - 3, P1));          // unsupported air
        Assert.False(map.TryPlantThornPatch(10, FloorRow, P1));              // inside solid
        Assert.True(map.CanPlantThornPatch(10, FloorRow - 1, P1));
        Assert.True(map.TryPlantThornPatch(10, FloorRow - 1, P1));
        Assert.Equal(TileState.Spike, map.GetCellState(10, FloorRow - 1));
        Assert.Equal(P1, map.GetCellTeam(10, FloorRow - 1));
        Assert.True(map.SpikeSeeds.Has(10, FloorRow - 1));                    // arms like any spike
        Assert.True(map.Thorns.IsSeedCell(10, FloorRow - 1));
        Assert.False(map.CanPlantThornPatch(10, FloorRow - 1, P1));          // occupied now

        // An ordinary spike on the next cell is not a thorn cell, and blocks planting.
        Assert.True(map.TryRequestSpike(20, FloorRow - 1, P1));
        Assert.False(map.Thorns.IsThornCell(20, FloorRow - 1));
        Assert.False(map.TryPlantThornPatch(20, FloorRow - 1, P1));

        // Dirt on the side counts too (a dirt wall), whatever the floor is.
        var wall = Flat(dirt: false);
        wall.EditorSetCell(30, FloorRow - 1, TileState.Solid, TileType.Dirt);
        Assert.True(wall.TryPlantThornPatch(30, FloorRow - 3, P1) == false);   // not adjacent
        Assert.True(wall.TryPlantThornPatch(31, FloorRow - 1, P1));            // dirt on its left
    }

    // ── Spreading ───────────────────────────────────────────────────────────────

    [Fact]
    public void Spreads_OneCellPerInterval_NearestFirst_OnAnySolidFace()
    {
        var map = Flat(dirt: false);
        map.EditorSetCell(20, FloorRow, TileState.Solid, TileType.Dirt);   // only the seed's root is dirt
        Assert.True(map.TryPlantThornPatch(20, FloorRow - 1, P1));

        Tick(map, Interval - 1);
        Assert.Equal(1, map.Thorns.CellCount);
        Tick(map, 1);
        Assert.Equal(2, map.Thorns.CellCount);
        Assert.True(map.Thorns.IsThornCell(19, FloorRow - 1));              // nearest, left on ties
        Tick(map, Interval);
        Assert.Equal(3, map.Thorns.CellCount);
        Assert.True(map.Thorns.IsThornCell(21, FloorRow - 1));              // then the right
        Tick(map, Interval * 2);
        Assert.Equal(5, map.Thorns.CellCount);                              // over stone: offshoots use the spike rule
        foreach (var (x, y) in CellsOf(map))
        {
            Assert.Equal(TileState.Spike, map.GetCellState(x, y));
            Assert.Equal(P1, map.GetCellTeam(x, y));
            Assert.InRange(x, 18, 22);
        }
        // Each new thorn arms through its own spike seed.
        Assert.True(map.SpikeSeeds.Has(22, FloorRow - 1) || map.SpikeSeeds.Has(18, FloorRow - 1));
    }

    [Fact]
    public void Spreading_IsDeterministic()
    {
        var a = Tunnel();
        var b = Tunnel();
        Assert.True(a.TryPlantThornPatch(20, TunnelFloor - 1, P1));
        Assert.True(b.TryPlantThornPatch(20, TunnelFloor - 1, P1));
        Assert.True(a.TryPlantThornPatch(50, TunnelFloor - 1, P2));
        Assert.True(b.TryPlantThornPatch(50, TunnelFloor - 1, P2));
        for (int f = 0; f < Interval * 12; f++)
        {
            a.TickSprouts(Dt); b.TickSprouts(Dt);
            Assert.Equal(a.TerrainHash, b.TerrainHash);
        }
        Assert.Equal(CellsOf(a), CellsOf(b));
        Assert.Equal(a.Thorns.Patches.ToArray(), b.Thorns.Patches.ToArray());
    }

    // ── Caps ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Patch_StaysWithinRadius_OfItsSeed()
    {
        var map = Flat();
        Assert.True(map.TryPlantThornPatch(20, FloorRow - 1, P1));
        Tick(map, Interval * 20);
        var cells = CellsOf(map);
        output.WriteLine(string.Join(" ", cells));
        foreach (var (x, y) in cells)
            Assert.True(Math.Abs(x - 20) + Math.Abs(y - (FloorRow - 1)) <= ThornPatches.MaxRadius);
    }

    [Fact]
    public void Patch_StopsAtMaxCells()
    {
        var map = Tunnel();
        Assert.True(map.TryPlantThornPatch(20, TunnelFloor - 1, P1));
        Tick(map, Interval * (ThornPatches.MaxCellsPerPatch + 5));
        Assert.Equal(ThornPatches.MaxCellsPerPatch, map.Thorns.CellCount);
        foreach (var (x, y) in CellsOf(map))
            Assert.True(Math.Abs(x - 20) + Math.Abs(y - (TunnelFloor - 1)) <= ThornPatches.MaxRadius);
    }

    [Fact]
    public void Team_LivePatches_AreCapped_PerTeam_AndFreeWhenTheSeedDies()
    {
        int cap = ThornPatches.MaxPatchesPerTeam;
        var map = Tunnel(12 * (cap + 3));
        int X(int i) => 6 + i * 12;
        for (int i = 0; i < cap; i++) Assert.True(map.TryPlantThornPatch(X(i), TunnelFloor - 1, P1));
        Assert.False(map.CanPlantThornPatch(X(cap), TunnelFloor - 1, P1));
        Assert.False(map.TryPlantThornPatch(X(cap), TunnelFloor - 1, P1));
        Assert.True(map.TryPlantThornPatch(X(cap), TunnelFloor - 1, P2));    // the cap is per team

        // A patch whose seed is gone no longer counts against the cap.
        Assert.False(map.DamageCell(X(0), TunnelFloor - 1, 1f));
        Assert.True(map.TryPlantThornPatch(X(cap + 1), TunnelFloor - 1, P1));
    }

    // ── Seed death ──────────────────────────────────────────────────────────────

    [Fact]
    public void SeedKnockedOff_StopsSpreading_OffshootsRemain()
    {
        var map = Flat();
        Assert.True(map.TryPlantThornPatch(20, FloorRow - 1, P1));
        Tick(map, Interval * 2);
        Assert.Equal(3, map.Thorns.CellCount);

        Assert.False(map.DamageCell(20, FloorRow - 1, 0.01f));               // any hit knocks it off
        Assert.Equal(TileState.Empty, map.GetCellState(20, FloorRow - 1));
        Assert.Equal(2, map.Thorns.CellCount);
        Assert.False(map.Thorns.Patches[0].Spreading);

        Tick(map, Interval * 10);
        Assert.Equal(2, map.Thorns.CellCount);
        Assert.Equal(TileState.Empty, map.GetCellState(20, FloorRow - 1));   // not regrown either
    }

    // ── Cleanup ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ClearedThornCells_LeaveTheTable_ByEveryPath()
    {
        var map = Flat();
        Assert.True(map.TryPlantThornPatch(20, FloorRow - 1, P1));
        Tick(map, Interval * 4);                                             // cells 18..22
        Assert.Equal(5, map.Thorns.CellCount);

        // Knocked off by a hit.
        map.DamageCell(18, FloorRow - 1, 0.5f);
        Assert.False(map.Thorns.IsThornCell(18, FloorRow - 1));

        // Its only support broke.
        Assert.True(map.BreakCell(22, FloorRow));
        Assert.Equal(TileState.Empty, map.GetCellState(22, FloorRow - 1));
        Assert.False(map.Thorns.IsThornCell(22, FloorRow - 1));

        // A block built over it.
        Assert.NotNull(map.TryRequestTile(21, FloorRow - 1, TileType.Stone));
        Assert.False(map.Thorns.IsThornCell(21, FloorRow - 1));

        // An editor write.
        map.EditorSetCell(19, FloorRow - 1, TileState.Solid, TileType.Dirt);
        Assert.False(map.Thorns.IsThornCell(19, FloorRow - 1));

        Assert.Equal(1, map.Thorns.CellCount);
        Assert.Equal(1, map.Thorns.PatchCount);
        map.DamageCell(20, FloorRow - 1, 1f);                                // the seed, last cell
        Assert.Equal(0, map.Thorns.CellCount);
        Assert.Equal(0, map.Thorns.PatchCount);                              // pruned with its last cell
    }

    // ── Reinforcement ───────────────────────────────────────────────────────────

    [Fact]
    public void ThornedBlock_TakesHalfDamage_OnlyWhileTheThornStands()
    {
        var map = Flat();
        Assert.True(map.TryPlantThornPatch(20, FloorRow - 1, P1));
        Assert.False(map.DamageCell(20, FloorRow, 0.4f));
        Assert.Equal(0.4f * ThornPatches.ReinforceDamageScale, map.Damage.Get(20, FloorRow), 4);

        // A block the thorn does not touch takes full damage.
        map.DamageCell(25, FloorRow, 0.4f);
        Assert.Equal(0.4f, map.Damage.Get(25, FloorRow), 4);

        // A block beside the thorn on a SIDE face is reinforced too (any face).
        map.EditorSetCell(21, FloorRow - 1, TileState.Solid, TileType.Stone);
        map.DamageCell(21, FloorRow - 1, 0.4f);
        Assert.Equal(0.4f * ThornPatches.ReinforceDamageScale, map.Damage.Get(21, FloorRow - 1), 4);

        // Strip the thorn: full damage from then on.
        map.DamageCell(20, FloorRow - 1, 0.1f);
        map.DamageCell(20, FloorRow, 0.4f);
        Assert.Equal(0.4f * ThornPatches.ReinforceDamageScale + 0.4f, map.Damage.Get(20, FloorRow), 4);
    }

    [Fact]
    public void HandPlacedSpike_DoesNotReinforce()
    {
        var map = Flat();
        Assert.True(map.TryRequestSpike(20, FloorRow - 1, P1));
        map.SpikeSeeds.Clear(20, FloorRow - 1);                              // armed, too
        map.DamageCell(20, FloorRow, 0.4f);
        Assert.Equal(0.4f, map.Damage.Get(20, FloorRow), 4);
        Assert.Equal(0, map.Thorns.CellCount);
    }

    // ── Snapshot / restore ──────────────────────────────────────────────────────

    [Fact]
    public void Table_RoundTripsThroughTerrainSnapshot_MidSpread()
    {
        var map = Tunnel();
        Assert.True(map.TryPlantThornPatch(20, TunnelFloor - 1, P1));
        Assert.True(map.TryPlantThornPatch(50, TunnelFloor - 1, P2));
        Tick(map, Interval * 3 + 17);                                         // mid-interval
        var snap = map.CaptureTerrain();
        var cells0 = CellsOf(map);
        var patches0 = map.Thorns.Patches.ToArray();
        ulong hash0 = map.TerrainHash;

        // Go on: spread more, knock a seed off, strip a thorn.
        Tick(map, Interval * 4);
        map.DamageCell(50, TunnelFloor - 1, 1f);
        Tick(map, Interval * 3);
        var cellsA = CellsOf(map);
        var patchesA = map.Thorns.Patches.ToArray();
        Assert.NotEqual(cells0, cellsA);

        map.RestoreTerrain(snap);
        Assert.Equal(cells0, CellsOf(map));
        Assert.Equal(patches0, map.Thorns.Patches.ToArray());
        Assert.Equal(hash0, map.TerrainHash);
        foreach (var (x, y) in cells0) Assert.Equal(TileState.Spike, map.GetCellState(x, y));

        // Replaying the same inputs lands on the same table.
        Tick(map, Interval * 4);
        map.DamageCell(50, TunnelFloor - 1, 1f);
        Tick(map, Interval * 3);
        Assert.Equal(cellsA, CellsOf(map));
        Assert.Equal(patchesA, map.Thorns.Patches.ToArray());
    }
}

// Thorns through the whole sim and through CombatSystem: contact damage (opposing team
// only), the checksum, and the strip-then-cut ordering of a swing over a thorn + block.
public class PlantThornCombatTests(ITestOutputHelper output)
{
    private const int FloorRow = 14;
    private const float T = Chunk.TileSize;

    private static ChunkMap Arena()
    {
        var rows = new List<string>();
        for (int r = 0; r < FloorRow; r++) rows.Add(new string('O', 38));
        rows.Add(new string('X', 38));
        rows.Add(new string('X', 38));
        var map = SimTerrain.FromAscii(string.Join("\n", rows));
        for (int x = 0; x < 38; x++) map[new Point(x / Chunk.Size, FloorRow / Chunk.Size)]
            .Tiles[x % Chunk.Size, FloorRow % Chunk.Size].Type = TileType.Dirt;   // pre-hash, unjournaled
        return map;
    }

    private static Vector2 StandAt(float x) => new(x, FloorRow * T - 20f);
    private static int CellX(float worldX) => (int)MathF.Floor(worldX / T);

    private static void ArmAll(ChunkMap chunks)
    {
        foreach (var kv in chunks.Thorns.CellEntries.ToList()) chunks.SpikeSeeds.Clear(kv.Key.gtx, kv.Key.gty);
    }

    [Fact]
    public void ThornPatch_HurtsTheOtherTeam_NotItsOwner()
    {
        var sim = new Simulation(Arena(), StandAt(40f));
        sim.AddSecondaryPlayer(StandAt(200f));
        var p1 = sim.Player;
        var p2 = sim.SecondaryPlayers[0].Player;
        for (int f = 0; f < 40; f++) sim.Step(default, default);

        // P1's patch under P2, and another P1 patch under P1 itself.
        Assert.True(sim.Chunks.TryPlantThornPatch(CellX(p2.Body.Position.X), FloorRow - 1, (byte)p1.Team));
        Assert.True(sim.Chunks.TryPlantThornPatch(CellX(p1.Body.Position.X), FloorRow - 1, (byte)p1.Team));
        ArmAll(sim.Chunks);
        float h2 = p2.Health;
        for (int f = 0; f < 120; f++) sim.Step(default, default);
        output.WriteLine($"P2 lost {h2 - p2.Health:F3}; thorn cells {sim.Chunks.Thorns.CellCount}");
        Assert.True(p2.Health < h2);
        Assert.Equal(p1.MaxHealth, p1.Health);
    }

    [Fact]
    public void Sim_SnapshotRestore_ReplaysThornGrowthIdentically()
    {
        var sim = new Simulation(Arena(), StandAt(40f));
        for (int f = 0; f < 20; f++) sim.Step(default);
        Assert.True(sim.Chunks.TryPlantThornPatch(20, FloorRow - 1, (byte)Teams.Player2));
        for (int f = 0; f < 100; f++) sim.Step(default);

        var snap = sim.Snapshot();
        ulong s0 = sim.Checksum();
        var sums = new List<ulong>();
        for (int f = 0; f < 400; f++) { sim.Step(default); sums.Add(sim.Checksum()); }
        int grown = sim.Chunks.Thorns.CellCount;
        Assert.True(grown > 1);

        sim.Restore(snap);
        Assert.Equal(s0, sim.Checksum());
        for (int f = 0; f < 400; f++) { sim.Step(default); Assert.Equal(sums[f], sim.Checksum()); }
        Assert.Equal(grown, sim.Chunks.Thorns.CellCount);
    }

    [Fact]
    public void Checksum_SeesTheThornTable()
    {
        var a = new Simulation(Arena(), StandAt(40f));
        var b = new Simulation(Arena(), StandAt(40f));
        Assert.True(a.Chunks.TryPlantThornPatch(20, FloorRow - 1, (byte)Teams.Player2));
        Assert.True(b.Chunks.TryRequestSpike(20, FloorRow - 1, Teams.Player2));   // same tile, no patch
        Assert.Equal(a.Chunks.TerrainHash, b.Chunks.TerrainHash);
        Assert.NotEqual(a.Checksum(), b.Checksum());
    }

    // One combat frame with a single tile-only hitbox.
    private static void Swing(ChunkMap chunks, BoundingBox region, float damage, Vector2? origin = null)
    {
        var hitboxes = new HitboxWorld();
        hitboxes.Publish(new Hitbox(region, 1, damage, Vector2.Zero, Faction.Player2, EntityId.None,
                                    targets: HitTargets.TilesOnly, origin: origin));
        new CombatSystem().Apply(chunks, hitboxes, new HurtboxWorld(), _ => null);
    }

    private static BoundingBox Cells(int gtx0, int gty0, int gtx1, int gty1)
        => new(gtx0 * T, gty0 * T, (gtx1 + 1) * T - 0.01f, (gty1 + 1) * T - 0.01f);

    [Fact]
    public void Swing_OverThornAndBlock_DealsReducedDamage_ThenStripsTheThorn()
    {
        var map = Arena();
        Assert.True(map.TryPlantThornPatch(10, FloorRow - 1, (byte)Teams.Player));
        var region = Cells(10, FloorRow - 1, 10, FloorRow);
        var origin = new Vector2(10.5f * T, (FloorRow - 3) * T);   // from above, occluded

        Swing(map, region, 0.4f, origin);
        Assert.Equal(0.4f * ThornPatches.ReinforceDamageScale, map.Damage.Get(10, FloorRow), 4);
        Assert.False(map.Thorns.IsThornCell(10, FloorRow - 1));            // stripped
        Assert.Equal(TileState.Empty, map.GetCellState(10, FloorRow - 1));

        Swing(map, region, 0.4f, origin);                                  // next frame: full
        Assert.Equal(0.4f * ThornPatches.ReinforceDamageScale + 0.4f, map.Damage.Get(10, FloorRow), 4);
    }

    [Fact]
    public void Swing_OverHandPlacedSpike_LeavesItAsToday()
    {
        var map = Arena();
        Assert.True(map.TryRequestSpike(10, FloorRow - 1, Teams.Player));
        Swing(map, Cells(10, FloorRow - 1, 10, FloorRow), 0.4f);
        Assert.Equal(TileState.Spike, map.GetCellState(10, FloorRow - 1));
        Assert.Equal(0.4f, map.Damage.Get(10, FloorRow), 4);
    }
}
