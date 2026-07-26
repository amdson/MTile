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

        var body = new PhysicsBody(Polygon.CreateRegular(PlayerCharacter.Radius, 6), start) { Velocity = vel };
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

        var body = new PhysicsBody(Polygon.CreateRegular(PlayerCharacter.Radius, 6), start) { Velocity = vel };
        var samples = Predict(body, FlatFloor(), dirX: 0, down: false, startGrounded: false, H);

        AssertParity(frames, samples, from: 0, count: H);
    }

    [Fact]
    public void GroundRun_SteadyState_MatchesSimRunner()
    {
        // Let the live sim settle into a steady hover-run, then predict onward
        // from a mid-trajectory state and compare against the continued rollout.
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

        var mid = frames[Settle - 1];
        var body = new PhysicsBody(Polygon.CreateRegular(PlayerCharacter.Radius, 6),
                                   new Vector2(mid.X, mid.Y))
                   { Velocity = new Vector2(mid.Vx, mid.Vy) };
        var samples = Predict(body, FlatFloor(), dirX: 1, down: false, startGrounded: true, H);

        AssertParity(frames, samples, from: Settle, count: H);
    }

    [Fact]
    public void Landing_FallOntoFlat_HoldRight_MatchesThroughTouchdown()
    {
        // Start above rest height falling at a speed under MaxGroundEngageVnRel:
        // the live sim catches via the standing FSD (spring + plane sweep), which
        // is exactly the landing path the predictor models.
        var start = new Vector2(200f, RestY - 40f);
        var vel   = new Vector2(40f, 150f);
        const int H = 24;   // = BallisticPredictor.MaxHorizon; touchdown lands well inside it

        var frames = SimRunner.Run(new SimConfig
        {
            Terrain = FlatFloor(), StartPosition = start, StartVelocity = vel,
            Script = InputScript.Always(new PlayerInput { Right = true }),
            Frames = H, Dt = Dt, Gravity = Gravity,
        });
        Assert.Contains(frames[..H], f => f.State == "StandingState");   // touchdown happened

        var body = new PhysicsBody(Polygon.CreateRegular(PlayerCharacter.Radius, 6), start) { Velocity = vel };
        var samples = Predict(body, FlatFloor(), dirX: 1, down: false, startGrounded: false, H);

        AssertParity(frames, samples, from: 0, count: H);
    }

    [Fact]
    public void GroundRun_ApproachingCap_MirrorsOverCapEquilibrium()
    {
        // StandingState's over-cap brake subtracts only excess/dt, so ground
        // steady state sits at MaxWalkSpeed + WalkAccel·dt (the same historical
        // formulation AirControl's comment describes fixing for air). The
        // predictor must mirror the live behavior, quirk included — this pins
        // both the equilibrium value and sample-for-sample parity around it.
        const int Settle = 90;
        var frames = SimRunner.Run(new SimConfig
        {
            Terrain = FlatFloor(),
            StartPosition = new Vector2(64f, RestY),
            Script = InputScript.Always(new PlayerInput { Right = true }),
            Frames = Settle, Dt = Dt, Gravity = Gravity,
        });
        var cfgNow = MovementConfig.Current;
        float equilibrium = cfgNow.MaxWalkSpeed + cfgNow.WalkAccel * Dt;
        Assert.True(MathF.Abs(frames[^1].Vx - equilibrium) < 1f,
            $"live vx {frames[^1].Vx} vs cap+accel·dt {equilibrium}");

        const int H = 20;
        var mid = frames[Settle - H - 1];
        var body = new PhysicsBody(Polygon.CreateRegular(PlayerCharacter.Radius, 6),
                                   new Vector2(mid.X, mid.Y))
                   { Velocity = new Vector2(mid.Vx, mid.Vy) };
        var samples = Predict(body, FlatFloor(), dirX: 1, down: false, startGrounded: true, H);

        AssertParity(frames, samples, from: Settle - H, count: H);
    }
}
