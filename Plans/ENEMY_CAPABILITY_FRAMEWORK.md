# Enemy Capability Framework — MVP

**Status:** draft / not yet implemented.

**Goal:** the smallest possible reusable enemy scaffold built the same way the player is —
two concurrent FSMs (movement + action), flyweight states with per-activation value
structs, snapshot via plain data. MVP = **one base class, one attack action, a small
handful of movement states**. Everything richer (multiple attacks, posture, charge meters,
phased AI, feints, sim-side telegraph descriptors) is explicitly out of scope and can be
added later without re-shaping the foundation.

## Design constraints (carry over from the engine)

1. **Determinism + snapshot-safe.** Same rules as the player ([Plans/ROLLBACK_ROADMAP.md](ROLLBACK_ROADMAP.md)).
   All per-activation state lives in `ref`-passed value structs; the flyweight state
   objects are stateless. No `System.Random`, no wall-clock reads, no sim-affecting
   mutable statics.
2. **Mirror the player.** `EnemyMovementState`/`EnemyActionState` are the enemy analogues
   of [Character/MovementStates.cs](../Character/MovementStates.cs) and [Character/ActionStates.cs](../Character/ActionStates.cs).
   Same precondition + priority + Enter/Update/Exit shape. Two registries, one current
   index each, the same selection loop.
3. **Telegraphs are animation, not sim.** Just like the player's slash dot, the stab tip
   ribbon, or the guard shield: an attack's tell is drawn in the state's `Draw` from its
   `vars.TimeInState` and `vars.WindupDuration`. No `Telegraph` struct, no sim/render
   bridge. The damage window is authoritative; the visual is whatever the renderer reads
   off `vars`.
4. **Sim owns timing; render owns pixels.** Same rule as everywhere else.

## Layering

```
   EnemyEntity : Entity
        │ owns two FSMs + small AI scratch state, snapshotted
        ├──> Movement FSM:  EnemyMovementState (flyweight) + EnemyMovementVars (value struct)
        └──> Action   FSM:  EnemyActionState   (flyweight) + EnemyActionVars   (value struct)

   EnemyContext   — per-frame bundle (analogue of EnvironmentContext)
```

Mapping to the player so it's familiar:

| MVP framework | Mirrors in player code |
|---|---|
| `EnemyEntity` | `PlayerCharacter` |
| `EnemyMovementState` | `MovementState` |
| `EnemyActionState` | `ActionState` |
| `EnemyMovementVars` / `EnemyActionVars` | `MovementVars` / `ActionVars` |
| `EnemyContext` | `EnvironmentContext` |
| (no separate input — AI reads ctx and writes intents internally) | `PlayerInput` |
| `EnemySnapshot` field on `EntitySnapshot` | `PlayerSnapshot` |

---

## 1. `EnemyEntity : Entity`

The host. An `Entity` subtype (`EntityKind.Enemy`) so it rides every existing system
(`PhysicsBody`, `IHittable`/`Hurtbox`, `CombatSystem`, knockback, snapshot) for free.
Same shape as [Entities/StalkerEnemy.cs](../Entities/StalkerEnemy.cs), generalized so concrete enemies
just hand it a movement-state list and an action-state list.

