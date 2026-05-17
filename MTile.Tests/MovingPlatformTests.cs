using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;

namespace MTile.Tests;

// Carry behavior for a body riding a MovingRectangle. The fix is in
// PhysicsWorld: constraint enforcement + collision resolution zero the
// *relative* normal velocity (body − surface) rather than the absolute,
// so a body resting on a moving surface inherits its motion along the normal.
// Tangential carry is NOT implemented — horizontal motion on a horizontally
// moving platform is expected to FAIL.
public class MovingPlatformTests
{
    private const float BodyRadius     = 9.5f;
    private const float PlatformWidth  = 64f;
    private const float PlatformHeight = 16f;
    private const float PlatformHalfH  = PlatformHeight * 0.5f;
    private const float Dt             = 1f / 60f;
    private const int   FrameCount     = 120;          // ~2 s

    private static readonly Vector2 Gravity = new(0f, 600f);

    private static (ChunkMap chunks, MovingRectangle platform, PhysicsBody body, List<PhysicsBody> bodies)
        Setup(Vector2 platformPos, Vector2 bodyPos)
    {
        var chunks = new ChunkMap();
        var platform = new MovingRectangle(platformPos, PlatformWidth, PlatformHeight);
        chunks.Providers.Add(platform);
        var body = new PhysicsBody(Polygon.CreateRegular(BodyRadius, 6), bodyPos);
        return (chunks, platform, body, new List<PhysicsBody> { body });
    }

    // Body falls onto a constant-velocity rising platform. Body should rest
    // on top and match the platform's vertical velocity (the original carry case).
    [Fact]
    public void BodyFallingOntoRisingPlatform_RidesItWithMatchingVelocity()
    {
        const float platformVelY = -50f;
        var (chunks, platform, body, bodies) = Setup(
            new Vector2(0f, 100f),
            new Vector2(0f, 100f - PlatformHalfH - BodyRadius - 50f));

        for (int i = 0; i < FrameCount; i++)
        {
            platform.SetPosition(platform.Position + new Vector2(0f, platformVelY * Dt), Dt);
            PhysicsWorld.StepSwept(bodies, chunks, Dt, Gravity);
        }

        float expectedY = platform.Top - BodyRadius;
        Assert.True(MathF.Abs(body.Position.Y - expectedY) < 2f,
            $"Y expected ≈{expectedY}, got {body.Position.Y}. Platform top={platform.Top}.");
        float velTol = Gravity.Y * Dt * 1.5f;
        Assert.True(MathF.Abs(body.Velocity.Y - platformVelY) < velTol,
            $"vy expected ≈{platformVelY}, got {body.Velocity.Y}. Tol ±{velTol:F1}.");
        Assert.True(MathF.Abs(body.Velocity.X) < 1f, $"vx should be 0, got {body.Velocity.X}.");
        Assert.True(MathF.Abs(body.Position.X) < 1f, $"x should be 0, got {body.Position.X}.");
    }

    // Sanity check: zero-velocity platform should behave exactly like a static tile.
    // Body falls onto it and settles.
    [Fact]
    public void BodyOnStationaryPlatform_RestsLikeOnStaticTile()
    {
        var (chunks, platform, body, bodies) = Setup(
            new Vector2(0f, 100f),
            new Vector2(0f, 60f));

        for (int i = 0; i < FrameCount; i++)
        {
            platform.SetPosition(platform.Position, Dt);  // zero velocity
            PhysicsWorld.StepSwept(bodies, chunks, Dt, Gravity);
        }

        float expectedY = platform.Top - BodyRadius;
        Assert.True(MathF.Abs(body.Position.Y - expectedY) < 2f,
            $"Body should rest on stationary platform. Expected Y≈{expectedY}, got {body.Position.Y}.");
        Assert.True(MathF.Abs(body.Velocity.Y) < 5f,
            $"Body should be at rest. Got vy={body.Velocity.Y}.");
    }

    // Platform descends at a constant rate slower than gravity-driven freefall.
    // Body should ride down with it (rather than separating and falling freely).
    [Fact]
    public void BodyOnDescendingPlatform_TracksDownwardMotion()
    {
        const float platformVelY = +30f;  // descending
        var (chunks, platform, body, bodies) = Setup(
            new Vector2(0f, -100f),
            new Vector2(0f, -100f - PlatformHalfH - BodyRadius));   // already in contact

        for (int i = 0; i < FrameCount; i++)
        {
            platform.SetPosition(platform.Position + new Vector2(0f, platformVelY * Dt), Dt);
            PhysicsWorld.StepSwept(bodies, chunks, Dt, Gravity);
        }

        float expectedY = platform.Top - BodyRadius;
        Assert.True(MathF.Abs(body.Position.Y - expectedY) < 2f,
            $"Body should ride descending platform. Expected Y≈{expectedY}, got {body.Position.Y}. Platform top={platform.Top}.");
        Assert.True(MathF.Abs(body.Velocity.Y - platformVelY) < 5f,
            $"Body vy should track platform vy={platformVelY}, got {body.Velocity.Y}.");
    }

