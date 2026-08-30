using Microsoft.Xna.Framework;

namespace MTile;

// MVP enemy framework — base shapes shared by the Movement and Action FSMs that
// EnemyEntity drives. Intentionally separate from the player's MovementState/
// ActionState so enemy code can't accidentally drift into reading player FSM
// internals (and vice versa). See Plans/ENEMY_CAPABILITY_FRAMEWORK.md.

// Per-frame bundle handed to enemy states — analogue of EnvironmentContext.
// Built fresh each Update by EnemyEntity, never stored, so it carries no
// snapshot weight.
public struct EnemyContext
{
    public float            Dt;
    public int              Frame;
    public EnemyEntity      Self;
    public PlayerCharacter  Player;
    public HitboxWorld      Hitboxes;
    public IEntitySpawner   Spawner;     // .HitIds, .SpawnEntity (for future ranged variants)
    public int              Facing;       // set by EnemyEntity before scanning states
    // Brain output — populated by EnemyEntity.Update right after it calls the
    // controller's Decide, before any state scan. Movement states should
    // prefer this over re-reading the world: MoveX over recomputing chase
    // direction, Jump over re-checking player altitude, AimWorld over
    // ctx.Player.Body.Position.
    public EnemyInput       Input;

    // Sight and memory, filled by EnemyEntity.Update before Decide. Only meaningful
    // when the blueprint set TargetMemory; otherwise PlayerVisible is true and
    // LastSeenPos tracks the player exactly, so a controller written against these
    // behaves identically on an enemy that does not pay for the raycast.
    public bool     PlayerVisible;
    public Vector2  LastSeenPos;
    public float    LastSeenAge;      // seconds since the last clear sighting

    public Vector2 ToPlayer => Player.Body.Position - Self.Body.Position;
    public float   Dist     => ToPlayer.Length();
}

// Generic state base — both EnemyMovementState and EnemyActionState reuse this
// so EnemyEntity can drive both FSMs through a single selection loop. The
// concrete state list (movement vs action) is the only thing that differs.
public abstract class EnemyState<TVars> where TVars : struct
{
    public abstract int  ActivePriority  { get; }
    public abstract int  PassivePriority { get; }

    public abstract bool CheckPreConditions(in EnemyContext ctx);
    public abstract bool CheckConditions  (in EnemyContext ctx, ref TVars v);

    public virtual  void Enter (in EnemyContext ctx, ref TVars v) {}
    public virtual  void Exit  (in EnemyContext ctx, ref TVars v) {}
    public abstract void Update(in EnemyContext ctx, ref TVars v);

    // Render-only: append this frame's overlay shapes (wind-up tells, strike zones)
    // to `t`. Never touches a SpriteBatch — Drawing/TelegraphRenderer draws the list.
    public virtual  void Telegraph(TelegraphList t, PhysicsBody body, in TVars v) {}
}

// Superset of every movement state's per-activation fields. Mutually exclusive
// in time, exactly like the player's MovementVars — value-type → struct copy
// snapshot.
public struct EnemyMovementVars
{
    public float TimeInState;
    // Saved off by states that mutate Entity-level physics knobs on Enter and
    // must restore them on Exit (e.g. EnemyClingMoveState toggles GravityScale).
    // Reset to 0 with the rest of the vars on transition, which is correct
    // because Enter is the first thing that reads/writes it.
    public float SavedGravityScale;
}

// Same idea for actions. WindupDuration/ActiveDuration/RecoveryDuration are
// copied off the flyweight on Enter so Draw can read phase progress without
// reaching back into the state object (and so a snapshot round-trip restores
// the right values when the flyweight knobs are virtual / per-subclass).
public struct EnemyActionVars
{
    public float TimeInState;
    public int   LockedFacing;
    public int   HitId;
    public bool  Committed;             // movement FSM reads this — the only cross-FSM channel
    // Aim direction captured at Enter, as a unit vector. LockedFacing (±1) is
    // enough for an enemy that only ever strikes horizontally; anything that
    // fires or swings along an arbitrary 2D axis — a sentry sniping diagonally,
    // a ceiling-crawler lashing downward — needs the full vector, and needs it
    // FROZEN at windup start so the telegraph the player reads is the same line
    // the hitbox will occupy. Snapshotted through the previously-unused
    // EntityData.Aim slot (see EnemyEntity.WriteState).
    public Vector2 LockedAim;
    public float WindupDuration;
    public float ActiveDuration;
    public float RecoveryDuration;
}

// Concrete bases — separate types so a movement-state list can't accidentally
// contain an action and vice versa.
public abstract class EnemyMovementState : EnemyState<EnemyMovementVars> {}

public abstract class EnemyActionState : EnemyState<EnemyActionVars>
{
    // Stamp Windup/Active/RecoveryDuration into vars from this flyweight's knobs.
    // Called by EnemyEntity.ReadState after restoring TimeInState so Draw and
    // phase math see the same numbers a live Enter would have written. Default
    // is no-op for actions that don't telegraph (none currently, but the hook
    // costs nothing).
    public virtual void PopulateDurations(ref EnemyActionVars v) {}
}
