# Animation system — current state of the code

Orientation doc for working on MTile's skeletal animation **by hand**. It describes
what the code actually does today (post joint-chain migration, post overlay-stack +
graded-off-region work), where each piece lives, and the two conventions that bite.
Pair it with the `anim-probe` skill (the *how-to-measure* loop) and the
`MTile.Probe` console tool (the *fast* way to run any of this — see the end).

Everything here is **render-only**: animation reads a `CharacterAnimSample` and draws.
It never writes back to the sim. You can break it freely without touching gameplay.

---

## 1. Where things live

| Path | What |
|---|---|
| `Skeletons/biped.json` | The rig (bone topology + bind transforms). One source of truth; no procedural fallback. |
| `SkeletonStates/*.json` | The clips (one `AnimationDocument` per file). Root copies are authoritative; per-host copies under `bin/` and `MTile.Web/wwwroot/` are generated. |
| `Animation/SkeletonStore.cs` | Rig load/save/capture (`SkeletonDocument`, `SkeletonBoneRecord`, `ResolveBind`). |
| `Animation/AnimationDocument.cs` | Clip data model + `AnimationStore.LoadAll/Save`. |
| `Animation/SkeletonComposition.cs` | Layers clip-local `ExtraBones` (e.g. the knife) onto the base rig. |
| `Animation/CharacterAnimator.cs` | The runtime: pull a sample → select clip → cadence → compose overlays → ease → draw. The big one. |
| `Animation/CharacterAnimSample.cs` | The read-only sim→anim boundary struct. |
| `Animation/MotionProbe.cs` | Angles → world (x,y). `Digest` / `Report` / `Diff`. The authoring instrument. |
| `Drawing/Skeleton.cs`, `SkeletonPose.cs`, `Affine2.cs` | Rig FK substrate (T·R·S). |
| `Drawing/SkeletonExamples.cs` | `SkeletonExamples.Biped()` — loads the rig by walking up to `Skeletons/`. |
| `MTile.Tests/Animation/MotionProbeTests.cs` | Re-runnable probe: writes `.probe/<clip>.digest.md` for every clip. Not pass/fail. |
| `MTile.Demo/` | Windowed viewer (`dotnet run --project MTile.Demo -- <clip>`). Plays **raw** clips. |
| `MTile.Probe/` | **NEW** headless console tool — digest/diff/report/anim a clip without the test runner or a window. |

---

## 2. The rig — pure joint chain (T·R·S)

The biped is a **pure joint chain**. Each bone:
- **attaches at its parent's TIP** — `(parent.Length, 0)` in the parent's frame;
- **rotates** about that joint by its local `Rotation`;
- **extends** `Length` along its own local +X to its own tip.

So a bone's joint = its parent's tip, and joints line up with anatomy:

```
hip(pelvis) → leg_*_upper.tip = leg_*_lower.joint (KNEE) → foot_*.joint (ANKLE) → foot_*.tip (TOE)
chest.tip   → head ; chest → arm_*_upper.tip = arm_*_lower.joint (ELBOW) → arm_*_lower.tip (HAND)
```

Because the attach is forced to the chain default, `biped.json` only stores
`Name / Parent / Rotation / Length` per bone. `Tx/Ty/Sx/Sy` are **nullable** and
omitted unless a bone deviates (only the `hip` root carries an explicit world offset).
`SkeletonBoneRecord.ResolveBind(parentLength)` fills the gaps at load;
`SkeletonStore.Capture` writes them back only when they differ from the default. The
rig geometry is byte-identical to the old verbose form — it's just deduplicated.

**Bind orientations** (rest `Rotation`, radians):

| bone | bind | meaning |
|---|---|---|
| `chest` | `-π/2` (`-1.5708`) | points up; lean is a deviation toward 0 |
| `head` | `0` | continues the chest |
| `arm_*_upper` | `+π` (`3.1416`) | **hangs straight down** (confirmed by walk) |
| `arm_*_lower` | `0` | straight (continues the upper arm) |
| `leg_*_upper` | `+π/2` (`1.5708`) | points down |
| `leg_*_lower` | `0` | straight |
| `foot_*` | `≈-1.3258` | toe forward |

