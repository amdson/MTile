using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;

namespace MTile.Tests.Sim;

// Shared test-only infrastructure for AVALANCHE_RIDING_V2 Part 1 (the harness).
// See Plans/AVALANCHE_RIDING_V2.md. Two consumers: AvalancheRideTests (the
// angle-sweep ride harness) and AvalancheOrderingTests (the back-ignition
// repro + shape-invariance scaffold).

// Walks a ray from an origin at angle theta with MassBall-matched speed decay
// and drag, depositing into TileMassField each frame — exactly what
// MassBall.ProjectileUpdate does (Entities/MassBall.cs), minus the entity, so
// the angle sweep is a pure function of (theta, speed, budget). Deposits carry
// a minted WaveId, matching the real ball's provenance tagging (V2 Part 2).
public sealed class ScriptedWave
{
    // Mirrors Entities/MassBall.cs's constants exactly.
    private const float Drag      = 0.3f;
    private const float LeakFraction = 6.0f;
    private const float LeakFloor    = 60.0f;
    private const float DoneMass     = 0.01f;

    // Minted per-wave, far above any player/entity index a harness test uses.
    private static int _nextWaveIndex = 1000;

    public Vector2  Position;
    public Vector2  Velocity;
    public float    Mass;
    public readonly TileType TileType;
    public readonly EntityId Wave = new(_nextWaveIndex++);

    public bool Done => Mass <= DoneMass;

    // angleRadians measured from horizontal; Y-DOWN convention, so an
    // upward-right wave (angle in (0, 90) degrees) gets a NEGATIVE Y velocity.
    public ScriptedWave(Vector2 origin, float angleRadians, float speed, float mass,
                        TileType tileType = TileType.Dirt)
    {
        Position = origin;
        Velocity = new Vector2(MathF.Cos(angleRadians), -MathF.Sin(angleRadians)) * speed;
        Mass     = mass;
        TileType = tileType;
    }

    // One frame: drag the velocity, leak+deposit at the CURRENT cell (matching
    // ProjectileUpdate's order — deposit happens before the physics step moves
    // the body), then advance the position. Returns the leaked amount.
    public float Step(ChunkMap terrain, float dt)
    {
        if (Done) return 0f;

        Velocity *= MathF.Max(0f, 1f - Drag * dt);

        float leak = MathF.Min(Mass, (Mass * LeakFraction + LeakFloor) * dt);
        Mass -= leak;

        int gtx = (int)MathF.Floor(Position.X / Chunk.TileSize);
        int gty = (int)MathF.Floor(Position.Y / Chunk.TileSize);
        terrain.TouchWave(Wave, Velocity);
        terrain.Mass.Deposit(terrain, gtx, gty, leak, TileType, Wave);

        Position += Velocity * dt;
        return leak;
    }
}

// One sampled frame of a scripted-wave ride: the player's post-physics state
// plus the wave's own "front" (max along-wave-axis projection over every
// growing volume center, across the WHOLE terrain — fine, since these tests
// use a fresh terrain with nothing but the one wave in it).
public record AvalancheFrame(
    int     Frame,
    Vector2 Pos,
    Vector2 Vel,
    Vector2 Force,
    string  State,
    float?  FrontProj,   // null when no Growing volume exists yet
    bool    WaveActive   // wave had mass left to leak going into this frame
);

// The metric table from AVALANCHE_RIDING_V2 Part 1. Diagnostic-first: report
// everything, pin nothing numeric yet (see the doc's "Two-stage pinning").
public record RideMetrics(
    bool  CatchRate,
    float TransportRatio,      // NaN if never carried, or front never moved
    float FrameAlignmentDeg,   // NaN if no carried frame had speed above the floor
    bool  Dropout,
    int   DropoutFrame,        // -1 if no dropout
    int   ContinuityRuns,
    float MaxJerk,
    int   CarriedFrameCount)
{
    public override string ToString() =>
        $"catch={CatchRate,-5} transport={Fmt(TransportRatio),6} alignDeg={Fmt(FrameAlignmentDeg),6} " +
        $"dropout={Dropout,-5}@{DropoutFrame,4} continuity={ContinuityRuns,2} maxJerk={MaxJerk,7:F1} " +
        $"carriedFrames={CarriedFrameCount,4}";

    private static string Fmt(float v) => float.IsNaN(v) ? "NaN" : v.ToString("F2");
}

public static class AvalancheHarness
{
    public const float Dt = 1f / 60f;
    public static readonly Vector2 Gravity = new(0f, 600f);
    private static float Ts => Chunk.TileSize;

    // Contact-rest spawn offset (see StandingJitterTests.RestOffset / SproutLiftJumpTests):
    // float height (R) plus the hexagon's bottom extent.
    public static readonly float RestOffset =
        PlayerCharacter.Radius * (1f + MathF.Sin(MathF.PI / 3f));

