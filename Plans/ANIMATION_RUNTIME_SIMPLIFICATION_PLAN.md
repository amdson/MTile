# Animation runtime: correctness, smoother locomotion, and cheaper solving

Status: proposed, 2026-09-09. Source-review findings and experiments; not an
implementation report. No runtime changes or new performance measurements were
made for this plan.

## Objective and boundaries

Improve running, step-up, and stopping while reducing per-character animation
cost. Preserve adaptive playback rate and the ability to meet terrain/contact
constraints. Numerical IK remains a valid tool; the proposal is to give it a
smaller, more consistent problem rather than remove it categorically.

Companion: [scene authoring plan](ANIMATION_SCENE_AUTHORING_PLAN.md), being worked
on separately. Reuse its shared body-path and endpoint contracts as they land;
do not create a competing format or edit the editor as part of this work.
Runtime changes should remain opt-in until compared against the current path.
Simulation movement/collision authority remains with the movement and corrector
systems. Authored displacement guides animation; it is not added to the simulated
body's position a second time.

## Proposed changes, in priority order

### 1. Establish a reproducible baseline

Use existing `MTile.Bench`, animation diagnostics, tests, and take playback. Record
the code/config/clip versions because these systems and the source clips are
currently changing. Historical numbers in `PERF_AUDIT.md` are context, not a fresh
baseline.

Include biped and rabbit: steady walk/run; acceleration; gradual deceleration;
abrupt stop at several gait phases; restart while settling; run↔walk↔step-up;
stairs at different speeds; low ceiling; support loss; and an overlay while moving.

Measure animation time separately from simulation, including p50/p95/p99 and
worst frames, residual/Jacobian evaluations, iterations, rejected trials, rows,
variables, and allocations. Include phase seed-search work outside `Minimize`'s
counters. Check target browser performance as well as desktop Release builds.

For quality, record phase/rate, contact identities and weights, planned targets,
rendered foot positions, root corrections, joint corrections, slip, penetration,
and target/foot velocity discontinuities. Review playback beside the numbers:
lower objective cost alone does not establish better motion. Set numerical
acceptance budgets from this baseline before promoting experiments.

### 2. Correct the phase Jacobian for animated stretch

Source finding: `AnimationSampler.SampleSmooth` varies local translation with
authored stretch, while `PointJacobianColumns` derives its phase column from
angular velocity alone. Rabbit running clips contain animated stretch. The
analytic phase derivative therefore omits a component of the actual sampled motion.

Extend the sampled-pose derivative to include local translation/stretch and
propagate it through FK and composition. First use a finite-difference phase-column
oracle on actual clips to establish the error and validate the correction; a
numerical phase column is also a possible temporary diagnostic implementation.
Do not replace the whole analytic Jacobian with finite differences.

Check between keys, loop seams, reflected facing, strut sign changes, and overlays.
Existing analytic-vs-FD tests should be extended to these channels, not merely
rerun on rotation-only examples. Measure whether rejected trials and solve cost
improve after correctness is restored.

### 3. Make the solved pose and rendered pose share one placement model

Two source-level discrepancies need explicit regression cases:

- The cadence solve fixes its root using `com` at entry phase, while the emitted
  CoM reference is sampled after phase advances. A varying `com.Y` changes placement.
- `SwingTargetConstraint` omits the solved body offsets even though those offsets
  move the rendered swing foot. Its target is expressed in world space.

Use the shared placement contract for solving, diagnostics, and rendering. If
phase remains an optimization variable, sample the anchor at candidate phase and
include its derivative. If timing is resolved first, freeze the root at that
resolved phase. Coordinate full X/Y anchor changes with legacy clip compatibility
in the scene-authoring work.

Preserve the intended policy that swing feet do not pull the body around, but
express it through variable ownership/solve ordering: determine the body offset,
then fit swing limbs in that resulting frame. Omitting a displacement from a
world-space residual is not a substitute for that policy.

### 4. Separate playback timing from full-body pose correction

