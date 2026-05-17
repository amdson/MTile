using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Jump velocity is set *relative* to the source surface, not added to the body's
// current velocity. The relative formulation prevents pathological launches when
// the body enters JumpingState with redirected vy (e.g. mid-Parkour ramp).
//
// Source surface is tracked via a state-owned FloatingSurfaceDistance (similar
// to StandingState's _ground but with a wider probe window).
public class JumpingStateTests(ITestOutputHelper output)
{
    // Plain ground, no exotic entry conditions. After jump fires, vy should be
    // exactly JumpVelocity (source is static, so source.vy = 0).
    [Fact]
    public void Jump_FromFlatStaticGround_SetsVyToJumpVelocity()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        // Hold Space immediately so the jump fires on frame 0/1.
        var script = InputScript.Always(new PlayerInput { Space = true });

        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(80f, 20f),
            Script        = script,
            Frames        = 10,
            Dt            = 1f / 60f,
            Gravity       = new Vector2(0f, 600f),
        };

        var frames = SimRunner.Run(cfg);
        SimReport.Print(frames, output, fullTable: true);

        // Find first JumpingState frame.
        int jumpFrame = -1;
        for (int i = 0; i < frames.Length; i++)
            if (frames[i].State.Contains("Jumping")) { jumpFrame = i; break; }
        Assert.True(jumpFrame >= 0, "Player should enter JumpingState");

        // On the first JumpingState frame, vy reflects JumpVelocity (set in Enter)
        // plus one frame of JumpHoldForce + gravity (applied by the physics step).
        float dt = 1f / 60f;
        float expectedVy = MovementConfig.Current.JumpVelocity
                         + (MovementConfig.Current.JumpHoldForce + 600f) * dt;
        Assert.True(MathF.Abs(frames[jumpFrame].Vy - expectedVy) < 5f,
            $"Expected vy ≈ {expectedVy} on jump frame, got {frames[jumpFrame].Vy}");
    }

    // KEY TEST: body has high pre-existing upward vy (simulating Parkour ramp
    // redirect). With the new relative-to-source formulation, the jump must
    // *overwrite* the upward vy with source.vy + JumpVelocity — NOT add to it.
    [Fact]
    public void Jump_WithPreExistingUpwardVelocity_OverwritesInsteadOfAdding()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        // Start with strong upward vy — represents a ramp redirect that pushed
        // the body upward while still in StandingState's probe range.
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(80f, 22f),       // body bottom ≈ 31.5, floor top = 32
            StartVelocity = new Vector2(0f, -400f),       // strong upward (simulated ramp redirect)
            Script        = InputScript.Always(new PlayerInput { Space = true }),
            Frames        = 6,
            Dt            = 1f / 60f,
            Gravity       = new Vector2(0f, 600f),
        };

        var frames = SimRunner.Run(cfg);
        SimReport.Print(frames, output, fullTable: true);

        // Find the JumpingState entry.
        int jumpFrame = -1;
        for (int i = 0; i < frames.Length; i++)
            if (frames[i].State.Contains("Jumping")) { jumpFrame = i; break; }
        Assert.True(jumpFrame >= 0, "Player should enter JumpingState");

        // After one frame of jump:
        //   New (relative): vy = JumpVelocity + (JumpHoldForce + gravity)·dt
        //   Old (additive): vy = -400 + JumpVelocity + (JumpHoldForce + gravity)·dt
        float dt = 1f / 60f;
        float jumpStep   = (MovementConfig.Current.JumpHoldForce + 600f) * dt;
        float expectedNew = MovementConfig.Current.JumpVelocity + jumpStep;
        float expectedOld = -400f + MovementConfig.Current.JumpVelocity + jumpStep;
        output.WriteLine(
            $"Jump frame {jumpFrame}: vy={frames[jumpFrame].Vy}. " +
            $"Old (additive) ≈ {expectedOld}; new (relative) ≈ {expectedNew}");
        Assert.True(MathF.Abs(frames[jumpFrame].Vy - expectedNew) < 20f,
            $"Expected new-style relative vy ≈ {expectedNew}, got {frames[jumpFrame].Vy}");
        Assert.True(MathF.Abs(frames[jumpFrame].Vy - expectedOld) > 100f,
            $"Vy looks like the old additive behavior ({expectedOld}); got {frames[jumpFrame].Vy}");
    }

    // The source FSD must be removed when JumpingState exits. Test in isolation:
    // run StepSwept once with the JumpingState owning a FSD, then drive a CheckConditions
    // failure (release jump) and observe the FSD is gone. Using the PlayerCharacter
    // for this is tricky because StandingState re-adds its own FSD on re-landing.
    [Fact]
    public void Jump_OnExit_RemovesSourceFsd()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        var player = new PlayerCharacter(new Vector2(80f, 20f));
        var bodies = new List<PhysicsBody> { player.Body };
        var ctrl = new Controller();
        var hitboxes  = new HitboxWorld();
        var hurtboxes = new HurtboxWorld();
        const float dt = 1f / 60f;
        var g = new Vector2(0f, 600f);

        // Let player settle into StandingState first.
        for (int i = 0; i < 10; i++)
        {
            ctrl.InjectInput(default);
            player.Update(ctrl, terrain, hitboxes, hurtboxes, dt);
            PhysicsWorld.StepSwept(bodies, terrain, dt, g);
        }
        Assert.Contains("Standing", player.CurrentStateName);

        // Press Space to enter Jumping. Confirm we have an FSD owned by Jumping.
        ctrl.InjectInput(new PlayerInput { Space = true });
        player.Update(ctrl, terrain, hitboxes, hurtboxes, dt);
        PhysicsWorld.StepSwept(bodies, terrain, dt, g);
        Assert.Contains("Jumping", player.CurrentStateName);
        int fsdInJumping = 0;
        foreach (var c in player.Body.Constraints)
            if (c is FloatingSurfaceDistance) fsdInJumping++;
        Assert.Equal(1, fsdInJumping);

        // Release Space. Two frames later CheckConditions fails (_jumpReleased
        // gets set in Update, then next frame's CheckConditions trips). At that
        // point Exit removes Jumping's FSD. If the body is mid-air on transition,
        // FallingState (which owns no FSD) is the destination — zero FSDs in
        // body.Constraints.
        for (int i = 0; i < 6; i++)
        {
            ctrl.InjectInput(default);
            player.Update(ctrl, terrain, hitboxes, hurtboxes, dt);
            PhysicsWorld.StepSwept(bodies, terrain, dt, g);
            if (!player.CurrentStateName.Contains("Jumping")) break;
        }
        Assert.DoesNotContain("Jumping", player.CurrentStateName);

        // Determine current state's expected FSD count:
        //   Falling owns no FSD → expect 0.
        //   Standing owns one  → expect 1 (its own, not the jump's — the jump's
        //   was removed in Exit; this is a fresh one re-added on landing).
        int expected = player.CurrentStateName.Contains("Standing") ? 1 : 0;
        int actual = 0;
        foreach (var c in player.Body.Constraints)
            if (c is FloatingSurfaceDistance) actual++;
        Assert.Equal(expected, actual);
    }

    // CheckConditions must end the jump once the body rises beyond the source's
    // probe window. The held-jump phase must NOT artificially keep the state alive.
    [Fact]
    public void Jump_RisingBeyondProbeWindow_EndsJumpState()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        // Hold Space indefinitely so the jump won't end via release. With a strong
        // initial upward velocity injected we ensure the body races past the probe
        // window quickly — the only thing that should end the jump is CheckConditions
        // detecting the source is out of range.
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(80f, 86f),
            StartVelocity = new Vector2(0f, 0f),
            Script        = InputScript.Always(new PlayerInput { Space = true }),
            Frames        = 60,
            Dt            = 1f / 60f,
            Gravity       = new Vector2(0f, 600f),
        };

        var frames = SimRunner.Run(cfg);
        SimReport.Print(frames, output, fullTable: false);

        // Find first JumpingState frame and the first non-JumpingState frame after.
        int jumpStart = -1, jumpEnd = -1;
        for (int i = 0; i < frames.Length; i++)
        {
            if (jumpStart < 0 && frames[i].State.Contains("Jumping")) { jumpStart = i; continue; }
            if (jumpStart >= 0 && !frames[i].State.Contains("Jumping")) { jumpEnd = i; break; }
        }
        Assert.True(jumpStart >= 0, "Should enter JumpingState");
        Assert.True(jumpEnd >= 0, $"Should exit JumpingState within {cfg.Frames} frames; held Space + no source-in-range should still trip CheckConditions");

        output.WriteLine($"Jumped from frame {jumpStart} to frame {jumpEnd} ({jumpEnd - jumpStart} frames)");
    }
}
