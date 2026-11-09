using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// todo.txt #7 — the spiked avalanche ("crystal wave"). Name matches the SpikeOverlay term
// in scripts/test-group.py, so it runs in both the combat and terrain groups.
public class SpikeOverlayCrystalWaveTests(ITestOutputHelper output)
{
    private static float Ts => Chunk.TileSize;
    private const int Cols = 60, Rows = 20, FloorRow = 12;
    private static readonly float RestOffset = PlayerCharacter.Radius * (1f + MathF.Sin(MathF.PI / 3f));

    private static ChunkMap Arena(int wallFrom = -1, int wallTo = -1, int wallTop = 0)
    {
        var sb = new StringBuilder();
        for (int y = 0; y < Rows; y++)
        {
            for (int x = 0; x < Cols; x++)
                sb.Append(y >= FloorRow || x < 2 || x >= Cols - 1 || (x >= wallFrom && x < wallTo && y >= wallTop) ? 'X' : 'O');
            sb.Append('\n');
        }
        return SimTerrain.FromAscii(sb.ToString());
    }

    private static Vector2 Standing(float col) => new(col * Ts + Ts * 0.5f, FloorRow * Ts - RestOffset);
    private static Vector2 CellCenter(int gtx, int gty) => new(gtx * Ts + Ts * 0.5f, gty * Ts + Ts * 0.5f);

    private static Simulation NewSim(ChunkMap terrain, Vector2 p1, Vector2 p2)
    {
        var sim = new Simulation(terrain, p1);
        sim.AddSecondaryPlayer(p2);
        for (int f = 0; f < 30; f++) sim.Step(default, default);
        return sim;
    }

    // A P1 crystal wave skimming the floor rightward from column `col`.
    private static MassBall LaunchWave(Simulation sim, int col, float speed = 320f, float mass = 100f)
    {
        var ball = new MassBall(CellCenter(col, FloorRow - 1), new Vector2(speed, 0f), mass,
                                TileType.Stone, Faction.Player1, crystal: true) { Team = Teams.Player };
        sim.SpawnEntity(ball);
        return ball;
    }

    private static void Step(Simulation sim, int frames)
    {
        for (int f = 0; f < frames; f++) sim.Step(default, default);
    }

    private static int Visible(ChunkMap c)
    {
        int now = c.TerrainFrame, n = 0;
        foreach (var (_, cr) in c.Crystals.Entries)
            if (!cr.Pending && cr.PhaseAt(now) != CrystalPhase.Gone) n++;
        return n;
    }

    private static int CountPhase(ChunkMap c, CrystalPhase phase)
    {
        int now = c.TerrainFrame, n = 0;
        foreach (var (_, cr) in c.Crystals.Entries)
            if (!cr.Pending && cr.PhaseAt(now) == phase) n++;
        return n;
    }

    private static int OccupiedAboveFloor(ChunkMap t)
    {
        int n = 0;
        for (int y = 0; y < FloorRow; y++)
        for (int x = 2; x < Cols - 1; x++)
            if (t.GetCellState(x, y) != TileState.Empty) n++;
        return n;
    }

    // Far-away P2 so nobody is hit, unless a test moves them.
    private static Simulation QuietSim(ChunkMap terrain = null)
        => NewSim(terrain ?? Arena(), Standing(4), Standing(Cols - 4));

