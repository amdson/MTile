# Ballistic Correction Solver (direct-transcription QP)

Status (2026-07-27, branch `corrector`): ALL build steps SHIPPED, including 7-8 —
the ramp stack is GONE. Predictor (+ parity suite), constraint builder (+
fixtures), CorrectionSolver (+ oracles), the corrector climb family
(Parkour/ArcJump/Mantle on CorrectorClimbBase) behind CorrectorVaultEnabled,
trigger-by-feasibility (refusal on the true corrected rollout), AmbientCorrector
(free-coast redirect-only, passable-feature rows, feasible-plans-only
anti-autopilot, greedy shallower-sense homotopy pick) behind
AmbientCorrectorEnabled, mid-maneuver snapshot round-trip green. Removal pass:
SteeringRamp (+ StepSwept redirect hook), ReflexSystem, old
ParkourState/ClimbStateBase/MantleState/ArcJumpState, CoveredJump/Dropdown ramp
insurance, ramp config knobs and overlay all deleted; RampPolicy became
AmbientPolicy (per-state ambient gate); BallisticVy lives on BallisticPredictor;
the anti-pop exemption reads MovementVars.AmbientLiftActive. Deviations:
per-problem fixed InnerIterations (4 per tick, 128 at entry — opposed-row
schedules need the deeper fixed budget); ambient implements the appendix's
anti-autopilot rules as "feasible plans only + vertical-face row emission".
Remaining: the StandServo hover root (the tight-tunnel-mouth Skip spec in
CorrectorOperationsTests is its acceptance test), fidelity terms / authored
HermiteClip references, shift-warm-start.

Original design (steps 1-6, 9 now reflect implementation): **This is the successor architecture for movement
correction generally, not a maneuver-state add-on.** It supersedes CORRIDOR_MANEUVER_PLAN's
reflex/maneuver split: most of the current movement-handling code — ReflexSystem, RampPolicy,
SteeringRamp, the ClimbStateBase brake/lift shepherd, crest-cap plumbing, and likely several
movement states (Parkour first) — is subject to rewrite or removal as the corrector reaches
parity. Migration is incremental behind config flags (see build order), but nothing in the
current correction stack is load-bearing for the end state.

**Correction (2026-07-26):** earlier drafts treated the ambient reflex stack as the runnable
A/B baseline. It is not — vault/mantle engagement regressed when the ambient layer landed
(3412cd9: ArcJump/Mantle/VaultOneBlock/RunningOver* sim tests fail from that commit onward;
they last pass at b086912) and the reflex system does not work today. That breakage is the
main motive for this plan. Read every A/B-against-ReflexSystem reference below (bootstrap,
build steps 4/7, migration discipline) accordingly: the failing sim fixtures are the spec
the corrector must satisfy, not a parity target to match.

## Problem statement

Each tick during an authored maneuver, given the current body state, a clear-terrain
**reference arc** (indexed by progress x, not time), and the **contact roots** the maneuver
grants — solve for correction accelerations over the remaining horizon (~15–20 steps) that:

- guarantee clearance of corridor features (margin-inflated floor/ceiling corners + faces),
  with infeasibility surfacing as a graded refusal/escalation signal, never a silent clip;
- minimize deformation per-root with strict priority (physical roots before unphysical);
- stay near the reference in the contouring sense (contour offset, heading, speed at
  corresponding x — timing is free);
- stay smooth (Δu-penalized; peak force near the 2m/t² floor, saturation = urgency signal);
- run deterministically at 60 fps, allocation-free, fixed iteration counts (rollback);
- tolerate unphysical dynamics modifiers by re-predicting with the real integrator every
  tick — the linear model is only used for the correction *delta*, never for safety.

The same solve run at Enter over the full arc **is the trigger**: a maneuver may fire iff
its corrected arc is feasible. This replaces hand-tuned entry gates (speed bands, distance
thresholds) and the GateArc/BallisticProbe items in CORRIDOR_MANEUVER_PLAN.

