using System;
using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests;

// HitResolver — the pure hit → momentum function (Plans/HIT_MOMENTUM_PLAN.md).
// Impulse mode must reproduce the legacy `Velocity += impulse/mass` exactly;
// Collision mode is checked in the three mass-ratio regimes that define the
// intended feel (ping-pong / equal-mass trade / immovable wall).
public class HitResolverTests
{
    private const float PlayerMass = 2.5f;

    private static Hitbox ImpulseHit(Vector2 impulse) =>
        new Hitbox(new BoundingBox(0, 0, 16, 16), hitId: 1, damage: 0f,
                   knockbackImpulse: impulse, owner: Faction.Player1,
                   source: new EntityId(1));

    private static Hitbox CollisionHit(Vector2 dir, float strikeSpeed, Vector2 attackerVel,
                                       float strikeMass = PlayerMass, float e = 0.5f,
                                       float minLaunch = 0f) =>
        new Hitbox(new BoundingBox(0, 0, 16, 16), hitId: 1, damage: 0f,
                   knockbackImpulse: Vector2.Zero, owner: Faction.Player1,
                   source: new EntityId(1),
                   mode: KnockbackMode.Collision,
                   strikeDir: dir, strikeVelocity: attackerVel + dir * strikeSpeed,
                   strikeMass: strikeMass, restitution: e, minLaunch: minLaunch);

    // ── Impulse mode: exact legacy parity ───────────────────────────────────────

    [Fact]
    public void Impulse_MatchesLegacyDivision_AndEchoesAuthoredImpulse()
    {
        var hit = ImpulseHit(new Vector2(200f, -100f));
        var res = HitResolver.Resolve(in hit, targetMass: 0.5f, targetVelocity: new Vector2(300f, 0f));

        Assert.Equal(new Vector2(400f, -200f), res.TargetDeltaV);          // impulse / mass
        Assert.Equal(new Vector2(200f, -100f), res.Impulse);               // recoil sees authored vector
        Assert.Equal(hit.KnockbackImpulse.Length(), res.Strength, 3);
    }

    [Fact]
    public void Impulse_ScaleMultipliesDeltaVAndStrength_NotRecoil()
    {
        var hit = ImpulseHit(new Vector2(100f, 0f));
        var res = HitResolver.Resolve(in hit, 2f, Vector2.Zero, scale: 1.5f);

        Assert.Equal(75f, res.TargetDeltaV.X, 3);      // 100 * 1.5 / 2
        Assert.Equal(100f, res.Impulse.X, 3);          // recoil unscaled, as CombatSystem did
        Assert.Equal(150f, res.Strength, 3);           // hitstun sees the scaled magnitude
    }

    [Fact]
    public void Impulse_ImmovableTarget_NoDeltaV()
    {
        var hit = ImpulseHit(new Vector2(200f, 0f));
        var res = HitResolver.Resolve(in hit, targetMass: 0f, targetVelocity: Vector2.Zero);
        Assert.Equal(Vector2.Zero, res.TargetDeltaV);
    }

    // ── Collision mode: the three regimes ───────────────────────────────────────

    // Light target (ping-pong ball): leaves with ~the closing speed times (1+e) —
    // strike speed PLUS its own reflected incoming speed.
    [Fact]
    public void Collision_LightTarget_PingsOffWithReflectedApproach()
    {
        var n = new Vector2(1f, 0f);
        // Ball flying INTO the swing at 100; striker swings at 300.  u = 400.
        var hit = CollisionHit(n, strikeSpeed: 300f, attackerVel: Vector2.Zero, e: 0.5f);
        var res = HitResolver.Resolve(in hit, targetMass: 0.1f, targetVelocity: new Vector2(-100f, 0f));

        // mu ≈ m_t for m_t << m_s  ⇒  Δv ≈ (1+e)·u = 600, well above strike speed.
        float mu = PlayerMass * 0.1f / (PlayerMass + 0.1f);
        float expected = 1.5f * mu * 400f / 0.1f;
        Assert.Equal(expected, res.TargetDeltaV.X, 2);
        Assert.True(res.TargetDeltaV.X > 300f, "light target must leave faster than the strike itself");
        Assert.Equal(400f, res.Strength, 3);           // Strength = closing speed u
    }