    // Platform moves horizontally — tangential motion. Friction on the SurfaceDistance
    // pulls the body's tangential velocity toward the surface's, so the body rides along.
    [Fact]
    public void BodyOnHorizontallyMovingPlatform_InheritsHorizontalMotion()
    {
        const float platformVelX = +50f;
        var (chunks, platform, body, bodies) = Setup(
            new Vector2(0f, 100f),
            new Vector2(0f, 100f - PlatformHalfH - BodyRadius));

        for (int i = 0; i < FrameCount; i++)
        {
            platform.SetPosition(platform.Position + new Vector2(platformVelX * Dt, 0f), Dt);
            PhysicsWorld.StepSwept(bodies, chunks, Dt, Gravity);
        }

        // Body's horizontal position should track the platform's center.
        float xGap = MathF.Abs(body.Position.X - platform.Position.X);
        Assert.True(xGap < PlatformHalfH,
            $"Body should ride platform horizontally. Body.X={body.Position.X}, platform.X={platform.Position.X}, gap={xGap}.");
        // And the body's vx should match the platform's vx (within a tolerance for
        // one-frame catch-up).
        Assert.True(MathF.Abs(body.Velocity.X - platformVelX) < 5f,
            $"Body vx should track platform vx={platformVelX}, got {body.Velocity.X}.");
    }

    // Platform oscillates sinusoidally (rises, peaks, descends). Body riding it
    // should stay in contact through the direction reversal.
    [Fact]
    public void BodyOnReversingPlatform_StaysInContactThroughPeak()
    {
        const float amplitude = 40f;
        const float period    =  2f;
        const int   frames    = 300;  // 5 s, ≈ 2.5 cycles
        var (chunks, platform, body, bodies) = Setup(
            new Vector2(0f, 100f),
            new Vector2(0f, 100f - PlatformHalfH - BodyRadius));

        float maxGapAfterSettle = 0f;
        for (int i = 0; i < frames; i++)
        {
            float t = (i + 1) * Dt;
            float y = 100f - amplitude * MathF.Sin(t * MathHelper.TwoPi / period);
            platform.SetPosition(new Vector2(0f, y), Dt);
            PhysicsWorld.StepSwept(bodies, chunks, Dt, Gravity);

            if (i > 30)  // skip transient
            {
                float bodyBottom = body.Position.Y + BodyRadius;
                float gap = MathF.Abs(bodyBottom - platform.Top);
                if (gap > maxGapAfterSettle) maxGapAfterSettle = gap;
            }
        }

        Assert.True(maxGapAfterSettle < 5f,
            $"Body should stay near platform top through direction reversals. Max bottom-to-top gap: {maxGapAfterSettle}.");
    }

    // Body riding a rising platform applies a jump impulse. The jump should
    // launch the body upward; the velocity after launch should reflect the
    // jump impulse (not be clamped to the platform's velocity).
    [Fact]
    public void BodyJumpingOffRisingPlatform_RetainsJumpVelocity()
    {
        const float platformVelY = -50f;
        const float jumpVelocity = -300f;   // strong upward impulse
        var (chunks, platform, body, bodies) = Setup(
            new Vector2(0f, 100f),
            new Vector2(0f, 100f - PlatformHalfH - BodyRadius));

        // Settle into contact.
        for (int i = 0; i < 30; i++)
        {
            platform.SetPosition(platform.Position + new Vector2(0f, platformVelY * Dt), Dt);
            PhysicsWorld.StepSwept(bodies, chunks, Dt, Gravity);
        }

        // Jump.
        body.Velocity = new Vector2(0f, jumpVelocity);

        platform.SetPosition(platform.Position + new Vector2(0f, platformVelY * Dt), Dt);
        PhysicsWorld.StepSwept(bodies, chunks, Dt, Gravity);

        // After one frame: gravity adds Gravity.Y * Dt to vy. Constraint should NOT
        // clamp the jump impulse (vnRel is positive — body moving away from surface).
        float expectedVy = jumpVelocity + Gravity.Y * Dt;
        Assert.True(MathF.Abs(body.Velocity.Y - expectedVy) < 5f,
            $"Body's jump should not be clamped to platform velocity. Expected vy≈{expectedVy}, got vy={body.Velocity.Y}.");
        // Body should be above platform top now.
        Assert.True(body.Position.Y < platform.Top - BodyRadius,
            $"Body should have left the platform. Body Y={body.Position.Y}, platform top={platform.Top}.");
    }

    // Platform descends faster than the body can fall under gravity alone (briefly).
    // Body should not get stuck or embedded — it separates from the platform until
    // gravity catches up and they re-meet.
    [Fact]
    public void BodyOnPlatformDescendingFasterThanGravity_DoesNotEmbed()
    {
        const float platformVelY = +400f;  // > Gravity*Dt accumulated for several frames
        var (chunks, platform, body, bodies) = Setup(
            new Vector2(0f, -100f),
            new Vector2(0f, -100f - PlatformHalfH - BodyRadius));

        for (int i = 0; i < FrameCount; i++)
        {
            platform.SetPosition(platform.Position + new Vector2(0f, platformVelY * Dt), Dt);
            PhysicsWorld.StepSwept(bodies, chunks, Dt, Gravity);

            // Body should never be inside the platform: body bottom must not exceed
            // platform bottom (more than a small Epsilon).
            float bodyBottom = body.Position.Y + BodyRadius;
            Assert.True(bodyBottom <= platform.Bottom + 1f,
                $"Frame {i}: body penetrated through platform. Body bottom={bodyBottom}, platform bottom={platform.Bottom}.");
        }
    }
}
