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
    private const float Speed              = 500f;
    private const float LifeSeconds        = 1.2f;
    private const float DamagePerFrame     = 1.0f;
    private const float HitboxHalfSize     = 5f;
    private const float CollisionStopSpeed = 30f;
    private const float ArmDelay           = 0.03f;
    // Sits in line with the brute's melee/lunge knockback (460/540) so a
    // ranged punish reads like a thrown body-check on hit rather than a poke.
    // vs player Mass 2.5 → 216 px/s; vs Brute Mass 1.2 → 450 px/s; vs Stalker
    // Mass 1.0 → 540 px/s.
    private const float KnockbackImpulse   = 540f;

    private readonly int _hitId;

    public override EntityKind Kind => EntityKind.EnergyBall;

    // _hitId is immutable (set once at construction); WriteState records it so
    // Rehydrate can pass it back through the ctor. No ReadState override needed —
    // the base body/stat restore is sufficient for a live-entity restore.
    protected override void WriteState(ref EntitySnapshot s)
    {
        base.WriteState(ref s);
        s.HitId = _hitId;
    }

    public EnergyBallProjectile(Vector2 pos, Vector2 dir, int hitId, Faction owner)
        : base(new PhysicsBody(Polygon.CreateRegular(4f, 6), pos), health: 0.1f, lifetime: LifeSeconds, owner: owner)
    {
        _hitId = hitId;
        if (dir.LengthSquared() < 1e-4f) dir = Vector2.UnitX;
        dir.Normalize();
        Body.Velocity = dir * Speed;
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
            region, _hitId, DamagePerFrame,
            dir * KnockbackImpulse,
            Faction, Id, Color,
            targets: HitTargets.EntitiesOnly));
    }
}
