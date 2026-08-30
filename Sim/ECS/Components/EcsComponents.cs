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

    // Render-only hit stamp (Entity.LastHitId) — snapshotted so a rollback replay
    // reproduces the same stamp and the white flash doesn't re-fire.
    public int LastHitId;

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
    public TileType            TileType;    // LobbedArea + MassBall + PullPoint (orb material)
    public float               BuildMass;   // MassBall — remaining mass to leak

    // PullPointEntity + the ball it spawns (Plans/BLOCK_THROW_PLAN.md). The peel group
    // itself is the sparse PeelGroupComp. LinkedId is the point→ball / ball→point
    // cross-reference (an id, never an object — objects are replaced on rehydrate).
    public bool     Driven;       // point: the owning BlockGrabAction still drives it
    public Vector2  TargetPos;    // point: action-written each driven frame (kernel / spring endpoint)
    public Vector2  OwnerPos;     // point: action-written each driven frame (reach origin)
    public float    HandoffTime;  // point: seconds since the action released it
    public EntityId LinkedId;     // point: its ball; ball: its point
    public bool     Tracking;     // ball: still following the point (gravity off, no hurtbox)
    public int      HarvestBlocks;// ball: blocks at break-out (Budget is what's left)
    public float    CarryTime;    // ball: seconds held while the point was driven (dissipation clock)
    public float    ChaseTime;    // ball: seconds chasing a released point (detach cap)
}

// Everything a PlayerCharacter needs snapshotted EXCEPT its body pose (BodyStateComp)
// and EntityId — the player-side analogue of EntityData. Carried by the primary and
// every secondary player. The two FSMs are registry indices (flyweight states built in
// a fixed order, so an index is stable across snapshot/restore); per-activation data
// rides in the MovementVars/ActionVars value structs; the helper objects are
// deep-copied. Because several members are reference types whose state matters
// (history int[]s, the intent array, the cloned abilities), the
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
    public PlayerAbilityState Abilities;
    public InputParserState   Parser;    // pure value struct
    public ActionIntent[]     Intents;

    // Player-local selection.
    public TileType ActiveBlockType;

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
        return c;
    }
}
