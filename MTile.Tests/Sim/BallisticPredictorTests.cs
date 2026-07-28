using System;
using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests.Sim;

// Predictor-parity suite (BALLISTIC_CORRECTOR_PLAN build step 1): the coast the
// BallisticPredictor rolls out must match an ACTUAL SimRunner rollout sample-for-
// sample — same integrator order, same modifiers, same spring/probe semantics.
// These tests are the mirror contract for BaselineFeedforward: if a live state's
// baseline math changes without the mirror, they fail loudly.
//
// Scenarios stay inside the predictor's declared scope: static tiles, no steering
// ramps (flat terrain / free air), friction suppressed by a held direction, landing
// speeds under MaxGroundEngageVnRel. Everything outside that scope is the
// constraint builder's job, not the predictor's.
public class BallisticPredictorTests
{
    private const float Dt  = 1f / 60f;
    private const float Tol = 1e-3f;

    private static readonly Vector2 Gravity = new(0f, 600f);

    // Flat floor: tile row at y = 96px (tile row 6), 40 tiles wide starting at x = 0.
    private static ChunkMap FlatFloor() => SimTerrain.FromAscii(
        new string('X', 40), originTileX: 0, originTileY: 6);

    private static float FloorTop => 6 * Chunk.TileSize;                 // 96
    private static float RestY    => FloorTop - 2f * PlayerCharacter.Radius; // center rest height

    private static void AssertParity(SimFrame[] frames, CoastSample[] samples, int from, int count)
    {
        for (int k = 0; k < count; k++)
        {
            var f = frames[from + k];
            Assert.True(MathF.Abs(samples[k].Pos.X - f.X)  < Tol, $"k={k}: pos.X {samples[k].Pos.X} vs {f.X} (state {f.State})");
            Assert.True(MathF.Abs(samples[k].Pos.Y - f.Y)  < Tol, $"k={k}: pos.Y {samples[k].Pos.Y} vs {f.Y} (state {f.State})");
            Assert.True(MathF.Abs(samples[k].Vel.X - f.Vx) < Tol, $"k={k}: vel.X {samples[k].Vel.X} vs {f.Vx} (state {f.State})");
            Assert.True(MathF.Abs(samples[k].Vel.Y - f.Vy) < Tol, $"k={k}: vel.Y {samples[k].Vel.Y} vs {f.Vy} (state {f.State})");
        }
    }

    private static CoastSample[] Predict(PhysicsBody body, ChunkMap chunks, int dirX, bool down,
                                         bool startGrounded, int steps)
    {
        var samples = new CoastSample[BallisticPredictor.MaxHorizon];
        int written = BallisticPredictor.Predict(
            body, chunks, dirX, down, startGrounded,
            MovementModifiers.Identity, Gravity, Dt, steps, samples);
        Assert.Equal(steps, written);
        return samples;
    }

    [Fact]
    public void AirborneCoast_HoldRight_MatchesSimRunner()
    {
        var start = new Vector2(100f, -200f);   // far above the floor — pure flight
        var vel   = new Vector2(60f, -150f);
        const int H = 20;

        var frames = SimRunner.Run(new SimConfig
        {
            Terrain = FlatFloor(), StartPosition = start, StartVelocity = vel,
            Script = InputScript.Always(new PlayerInput { Right = true }),
            Frames = H, Dt = Dt, Gravity = Gravity,
        });
        Assert.All(frames, f => Assert.Equal("FallingState", f.State));

        var body = new PhysicsBody(PlayerCharacter.CreateBodyPolygon(), start) { Velocity = vel };
        var samples = Predict(body, FlatFloor(), dirX: 1, down: false, startGrounded: false, H);

        AssertParity(frames, samples, from: 0, count: H);
    }

    [Fact]
    public void AirborneCoast_NoInput_DragBrakesToMatch()
    {
        var start = new Vector2(100f, -200f);
        var vel   = new Vector2(90f, -50f);
        const int H = 15;

        var frames = SimRunner.Run(new SimConfig
        {
            Terrain = FlatFloor(), StartPosition = start, StartVelocity = vel,
            Script = InputScript.Always(default),
            Frames = H, Dt = Dt, Gravity = Gravity,
        });
        Assert.All(frames, f => Assert.Equal("FallingState", f.State));

        var body = new PhysicsBody(PlayerCharacter.CreateBodyPolygon(), start) { Velocity = vel };
        var samples = Predict(body, FlatFloor(), dirX: 0, down: false, startGrounded: false, H);

        AssertParity(frames, samples, from: 0, count: H);
    }

