using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Bug report: "the character fails to jump occasionally. They take damage
// every time they jump, these are probably linked effects."
//
// Root cause: a max-hold jump's landing impulse (|vnRel| ≈ 260-270 px/s) sat
// just above the old CrushImpulseThreshold of 200. Every clean jump:
//   * dealt ~0.21 HP crush damage on landing
//   * routed through Combat.OnHitRegistered → 8 frames of HitstunActive
//   * which gates JumpingState.CheckPreConditions → next press dropped
// Both symptoms come from the same threshold being below the player's own
// jump landings. Raising it past compound double-jump landings (~370) fixes
// both at once.
public class JumpFromCompressedTests(ITestOutputHelper output)
{
    // A clean held jump from rest should not damage the player, regardless of
    // landing position fidelity (the test only needs to detect that an
    // airborne→standing cycle ran AND HP is intact at the end).
    [Fact]
    public void HeldJump_LandsCleanly_NoDamageNoStun()
    {
        // Tall stack so the body can't escape the level. Floor top y = 160.
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        var player = new PlayerCharacter(new Vector2(80f, 141f));   // rest = 160-19
        var bodies = new List<PhysicsBody> { player.Body };
        var ctrl = new Controller();
        var hb = new HitboxWorld();
        var hu = new HurtboxWorld();
        const float dt = 1f / 60f;
        var g = new Vector2(0f, 600f);

        for (int i = 0; i < 30; i++)
        {
            ctrl.InjectInput(default);
            player.Update(ctrl, terrain, hb, hu, dt);
            PhysicsWorld.StepSwept(bodies, terrain, dt, g);
        }
        float postSettleHealth = player.Health;
        output.WriteLine($"Settled: state={player.CurrentStateName}, posY={player.Body.Position.Y:F2}, HP={postSettleHealth}");

        float peakImpulse = 0f;
        bool sawJumping = false;
        bool sawAirborne = false;

        for (int f = 0; f < 120; f++)
        {
            ctrl.InjectInput(f < 8 ? new PlayerInput { Space = true } : default);
            player.Update(ctrl, terrain, hb, hu, dt);
            PhysicsWorld.StepSwept(bodies, terrain, dt, g);

            if (player.CurrentStateName.Contains("Jumping")) sawJumping = true;
            if (!player.CurrentStateName.Contains("Standing")) sawAirborne = true;
            peakImpulse = MathF.Max(peakImpulse, player.Body.LastImpulseMagnitude);

            if (sawAirborne && player.CurrentStateName.Contains("Standing")
                && MathF.Abs(player.Body.Position.Y - 141f) < 2f) break;
        }

        output.WriteLine($"After cycle: state={player.CurrentStateName}, posY={player.Body.Position.Y:F2}, HP={player.Health}, peakImpulse={peakImpulse:F2}");

        Assert.True(sawJumping, "Player should have entered JumpingState");
        Assert.Equal(postSettleHealth, player.Health);
    }

    // The hitstun half: after a clean held jump lands, pressing Space again
    // shortly after should fire a new jump (not be gated by hitstun).
    [Fact]
    public void HeldJump_ThenImmediatelyJumpAgain_SecondJumpFires()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        var player = new PlayerCharacter(new Vector2(80f, 141f));
        var bodies = new List<PhysicsBody> { player.Body };
        var ctrl = new Controller();
        var hb = new HitboxWorld();
        var hu = new HurtboxWorld();
        const float dt = 1f / 60f;
        var g = new Vector2(0f, 600f);

        for (int i = 0; i < 30; i++)
        {
            ctrl.InjectInput(default);
            player.Update(ctrl, terrain, hb, hu, dt);
            PhysicsWorld.StepSwept(bodies, terrain, dt, g);
        }

        // First jump cycle.
        bool sawAirborne = false;
        for (int f = 0; f < 120; f++)
        {
            ctrl.InjectInput(f < 8 ? new PlayerInput { Space = true } : default);
            player.Update(ctrl, terrain, hb, hu, dt);
            PhysicsWorld.StepSwept(bodies, terrain, dt, g);

            if (!player.CurrentStateName.Contains("Standing")) sawAirborne = true;
            if (sawAirborne && player.CurrentStateName.Contains("Standing")
                && MathF.Abs(player.Body.Position.Y - 141f) < 2f) break;
        }
        Assert.Contains("Standing", player.CurrentStateName);

        // Now press Space immediately — should NOT be gated by hitstun from
        // the landing (since landing impulse is below CrushImpulseThreshold).
        // Edge-detect Space: must go from up to down for JumpJustPressed.
        // (We were holding `default` last; now press Space afresh.)
        ctrl.InjectInput(new PlayerInput { Space = true });
        player.Update(ctrl, terrain, hb, hu, dt);
        PhysicsWorld.StepSwept(bodies, terrain, dt, g);

        output.WriteLine($"Second jump press: state={player.CurrentStateName}, posY={player.Body.Position.Y:F2}, HP={player.Health}");
        Assert.Contains("Jumping", player.CurrentStateName);
    }
}
