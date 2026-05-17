using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Friction on a static tile floor. The contact must brake horizontal velocity
// when no tangential force is being applied — this is the role that
// StandingState's old "no-input → BrakingForce" branch used to play, now
// delegated to SurfaceContact.Friction.
public class GroundFrictionTests(ITestOutputHelper output)
{
    private const float BodyRadius = 9.5f;
    private const float Dt         = 1f / 60f;
    private static readonly Vector2 Gravity = new(0f, 600f);

    // Build a chunk with a single floor row at y = 16 (row 1). Origin chunk.
    private static (ChunkMap chunks, PhysicsBody body) Setup(Vector2 bodyPos)
    {
        var chunks = new ChunkMap();
        var chunk  = new Chunk { ChunkPos = new Point(0, 0) };
        for (int tx = 0; tx < Chunk.Size; tx++)
            chunk.Tiles[tx, 1].IsSolid = true;
        chunks[chunk.ChunkPos] = chunk;

        var body = new PhysicsBody(Polygon.CreateRegular(BodyRadius, 6), bodyPos);
        return (chunks, body);
    }

    // Body sits on a tile floor (no applied force). Settle a few frames so a
    // SurfaceDistance constraint exists, then nudge it horizontally and step.
    // With Friction = GroundFriction (default 3000 px/s²), a 50 px/s slide
    // should fully brake in well under 50/3000·60 ≈ 1 frame at 60fps.
    [Fact]
    public void BodyOnTileFloor_NoForce_HorizontalVelocityBrakesToZero()
    {
        // Floor row 1 top = y:16. Body center.Y = 16 - radius = 6.5.
        var (chunks, body) = Setup(new Vector2(100f, 6.5f - 0.4f));
        var bodies = new List<PhysicsBody> { body };

        // Settle: let body acquire a SurfaceDistance contact.
        for (int i = 0; i < 30; i++)
            PhysicsWorld.StepSwept(bodies, chunks, Dt, Gravity);

        // Sanity: body is resting near floor.
        Assert.True(MathF.Abs(body.Velocity.Y) < 5f,
            $"Body should be at rest before nudge; got vy={body.Velocity.Y}");

        // Nudge horizontally.
        body.Velocity = new Vector2(50f, body.Velocity.Y);

        // One frame of friction should fully kill 50 px/s (Friction·dt = 3000·(1/60) = 50).
        PhysicsWorld.StepSwept(bodies, chunks, Dt, Gravity);

        output.WriteLine($"After 1 frame: vx={body.Velocity.X}, vy={body.Velocity.Y}");
        Assert.True(MathF.Abs(body.Velocity.X) < 5f,
            $"Friction should brake horizontal velocity. After 1 frame got vx={body.Velocity.X}");
    }

    // As above, but with a smaller per-step cap: 30 px/s nudge across multiple
    // frames should monotonically decay.
    [Fact]
    public void BodyOnTileFloor_NoForce_VelocityDecaysMonotonically()
    {
        var (chunks, body) = Setup(new Vector2(100f, 6.5f - 0.4f));
        var bodies = new List<PhysicsBody> { body };

        for (int i = 0; i < 30; i++)
            PhysicsWorld.StepSwept(bodies, chunks, Dt, Gravity);

        body.Velocity = new Vector2(200f, body.Velocity.Y);
        float prevVx = body.Velocity.X;

        for (int i = 0; i < 30; i++)
        {
            PhysicsWorld.StepSwept(bodies, chunks, Dt, Gravity);
            output.WriteLine($"Frame {i}: vx={body.Velocity.X}");
            Assert.True(MathF.Abs(body.Velocity.X) <= MathF.Abs(prevVx) + 0.1f,
                $"Velocity should monotonically decay. Frame {i}: vx={body.Velocity.X}, prev={prevVx}");
            prevVx = body.Velocity.X;
        }

        Assert.True(MathF.Abs(body.Velocity.X) < 5f,
            $"After 30 frames of friction, vx should be near zero, got {body.Velocity.X}");
    }

    // Applying a tangential force in the direction of motion (walking) should
    // suppress friction so the walk force is not fought.
    [Fact]
    public void BodyOnTileFloor_WalkingForceApplied_FrictionDoesNotFight()
    {
        var (chunks, body) = Setup(new Vector2(100f, 6.5f - 0.4f));
        var bodies = new List<PhysicsBody> { body };

        for (int i = 0; i < 30; i++)
            PhysicsWorld.StepSwept(bodies, chunks, Dt, Gravity);

        // Apply a steady horizontal walk force for 10 frames.
        const float WalkAccel = 3000f;
        for (int i = 0; i < 10; i++)
        {
            body.AppliedForce = new Vector2(WalkAccel, 0f);
            PhysicsWorld.StepSwept(bodies, chunks, Dt, Gravity);
            output.WriteLine($"Frame {i}: vx={body.Velocity.X}");
        }

        // 10 frames · 3000 · (1/60) = 500 px/s if friction is fully suppressed.
        // Even with mild interference we should see strong acceleration.
        Assert.True(body.Velocity.X > 200f,
            $"Walking force should accelerate body; friction must not fight it. Got vx={body.Velocity.X}");
    }