    // ── Fold-era ground contracts ────────────────────────────────────────────
    // Grounded locomotion is now coast + SOLVER CORRECTIONS (the stand fold), so
    // sample-for-sample parity of the correction-free coast against the live sim
    // is unobservable by construction on the ground — the airborne tests above
    // remain the strict mirror contract. What IS pinned here instead:
    //  - the live steady run holds the hover band and walks at the TRUE
    //    configured MaxWalkSpeed (the old over-cap equilibrium quirk is retired
    //    with the walk fold's one-sided progress rows);
    //  - the coast from a mid-run state stays NEAR the live rollout (the
    //    corrected system's rest point is the coast's own fixed point — small
    //    corrections, not a divergent baseline).
    [Fact]
    public void GroundRun_SteadyState_HoldsHoverBandAtWalkSpeed()
    {
        const int Settle = 60, H = 20;

        var frames = SimRunner.Run(new SimConfig
        {
            Terrain = FlatFloor(),
            StartPosition = new Vector2(64f, RestY),
            Script = InputScript.Always(new PlayerInput { Right = true }),
            Frames = Settle + H, Dt = Dt, Gravity = Gravity,
        });
        Assert.Equal("StandingState", frames[Settle - 1].State);
        Assert.Equal("StandingState", frames[Settle + H - 1].State);

        var cfg = MovementConfig.Current;
        for (int f = Settle; f < Settle + H; f++)
        {
            Assert.True(MathF.Abs(frames[f].Vx - cfg.MaxWalkSpeed) < 2f,
                $"f={f}: steady walk vx {frames[f].Vx:F2} vs configured {cfg.MaxWalkSpeed}");
            Assert.True(MathF.Abs(frames[f].Y - frames[Settle - 1].Y) < 2.5f,
                $"f={f}: hover drifted {frames[f].Y:F2} vs settled {frames[Settle - 1].Y:F2}");
        }

        // Coast-proximity: the correction-free coast from a settled state must
        // track the corrected live rollout closely (corrections are trims).
        var mid = frames[Settle - 1];
        var body = new PhysicsBody(PlayerCharacter.CreateBodyPolygon(), new Vector2(mid.X, mid.Y))
                   { Velocity = new Vector2(mid.Vx, mid.Vy) };
        var samples = Predict(body, FlatFloor(), dirX: 1, down: false, startGrounded: true, H);
        for (int k = 0; k < H; k++)
        {
            var f = frames[Settle + k];
            Assert.True(MathF.Abs(samples[k].Pos.Y - f.Y) < 4f,
                $"k={k}: coast y {samples[k].Pos.Y:F2} diverged from live {f.Y:F2}");
            Assert.True(MathF.Abs(samples[k].Vel.X - f.Vx) < 8f,
                $"k={k}: coast vx {samples[k].Vel.X:F2} diverged from live {f.Vx:F2}");
        }
    }

    [Fact]
    public void Landing_FallOntoFlat_HoldRight_SettlesToHover()
    {
        // Fold-era landing contract: a descent under MaxGroundEngageVnRel is the
        // solver's catch (LegServo + envelope rows), not an FSD sweep — so the
        // pin is the OUTCOME: touchdown happens, the body settles into the hover
        // band without ever penetrating the floor, and horizontal speed is not
        // eaten by the landing.
        var start = new Vector2(200f, RestY - 40f);
        var vel   = new Vector2(40f, 150f);
        const int H = 60;

        var frames = SimRunner.Run(new SimConfig
        {
            Terrain = FlatFloor(), StartPosition = start, StartVelocity = vel,
            Script = InputScript.Always(new PlayerInput { Right = true }),
            Frames = H, Dt = Dt, Gravity = Gravity,
        });
        Assert.Contains(frames, f => f.State == "StandingState");   // touchdown happened

        // Never below physical rest (floor top − body half-height), small slack.
        float floorRest = FloorTop - 10.39f;   // bottom-vertex rest height
        Assert.All(frames, f => Assert.True(f.Y < floorRest + 1.5f,
            $"body sank to y={f.Y:F2} (floor-contact rest ≈ {floorRest:F2})"));

        // Settled: last 15 frames inside the hover band, gently moving.
        for (int f = H - 15; f < H; f++)
        {
            Assert.True(MathF.Abs(frames[f].Y - (RestY + 1.6f)) < 3f,
                $"f={f}: not settled at hover, y={frames[f].Y:F2}");
            Assert.True(MathF.Abs(frames[f].Vy) < 20f,
                $"f={f}: still bobbing, vy={frames[f].Vy:F2}");
        }
        // The landing must not eat the run: horizontal speed stays near the walk.
        Assert.True(frames[^1].Vx > 30f, $"landing ate the run: vx={frames[^1].Vx:F2}");
    }

    [Fact]
    public void GroundRun_WalksAtConfiguredSpeed_QuirkRetired()
    {
        // The historical over-cap equilibrium (MaxWalkSpeed + WalkAccel·dt, from
        // StandingState's excess/dt brake) is deliberately RETIRED by the walk
        // fold: one-sided progress rows assist up to the target and no channel
        // brakes held momentum, so the sustained walk speed is exactly the
        // configured MaxWalkSpeed. This pins the quirk's absence.
        const int Settle = 90;
        var frames = SimRunner.Run(new SimConfig
        {
            Terrain = FlatFloor(),
            StartPosition = new Vector2(64f, RestY),
            Script = InputScript.Always(new PlayerInput { Right = true }),
            Frames = Settle, Dt = Dt, Gravity = Gravity,
        });
        var cfgNow = MovementConfig.Current;
        Assert.True(MathF.Abs(frames[^1].Vx - cfgNow.MaxWalkSpeed) < 1.5f,
            $"live vx {frames[^1].Vx:F2} vs configured MaxWalkSpeed {cfgNow.MaxWalkSpeed}");
        float quirk = cfgNow.MaxWalkSpeed + cfgNow.WalkAccel * Dt;
        Assert.True(frames[^1].Vx < quirk - 10f,
            $"over-cap quirk resurfaced: vx={frames[^1].Vx:F2} ≈ {quirk}");
    }
}