Current `SolvePhaseStepLm` searches eleven phase candidates, then jointly solves
phase, root offsets, and all bone rotations. Contact/planner state is prepared
before phase advances. The maximum phase step is 0.25; correction smoothness does
not penalize clip playback itself. These are plausible contributors to sudden
phase changes and timing/contact disagreements, not measured attribution yet.

Prototype a timing stage that produces one proposed phase advance before pose IK.
Compare two bounded alternatives on the same scenarios:

1. **Travel-based timing:** use actual body travel and authored cycle displacement
   to advance phase. Begin with `deltaPhase = travel / cycleDistance` for suitable
   locomotion cycles. A monotonic authored travel-to-phase curve can preserve
   intentional variation within the cycle better than one constant stride length.
2. **Small numerical timing solve:** refine the travel-based estimate in a local
   one-dimensional interval. Match supporting-foot authored travel against actual
   body displacement, with rate continuity and a preference for the nominal rate.
   Keep root and joint corrections out of this timing objective.

Do not infer cadence from a turning-point derivative alone. Handle absent support,
double support, near-zero authored displacement, backward travel, and lateral or
vertical maneuvers explicitly. Use a per-clip nominal fallback for legacy clips.
Do not invert a non-monotonic body-bob path as if it were forward progress.

Represent continuity in phase rate (cycles/second), accounting for actual `dt`;
do not compare raw per-frame phase increments across unequal frame durations.
Keep phase unwrapped for event traversal and cycle displacement accounting.
Bound refinement to a neighborhood that cannot jump to another gait solution.

After timing resolves, process all crossed touchdown/liftoff events and evaluate
pose/contact targets at the same resulting phase. Predict using the timing stage's
chosen rate, rather than last frame's jointly solved rate. Large time steps must
not silently skip contacts; traverse events or substep their state updates.

### 5. Add explicit deceleration and stopping behavior

Adaptive timing is required. As the body gradually slows, reduce the travel-driven
gait rate. However, pure distance advancement would freeze a foot mid-swing when
travel reaches zero. Stopping therefore needs a short stateful policy:

| State | Timing and contact behavior |
|---|---|
| Traveling | Track actual travel, with bounded cadence adjustment |
| Settling | Preserve valid stance support; finish/shorten the active swing toward a reachable landing on a bounded time schedule |
| Supported idle | Retain the settled support points and blend into idle |

Enter settling using speed and stopping intent with hysteresis. Continue handling
residual travel during settling; do not plant an unreachable foot while the body
still moves. A planted or double-support stop may settle directly. A flight-phase
stop needs landing/support acquisition before declaring supported idle.

Keep position and velocity continuous when shortening a swing. The simplest pilot
can finish the current step and blend to idle; introduce dedicated stopping clips
only if that cannot meet the quality target. Restarting during settling must
preserve the current foot trajectory/support and rejoin a compatible gait phase.
An impossible contact should release or tolerate bounded slip instead of forcing
extreme joint bends. Terrain validity still governs the landing.

### 6. Preserve support and make replanning continuous

Current clip changes clear contacts and reset `StepPlanner`. Preserve compatible
stance support across gait changes by semantic foot/contact identity, target
validity, reach, and incoming contact phase. Do not carry a plant into an incoming
swing just because the foot name matches. Use endpoint roles from the companion
work when available, with a compatibility adapter for current node labels.

Current swing targets can change on tread reselection, on movement within the same
tread, or on fallback after support loss. Hysteresis and the 80% late lock reduce
some switching but do not ensure trajectory continuity.

Commit a landing point early when feasible. Replan only for a material prediction
change, obstruction, or invalid support. Construct the replacement trajectory
from the current foot position and velocity over the remaining time. Smooth
transitions into fallback too. Revalidate the replacement path for clearance;
interpolating between two individually clear targets does not ensure a clear path.

### 7. Give step-up its own progress/placement policy

The current StepUp driver chooses `CadencePhase`. Test a maneuver-aware policy
that coordinates approach, lift, edge passage, and touchdown with actual movement
progress and the selected support. Reuse the moving-CoM step-up pilot as reference
intent, mapped to real obstacle geometry and the corrector's actual path.

