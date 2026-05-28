using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Roadmap §4.4 — sticky grenade. Player throws a small ballistic projectile
// that arms a fuse the moment it sticks to terrain (velocity-stalled signal,
// same trick BulletProjectile uses), and explodes after FuseSeconds with a
// radial Pulse-style segment hitbox covering ExplosionRadius. The radial
// segments use HitTargets.All so the explosion damages BOTH entities (knockback +
// damage radiating from the center) and tiles (chips/breaks anything in the
// blast bounds via CombatSystem's tile-damage path).
//
// Why segments-not-one-AABB: a single big AABB would push every entity caught
// inside in one shared direction. Segments give each segment its own outward
// knockback vector, so entities on opposite sides of the blast get pushed away
// from the center — radial feel.
public class StickyGrenadeProjectile : Projectile
{
    private const float ThrowSpeed       = 360f;
    private const float LifeSeconds      = 6.0f;        // hard cap if it never sticks (rolled off the map etc.)
    private const float FuseSeconds      = 1.2f;
    private const float StickStopSpeed   = 28f;         // |v| below this → considered stuck
    private const float ArmDelay         = 0.04f;       // skip stop-check at t=0
    private const float BodyMass         = 0.6f;
    private const float ExplosionRadius  = 56f;         // covers ~3.5 tiles each side
    private const int   ExplosionSegments = 12;
    private const float SegmentHalfSize  = 10f;
    private const float ExplosionKnockback = 700f;
    // Per-frame damage during the (one-frame) explosion. Tile damage scales
    // against TileMaxHP — set near TileMaxHP so a Sand cell pops, Dirt cracks,
    // Stone chips. Entity damage is just this raw value (no per-frame
    // accumulation because the hitbox only lives for one frame).
    private const float ExplosionDamage  = TileDamage.TileMaxHP * 1.2f;

    private readonly int _hitId;

    private bool  _stuck;
    private float _stuckSince;       // Age at time of sticking; explosion fires at _stuckSince + FuseSeconds
    private bool  _exploded;

    public override EntityKind Kind => EntityKind.StickyGrenade;

    // _hitId is immutable (ctor) — recorded for Rehydrate. The fuse flags are
    // mutable per-frame state and round-trip on a live restore too.
    protected override void WriteState(ref EntitySnapshot s)
    {
        base.WriteState(ref s);
        s.HitId      = _hitId;
        s.Stuck      = _stuck;
        s.StuckSince = _stuckSince;
        s.Exploded   = _exploded;
    }

    protected override void ReadState(in EntitySnapshot s)
    {
        base.ReadState(in s);
        _stuck      = s.Stuck;
        _stuckSince = s.StuckSince;
        _exploded   = s.Exploded;
    }

    public StickyGrenadeProjectile(Vector2 pos, Vector2 dir, int hitId, Faction owner)
        : base(new PhysicsBody(Polygon.CreateRegular(5f, 6), pos), health: 0.1f, lifetime: LifeSeconds, owner: owner)
    {
        _hitId = hitId;
        if (dir.LengthSquared() < 1e-4f) dir = Vector2.UnitX;
        dir.Normalize();
        Body.Velocity = dir * ThrowSpeed;
        Mass          = BodyMass;
        GravityScale  = 1f;
        Color         = Color.OliveDrab;
        Sprite        = Sprites.Ball(5f);
    }

    protected override void ProjectileUpdate(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        // Already exploded last frame and the post-explode bookkeeping killed us
        // is the normal path. Guard anyway.
        if (_exploded) { Health = 0f; return; }

        // Phase 1: in flight. Watch for the physics solver halting us (terrain
        // contact) — same velocity-magnitude trick BulletProjectile uses.
        if (!_stuck)
        {
            if (Age >= ArmDelay && Body.Velocity.LengthSquared() < StickStopSpeed * StickStopSpeed)
            {
                _stuck = true;
                _stuckSince = Age;
                // Freeze in place so the grenade stays glued to where it landed.
                Body.Velocity = Vector2.Zero;
                GravityScale  = 0f;
                Color         = Color.LimeGreen;     // visual cue: armed
            }
            return;
        }

        // Phase 2: armed. Wait for fuse to expire.
        if (Age - _stuckSince < FuseSeconds) return;

        // Phase 3: explode. Publish ExplosionSegments segment hitboxes radiating
        // outward from the body's position. CombatSystem will dispatch each
        // segment to BOTH tile damage and entity hurtboxes (HitTargets.All).
        var center = Body.Position;
        if (hitboxes != null)
        {
            for (int i = 0; i < ExplosionSegments; i++)
            {
                float angle = i * MathHelper.TwoPi / ExplosionSegments;
                var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                var segCenter = center + dir * ExplosionRadius;
                var region = new BoundingBox(
                    segCenter.X - SegmentHalfSize, segCenter.Y - SegmentHalfSize,
                    segCenter.X + SegmentHalfSize, segCenter.Y + SegmentHalfSize);
                hitboxes.Publish(new Hitbox(
                    region, _hitId, ExplosionDamage,
                    dir * ExplosionKnockback,
                    Faction, Id, Color.Orange));
            }
        }
        _exploded = true;
        // Note: actual Health = 0 happens on the next frame's entry. Keeps this
        // frame's hitboxes alive in HitboxWorld for CombatSystem.Apply at the
        // end of the tick.
    }
}