## Two operating modes, one solver

- **Maneuver mode** (described above): authored ReferencePath, feasibility-as-trigger,
  maneuver-granted roots, fidelity cost against the reference.
- **Ambient mode** (replaces ReflexSystem + SteeringRamps): runs during free movement
  whenever a horizontal direction is held. The "reference" is the coast prediction itself —
  there is no authored arc, so the fidelity term vanishes and the cost is pure deformation
  minimality. Roots: passive Redirect everywhere (grounded AND airborne — the air-graze case
  ramps never covered), ground servo only if a maneuver granted it (ambient mode injects no
  energy, preserving the ramps' energy-honesty contract: too slow → the correction is
  infeasible → flush stall → mantle precondition, exactly the current taper handoff).
  The old Over/Under assists become emergent minimal corrections: a 1-block corner graze is
  a predicted floor-corner violation resolved by a passive deflection; a head-tuck is the
  same with a ceiling row. Crest capping falls out of the clearance rows (the landing gate
  is a constraint, not a special-cased √(2g·h) clamp).

Input-gating (released stick ⇒ no ambient corrections) and the ~2-block horizon carry over
unchanged — they are what keeps this reading as character reflexes rather than autopilot.

### Baseline locomotion & automatic posture (run/jump/duck integration)

Principle: **intent generates the reference; the solver only deforms it; the FSM classifies
the result.** Discrete intents (jump press, held direction) are the player's contract and are
never solver-initiated; continuous DOFs migrate into the solve.

- **Prediction includes the baseline drive.** Ambient-mode coast = "what baseline locomotion
  would do under current input" (run drive, air control), not pure ballistic coast — else
  every grounded tick mispredicts. The solver's output is strictly the clearance delta.
- **StandServo root**: bounded force adjusting hover height within [crouchHover, standHover]
  while grounded (ducking is hover-height via standing force, not a shape change). A low
  ceiling ahead is just a ceiling clearance row; the minimal correction IS the duck, with
  smooth early onset from the horizon + Δu term. Energy-honest (reduces support force).
  **UnderpassState/PreemptiveDuck dissolves into this row + root.**
- **Jump splits**: trigger = input press (always); shaping (vy within bounds so the arc fits
  the corridor) = JumpServo root, same intent/feasibility split as maneuver triggers.
- **Classification is downstream of physics**: the solver lowers the body; CrouchedState's
  precondition matches; it claims the frame via normal passive-priority arbitration and
  selects config/anim. States claiming this way must NOT re-drive the DOF they observed
  (no dual ownership of standing force). Classify off the solver's **predicted** profile
  ("hover below crouch height within k ticks"), not the instantaneous pose — earlier signal,
  no one-frame config lag — and give thresholds hysteresis: reclassification selects config,
  config feeds the next prediction, and that loop limit-cycles without it (see the old
  Parkour↔Mantle oscillation).

## Contact roots (as solver channels — see §4 for the channel interface)

| Root | Active when (per predicted tick) | Channel (admissible set, lever) | Cost |
|---|---|---|---|
| **Redirect** | within window of a terrain corner | RedirectDisc: v′·(v̂ₖ − v′) ≥ 0 (Thales disc of predicted v̂ₖ, rebuilt per outer pass), velocity-update lever | FREE + ε‖δv‖² (uniqueness only) — real cost is the speed the geometry charges |
| **JumpServo** | ground contact within reach (early ticks) | Force: ‖u‖ ≤ ServoMaxForce, stops adding vy past launch cap | weighted (cheapest force tier) |
| **CornerGrab** | grip corner acquired (mantle family) | Force: ‖u‖ ≤ GrabMaxForce, bilateral | weighted (dearest tier — fires when redirect + servo are exhausted) |

