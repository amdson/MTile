using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// AVALANCHE_RIDING_V2 Part 1 — the angle-sweep ride harness. See
// Plans/AVALANCHE_RIDING_V2.md. This lands the metric table (diagnostic
// output) with only STRUCTURAL assertions — the wave commits tiles, the sim
// never NaNs/tunnels the player, metrics compute without throwing. Numeric
// thresholds on transport ratio / alignment / continuity are pinned only
// after the feel pass (Parts 2-4), per the doc's "Two-stage pinning".
public class AvalancheRideTests(ITestOutputHelper output)
{
    private static float Ts => Chunk.TileSize;
    private const int FloorRow = 60;
    private const int PlayerCol = 60;
    private const int Cols = 140;
    private const int Rows = 90;
    private const int SweepFrames = 200;

    private static Vector2 OriginAt(int col, int floorRow)
        => new((col * Chunk.TileSize) + Chunk.TileSize * 0.5f,
              ((floorRow - 1) * Chunk.TileSize) + Chunk.TileSize * 0.5f);

    // θ ∈ {20,30,45,60,75,90} × speed {slow,fast} × budget {small,large}.
    public static IEnumerable<object[]> SweepCases()
    {
        float[] angles = { 20f, 30f, 45f, 60f, 75f, 90f };
        (string name, float speed)[] speeds = { ("slow", 10f * Chunk.TileSize), ("fast", 25f * Chunk.TileSize) };
        (string name, float mass)[] budgets = { ("small", 6f), ("large", 60f) };
        foreach (var a in angles)
        foreach (var sp in speeds)
        foreach (var b in budgets)
            yield return new object[] { a, sp.name, sp.speed, b.name, b.mass };
    }

    // ── The angle sweep: the test the mechanic is designed against ──────────
    [Theory]
    [MemberData(nameof(SweepCases))]
    public void AngleSweep_ReportsMetricTable(float angleDeg, string speedName, float speed,
                                              string budgetName, float budget)
    {
        var terrain = AvalancheHarness.BuildFlatFloor(Cols, Rows, FloorRow);
        var player = AvalancheHarness.SpawnStandingAt(PlayerCol, FloorRow);
        var origin = OriginAt(PlayerCol, FloorRow);
        float rad = angleDeg * MathF.PI / 180f;
        var wave = new ScriptedWave(origin, rad, speed, budget);
        var waveDir = wave.Velocity;

        var samples = AvalancheHarness.RunScriptedRide(terrain, player, wave, waveDir, SweepFrames);
        var metrics = AvalancheHarness.ComputeMetrics(samples, waveDir);

        output.WriteLine($"[{angleDeg,4:F0}deg {speedName,-4} {budgetName,-5}] {metrics}");

        // Structural facts only — see class comment.
        var committed = AvalancheHarness.CommittedCells(terrain, TileType.Dirt, 0, Cols - 1, 0, Rows - 1);
        Assert.True(committed.Count > 0, "the wave should commit at least one tile");

        foreach (var s in samples)
        {
            Assert.False(float.IsNaN(s.Pos.X) || float.IsNaN(s.Pos.Y), $"NaN position at frame {s.Frame}");
            Assert.False(float.IsNaN(s.Vel.X) || float.IsNaN(s.Vel.Y), $"NaN velocity at frame {s.Frame}");
            Assert.True(s.Pos.Y < (FloorRow + 2) * Ts,
                $"player tunneled through the floor at frame {s.Frame} (y={s.Pos.Y:F1})");
        }

        // A very generous structural cap — only to catch an outright teleport,
        // not to pin jerk quality (that's the doc's later feel pass).
        Assert.True(metrics.MaxJerk < 5000f, $"structural jerk cap blown: {metrics.MaxJerk:F1} px/s per frame");
    }