    // Equal mass (mirror player): bounces away, but at (1+e)/2 of the closing
    // speed — solid but not ping-pong.
    [Fact]
    public void Collision_EqualMass_BouncesAtHalfClosingSpeedScale()
    {
        var n = new Vector2(1f, 0f);
        var hit = CollisionHit(n, strikeSpeed: 300f, attackerVel: Vector2.Zero, e: 0.5f);
        var res = HitResolver.Resolve(in hit, targetMass: PlayerMass, targetVelocity: Vector2.Zero);

        Assert.Equal(1.5f * 300f / 2f, res.TargetDeltaV.X, 2);   // (1+e)·u/2 = 225
        Assert.True(res.TargetDeltaV.X < 300f, "equal mass must NOT exceed the strike speed");
    }

    // Heavy target: barely deflects, and the delivered impulse (the attacker's
    // recoil feed) is LARGER than what an equal-mass hit delivers — the attacker
    // bounces off.
    [Fact]
    public void Collision_HeavyTarget_BarelyMoves_RecoilExceedsEqualMassCase()
    {
        var n = new Vector2(1f, 0f);
        var hit = CollisionHit(n, strikeSpeed: 300f, attackerVel: Vector2.Zero, e: 0.5f);

        var heavy = HitResolver.Resolve(in hit, targetMass: 25f, targetVelocity: Vector2.Zero);
        var equal = HitResolver.Resolve(in hit, targetMass: PlayerMass, targetVelocity: Vector2.Zero);

        Assert.True(heavy.TargetDeltaV.X < 0.2f * equal.TargetDeltaV.X,
            $"heavy target Δv {heavy.TargetDeltaV.X} should be a small fraction of equal-mass {equal.TargetDeltaV.X}");
        Assert.True(heavy.Impulse.X > equal.Impulse.X,
            "hitting something heavy must kick the attacker back harder than an equal-mass trade");
    }

    // A falling boulder keeps falling: the hit is horizontal, gravity-axis
    // velocity is tangential to n and must be untouched.
    [Fact]
    public void Collision_TangentialVelocityUntouched()
    {
        var n = new Vector2(1f, 0f);
        var hit = CollisionHit(n, strikeSpeed: 300f, attackerVel: Vector2.Zero);
        var res = HitResolver.Resolve(in hit, targetMass: 25f, targetVelocity: new Vector2(0f, 500f));
        Assert.Equal(0f, res.TargetDeltaV.Y, 3);
    }

    // Attacker velocity feeds the strike: a sprinting slash hits harder than a
    // standing one by exactly the sprint speed along n.
    [Fact]
    public void Collision_AttackerVelocityAddsToClosingSpeed()
    {
        var n = new Vector2(1f, 0f);
        var standing  = CollisionHit(n, 300f, attackerVel: Vector2.Zero);
        var sprinting = CollisionHit(n, 300f, attackerVel: new Vector2(150f, 0f));

        var rs = HitResolver.Resolve(in standing,  1f, Vector2.Zero);
        var rp = HitResolver.Resolve(in sprinting, 1f, Vector2.Zero);
        Assert.Equal(rp.Strength, rs.Strength + 150f, 2);
        Assert.True(rp.TargetDeltaV.X > rs.TargetDeltaV.X);
    }

    // Target fleeing faster than the swing: no negative (pulling) impulse; the
    // MinLaunch floor still produces a visible connect.
    [Fact]
    public void Collision_FleeingTarget_ClampsToZero_MinLaunchFloors()
    {
        var n = new Vector2(1f, 0f);
        var hit = CollisionHit(n, strikeSpeed: 200f, attackerVel: Vector2.Zero, minLaunch: 80f);
        var res = HitResolver.Resolve(in hit, targetMass: 1f, targetVelocity: new Vector2(500f, 0f));

        Assert.Equal(0f, res.Strength, 3);                       // u clamped to 0
        Assert.Equal(Vector2.Zero, res.Impulse);                 // no recoil from a whiff-grade touch
        Assert.Equal(80f, res.TargetDeltaV.X, 3);                // floor still nudges the target
    }

