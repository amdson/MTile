namespace MTile;

// Tag identifying an entity's concrete type for snapshot rehydration. The sim is
// polymorphic over Entity, so a restore that must *recreate* a despawned entity
// (one alive at the snapshot frame but gone now) needs to know which class to
// construct. Stored on each entity's EntityData component; consumed by
// EntityFactory.Rehydrate. (Restoring into a still-live entity uses the virtual
// RestoreState and doesn't consult this.)
public enum EntityKind
{
    Generic,        // balloons / balls (the base Entity class, parametrized by ctor)
    Stalker,
    Turret,
    Bullet,
    EnergyBall,
    StickyGrenade,
    LobbedArea,
    Brute,          // MVP EnemyEntity subtype (see Plans/ENEMY_CAPABILITY_FRAMEWORK.md)
    PracticeBall,   // juggling target — breaks on tile contact, respawns at its spawn point

    // Factory-built enemy variants. Each blueprint registered with
    // EnemyFactory owns its own EntityKind so Rehydrate can dispatch to it.
    // Add new variants here as you draft new enemy types — names are the
    // single source of truth for snapshot identity across hosts and replays.
    Skirmisher,
    Bombardier,
}