```csharp
public class EnemyEntity : Entity
{
    public override EntityKind Kind => EntityKind.Enemy;

    private readonly List<EnemyMovementState> _movement;
    private readonly List<EnemyActionState>   _actions;
    private int _currentMovement = 0;
    private int _currentAction   = -1;        // -1 == none
    private EnemyMovementVars _moveVars;
    private EnemyActionVars   _actionVars;
    private int   _facing = 1;
    private int   _frame;

    public EnemyEntity(Vector2 pos, float hp,
                       List<EnemyMovementState> movement,
                       List<EnemyActionState> actions)
        : base(new PhysicsBody(Polygon.CreateRegular(12f, 6), pos), health: hp)
    {
        _movement = movement;   // index order == snapshot identity
        _actions  = actions;
        Faction   = Faction.Enemy;
    }

    public override void Update(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        if (IsDead) return;
        _frame++;

        var ctx = new EnemyContext {
            Dt = dt, Frame = _frame, Self = this, Player = player,
            Hitboxes = hitboxes, Spawner = spawner,
        };

        // Face the player only while NOT mid-action (actions lock facing on Enter).
        if (_currentAction < 0)
        {
            float dx = player.Body.Position.X - Body.Position.X;
            if (MathF.Abs(dx) > 1f) _facing = dx >= 0 ? 1 : -1;
        }
        ctx.Facing = _facing;

        // 1) Action FSM — same precondition+priority scan as ActionState (see §3).
        SelectAndStep(_actions, ref _currentAction, ref _actionVars, ctx, isAction: true);

        // 2) Movement FSM — same scan as MovementState.
        SelectAndStep(_movement, ref _currentMovement, ref _moveVars, ctx, isAction: false);
    }

    // Single shared loop for both FSMs — they have identical shape. See §2/§3 for the
    // base classes; the loop here just calls CheckPreConditions on every state, picks
    // the highest-priority candidate (with the current state's *active* priority as
    // the incumbency tiebreak), and runs Enter/Update/Exit.
    private void SelectAndStep<TState, TVars>(
        List<TState> states, ref int current, ref TVars vars, in EnemyContext ctx, bool isAction)
        where TState : EnemyState<TVars>
        where TVars  : struct
    {
        int next = -1, bestPri = int.MinValue;
        for (int i = 0; i < states.Count; i++)
        {
            if (!states[i].CheckPreConditions(ctx)) continue;
            int pri = (i == current) ? states[i].ActivePriority : states[i].PassivePriority;
            if (pri > bestPri) { bestPri = pri; next = i; }
        }
        if (next != current)
        {
            if (current >= 0) states[current].Exit(ctx, ref vars);
            current = next;
            vars = default;
            if (current >= 0) states[current].Enter(ctx, ref vars);
        }
        if (current >= 0)
        {
            // CheckConditions can drop us back to "no state" for actions, or to the
            // fallback movement state (typically index 0 = Idle/Fall).
            if (!states[current].CheckConditions(ctx, ref vars))
            {
                states[current].Exit(ctx, ref vars);
                current = isAction ? -1 : 0;
                vars = default;
                if (current >= 0) states[current].Enter(ctx, ref vars);
            }
            else
            {
                states[current].Update(ctx, ref vars);
            }
        }
    }

    public override void Draw(SpriteBatch sb, Texture2D pixel)
    {
        base.Draw(sb, pixel);
        if (_currentMovement >= 0) _movement[_currentMovement].Draw(sb, pixel, Body, in _moveVars);
        if (_currentAction   >= 0) _actions  [_currentAction  ].Draw(sb, pixel, Body, in _actionVars);
    }
}
```

The selection rules (active vs passive priority, incumbency, two-FSM concurrency) are
copied verbatim from the player so the determinism and rollback behavior is identical.
Action-FSM `current = -1` is the analogue of the player's `NullAction` always-on
fallback — equivalent and simpler for MVP since enemies don't need a per-frame "ready"
indicator.

---

## 2. `EnemyMovementState` — the movement FSM

Same shape as `MovementState`. MVP ships three concrete states, picked by AI-style
preconditions reading `ctx.ToPlayer` / `ctx.Dist`:

| State           | When it runs (precondition)                              | Notes |
|-----------------|----------------------------------------------------------|-------|
| `EnemyIdleState`| always (priority 0 — fallback)                            | brakes velocity, faces player |
| `EnemyChaseState`| `ctx.Dist > AttackRange`                                  | walks toward player |
| `EnemyAttackHoldState` | an attack is committing (`ctx.Self.IsActionCommitted`) | roots the body; locks out chase |

