# Animation workplan: chunking the editor + runtime plans

Status: agreed chunking, 2026-09-09. This doc is the shared roadmap over
[ANIMATION_SCENE_AUTHORING_PLAN.md](ANIMATION_SCENE_AUTHORING_PLAN.md) (the
editor update) and [ANIMATION_RUNTIME_SIMPLIFICATION_PLAN.md](ANIMATION_RUNTIME_SIMPLIFICATION_PLAN.md)
(runtime correctness/timing/solver). It groups both into workable chunks,
records the dependencies between them, and names the two places they interface.
Section references like "runtime §4" mean the numbered proposals in the runtime
plan; authoring sections are named.

## The chunks

### 1. Baseline + Jacobian correctness — runtime §1, §2

Fully independent; do first or in parallel with anything. §2 is a provable bug
fix: rabbit clips animate stretch, and `PointJacobianColumns` derives its phase
column from angular velocity alone, so the solver iterates against a wrong
derivative today. The finite-difference phase-column oracle test is as much the
deliverable as the fix. §1's measured baseline (perf + quality traces, versions
recorded) is what every later chunk's acceptance criteria compare against.

### 1.5. Functional solve core — restructure the residual assembly (user request, 2026-09-09)

Rework the cadence solve's constraint layer into an explicit, JAX-like functional
style: pure functions of explicit parameters, transparent composition, and
machine-checked hand Jacobians. The current architecture is already close — the
objective is a flat block list (`CharacterAnimator.cs` `_coreGeom`/`_corePriors`/
`_frameComposite`) and `LeastSquaresSolver.Minimize` takes plain delegates — but
every `ISolveConstraint` closes over CharacterAnimator's mutable private state
(`_scratch`, `_baseBlend`, `_angVel`, `_solveRoot`, `_contacts`), with a hidden
"forward pass ran first" contract. Three moves:

1. **Freeze the problem explicitly.** One function per solve builds an immutable
   `SolveProblem` (contacts/targets/weights, swing targets, pins, config
   snapshot, base-pose samples, baseBlend, solve root). Mostly relocating fields
   the row-count-stability contract already forces to be frozen — just scattered
   and implicit today.
2. **The forward pass returns a value.** `Forward(in SolveProblem, x, scratch)
   → PoseEval` (world transforms + angular velocities) instead of side-effecting
   animator fields. Preallocated caller-owned scratch for perf; purity by
   convention.
3. **Blocks become pure static functions.** Each constraint is
   `(in SolveProblem, in PoseEval, x, rows)` plus its paired analytic Jacobian;
   the objective is a printable list of named blocks. `PointJacobianColumns`
   survives as-is, parameterized on `PoseEval`.

Payoff beyond legibility: a per-block finite-difference checker
(`FdCheck(problem, block, x)`) falls out for free — it IS chunk 1's §2 oracle —
and per-block cost printing makes "which term fights which" one loop.
Explicitly out of scope: autodiff (no `jax.grad` in C#; hand Jacobians stay,
now machine-checked; dual-number source-gen is a separate future project).

Scope: ~2k lines (`CharacterAnimator.Constraints.cs` wholesale + orchestration
seams; the LM core and FK untouched), mechanical, behavior-preserving — guard
with a golden-trace test (record residual/Jacobian vectors for recorded frames
pre-refactor, assert bit-identical after). Sits right after chunk 1 because
chunk 1's FD oracle builds on it, chunk 5's timing solve should be born in the
new style, and chunk 8's "solve only constrained chains" becomes filtering an
explicit block list. **Do not interleave with chunk 2** — both rewrite
`CharacterAnimator.Constraints.cs`; either order, but pick one.

### 2. One placement model — runtime §3 + authoring "Coordinate and ownership contract"

**The hinge between the two plans.** Each currently defers to the other — the
runtime doc says "coordinate full X/Y anchor changes with the scene-authoring
work," the authoring doc says "update X/Y anchor handling consistently with
runtime rendering." That circular deferral means this must be its own small,
early chunk that both then consume:

- Pick the canonical com convention: draw's `dir·com.X + com.Y`
  (`AttackGlowSystem.RigRoot`) vs the solve root / `ClipStrideTrack`'s
  `com.Y`-only. Decide, don't split the difference.
- Migrate or grandfather the ~25 clips shipping nonzero authored `com.X`.
- Route the cadence solve root, draw root, `ClipStrideTrack` offsets,
  `SwingTargetConstraint`, and the editor through one shared sampler
  (`Animation/BodyPath.cs` is the contract home and already documents the
  asymmetry to collapse).

Small code, one real decision, and the best effort-to-leverage ratio in either
plan — every downstream chunk (editor ghosts, swing targets, timing travel,
step-up targets) stops needing per-consumer caveats once it exists.

### 3. Editor track — authoring: extraction → scene guides → motion dropdown → moving-loop preview

Editor-only; touches no runtime behavior. The behavior-preserving `DemoGame`
extraction can start immediately; ghost/path preview consumes chunk 2's
contract but extraction itself doesn't wait on it.

### 4. Endpoint track — authoring: resolver → endpoint menus / knife / contact UI → helper-feet removal

Kept separate from chunk 3 because the endpoint resolver is **not** editor code:
it lives in the shared animation layer and is what runtime §6 (semantic contact
identity) and §10 (hand/foot resource claims) reference. Order within the
chunk: resolver first (with legacy `ContactLabel.Node` compatibility), UI
second, helper-feet removal last — that migration is the long tail.

### 3.5. Prior-based kinematics drag mode in the editor (user request, 2026-09-09)