**Authored `Rotation` is the FULL LOCAL ANGLE (bind + swing), not a deviation.**
`PoseData.Apply` *replaces* a bone's rotation. To "swing the thigh forward from rest"
in a clip you write the absolute angle (e.g. `1.18`), not `1.18 - π/2`.

Forward = swing toward +X, but **measure every sign with the digest** — the migration
re-mapped all the old authored values, so remembered signs are stale.

---

## 3. The clip data model (`AnimationDocument`)

A clip is a JSON file = one `AnimationDocument`:

- **`Type`** — matched to drive selection. If it parses to an `AnimClip` enum
  (`Idle/Walk/WalkBack/Crouch/Jump/Fall/Vault/Run`) it's a **base locomotion clip**;
  otherwise it's an **action overlay** keyed by the exact `Type` string (e.g.
  `"GroundSlash1"`, matched against `PlayerCharacter.CurrentActionName`).
- **`Skeleton`** — rig name; the animator only binds clips whose `Skeleton` matches.
- **`Duration` / `Loop`** — `Duration` is editor-playback seconds; overlay phase is
  actually remapped to the *action's* lifetime at runtime (see §5).
- **`Region`** (`FullBody` / `UpperBody` / `LowerBody`) — which bones the clip owns
  when layered. `FullBody` = value 0 = serialization default (omitted on save).
- **`OffRegionWeight`** (0..1, default 0) — graded weight for bones *outside* the
  Region. `0` = hard mask (legacy). `>0` lets a "whole-body" overlay lightly drive its
  off-region bones (e.g. a pulse cast `Region=UpperBody` + `OffRegionWeight=0.3` braces
  the legs at 30% without taking them over).
- **`ExtraBones`** — clip-local bones layered onto the rig (the `knife` on
  `arm_r_lower`, `Length=0` = invisible marker). Keeps the shared rig clean.
- **`Keyframes[]`** — each is `Time` (normalized [0,1]) + `Bones[]` (per-bone
  `Rotation` only — translations/scales/lengths come from the rig) + optional
  `Contacts[]` (planted-foot labels for the cadence solver) + optional `Additions[]`
  (labeled points/vectors, e.g. the `"com"` reference the host uses to place the rig).

---

## 4. The runtime pipeline (`CharacterAnimator.Update`)

Pull model. Each render frame `Update(in CharacterAnimSample s)` runs, in order:

0. **Land squash** — touchdown (was airborne, now grounded) arms a decaying squash.
1. **Select clip** from the sample only (`SelectClip`): movement-state strings
   (`Parkour→Vault`, `Crouch`, ledge holds) win, else airborne→`Jump`/`Fall`, else
   speed bands → `Idle`/`Walk`/`Run`/`WalkBack`. Clip change resets `ClipTime`/contacts.
1.5 **Resolve the overlay stack** (see §5) — bind + ease slot 0 (the Action overlay)
   and slots 1+ (movement overlays). Done *before* the cadence solve so the solver
   optimizes the **post-blend** skeleton (the feet it pins are the composed feet).