The "committing" bit is the only coupling between FSMs — same direction as the player
(actions may influence movement; movement does not read action state's *contents*, just
a single committed flag the action exposes via `vars.Committed`). This mirrors the
existing `MovementModifiers` channel.

```csharp
public abstract class EnemyMovementState : EnemyState<EnemyMovementVars>
{
    // Same Active/PassivePriority + Check + Enter/Exit/Update + Draw as EnemyState<>.
}

public struct EnemyMovementVars
{
    public float TimeInState;
    // Add scratch fields as concrete states need them (e.g. target cell, hop timer).
}

public class EnemyIdleState : EnemyMovementState
{
    public override int ActivePriority  => 5;
    public override int PassivePriority => 0;
    public override bool CheckPreConditions(in EnemyContext ctx) => true;
    public override bool CheckConditions  (in EnemyContext ctx, ref EnemyMovementVars v) => true;
    public override void Update(in EnemyContext ctx, ref EnemyMovementVars v)
    {
        v.TimeInState += ctx.Dt;
        ctx.Self.Body.Velocity.X *= 0.8f;
    }
}

public class EnemyChaseState : EnemyMovementState
{
    private const float Speed = 70f;
    public override int ActivePriority  => 20;
    public override int PassivePriority => 15;
    public override bool CheckPreConditions(in EnemyContext ctx) => ctx.Dist > 56f && !ctx.Self.IsActionCommitted;
    public override bool CheckConditions  (in EnemyContext ctx, ref EnemyMovementVars v) => !ctx.Self.IsActionCommitted;
    public override void Update(in EnemyContext ctx, ref EnemyMovementVars v)
    {
        v.TimeInState += ctx.Dt;
        ctx.Self.Body.Velocity.X = ctx.Facing * Speed;
    }
}

public class EnemyAttackHoldState : EnemyMovementState
{
    public override int ActivePriority  => 40;
    public override int PassivePriority => 35;
    public override bool CheckPreConditions(in EnemyContext ctx) => ctx.Self.IsActionCommitted;
    public override bool CheckConditions  (in EnemyContext ctx, ref EnemyMovementVars v) => ctx.Self.IsActionCommitted;
    public override void Update(in EnemyContext ctx, ref EnemyMovementVars v)
    {
        v.TimeInState += ctx.Dt;
        ctx.Self.Body.Velocity.X *= 0.6f;     // root in place for readability
    }
}
```

That's the whole movement layer for MVP: idle, chase, attack-hold. Adding a `JumpState`,
`RepositionDash`, etc. later is a new flyweight + a precondition — no framework change.

---

## 3. `EnemyActionState` — the action FSM

Same shape as `ActionState`. MVP ships **one** concrete action: a melee `EnemyMeleeAction`
with Windup → Active → Recovery, telegraphed by `Draw` (a growing dot / shake / color
ramp keyed on `vars.TimeInState / vars.WindupDuration`).

