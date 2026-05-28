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
    // Build a ramp whose corner is far above the body so the binding vertex yields
    // a near-vertical SurfaceDir (large θ*). Without the vy cap a fast horizontal
    // inbound velocity would emerge as ~equally fast upward velocity.
    private static (PhysicsBody body, SteeringRamp ramp) BuildHighCornerRamp(float vyCap)
    {
        var body = new PhysicsBody(Polygon.CreateRegular(8f, 6), new Vector2(0f, 0f));
        // Place a corner far above and slightly to the right (forward = +X, body must
        // pass OVER the corner). Body at origin, corner at (16, -200) ⇒ all body
        // vertices are far below and behind the corner ⇒ θ* very close to π/2.
        var ramp = new SteeringRamp
        {
            Sense = SteeringSense.Over,
            ForwardDir = +1,
            Corner = new Vector2(16f, -200f),
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
    }
}