    // Diagnostic: print what the contact looks like after settling so we can
    // see whether Friction is being set on it.
    [Fact]
    public void BodyOnTileFloor_AfterSettle_ContactHasFriction()
    {
        var (chunks, body) = Setup(new Vector2(100f, 6.5f - 0.4f));
        var bodies = new List<PhysicsBody> { body };

        for (int i = 0; i < 30; i++)
            PhysicsWorld.StepSwept(bodies, chunks, Dt, Gravity);

        bool foundFloor = false;
        foreach (var c in body.Constraints)
        {
            if (c is SurfaceContact sc)
            {
                output.WriteLine(
                    $"Contact: type={c.GetType().Name} normal={sc.Normal} " +
                    $"friction={sc.Friction} surfVel={sc.SurfaceVelocity} " +
                    $"pos={sc.Position} minDist={sc.MinDistance}");
                if (sc.Normal.Y < -0.7f)
                {
                    foundFloor = true;
                    Assert.True(sc.Friction > 0f,
                        $"Floor contact should have Friction > 0; got {sc.Friction}");
                }
            }
        }
        Assert.True(foundFloor, "Should have a floor-pointing surface contact after settling.");
    }

    // The on-disk movement_config.json predates GroundFriction and doesn't list it.
    // Verify System.Text.Json keeps the class initializer (3000) for missing properties.
    [Fact]
    public void MovementConfig_LoadFromStaleJson_KeepsGroundFrictionDefault()
    {
        var tmpPath = System.IO.Path.GetTempFileName();
        System.IO.File.WriteAllText(tmpPath,
            "{ \"WalkAccel\": 3000, \"BrakingForce\": 3000 }");
        MovementConfig.Load(tmpPath);
        output.WriteLine($"GroundFriction after Load = {MovementConfig.Current.GroundFriction}");
        Assert.True(MovementConfig.Current.GroundFriction > 0f,
            $"GroundFriction should default to nonzero when JSON omits it. Got {MovementConfig.Current.GroundFriction}");
        System.IO.File.Delete(tmpPath);
    }

    // Full PlayerCharacter on flat tile ground. Body starts with horizontal velocity,
    // no input. StandingState should run; its FloatingSurfaceDistance ground contact
    // carries Friction = GroundFriction; the physics solver should brake the body
    // to a stop in ~1 frame at full friction.
    [Fact]
    public void Player_OnFlatGround_NoInput_DecelaratesToStop()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        // Ground top y=32, body center y = 32 - radius ≈ 22.5. Start mid-row 1.
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(80f, 20f),
            StartVelocity = new Vector2(80f, 0f),
            Script        = InputScript.Always(default),  // no input
            Frames        = 30,
            Dt            = 1f / 60f,
            Gravity       = new Vector2(0f, 600f),
        };

        var frames = SimRunner.Run(cfg);
        SimReport.Print(frames, output, fullTable: true);

        // After ~10 frames the body should be near zero velocity if friction works.
        var settled = frames[20];
        Assert.Contains("Standing", settled.State);
        Assert.True(MathF.Abs(settled.Vx) < 10f,
            $"Body should brake to near-rest with no input on tile floor. Frame 20: vx={settled.Vx:F2}");
    }

    // Repro: jump, then release input. After landing, friction should brake the body.
    // User report: "velocity doesn't decay after landing from a jump".
    [Fact]
    public void Player_JumpsThenLands_FrictionBrakesPostLanding()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        // Frame 0..15:  Hold right + space (jump)
        // Frame 16..29: Hold right (in air, then landed)
        // Frame 30..89: Nothing — friction should brake post-landing.
        var script = new InputScript()
            .For(15, new PlayerInput { Right = true, Space = true })
            .For(15, new PlayerInput { Right = true })
            .Forever(default);

        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(40f, 36f),
            Script        = script,
            Frames        = 120,
            Dt            = 1f / 60f,
            Gravity       = new Vector2(0f, 600f),
        };

        var frames = SimRunner.Run(cfg);
        SimReport.Print(frames, output, fullTable: true);
        SimReport.WriteCsv(frames, "jump_then_land_friction", outputDir: null);

        // Find first frame after landing in Standing/Crouched with input released.
        // After frame 30 the script is default (no input).
        // Find a frame where state is Standing and we're past frame 30.
        int landFrame = -1;
        for (int i = 30; i < frames.Length; i++)
        {
            if (frames[i].State.Contains("Standing"))
            {
                landFrame = i;
                break;
            }
        }
        Assert.True(landFrame >= 0, "Player should be in StandingState at some point after frame 30");

        // 30 frames after landing in Standing without input: vx should be ~0.
        int checkFrame = MathF.Min(landFrame + 30, frames.Length - 1) is var cf ? (int)cf : 0;
        var f = frames[checkFrame];
        output.WriteLine($"Land at frame {landFrame}; check frame {checkFrame}: state={f.State} vx={f.Vx:F2}");
        Assert.True(MathF.Abs(f.Vx) < 10f,
            $"Friction should brake body after landing. Frame {checkFrame}: state={f.State}, vx={f.Vx:F2}");
    }

    // Walking right, then releasing input. After release, friction should brake
    // the body. Asserts horizontal velocity decays after input release.
    [Fact]
    public void Player_WalksThenReleases_DecelaratesAfterRelease()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        // Hold Right for 30 frames, then no input for 30 frames.
        var script = new InputScript()
            .For(30, new PlayerInput { Right = true })
            .Forever(default);

        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(20f, 20f),
            Script        = script,
            Frames        = 60,
            Dt            = 1f / 60f,
            Gravity       = new Vector2(0f, 600f),
        };

        var frames = SimRunner.Run(cfg);
        SimReport.Print(frames, output, fullTable: true);

        // At frame 29 the player was still pressing Right.
        var atRelease = frames[29];
        Assert.True(atRelease.Vx > 50f,
            $"Pre-release: body should be walking. Got vx={atRelease.Vx:F2}");

        // At frame 59 (30 frames after release), body should have braked to near-rest.
        var afterRelease = frames[59];
        Assert.True(MathF.Abs(afterRelease.Vx) < 10f,
            $"Post-release: friction should brake body. Got vx={afterRelease.Vx:F2}");
    }
}