```csharp
public abstract class EnemyActionState : EnemyState<EnemyActionVars>
{
    // Lifecycle inherited from EnemyState<>.
}

public struct EnemyActionVars
{
    public float TimeInState;
    public int   LockedFacing;
    public int   HitId;
    public bool  Committed;      // read by EnemyAttackHoldState; set in Enter, cleared in Exit
    public float WindupDuration; // copied from the flyweight at Enter so Draw can read it
    public float ActiveDuration;
    public float RecoveryDuration;
}

public class EnemyMeleeAction : EnemyActionState
{
    // Tuning knobs — would be virtual properties if we had multiple variants, but for
    // MVP we keep them as constants. New variants subclass and override.
    protected virtual float Windup   => 0.45f;
    protected virtual float Active   => 0.12f;
    protected virtual float Recovery => 0.40f;
    protected virtual float Range    => 32f;
    protected virtual float Damage   => 1.0f;
    protected virtual Vector2 Knockback => new(220f, -90f);

    public override int ActivePriority  => 30;
    public override int PassivePriority => 25;

    public override bool CheckPreConditions(in EnemyContext ctx)
        => ctx.Dist < Range && MathF.Abs(ctx.ToPlayer.Y) < 24f;

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedFacing     = ctx.Facing;
        v.HitId            = ctx.Spawner.HitIds.Next();
        v.Committed        = true;
        v.WindupDuration   = Windup;
        v.ActiveDuration   = Active;
        v.RecoveryDuration = Recovery;
    }

    public override void Exit(in EnemyContext ctx, ref EnemyActionVars v) => v.Committed = false;

    public override void Update(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.TimeInState += ctx.Dt;
        float t = v.TimeInState;
        if (t < v.WindupDuration) return;                                 // telegraphing
        if (t >= v.WindupDuration + v.ActiveDuration) return;             // recovery

        // Active window — publish a forward hitbox each frame; CombatSystem dedupes on HitId.
        var c = ctx.Self.Body.Position + new Vector2(v.LockedFacing * 22f, 0f);
        var region = new BoundingBox(c.X - 12f, c.Y - 12f, c.X + 12f, c.Y + 12f);
        ctx.Hitboxes?.Publish(new Hitbox(
            region, v.HitId, Damage,
            new Vector2(v.LockedFacing * Knockback.X, Knockback.Y),
            Faction.Enemy, ctx.Self, Color.OrangeRed,
            targets: HitTargets.EntitiesOnly));
    }

    // Telegraph IS the Draw. Mirror player slash/stab visuals — read everything off vars.
    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body, in EnemyActionVars v)
    {
        float t = v.TimeInState;
        if (t < v.WindupDuration)
        {
            float p = t / v.WindupDuration;   // 0 → 1 across windup
            // Growing red dot offset toward facing; alpha + size ramp on p.
            int sz = 2 + (int)(p * 4f);
            var pos = body.Position + new Vector2(v.LockedFacing * (8f + p * 14f), 0f);
            var color = Color.Lerp(new Color(255, 80, 80, 100), Color.Red, p);
            sb.Draw(pixel, new Rectangle((int)pos.X - sz / 2, (int)pos.Y - sz / 2, sz, sz), color);
        }
        else if (t < v.WindupDuration + v.ActiveDuration)
        {
            // Strike flash — full-intensity slab where the hitbox is.
            var c = body.Position + new Vector2(v.LockedFacing * 22f, 0f);
            sb.Draw(pixel, new Rectangle((int)c.X - 12, (int)c.Y - 12, 24, 24), Color.OrangeRed * 0.7f);
        }
        // Recovery: nothing — body sprite alone reads the lockout, same as player slash.
    }
}
```

That's the whole action layer for MVP: one melee swing. Adding a ranged projectile spawn
or a ground-pound terrain mutation later is a new `EnemyActionState` subclass — no
framework change.

---

## 4. `EnemyState<TVars>` — the shared base

Both FSMs share one tiny base so `SelectAndStep<>` in `EnemyEntity` can drive them with
the same loop. Lifted directly from the player's two base classes, intersected.

```csharp
public abstract class EnemyState<TVars> where TVars : struct
{
    public abstract int  ActivePriority  { get; }
    public abstract int  PassivePriority { get; }
    public abstract bool CheckPreConditions(in EnemyContext ctx);
    public abstract bool CheckConditions  (in EnemyContext ctx, ref TVars v);
    public virtual  void Enter (in EnemyContext ctx, ref TVars v) {}
    public virtual  void Exit  (in EnemyContext ctx, ref TVars v) {}
    public abstract void Update(in EnemyContext ctx, ref TVars v);
    public virtual  void Draw  (SpriteBatch sb, Texture2D pixel, PhysicsBody body, in TVars v) {}
}
```

---

## 5. `EnemyContext`

Per-`Update` bundle, analogue of `EnvironmentContext`. Built fresh each frame, never
stored, so it carries no snapshot weight.

