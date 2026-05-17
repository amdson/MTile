# Action States

## Context

Movement states handle locomotion: a single FSM owns the body's force output every frame. They cover ground contact, jumps, vaults, ledge grabs, etc. They do *not* cover **what the player is doing** — slashing, blocking, casting, throwing, picking up items, etc. Those are orthogonal concerns: the player should be able to slash while walking, while crouched, while mid-jump (eventually). They have their own preconditions, lifetimes, visuals, and interrupt rules.

Rather than overloading MovementState with action logic (or stuffing action triggers into MovementConfig and dispatching from `Update`), this plan adds a **second concurrent FSM** owned by `PlayerCharacter`: the **ActionState** machine. Same shape as MovementState — preconditions, conditions, Enter/Exit/Update, priority-based selection — but separate registry, separate history, separate `Update` tick.

This document is the design for that system plus the first two action states (`NullAction`, `SlashAction`).

## Core architecture

### 1. The two FSMs run **independently** every frame

```
PlayerCharacter.Update(controller, chunks, dt):
    build ctx, refresh _abilities flags
    drive movement FSM         ← unchanged
    drive action FSM           ← new (same code shape, separate registry)
```

The action FSM uses the **same EnvironmentContext** the movement FSM does. No new context type; actions get all the world probes (ground, ceiling, walls, corners) and the input/intent stream for free.

### 2. Coupling rules

> **Actions may depend on movement; movement must not depend on actions.**

In practice:
- `ActionState.CheckPreConditions` and `CheckConditions` may inspect `ctx.Body`, `ctx.TryGetGround`, **and** the current/previous movement state via a new helper on `EnvironmentContext` (see §5).
- `MovementState` code reads nothing from the action FSM. No new fields in EnvironmentContext that reference action state.

This keeps the dependency graph a DAG and avoids the cycle where Slash-locked-movement-locked-Slash would create.

The one-way coupling is enforced by convention + code review, not by type system. Cheap and easy to maintain.

### 3. ActionState base class (mirrors MovementState exactly)

New file `Character/ActionStates.cs`:

```csharp
public abstract class ActionState
{
    public abstract int ActivePriority  { get; }
    public abstract int PassivePriority { get; }

    public abstract bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities);
    public abstract bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities);

    public virtual void Enter(EnvironmentContext ctx, PlayerAbilityState abilities) {}
    public virtual void Exit (EnvironmentContext ctx, PlayerAbilityState abilities) {}

    public abstract void Update(EnvironmentContext ctx, PlayerAbilityState abilities);

    // Optional: draw an action's overlay (slash arc, casting glow, etc.). Default is no-op.
    public virtual void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body) {}
}
```

`Draw` is the one cosmetic departure from `MovementState`'s interface. MovementState has no Draw because the body itself is what gets drawn and Game1 owns that. Actions own *additional* visuals (slash arc, projectile, etc.), so they get a hook that runs after the player polygon is drawn.

### 4. Selection loop (identical to PlayerCharacter.Update's movement loop)

Inside `PlayerCharacter.Update`, after the movement FSM step:

```csharp
if (!_currentAction.CheckConditions(ctx, _abilities))
{
    _currentAction.Exit(ctx, _abilities);
    _currentAction = _actionRegistry.First(a => a is NullAction);
    _currentAction.Enter(ctx, _abilities);
}

ActionState best = null;
int bestPrio = int.MinValue;
foreach (var a in _actionRegistry)
{
    if (a == _currentAction) continue;
    if (a.CheckPreConditions(ctx, _abilities) && a.PassivePriority > bestPrio)
    {
        bestPrio = a.PassivePriority;
        best = a;
    }
}
if (best != null && bestPrio > _currentAction.ActivePriority)
{
    _currentAction.Exit(ctx, _abilities);
    _currentAction = best;
    _currentAction.Enter(ctx, _abilities);
}
_currentAction.Update(ctx, _abilities);

_actionHistoryHead = (_actionHistoryHead + 1) % HistorySize;
_actionHistory[_actionHistoryHead] = _currentAction;
```

The two loops never see each other's state directly except through the lookups in §5.

### 5. History access

`EnvironmentContext` already has `PreviousState : Func<int, MovementState>`. Add a sibling:

```csharp
public Func<int, ActionState> PreviousAction;
```

Wired by `PlayerCharacter` analogously to `_getState`. So an ActionState's preconditions can say *"only fire if the last 6 frames weren't already SlashAction"* (to enforce cooldown), and a MovementState — by convention — does not read `PreviousAction`. The field exists on the shared context because the context lifetime matches the frame, not because movement is allowed to peek.

## NullAction

Fallback. Always passes preconditions, lowest possible priority. Mirrors `FallingState`'s role.

