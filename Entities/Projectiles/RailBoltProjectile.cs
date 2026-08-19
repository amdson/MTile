using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Bastion ordnance — the payload of EnemyRailShotAction. Everything about it is
// the opposite of its launcher's windup: the emplacement telegraphs for well
// over a second, then this crosses the room in a handful of frames.
//
// Two properties distinguish it from EnergyBallProjectile:
//
//   1. It damages TILES (HitTargets.All). A bolt that only threatened the player
//      would make cover a free win; a bolt that eats cover makes the gallery a
//      countdown — hide, but know the pillar you're behind has three shots left
//      in it. Damage is set above Stone's MaxHP so each pass clears a cell
//      outright rather than leaving a half-chewed wall.
//
//   2. It carries a finite penetration budget instead of a terrain-collision
//      death. Killing it on first contact would make one tile of dirt a perfect
//      shield; letting it fly forever would let a single shot trench the whole
//      level. `Budget` counts how many times the solver may halt it against
//      terrain before it gives out, so the tunnel it bores is bounded and
//      readable — and it rides the existing EntityData.Budget snapshot slot,
//      so this costs the snapshot nothing.
//
// Faction.Enemy, so it hurts the player and NOT the Bastion that fired it — and
// so a second Bastion's bolt can't friendly-fire the first.
public class RailBoltProjectile : Projectile
{
    // Fast enough that dodging is a matter of not being on the line when it
    // fires, rather than of reacting to the bolt itself. That's deliberate: the
    // windup is the counterplay, the flight is the consequence.
    private const float Speed              = 1500f;
    private const float LifeSeconds        = 0.85f;
    // Above Stone's MaxHP (2.0) so one pass clears a cell. Also the percent
    // contribution on an entity hit — heavy, matched to the telegraph's length.
    private const float DamagePerFrame     = 2.6f;
    private const float HitboxHalfSize     = 6f;
    private const float CollisionStopSpeed = 200f;   // high: the bolt is fast, "slowed" means "blocked"
    private const float ArmDelay           = 0.03f;
    // Launches the player hard along the flight line. vs player Mass 2.5 →
    // ~380 px/s, i.e. a real displacement that can throw them into geometry
    // (which is where the actual HP loss comes from — see CrushDamage).
    private const float KnockbackImpulse   = 950f;
    // How many terrain halts the bolt survives. 3 ≈ a pillar's worth.
    private const int   DefaultBudget      = 3;

    private readonly int _hitId;
    private int _budget;

    public override EntityKind Kind => EntityKind.RailBolt;

    public RailBoltProjectile(Vector2 pos, Vector2 dir, int hitId, Faction owner, int budget = DefaultBudget)
        : base(new PhysicsBody(Polygon.CreateRegular(4f, 4), pos), health: 0.1f, lifetime: LifeSeconds, owner: owner)
    {
        _hitId  = hitId;
        _budget = budget;
        if (dir.LengthSquared() < 1e-4f) dir = Vector2.UnitX;
        dir.Normalize();
        Body.Velocity = dir * Speed;
        // Reuse the energy-ball impact tuning: its low BreakThreshold is exactly
        // the "keep going once a cell gives way" behaviour the penetration
        // budget is metering. Without an Impact config the solver would pin the
        // bolt against the first cell and the budget would drain in place.
        Body.Impact  = ImpactProfiles.Build(ImpactProfiles.EnergyBall);
        Mass         = 0.6f;
        GravityScale = 0f;
        Color        = new Color(255, 120, 60);
        Sprite       = Sprites.Bullet(4f);
    }

    // _hitId is immutable and _budget is live state; both round-trip so a
    // mid-flight snapshot restores a bolt with the right amount of wall left in
    // it. Budget shares EntityData.Budget with LobbedArea — same slot, disjoint
    // kinds, so there's no collision.
    protected override void WriteState(ref EntityData s)
    {
        base.WriteState(ref s);
        s.HitId  = _hitId;
        s.Budget = _budget;
    }

    protected override void ReadState(in EntityData s)
    {
        base.ReadState(in s);
        _budget = s.Budget;
    }

    protected override void ProjectileUpdate(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        // Velocity-magnitude collision detect, same trick BulletProjectile uses —
        // but instead of dying on the first halt we spend a point of budget and
        // let this frame's hitbox chew the cell that stopped us. The solver's
        // break-through path then carries the bolt into the gap next frame.
        if (Age >= ArmDelay && Body.Velocity.LengthSquared() < CollisionStopSpeed * CollisionStopSpeed)
        {
            if (--_budget <= 0)
            {
                // Spend the last frame publishing so the bolt still breaks the
                // cell it died against — otherwise a bolt with 1 budget left
                // reads as fizzling out for no reason.
                PublishBolt(hitboxes);
                Health = 0f;
                return;
            }
        }

        PublishBolt(hitboxes);
    }

    private void PublishBolt(HitboxWorld hitboxes)
    {
        var p = Body.Position;
        var region = new BoundingBox(
            p.X - HitboxHalfSize, p.Y - HitboxHalfSize,
            p.X + HitboxHalfSize, p.Y + HitboxHalfSize);

        Vector2 vel = Body.Velocity;
        Vector2 dir = vel.LengthSquared() > 0.01f ? Vector2.Normalize(vel) : Vector2.UnitX;
        hitboxes?.Publish(new Hitbox(
            region, _hitId, DamagePerFrame,
            dir * KnockbackImpulse,
            Faction, Id, Color,
            // All, not EntitiesOnly — this is the whole point of the bolt.
            targets: HitTargets.All));
    }
}
