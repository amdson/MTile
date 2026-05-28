using Microsoft.Xna.Framework;

namespace MTile;

// Turret round. Flies at constant velocity (gravity-free), publishes a small
// hitbox each frame so it damages anything it overlaps mid-flight, and self-
// destructs on three triggers:
//   1. Lifetime expires (range limit, handled by Projectile base).
//   2. The physics solver has effectively halted it (collision with terrain).
//   3. It hits something the player can't deflect (slash-faction mismatch).
//
// Deflection: if a Player-faction hitbox lands on the bullet, it doesn't die.
// It reorients along the slash's knockback vector, gets a speed boost, and
// flips its own Faction to Player — the same hitbox the bullet publishes per
// frame will then read as Player.Owner, hurting enemies instead of the player.
public class BulletProjectile : Projectile
{
    private const float LifeSeconds        = 1.4f;
    private const float DamagePerFrame     = 0.5f;
    private const float HitboxHalfSize     = 4f;
    private const float CollisionStopSpeed = 30f;        // |v| below this = "we hit something"
    private const float KnockbackImpulse   = 1200f;      // shoves the player hard on hit
    private const float ArmDelay           = 0.04f;      // skip stop-check at t=0 (just spawned)
    private const float DeflectSpeed       = 520f;       // post-deflect bullet speed

    // Held (not just an int) because a deflect re-mints a fresh HitId from OnHit,
    // which has no spawner reference of its own.
    private readonly HitIdAllocator _hitIds;
    private int _hitId;

    public override EntityKind Kind => EntityKind.Bullet;

    // _hitId is mutable here (a deflect re-mints it), so it round-trips through the
    // snapshot. _hitIds is the shared allocator ref — supplied to Rehydrate via the
    // ctor, never copied as data.
    protected override void WriteState(ref EntitySnapshot s)
    {
        base.WriteState(ref s);
        s.HitId = _hitId;
    }

    protected override void ReadState(in EntitySnapshot s)
    {
        base.ReadState(in s);
        _hitId = s.HitId;
    }

    public BulletProjectile(Vector2 pos, Vector2 velocity, HitIdAllocator hitIds)
        : base(new PhysicsBody(Polygon.CreateRegular(3f, 6), pos), health: 0.1f, lifetime: LifeSeconds, owner: Faction.Enemy)
    {
        _hitIds = hitIds;
        _hitId  = hitIds.Next();
        Body.Velocity = velocity;
        Mass          = 0.4f;
        GravityScale  = 0f;
        Color         = Color.OrangeRed;
        Sprite        = Sprites.Bullet(3f);
    }

    public override void OnHit(in Hitbox hit, in Hurtbox myHurtbox)
    {
        // Player slash/stab/pulse → deflect rather than absorb. Direction comes
        // from the hitbox's KnockbackImpulse (the slash's swing vector / stab
        // forward / pulse radial), so the bullet flies the way the player
        // swung. Speed is reset to a fixed value so a glancing slash still
        // produces a clean fast deflect.
        if (Factions.IsPlayer(hit.Owner))
        {
            Vector2 dir = hit.KnockbackImpulse;
            if (dir.LengthSquared() < 0.01f) dir = -Body.Velocity;
            if (dir.LengthSquared() < 0.01f) dir = new Vector2(1f, 0f);
            dir.Normalize();

            Body.Velocity = dir * DeflectSpeed;
            // Inherit the deflecting player's faction: the bounced bullet now hurts
            // the OTHER player + enemies, but not the deflector (self-immune).
            Faction       = hit.Owner;
            Color         = Color.Cyan;
            Age           = 0f;
            // Fresh HitId so the (HitId,Target) dedupe in CombatSystem treats
            // post-deflect overlaps as a new attack — without this, any enemy
            // the bullet had already brushed pre-deflect would be immune.
            _hitId = _hitIds.Next();
            return;
        }

        // Anything else (a tile crash, a stray friendly pulse) kills the bullet.
        Health = 0f;
    }

    protected override void ProjectileUpdate(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        // Collision-detect via velocity magnitude — the chunk solver halts the
        // body when it runs into a tile, so a fully-stopped bullet has hit
        // something solid. Skip the check briefly after spawn so the muzzle
        // velocity has time to register.
        if (Age >= ArmDelay && Body.Velocity.LengthSquared() < CollisionStopSpeed * CollisionStopSpeed)
        {
            Health = 0f;
            return;
        }

        // Publish a small offensive hitbox at the bullet's current position. The
        // CombatSystem dedupe (HitId,Target) ensures a bullet damages each entity
        // at most once across its lifetime, so even though the bullet keeps
        // publishing during its short flight, an overlapped target only takes
        // damage once. Owner = this.Faction so a deflected bullet hits enemies
        // (and a live one hits the player).
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