Tier priority is encoded as fixed cost-weight ratios in one QP, and the spread is
deliberately TIGHT (start ≈ 1 : 4 : 19 — user decision 2026-07-25). This is weighted
preference, not lexicographic: a large cheap-root correction CAN trade against a small
expensive-root one, which matches intent and keeps the quadratic well-conditioned for a
fixed-iteration first-order solver (the earlier 1:10²:10⁴ idea would have put a ≥10⁴
condition number into the Hessian). Sequential per-tier QPs remain the fallback if weighted
trades ever feel wrong.

## Components (all root-compiled into MTile.Core; pure math — KNI-safe)

### 1. ReferencePath (`Character/Corrector/ReferencePath.cs`)
An **authored cubic Hermite clip, retargeted at Enter** — not a computed ballistic arc.
Division of labor: the reference owns *feel*; physics honesty is enforced entirely by the
roots' admissible sets and force caps. The curve therefore does not need to be flyable —
it is a soft attractor via the fidelity term, never a feedforward, and no dynamics model
is involved in building it.

- Authored as a **2D cubic Hermite curve p(t)**, t ∈ [0,1], in a **normalized frame**
  (entry (0,0), gate (1,-1); unit ledge height / unit gap): keys are position + tangent
  *vectors*. Parametric, so there is no monotone-x restriction — vertical phases (mantle
  pull-up) author fine. Key t values are auto-derived from chord length; tangent
  magnitudes modulate local speed around that. Authored in the dedicated editor
  (`dotnet run --project MTile.Demo -- --ref <name>` → `ReferenceClips/<name>.json`;
  format + eval in `Character/Corrector/HermiteClip.cs`, KNI-safe).
  `ReferenceClips/` is empty as of 2026-07-26 — expect to author new clips in the editor
  as each maneuver is wired (build steps 4 and 6, and any later maneuver). Clip authoring
  is recurring per-maneuver work, not one-time setup.
- Retargeted at Enter: entry endpoint + tangent bind to the **actual entry state** (the
  entry tangent IS the incoming velocity, so entry-speed parametrization is automatic and
  continuous); exit endpoint + tangent bind to measured corridor geometry — the target
  **gate** (gates-as-targets rule from CORRIDOR_MANEUVER_PLAN holds). Target-height
  parametrization = scaling the normalized frame by the measured obstacle.
- Heading reference = curve tangent; speed reference = entry speed carried along the curve
  unless a maneuver authors an explicit profile. Fidelity errors are measured at
  corresponding **progress** (nearest reference point / monotone progress map), never at
  corresponding wall-time.
- Snapshot contract: the defining scalars (entry pos/vel, gate column/Y, clip params) live
  in **MovementVars (snapshot state)**; the dt-sampled arrays (pos, heading, |v| per
  sample) are derived cache regenerated lazily — rollback restores the scalars, the next
  frame resamples.
- Consequences accepted by design (feel > model purity): (a) steady-state tracking effort
  against an unflyable curve is nonzero, so the refusal threshold is a **per-maneuver
  knob** ("residual above this maneuver's normal tracking level"), not ≈0; (b) phases
  granting only passive roots will **sag** below energy-adding curve segments — the fix is
  authoring (shape the curve while the servo is still granted), never new machinery.

### 2. BallisticPredictor (`Character/Corrector/BallisticPredictor.cs`)
Forward-simulate the **coast** (no corrections) H steps from the current state, mirroring
PhysicsWorld.Step's exact order (AppliedForce → gravity → position) including movement
modifiers. Output p̂ₖ, v̂ₖ into pooled scratch arrays on PlayerCharacter (Corridor-scratch
idiom). This is the only place dynamics are evaluated — drag or any future modifier is
automatically respected because prediction *is* the integrator.

Scope (deliberately thin — NOT a second physics engine): integrate baseline feedforward +
gravity only. The feedforward is the SAME purified function the live tick calls (run drive,
air control, standing spring — the spring is a movement force in this codebase, so landing
resolves in prediction through the feedforward, no contact machinery needed). Excluded:
ResolveChunkCollisions and all collision *response*, contact/friction constraint
bookkeeping, FSM/actions (frozen classification). Penetration *detection* is the constraint
builder's job. Truncate prediction at the first deep violation — samples past an unavoided
impact are meaningless and their rows are moot. Predictor-owned code ≈ a 30–50 line loop;
cost center = one ground query per grounded predicted sample (H ≈ 15–20 local tile probes).

