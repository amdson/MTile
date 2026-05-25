# Animation Layering Plan — composing movement + action animations

Status: **proposed** (not yet implemented). Captures the design for letting the
movement FSM and the action FSM both drive the skeleton at the same time.

## The problem

The player runs two parallel FSMs — movement (`MovementState`) and action
(`ActionState`) — and they overlap in time. You can be **running while slashing**:
movement wants the legs cycling, the action wants the arms swinging a blade. The
current [`CharacterAnimator`](../Animation/CharacterAnimator.cs) selects exactly one
clip (from movement), so it can't express both at once. We need to **compose**
animations from both FSMs, not pick one.

This is the standard skeletal-animation **layering + masking** problem (Unity avatar
masks / layers, Unreal "layered blend per bone").

## Current state (what exists today)

- [`CharacterAnimator`](../Animation/CharacterAnimator.cs): pull-model, render-only.
  Selects an `AnimClip { Idle, Walk, Air }` from the observed sample, binds an
  authored animation per clip by `Type`, samples it via `AnimationSampler` driven by
  `ClipTime` (animator-derived time-in-state), falls back to procedural builders, then
  eases the live pose toward the target and applies a landing squash.
- [`CharacterAnimSample`](../Animation/CharacterAnimSample.cs): read-only view —
  `Position, Velocity, Facing, Grounded, MovementState, Action, Dt`. The one-way
  boundary; the sim is unaware of it.
- [`AnimationDocument`](../Animation/AnimationDocument.cs): `Name, Type, Duration,
  Loop, Keyframes`. [`AnimationSampler`](../Animation/AnimationSampler.cs) maps elapsed
  seconds → normalized timeline and lerps bracketing keyframes.
- `PlayerCharacter` already exposes everything we need: `CurrentStateName`,
  `CurrentActionName`, and `CurrentActionVars` (which carries `TimeInState`).

## Design: two-layer animator with masked overlay

Produce the target pose in two layers, then compose:

1. **Base layer (movement)** — full-body clip (`idle`/`walk`/`air`), driven by the
   animator-derived movement clip time. (Unchanged from today.)
2. **Overlay layer (action)** — the action's clip (slash/stab/…), driven by the
   action's **`ActionVars.TimeInState`** (deterministic sim state, so it stays frame-
   synced with the hitbox windows), applied to **only a subset of bones**, with a
   blend weight that eases in when an action starts and out when it ends / goes to
   `None`.

Compose per bone into the target pose:

```
target[bone] = mask.Contains(bone) ? BoneTransform.Lerp(base[bone], action[bone], weight)
                                    : base[bone];
```

then ease the live pose toward `target` and apply secondary effects (landing squash),
exactly as now.

### Why drive the action layer from `ActionVars.TimeInState`

It's deterministic, snapshotted sim state, so the slash pose lines up with the actual
damage frames and survives rollback. Movement time stays animator-derived/cosmetic
(fine — a looping walk re-converges visually after a rollback). This keeps the
sim/render firewall intact: movement and action FSMs stay **agnostic** to animation;
the animator just reads two channels and blends.

## Masking: region tags (recommended) vs per-bone

Each animation declares which bones it owns.

- **Region tag** — `FullBody | UpperBody | LowerBody`. Trivial to author and, for an
  11-bone biped, plenty. A normal slash = `UpperBody` (chest/head/arms) so the legs
  keep walking; a lunging stab = `FullBody` so it overrides the legs too.
  **Recommended for v1.**
- **Per-bone mask list** — maximum flexibility ("only the right arm"), more to author.
  Add later as the power option; the region tags become presets over it.

Region → bone set is resolved from the rig hierarchy on the biped:
- `UpperBody` = the `chest` bone and its descendants (head + both arms).
- `LowerBody` = `hip` + its descendants **excluding** the chest subtree (the legs).
- `FullBody` = all bones.

## Data-model changes

