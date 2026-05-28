using Microsoft.Xna.Framework;

namespace MTile;

// Shared infrastructure for entities whose primary behavior is "travel in world
// space and publish hitboxes." Today this is BulletProjectile (turret round);
// soon EnergyBall, StickyGrenade, LobbedAreaBall, etc. all live on top.
//
// Concrete projectiles override Update for per-frame behavior (lifetime, fuse,
// publishing custom hitboxes) but get the basics — owner faction, death-on-
// lifetime, sprite sync — from here. Hitbox publication is NOT shared because
// each projectile shapes its hitbox differently (size, knockback direction,
// targets filter, dedupe HitId).
public abstract class Projectile : Entity
{
    // Counts seconds since spawn. Override Update can use this to gate behavior
    // (arming delay, fuse, lifetime).
    protected float Age;
    // After this many seconds, the projectile dies on its own. Subclasses set
    // in constructor. 0 = no lifetime cap (caller manages death).
    protected float Lifetime;

    protected Projectile(PhysicsBody body, float health, float lifetime, Faction owner)
        : base(body, health)
    {
        Lifetime = lifetime;
        Faction  = owner;
    }

    public sealed override void Update(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        if (IsDead) return;
        Age += dt;
        if (Lifetime > 0f && Age >= Lifetime) { Health = 0f; return; }
        ProjectileUpdate(dt, player, hitboxes, spawner);
    }

    // Per-frame behavior after the base class has aged + lifetime-checked.
    protected abstract void ProjectileUpdate(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner);

    // Snapshot the shared projectile fuse/lifetime alongside the base entity fields.
    // Concrete projectiles chain through these for their own per-type state.
    protected override void WriteState(ref EntityData s)
    {
        base.WriteState(ref s);
        s.Age      = Age;
        s.Lifetime = Lifetime;
    }

    protected override void ReadState(in EntityData s)
    {
        base.ReadState(in s);
        Age      = s.Age;
        Lifetime = s.Lifetime;
    }
}
