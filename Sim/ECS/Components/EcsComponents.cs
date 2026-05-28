using Microsoft.Xna.Framework;

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

// ── Snapshotted value components (Plans/ECS_MIGRATION_PLAN.md, Phases 4-6) ────────
// Unlike the live-only refs above, these hold pure value data and ARE captured by the
// World snapshot. The live OO objects (Entity/PlayerCharacter) remain the behavioral
// homes and authority during a Step; these components mirror their serializable state
// and are synced to/from the objects only at snapshot boundaries (CaptureState /
// RestoreState). This is what makes the World snapshot the single rollback substrate
// while keeping PhysicsBody — and the FSM-bearing entities — as classes.

// A body's pose + kinematics + maintained (hard) contacts. Carried by every physical
// entity (entities now; players in Phase 5). The Maintained contact array holds class
// refs that are deep-copied in place, so its store registers a Cloner (see
// World.SetCloner + BodyState.DeepCopy) — a shallow array copy would alias the live
// body's contacts into the snapshot.
public struct BodyStateComp { public BodyState State; }

// Everything an Entity needs snapshotted EXCEPT its body pose (BodyStateComp) and its
// EntityId (the World owns identity). Fields are unioned across entity types exactly
// like the old EntitySnapshot — an AIState int reused by Stalker/Turret, a HitId reused
// by every projectile — so one component covers the whole zoo. Kind is the rehydration
// discriminant (a despawned entity restored at an earlier frame must know which class
// to reconstruct); Polygon/Impact are the immutable construction inputs a Generic
// entity needs to rebuild its body. The subtype WriteState/ReadState hooks marshal
// their own fields into/out of this struct (same field names the old EntitySnapshot
// had, so those hooks are unchanged save the parameter type).
public struct EntityData
{
    public EntityKind Kind;

    // Entity base
    public float   Health;
    public float   MaxHealth;
    public float   Mass;
    public float   GravityScale;
    public Color   Color;
    public Faction Faction;

    // Immutable construction inputs (rebuild a Generic entity's body on rehydrate).
    public Polygon      Polygon;
    public ImpactDamage Impact;

    // Projectile base
    public float Age;
    public float Lifetime;

    // AI (Stalker / Turret / EnemyEntity movement-FSM index).
    public int     AIState;
    public float   StateTime;
    public int     Facing;
    public Vector2 Aim;

    // EnemyEntity action-FSM (ActionIdx == -1 ⇒ no action active).
    public int   ActionIdx;
    public float ActionTime;
    public int   LockedFacing;

    // Projectile subtype state
    public int                 HitId;
    public bool                Stuck;       // StickyGrenade
    public float               StuckSince;  // StickyGrenade
    public bool                Exploded;    // StickyGrenade
    public bool                Detonated;   // LobbedArea
    public int                 Budget;      // LobbedArea
    public TileType            TileType;    // LobbedArea
    public EruptionPlannerMode Mode;        // LobbedArea
}

// Everything a PlayerCharacter needs snapshotted EXCEPT its body pose (BodyStateComp)
// and EntityId — the player-side analogue of EntityData. Carried by the primary and
// every secondary player. The two FSMs are registry indices (flyweight states built in
// a fixed order, so an index is stable across snapshot/restore); per-activation data
// rides in the MovementVars/ActionVars value structs; the helper objects are
// deep-copied. Because several members are reference types whose state matters
// (history int[]s, the intent array, the cloned abilities, the gesture samples), the
// store registers a Cloner (DeepCopy) so capture/restore never alias the live player.
public struct PlayerData
{
    public float Health;
    public float HitInvulnRemaining;
    public int   LastCrushFrame;
    public int   Frame;

    // FSM current selection + history rings, as registry indices.
    public int   StateIndex;
    public int   ActionIndex;
    public int[] StateHistory;
    public int[] ActionHistory;
    public int   HistoryHead;
    public int   ActionHistoryHead;

    // Per-activation FSM data (pure value structs).
    public MovementVars MoveVars;
    public ActionVars   ActionVars;

    // Helper objects (deep-copied — see DeepCopy).
    public PlayerAbilityState   Abilities;
    public InputParserState     Parser;    // pure value struct
    public ActionIntent[]       Intents;
    public EruptionGestureState Eruption;  // holds a PathSample[]

    // Player-local selections.
    public TileType            ActiveBlockType;
    public EruptionPlannerMode EruptionMode;
    public bool                WasPDown;

    // Deep-copy the reference members so a captured/restored PlayerData never shares
    // mutable state with the live player or with another (repeated-restore) copy. Value
    // fields — including the pure-value InputParserState/MoveVars/ActionVars — copy with
    // the struct itself.
    public readonly PlayerData DeepCopy()
    {
        var c = this;
        c.StateHistory  = (int[])StateHistory?.Clone();
        c.ActionHistory = (int[])ActionHistory?.Clone();
        c.Intents       = (ActionIntent[])Intents?.Clone();
        c.Abilities     = Abilities?.Clone();
        if (Eruption.Samples != null)
        {
            var e = Eruption;
            e.Samples = (PathSample[])Eruption.Samples.Clone();
            c.Eruption = e;
        }
        return c;
    }
}
