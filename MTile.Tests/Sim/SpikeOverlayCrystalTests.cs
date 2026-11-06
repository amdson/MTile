using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// todo.txt #8: placing a spike SEEDS the cell (a small crystal), which flashes and then
// grows into the ordinary 1-cell spike overlay (CrystalLifecycle.SpikePlacement:
// 1.0 s seed + 0.25 s flash). Only the grown spike damages; seeds/flashes are harmless,
// snapshot with the terrain and replay identically, and die with their block.
public class SpikeOverlayCrystalTests(ITestOutputHelper output)
{
    private const int FloorRow = 14;
    private const float T = Chunk.TileSize;

    private static ChunkMap Arena()
    {
        var rows = new List<string>();
        for (int r = 0; r < FloorRow; r++) rows.Add("OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO");
        rows.Add("XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX");
        rows.Add("XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX");
        return SimTerrain.FromAscii(string.Join("\n", rows));
    }

    private static float FloorTop => FloorRow * T;
    private static int CellX(float worldX) => (int)MathF.Floor(worldX / T);
    private static Vector2 StandAt(float x) => new(x, FloorTop - 20f);
    private static readonly TileSpikes.Side P1Side = new(Faction.Player1, Teams.Player);

    private static Simulation TwoPlayers()
    {
        var sim = new Simulation(Arena(), StandAt(40f));
        sim.AddSecondaryPlayer(StandAt(200f));
        for (int f = 0; f < 40; f++) sim.Step(default, default);   // both settle
        return sim;
    }

    // Seed the floor cells under world x for P1, stamped at the current terrain frame.
    private static void SeedFloor(ChunkMap chunks, float x, int halfWidth = 3)
    {
        int c = CellX(x);
        for (int gx = c - halfWidth; gx <= c + halfWidth; gx++)
            Assert.True(chunks.SpikeSeeds.Set(gx, FloorRow, P1Side, chunks.TerrainFrame));
    }

    // ── The reusable phase machine ──────────────────────────────────────────────

    [Fact]
    public void Lifecycle_SpikePreset_IsOneSecondSeed_QuarterSecondFlash_PermanentGrown()
    {
        var l = CrystalLifecycle.SpikePlacement;
        Assert.Equal(60, l.SeedFrames);
        Assert.Equal(15, l.FlashFrames);
        Assert.Equal(75, l.FramesToGrown);
        Assert.Equal(CrystalPhase.Seed,  l.PhaseAt(0));
        Assert.Equal(CrystalPhase.Seed,  l.PhaseAt(59));
        Assert.Equal(CrystalPhase.Flash, l.PhaseAt(60));
        Assert.Equal(CrystalPhase.Flash, l.PhaseAt(74));
        Assert.Equal(CrystalPhase.Grown, l.PhaseAt(75));
        Assert.Equal(CrystalPhase.Grown, l.PhaseAt(1_000_000));
    }

    [Fact]
    public void Lifecycle_FiniteGrown_ReachesGone()
    {
        // The shape #7 (the crystal wave) will use: grow briefly, then vanish.
        var l = new CrystalLifecycle(10, 4, 6);
        Assert.Equal(CrystalPhase.Grown, l.PhaseAt(14));
        Assert.Equal(CrystalPhase.Grown, l.PhaseAt(19));
        Assert.Equal(CrystalPhase.Gone,  l.PhaseAt(20));
        Assert.Equal(0.5f, l.PhaseProgress(5), 4);
        Assert.Equal(0.5f, l.PhaseProgress(12), 4);
    }

    // ── Sim behaviour ───────────────────────────────────────────────────────────