Do not assume a repeating stair gait and a one-off curb step are interchangeable.
Decide which the current clip represents, and retain cadence where repeated steps
need it. A progress-driven segment must handle pauses, cancellation, and resumption
without advancing a plant through an obstacle or jumping backwards in the clip.
Avoid spending pose-solver iterations compensating for the wrong maneuver phase.

### 8. Reduce the pose solve and enforce anatomical preferences

Current correction limits allow approximately ±183° on every bone. Pose priors
alone do not enforce knee/elbow bend direction or anatomical joint limits.

Pilot ordinary locomotion with a small body-offset calculation followed by
two-bone leg IK, retaining authored bend direction and limiting extension. Compare
against a reduced numerical solve over the same relevant chains. Coordinate the
body correction with both supports rather than sequentially making each leg move
the pelvis independently. Account for rabbit struts and animated lengths.

Keep general numerical IK for genuinely coupled constraints such as simultaneous
hand and foot support. Add rig-appropriate joint limits/preferred bends there.
Retain decorative/yaw channels as authored unless a demonstrated constraint needs
to change them. Anatomical limits must follow this rig's transform convention,
including facing, rather than imposing arbitrary world-space angle clamps.

### 9. Optimize the remaining numerical work

After correctness and variable ownership are established:

- Solve only constrained chains and necessary shared ancestors/body variables.
  Resolve independent prior-only variables analytically where equivalent.
- Remove permanently irrelevant residual rows when assembling a solve. Keep its
  row layout stable during iterations, including candidate collision constraints
  that can activate as the pose moves.
- Compare the existing QR and higher-precision alternatives with scalar normal
  equations on representative clips. Do not simply enable vectorization in the
  cadence path: existing comments document numerical sensitivity there.
- Add measured convergence/budget criteria to remaining dynamic solves. Evaluate
  rebased prior-frame corrections as warm starts, retaining valid bounds and a
  safe baseline candidate; raw previous offsets are not valid across arbitrary
  phase/clip changes.
- Reuse sampling/FK work where profiling justifies it. Measure total frame savings
  rather than assuming callback optimization dominates dense algebra.

### 10. Select compatible clips through ownership and lifecycle rules

Use the existing movement/action system's vocabulary: **preconditions,
conditions, and exit behavior**. Reuse suitable lifecycle patterns after inspecting
their implementation; this proposal does not require animation to inherit gameplay
state classes or write gameplay conditions. Movement/actions remain authoritative
about what the character is doing. Animation selects and composes compatible
visual representations of those decisions.

Multiple eligible clips are normal. Separate eligibility, preference, and switch
timing. For example, a nearby step makes step-up a candidate; a selected higher
landing and movement intent make it preferable to stand. A stopping character
mid-swing may need to finish a shortened step before standing. Geometric validity
alone does not establish the best clip.

Use a small declaration of tags and ownership rules instead of a pairwise
incompatibility table:

| Declaration | Meaning |
|---|---|
| Descriptive tags | Search/filter labels such as running, attack, or traversal |
| Exclusive slot | One logical owner, such as base locomotion or right-hand action |
| Resource claims | Exclusive use of a hand or authority over the foot-support plan |
| Pose contribution | Base, regional override, or additive; bone mask and weight |
| Timing source | Locomotion phase, action progress, maneuver progress, or local clock |
| Transition policy | Priority, commitment window, and allowed interruption reasons |

Descriptive tags do not implicitly acquire resources. Bone overlap alone does
not imply exclusion: upper-body attack motion can override a full-body run pose.
Conversely, separate pose layers must not supply competing world targets for the
same endpoint. Keep one authoritative contact target per endpoint and one timing
owner per movement task. Independent action layers can have independent clocks.

Illustrative declarations, not final serialized schema:

```
run:
  slot: locomotion
  claims: foot_support_plan
  pose: full_body_base
  timing: locomotion_phase

knife_slash:
  slot: right_hand_action
  claims: right_hand
  pose: upper_body_override
  timing: action_progress

ledge_pull:
  slot: locomotion
  claims: left_hand, right_hand, foot_support_plan
  pose: full_body_base
  timing: maneuver_progress
```

