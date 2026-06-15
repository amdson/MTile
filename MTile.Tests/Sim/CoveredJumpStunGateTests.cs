using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Regression for the CoveredJumpState stun-escape bug (Plans/ARBITRATION_CLEANUP_PLAN.md
// Task 2c): the other jump states each carried an inline `BlocksJump` precondition, but
// CoveredJumpState silently lacked one — a stunned player wedged under an overhang could
// covered-jump out of stun. The capability mask (RequiredCapabilities.Jump applied in the
// selection loop) now gates it like the rest of the jump family.
//
// Geometry is the left-facing corridor from CoveredJumpLeftCorridorTests: ceiling slab
// with its exit corner at x=80, floor top at y=80, body at startX=80 holding Left+Space
// sticks out past the corner and (without stun) covered-jumps.
public class CoveredJumpStunGateTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 30f;
    private const float Gravity = 600f;

    private const string Terrain = @"
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        OOOOOXXXXXXXXXXXXXXX
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXX
        XXXXXXXXXXXXXXXXXXXX";

    private static readonly Vector2 Start = new(80f, 60.5f);
    private static readonly PlayerInput SpaceLeft = new() { Left = true, Space = true };

    // Control: with no stun, this exact setup DOES covered-jump — so the gated case below
    // is meaningfully suppressing something, not vacuously passing.
    [Fact]
    public void Sanity_NoStun_CoveredJumpFires()
    {
        var cfg = new SimConfig
        {
            Terrain       = SimTerrain.FromAscii(Terrain, originTileX: 0, originTileY: 0),
            StartPosition = Start,
            Script        = InputScript.Always(SpaceLeft),
            Frames        = 30,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };
        var frames = SimRunner.Run(cfg);
        Assert.Contains(frames, f => f.State.Contains("CoveredJump"));
    }

    // With stun forced active every frame, the same setup must never enter CoveredJump.
    [Fact]
    public void StunActive_HoldSpaceLeftUnderOverhang_DoesNotCoveredJump()
    {
        var terrain = SimTerrain.FromAscii(Terrain, originTileX: 0, originTileY: 0);
        var player  = new PlayerCharacter(Start);
        var bodies  = new List<PhysicsBody> { player.Body };
        var ctrl    = new Controller();
        var hb      = new HitboxWorld();
        var hu      = new HurtboxWorld();

        var states  = new List<string>(40);
        bool sawStun = false;

        for (int f = 0; f < 40; f++)
        {
            // Refresh stun before each step so StunActive stays true through the whole run.
            // OnHitRegistered sets StunExpireFrame = currentFrame + StunSeconds at this dt;
            // passing the frame about to be processed (player.Frame + 1) keeps it ahead.
            // Impulse 400 > StunImpulseThreshold (350) so StunActive (not just hitstun) flips.
            player.Combat.OnHitRegistered(player.Frame + 1, 400f, Dt);

            ctrl.InjectInput(SpaceLeft);
            terrain.TickSprouts(Dt);
            player.Update(ctrl, terrain, hb, hu, Dt);
            PhysicsWorld.StepSwept(bodies, terrain, Dt, new Vector2(0f, Gravity));

            if (player.Combat.StunActive) sawStun = true;
            states.Add(player.CurrentStateName);
        }

        output.WriteLine($"states: {string.Join(",", states.Distinct())}");
        Assert.True(sawStun, "Stun injection failed — StunActive never observed.");
        Assert.DoesNotContain("CoveredJumpState", states);
    }
}
