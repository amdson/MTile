using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;

namespace MTile.Tests;

// Physics/Launch.cs — a launch is bookkeeping over Body.Velocity: it decays only the
// share a hit put there, rides bounces, and gives up whatever physics took away.
// Gravity is off throughout so every number is the launch's own.
public class PhysicsLaunchTests
{
    private const float Dt = 1f / 60f;
    private const float T  = Chunk.TileSize;

    // Open air, with a 1-col stone wall at col 12 (rows 0..24).
    private static ChunkMap Wall()
    {
        var sb = new StringBuilder();
        for (int r = 0; r < 25; r++)
        {
            var line = new char[24];
            for (int i = 0; i < 24; i++) line[i] = i == 12 ? 'X' : 'O';
            sb.Append(line).Append('\n');
        }
        return SimTerrain.FromAscii(sb.ToString());
    }

    private static PhysicsBody Box(Vector2 pos, float restitution)
    {
        var poly = new Polygon(new[]
        {
            new Vector2(-8f, -8f), new Vector2(8f, -8f),
            new Vector2( 8f,  8f), new Vector2(-8f, 8f),
        });
        return new PhysicsBody(poly, pos)
        {
            Impact = new ImpactDamage
            {
                Mass = 1f, ImpulseThreshold = 1e9f, DamagePerUnitImpulse = 0f,
                BreakThreshold = float.PositiveInfinity,
                BounceRestitution = restitution, BounceImpulseThreshold = 50f,
            },
        };
    }

    private static void Run(PhysicsBody body, ChunkMap terrain, int frames)
    {
        var bodies = new List<PhysicsBody> { body };
        for (int f = 0; f < frames; f++) LaunchSystem.StepSwept(bodies, terrain, Dt, Vector2.Zero);
    }

    [Fact]
    public void Launch_DecaysLinearly_AndLeavesOwnVelocityAlone()
    {
        var terrain = Wall();
        var body = Box(new Vector2(2 * T, 10 * T), restitution: 0f);
        body.Velocity = new Vector2(0f, -100f);                       // the body's own motion
        LaunchSystem.Hit(body, ref body.Velocity, new Vector2(300f, 0f), decay: 1200f, impactMass: 1f);

        Run(body, terrain, 6);                                        // 0.1 s: −120 px/s
        Assert.Equal(180f, body.Velocity.X, 1);
        Assert.Equal(-100f, body.Velocity.Y, 2);

        Run(body, terrain, 30);                                       // spent at 0.25 s
        Assert.False(body.Launch.Active);
        Assert.Equal(0f, body.Velocity.X, 2);
        Assert.Equal(-100f, body.Velocity.Y, 2);                      // untouched throughout
    }

    [Fact]
    public void Launch_IntoBouncyWall_ReflectsAndKeepsDecaying()
    {
        var terrain = Wall();
        var body = Box(new Vector2(10 * T, 10 * T), restitution: 0.5f);
        LaunchSystem.Hit(body, ref body.Velocity, new Vector2(600f, 0f), decay: 600f, impactMass: 1f);

        float minVx = 0f;
        var bodies = new List<PhysicsBody> { body };
        for (int f = 0; f < 120; f++)
        {
            LaunchSystem.StepSwept(bodies, terrain, Dt, Vector2.Zero);
            if (body.Velocity.X < minVx) minVx = body.Velocity.X;
            if (body.Velocity.X < 0f)
                // Rebounding: the launch rode the bounce, pointing away from the wall
                // and matching the real velocity.
                Assert.True(body.Launch.Vel.X <= 0f, $"f{f}: launch {body.Launch.Vel} vs v {body.Velocity}");
        }

        Assert.True(minVx < -100f, $"never rebounded (min vx {minVx})");
        Assert.False(body.Launch.Active);
        Assert.Equal(0f, body.Velocity.X, 1);                         // the rebound decayed out too
    }

    [Fact]
    public void Launch_IntoDeadWall_Ends()
    {
        var terrain = Wall();
        var body = Box(new Vector2(10 * T, 10 * T), restitution: 0f);
        LaunchSystem.Hit(body, ref body.Velocity, new Vector2(600f, 0f), decay: 300f, impactMass: 1f);

        Run(body, terrain, 30);
        Assert.False(body.Launch.Active);
        Assert.True(System.MathF.Abs(body.Velocity.X) < 1f, $"vx {body.Velocity.X}");
    }

    [Fact]
    public void Launch_Frozen_DoesNotDecay()
    {
        var body = Box(new Vector2(2 * T, 10 * T), restitution: 0f);
        LaunchSystem.Hit(body, ref body.Velocity, new Vector2(300f, 0f), decay: 1200f, impactMass: 1f);

        body.Frozen = true;
        body.Velocity = Vector2.Zero;                                 // the owner parked it
        LaunchSystem.PreStep(body, Dt);
        LaunchSystem.PostStep(body);
        Assert.Equal(300f, body.Launch.Vel.X, 3);
    }

    [Fact]
    public void SecondHit_ReplacesTheRemainingLaunch()
    {
        var body = Box(Vector2.Zero, restitution: 0f);
        body.Velocity = new Vector2(0f, 50f);
        LaunchSystem.Hit(body, ref body.Velocity, new Vector2(400f, 0f), 1200f, 1f);
        LaunchSystem.Hit(body, ref body.Velocity, new Vector2(0f, -200f), 1200f, 1f);

        Assert.Equal(new Vector2(0f, -150f), body.Velocity);          // own + second hit only
        Assert.Equal(new Vector2(0f, -200f), body.Launch.Vel);
    }

    [Fact]
    public void ImpactMassScale_FadesWithTheLaunch()
    {
        var body = Box(Vector2.Zero, restitution: 0f);
        LaunchSystem.Hit(body, ref body.Velocity, new Vector2(400f, 0f), decay: 1200f, impactMass: 4f);

        LaunchSystem.PreStep(body, Dt);
        Assert.Equal(4f, body.ImpactMassScale, 3);                    // full on the first step

        body.Launch.Vel = new Vector2(200f, 0f);                      // half spent
        LaunchSystem.PreStep(body, Dt);
        Assert.Equal(2.5f, body.ImpactMassScale, 3);

        body.Launch = default;
        LaunchSystem.PreStep(body, Dt);
        Assert.Equal(1f, body.ImpactMassScale);
    }

    [Fact]
    public void BodyState_RoundTripsTheLaunch()
    {
        var body = Box(Vector2.Zero, restitution: 0f);
        LaunchSystem.Hit(body, ref body.Velocity, new Vector2(400f, 0f), 1200f, 3f);
        var snap = BodyState.Capture(body);

        body.Launch = default;
        snap.RestoreInto(body);
        Assert.Equal(new Vector2(400f, 0f), body.Launch.Vel);
        Assert.Equal(1200f, body.Launch.Decay);
        Assert.Equal(3f, body.Launch.ImpactMass);
    }
}
