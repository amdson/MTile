using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Ground advantages (todo K / L, 2026-11-05):
//   K3  a victim standing on the floor takes reduced knockback - the upward component
//       hardest - so they stagger along the ground instead of popping into the air;
//   L   gravity is scaled down on the way DOWN only (MovementConfig.FallGravityScale):
//       the climb of a jump is untouched, the descent is floatier.
public class CombatGroundAdvantageTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;
    private static readonly Vector2 Gravity = new(0f, 600f);

    private static ChunkMap FlatGround() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    private static float FloorTopY => 8 * Chunk.TileSize;

    // The same Collision-mode hit (an upward-angled stock slash) on a victim standing on
    // the floor vs one hanging in the air. Both are hit on the hit's first frame; the
    // grounded one must be pushed less, and much less upward.
    private static (float vx, float vy) HitOnce(bool grounded)
    {
        float y = grounded ? FloorTopY - PlayerCharacter.Radius : FloorTopY - 120f;
        var victim = new PlayerCharacter(new Vector2(100f, y));
        var bodies = new List<PhysicsBody> { victim.Body };
        var terrain = FlatGround();
        var ctrl = new Controller(); var hb = new HitboxWorld(); var hu = new HurtboxWorld();
        // Settle: a grounded body needs a few frames to land on its stand fold.
        for (int f = 0; f < 20; f++)
        {
            ctrl.InjectInput(default);
            terrain.TickSprouts(Dt);
            victim.Update(ctrl, terrain, hb, hu, Dt);
            PhysicsWorld.StepSwept(bodies, terrain, Dt, Gravity);
        }
        if (!grounded) victim.Body.Velocity = Vector2.Zero;   // hover for the measurement
        var dir = Vector2.Normalize(new Vector2(1f, -0.6f));
        var hit = new Hitbox(victim.Body.Bounds, 7, 0.5f, dir * 60f, Faction.Enemy, EntityId.None,
                             mode: KnockbackMode.Collision, strikeDir: dir,
                             strikeVelocity: dir * 200f, strikeMass: 2.5f, restitution: 0.5f, minLaunch: 100f);
        var hurt = new Hurtbox(victim.Body.Bounds, victim.Faction, victim.Id);
        var before = victim.Body.Velocity;
        victim.OnHit(in hit, in hurt);
        // The knockback lands in the hitstop stash while held; read the velocity that will
        // be handed back (no hitstop here: dv is under the big-hit line).
        var dv = victim.Body.Velocity - before;
        return (dv.X, dv.Y);
    }

    [Fact]
    public void GroundedVictim_TakesLessKnockback_AndBarelyPopsUp()
    {
        var air = HitOnce(grounded: false);
        var gnd = HitOnce(grounded: true);
        output.WriteLine($"air dv=({air.vx:F1},{air.vy:F1}) grounded dv=({gnd.vx:F1},{gnd.vy:F1})");
        Assert.True(air.vx > 0f && air.vy < 0f, "The airborne reference hit should push right and up.");
        Assert.True(gnd.vx < air.vx, "Grounded horizontal knockback should be reduced.");
        Assert.True(gnd.vy > air.vy * 0.6f, "Grounded upward knockback should be cut hard (GroundKnockbackRiseScale).");
    }

    // Descent-only gravity: a body dropped from rest must fall slower than the full-g
    // reference over the same frames, while a body thrown UP must decelerate at full g
    // (the climb is untouched - jump heights stay calibrated).
    [Fact]
    public void FallGravityScale_SlowsTheDescentOnly()
    {
        float scale = MovementConfig.Current.FallGravityScale;
        Assert.True(scale > 0f && scale < 1f, $"FallGravityScale {scale} should be a real descent reduction.");

        float Simulate(float vy0, int frames)
        {
            var player = new PlayerCharacter(new Vector2(100f, FloorTopY - 500f));
            player.Body.Velocity = new Vector2(0f, vy0);
            var bodies = new List<PhysicsBody> { player.Body };
            var terrain = FlatGround();
            var ctrl = new Controller(); var hb = new HitboxWorld(); var hu = new HurtboxWorld();
            for (int f = 0; f < frames; f++)
            {
                ctrl.InjectInput(default);
                terrain.TickSprouts(Dt);
                player.Update(ctrl, terrain, hb, hu, Dt);
                PhysicsWorld.StepSwept(bodies, terrain, Dt, Gravity);
            }
            return player.Body.Velocity.Y;
        }

        const int frames = 20;
        float fullG = Gravity.Y * frames * Dt;                 // 200 px/s at full gravity, drag aside
        float fallVy = Simulate(0f, frames);                   // dropped from rest: descending the whole time
        float riseVy = Simulate(-300f, frames);                // thrown up: climbing the whole time
        output.WriteLine($"after {frames}f: fall vy={fallVy:F1} (full-g {fullG:F0}, scaled {fullG * scale:F0}); rise vy={riseVy:F1} (full-g {-300f + fullG:F0})");
        Assert.InRange(fallVy, fullG * scale * 0.85f, fullG * scale * 1.15f);
        Assert.InRange(riseVy, (-300f + fullG) - 25f, (-300f + fullG) + 25f);
    }
}
