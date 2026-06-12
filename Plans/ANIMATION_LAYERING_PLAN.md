# Animation Layering Plan — composing movement + action animations

Status: **implemented** (June 2026). The movement FSM and the action FSM both drive
the skeleton at the same time: the movement layer (including the cadence solver from
[ANIMATION_LOCOMOTION_PLAN.md](ANIMATION_LOCOMOTION_PLAN.md)) builds the base pose,
and a masked action overlay lerps on top of it. This doc records the as-built design
and the deltas from the original proposal.

## The problem

The player runs two parallel FSMs — movement (`MovementState`) and action
(`ActionState`) — and they overlap in time. You can be **running while slashing**:
movement wants the legs cycling, the action wants the arms swinging a blade.
[`CharacterAnimator`](../Animation/CharacterAnimator.cs) used to select exactly one
clip (from movement), so it couldn't express both. This is the standard skeletal
**layering + masking** problem (Unity avatar masks, Unreal layered-blend-per-bone).

## As-built design: base layer + masked action overlay

Per `CharacterAnimator.Update` frame (step numbers as in the code):

1. Select the movement clip; 2. advance the locomotion phase (cadence solver) —
   **unchanged**, reads only the movement clip and its contact labels.
3. Sample the base movement pose into `_target` — **unchanged**.
3.5 **Action overlay (new):**
   - Rebind when `sample.Action` changes: look up `_actionClips[actionName]`
     (clip `Type` = exact action class name, e.g. `"GroundSlash1"`); resolve the
     clip's `Region` to a cached bone mask. No clip → no overlay.
   - If active, sample the clip at **`sample.ActionTime`** (=
     `ActionVars.TimeInState`, deterministic sim seconds) into `_actionPose`.
   - Ease `ActionWeight` toward 1 (active) or 0 (inactive) — asymmetric rates
     (`ActionEaseIn = 25/s`, `ActionEaseOut = 8/s`): slashes last ~0.14s so the
     overlay must register immediately; the slow release bridges the `ReadyAction`
     gaps inside a combo so the arm doesn't dip back to the walk pose between hits.
   - Compose: for masked bones only,
     `_target.Local[i] = BoneTransform.Lerp(_target.Local[i], _actionPose.Local[i], ActionWeight)`.
3b. Directional lean; 3c. landing squash — **unchanged, applied after the compose**.
4. `_pose.BlendToward(_target, …)` — unchanged.

### Why the action layer reads `ActionVars.TimeInState`

It's deterministic, snapshotted sim state, so the slash pose lines up with the actual
damage frames and survives rollback. Movement time stays animator-derived/cosmetic.
The sim/render firewall is intact: both FSMs stay agnostic to animation; the animator
reads `CharacterAnimSample` (which now carries `ActionTime`) and blends.

### Masking: region tags

`AnimationDocument.Region` (`AnimRegion { FullBody, UpperBody, LowerBody }`, default
`FullBody`) declares which bones a clip owns when layered. Resolved per skeleton by
[`BoneMask.Resolve`](../Animation/BoneMask.cs):

- `UpperBody` = the `chest` bone + descendants (head, both arms).
- `LowerBody` = the complement (hip root, legs, feet).
- `FullBody` = all bones (a lunging stab can override the legs too).
- A rig with no `chest` bone resolves UpperBody to all-false → overlay no-ops.

JSON back-compat: `Region` is omitted when `FullBody` (`WhenWritingDefault`), so
legacy files load and re-save unchanged; non-default regions serialize as strings
(`"Region": "UpperBody"`).

### Clip binding

In the `CharacterAnimator` constructor, a loaded clip whose `Type` parses as an
`AnimClip` binds to the movement table (as before); any other `Type` lands in the
string-keyed `_actionClips` table. Exact-name mapping, no aliases: an action without
an authored clip simply gets no overlay. `NullAction`/`ReadyAction`/`RecoveryAction`/
`"None"` read as "no action" (`IsOverlayAction`) — the overlay fades out through them.

