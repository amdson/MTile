# Step planner — implementation plan

Status: **P0–P3 built and green, 2026-09-05** (walk + stepup live behind `PlannerEnabled`);
P4 (transitions/coverage) open. Deviations from the proposal, discovered while building:
`FootStrideTrack.MaxReachRig` is 1.25× the clip's max authored |body→foot| extension, not a
bone-length sum — offsets live in the com frame and a hip-rooted chain sum undershoots by
the com-to-hip distance (every candidate failed the reach gate); and the swing-path check
carries a 3px `SwingProbeLift` tolerance — authored swings legitimately graze or dip
sub-tile amounts below tread tops (ground-hold's business), only tile-scale obstruction
rejects a landing.

## Cadence patch disposition (the P3 deliverable)

For planner-owned feet (all five patches stay untouched for legacy SelfPlant clips):

- **Engage ramp (`ContactEngageTime`)** — SURVIVES, relocated: the planner's stance weight
  ramps from 0 with the same config constant, so the landing-jerk protection is identical
  (`AnimPlannedGaitTests` walk parity).
- **Time-fade release floor (foot-swap deadlock)** — RETIRED for planned feet: release is
  the planner's schedule (liftoff phase / support invalidation), never a φ-feather stall,
  so the deadlock it patched cannot arise. The mirror removes the contact outright.
- **Prompt toe-off (no release slew)** — SURVIVES as the mirror's rule: leaving stance
  removes the contact immediately, matching the documented 2026-08-26 decision.
- **Frozen weights inside the solve** — SURVIVES untouched: mirrored contacts carry a
  frozen per-frame weight like every other; the self-deleting-constraint hazard is gone
  by construction (weight no longer derives from φ at all).
- **Momentum prior / phase accel box / rate floor** — SURVIVE untouched: Δφ is still
  solved from no-slip rows; the planner owns lifecycle, not phase (ground rule 5).
Companion to [ANIMATION_STEP_PLANNER_PLAN.md](ANIMATION_STEP_PLANNER_PLAN.md) (the design
doc — objective, authoring contract, fallback policy). This doc is the concrete build:
modules, data types, integration points, phases, tests. Optimized hard for **simplicity
and modularity**: four small new files, one enum value, one new constraint block, zero
Demo-editor changes, and a knob budget of three.

## Ground rules

1. **Modules are pure or have one owner.** Derived data (`ClipStrideTrack`) and terrain
   queries (`SupportQuery`) are pure functions. Mutable per-character state lives in ONE
   object (`StepPlanner`) with an explicit inputs struct — no reference to
   `CharacterAnimator`, so it unit-tests headless.
2. **No hardcoded edge cases.** Every planner rule is either a *structural invariant*
   (stances alternate with swings; a support segment must exist and contain the foot) or
   a *scored preference* (one scalar score, below). Forbidden outright: terrain-class
   conditionals ("if staircase…"), clip-name lists, per-clip solver weights, special
   step heights. Feasibility limits are derived from rig bone lengths, never per-clip
   constants. If a behavior can't be expressed as an invariant or a score term, it
   doesn't go in — it becomes a reported infeasibility instead (design doc's fallback
   policy).
3. **The Demo editor does not change.** Authoring is the existing probe `contact` op
   plus one optional token. The editor keeps displaying contact labels exactly as it
   does today. Any richer contact UI is deferred until the runtime has proven the
   authoring contract is right (design doc step 5).
4. **Knob budget: 3.** `PlannerEnabled` (master A/B), `PlannerHysteresis` (score bonus
   for keeping the previous tread), `PlannerLateSwingLock` (phase fraction after which
   a valid target won't be swapped). All in `AnimSolverConfig` (hot-reloads, render-only
   — always safe). Engage/release ramps reuse the existing `ContactEngageTime` /
   `ContactReleaseTime`; selection preferences that would otherwise be knobs are fixed
   unit weights until playtesting proves otherwise. *Chunk 6 (2026-09-11) added a fourth,
   `PlannerReplanDistance` — the wish movement that reopens a committed landing — and the
   fixed `MaxLandingMiss` (1.5 tiles); see Plans/ANIMATION_OWNERSHIP_CONTRACT.md §7.*
5. **Δφ cadence solve is untouched in the first live slice.** The planner takes over
   contact *lifecycle* (when/where feet plant and release); the solve keeps deriving Δφ
   from the no-slip rows of whatever contacts exist. The full cadence-ownership
   inversion in the design doc happens only if lifecycle ownership alone proves
   insufficient — don't build it speculatively.

## Modules

| File | Kind | Depends on |
|---|---|---|
| `Animation/ClipStrideTrack.cs` | pure derived data + compiler | AnimationDocument, Skeleton, AnimationSampler |
| `Animation/SupportQuery.cs` | pure static query | ChunkMap |
| `Animation/StepPlanner.cs` | per-character state, explicit I/O | ClipStrideTrack, SupportQuery |
| `Drawing/LatticePathSampler.cs` | host-side adapter (~30 lines) | CorrectorScratch |

