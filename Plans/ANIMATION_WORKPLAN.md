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
1 → 2 → (3 ∥ 4 ∥ 5, with 6's design doc alongside) → 6 impl → 7 → 8
```

Chunks 3, 4, 5 are genuinely parallel once 2 lands.

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