```csharp
public struct EnemyContext
{
    public float Dt; public int Frame;
    public EnemyEntity Self;
    public PlayerCharacter Player;
    public HitboxWorld Hitboxes;
    public IEntitySpawner Spawner;     // .HitIds, .SpawnEntity (for ranged variants later)
    public int Facing;                  // set by EnemyEntity.Update before scanning states

    public Vector2 ToPlayer => Player.Body.Position - Self.Body.Position;
    public float   Dist     => ToPlayer.Length();
}
```

No `Chunks` field yet — terrain-interacting attacks aren't in MVP scope (add when the
first terrain-mutating action is written).

---

## 6. Snapshot integration

Two ints + two value structs + a facing/frame counter. Use the same pattern as the other
entity subtypes (`StalkerEnemy.WriteState/ReadState`) — fold these fields into a new
`EnemySnapshot` nullable on `EntitySnapshot`, or — since the count is small — reuse the
existing `AIState`/`StateTime`/`Facing`/`HitId` fields plus two new ones (`ActionIdx`,
`ActionTime`). MVP picks the **second** option to avoid touching the snapshot system
beyond a couple of fields:

- `EntitySnapshot.AIState` → `_currentMovement` (already an int, repurposed)
- `EntitySnapshot.StateTime` → `_moveVars.TimeInState`
- `EntitySnapshot.Facing` → `_facing`
- `EntitySnapshot.HitId` → `_actionVars.HitId`
- **new** `EntitySnapshot.ActionIdx` (int) → `_currentAction`
- **new** `EntitySnapshot.ActionTime` (float) → `_actionVars.TimeInState`
- **new** `EntitySnapshot.LockedFacing` (int) → `_actionVars.LockedFacing`

The melee action's `WindupDuration`/`ActiveDuration`/`RecoveryDuration` are copied from
the flyweight on Enter, so they restore deterministically by re-running Enter logic —
but since restore writes them *back* into vars, persist them too if Enter isn't replayed
on restore. (Three more floats; or recompute from the flyweight via the current
`ActionIdx`. MVP recomputes — simpler.)

```csharp
// In EnemyEntity:
protected override void WriteState(ref EntitySnapshot s)
{
    base.WriteState(ref s);
    s.AIState      = _currentMovement;
    s.StateTime    = _moveVars.TimeInState;
    s.Facing       = _facing;
    s.HitId        = _actionVars.HitId;
    s.ActionIdx    = _currentAction;
    s.ActionTime   = _actionVars.TimeInState;
    s.LockedFacing = _actionVars.LockedFacing;
}
protected override void ReadState(in EntitySnapshot s)
{
    base.ReadState(in s);
    _currentMovement       = s.AIState;
    _moveVars              = default; _moveVars.TimeInState = s.StateTime;
    _facing                = s.Facing;
    _currentAction         = s.ActionIdx;
    _actionVars            = default;
    _actionVars.HitId        = s.HitId;
    _actionVars.TimeInState  = s.ActionTime;
    _actionVars.LockedFacing = s.LockedFacing;
    _actionVars.Committed    = _currentAction >= 0;
    // Re-derive WindupDuration/ActiveDuration/RecoveryDuration from the flyweight:
    if (_currentAction >= 0 && _actions[_currentAction] is EnemyMeleeAction m) {
        _actionVars.WindupDuration   = m.GetWindup();   // expose protected knobs via internal getters
        _actionVars.ActiveDuration   = m.GetActive();
        _actionVars.RecoveryDuration = m.GetRecovery();
    }
}
```

**Snapshot rule of thumb:** anything that needs to survive a frame goes in
`EnemyMovementVars` or `EnemyActionVars` — never as a private field on a flyweight state
or on the `EnemyEntity` outside the fields enumerated above.

Add `EntityKind.Enemy` + a `Rehydrate` case (construct an `EnemyEntity` with empty move
lists is wrong — Rehydrate needs to know which *concrete enemy* it was). MVP punts this:
the only enemy in MVP is a single concrete `BruteEnemy : EnemyEntity` that constructs
its own movement+action list in its ctor, exactly like `StalkerEnemy` does.