Plus surgical touches to existing files: `ContactLabel.cs` (one enum value),
`CharacterAnimator.cs` (own a planner, one ownership check in `RefreshContacts`, one
constraint block), `CharacterAnimSample.cs` (two fields), `MTile.Probe/Program.cs`
(one token + one read-only op), `CorrectorScratch.cs`/`LatticeTracker.cs` (two
diagnostic fields).

### 1. `ClipStrideTrack` — compile authored labels into stride data

Pure function of `(AnimationDocument, Skeleton)`; cached per document *reference* (an
editor-reloaded doc is a new reference → recompiles; no invalidation protocol needed).

```csharp
sealed class ClipStrideTrack
{
    public readonly FootTrack[] Feet;          // one per opted-in foot bone
    public static bool TryCompile(AnimationDocument doc, Skeleton rig,
                                  out ClipStrideTrack track, out string error);
}
struct Stance   { float Touchdown, Liftoff;    // phases, unwrapped across the loop seam
                  Vector2 TdOffset, LoOffset;  // preferred foot pos, body-relative (com frame)
                  bool Persistent; }           // spans the whole cycle (idle) — maintain, don't stride
struct FootTrack { int Bone; Stance[] Stances;
                   Vector2[] SwingShape; }     // authored swing sampled between stances,
                                               // normalized to its own chord (never a parabola)
```

Opt-in = any `ContactLabel` on that foot with `Source: PlannedSupport`. Body-relative
extraction uses the SAME root construction as the live solve (`com` anchor via
`SampleNamedPoint`, scale, facing — `CharacterAnimator.cs:620-624`); factor that root
math into a small shared helper rather than duplicating it.

Validation is structural only: labels resolve to bones; stances have nonzero duration
and alternate with swings; coincident swing endpoints fall back to a vertical-only
shape. Any violation → `TryCompile` false with a message; the clip **stays on the
legacy path** and the probe reports why. No partial compiles.

### 2. `SupportQuery` — finite treads with identity

```csharp
struct SupportSegment { Vector2 A, B; Vector2 Normal; long Id; }   // Id packs cell + face
static int  QueryTreads(ChunkMap chunks, Vector2 center, float radius, Span<SupportSegment> dst);
static bool Revalidate(ChunkMap chunks, in SupportSegment s);      // tile still solid+exposed
```

Same exposed-face walk `TerrainSurfaces` already does (reading `ChunkMap` from the
render layer is established precedent), but returning finite upward-facing segments
with identity instead of infinite half-planes. Keep it a sibling file, sharing face
enumeration helpers if extraction wants them — do NOT complicate `TerrainSurfaces`'
no-penetration contract. Static solid tiles only; sprouting/moving cells are simply
not emitted (the planner then reports "no support", per the design doc's boundary —
no special handling, absence IS the policy).

### 3. `StepPlanner` — the one stateful module

```csharp
struct PlannerInputs {
    Vector2 BodyPos, BodyVel; float Phase, NominalRate, Dt; int Facing;
    ClipStrideTrack Track; ChunkMap Chunks;
    Func<float, Vector2> PredictAt;            // dt-ahead → predicted body pos (never null;
}                                              // caller supplies velocity fallback)
struct FootPlan {
    PlanState State;                           // Unplanned | Swing | Stance
    Vector2 Target;                            // swing: this frame's trajectory point;
                                               // stance: the fixed world support point
    SupportSegment Support; float Weight;      // ramped by Engage/ReleaseTime
    RejectReason Reject;                       // None | NoSupport | Unreachable | SwingBlocked …
}
void Update(in PlannerInputs inp, Span<FootPlan> plans);   // one call per frame, no allocation
```

Per foot, per frame: find the active/next stance from phase; predict the body at
authored touchdown via `PredictAt`; transform the authored offset; query treads around
it; score candidates with **one scalar**:

```
score = |candidate − preferred| + reachPenalty(candidate) + (prev tread ? −PlannerHysteresis : 0)
```

`reachPenalty` is derived from rig leg chain length (min/max reach band) — infinite
outside the band. Swing feasibility = sampling the reconstructed `SwingShape` between
actual takeoff and the candidate and checking clearance against the same tread set;
fail → next candidate, none → `Unplanned` with a reason. After
`PlannerLateSwingLock` of the swing, a still-valid target is frozen. Stance:
`Revalidate` each frame; invalid → release (state → Unplanned, weight ramps out).

That's the whole algorithm. Prediction quality, support identity, and the score are
the only levers — no per-terrain or per-gait branches anywhere.

### 4. Prediction — a delegate, sourced host-side

`CharacterAnimSample` gains `Func<float, Vector2> PredictAt` (nullable; animator
substitutes velocity extrapolation when null). `Drawing/LatticePathSampler.cs` builds
it in `CosmeticUpdateSystem` (the seam that already runs `TerrainSurfaces.Extract`,
`CosmeticUpdateSystem.cs:93`) from the lattice path when fresh, else velocity.
**Not `BallisticPredictor`** — excluded by owner's call (see the design doc and the
memory note).