    // ── End-to-end: a real MassBall should ride comparably to the scripted
    //    wave (loose comparison, diagnostic output). ──────────────────────────
    [Fact]
    public void RealMassBall_RidesComparablyToScriptedWave()
    {
        const float angleDeg = 45f;
        float rad = angleDeg * MathF.PI / 180f;
        float speed = 25f * Ts;
        float budget = 60f;

        // Scripted half.
        var scriptedTerrain = AvalancheHarness.BuildFlatFloor(Cols, Rows, FloorRow);
        var scriptedPlayer = AvalancheHarness.SpawnStandingAt(PlayerCol, FloorRow);
        var origin = OriginAt(PlayerCol, FloorRow);
        var scriptedWave = new ScriptedWave(origin, rad, speed, budget);
        var waveDir = scriptedWave.Velocity;
        var scriptedSamples = AvalancheHarness.RunScriptedRide(scriptedTerrain, scriptedPlayer, scriptedWave, waveDir, SweepFrames);
        var scriptedMetrics = AvalancheHarness.ComputeMetrics(scriptedSamples, waveDir);

        // Real-entity half: a HeadlessEntityWorld carrying one live MassBall
        // (see SimRunner.HeadlessEntityWorld and SimRunner.RunMulti's entity slot).
        var ballTerrain = AvalancheHarness.BuildFlatFloor(Cols, Rows, FloorRow);
        var ballPlayer = AvalancheHarness.SpawnStandingAt(PlayerCol, FloorRow);
        var hitIds = new HitIdAllocator();
        var world = new HeadlessEntityWorld(ballTerrain, hitIds, firstIndex: 2);
        var ballVelocity = new Vector2(MathF.Cos(rad), -MathF.Sin(rad)) * speed;
        var ball = new MassBall(origin, ballVelocity, budget, TileType.Dirt, Faction.Player1);
        world.SpawnEntity(ball);

        var ballSamples = RunWithMassBall(ballTerrain, ballPlayer, world, waveDir, SweepFrames);
        var ballMetrics = AvalancheHarness.ComputeMetrics(ballSamples, waveDir);

        output.WriteLine($"scripted : {scriptedMetrics}");
        output.WriteLine($"massball : {ballMetrics}");

        // Structural only: both waves actually built something, and neither
        // produced NaN/tunneled the rider. No numeric parity assertion — the
        // doc calls this a loose comparison.
        var scriptedCommitted = AvalancheHarness.CommittedCells(scriptedTerrain, TileType.Dirt, 0, Cols - 1, 0, Rows - 1);
        var ballCommitted = AvalancheHarness.CommittedCells(ballTerrain, TileType.Dirt, 0, Cols - 1, 0, Rows - 1);
        output.WriteLine($"committed tiles: scripted {scriptedCommitted.Count}, massball {ballCommitted.Count}");
        Assert.True(scriptedCommitted.Count > 0, "scripted wave should commit tiles");
        Assert.True(ballCommitted.Count > 0, "real MassBall should commit tiles");

        foreach (var s in ballSamples)
        {
            Assert.False(float.IsNaN(s.Pos.X) || float.IsNaN(s.Pos.Y), $"NaN position at frame {s.Frame}");
            Assert.True(s.Pos.Y < (FloorRow + 2) * Ts, $"player tunneled through the floor at frame {s.Frame}");
        }
    }

    // Mirrors SimRunner.RunMulti's entity slot: e.Update, e.PreStep, include
    // e.Body in the swept bodies, world.SweepDead() — but for a single player
    // and one (or more) pre-spawned entities.
    private static List<AvalancheFrame> RunWithMassBall(ChunkMap terrain, PlayerCharacter player,
                                                        HeadlessEntityWorld world, Vector2 waveDir, int frames)
    {
        waveDir = Vector2.Normalize(waveDir);
        var ctrl = new Controller();
        var hitboxes = new HitboxWorld();
        var hurtboxes = new HurtboxWorld();
        var samples = new List<AvalancheFrame>(frames);
        var bodies = new List<PhysicsBody> { player.Body };

        for (int f = 0; f < frames; f++)
        {
            ctrl.InjectInput(new PlayerInput());
            terrain.TickSprouts(AvalancheHarness.Dt);
            terrain.Impact.Tick(AvalancheHarness.Dt);

            hitboxes.Clear();
            hurtboxes.Clear();
            player.PublishHurtboxes(hurtboxes);
            foreach (var e in world.Entities) e.PublishHurtboxes(hurtboxes);

            bool waveActive = world.Entities.Any(e => !e.IsDead);
            player.Update(ctrl, terrain, hitboxes, hurtboxes, AvalancheHarness.Dt, spawner: world);
            var force = player.Body.AppliedForce;

            var entityScratch = new List<Entity>(world.Entities);
            foreach (var e in entityScratch) e.Update(AvalancheHarness.Dt, player, hitboxes, world);

            foreach (var e in world.Entities) e.PreStep(AvalancheHarness.Gravity);
            var bodyScratch = new List<PhysicsBody>(bodies);
            foreach (var e in world.Entities) bodyScratch.Add(e.Body);
            PhysicsWorld.StepSwept(bodyScratch, terrain, AvalancheHarness.Dt, AvalancheHarness.Gravity);
            world.SweepDead();

            samples.Add(new AvalancheFrame(f, player.Body.Position, player.Body.Velocity, force,
                                           player.CurrentStateName, AvalancheHarness.FrontProjection(terrain, waveDir),
                                           waveActive));
        }
        return samples;
    }

