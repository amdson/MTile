using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// PhysicsContact.LastImpulse: per-step transient that accumulates the impulse
// delivered TO the body through each contact during PhysicsWorld.StepSwept.
// Step 2 of Plans/TODO_TOP_BULLETS_PLAN.md — unlocks no-damage tests and jitter
// diagnostics. These tests pin signs + orders of magnitude (Newton's third for
// resting weight, horizontal accumulation for a wall push), not exact values.
public class PhysicsContactImpulseTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 30f;
    private static readonly Vector2 Gravity = new(0f, 600f);

    private static PhysicsBody NewBody(Vector2 pos)
        => new PhysicsBody(Polygon.CreateRectangle(16f, 16f), pos);

    // Drop a body on a floor; once at rest, the floor contact must report
    // upward (negative-Y) impulse per step ≈ |gravity.Y| * dt — the floor pushing
    // back at gravity's weight (Newton's third).
    [Fact]
    public void RestingBodyOnFloor_FloorContact_ReportsGravityImpulse()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOO
            OOOOOOOOOO
            OOOOOOOOOO
            OOOOOOOOOO
            XXXXXXXXXX");

        // Floor top at y = 4*16 = 64; body radius 8; place a bit above to drop in.
        var body = NewBody(new Vector2(80f, 40f));
        var bodies = new List<PhysicsBody> { body };

        // Let it settle.
        for (int f = 0; f < 30; f++)
            PhysicsWorld.StepSwept(bodies, terrain, Dt, Gravity);

        // Find the floor contact (normal points UP from surface toward body ⇒ Y < 0).
        SurfaceContact floor = null;
        foreach (var c in body.Constraints)
            if (c is SurfaceContact sc && sc.Normal.Y < -0.7f) { floor = sc; break; }
        Assert.NotNull(floor);

        // Take one more clean step to sample the rest-state impulse.
        PhysicsWorld.StepSwept(bodies, terrain, Dt, Gravity);

        float expected = Gravity.Y * Dt; // 20 px/s of momentum per step
        output.WriteLine($"floor.LastImpulse = {floor.LastImpulse}, expected Y ≈ -{expected:F2}");

        // Upward push (negative Y), magnitude within a wide tolerance.
        Assert.True(floor.LastImpulse.Y < 0f, $"floor impulse Y should be negative (upward); got {floor.LastImpulse.Y}");
        Assert.InRange(-floor.LastImpulse.Y, expected * 0.5f, expected * 2.0f);
        // Horizontal noise should be tiny — body is at rest.
        Assert.InRange(floor.LastImpulse.X, -1f, 1f);
    }

    // Drive a body horizontally into a wall with NO floor underneath (zero gravity)
    // so the wall is the only contact and absorbs the full inbound momentum. The
    // wall contact's LastImpulse should accumulate a leftward (negative-X) impulse
    // roughly equal to the inbound vx the wall had to zero each step.
    [Fact]
    public void BodyMovingIntoWall_WallContact_AccumulatesHorizontalImpulse()
    {
        // Just a vertical wall on the right.
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOXOOO
            OOOOOOXOOO
            OOOOOOXOOO
            OOOOOOXOOO
            OOOOOOXOOO");

        // Wall left face at x = 6*16 = 96. Body half-width = 8 (16x16 rectangle).
        var body = NewBody(new Vector2(70f, 32f));
        var bodies = new List<PhysicsBody> { body };
        // Zero gravity so the wall is the only constraint that ever forms.
        var noGravity = Vector2.Zero;

        // Drive into the wall at 150 px/s for a few frames; wall SD gets established.
        for (int f = 0; f < 15; f++)
        {
            body.Velocity = new Vector2(150f, 0f);
            PhysicsWorld.StepSwept(bodies, terrain, Dt, noGravity);
        }

        // Find the wall contact (normal points LEFT from wall toward body ⇒ X < 0).
        SurfaceContact wall = null;
        foreach (var c in body.Constraints)
            if (c is SurfaceContact sc && sc.Normal.X < -0.7f) { wall = sc; break; }
        Assert.NotNull(wall);

        // One more frame to sample the steady-state impulse — body is pressed against
        // wall with vx=150; existing-constraint loop should zero vx, accumulating
        // -150 of X impulse onto the wall contact.
        body.Velocity = new Vector2(150f, 0f);
        PhysicsWorld.StepSwept(bodies, terrain, Dt, noGravity);

        output.WriteLine($"wall.Normal = {wall.Normal}, wall.LastImpulse = {wall.LastImpulse}");

        // Leftward push back on the body of full inbound momentum (no friction to bleed it).
        Assert.True(wall.LastImpulse.X < 0f, $"wall impulse X should be negative (leftward); got {wall.LastImpulse.X}");
        Assert.InRange(-wall.LastImpulse.X, 100f, 200f);
        // Y stays near zero (no gravity, no tangential coupling on a wall).
        Assert.InRange(wall.LastImpulse.Y, -1f, 1f);
    }
}