Tiny sim-side enabler: `CorrectorScratch` gains `LatticePathCount` / `LatticePathFrame`,
written unconditionally by `LatticeTracker` after its solve (today the count is only
kept under `CaptureTrajectories`). Two write-only diagnostic ints; no sim behavior
change, nothing snapshot-relevant.

### 5. Solver integration — one enum value, one ownership check, one block

- `ContactSource.PlannedSupport` (schema already serializes `Source`; legacy JSON
  defaults to `SelfPlant`, untouched).
- **Ownership check** in `RefreshContacts` (`CharacterAnimator.cs:956`): a foot whose
  active clip compiled a `FootTrack` is *planner-owned* — skip capture/release for it
  entirely. The animator instead mirrors that foot's `FootPlan` into `_contacts`
  (stance ⇒ ensure a `PlannedSupport` contact at `Target` with the plan's weight;
  otherwise ⇒ none). Non-owned bones and legacy clips run the existing path,
  bit-for-bit. This is the entire lifecycle handover — the capture-side patches
  (engage ramp) become planner weight policy using the same config constants; the
  release-side patches simply never see planner-owned bones.
- **`SwingTargetConstraint : ISolveConstraint`**, appended to `_coreGeom`: 2 rows per
  currently-swinging planner-owned foot (0 rows when none — same frame-frozen pattern
  as `NoPenetrationConstraint`). Point Jacobian from `PointJacobianColumns`, weight
  well below contact weight, target frozen per frame. It must NOT touch `SkipPair`,
  no-slip, or δ — it is a soft "follow the swing" pull only.
- Stance contacts flow through the **unchanged** `PlantedContactsConstraint`, so Δφ,
  the momentum prior, and the accel box behave exactly as today (ground rule 5).

## Phases

Each phase ships green and observable before the next starts.

**P0 — schema + probe (half a day).** `PlannedSupport` enum value; probe
`contact <clip> <t> <node|none> [planned]`. No runtime change (no clip is opted in).

**P1 — stride tracks, no consumption.** `ClipStrideTrack` + probe op
`stride <clip>` dumping events/offsets/swing shapes/compile errors. Tests:
`AnimStrideTrackTests` — loop-seam unwrap, persistent stance, alternation violations,
com-frame agreement (extract → re-place at a known root → matches sampled FK).

**P2 — plan without deforming.** `SupportQuery`, `StepPlanner`, `LatticePathSampler`,
the two `CharacterAnimSample` fields, the two `CorrectorScratch` fields. Animator runs
the planner and hands plans ONLY to a debug overlay (targets, chosen treads, predicted
touchdown, reject reasons — `DebugOverlayRenderer`, same style as the lattice overlays)
and to diagnostics. Solve untouched. Tests: `AnimSupportQueryTests` (finite bounds,
identity, revalidation after tile removal), `AnimStepPlannerTests` (headless scripted
motion: stable selection, hysteresis, late-swing lock, invalidation, reject reporting).
Add a planner slot to `MTile.Bench` AnimDiag while here — cost target: small next to
`TerrainSurfaces.Extract`.

**P3 — one gait live.** Opt in `walk` and `stepup` labels via probe (reviewing their
timing against the design doc's warning about legacy compensation labels). Ownership
check + plan mirroring + `SwingTargetConstraint`. `PlannerEnabled` gates the whole
thing for live A/B against the legacy path on the same build. Deliverable alongside:
the **patch disposition list** — for each of {time-fade release floor, engage ramp,
frozen weights, momentum prior, accel box}: survives / retired-for-planner-owned-feet,
each with the behavioral test that justifies it. Tests: `AnimPlannedGaitTests` —
flat walk parity (planner vs legacy within tolerance), single step, repeated stairs,
descent, stop/start/reversal, tile removed mid-stance.

**P4 — transitions + coverage.** At clip switch, preserve planner-owned stance
contacts whose foot is also opted-in in the incoming clip (replace the unconditional
`_contacts.Clear()` at `CharacterAnimator.cs:469` with the by-foot-identity rule; the
other two clear sites, lines 652/1140, are non-locomotion paths and stay). Flight and
overlay interaction tests; then opt in further gaits clip-by-clip. Moving/growing
supports stay out (design doc: needs provider identity — a later, separate plan).

## Explicitly not in scope

Cadence-ownership inversion beyond lifecycle (ground rule 5); multi-step lookahead;
moving/growing supports; any Demo editor UI; any new authored JSON fields beyond the
`Source` value; per-clip tuning. If P3 playtests demand more than the three knobs,
that is a design-doc conversation, not a config addition.

## Test-group note

New test classes are prefixed `Anim…` so they match the `animation` group's name terms
in `scripts/test-group.py` ("Anim", …) — a class matching no term only runs in the full
sweep (CLAUDE.md's silent-rot warning).