    // ── Relative steering: hold-right during a ~60° ride ─────────────────────
    [Fact]
    public void RelativeSteering_HoldRight_ShiftsAlongWaveFrame()
    {
        const float angleDeg = 60f;
        float rad = angleDeg * MathF.PI / 180f;
        float speed = 25f * Ts;
        float budget = 60f;
        var origin = OriginAt(PlayerCol, FloorRow);

        List<AvalancheFrame> RunVariant(bool holdRight)
        {
            var terrain = AvalancheHarness.BuildFlatFloor(Cols, Rows, FloorRow);
            var player = AvalancheHarness.SpawnStandingAt(PlayerCol, FloorRow);
            var wave = new ScriptedWave(origin, rad, speed, budget);
            return AvalancheHarness.RunScriptedRide(terrain, player, wave, wave.Velocity, SweepFrames,
                holdRight ? (_ => new PlayerInput { Right = true }) : null);
        }

        var neutral = RunVariant(holdRight: false);
        var steered = RunVariant(holdRight: true);
        var waveDir = Vector2.Normalize(new Vector2(MathF.Cos(rad), -MathF.Sin(rad)));
        var across = new Vector2(-waveDir.Y, waveDir.X);   // perpendicular to the wave frame

        var neutralMetrics = AvalancheHarness.ComputeMetrics(neutral, waveDir);
        var steeredMetrics = AvalancheHarness.ComputeMetrics(steered, waveDir);
        output.WriteLine($"neutral : {neutralMetrics}");
        output.WriteLine($"steered : {steeredMetrics}");

        Vector2 dNeutral = neutral[^1].Pos - neutral[0].Pos;
        Vector2 dSteered = steered[^1].Pos - steered[0].Pos;
        output.WriteLine($"neutral displacement: along {Vector2.Dot(dNeutral, waveDir):F1}, across {Vector2.Dot(dNeutral, across):F1}");
        output.WriteLine($"steered displacement: along {Vector2.Dot(dSteered, waveDir):F1}, across {Vector2.Dot(dSteered, across):F1}");

        // Structural only: no NaNs, no thresholds pinned yet.
        Assert.False(float.IsNaN(dNeutral.X) || float.IsNaN(dSteered.X));
    }

    // ── Jump inheritance: flat-ground jump vs jump during a live ride ────────
    [Fact]
    public void JumpInheritance_FlatVsLiveRide_ReportsVelocity()
    {
        const float angleDeg = 45f;
        float rad = angleDeg * MathF.PI / 180f;
        float speed = 25f * Ts;
        float budget = 60f;
        var origin = OriginAt(PlayerCol, FloorRow);
        var waveDir = Vector2.Normalize(new Vector2(MathF.Cos(rad), -MathF.Sin(rad)));

        // Find a frame where an unforced run is actually carried.
        var probeTerrain = AvalancheHarness.BuildFlatFloor(Cols, Rows, FloorRow);
        var probePlayer = AvalancheHarness.SpawnStandingAt(PlayerCol, FloorRow);
        var probeWave = new ScriptedWave(origin, rad, speed, budget);
        var probeSamples = AvalancheHarness.RunScriptedRide(probeTerrain, probePlayer, probeWave, waveDir, SweepFrames);
        int jumpFrame = probeSamples.FindIndex(s => s.State == "TerrainCarriedState");
        output.WriteLine(jumpFrame < 0
            ? "no carried frame found in the probe run — reporting flat jump only"
            : $"probe found first carried frame at {jumpFrame}");

        List<AvalancheFrame> RunJump(bool withWave)
        {
            var terrain = AvalancheHarness.BuildFlatFloor(Cols, Rows, FloorRow);
            var player = AvalancheHarness.SpawnStandingAt(PlayerCol, FloorRow);
            int jf = jumpFrame >= 0 ? jumpFrame : 40;
            var wave = withWave ? new ScriptedWave(origin, rad, speed, budget) : null;
            return AvalancheHarness.RunScriptedRide(terrain, player, wave ?? new ScriptedWave(origin, rad, 0f, 0f),
                waveDir, SweepFrames, f => new PlayerInput { Space = f >= jf && f < jf + 6 });
        }

        var flat = RunJump(withWave: false);
        var ride = RunJump(withWave: true);
        int jFrame = jumpFrame >= 0 ? jumpFrame : 40;

        output.WriteLine("frame  flat.vy   flat.vx   ride.vy   ride.vx");
        for (int n = 0; n <= 8; n += 2)
        {
            int idx = jFrame + n;
            if (idx >= flat.Count || idx >= ride.Count) break;
            output.WriteLine($"+{n,2}    {flat[idx].Vel.Y,8:F1}  {flat[idx].Vel.X,8:F1}  {ride[idx].Vel.Y,8:F1}  {ride[idx].Vel.X,8:F1}");
        }

        // Structural only — Y-down: upward carrier velocity is negative, so
        // don't assert sign/magnitude relationships here (that's the pinned
        // pass); just confirm both runs stay finite.
        foreach (var s in flat) Assert.False(float.IsNaN(s.Vel.X) || float.IsNaN(s.Vel.Y));
        foreach (var s in ride) Assert.False(float.IsNaN(s.Vel.X) || float.IsNaN(s.Vel.Y));
    }