    [Fact]
    public void Wave_RootWarnsAboutOneSecond_ThenFrontPropagatesFast()
    {
        var sim = QuietSim();
        int f0 = sim.Chunks.TerrainFrame;
        // A full-charge wave: with the ordinary avalanche leak it runs ~14 cells.
        LaunchWave(sim, 8, mass: CrystalWave.MaxBallMass);
        int firstSeed = -1, firstGrown = -1;
        int maxGrownX = int.MinValue, frontFrame10 = -1, frontFrame20 = -1;
        for (int f = 0; f < 200; f++)
        {
            Step(sim, 1);
            int now = sim.Chunks.TerrainFrame - f0;
            if (firstSeed < 0 && Visible(sim.Chunks) > 0) firstSeed = now;
            if (firstGrown < 0 && CountPhase(sim.Chunks, CrystalPhase.Grown) > 0) firstGrown = now;
            foreach (var (cell, c) in sim.Chunks.Crystals.Entries)
                if (!c.Pending && c.PhaseAt(sim.Chunks.TerrainFrame) >= CrystalPhase.Grown)
                    maxGrownX = Math.Max(maxGrownX, cell.gtx);
            if (frontFrame10 < 0 && maxGrownX >= 10) frontFrame10 = now;
            if (frontFrame20 < 0 && maxGrownX >= 20) frontFrame20 = now;
        }
        output.WriteLine($"first seed +{firstSeed}, first grown +{firstGrown}, front x10 +{frontFrame10}, x20 +{frontFrame20}, max x {maxGrownX}");
        Assert.InRange(firstSeed, 0, 3);
        // The first crystals warn for ~1 s (CrystalWave.Root.FramesToGrown = 60).
        Assert.InRange(firstGrown - firstSeed, CrystalWave.Root.FramesToGrown - 1, CrystalWave.Root.FramesToGrown + 1);
        // …then the front covers 10 cells in ≈ 10 × FollowFramesPerCell frames.
        Assert.True(frontFrame20 > 0, "the front should reach column 20");
        Assert.InRange(frontFrame20 - frontFrame10, 10 * CrystalWave.FollowFramesPerCell - 6, 10 * CrystalWave.FollowFramesPerCell + 6);
    }

    [Fact]
    public void Wave_VanishesLeavingNoTerrain()
    {
        var sim = QuietSim();
        int before = OccupiedAboveFloor(sim.Chunks);
        ulong hashBefore = sim.Chunks.TerrainHash;
        LaunchWave(sim, 8);
        int peak = 0;
        for (int f = 0; f < 420; f++)
        {
            Step(sim, 1);
            peak = Math.Max(peak, Visible(sim.Chunks));
            Assert.Equal(before, OccupiedAboveFloor(sim.Chunks));   // never terrain, at any frame
            Assert.Empty(sim.Chunks.ActiveSprouts);
            Assert.Empty(sim.Chunks.PendingSprouts);
        }
        output.WriteLine($"peak visible crystals {peak}; left {sim.Chunks.Crystals.Count}");
        Assert.True(peak > 20, $"a big wave ({peak})");
        Assert.Equal(0, Visible(sim.Chunks));
        Assert.Equal(0, sim.Chunks.Crystals.Count);       // residue pruned too
        Assert.Equal(hashBefore, sim.Chunks.TerrainHash);
    }

    [Fact]
    public void SeedAndFlash_Harmless_GrownDamages_CrystalBreaks_CapHolds()
    {
        // P2 stands on the wave's path; P1 launches.
        var sim = NewSim(Arena(), Standing(4), Standing(15));
        var p2 = sim.SecondaryPlayers[0].Player;
        LaunchWave(sim, 8, mass: 160f);
        float hp0 = p2.Health;
        int now0 = sim.Chunks.TerrainFrame;
        int firstHurt = -1, hits = 0;
        float prev = hp0;
        for (int f = 0; f < 300; f++)
        {
            int brokenBefore = sim.Chunks.Crystals.Entries.Count(e => e.Value.Broken);
            Step(sim, 1);
            if (p2.Health < prev - 1e-4f)
            {
                hits++;
                if (firstHurt < 0) firstHurt = sim.Chunks.TerrainFrame - now0;
                // Whoever hurt P2 was a GROWN crystal, and it broke.
                int brokenAfter = sim.Chunks.Crystals.Entries.Count(e => e.Value.Broken);
                Assert.True(brokenAfter > brokenBefore, "the crystal that lands breaks");
            }
            prev = p2.Health;
        }
        output.WriteLine($"P2 hp {hp0} -> {p2.Health}, hits {hits}, first hurt +{firstHurt}");
        Assert.True(hits >= 1, "the wave should hit a body standing in its path");
        // Nothing during the root warning.
        Assert.True(firstHurt >= CrystalWave.Root.FramesToGrown, $"hurt at +{firstHurt}, before anything grew");
        // Cap per wave per victim.
        Assert.True(hp0 - p2.Health <= CrystalWave.WaveDamageCap + 1e-3f, $"took {hp0 - p2.Health}");
        // The launcher is immune.
        Assert.Equal(sim.Player.MaxHealth, sim.Player.Health);
    }