## Deltas from the original proposal (decided during implementation)

- **No `ActionActive` sample field.** The active/inactive policy is the animator-side
  `IsOverlayAction(string)` — the sample stays a dumb snapshot, consistent with
  `SelectClip` owning the `MovementState` string policy.
- **One scratch pose (`_actionPose`), not `_base` + `_action`.** The base is built in
  `_target` directly (as before); `_actionPose` persists the last-sampled overlay so
  the fade-out blends away from it rather than resampling or popping.
- **Compose runs *before* lean/squash** (the proposal left them implicitly after the
  base only). Both are additive deltas, so applied post-compose they stay continuous
  in `ActionWeight`; run-slash keeps its lean, landing mid-air-slash still squashes.
- **Asymmetric ease rates** instead of one `ActionWeight` ease constant.
- Action clips are **constraint-free fixed-rate overlays** (resolved in
  [ANIMATION_LOCOMOTION_PLAN.md](ANIMATION_LOCOMOTION_PLAN.md) §12): no contact
  labels, never enter the cadence φ-solve. All contacts/IK stay movement-side.

## Known v1 limits

- **`ChargeTime`-driven actions** (BlockReady, Beam, LobbedArea) aren't wired — only
  `TimeInState` flows through the sample. Authoring clips for them will sample
  "wrong" until a charge channel is added.
- **`SlashDir` aiming** isn't reflected: one clip per action name, facing handled by
  the root flip. Up/down slash variants need either per-direction clips or a
  procedural aim layer later.
- **Mask swap mid-fade** (e.g. FullBody action chained after an UpperBody one)
  switches the mask instantly at rebind; worst case a one-frame region pop, smoothed
  by the step-4 ease.
- **`CurrentSoleY()` scans all bone tips** for ground placement, including overlay
  arms. On this rig a straight-down arm stays ~15px above the foot line, so it can't
  steal the sole — but a pathological clip (torso folded double) could lift the rig.
  If that ever happens, skip UpperBody-masked bones in the sole loop.

## Editor + authoring (MTile.Demo)

- **`R`** cycles the selected clip's `Region`; shown in the header.
- **`T` / Shift-T** cycles `Type` through `AnimClip` names + all concrete action
  state class names (reflection over `ActionState` subclasses; editor-only, desktop-
  only) + `Misc`.
- `BuildSeeds` ships a `slash1` seed (`Type="GroundSlash1"`, `Region=UpperBody`,
  `Duration=0.14`, `Loop=false`): windup → forward swing (~hitbox open) → low
  follow-through → settle. Placeholder art — refine in the editor.
- See [MTile.Demo/CONTROLS.md](../MTile.Demo/CONTROLS.md).

## Tests

- [`MTile.Tests/Animation/BoneMaskTests.cs`](../MTile.Tests/Animation/BoneMaskTests.cs)
  — region→bone-set resolution + `Region` JSON round-trip.
- [`MTile.Tests/Animation/ActionOverlayTests.cs`](../MTile.Tests/Animation/ActionOverlayTests.cs)
  — headless overlay behavior: arms converge to the slash while legs keep cycling;
  lower body bit-identical to a no-action control; inactive actions and missing
  clips produce no overlay; monotonic fade-out from the last overlay pose; combo
  rebind without a weight dip; `Loop=false` clips hold their final keyframe.

## Future extensions (still out of scope)

- **Additive layer** — author actions as deltas from a reference pose; composes for
  recoil/flinch that should stack regardless of the base.
- **Per-bone masks** — generalize region tags into arbitrary bone sets (regions
  become presets over it).
- **More than two layers** — e.g. a hit-reaction layer above actions.
- **Crossfade between movement clips** (idle↔walk↔air) beyond the single
  `BlendToward`.
- **Charge-time channel** for `ChargeTime`-driven action clips.
- **Snapshotted movement time-in-state** — only if frame-perfect deterministic
  locomotion timing (replays) is ever needed.