```csharp
public class NullAction : ActionState
{
    public override int ActivePriority  => 0;
    public override int PassivePriority => 0;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab) => true;
    public override bool CheckConditions  (EnvironmentContext ctx, PlayerAbilityState ab) => true;
    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab) {}
}
```

## SlashAction

A short ground-locked attack. Visualized as a red dot tracing a small arc out from the player and back, lasting **0.5 s** total.

### Trigger

- **Input**: a *fast* left click — `LeftClick` was held for ≤ `MaxClickHoldFrames` frames and is **released this frame**. Concretely: `!current.LeftClick && prev1.LeftClick`, and walking back through `Controller.GetPrevious` the click was held for no more than `MaxClickHoldFrames` consecutive frames before that release. Proposal: **`MaxClickHoldFrames = 6`** (≈ 0.2 s at 30 fps) — short enough that "hold to build" / future "hold to charge" gestures don't fire a slash on release, long enough that a normal mouse tap (typically 2–4 frames at this framerate) reliably counts. The release-frame trigger means a hold longer than the cap simply produces no slash on release — not a slash on the 7th frame of hold.
- **Movement requirement**: grounded — `ctx.TryGetGround(out _)` true at trigger time. Crouched/Standing both qualify.
- **Cooldown**: cannot re-enter SlashAction until ≥ `SlashCooldown` (proposal: 0.15 s ≈ 5 frames after the previous SlashAction exits). Implemented by inspecting `PreviousAction(1..N)`.

### Lifetime + interrupts

- **Duration**: 0.5 s nominal. The state holds itself active for this long, then exits to NullAction.
- **Interrupted by**: losing ground contact mid-slash (`!ctx.TryGetGround(out _)`). On interrupt, the visual snaps off — no trailing fade — and a `SlashInterrupted` flag is set in `PlayerAbilityState` so a future combo-or-recovery state can read it (initially unused).
- **Not interrupted by**: input changes, jump just pressed, left click released, direction changes. The player can keep walking/crouching through the slash. (Jump pressed → movement FSM still fires JumpingState normally, which takes the player off the ground, which interrupts the slash via the ground check above. So jump-cancels-slash is an emergent behavior, not a special case.)

### Effect on the body

**None for the first pass.** Slash does not apply force, does not lock velocity, does not add constraints. It's purely cosmetic + bookkeeping. A future change to add hit detection / damage hooks will live entirely inside SlashAction.Update.

### Visual

A red dot (one pixel filled square, drawn at scale via the same `_pixel` texture Game1 uses for line drawing) that traces a small arc:

- **Anchor**: player body center.
- **Arc plane**: vertical, on the side of the player matching `_abilities.Facing` at Enter (so a standstill slash uses the last walking direction, not a hardcoded right).
- **Arc shape**: out from body center along a 90° sweep over the first half (0–0.25 s, dot moves from center to apex roughly 1.5 × Radius away), then retracts back over the second half (0.25–0.5 s).
- **Trail**: a fading polyline of the last ~6 dot positions, alpha-attenuated linearly. Already the same primitive Game1 uses for ramps / paths, so no new draw machinery.
- **Parametrization**: `t ∈ [0, 1]` is `timeInState / 0.5`. Position = anchor + Radius × 1.5 × outwardFactor(t) × `dir(t)`, where `outwardFactor(t) = sin(πt)` (smooth 0 → 1 → 0) and `dir(t)` rotates from straight-ahead horizontal through straight-up and back to horizontal again on the same side. Exact arc parameterization is a tuning detail — start with the sin curve and adjust if it looks too flat or too hooked.

### Priorities

```
NullAction         (Active 0,  Passive 0)
SlashAction        (Active 10, Passive 20)
```

`Passive 20 > NullAction.Active 0` ⇒ Slash always preempts Null on a valid trigger.
`Active 10` is what Slash holds itself at while running. Nothing currently exceeds it (no other actions yet). When a heavier action lands later (a charge attack, a block?), it gets a Passive above 10 and naturally preempts a slash-in-progress.

## PlayerAbilityState additions

```csharp
public class PlayerAbilityState
{
    // ... existing fields ...
    public int  Facing = 1;         // -1 or +1. Last non-zero Intent.CurrentHorizontal; persisted across standstills.
    public bool SlashInterrupted;   // set by SlashAction.Exit on ground-loss interrupt; cleared by SlashAction.Enter
}
```

**Facing update**: `PlayerCharacter.Update` refreshes `_abilities.Facing` once per frame, before the action FSM runs:

```csharp
if (ctx.Intent.CurrentHorizontal != 0) _abilities.Facing = ctx.Intent.CurrentHorizontal;
```

