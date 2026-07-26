using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Step 3 of Plans/TODO_TOP_BULLETS_PLAN.md — SteeringRamp.MaxRedirectVy.
// Without the cap, a steep redirect on a tall ledge converts inbound horizontal
// speed into a fast vertical kick that the existing |v| magnitude cap (MaxSpeed)
// still allows: a body running into a corner at 100 px/s with θ* ≈ π/3 emerges
// at vy ≈ -87 px/s; with a tall ledge that θ* approaches π/2 and vy can be the
// entire speed. MaxRedirectVy clamps the upward component independently.
//
// We drive SteeringRamp.ResolveVelocity directly with a constructed high-corner
// setup — reaching ParkourState from a scripted scenario is brittle and would
// hide whether the cap itself is doing its job.
public class RampVerticalCapTests(ITestOutputHelper output)
{
    // Build a ramp whose corner sits high enough that the binding vertex yields a
    // steep-but-in-regime SurfaceDir (θ* ≈ 55°, inside the ramp's authority band —
    // the steep-angle Weight taper zeroes anything past ~77°). Without the vy cap
    // a fast horizontal inbound velocity emerges with vy ≈ |v|·sin θ* ≈ 0.82·|v|.
    private static (PhysicsBody body, SteeringRamp ramp) BuildHighCornerRamp(float vyCap)
    {
        var body = new PhysicsBody(Polygon.CreateRegular(8f, 6), new Vector2(0f, 0f));
        // Forward = +X, body must pass OVER the corner. Body at origin, corner at
        // (45, -40): with the Clearance/OverVertLift offsets the binding vertex sees
        // θ* ≈ 55° — steep enough to exercise the vy cap, shallow enough that the
        // steep-angle taper leaves the ramp at full weight.
        var ramp = new SteeringRamp
        {
            Sense = SteeringSense.Over,
            ForwardDir = +1,
            Corner = new Vector2(45f, -40f),
            MaxRedirectVy = vyCap,
        };
        body.Constraints.Add(ramp);
        return (body, ramp);
    }

    [Fact]
    public void HighCorner_FastInbound_WithoutCap_VyExceedsLimit()
    {
        // Baseline / sanity: with no cap, the redirect produces a large upward vy.
        var (body, _) = BuildHighCornerRamp(vyCap: float.PositiveInfinity);
        body.Velocity = new Vector2(300f, 0f);

        SteeringRamp.ApplyRedirect(body, 1f / 30f);

        output.WriteLine($"no-cap result: vy = {body.Velocity.Y:F2}");
        // Sanity-check the fixture: redirect should have produced a strong upward kick.
        Assert.True(body.Velocity.Y < -100f,
            $"Fixture sanity: expected strong upward redirect with no cap, got vy={body.Velocity.Y:F2}");
    }

    // ── Steep-angle cutoff ─────────────────────────────────────────────────
    // A corner so high that the "shallowest clearing trajectory" is near-vertical
    // (θ* → π/2) is outside the ramp's physical regime — redirecting onto it is a
    // winch, not a graze assist. Past the binary steep cutoff (~72°) the ramp
    // disengages entirely: velocity passes through untouched and the body meets
    // the wall as a real collision. (This fixture — corner at (16, -200),
    // θ* ≈ 86° — was the original vy-cap fixture before the cutoff existed.)
    [Fact]
    public void NearVerticalCorner_RampAbstains_VelocityUntouched()
    {
        var body = new PhysicsBody(Polygon.CreateRegular(8f, 6), new Vector2(0f, 0f));
        var ramp = new SteeringRamp
        {
            Sense = SteeringSense.Over,
            ForwardDir = +1,
            Corner = new Vector2(16f, -200f),
        };
        body.Constraints.Add(ramp);
        body.Velocity = new Vector2(300f, 0f);
        Vector2 vBefore = body.Velocity;

        SteeringRamp.ApplyRedirect(body, 1f / 30f);

        ramp.Recompute(body.Polygon.GetVertices(body.Position));
        output.WriteLine($"θ* = {ramp.ThetaStar:F3} rad, Engaged = {ramp.Engaged}, v = ({body.Velocity.X:F2}, {body.Velocity.Y:F2})");
        Assert.False(ramp.Engaged,
            $"Expected the steep cutoff to disengage the ramp at θ*={ramp.ThetaStar:F3}");
        Assert.Equal(vBefore, body.Velocity);
    }

