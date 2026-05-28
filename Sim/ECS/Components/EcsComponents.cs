namespace MTile;

// Live-only components that wrap the existing OO objects so the World can own
// identity + iteration without decomposing them (Plans/ECS_MIGRATION_PLAN.md,
// Phase 2). Each holds a class reference, so these stores are marked live-only on
// the World (never value-snapshotted) and rebuilt from rehydrated entities on
// restore. Decomposition into fine-grained value components is a later phase.

// Every physical entity (players + entities) carries one. PhysicsWorld.StepSwept
// iterates the store of these instead of a List<PhysicsBody>.
public struct PhysicsBodyComponent { public PhysicsBody Body; }

// Non-player hittable/AI entities (enemies, projectiles, props).
public struct EntityRef { public Entity Obj; }

// Player characters (primary + secondaries).
public struct PlayerRef { public PlayerCharacter Obj; }
