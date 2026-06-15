using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// COMBAT_FEEL_PLAN Phase 4 — tumble, tech, and gating defensive movement.
//
// The combat disadvantage window (hitstun OR stun) now blocks the WallCling and
// LedgeGrab movement capabilities, so a hit can't be cancelled by clinging to
// terrain — knockback becomes juggle/edgeguard pressure. A heavy airborne hit goes
// to TumbleState (launch band) instead of a grounded StunnedState, and the launched
// player can tech (jump just before landing) for a brief-invuln bounce.
//
// Stun is injected directly via CombatState.OnHitRegistered (same technique as
// CoveredJumpStunGateTests) so the geometry is deterministic and doesn't depend on
// landing a real attack at a precise frame.
public class TumbleTechTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 30f;
    private static readonly Vector2 Gravity = new(0f, 600f);

    // Tall wall on the right (column 10), open air to its left, no floor in the body's
    // column — a body falling flush against it normally wall-slides.
    private static ChunkMap RightWall()
    {
        var sb = new StringBuilder();
        for (int r = 0; r < 41; r++)
        {
            var line = new char[12];
            for (int i = 0; i < 12; i++) line[i] = 'O';
            line[10] = 'X';
            sb.Append(line).Append('\n');
        }
        return SimTerrain.FromAscii(sb.ToString());
    }

    private static float HalfWidth()
    {
        var probe = new PlayerCharacter(Vector2.Zero);
        return probe.Body.Bounds.Right - probe.Body.Position.X;
    }

    // Sanity: flush against the wall, falling, holding Right (into the wall) and NOT
    // hit → the body wall-slides. Confirms the gated cases below suppress something
    // real rather than passing vacuously.
    [Fact]
    public void Sanity_NoHit_FallingIntoWall_WallSlides()
    {
        var terrain = RightWall();
        const float wallLeft = 10 * 16;
        var player = new PlayerCharacter(new Vector2(wallLeft - HalfWidth(), 60f));
        var bodies = new List<PhysicsBody> { player.Body };
        var ctrl = new Controller();
        var hb = new HitboxWorld(); var hu = new HurtboxWorld();

        bool sawWallSlide = false;
        for (int f = 0; f < 24; f++)
        {
            ctrl.InjectInput(new PlayerInput { Right = true });
            terrain.TickSprouts(Dt);
            player.Update(ctrl, terrain, hb, hu, Dt);
            PhysicsWorld.StepSwept(bodies, terrain, Dt, Gravity);
            if (player.CurrentStateName == "WallSlidingState") sawWallSlide = true;
        }
        Assert.True(sawWallSlide, "Control setup never wall-slid — geometry is wrong.");
    }

    // Hitstun (below the stun threshold) blocks WallCling: the same falling-into-wall
    // body, with hitstun refreshed each frame, must never enter WallSlidingState
    // while HitstunActive.
    [Fact]
    public void Hitstun_BlocksWallCling()
    {
        var terrain = RightWall();
        const float wallLeft = 10 * 16;
        var player = new PlayerCharacter(new Vector2(wallLeft - HalfWidth(), 60f));
        var bodies = new List<PhysicsBody> { player.Body };
        var ctrl = new Controller();
        var hb = new HitboxWorld(); var hu = new HurtboxWorld();

        bool sawHitstun = false;
        var violations = new List<int>();
        for (int f = 0; f < 24; f++)
        {
            // Impulse 300 < StunImpulseThreshold (350): hitstun only, no stun/tumble —
            // isolates the WallCling capability gate.
            player.Combat.OnHitRegistered(player.Frame + 1, 300f, Dt);

            ctrl.InjectInput(new PlayerInput { Right = true });
            terrain.TickSprouts(Dt);
            player.Update(ctrl, terrain, hb, hu, Dt);
            PhysicsWorld.StepSwept(bodies, terrain, Dt, Gravity);

            if (player.Combat.HitstunActive) sawHitstun = true;
            if (player.Combat.HitstunActive && player.CurrentStateName == "WallSlidingState")
                violations.Add(f);
        }

        Assert.True(sawHitstun, "Hitstun injection failed — never observed.");
        Assert.False(player.Combat.StunActive, "Impulse 300 should not have stunned.");
        Assert.Empty(violations);
    }

    // A heavy airborne hit (stun-threshold impulse) launches into TumbleState, not a
    // grounded StunnedState, and a launched body against a wall still can't cling.
    [Fact]
    public void HeavyAirborneHit_Tumbles_AndDoesNotWallCling()
    {
        var terrain = RightWall();
        const float wallLeft = 10 * 16;
        var player = new PlayerCharacter(new Vector2(wallLeft - HalfWidth(), 60f));
        var bodies = new List<PhysicsBody> { player.Body };
        var ctrl = new Controller();
        var hb = new HitboxWorld(); var hu = new HurtboxWorld();

        bool sawTumble = false;
        var wallSlideWhileStunned = new List<int>();
        var states = new List<string>(24);
        for (int f = 0; f < 24; f++)
        {
            player.Combat.OnHitRegistered(player.Frame + 1, 500f, Dt);   // > 350 ⇒ stun

            ctrl.InjectInput(new PlayerInput { Right = true });
            terrain.TickSprouts(Dt);
            player.Update(ctrl, terrain, hb, hu, Dt);
            PhysicsWorld.StepSwept(bodies, terrain, Dt, Gravity);

            states.Add(player.CurrentStateName);
            if (player.CurrentStateName == "TumbleState") sawTumble = true;
            if (player.Combat.StunActive && player.CurrentStateName == "WallSlidingState")
                wallSlideWhileStunned.Add(f);
        }

        output.WriteLine($"states: {string.Join(",", states.Distinct())}");
        Assert.True(sawTumble, "Airborne stun should have entered TumbleState.");
        Assert.Empty(wallSlideWhileStunned);
    }

    // Tech: a tumbling body that presses jump just before landing techs — it ends the
    // launch (StunActive cleared), gains brief i-frames, and pops upward. Pressing
    // jump while still high (outside the tech window) does NOT tech.
    [Fact]
    public void Tumble_JumpNearGround_TechsWithInvulnBounce()
    {
        // Floor at row 10 (top edge y = 160); open air above.
        var sb = new StringBuilder();
        for (int r = 0; r < 10; r++) sb.Append(new string('O', 12)).Append('\n');
        for (int r = 0; r < 3;  r++) sb.Append(new string('X', 12)).Append('\n');
        var terrain = SimTerrain.FromAscii(sb.ToString());
        const float floorTop = 10 * 16;   // y = 160

        // Spawn well above the floor so there's a clear "high, outside window" phase
        // before the descent reaches the tech window.
        var player = new PlayerCharacter(new Vector2(80f, 40f));
        var bodies = new List<PhysicsBody> { player.Body };
        var ctrl = new Controller();
        var hb = new HitboxWorld(); var hu = new HurtboxWorld();

        bool techedWhileHigh = false;   // any tech that fired while far above the floor
        bool sawTechBounce   = false;   // invuln + upward velocity near the ground
        bool stunClearedByTech = false;

        for (int f = 0; f < 50; f++)
        {
            // Keep the launch alive until a tech ends it. Once teched (invulnerable),
            // stop re-injecting so the tech can actually resolve.
            if (!player.Combat.IsInvulnerable(player.Frame + 1) && !stunClearedByTech)
                player.Combat.OnHitRegistered(player.Frame + 1, 500f, Dt);

            // Pulse Space so a fresh Jump intent is always within the buffer window.
            bool space = (f % 2) == 0;
            ctrl.InjectInput(new PlayerInput { Space = space });
            terrain.TickSprouts(Dt);
            player.Update(ctrl, terrain, hb, hu, Dt);
            PhysicsWorld.StepSwept(bodies, terrain, Dt, Gravity);

            float distToFloor = floorTop - player.Body.Bounds.Bottom;
            bool invuln = player.Combat.IsInvulnerable(player.Frame);

            // A successful tech: invulnerable + bounced upward (vy < 0) near the floor.
            if (invuln && player.Body.Velocity.Y < 0f && distToFloor < 80f)
            {
                sawTechBounce = true;
                if (!player.Combat.StunActive) stunClearedByTech = true;
            }
            // Outside the window: invuln granted while still high up would be a bug.
            if (invuln && distToFloor > 120f) techedWhileHigh = true;
        }

        Assert.False(techedWhileHigh, "Tech fired while high above the floor (outside the tech window).");
        Assert.True(sawTechBounce, "Tech never produced an invuln upward bounce near the ground.");
        Assert.True(stunClearedByTech, "Tech did not clear the launch (StunActive).");
    }
}