### 3. Constraint builder
End state: sweep the margin-inflated body along the predicted polyline **directly against
tiles** (local TileQuery neighborhood per sample). Extract penetrated faces/corners; the
outward normal comes from the tile's exposed faces (neighbor solidity — purely local), so
the clearance side needs no tube labeling and free-standing blocks are handled naturally
(greedy first-hit side). Relevance is defined by the prediction, not a scan pattern —
lifts the corridor's x-monotone limitation (wall slides, reversals, drops all work). Emit
up to MaxEvents (4) clearance rows {event tick kⱼ, normal n̂ⱼ, depth mⱼ}.
Migration: the existing Corridor is an acceptable first constraint feed (build steps 1–5);
**CorridorProbe is scaffolding, not architecture** — don't build new capability on it. The
removal pass shrinks it to a few-tile "ledge spotter" used only to PROPOSE maneuver
references (feasibility-as-trigger then tests them); its truncation/pinch feasibility
taxonomy dies — impossibly thin gaps are allowed to bonk honestly (user decision:
least-violation best-effort + real collision, no geometry rejection in ambient mode).

### 4. CorrectionSolver (`Character/Corrector/CorrectionSolver.cs`)

**Channel interface (the extension point for all future work).** A channel is exactly the
triple: (1) a per-tick convex admissible set with a CLOSED-FORM projection, (2) a lever
kind — velocity-update (lever (kⱼ−k)·dt) or force (lever (kⱼ−k)·dt²), (3) a quadratic cost
weight. The solver is one loop over channels and knows no physics. Adding a contact type
(StandServo, WallDrag, grab, …) = one projection function + a weight + a lever kind; zero
solver edits. Anything not expressible as this triple is not a channel — that discipline is
where solver simplicity lives.

**v1 channels (two only):**
- **RedirectDisc** (velocity-update, free + ε‖δv‖² regularizer): admissible set is the
  Thales disc — v′ redirect-reachable from v̂ₖ ⟺ v′·(v̂ₖ − v′) ≥ 0 — the EXACT reachable
  set of (composed) frictionless projections. Encodes for free: positive-dot-product rule,
  speed never added, ≤90°/tick. The ε term (fixed constant, not a knob) selects the
  least-speed-loss profile among clearance-achieving ones and makes the optimum unique
  (determinism). Replaces the passivity half-plane force model, and removes the redirect
  tier weight from the tuning surface — redirect cost is geometry, not a weight.
- **Force** (force lever, weighted, per-root cap as disc radius): the energy-adding root(s).

Both exit uniformly through Body.AppliedForce (a velocity-update δv₀ applies as force
δv₀/dt — identical under semi-implicit Euler).

- **Dynamics map** (exact under semi-implicit Euler): clearance row j is linear in all
  channel variables via the lever kinds above: Σₖ<ₖⱼ leverₖⱼ·(zₖ·n̂ⱼ) ≥ mⱼ.
- **Cost**: Σ channel-weight·‖zₖ‖² + wΔ·‖zₖ−zₖ₋₁‖² + fidelity terms (contour/heading vs
  ReferencePath at predicted x). The Δ sum is anchored at **z₋₁ = last tick's APPLIED
  correction** (one Vector2 of snapshot state) — without the anchor, cold-started re-solves
  enforce smoothness only within each plan, and constraint-set changes between ticks can
  still step the output.
- **Clearance as hinge penalty** (stiff, fixed weight — a stiffness constant, not a feel
  knob): gives least-violation best-effort output when infeasible.