    public static ChunkMap BuildFlatFloor(int cols, int rows, int floorRow)
    {
        var sb = new StringBuilder();
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++) sb.Append(r >= floorRow ? 'X' : 'O');
            sb.Append('\n');
        }
        return SimTerrain.FromAscii(sb.ToString());
    }

    public static PlayerCharacter SpawnStandingAt(int col, int floorRow)
        => new(new Vector2(col * Ts + Ts * 0.5f, floorRow * Ts - RestOffset));

    // Manual frame loop, styled on SproutLiftJumpTests.Run: TickSprouts, Impact.Tick,
    // player.Update, then the wave's own "entity update" slot (matching where
    // SimRunner.RunMulti updates entities — after players, before the physics step),
    // then StepSwept. `waveDir` is the wave's constant direction (never turns).
    public static List<AvalancheFrame> RunScriptedRide(
        ChunkMap terrain, PlayerCharacter player, ScriptedWave wave, Vector2 waveDir,
        int frames, Func<int, PlayerInput> input = null)
    {
        waveDir = Vector2.Normalize(waveDir);
        var bodies = new List<PhysicsBody> { player.Body };
        var ctrl = new Controller();
        var samples = new List<AvalancheFrame>(frames);

        for (int f = 0; f < frames; f++)
        {
            ctrl.InjectInput(input?.Invoke(f) ?? new PlayerInput());
            terrain.TickSprouts(Dt);
            terrain.Impact.Tick(Dt);

            player.Update(ctrl, terrain, new HitboxWorld(), new HurtboxWorld(), Dt);
            var force = player.Body.AppliedForce;   // capture before StepSwept zeroes it

            bool waveActive = !wave.Done;
            if (waveActive) wave.Step(terrain, Dt);

            PhysicsWorld.StepSwept(bodies, terrain, Dt, Gravity);

            samples.Add(new AvalancheFrame(f, player.Body.Position, player.Body.Velocity, force,
                                           player.CurrentStateName, FrontProjection(terrain, waveDir),
                                           waveActive));
        }
        return samples;
    }

    // Max along-wave-axis projection over every growing volume center.
    public static float? FrontProjection(ChunkMap terrain, Vector2 waveDir)
    {
        float? front = null;
        var growing = terrain.Graph.Growing;
        for (int i = 0; i < growing.Count; i++)
        {
            var sp = growing[i];
            foreach (var face in TileSproutNode.FaceOrder)
            {
                if ((sp.Faces & face) == 0) continue;
                float proj = Vector2.Dot(sp.VolumeCenter(face), waveDir);
                if (front == null || proj > front.Value) front = proj;
            }
        }
        return front;
    }

    // The Part-1 metric table (see the doc). dropoutTiles/speedFloor match the
    // doc's "a couple of tiles" / "frames with speed above a floor" language.
    public static RideMetrics ComputeMetrics(IReadOnlyList<AvalancheFrame> samples, Vector2 waveDir,
                                             float dropoutTiles = 2f, float speedFloor = 5f)
    {
        waveDir = Vector2.Normalize(waveDir);
        bool anyCarried = false;
        int continuityRuns = 0;
        bool wasCarried = false;
        int firstCarried = -1, lastCarried = -1;
        float maxJerk = 0f;
        Vector2 prevVel = default;
        bool havePrevVel = false;
        float alignSum = 0f;
        int alignCount = 0;
        bool dropout = false;
        int dropoutFrame = -1;
        int carriedCount = 0;

        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            bool carried = s.State == "TerrainCarriedState";
            if (carried)
            {
                anyCarried = true;
                carriedCount++;
                if (firstCarried < 0) firstCarried = i;
                lastCarried = i;

                float speed = s.Vel.Length();
                if (speed > speedFloor)
                {
                    var vn = s.Vel / speed;
                    float dot = Math.Clamp(Vector2.Dot(vn, waveDir), -1f, 1f);
                    alignSum += MathF.Acos(dot) * (180f / MathF.PI);
                    alignCount++;
                }

                if (havePrevVel)
                    maxJerk = MathF.Max(maxJerk, (s.Vel - prevVel).Length());

                if (s.WaveActive && s.FrontProj is float fp)
                {
                    float playerProj = Vector2.Dot(s.Pos, waveDir);
                    if (fp - playerProj > dropoutTiles * Ts && !dropout)
                    { dropout = true; dropoutFrame = i; }
                }
            }
            if (carried && !wasCarried) continuityRuns++;
            wasCarried = carried;
            prevVel = s.Vel;
            havePrevVel = true;
        }

        // A carried run that ends while the wave is still actively depositing is
        // also a dropout (the doc's "carried run ends before the wave does").
        if (lastCarried >= 0 && lastCarried < samples.Count - 1 && samples[lastCarried].WaveActive && !dropout)
        { dropout = true; dropoutFrame = lastCarried; }

        // Transport window: the carried span CLIPPED to frames where a front
        // actually exists — the grace tail outlives the last growing volume, so
        // the raw last-carried frame often has no front sample to diff against.
        float transport = float.NaN;
        if (firstCarried >= 0 && lastCarried > firstCarried)
        {
            int a = firstCarried, b = lastCarried;
            while (a <= b && samples[a].FrontProj == null) a++;
            while (b >= a && samples[b].FrontProj == null) b--;
            if (b > a && samples[a].FrontProj is float f0 && samples[b].FrontProj is float f1)
            {
                float playerDisp = Vector2.Dot(samples[b].Pos - samples[a].Pos, waveDir);
                float frontDisp = f1 - f0;
                if (MathF.Abs(frontDisp) > 1e-3f) transport = playerDisp / frontDisp;
            }
        }

        float alignment = alignCount > 0 ? alignSum / alignCount : float.NaN;

        return new RideMetrics(anyCarried, transport, alignment, dropout, dropoutFrame,
                               continuityRuns, maxJerk, carriedCount);
    }

    // Every solid cell of `type` in the given cell-index box — the "committed
    // cell set" for shape-invariance comparisons (Ordering tests). Using TYPE
    // rather than diffing before/after keeps this independent of any
    // pre-existing floor (which stamps TileType.Stone by default).
    public static HashSet<(int, int)> CommittedCells(ChunkMap terrain, TileType type,
                                                      int x0, int x1, int y0, int y1)
    {
        var set = new HashSet<(int, int)>();
        for (int gtx = x0; gtx <= x1; gtx++)
        for (int gty = y0; gty <= y1; gty++)
            if (terrain.GetCellState(gtx, gty) == TileState.Solid && terrain.GetCellType(gtx, gty) == type)
                set.Add((gtx, gty));
        return set;
    }
}