2. **Advance the locomotion phase.** Walk/Run/WalkBack with contact labels are
   **cadence-driven** (the solver picks Δφ so the planted foot doesn't slip); idle uses
   a breathing bob; everything else uses a distance-based rate. (See §6.)
3. **Sample the base clip** at phase (locomotion/idle) or `ClipTime` (one-shots) into
   `_target` via the C1 spline (`SampleSmooth`).
3a. **Apply solver Δθ** corrections (≈0 until a constraint phase needs them).
3.5 **Compose overlays** onto `_target` (`ComposeOverlays`) — the same blend the
   solver used, so the drawn skeleton is bit-identical to the optimized one.
3b. **Directional lean** for locomotion; **3c. land squash** on top.
4. **Ease** the live `_pose` toward `_target`, framerate-independent. The upper-body
   subtree (`_upperMask`: chest + arms + knife) stiffens with `ActionWeight` so a fast
   attack swing isn't low-passed away; everything else keeps the soft locomotion rate.

`Draw(ctx, worldPos, facing, …)` renders the eased pose; `facing=-1` flips X (mirror).
**Author for facing-right** (the right arm leads forward on the +X side).

---

## 5. The overlay stack

`CharacterAnimator` keeps an ordered **stack of 3 `OverlaySlot`s**:

- **Slot 0 = the Action overlay** (privileged). Bound to the clip whose `Type` ==
  `s.Action` (when it's a "real" action — `None/NullAction/ReadyAction/RecoveryAction`
  read as no-overlay and fade out, which is also what bridges combo gaps). Its eased
  opacity is the public `ActionWeight`. Its τ = action progress, time-remapped onto
  `s.ActionDuration` (sweeps once over the swing, holds past the end).
- **Slots 1+ = movement overlays** (`ResolveMovementOverlays`) — currently just
  `VaultHands` during `Parkour`, timed by **spatial** progress (`MovementProgress`),
  not a clock.

Composition (`PaintMotionLayer`): per bone, `BoneTransform.Lerp(acc, layer, w[i])`,
shortest-path. Each slot's per-bone weight is `(Mask[i] ? 1 : OffWeight) * Weight`.
Painted foreground-last (slot 0 wins shared bones). The base layer's surviving
coefficient per bone is `_baseBlend[i] = Π(1 − slotWeight[i])`, cached so the analytic
cadence Jacobian can scale base-driven columns correctly — this is why graded
off-region weight had to flow into `_baseBlend`, not just the paint.

**Masks** (`BoneMask.Resolve`, `_regionMasks`): `FullBody` = all bones; `UpperBody` =
chest subtree (arms/knife — legs & hip masked); `LowerBody` = complement. A masked
(weight-0) leg pose **still shows in the editor/digest** (they play raw clips), so an
overlay must still carry a *neutral, flag-free* leg pose even though it won't render at
runtime over a locomotion base.

---

## 6. The cadence solver (locomotion only — skip for overlay authoring)

Only Walk/Run/WalkBack with `Contacts[]` use it; it picks the phase advance Δφ so the
planted foot doesn't slide against the body's real motion. Two paths, same objective:

- **Golden-section** (production, `useSolver=false`) — 1-D minimize horizontal slip +
  a momentum prior over Δφ ∈ [0, MaxPhaseStep].
- **LM least-squares** (opt-in, `useSolver=true`) — the general solver: residuals =
  per-contact horizontal no-slip + vertical ground hold, playback continuity, soft com,
  Tikhonov pose prior. Has an analytic Jacobian (`MaxJacobianError` validates it vs FD).
  Carries headroom variables (δ vertical offset, per-bone Δθ) for later IK phases that
  are currently ≈0.

Slip is **horizontal-only** by design. Cadence gotchas (one contact per keyframe; the
freeze rule on toe-off; feather timing) live in the `anim-probe` skill — read it before
touching locomotion contacts. **None of this matters for authoring an UpperBody overlay**
— overlays carry no contacts of their own.

---

## 7. ⚠️ The two rotation conventions (the footgun)

There are two *different* angle conventions in the codebase. Mixing them silently
breaks clips:

- **Authored clips** (`SkeletonStates/*.json`) store the **full local angle**
  (`bind + swing`). This is what `PoseData.Apply` replaces.
- **Synthetic-pose test harnesses** (the skill's `Zzz` template, `GoldenSectionTests`,
  `CharacterAnimatorTests`) often pose "swing ±δ from rest" as **`Bind.Rotation + δ`**
  (bind-relative).

The throwaway harness prints bind-relative numbers; a clip file needs full-local. The
prior batch workers trusted their own bind-relative harness over the canonical digest
and authored `leg = swing − π/2` into clip files → bodies folded up. **When a number
goes into a `SkeletonStates/*.json`, it is the full local angle. Verify with the digest,
which reads the actual file through real FK.**

---

## 8. Probing — read geometry as numbers, never eyeball

`MotionProbe` runs a clip through FK to world (x,y). Three views:

- **`Digest`** — per keyframe: ground line, lean, each leg's knee direction
  (signed-cross test, auto-**FLAGS** recurvatum), planted/clearance, each hand's
  height bucket + front/back; plus an assembled-clip trajectory (foot bob/sweep).
  **Read this first.** Y-down, +x forward, C1 sampling (matches the game).
- **`Report`** — raw per-joint world table + phase velocity (deep dives).
- **`Diff`** — per-keyframe tip deltas vs a reference clip; confirms a pose actually
  **departs** from a baseline (the classic blind miss: "reads the same as idle").

Two ways to run it:
- `dotnet test MTile.Tests --filter FullyQualifiedName~MotionProbeTests` → writes
  `.probe/<clip>.digest.md` for every clip (rebuilds the test dll — slower).
- **`dotnet run --project MTile.Probe -- digest <clip>`** → prints to stdout instantly
  (no window, no test runner). See §10.

The editor (`MTile.Demo`) plays the **raw** clip — no cadence solver, no com anchor, no
masking. It shows foot *poses*, not cadence/compose results. Use it for silhouette; use
the probe (and the `anim` tool) for everything geometric.

---

## 9. Validated authoring recipe (slashes/stabs)

Confirmed on `groundslash1`. The knife is on `arm_r_lower`, so **all knife moves are
right-arm**; parkour/vault uses the left arm.

- **Legs** = the staggered idle stance (so they don't overlap), same in every keyframe:
  `leg_l_upper 1.18 / leg_l_lower 0.62 / foot_l -1.3258`,
  `leg_r_upper 1.62 / leg_r_lower 0.2 / foot_r -1.3258`. (Masked at runtime, but keeps
  the digest clean and the editor readable.)
- **Left arm** = idle hang, constant across all keyframes: `arm_l_upper 3.1416 /
  arm_l_lower 0`. So only the right arm animates.
- **Right arm** = the slash itself — authored per clip (each move has its own arc).
  Verify with the digest: the **right hand's x must cross from behind the hip to in
  front** through the active window (cocked above-head-back → strike forward-down →
  follow-through), and the left hand stays back/center.
- **Chest** leans subtly into the strike; **no FLAGS** in the digest.

`idle` itself is the canonical standing pose (staggered legs above, `chest -1.5708`,
arms hanging, `hip 0`): left foot ≈`(-1.9, 5.3)`, right foot ≈`(-6.2, 5.9)`, ~4px apart,
both planted, knees-FWD.

---

## 10. The fast tool — `MTile.Probe`

A headless console app that references `MTile.Core` and runs the key classes without a
window or the test runner. Build once, then each command is instant.

```bash
dotnet run --project MTile.Probe -- list                  # every clip: Type, Region, #kf, FLAGS?
dotnet run --project MTile.Probe -- digest groundslash1   # full semantic digest to stdout
dotnet run --project MTile.Probe -- diff groundslash1 idle# tip deltas vs a reference
dotnet run --project MTile.Probe -- report walk           # raw joint/tip table
dotnet run --project MTile.Probe -- anim groundslash1 GroundSlash1   # RUNTIME compose smoke
```

`digest/diff/report` are the same `MotionProbe` views the test writes, but printed
immediately. `anim` is new and fills a real gap: it constructs a real
`CharacterAnimator`, ticks it for ~1s with a synthetic `CharacterAnimSample` stream
(grounded, optional action overlay), and prints the **live composed+eased pose** —
the only headless way to see masks, the overlay stack, and easing actually applied
(the editor and the static probe both bypass that path).

With no clip arg, `digest`/`anim` fall back to writing `.probe/` like the test does.
The tool is read-only; it never modifies clips.