- **Solver: sequential convexification, 2 outer passes × 4 inner projected-gradient
  iterations** (fixed counts, fixed order). Pass 1: rollout the coast, freeze discs/rows,
  4 PG iterations (gradient of the quadratic+hinge surrogate; project each channel in
  closed form; fixed step η = 1/L precomputed per pass). Pass 2: re-rollout WITH the
  planned corrections applied, rebuild discs/rows from that corrected trajectory (this is
  where the state-dependence of the redirect discs is honored), 4 more PG iterations.
  Freezing-per-pass keeps every subproblem convex with descent guarantees; the outer
  iteration's fixed point is a stationary point of the true nonconvex problem; the
  per-frame re-solve is the outermost pass with the real body as the iterate. Do NOT
  rebuild sets between individual gradient steps — projection onto iterate-dependent sets
  is a fixed-point scheme with no descent guarantee and can limit-cycle at activation
  boundaries; failure would be silent under fixed iteration counts.
- **Cold start at z = 0 every tick** — this already IS warm-starting from the ballistic
  trajectory (variables are corrections; the coast is the origin). Shift-warm-starting from
  last tick's solution is the profiling-gated upgrade; its buffer must then be snapshotted
  (shares the decision with the z₋₁ anchor, snapshot state regardless).
- **Output**: z₀ per channel (applied this tick), residual, per-tick profile (overlay).
  **The shipped residual — refusal, escalation, overlay — is always measured on a final
  TRUE corrected rollout, never on the surrogate** (one extra H-step rollout; the
  feasibility signal must not inherit linearization optimism).

### 5. Maneuver-state integration
- **Enter / CheckPreConditions**: build ReferencePath, run the solver over the full arc.
  Residual > threshold ⇒ refuse (state never activates). This is the trigger.
- **Update**: predict → build constraints → solve → apply u₀ through each root's mechanism
  (all via Body.AppliedForce — forces are accelerations; movement never reads action state).
  While ReflexSystem still exists during migration, publish RampPolicy.Off so ramps never
  fight the corrector; RampPolicy and the ramp stack die at parity. Cancel-on-release and
  MaxVaultTime liveness unchanged.
- **Exit**: maneuver ends when progress passes the gate; terminal transverse error (contour,
  heading, speed at gate x) is logged and handed to normal passive-priority arbitration.

### 6. Config (MovementConfig, hot-reloaded)
CorrectorHorizon, CorrectorIterations, CorrectorMargin, per-root force caps, tier weights,
wΔ, hinge stiffness, refusal threshold (**per maneuver** — calibrated above that maneuver's
normal tracking residual, see §1), plus each maneuver's normalized clip scalars.

### 7. Debug overlay (DebugOverlayRenderer, day one)
Reference arc, predicted coast polyline, violated features + normals, per-tick correction
vectors scaled, residual readout. Tuning depends on seeing it (corridor-plan precedent).

### Passive states (fall, wall slide)

Every state reduces to three answers: baseline feedforward (pure function of input+body,
shared with the predictor), roots granted, classification predicate. Passive states have
trivial answers and barely change:

- **Fall**: predicate = airborne/no-wall/no-maneuver; baseline = gravity + air control;
  roots = passive Redirect only (the air-graze case that motivated ambient reflexes). With
  no predicted violation the ambient cost's minimum is exactly zero — the solver is provably
  silent during ordinary falling. The state keeps config band + anim, nothing else.
- **Wall slide**: already secretly a contact root — sustained unilateral wall contact +
  dissipative tangential drag (u·v ≤ 0 ⇒ passive/energy-honest). Tier 1: drag stays baseline
  feedforward, solver gets no wall authority. Tier 2 (pull-driven, needs a fixture first —
  e.g. slide toward a protruding block below): promote to a WallDrag root the solver can
  modulate within bounds (brake earlier, still passive, + redirect at arrival).