    // ── Jump mid-ride launches in the carrier's frame (mechanism test) ───────
    // The rider floats AnchorStandoff proud of the crest, so a jump's contact
    // probe often finds no moving source — the carried state's exit handoff
    // (MovementVars.JumpCarrySource) must supply the carrier frame instead.
    // Steep wave (75deg) for a strong upward carrier component; Y-down, so the
    // ride jump's launch vy must be MORE NEGATIVE than the flat jump's by a
    // real margin. Mechanism, not tuning: the margin is far below the carrier
    // speed the servo demonstrably tracks (see the sweep's 75deg rows).
    [Fact]
    public void JumpMidRide_LaunchesInCarrierFrame()
    {
        const float angleDeg = 75f;
        float rad = angleDeg * MathF.PI / 180f;
        float speed = 25f * Ts;
        float budget = 60f;
        var origin = OriginAt(PlayerCol, FloorRow);
        var waveDir = Vector2.Normalize(new Vector2(MathF.Cos(rad), -MathF.Sin(rad)));

        // Probe: find an established carried frame (a few frames into the ride,
        // once the servo tracks the crest rather than the entry transient).
        var probeTerrain = AvalancheHarness.BuildFlatFloor(Cols, Rows, FloorRow);
        var probePlayer = AvalancheHarness.SpawnStandingAt(PlayerCol, FloorRow);
        var probeWave = new ScriptedWave(origin, rad, speed, budget);
        var probeSamples = AvalancheHarness.RunScriptedRide(probeTerrain, probePlayer, probeWave, waveDir, SweepFrames);
        int firstCarried = probeSamples.FindIndex(s => s.State == "TerrainCarriedState");
        Assert.True(firstCarried >= 0, "probe run was never carried — can't test the mid-ride jump");
        int jf = firstCarried + 8;

        List<AvalancheFrame> RunJump(bool withWave)
        {
            var terrain = AvalancheHarness.BuildFlatFloor(Cols, Rows, FloorRow);
            var player = AvalancheHarness.SpawnStandingAt(PlayerCol, FloorRow);
            var wave = new ScriptedWave(origin, rad, withWave ? speed : 0f, withWave ? budget : 0f);
            return AvalancheHarness.RunScriptedRide(terrain, player, wave, waveDir, SweepFrames,
                f => new PlayerInput { Space = f >= jf && f < jf + 6 });
        }

        var flat = RunJump(withWave: false);
        var ride = RunJump(withWave: true);

        // The jump must actually fire out of the ride.
        int rideJumpFrame = ride.FindIndex(jf, s => s.State.Contains("Jump"));
        Assert.True(rideJumpFrame >= 0 && rideJumpFrame <= jf + 3,
            $"jump never fired out of the ride (pressed at {jf}, first jump state at {rideJumpFrame})");
        int flatJumpFrame = flat.FindIndex(jf, s => s.State.Contains("Jump"));
        Assert.True(flatJumpFrame >= 0, "flat-ground control jump never fired");

        // Launch vy: most-upward velocity within a few frames of the press.
        float LaunchVy(List<AvalancheFrame> t, int from)
        {
            float best = float.PositiveInfinity;
            for (int i = from; i < Math.Min(from + 5, t.Count); i++) best = MathF.Min(best, t[i].Vel.Y);
            return best;
        }
        float flatVy = LaunchVy(flat, flatJumpFrame);
        float rideVy = LaunchVy(ride, rideJumpFrame);
        output.WriteLine($"flat launch vy={flatVy:F1}   ride launch vy={rideVy:F1}   " +
                         $"(carrier vy at press: {ride[jf - 1].Vel.Y:F1})");
        for (int i = Math.Max(0, jf - 2); i <= Math.Min(ride.Count - 1, jf + 14); i++)
            output.WriteLine($"  ride f{i}: {ride[i].State,-22} vy={ride[i].Vel.Y,7:F1} fy={ride[i].Force.Y,8:F0}  |  flat: {flat[i].State,-14} vy={flat[i].Vel.Y,7:F1}");

        const float Margin = 25f;   // px/s — well under the ~100 px/s carrier vy at 75deg
        Assert.True(rideVy <= flatVy - Margin,
            $"mid-ride jump launched at vy={rideVy:F1} vs flat {flatVy:F1} — carrier momentum was not inherited");
    }