    [Fact]
    public void Launcher_IsImmune_StandingInItsOwnWave()
    {
        var sim = NewSim(Arena(), Standing(12), Standing(Cols - 4));
        LaunchWave(sim, 8, mass: 120f);
        Step(sim, 240);
        Assert.Equal(sim.Player.MaxHealth, sim.Player.Health);
    }

    [Fact]
    public void FormingCrystals_PushBodyOut_WithoutDamage()
    {
        var chunks = Arena();
        var sim = NewSim(chunks, Standing(4), Standing(30));
        var p2 = sim.SecondaryPlayers[0].Player;
        // Hand-place a root right under P2, as a wave of P1's would.
        int gx = (int)MathF.Floor(p2.Body.Position.X / Ts);
        var wave = new EntityId(9999);
        chunks.Crystals.TouchWave(wave, new CrystalWaveField.Side(Faction.Player1, Teams.Player), chunks.TerrainFrame);
        Assert.True(chunks.TryRequestCrystal(gx, FloorRow - 1, wave));
        var life = CrystalWave.Root;
        float y0 = p2.Body.Position.Y, minY = y0, minVy = 0f;
        for (int f = 0; f < life.FramesToGrown - 1; f++)
        {
            chunks.Crystals.TouchWave(wave, new CrystalWaveField.Side(Faction.Player1, Teams.Player), chunks.TerrainFrame);
            sim.Step(default, default);
            minY = MathF.Min(minY, p2.Body.Position.Y);
            minVy = MathF.Min(minVy, p2.Body.Velocity.Y);
            Assert.Equal(p2.MaxHealth, p2.Health);       // seed + flash never damage
        }
        output.WriteLine($"P2 y {y0} -> min {minY}, min vy {minVy}");
        Assert.True(minVy <= -CrystalWave.PushSpeed * 0.5f || minY < y0 - 3f, "the forming shard shoves the body up");
    }

    [Fact]
    public void WallStopsTheWave_NothingBeyond()
    {
        // A 5-tall barricade at columns 20-21.
        var sim = QuietSim(Arena(20, 22, FloorRow - 5));
        var ball = LaunchWave(sim, 8, mass: 160f);
        int maxX = int.MinValue;
        for (int f = 0; f < 300; f++)
        {
            Step(sim, 1);
            foreach (var (cell, _) in sim.Chunks.Crystals.Entries) maxX = Math.Max(maxX, cell.gtx);
        }
        output.WriteLine($"stopped {ball.StoppedAtWall}, max crystal x {maxX}");
        Assert.True(ball.StoppedAtWall);
        Assert.True(maxX < 20, $"crystal at x={maxX} past the wall face");
    }

    [Fact]
    public void WallRaisedAcrossPendingWave_CutsIt()
    {
        var sim = QuietSim();
        LaunchWave(sim, 8, mass: 160f);
        Step(sim, 40);   // ball done or nearly; root still warning
        // A 4-tall stone column at x=16 (the build path's request evicts crystals there).
        for (int y = FloorRow - 1; y >= FloorRow - 4; y--)
            Assert.NotNull(sim.Chunks.TryRequestTile(16, y, TileType.Stone, source: SproutSource.Place) ??
                           (sim.Chunks.Graph.TryGet(16, y, out var n) ? n : null));
        int maxGrownX = int.MinValue;
        for (int f = 0; f < 300; f++)
        {
            Step(sim, 1);
            foreach (var (cell, c) in sim.Chunks.Crystals.Entries)
                if (!c.Pending && c.PhaseAt(sim.Chunks.TerrainFrame) >= CrystalPhase.Grown)
                    maxGrownX = Math.Max(maxGrownX, cell.gtx);
        }
        output.WriteLine($"max grown x {maxGrownX}");
        Assert.True(maxGrownX < 16, $"grown at x={maxGrownX} past the wall");
    }