- **Mode boundaries inside the horizon**: landing comes free because the standing spring is
  part of the baseline feedforward (a movement force, not collision response) — the
  predicted trajectory settles onto the floor, so landing is NOT a floor violation, with no
  contact machinery in the predictor. Hard wall/ceiling hits are violations by definition
  (truncate prediction there). Input feedforward switching does not come free: start with
  frozen-mode prediction (current classification's baseline for the whole horizon; re-solve
  heals boundary error), upgrade to per-predicted-tick classification only if boundary
  mispredictions cause visible artifacts. This is the practical argument for baselines as
  pure functions; purify the passive states' baselines first — they're the easiest.

## Determinism rules (restated for this system)
No statics; scratch pooled on PlayerCharacter; fixed iteration counts and orderings; solver
cold-starts so its only inputs are snapshot state + terrain; ReferencePath defined by
MovementVars scalars, arrays derived. Snapshot round-trip mid-maneuver must continue
bit-identically.

## Branch & reimplementation bootstrap (agreed 2026-07-25)

This is a **fresh reimplementation on a dedicated branch**, not an in-place refactor:

- Commit the current in-flight work on main first, then branch (suggested name: `corrector`).
  (Done 2026-07-26. Note: the ambient reflex system is NOT a working baseline — see the
  correction at the top; the vault/mantle fixtures fail on main.)
- New core classes are written fresh (BallisticPredictor, constraint builder,
  CorrectionSolver, ReferencePath) **together with their test classes from day one**:
  predictor-parity tests (matches SimRunner rollout sample-for-sample), constraint-builder
  fixtures (hand-computed rows), solver oracle tests (the analytic closed forms), and the
  anchor-scenario SimTerrain fixtures. The tests are not written after the classes — the
  oracle suite IS the specification of the solver.
- Existing movement files are touched only at integration points (build steps 4+); the
  purified baseline feedforwards are extracted into new shared functions rather than edited
  in place, so main and the branch stay mergeable during migration.
- **Migration discipline (the real risk is the half-migrated steady state):** every
  coexistence flag carries a decision criterion — the parity fixtures — and a decision, not
  an indefinite A/B. If ambient mode cannot beat ReflexSystem (and the corner-forgiveness
  baseline) on the anchor scenarios within bounded effort, the outcome is "maneuver-mode
  only, keep the ramps," not permanent coexistence.

## Build order (each step ships independently, test-first)

1. **BallisticPredictor + overlay.** Zero behavior change. Test: predictor output matches an
   actual SimRunner coast rollout sample-for-sample (same integrator, same modifiers).
2. **Constraint builder + overlay.** Test: ASCII fixtures (tunnel, staircase, overcrop) —
   assert event ticks, normals, depths against hand-computed values.
3. **CorrectionSolver + oracle unit tests.** Pure math, no game wiring. Oracles from the
   analytic special cases: single corner/single root ⇒ min-norm answer (needed Δv = m/t_c,
   lever (kⱼ−k)dt²); constant-spread amplitude ⇒ ≈2m/t_c²; passivity blocks upward-only
   demands ⇒ residual flags; Δu weight ⇒ no bang-bang (assert max |uₖ−uₖ₋₁|). These
   closed forms are the permanent debugging oracle for the QP.
4. **Wire ONE maneuver** — the 1-block vault family (ClimbStateBase's shepherd is replaced
   by predict/solve/apply) — behind a config flag for A/B against current behavior. Fixtures:
   the existing vault/mantle sim tests must pass with the flag on. (They currently FAIL with
   the flag off — passing them is the corrector's success criterion, not a parity check.)
5. **Trigger-by-feasibility.** Entry gates (speed bands, flush distances) become "solver
   residual ≤ threshold". Fixtures: tunnel-vault (two-corner squeeze — flattened arc, no
   clip), infeasible tunnel (state refuses, body never enters), slalom (opposed corrections
   at different event ticks — the case single-Δv designs cannot schedule).