A header-toggled editor mode where dragging a joint node runs interactive IK
steps pulling the clicked node toward the mouse — biased toward the previous
position and the pose's original rotations — instead of the direct
rotate-one-bone edit. Speeds up posing a lot; genuinely small, because
`Animation/PoseIk.cs` (the probe `ik` command's solver) is already 90% of it:
LM over a limb chain, seed prior, per-joint bounds, graceful miss on
unreachable targets, editor-safe (offline-class, never in the game loop).

**Solve shape** (per frame while the drag is held, chain vars only, n ≤ 4):

- Target rows: clicked node's tip vs mouse in rig root-local space (2 rows).
- Prior A — *original pose*: weight toward the **drag-start** authored
  rotations (PoseIk's existing seed prior). Elastic minimal-change; error
  can't accumulate across the drag because the anchor never moves.
- Prior B — *previous position*: warm-start each frame's solve from the last
  frame's solution plus a small weight toward it. Continuity/damping so the
  limb doesn't snap between LM basins mid-drag.
- Fold-sign guard ("elbows don't bend backwards"): one-sided bound on
  `*_lower` bones keeping the bend on the drag-start side of straight —
  the same grid-IK either-fold hazard the probe workflow already documents.
  Pragmatic now; when runtime §8 defines rig-level preferred-bend/joint-limit
  metadata, this mode should consume that definition instead of its own
  (interface note — don't let two anatomical-limit vocabularies grow).

**Editor integration** (`MTile.Demo/DemoGame.cs`):

- Clickable header box (DrawHeader ~:1498) toggles the mode; picking priority
  header-first per the authoring plan's rules. Distinct hover/drag node color.
- Drag routing: in-mode, the `_dragBone` branch (~:469) calls the solve
  instead of `EditBone`; write-back hits the active keyframe's rotations
  exactly as today, so save/dirty/undo-by-Escape semantics are unchanged.
  Escape mid-drag restores the drag-start pose (authoring plan: cancelable
  drags). Draw the target cross + miss distance while held.
- Chain policy: start with `PoseIk.DefaultChain` (limb up to hip/chest).
  Evaluate feel; a held modifier extending the chain through the torso
  (graded prior weights up-chain, root always excluded) is the follow-up if
  limb-only pulls feel too local. Root/com drags keep their existing gestures.
- Hoist one solver instance + arrays out of the per-frame path (PoseIk
  allocates per call — fine for the probe, not for a 60 Hz drag).

Scope: roughly a day. Depends on nothing (PoseIk and LM core are standalone);
touches the same DemoGame interaction code chunk 3's extraction will move, so
land it either before extraction (it's small, migrates along) or as part of
the extracted interaction component — not concurrently.

### 5. Timing & stopping — runtime §4, §5

One prototype-shaped chunk behind an A/B switch. Partly unblocked already:
travel-based timing's `deltaPhase = travel / cycleDistance` is exactly
`BodyPath.TryCycleDisplacement` — but only 7 clips author a body path today, so
§4's per-clip nominal fallback is load-bearing from day one.

### 6. Ownership/lifecycle design + contact persistence — runtime §10 + §6

Split into design-early, implement-later:

- **Design doc + slot/claims schema early** (cheap, mostly writing). The runtime
  plan's own rollout note is the constraint: §10's declaration contract must be
  designed before chunks 5 and 7 are *implemented*, or settling and step-up each
  invent their own answer to "who owns phase / who owns this foot."
- **Persistent-contact / continuous-replanning implementation later**, once
  chunk 4's resolver gives contacts stable semantic identity (the adapter over
  current node labels covers the gap until then).

### 7. Step-up vertical slice — authoring pilot + downstream step-placement use + runtime §7

Where the two plans genuinely meet, and the natural showcase: author the moving
step-up scene, expose its intent through BodyPath diagnostics, then give StepUp
a maneuver-aware progress policy instead of `CadencePhase`. Wants chunk 2
(anchor parity), chunk 4 (the pilot should exercise the intended authoring
workflow, per the authoring plan's rollout), chunk 5 (so it's a second timing
*source*, not a second timing *architecture*), and chunk 6's contract.

### 8. Solver diet — runtime §8, §9

Strictly last by construction: §9 says so, and §8's "smaller problem" only
makes sense after chunks 2 and 5 settle which variables the solve still owns.
Needs chunk 1's baseline to prove anything.

## Sequencing

```
1 → 1.5 → 2 → (3 ∥ 4 ∥ 5, with 6's design doc alongside) → 6 impl → 7 → 8
```

Chunks 3, 4, 5 are genuinely parallel once 2 lands. 1.5 and 2 touch the same
file (`CharacterAnimator.Constraints.cs`) and must run sequentially in either
order; 1.5-first is listed because chunk 1's FD oracle wants the per-block
harness, and chunk 2's edits are smaller to redo in the new style than vice versa.

## Friction points (where toes get stepped on)

- **Chunks 3 and 4 both rewrite DemoGame interaction.** The extraction's
  "endpoint inspection / selection-menu plumbing" component is the same code the
  endpoint menus need — do that extraction piece before (or as the first step
  of) chunk 4, or the menu UI gets built twice.
- **Chunks 5 and 7 both take phase away from the joint solve.** If step-up
  policy work starts before the timing stage exists, two competing phase-advance
  mechanisms appear and the runtime plan's closing rule ("fewer competing
  controls, not permanently run both") is violated on arrival. Chunk 5
  establishes the timing-stage seam; chunk 7 plugs a maneuver-progress source
  into it.

## Already landed

- `Animation/BodyPath.cs` + the `edref` → `body_path` migration (7 clips): the
  shared placement sampler and cycle-displacement query that chunks 2, 3, 5,
  and 7 consume. Runtime placement still anchors via `com` alone until chunk 2.