    [Fact]
    public void SnapshotMidWave_RestoreReplaysIdentically()
    {
        var sim = NewSim(Arena(), Standing(4), Standing(22));
        LaunchWave(sim, 8, mass: 160f);
        Step(sim, 70);   // root grown, front running
        var snap = sim.Snapshot();
        var sums = new List<ulong>();
        for (int f = 0; f < 90; f++) { sim.Step(default, default); sums.Add(sim.Checksum()); }
        float hp = sim.SecondaryPlayers[0].Player.Health;
        sim.Restore(snap);
        for (int f = 0; f < 90; f++)
        {
            sim.Step(default, default);
            Assert.Equal(sums[f], sim.Checksum());
        }
        Assert.Equal(hp, sim.SecondaryPlayers[0].Player.Health);
    }

    // ── Input: the spikes-selected RMB gesture ────────────────────────────────────

    private static PlayerInput In(Vector2 cursor, bool rmb, bool shift = false)
        => new() { RightClick = rmb, Shift = shift, MouseWorldPosition = cursor };

    private static Simulation InputSim()
    {
        var sim = new Simulation(Arena(), Standing(10));
        for (int f = 0; f < 30; f++) sim.Step(default);
        sim.Step(new PlayerInput { Num5 = true });
        Assert.True(sim.Player.SpikesSelected);
        return sim;
    }

    private static int CrystalBalls(Simulation sim)
        => sim.Entities.Count(e => e is MassBall { Crystal: true });

    // Plain RMB with spikes selected is the painter's gesture (2026-11-06): a press IN
    // solid is a charge from its first frame, never a spike — a tap banks a few units and
    // retires them, spiking nothing. (Deliberate single spikes are Shift+RMB.)
    [Fact]
    public void TapInSolid_IsAChargePress_NoSpike_NoWave()
    {
        var sim = InputSim();
        var floor = CellCenter(12, FloorRow);
        for (int f = 0; f < 4; f++) sim.Step(In(floor, true));
        Assert.Equal(nameof(BlockPaintAction), sim.Player.CurrentActionName);
        Assert.True(sim.Player.CurrentActionVars.ChargeGesture);
        sim.Step(In(floor, false));
        Assert.Equal(0, SpikeTiles.Count(sim.Chunks));
        Assert.Equal(0, CrystalBalls(sim));
        Assert.True(sim.Player.Abilities.Meters.CrystalWaveReady);
    }

    // A charge no longer depends on the cursor sitting still on its press cell: a press 1 px
    // inside a cell edge that drifts 2 px across it (a hand settling) still charges. This
    // was the "can't charge the wave the second time" bug.
    [Fact]
    public void ChargePress_SurvivesAHandDriftAcrossACellEdge()
    {
        var sim = InputSim();
        var p = new Vector2(12 * Ts + 1f, FloorRow * Ts + Ts * 0.5f);
        for (int f = 0; f < 60; f++)
        {
            if (f == 3) p.X -= 2f;
            sim.Step(In(p, true));
        }
        Assert.True(sim.Player.Abilities.Meters.EruptMove > 60f, $"erupt {sim.Player.Abilities.Meters.EruptMove}");
        Assert.Equal(0, sim.Chunks.SpikeSeeds.Count);
    }

    // A stroke — a press in open air — paints spike MASS: it seeks the nearest surface
    // through TileMassField, so skimming the air row over the floor lays spike tiles on
    // the floor along the way. The ball lags the cursor (SmoothTime), so each cell gets a
    // few frames and the hold lingers at the end for the ball to catch up.
    [Fact]
    public void Stroke_FromAir_SpikesTheSurfaceItSkims()
    {
        var sim = InputSim();
        sim.Step(In(CellCenter(11, FloorRow - 1), true));            // press in the air row
        Assert.False(sim.Player.CurrentActionVars.ChargeGesture);
        for (int i = 0; i < 4; i++)
            for (int f = 0; f < 10; f++) sim.Step(In(CellCenter(11 + i, FloorRow - 1), true));
        for (int f = 0; f < 20; f++) sim.Step(In(CellCenter(14, FloorRow - 1), true));
        sim.Step(In(CellCenter(14, FloorRow - 1), false));
        var spikes = SpikeTiles.All(sim.Chunks);
        output.WriteLine($"spikes: {string.Join(" ", spikes)}");
        int onFloor = spikes.FindAll(c => c.gty == FloorRow - 1 && c.gtx >= 10 && c.gtx <= 15).Count;
        Assert.True(onFloor >= 3, $"only {onFloor} spikes along the stroke");
        Assert.All(spikes, c => Assert.Equal(TileState.Solid, sim.Chunks.GetCellState(c.gtx, c.gty + 1)));
        Assert.Equal(0f, sim.Player.Abilities.Meters.EruptMove);     // a stroke never charges
        Assert.Equal(0, CrystalBalls(sim));
    }