6. **Extend**: ArcJump revival on the same solver (different reference + roots), then the
   low-roof-landing scenario (corridor-plan anchor #4) which is now just a reference arc
   whose gate is low + ceiling rows in the QP. Retire GateArc/BallisticProbe plan items.
7. **Ambient mode behind a flag**, A/B against ReflexSystem. Parity fixtures = the corridor
   plan's anchor scenarios that ramps own today: run + 1-block up (no hitch, no overshoot),
   45° staircase (no per-step kicks), level entry into 2-high tunnel (head-tuck), 1-block
   drop into tunnel mouth. The course-corridor benchmark (Skip-attributed
   HoldRight_CourseCorridor) is the integration acceptance test.
8. **Removal pass** once ambient parity holds: delete ReflexSystem, RampPolicy, SteeringRamp
   and its Engaged/redirect machinery, the ClimbStateBase shepherd, crest-cap plumbing
   (BallisticVy call sites), and dissolve ParkourState (its anim contract — AnimTag.Parkour,
   vault progress, hand grip — moves to whatever state hosts the corrector at that moment).
   The corner/ledge checkers shrink to wrappers over the corridor or die.
9. **Snapshot determinism test** mid-maneuver (extends SnapshotRoundTrip pattern) + perf
   measurement (budget: well under 0.1 ms/player/tick; H·iterations ≈ 160 cheap ops —
   ambient mode adds one solve per held-direction player tick, same budget).

## Anchor scenarios (SimTerrain fixtures, written before the code they exercise)
- **Tunnel vault** (the design's motivating case): 2-high ledge inside a tunnel, overhang
  corners clipping the authored arc by a small margin ⇒ flattened arc, clears both, lands
  in gate, redirect-tier only, grab never fires.
- **Refusal**: same tunnel, gap too tight for any admissible correction ⇒ maneuver never
  triggers; body runs into the wall like a wall (no half-committed clip).
- **Slalom**: ceiling corner then floor corner needing opposed deflections ⇒ scheduled
  profile (down early, up late), both cleared, smooth force trace.
- **Smoothness**: single-corner graze ⇒ assert peak force ≤ c·2m/t_c² and no sign flips.

## Non-goals
- No route planning / homotopy choice: clearance sides come from exposed tile faces at the
  first predicted hit — greedy and local by design. A free-standing block is eased around
  on whichever side the prediction meets; no optimal-side search.
- Horizon stays within the corridor window (~2 blocks / ≤20 ticks); no anticipatory braking
  for geometry beyond it.










NOTES FOR FUTURE WORK (DO NOT IMPLEMENT)

### Anti-autopilot policy (ambient mode)

The corrector's job is corner CLEARANCE, not collision avoidance — running into a wall is
the commanded outcome, not a problem to correct. Two rules make intent-opposing assists
structurally impossible (not tuned away):

1. **Row emission filter — passable features only.** A predicted violation gets a clearance
   row only if an admissible deflection clears it while preserving progress along the held
   input direction. Corners/lips qualify; a blocking wall face does not — no row, prediction
   truncates at impact, full-speed honest bonk.
2. **Ambient drops infeasible rows; only maneuvers get best-effort.** Least-violation
   best-effort is correct mid-commitment (maneuver mode) but in ambient mode best-effort
   against an unavoidable collision IS the autopilot slowdown (e.g. forcefully braking a
   player who is deliberately running at a wall). If the required deflection exceeds passive
   authority, delete the row and let physics deliver the collision.

Net guarantee: ambient corrections never oppose the input direction — the system only helps
the player go where they were already going. Residual intent-volatility risk (player
reverses/jumps mid-horizon) is bounded by input-gating + the short horizon + re-solve;
evaluate in feel testing, not in more machinery.

### Corner forgiveness (complement, not substitute)

Genre-standard nudges (Celeste-style corner correction: shift the body a few px when a
collision barely clips; beveled response on convex corners) are cheap, contact-time-local,
and immune to intent-volatility by construction. Worth a spike behind a flag as a BASELINE
the ambient corrector must beat on the anchor scenarios. They are not a substitute: the
core problem this plan exists for is smooth movement across non-smooth terrain WITHOUT
hand-tuning forces inside movement states (arcs shaped before contact, tunnels threaded,
speed traded for height) — nudges do none of that.