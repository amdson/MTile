using Microsoft.Xna.Framework;

namespace MTile;

// Tag identifying an entity's concrete type for snapshot rehydration. The sim is
// polymorphic over Entity, so a restore that must *recreate* a despawned entity
// needs to know which class to construct. (Restoring into a still-live entity uses
// the virtual RestoreInto and doesn't consult this.)
public enum EntityKind
{
    Generic,        // balloons / balls (the base Entity class, parametrized by ctor)
    Stalker,
    Turret,
    Bullet,
    EnergyBall,
    StickyGrenade,
    LobbedArea,
}

// Flat, plain-data snapshot of any entity (roadmap goal 4 / Plans/STATE_SNAPSHOT_PLAN
// §G). One superset struct covers every entity type — fields are unioned across types
// (an AIState int reused by Stalker/Turret, a HitId reused by every projectile), the
// same way ActionVars/MovementVars union the FSM fields. Because it's a value type,
// capture/restore of the *value* is a struct copy; the only reference members are
// immutable/shared (Polygon shape, Impact config, deep-copied Maintained contacts in
// Body), so it carries no aliases into live sim objects.
public struct EntitySnapshot
{
    public EntityKind Kind;
    public int        Id;

    public BodyState Body;       // pose + kinematics + maintained contacts (§B)

    // Entity base
    public float   Health;
    public float   MaxHealth;
    public float   Mass;
    public float   GravityScale;
    public Color   Color;
    public Faction Faction;

    // Immutable construction inputs — needed to rebuild a Generic entity's body
    // (balloons/balls have no dedicated subclass to reconstruct their shape/impact).
    public Polygon      Polygon;
    public ImpactDamage Impact;

    // Projectile base
    public float Age;
    public float Lifetime;

    // AI (Stalker / Turret) — AIState is the concrete enum cast to int.
    public int     AIState;
    public float   StateTime;
    public int     Facing;
    public Vector2 Aim;

    // Projectile subtype state
    public int                 HitId;
    public bool                Stuck;       // StickyGrenade
    public float               StuckSince;  // StickyGrenade
    public bool                Exploded;    // StickyGrenade
    public bool                Detonated;   // LobbedArea
    public int                 Budget;      // LobbedArea
    public TileType            TileType;    // LobbedArea
    public EruptionPlannerMode Mode;        // LobbedArea

    // Rebuild a live entity from this snapshot. Used by Simulation.Restore for
    // entities that were alive at snapshot time but have since despawned. The
    // gameplay ctor runs (creates the sprite, sets immutable Impact/config), then
    // RestoreInto overwrites every dynamic field — including body pose, faction,
    // and the private AI/projectile state — so the post-ctor transients don't leak.
    // hitIds is only consulted by Bullet (it holds the allocator for re-deflects);
    // the allocator's counter is restored separately by SimSnapshot.
    public readonly Entity Rehydrate(HitIdAllocator hitIds)
    {
        Entity e = Kind switch
        {
            EntityKind.Generic       => new Entity(new PhysicsBody(Polygon, Body.Position) { Impact = Impact }, MaxHealth),
            EntityKind.Stalker       => new StalkerEnemy(Body.Position),
            EntityKind.Turret        => new TurretEnemy(Body.Position),
            EntityKind.Bullet        => new BulletProjectile(Body.Position, Body.Velocity, hitIds),
            EntityKind.EnergyBall    => new EnergyBallProjectile(Body.Position, Body.Velocity, HitId, Faction),
            EntityKind.StickyGrenade => new StickyGrenadeProjectile(Body.Position, Body.Velocity, HitId, Faction),
            EntityKind.LobbedArea    => new LobbedAreaProjectile(Body.Position, Body.Velocity, Budget, TileType, Mode, HitId, Faction),
            _                        => new Entity(new PhysicsBody(Polygon, Body.Position) { Impact = Impact }, MaxHealth),
        };
        e.RestoreInto(in this);
        return e;
    }
}