A claim requires availability and reserves the resource for the admitted layer;
checking “hand free” without reserving it is insufficient. Claims must reflect the
actual clip/action: a two-handed slash claims both hands. Foot-support-plan
ownership delegates its individual targets to the planner, rather than creating a
second competing foot controller. Resolve all claims together before mutating the
active set, so a rejected candidate cannot partially acquire resources.

Expected combinations:

- Run/step-up plus a one-handed slash can compose when that hand is available.
- Ledge pull and a slash using a required support hand conflict.
- Stand and step-up compete for locomotion ownership; their poses may crossfade
  during a handoff, but their support plans do not independently run the feet.
- Recoil can be additive within its declared region and correction budget.

#### Lifecycle

| Stage | Responsibility |
|---|---|
| Preconditions | Check gameplay intent, entry support/phase, target reach/clearance, and resource availability |
| Enter | Acquire claims, choose entry phase, accept compatible transferred contacts, initialize blend state |
| Conditions | Check continued support/target validity and whether the gameplay request still applies |
| Exit/handoff | Distinguish completion, replacement, and forced interruption; transfer valid contacts, release remaining claims, and define visual blend-out |

An outgoing pose may remain visible while blending out after losing logical
ownership. Its fading pose must not continue issuing contact targets or competing
timing decisions. Keep gameplay action outcomes outside this visual lifecycle:
animation admission/rejection must not silently cancel attacks or alter movement.
If an authoritative action has no compatible visual representation, report that
missing case and use a defined fallback rather than override gameplay implicitly.

#### Selection and persistence

1. Gather candidates from authoritative movement/action requests and available clips.
2. Reject failed preconditions and incompatible resource combinations.
3. Rank eligible alternatives by intent/trajectory fit, entry-support compatibility,
   pose/velocity continuity, and explicit priority. Keep impossible reach/clearance
   as rejection conditions, not tradeable costs.
4. Prefer continuing a valid selection. Require a meaningful improvement to switch,
   and favor contact-compatible event boundaries. Invalid support and mandatory
   gameplay interruptions can break commitment immediately.
5. Commit the compatible set, perform handoffs, compose poses through the existing
   overlay stack, then solve against the resulting authoritative targets.

For the initial small candidate set, use explicit stable priorities and tie-breaks;
do not introduce a general combinatorial search or another nonlinear optimizer.
Extend `Region`, `OffRegionWeight`, move-driver selection, and overlay composition
with these semantics rather than building a parallel animation framework. Pose
blending handles compatible representations; it cannot reconcile contradictory
intentions such as pinning and swinging the same hand.

Add concise diagnostics: eligible/rejected candidates, rejection reason, winning
priority/score, active claims, and pending handoff. Important validation scenarios
are run+slash, step-up+slash, ledge-hand conflicts, stationary beside a step,
stopping mid-step, and repeated threshold crossings without selection flicker.
Verify failed admission leaves no claims behind, interrupted layers release only
their own resources, and a crossfade never creates duplicate endpoint authorities.

## Rollout and acceptance

Implement as separate reviewable changes: baseline; derivative/placement fixes;
timing and stopping experiment; persistent contacts/replanning; ownership/lifecycle
selection; step-up policy; smaller pose solve; remaining numerical optimizations.
Item 10's declaration and handoff contract should be designed alongside items 5–7,
before their implementations establish incompatible ownership rules. Coordinate with in-flight
authoring changes instead of bulk rewriting source clips or generated rabbit files.

The experiment succeeds when deceleration settles into idle without a suspended
foot, transitions preserve valid support, rendered targets agree with solved
targets, and running/step-up show fewer velocity jumps and extreme corrections.
It must also improve measured cost within the chosen frame budget, without new
material penetration, slip, overlay, or maneuver regressions.
Compatible movement/action clips must compose, conflicting resource claims must
resolve predictably, and animation choices must preserve gameplay authority.

Keep A/B switches during evaluation, then remove superseded phase-floor,
acceleration, seed-search, and smoothing mechanisms only after identifying which
new component owns each former responsibility. The final system should have fewer
competing controls, not permanently run both timing architectures.
