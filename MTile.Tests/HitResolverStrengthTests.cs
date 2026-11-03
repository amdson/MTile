using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests;

// The two strength numbers combat keys on (2026-11-02): the victim's dv (stun, hitstun,
// armor) and HitResolver.ImpactStrength of the delivered impulse (hitstop, both sides).
public class HitResolverStrengthTests
{
    private static Hitbox Strike(float speed, float strikeMass, float e) => new(
        new BoundingBox(0, 0, 1, 1), hitId: 1, damage: 0f, Vector2.UnitX,
        Faction.Player1, default,
        mode: KnockbackMode.Collision, strikeDir: Vector2.UnitX,
        strikeVelocity: Vector2.UnitX * speed, strikeMass: strikeMass, restitution: e);

    [Fact]
    public void PlayerVictim_ImpactStrengthEqualsDv()
    {
        var res = HitResolver.Resolve(Strike(650f, 2.5f, 0.5f), HitResolver.ReferenceMass, Vector2.Zero);
        Assert.Equal(res.TargetDeltaV.Length(), HitResolver.ImpactStrength(res.Impulse), 2);
    }

    [Fact]
    public void HeavierTarget_MovesLess_ButHitsHarder()
    {
        var hit   = Strike(400f, 20f, 0.1f);
        var light = HitResolver.Resolve(hit, 2.5f, Vector2.Zero);
        var heavy = HitResolver.Resolve(hit, 40f,  Vector2.Zero);
        Assert.True(heavy.TargetDeltaV.Length() < light.TargetDeltaV.Length());
        Assert.True(HitResolver.ImpactStrength(heavy.Impulse) > HitResolver.ImpactStrength(light.Impulse));
    }

    [Fact]
    public void HeavyStriker_CapsTheLaunchNear_OnePlusE_TimesSpeed()
    {
        const float speed = 380f, e = 0.1f;
        var res = HitResolver.Resolve(Strike(speed, 1e4f, e), 2.5f, Vector2.Zero);
        Assert.InRange(res.TargetDeltaV.Length(), (1f + e) * speed * 0.99f, (1f + e) * speed);

        // A target already moving away at the striker's speed gains (almost) nothing.
        var fleeing = HitResolver.Resolve(Strike(speed, 1e4f, e), 2.5f, Vector2.UnitX * speed);
        Assert.True(fleeing.TargetDeltaV.Length() < 1f);
    }
}