    // Holding still a couple of cells OFF the surface still reaches it: the mass pools,
    // forwards toward support, and the patch of spikes grows with the hold.
    [Fact]
    public void HoldAboveTheFloor_SpikesSpreadOverTheSurfaceBelow()
    {
        var sim = InputSim();
        var p = CellCenter(13, FloorRow - 3);
        int Count() => SpikeTiles.All(sim.Chunks).FindAll(c => c.gty == FloorRow - 1).Count;
        for (int f = 0; f < 20; f++) sim.Step(In(p, true));
        int early = Count();
        for (int f = 0; f < 60; f++) sim.Step(In(p, true));
        int late = Count();
        output.WriteLine($"floor spikes after 20f: {early}, after 80f: {late}");
        Assert.True(late >= 2, $"only {late} spikes under a held cursor");
        Assert.True(late > early, "a longer hold should spread more spikes");
    }

    [Fact]
    public void ShiftRmb_PlacesOneSpike_OnTheOpenCellBesideSolid()
    {
        var sim = InputSim();
        var spot = CellCenter(12, FloorRow - 1);
        sim.Step(In(spot, true, shift: true));
        Assert.Equal(TileState.Spike, sim.Chunks.GetCellState(12, FloorRow - 1));
        Assert.True(sim.Chunks.SpikeSeeds.Has(12, FloorRow - 1));
        Assert.Equal(nameof(BlockPlaceAction), sim.Player.CurrentActionName);
        for (int f = 0; f < 40; f++) sim.Step(In(spot, true, shift: true));   // a long hold never charges
        Assert.Equal(0f, sim.Player.Abilities.Meters.EruptMove);
        Assert.Equal(1, SpikeTiles.Count(sim.Chunks));
    }

    // Charge in the floor, pull the cursor out along the surface and release fast.
    private static void ChargeAndFling(Simulation sim, int chargeFrames = 50)
    {
        var p = CellCenter(12, FloorRow);
        for (int f = 0; f < chargeFrames; f++) sim.Step(In(p, true));
        var cur = new Vector2(p.X, (FloorRow - 1) * Ts + Ts * 0.5f);
        for (int f = 0; f < 10; f++) { cur.X += 12f; sim.Step(In(cur, true)); }
        sim.Step(In(cur, false));
    }

    [Fact]
    public void ChargedFastRelease_LaunchesWave_NoSpikes_AndASecondRightAway()
    {
        var sim = InputSim();
        ChargeAndFling(sim);
        Assert.Equal(1, CrystalBalls(sim));
        Assert.Equal(0, sim.Chunks.SpikeSeeds.Count);   // the charge never spiked its cell
        var m = sim.Player.Abilities.Meters;
        // No cooldown (2026-11-05): the wave is kept in check by the meter and the telegraph.
        Assert.True(m.CrystalWaveReady);

        for (int f = 0; f < 200; f++) sim.Step(default);   // first ball is long gone
        Assert.Equal(0, CrystalBalls(sim));
        // Same gesture again launches another wave straight away.
        ChargeAndFling(sim);
        Assert.Equal(1, CrystalBalls(sim));

        for (int f = 0; f < 200; f++) sim.Step(default);
        Assert.Equal(0, CrystalBalls(sim));
        Assert.True(m.CrystalWaveReady);
        var p = CellCenter(15, FloorRow);
        for (int f = 0; f < 50; f++) sim.Step(In(p, true));
        var cur = new Vector2(p.X, (FloorRow - 1) * Ts + Ts * 0.5f);
        for (int f = 0; f < 10; f++) { cur.X += 12f; sim.Step(In(cur, true)); }
        sim.Step(In(cur, false));
        Assert.Equal(1, CrystalBalls(sim));
    }
}