    [Fact]
    public void SeedAndFlash_AreHarmless_GrownSpikeDamages()
    {
        var sim = TwoPlayers();
        var p2 = sim.SecondaryPlayers[0].Player;
        SeedFloor(sim.Chunks, p2.Body.Position.X);
        int seeded = sim.Chunks.TerrainFrame;
        var life = CrystalLifecycle.SpikePlacement;

        int firstHurtElapsed = -1, firstSpikedElapsed = -1;
        float prev = p2.Health;
        for (int f = 0; f < 150 && firstHurtElapsed < 0; f++)
        {
            sim.Step(default, default);
            int elapsed = sim.Chunks.TerrainFrame - seeded;
            if (firstSpikedElapsed < 0 && sim.Chunks.Spikes.Count > 0) firstSpikedElapsed = elapsed;
            if (elapsed < life.FramesToGrown)
            {
                // Still a crystal: no overlay, no damage.
                Assert.Equal(0, sim.Chunks.Spikes.Count);
                Assert.Equal(7, sim.Chunks.SpikeSeeds.Count);
                Assert.Equal(p2.MaxHealth, p2.Health);
            }
            if (p2.Health < prev - 1e-4f) firstHurtElapsed = elapsed;
            prev = p2.Health;
        }
        output.WriteLine($"spiked at +{firstSpikedElapsed} frames, first hurt at +{firstHurtElapsed}");
        Assert.Equal(life.FramesToGrown, firstSpikedElapsed);
        Assert.Equal(0, sim.Chunks.SpikeSeeds.Count);
        Assert.Equal(7, sim.Chunks.Spikes.Count);
        // A standing body on a fresh spike is bitten the frame it grows.
        Assert.Equal(life.FramesToGrown, firstHurtElapsed);
        Assert.True(sim.Chunks.Spikes.TryGet(CellX(p2.Body.Position.X), FloorRow, out var side));
        Assert.Equal(Faction.Player1, side.Faction);
    }

    [Fact]
    public void SeedPhase_IsVisibleAsSeedThenFlash()
    {
        var chunks = Arena();
        Assert.True(chunks.SpikeSeeds.Set(5, FloorRow, P1Side, chunks.TerrainFrame));
        var phases = new List<CrystalPhase>();
        for (int f = 0; f < 74; f++)
        {
            chunks.TickSprouts(Simulation.FixedDt);
            Assert.True(chunks.SpikeSeeds.TryGet(5, FloorRow, out var s));
            phases.Add(TileSpikeSeeds.PhaseOf(s, chunks.TerrainFrame));
        }
        Assert.Equal(59, phases.FindAll(p => p == CrystalPhase.Seed).Count);    // frames 1..59
        Assert.Equal(15, phases.FindAll(p => p == CrystalPhase.Flash).Count);   // frames 60..74
        chunks.TickSprouts(Simulation.FixedDt);
        Assert.False(chunks.SpikeSeeds.Has(5, FloorRow));
        Assert.True(chunks.Spikes.IsSpiked(5, FloorRow));
    }

    [Fact]
    public void Input_RmbWithSpikesSelected_SeedsThenGrows()
    {
        var sim = new Simulation(Arena(), StandAt(60f));
        for (int f = 0; f < 30; f++) sim.Step(default);
        var target = new Vector2(60f + T, FloorTop + 0.5f * T);
        int gx = CellX(target.X);
        sim.Step(new PlayerInput { Num5 = true, MouseWorldPosition = target });
        // Shift+RMB: the press-frame spike path. Plain RMB with spikes selected is the
        // painter's gesture — a press in solid charges the crystal wave (todo.txt #7;
        // SpikeOverlayCrystalWaveTests covers that gesture).
        sim.Step(new PlayerInput { RightClick = true, Shift = true, MouseWorldPosition = target });
        Assert.True(sim.Chunks.SpikeSeeds.Has(gx, FloorRow));
        Assert.False(sim.Chunks.Spikes.IsSpiked(gx, FloorRow));

        // Holding RMB over a seeded cell doesn't re-seed or re-charge.
        var m = sim.Player.Abilities.Meters;
        float hold0 = m.BuildMove + m.EruptMove + m.Build;
        for (int f = 0; f < 10; f++) sim.Step(new PlayerInput { RightClick = true, Shift = true, MouseWorldPosition = target });
        Assert.True(m.BuildMove + m.EruptMove + m.Build >= hold0 - 1e-3f);

        for (int f = 0; f < CrystalLifecycle.SpikePlacement.FramesToGrown; f++) sim.Step(default);
        Assert.True(sim.Chunks.Spikes.IsSpiked(gx, FloorRow));
        Assert.False(sim.Chunks.SpikeSeeds.Has(gx, FloorRow));
    }

