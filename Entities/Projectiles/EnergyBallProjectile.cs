using Microsoft.Xna.Framework;

namespace MTile;

// Roadmap §4.1. Player-spawned ranged projectile, fired by EnergyBallAction
// (Shift+LMB-tap). Flies straight toward the cursor at a fixed speed, publishes
// a small hitbox each frame. Dies on lifetime expiry OR when the physics solver
// halts it (terrain hit) — same "collision via velocity-magnitude" trick
// BulletProjectile uses.
//
// Faction = Player, so the same hitbox the projectile publishes can hurt enemy
// hurtboxes via CombatSystem. Distinct dedupe HitId means each enemy takes
// damage at most once per energy ball.
//
// Roadmap calls for "pierces 1–2 tiles before dying (uses §2 destructive-physics
// machinery)." That falls out for free as soon as Body.Impact is set:
// PhysicsWorld's break-through path bleeds normal velocity, the ball survives
// the contact, then dies on the next collision (or lifetime).
public class EnergyBallProjectile : Projectile
{
    // Public so ActionSpec.Default(ActionKind.Ranged) can quote them: an enemy
    // ranged spec that leaves Speed / Damage at these fires exactly this ball.
    public  const float DefaultSpeed       = 500f;
    public  const float DefaultDamage      = 1.0f;
    private const float LifeSeconds        = 1.2f;
    private const float HitboxHalfSize     = 5f;
    private const float CollisionStopSpeed = 30f;
    private const float ArmDelay           = 0.03f;
    // Sits in line with the creature melee/lunge knockback (250/300) so a ranged
    // punish reads like the same family of hit. vs player Mass 2.5 → 132 px/s;
    // vs Brute Mass 1.2 → 275 px/s; vs Stalker Mass 1.0 → 330 px/s.
    private const float KnockbackImpulse   = 330f;

    private readonly int   _hitId;
    private readonly float _damage;

    public override EntityKind Kind => EntityKind.EnergyBall;

    // _hitId and _damage are immutable (set once at construction); WriteState
    // records them so Rehydrate can pass them back through the ctor. Speed needs
    // no slot — it lives in Body.Velocity, which BodyStateComp already carries. No
    // ReadState override needed: the base body/stat restore covers a live entity.
    protected override void WriteState(ref EntityData s)
    {
        base.WriteState(ref s);
        s.HitId      = _hitId;
        s.ProjDamage = _damage;
    }

    // `speed` / `damage` default to the stock ball. An enemy ranged action passes
    // its ActionSpec's values; the player's EnergyBallAction and Rehydrate pass
    // what they have (Rehydrate: the snapshotted damage, or 0 ⇒ stock).
    public EnergyBallProjectile(Vector2 pos, Vector2 dir, int hitId, Faction owner,
                                float speed = DefaultSpeed, float damage = DefaultDamage)
        : base(new PhysicsBody(Polygon.CreateRegular(4f, 6), pos), health: 0.1f, lifetime: LifeSeconds, owner: owner)
    {
        _hitId  = hitId;
        _damage = damage > 0f ? damage : DefaultDamage;
        if (dir.LengthSquared() < 1e-4f) dir = Vector2.UnitX;
        dir.Normalize();
        Body.Velocity = dir * (speed > 0f ? speed : DefaultSpeed);
        // Impact config so the ball can pierce 1-2 cells before dying — the
        // chunk solver's break-through path keeps it moving once a tile breaks
        // under the impulse threshold. Tuning lives in impact_profiles.json
        // under the "energy_ball" key (default thresholds are tight so a
        // single Stone won't stop it dead but a stack of 3 will).
        Body.Impact = ImpactProfiles.Build(ImpactProfiles.EnergyBall);
        Mass         = 0.5f;
        GravityScale = 0f;
        Color        = Color.LightCyan;
        Sprite       = Sprites.Bullet(4f);
    }

    protected override void ProjectileUpdate(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        if (Age >= ArmDelay && Body.Velocity.LengthSquared() < CollisionStopSpeed * CollisionStopSpeed)
        {
            Health = 0f;
            return;
        }

        var p = Body.Position;
        var region = new BoundingBox(
            p.X - HitboxHalfSize, p.Y - HitboxHalfSize,
            p.X + HitboxHalfSize, p.Y + HitboxHalfSize);

        Vector2 vel = Body.Velocity;
        Vector2 dir = vel.LengthSquared() > 0.01f ? Vector2.Normalize(vel) : Vector2.UnitX;
        hitboxes?.Publish(new Hitbox(
            region, _hitId, _damage,
            dir * KnockbackImpulse,
            Faction, Id, Color,
            targets: HitTargets.EntitiesOnly,
            origin: p));
    }
}