Movement does not read `Facing` — it already has `Intent.CurrentHorizontal` for the current frame and `_wallDir` for directional states. This field exists for the action FSM's benefit (slash side, projectile direction, etc.).

## EnvironmentContext additions

```csharp
public class EnvironmentContext
{
    // ... existing ...
    public Func<int, ActionState> PreviousAction;   // mirrors PreviousState
}
```

## PlayerCharacter additions

```csharp
public class PlayerCharacter
{
    // ... existing movement registry / history / state ...

    private readonly List<ActionState> _actionRegistry = new();
    private ActionState _currentAction;
    private readonly ActionState[] _actionHistory = new ActionState[HistorySize];
    private int _actionHistoryHead = 0;
    private readonly Func<int, ActionState> _getAction;

    public ActionState GetPreviousAction(int framesBack) { /* mirrors GetPreviousState */ }
    public ActionState CurrentAction => _currentAction;
    public string CurrentActionName => _currentAction?.GetType().Name ?? "None";

    // In constructor:
    _actionRegistry.Add(new NullAction());
    _actionRegistry.Add(new SlashAction());
    _currentAction = _actionRegistry[0];
    _getAction = GetPreviousAction;

    // In Update: build ctx with PreviousAction = _getAction, then run the action loop
    // (after the movement loop) as in §4.
}
```

## Game1 wiring

Two small additions in `Game1.Draw`:

```csharp
// After DrawPolygon(_player.Body.Polygon, ...)
_player.CurrentAction.Draw(_spriteBatch, _pixel, _player.Body);

// Debug HUD: show current action name alongside movement state name
_spriteBatch.DrawString(_debugFont, _player.CurrentStateName,  new Vector2(8,  8), Color.White);
_spriteBatch.DrawString(_debugFont, _player.CurrentActionName, new Vector2(8, 24), Color.White);
```

`SlashAction` stores its own trail buffer internally; nothing leaks out.

No changes to `HandleBuildInput` — that uses `RightClick`. LeftClick is free for SlashAction's trigger.

## Files

| File | Status | Purpose |
|---|---|---|
| `Character/ActionStates.cs` | NEW | `ActionState` base class + `NullAction` + `SlashAction` |
| `Character/PlayerAbilityState.cs` | EDIT | add `SlashInterrupted` |
| `Character/EnvironmentContext.cs` | EDIT | add `PreviousAction` field |
| `Character/PlayerCharacter.cs` | EDIT | wire action registry + selection loop + `CurrentAction` accessor |
| `Game1.cs` | EDIT | call `CurrentAction.Draw`, show action name in HUD |

No changes to `MovementStates.cs`, `MovementConfig.cs`, or any physics code.

## Migration order

1. **Land the scaffolding** — `ActionState` base, `NullAction`, registry, selection loop, history, `EnvironmentContext.PreviousAction`, `PlayerCharacter.CurrentAction`, HUD wiring. NullAction is the only action. Game still feels identical. Validates the FSM plumbing without any visible behavior change.
2. **Add SlashAction** — trigger detection, 0.5 s lifetime, ground-loss interrupt, cooldown via `PreviousAction` lookups. No visual yet; verify in HUD that "SlashAction" name appears on click and disappears 0.5 s later.
3. **Add the slash visual** — red dot + trail, parametric arc. Tune the arc parameters until it reads as a slash rather than a blob.
4. **(deferred)** Hit detection / damage hooks live entirely inside SlashAction.Update.

## Confirmed design choices

1. **"Fast" left click** — hold ≤ 6 frames, trigger on release frame (see Trigger section).
2. **Slash direction** — `_abilities.Facing`, updated each frame from `Intent.CurrentHorizontal` and persisted across standstills (see PlayerAbilityState additions).
3. **No velocity tilt** — arc is axis-aligned to `Facing`, regardless of body velocity.

## Still to tune during implementation

- **Trail buffer size**. 6 samples × 16 ms ≈ 96 ms tail. Adjust visually.
- **Cooldown duration**. 0.15 s placeholder. Loosen/tighten to feel responsive but not spammy.
- **Arc apex distance**. 1.5 × Radius starting value; widen or narrow once it's on screen.

## Verification

- Run `dotnet test`. No tests should break — the action system is additive.
- Run game. Walk around; HUD shows `NullAction`. Click left mouse; HUD shows `SlashAction` for ~0.5 s then returns to `NullAction`. Red arc visible.
- Jump mid-slash → slash ends the moment the body leaves the ground (HUD returns to `NullAction` before the 0.5 s elapses).
- Hold left mouse for > 8 frames → no slash triggers. Release after 1 frame → slash triggers.
- Rapid-click twice in 100 ms → second click does *not* re-trigger (cooldown enforced).