    [Fact]
    public void BreakingTheBlockUnderASeed_RemovesIt_NoSpikeEverGrows()
    {
        var chunks = Arena();
        var ab = new PlayerAbilityState();
        var ctx = new EnvironmentContext { Chunks = chunks, Faction = Faction.Player1, Team = Teams.Player };
        float pool = ab.Meters.BuildMove + ab.Meters.EruptMove;
        Assert.True(BlockPlaceAction.TrySpikeCell(ctx, ab, 5, FloorRow));
        chunks.TickSprouts(Simulation.FixedDt);

        // The first hit knocks the seed off and leaves the block whole; stone then breaks
        // in its usual 4 quarter-HP hits.
        float hit = TileDamage.MaxHPFor(TileType.Stone) / 4f;
        Assert.False(chunks.DamageCell(5, FloorRow, hit));
        Assert.False(chunks.SpikeSeeds.Has(5, FloorRow));
        Assert.Equal(0f, chunks.Damage.Get(5, FloorRow));
        int n = 0;
        while (!chunks.DamageCell(5, FloorRow, hit)) Assert.True(++n < 10);
        Assert.Equal(3, n);

        for (int f = 0; f < 120; f++) chunks.TickSprouts(Simulation.FixedDt);
        Assert.Equal(0, chunks.Spikes.Count);
        // No refund: the cost was paid at seeding.
        Assert.Equal(pool - MaterialStrengths.SpikeBuildCost, ab.Meters.BuildMove + ab.Meters.EruptMove, 4);

        // HarvestCell (block grab) and the level editor clear it too.
        chunks.SpikeSeeds.Set(8, FloorRow, P1Side, chunks.TerrainFrame);
        Assert.True(chunks.HarvestCell(8, FloorRow, out _));
        Assert.False(chunks.SpikeSeeds.Has(8, FloorRow));
        chunks.SpikeSeeds.Set(9, FloorRow, P1Side, chunks.TerrainFrame);
        Assert.True(chunks.EditorSetCell(9, FloorRow, TileState.Solid, TileType.Dirt));
        Assert.False(chunks.SpikeSeeds.Has(9, FloorRow));
    }

    [Fact]
    public void SnapshotMidSeed_RestoresAndReplaysIdentically()
    {
        var sim = TwoPlayers();
        var p2 = sim.SecondaryPlayers[0].Player;
        SeedFloor(sim.Chunks, p2.Body.Position.X);
        for (int f = 0; f < 30; f++) sim.Step(default, default);   // mid-seed

        var snap = sim.Snapshot();
        ulong snapSum = sim.Checksum();

        var sums = new List<ulong>();
        var hp = new List<float>();
        for (int f = 0; f < 90; f++) { sim.Step(default, default); sums.Add(sim.Checksum()); hp.Add(p2.Health); }
        Assert.Equal(0, sim.Chunks.SpikeSeeds.Count);
        Assert.Equal(7, sim.Chunks.Spikes.Count);
        Assert.True(p2.Health < p2.MaxHealth);

        sim.Restore(snap);
        Assert.Equal(snapSum, sim.Checksum());
        Assert.Equal(7, sim.Chunks.SpikeSeeds.Count);
        Assert.Equal(0, sim.Chunks.Spikes.Count);
        Assert.Equal(p2.MaxHealth, p2.Health);
        for (int f = 0; f < 90; f++)
        {
            sim.Step(default, default);
            Assert.Equal(sums[f], sim.Checksum());
            Assert.Equal(hp[f], p2.Health);
        }
    }

    [Fact]
    public void Checksum_SeesSeedAndItsClock()
    {
        var sim = new Simulation(Arena(), StandAt(40f));
        for (int f = 0; f < 5; f++) sim.Step(default);
        ulong a = sim.Checksum();
        sim.Chunks.SpikeSeeds.Set(10, FloorRow, P1Side, 3);
        ulong b = sim.Checksum();
        sim.Chunks.SpikeSeeds.Clear(10, FloorRow);
        sim.Chunks.SpikeSeeds.Set(10, FloorRow, P1Side, 4);
        ulong c = sim.Checksum();
        Assert.NotEqual(a, b);
        Assert.NotEqual(b, c);
    }
}