    [Theory]
    [InlineData(50f)]
    [InlineData(100f)]
    [InlineData(200f)]
    public void HighCorner_FastInbound_WithCap_VyStaysBelowLimit(float vyCap)
    {
        var (body, ramp) = BuildHighCornerRamp(vyCap);
        body.Velocity = new Vector2(300f, 0f);

        SteeringRamp.ApplyRedirect(body, 1f / 30f);

        output.WriteLine($"vyCap={vyCap}: result vy = {body.Velocity.Y:F2}, |v|={body.Velocity.Length():F2}");
        // Y-down: upward is negative. Cap clamps vy >= -vyCap.
        const float Epsilon = 0.01f;
        Assert.True(body.Velocity.Y >= -vyCap - Epsilon,
            $"vy={body.Velocity.Y} should be >= -{vyCap} (i.e. upward magnitude ≤ cap)");
        // Per-contact impulse should reflect the delta we actually applied.
        Assert.True(ramp.LastImpulse.Y > -vyCap - 300f - Epsilon,
            $"ramp.LastImpulse.Y={ramp.LastImpulse.Y} outside plausible range for vy cap {vyCap}");
    }

    // ── Layer 1: MaxForce clip ─────────────────────────────────────────────
    // Soft-constraint ramp behavior. The redirect's Δv is bounded to
    // MaxForce·dt per step, so an external velocity stronger than the ramp
    // gets clipped: most stays in body.Velocity (heading into the surface)
    // and a real impact resolves downstream. Default MaxForce=+∞ ⇒ unchanged
    // legacy behavior, which is what every other test in this file expects.

    [Fact]
    public void MaxForce_FiniteCap_ClipsRedirectDvToMaxForceTimesDt()
    {
        // Inbound velocity is huge (1000 px/s into the banned direction).
        // With no force cap, the ramp would zero the banned component and
        // emit something along SurfaceDir. With MaxForce·dt = 50 px/s the
        // Δv from vBefore must be ≤ 50 in magnitude.
        var (body, ramp) = BuildHighCornerRamp(vyCap: float.PositiveInfinity);
        ramp.MaxForce = 1500f;          // px/s²
        body.Velocity = new Vector2(1000f, 0f);
        Vector2 vBefore = body.Velocity;

        const float dt = 1f / 30f;       // ⇒ MaxDv = 1500/30 = 50 px/s
        SteeringRamp.ApplyRedirect(body, dt);

        Vector2 dv = body.Velocity - vBefore;
        output.WriteLine($"dv.Length() = {dv.Length():F3} (expected ≤ 50.0)");
        output.WriteLine($"body.Velocity post-clip = ({body.Velocity.X:F2}, {body.Velocity.Y:F2})");
        Assert.True(dv.Length() <= 50f + 0.01f,
            $"Δv magnitude {dv.Length():F3} exceeds MaxForce·dt = 50.0");
        // The unredirected forward velocity must still be substantial — the
        // body should NOT have been fully redirected away from the wall.
        Assert.True(body.Velocity.X > 900f,
            $"Expected most forward velocity to survive the cap, got vx={body.Velocity.X:F2}");
        // LastImpulse must reflect the (clipped) actual Δv, not the ideal one.
        Assert.InRange(ramp.LastImpulse.Length(), dv.Length() - 0.01f, dv.Length() + 0.01f);
    }