```csharp
public sealed class BruteEnemy : EnemyEntity
{
    public BruteEnemy(Vector2 pos)
        : base(pos, hp: 3f,
               movement: new() { new EnemyIdleState(), new EnemyChaseState(), new EnemyAttackHoldState() },
               actions:  new() { new EnemyMeleeAction() })
    { Color = Color.DarkOrange; Sprite = Sprites.Stalker(12f); }

    public override EntityKind Kind => EntityKind.Brute;
}
```

---

## 7. Integration checklist (what touches the live tree)

Additive new files (no conflicts):
- `Entities/EnemyEntity.cs`, `Entities/EnemyContext.cs`, `Entities/EnemyState.cs`,
  `Entities/EnemyMovementStates.cs` (Idle/Chase/AttackHold), `Entities/EnemyActions.cs`
  (`EnemyMeleeAction`), `Entities/BruteEnemy.cs`.

Shared touchpoints (small, coordinated):
- `EntitySnapshot`: add `int ActionIdx; float ActionTime; int LockedFacing;` + an
  `EntityKind.Brute` enum value + a `Rehydrate` case for `new BruteEnemy(Body.Position)`.
- `Stage.Populate` (or a new test stage): `g.SpawnEntity(new BruteEnemy(pos))`.

Do **not** touch: `PhysicsWorld`, `CombatSystem`, `PlayerCharacter`, `Simulation.Step`
ordering. The enemy is just another `Entity` to all of them.

---

## 8. Build milestones

1. **Plumbing.** `EnemyEntity` + `EnemyState<>` + `EnemyContext`. No states yet — entity
   just sits there. Prove it spawns, takes damage, dies.
2. **One movement state.** Add `EnemyIdleState` only; verify the selection loop runs
   and `_currentMovement` snapshots/restores cleanly.
3. **Chase + AttackHold + one melee action.** All three movement states and
   `EnemyMeleeAction`. The boss walks up, telegraphs (Draw), swings (hitbox), recovers.
4. **Snapshot round-trip test.** Add `BruteEnemy` to a `SnapshotRoundTripTests` case
   mid-windup, mid-active, mid-recovery, mid-chase. **This is the gate** before adding
   more enemies.
5. **(Out of MVP)** Second action variant, ranged projectile attack, terrain-mutating
   attack, jump movement state, posture/charge meters, multi-enemy controller — each is
   a strictly additive change once the gate above is green.

---

## 9. Deferred (explicitly NOT in MVP)

- **Resources / meters** (stamina, posture, charge, cooldown families). No struct, no
  rules. Per-attack cooldowns, if needed before the deferred work lands, live as a
  single `float _attackCooldown` field on `EnemyEntity` ticked in `Update`.
- **Sim-side `Telegraph` descriptor.** Animations read `vars.TimeInState /
  vars.WindupDuration` in `Draw` — same as the player's slash/stab visuals. If we ever
  need a HUD danger overlay or screen-shake-on-windup, add it as a render-side reader of
  the same vars, not a new sim struct.
- **`DetRng` / `ControllerBlackboard` / phased AI / feints / habit tracking.** State
  selection is the precondition+priority scan, deterministic and replayable. Adding a
  PRNG for variety is straightforward later (seed in `EnemyEntity`, snapshot the state),
  but no MVP enemy needs it.
- **`MoveStatus.Committed` / `Cancellable` etc.** Replaced by the single `vars.Committed`
  bool the movement FSM reads. Richer status reporting comes back if/when an action
  needs to expose more granularity (e.g. "in a cancellable tail").
- **`HasArmor` / hyperarmor** during windup. Added per-action later as a `vars.Armored`
  bool the entity's `OnHit` checks.
- **Hot-swappable controllers / data-driven behavior trees.** Reconsider once we have
  3+ enemies with overlapping decision logic.