- `AnimationDocument` gains `Region` (enum, default `FullBody`).
- A `BoneMask` resolver: `Region` → `bool[boneCount]` (or a bitmask), computed once
  per skeleton by walking the hierarchy from `chest` / `hip`.
- Action clips are mapped by `Type` = action name (e.g. an animation typed
  `GroundSlash1`), the same way movement clips map to `Idle`/`Walk`/`Air` today. A
  coarser mapping (one `Slash` clip for all slash variants) is an option if per-action
  authoring is too much.

## Animator changes ([`CharacterAnimator`](../Animation/CharacterAnimator.cs))

- Split the clip table into `_moveClips` (keyed by `AnimClip`) and `_actionClips`
  (keyed by action-name string).
- Add to [`CharacterAnimState`](../Animation/CharacterAnimator.cs): an `ActionWeight`
  (eased 0↔1) so the overlay fades in/out smoothly.
- Add to [`CharacterAnimSample`](../Animation/CharacterAnimSample.cs): `ActionTime`
  (= `CurrentActionVars.TimeInState`) and an `ActionActive` flag (false for
  `NullAction`/`ReadyAction`/`RecoveryAction`). Update `From(...)` accordingly.
- New per-frame flow in `Update`:
  1. Build the **base** target from the movement clip (today's path) into `_base`.
  2. If `ActionActive` and an action clip exists, sample it at `ActionTime` into
     `_action`; ease `ActionWeight` toward 1, else toward 0.
  3. Compose `_target` = base, then for masked bones lerp toward `_action` by
     `ActionWeight`.
  4. Ease `_pose` toward `_target`; apply landing squash. (Unchanged tail.)
- Needs two more scratch poses (`_base`, `_action`) alongside `_kfA`/`_kfB`.

## Editor support ([`MTile.Demo`](../MTile.Demo/DemoGame.cs))

- A `Region` toggle (cycle FullBody/UpperBody/LowerBody) on the selected animation,
  shown in the header and saved to JSON.
- (Nice-to-have) overlay preview: render the action clip masked on top of a chosen
  base movement clip, so authoring a slash shows it over a walk.

## Edge cases / decisions

- **Whole-body actions** (lunge, air-spin): `Region = FullBody` → fully overrides
  movement on every bone while `ActionWeight` is high.
- **No action**: `NullAction`/`ReadyAction`/`RecoveryAction` → `ActionActive = false`
  → weight eases to 0 → pure movement. This is also the graceful fallback when an
  action has no authored clip.
- **Pops on start/end**: prevented by the eased `ActionWeight` (arms ease into the
  slash and back to the walk).
- **Determinism**: action layer time = snapshotted `ActionVars.TimeInState`; movement
  layer time = animator-derived (cosmetic). No sim change required for v1.

## Build phases (when we pick this up)

1. `AnimationDocument.Region` + the region→bone-set resolver on the biped.
2. Editor `Region` toggle + save.
3. Animator: action-clip table, `ActionWeight`, sample base + action layers,
   mask-compose. Add `ActionTime`/`ActionActive` to the sample.
4. Author a seed `slash` clip (UpperBody) and confirm "walk + slash" reads correctly
   in-game (legs cycle, arms slash).

## Future extensions (explicitly out of scope for v1)

- **Additive layer** — author actions as deltas from a reference pose, add on top.
  Composes elegantly for recoil/lean/hit-reactions that should stack regardless of the
  base. Deferred because the editor produces *absolute* poses; masked override is the
  natural match for what we can author today.
- **Per-bone masks** — generalize region tags into arbitrary bone sets.
- **More than two layers** — e.g. a separate hit-reaction/flinch layer above actions.
- **Crossfade between movement clips** — ease when the movement clip itself changes
  (idle↔walk↔air), beyond the single `BlendToward` we have now.
- **Snapshotted movement time-in-state** — only needed if we want frame-perfect
  deterministic locomotion timing (replays). The pre-approved movement-FSM duration
  field would feed this; add it then, not now.