    // ── No proximity theft: a wave passing well clear of a standing player
    //    must never grab them (hard assertion — spirit invariant #5). ────────
    [Fact]
    public void NoProximityTheft_WavePassingBeside_NeverEntersCarried()
    {
        const float angleDeg = 75f;
        float rad = angleDeg * MathF.PI / 180f;
        float speed = 25f * Ts;
        float budget = 60f;
        const int OffsetTiles = 8;

        var terrain = AvalancheHarness.BuildFlatFloor(Cols, Rows, FloorRow);
        var player = AvalancheHarness.SpawnStandingAt(PlayerCol, FloorRow);
        var origin = OriginAt(PlayerCol + OffsetTiles, FloorRow);
        var wave = new ScriptedWave(origin, rad, speed, budget);
        var waveDir = wave.Velocity;

        var samples = AvalancheHarness.RunScriptedRide(terrain, player, wave, waveDir, SweepFrames);
        var metrics = AvalancheHarness.ComputeMetrics(samples, waveDir);
        output.WriteLine($"[{OffsetTiles} tiles beside] {metrics}");

        Assert.False(metrics.CatchRate,
            $"a wave {OffsetTiles} tiles beside a standing player entered TerrainCarriedState — proximity theft");
    }

    // ── End of wave: small budget dies mid-ride ───────────────────────────────
    [Fact]
    public void EndOfWave_SmallBudgetDiesMidRide_ReturnsToLocomotion()
    {
        const float angleDeg = 45f;
        float rad = angleDeg * MathF.PI / 180f;
        float speed = 25f * Ts;
        const float budget = 6f;   // small — dies fast

        var terrain = AvalancheHarness.BuildFlatFloor(Cols, Rows, FloorRow);
        var player = AvalancheHarness.SpawnStandingAt(PlayerCol, FloorRow);
        var origin = OriginAt(PlayerCol, FloorRow);
        var wave = new ScriptedWave(origin, rad, speed, budget);
        var waveDir = wave.Velocity;

        var samples = AvalancheHarness.RunScriptedRide(terrain, player, wave, waveDir, SweepFrames);
        var metrics = AvalancheHarness.ComputeMetrics(samples, waveDir);
        output.WriteLine($"{metrics}");

        string prevState = "";
        for (int i = 0; i < samples.Count; i += 5)
        {
            var s = samples[i];
            if (s.State != prevState || i % 20 == 0)
                output.WriteLine($"  f{s.Frame,3}  state {s.State,-20} force ({s.Force.X,7:F1},{s.Force.Y,7:F1})  wave-active {s.WaveActive}");
            prevState = s.State;
        }

        foreach (var s in samples)
            Assert.False(float.IsNaN(s.Pos.X) || float.IsNaN(s.Pos.Y), $"NaN position at frame {s.Frame}");

        string finalState = samples[^1].State;
        Assert.True(finalState is "StandingState" or "FallingState" or "CrouchedState",
            $"expected the ride to decay back into ordinary locomotion, ended in {finalState}");
    }
}