    [Fact]
    public void MaxForce_InfiniteCap_BehavesLikeLegacyFullRedirect()
    {
        // Sanity: with MaxForce = +∞ the ramp redirects to full magnitude
        // along SurfaceDir, same as before this knob existed.
        var (body, _) = BuildHighCornerRamp(vyCap: float.PositiveInfinity);
        // ramp.MaxForce left at default = +∞
        body.Velocity = new Vector2(300f, 0f);

        SteeringRamp.ApplyRedirect(body, 1f / 30f);

        output.WriteLine($"no-force-cap result: ({body.Velocity.X:F2}, {body.Velocity.Y:F2})");
        // Same expectation as HighCorner_FastInbound_WithoutCap_VyExceedsLimit:
        // strong upward redirect because the ramp is at infinite stiffness.
        Assert.True(body.Velocity.Y < -100f,
            $"Expected strong upward redirect with infinite force cap, got vy={body.Velocity.Y:F2}");
    }

    // End-to-end through PhysicsWorld + a real one-block vault, asserting that the
    // ParkourState path wires MaxRedirectVy = MovementConfig.Current.ParkourRampMaxVy
    // so a vault never exceeds that cap in vy. Uses the same fixture as
    // SimulationTests.HoldRight_VaultOneBlock_LandsOnTop.
    [Fact]
    public void VaultOneBlock_PeakUpwardVy_StaysWithinConfigCap()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOXXXXXXXXOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(12f, 36f),
            Script        = InputScript.Always(new PlayerInput { Right = true }),
            Frames        = 120,
            Dt            = 1f / 30f,
            Gravity       = new Vector2(0f, 600f),
        };

        var frames = SimRunner.Run(cfg);

        float cap = MovementConfig.Current.ParkourRampMaxVy;
        float peakUp = 0f; // most-negative vy seen during ParkourState
        int peakFrame = -1;
        for (int i = 0; i < frames.Length; i++)
        {
            if (!frames[i].State.Contains("Parkour")) continue;
            if (frames[i].Vy < peakUp) { peakUp = frames[i].Vy; peakFrame = i; }
        }
        output.WriteLine($"Peak upward vy during Parkour: {peakUp:F2} on frame {peakFrame} (cap={cap})");

        // Cap is enforced inside the ramp redirect; after the redirect the state's
        // anti-grav force is applied next frame, so we allow a small headroom for
        // one frame of integration over the cap (≈ gravity*dt = 20). Per-frame
        // gravity is added before the next redirect samples vy, but ResolveVelocity
        // re-clamps every step, so the trace is the cap value at most plus one
        // frame's gravity drift.
        const float Headroom = 25f;
        Assert.True(-peakUp <= cap + Headroom,
            $"Peak upward vy during Parkour = {-peakUp:F2}, exceeds cap {cap} + headroom {Headroom}");

        // Ballistic crest cap: the redirect may never carry more upward speed than a
        // free-fall arc needs to coast the remaining rise, so the body crests at ~zero
        // vy and settles — no ballistic pop above the step. Assert the apex of the whole
        // traversal barely exceeds the final resting height on top of the block.
        float apexY  = float.MaxValue;                 // y-down: min Y = highest point
        foreach (var f in frames) if (f.Y < apexY) apexY = f.Y;
        // Standing rest height on top of the block: block top is row 2, body center
        // floats 2·Radius above the surface it stands on. (Not frames[^1] — by frame
        // 120 the body has run off the right edge of the fixture and is falling.)
        float restY  = 2 * Chunk.TileSize - 2f * PlayerCharacter.Radius;
        const float OvershootMargin = 6f;
        output.WriteLine($"apexY={apexY:F2}, restY={restY:F2}, overshoot={restY - apexY:F2}");
        Assert.True(restY - apexY <= OvershootMargin,
            $"Vault overshoots its landing height by {restY - apexY:F2}px (allowed {OvershootMargin})");
    }
}