    // Immovable (mass <= 0) target: no Δv, full striker-share recoil.
    [Fact]
    public void Collision_ImmovableTarget_NoDeltaV_FullRecoil()
    {
        var n = new Vector2(0f, -1f);   // upward stab into a ceiling-like target
        var hit = CollisionHit(n, strikeSpeed: 400f, attackerVel: Vector2.Zero, e: 0.5f);
        var res = HitResolver.Resolve(in hit, targetMass: 0f, targetVelocity: Vector2.Zero);

        Assert.Equal(Vector2.Zero, res.TargetDeltaV);
        // J = (1+e)·m_s·u — the m_t → ∞ limit.
        Assert.Equal(-1.5f * PlayerMass * 400f, res.Impulse.Y, 2);
    }

    // ── TileRecoil: the m_t → ∞ surface bounce ──────────────────────────────────

    private static Hitbox TileHit(Vector2 dir, Vector2 strikeVel, float recoilScale,
                                  float minRecoilSpeed = 0f) =>
        new Hitbox(new BoundingBox(0, 0, 16, 16), hitId: 1, damage: 0f,
                   knockbackImpulse: Vector2.Zero, owner: Faction.Player1,
                   source: new EntityId(1),
                   mode: KnockbackMode.Collision, strikeDir: dir,
                   strikeVelocity: strikeVel, strikeMass: PlayerMass,
                   recoilScale: recoilScale, minRecoilSpeed: minRecoilSpeed);

    // Bounce = (1+e_material)·approach·RecoilScale, opposite the strike direction.
    [Fact]
    public void TileRecoil_ReflectsApproachWithMaterialRestitution()
    {
        var n = new Vector2(0f, 1f);   // downward stab into a floor
        var hit = TileHit(n, strikeVel: new Vector2(0f, 400f), recoilScale: 0.5f);
        var recoil = HitResolver.TileRecoil(in hit, restitution: 0.7f);

        Assert.Equal(-1.7f * 400f * 0.5f, recoil.Y, 2);   // -340, upward
        Assert.Equal(0f, recoil.X, 3);
    }

    // Retreating striker (u ≤ 0): nothing, floor included — this is what makes a
    // multi-frame hitbox overlap self-limiting after the first bounce.
    [Fact]
    public void TileRecoil_Retreating_NoRecoil_EvenWithFloor()
    {
        var n = new Vector2(1f, 0f);
        var hit = TileHit(n, strikeVel: new Vector2(-100f, 0f), recoilScale: 0.5f,
                          minRecoilSpeed: 380f);
        Assert.Equal(Vector2.Zero, HitResolver.TileRecoil(in hit, restitution: 0.7f));
    }

    // Slow but positive approach: the floor guarantees the baseline pogo.
    [Fact]
    public void TileRecoil_SlowApproach_FloorGuaranteesPogo()
    {
        var n = new Vector2(0f, 1f);
        var hit = TileHit(n, strikeVel: new Vector2(0f, 50f), recoilScale: 0.25f,
                          minRecoilSpeed: 380f);
        var recoil = HitResolver.TileRecoil(in hit, restitution: 0.7f);
        Assert.Equal(-380f, recoil.Y, 2);   // 1.7·50·0.25 ≈ 21 → floored to 380
    }

    // Determinism sanity: identical inputs give bit-identical outputs.
    [Fact]
    public void Resolve_IsPure()
    {
        var hit = CollisionHit(Vector2.Normalize(new Vector2(1f, -1f)), 313f, new Vector2(42f, -7f));
        var a = HitResolver.Resolve(in hit, 1.3f, new Vector2(-55f, 20f), 1.2f);
        var b = HitResolver.Resolve(in hit, 1.3f, new Vector2(-55f, 20f), 1.2f);
        Assert.Equal(a.TargetDeltaV, b.TargetDeltaV);
        Assert.Equal(a.Impulse, b.Impulse);
        Assert.Equal(a.Strength, b.Strength);
    }
}
