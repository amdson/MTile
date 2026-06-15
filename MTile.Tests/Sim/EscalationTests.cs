using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// COMBAT_FEEL_PLAN Phase 5 — escalation arc.
//
// Direct combat hits no longer chip HP; they raise a monotonic DamagePercent and
// apply knockback scaled by that percent. HP is a fast-regenerating pool whose only
// loss path is hard impacts into terrain (crush). KO (HP→0) resets the percent.
public class EscalationTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 30f;
    private static readonly Vector2 Gravity = new(0f, 600f);

    private static ChunkMap FlatGround() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    private static Hitbox MakeHit(PhysicsBody victimBody, Vector2 knockback) =>
        new Hitbox(victimBody.Bounds, hitId: 1, damage: 0.5f, knockbackImpulse: knockback,
                   owner: Faction.Player2, source: new EntityId(99), debugColor: Color.White);

    // Each connecting hit adds to DamagePercent, and it only ever goes up.
    [Fact]
    public void Percent_RisesMonotonically_WithRepeatedHits()
    {
        var p = new PlayerCharacter(new Vector2(40f, 20f));
        var hb = new Hurtbox(p.Body.Bounds, Faction.Player1, p.Id);

        float last = p.Combat.DamagePercent;
        var seen = new List<float>();
        for (int i = 0; i < 5; i++)
        {
            p.OnHit(MakeHit(p.Body, new Vector2(120f, 0f)), hb);
            seen.Add(p.Combat.DamagePercent);
            Assert.True(p.Combat.DamagePercent > last, "percent must strictly increase per hit");
            last = p.Combat.DamagePercent;
        }
        output.WriteLine($"percent series: {string.Join(",", seen)}");
    }

    // The same knockback impulse launches a high-percent victim much harder than a
    // fresh one — that's the escalation (combo at low %, launch at high %).
    [Fact]
    public void Knockback_ScalesWithPercent()
    {
        var knockback = new Vector2(300f, 0f);

        var low = new PlayerCharacter(new Vector2(40f, 20f));
        var lowHb = new Hurtbox(low.Body.Bounds, Faction.Player1, low.Id);
        low.Body.Velocity = Vector2.Zero;
        low.OnHit(MakeHit(low.Body, knockback), lowHb);    // ~0% before the hit
        float dvLow = low.Body.Velocity.X;

        var high = new PlayerCharacter(new Vector2(40f, 20f));
        var highHb = new Hurtbox(high.Body.Bounds, Faction.Player1, high.Id);
        high.Combat.DamagePercent = 200f;                  // pre-loaded high percent
        high.Body.Velocity = Vector2.Zero;
        high.OnHit(MakeHit(high.Body, knockback), highHb);
        float dvHigh = high.Body.Velocity.X;

        output.WriteLine($"dvLow={dvLow:F1}, dvHigh={dvHigh:F1}, ratio={dvHigh / dvLow:F2}");
        Assert.True(dvHigh > dvLow * 2.5f,
            $"High-percent knockback should dwarf low-percent ({dvHigh:F1} vs {dvLow:F1}).");
    }

    // Direct hits leave HP untouched (only impacts into terrain hurt) — and HP
    // regenerates back up over time.
    [Fact]
    public void DirectHits_DontChipHp_AndHpRegens()
    {
        var p = new PlayerCharacter(new Vector2(40f, 20f));
        var hb = new Hurtbox(p.Body.Bounds, Faction.Player1, p.Id);
        float full = p.Health;

        for (int i = 0; i < 6; i++) p.OnHit(MakeHit(p.Body, new Vector2(120f, 0f)), hb);
        Assert.Equal(full, p.Health);   // direct hits never reduce HP now

        // Manually dip HP and confirm it regenerates while stepping un-damaged on flat
        // ground (no crush — the regen-delay anchor is far in the past).
        var terrain = FlatGround();
        var bodies = new List<PhysicsBody> { p.Body };
        var ctrl = new Controller();
        var hbx = new HitboxWorld(); var hux = new HurtboxWorld();
        p.Health = 1.0f;
        for (int f = 0; f < 40; f++)
        {
            ctrl.InjectInput(default);
            terrain.TickSprouts(Dt);
            p.Update(ctrl, terrain, hbx, hux, Dt);
            PhysicsWorld.StepSwept(bodies, terrain, Dt, Gravity);
        }
        output.WriteLine($"HP after regen window: {p.Health}");
        Assert.True(p.Health > 1.0f, "HP should regenerate after the delay.");
        Assert.True(p.Health <= p.MaxHealth + 1e-4f, "HP must not overshoot MaxHealth.");
    }

    // KO/respawn is the only thing that clears the percent.
    [Fact]
    public void Respawn_ResetsPercent()
    {
        var p = new PlayerCharacter(new Vector2(40f, 20f));
        p.Combat.DamagePercent = 150f;
        p.Health = 0.1f;
        p.Respawn(new Vector2(40f, 20f));
        Assert.Equal(0f, p.Combat.DamagePercent);
        Assert.Equal(p.MaxHealth, p.Health);
    }
}
