# Animation solver — polish & consolidation plan

Fixes agreed from the 2026-07-14 system review, before any new solver capability lands
(new directions live in `Plans/ANIMATION_DIRECTIONS.md`). Ordered so each item is
independently shippable; item 1 is the substantive one, the rest are consolidation.

Everything here is render-only (the `CharacterAnimSample` boundary is untouched), so all
of it is A/B-able live and carries no determinism risk.

---

## 1. Retire the stiffness/ease system — smoothing becomes a solver term — DONE (2026-07-14)

**Implemented essentially as specced below, with one load-bearing reformulation found by
the tests:** smoothness must be measured in DEVIATION space, not absolute-pose space.
The specced residual (final angle vs last emitted angle) charges clip PLAYBACK itself —
Δφ advancing the walk IS pose change — and measurably dragged the run cadence to a crawl
(phase 0.12 over 60 frames at 90px/s). The shipped residual is
`√λs_i·(Δθ_i − t_i)`, `t_i = wrapAngle(emitted_i − composedEntry_i)` (the deviation of
last frame's emitted pose from THIS frame's composed base at the entry phase, a per-solve
constant — `FillSmoothTargets`). Playback is free; deviations ease exactly; clip switches
still bridge (right after a switch, emitted − newBase is the whole pose gap); and the row
is diagonal + Δφ-free. What shipped:
- `BlendToward` deleted; `_pose.CopyFrom(_target)` — the solved pose IS the rendered pose.
- λs_i derived per frame: `λp_i·(1−b_i)/b_i`, `b_i = 1−exp(−k_i·dt)` (`Stiffness` /
  ActionWeight-ramped `UpperBodyStiffness` on the upper mask). The `ThetaSmooth` config
  knob is GONE — tune feel via the stiffness constants. Unconstrained bones reproduce the
  old ease exactly (λp cancels); constrained bones satisfy pins/contacts on the RENDERED
  pose (vault rendered reach ≈ solved reach now — the pin-lag defect is closed).
- Frames with no geometric rows skip LM: the objective is diagonal and its optimum is the
  closed-form ease (`θ = emitted + b·wrap(target − emitted)`), applied directly in Update.
- `_thetaEmitted` (captured PRE-lean/squash — folding lean in creates a double-lean
  feedback loop) persists across clip switches: that persistence IS the crossfade.
- Lean + landing squash became independently-eased scalars layered post-solve
  (`_leanEase`, `_squashEase`); squash keeps its pop-in softening without the global ease.
- `AngleCorrLimit` 0.6 → 3.2: Δθ now also bridges clip-switch gaps (> 1 rad); the box is
  a sanity backstop, the priors do the bounding. JointLimits remains the proper answer.
- Item 4 (release-rate doc) largely DISSOLVED: the release/crossfade rate is now
  explicitly `Stiffness` (1/s) rather than an emergent weight ratio.
- Tests: `SmoothingTests` (ease-rate parity vs `exp(−Stiffness·dt)` + clip-switch no-snap),
  rendered-reach gate added to `VaultGripSolverTests`; `Solver_AngleCorrections_
  StayNegligible` re-purposed to `StayBoundedAndDecay` (Δθ now legitimately bridges the
  per-stride Δφ hop — the pose no longer teleports there, a visual improvement);
  `FixedPointSolverTests`' arm bound raised to the honest cost of actually HOLDING the pin
  (the old number was flattered by the ease diluting the follow).

### Original spec (kept for the derivation)

### 1.1 The insight

`_pose.BlendToward(_target, _blend)` (the per-bone exponential ease, `Stiffness = 20/s`,
`UpperBodyStiffness = 90/s` under actions) is **the closed-form solution of a quadratic
smoothness objective**. For one bone with no geometric constraints,

```
minimize  λp·(θ − θ_target)²  +  λs·(θ − θ_prev)²
   ⇒      θ* = (λp·θ_target + λs·θ_prev) / (λp + λs)
```

— exactly an exponential blend with factor `b = λp/(λp+λs)`. So the ease is a special
case of the solver's temporal-smoothness prior, evaluated OUTSIDE the solve where the
geometric constraints can't see it. That placement is the root cause of the **pin-lag
defect**: the solve satisfies a pin exactly, then the ease dilutes the emitted pose by
~50%/frame (at 30fps), so the *rendered* hand systematically lags a moving-relative-to-
the-rig target (the vault corner) by ~one frame of body motion. `VaultGripSolverTests`
asserts reach on the solved pose and can't see this.

Moving smoothing INTO the objective makes constraints and smoothness trade off in one
minimization: hard rows (pins, contacts, no-pen) out-weigh the smoothness prior, so the
pinned tip reaches its target in the RENDERED frame, while unconstrained bones still
ease exactly as before. The pin lag disappears by construction — no per-bone stiffness
special cases needed.

### 1.2 Spec

**Variables/structure unchanged** (`x = [Δφ, δ, Δθ…]`). Three changes:

1. **Re-target `ThetaSmoothnessConstraint`.** Residual changes from
   `√λs·(Δθ_i − Δθ_prev,i)` to
   `√λs_i · wrapAngle(blendθ_i(x) + Δθ_i − θ_emitted,i,prev)` —
   i.e. smoothness of the FINAL local angle against the angle actually emitted last
   frame, not of the correction against the previous correction. `blendθ_i(x)` is the
   post-compose local angle already computed by `BuildSolvePose` (depends on Δφ through
   the base clip, so this row gains a `baseBlend_i·ω_i` entry in the Δφ column —
   Jacobian is otherwise the same constant diagonal). MUST use the shortest-path angle
   difference (same convention as `BoneTransform.Lerp`), or a clip switch that crosses
   ±π will unwind the long way.
2. **Emit the solved pose directly.** `Update` step 4 (`BlendToward`) is DELETED; the
   composed+corrected target becomes `_pose`. `_prevTheta` is replaced by
   `_thetaEmitted[i]` = last frame's final local rotation per bone, captured after
   lean/squash (see 1.4) so the smoothness target is exactly what was drawn.
3. **The solve runs every frame** (today it only runs on locomotion-with-contacts or
   the static pins/surfaces/aim path). Frames with NO geometric rows don't need LM:
   the objective is then diagonal per bone and the closed form above IS the answer —
   a `SmoothOnlyStep()` fast path (one sample + compose + per-bone blend, i.e. exactly
   today's cost). LM runs only when geometric rows exist, same as now.

### 1.3 Weight schedule (framerate independence)

The ease is currently framerate-independent via `1 − exp(−k·dt)`. Preserve that by
computing `λs` per frame from the SAME constants instead of hand-tuning a new one:

```
b_i(dt) = 1 − exp(−k_i·dt)          # k_i = Stiffness, or the ActionWeight-ramped
                                     # upper-body rate on _upperMask bones (unchanged)
λs_i    = λp_i · (1 − b_i) / b_i    # λp_i = the bone's Tikhonov prior (Core/LimbPosePrior)
```

At 30fps/k=20 this gives `λs ≈ 1.05·λp` — same order of magnitude as the existing
weights (per the "similar OOM" preference; the current `ThetaSmooth = 40` constant is
retired in favor of this derived value). Deriving `λs` from `λp` per-region keeps each
region's effective ease rate identical to today's on unconstrained bones — the visual
baseline should be indistinguishable, which is the A/B test.

### 1.4 Interactions to handle

- **Clip switches.** The discontinuity previously masked by the ease is now bridged by
  Δθ jumping to span the pose gap and decaying under `λp` vs `λs`. Idle↔Walk bone gaps
  can exceed the `AngleCorrLimit = 0.6 rad` box, which would clamp the bridge and pop.
  Options: (a) widen the box and let the priors do the work (preferred — the box was
  sized for IK trims, not crossfades); (b) keep the box for constrained frames and
  bypass it on the smooth-only path. Decide during implementation; test with an
  Idle→Walk→Run→Fall script asserting max per-frame emitted-angle delta stays under a
  bound.
- **Do NOT clear the smoothness target on clip switch.** Today `_prevTheta` is cleared
  when the clip changes (correct for a correction-space target). `_thetaEmitted` must
  PERSIST across the switch — it's what produces the crossfade.
- **Landing squash / lean.** Applied after the solve today and smoothed only by the
  global ease. Lean is a continuous function of speed — needs nothing. Squash JUMPS to
  1 on touchdown and was visually softened by the ease; give `LandSquash` its own
  explicit ramp-in (~2 frames, `1 − exp` style) instead, and keep both as post-solve
  additive deltas. They feed `_thetaEmitted` capture (rotation part) so smoothness sees
  the true drawn pose.
- **Overlay pops** are already handled by the slots' own weight ease
  (`ActionEaseIn/Out`) — unaffected.
- **`UpperBodyStiffness` snap** during attacks survives as the ActionWeight-ramped
  `k_i` in 1.3 — same mechanism, new home.
- **Golden path**: not made compatible — it is deleted first (item 2 below is a
  prerequisite, or at least land them together).

### 1.5 Tests

- Rendered-reach test: vault grip scenario asserting `‖tip − corner‖` on the EMITTED
  pose (< ~0.5px while the pin window is active, walking body) — the assertion the old
  suite couldn't express. Same for wall-slide no-pen (emitted limb outside the plane).
- Smooth-only parity: on an unconstrained walk→idle script, emitted pose per frame
  matches the old `BlendToward` output to float tolerance (the closed form is the same
  math — this is a regression harness for the refactor, deletable after).
- Clip-switch continuity: max per-frame emitted delta bounded across Idle/Walk/Fall
  transitions.
- FD-vs-analytic oracle extended to the re-targeted smoothness row (Δφ column now
  nonzero there).

---

## 2. Retire the golden-section path — DONE (2026-07-14), and it forced three real fixes

Deleted: `SolvePhaseStep`, `GoldenSection.cs` (+ its tests), the `useSolver` constructor
flag and both branches, and the `GameConfig.AnimSolver` gate — the LM solver is THE
animator now. Moving the golden-calibrated cadence tests onto the LM path exposed a real
pre-existing defect chain (live in-game already, since game_config.json ran the solver):

**The FOOT-SWAP STALL.** At low walk speed (~20–35 px/s) the phase parked at the
foot-crossover feather for ~30 frames (legs frozen, leg Δθ pegged at the box absorbing
body motion), then took a MaxPhaseStep catch-up lurch, and repeated. The shipped fix is
ONE bookkeeping change at the actual root cause:

**Time-continued contact release** (`RefreshContacts` + `ContactReleaseTime`, 0.1s).
The deadlock's core: escape needs the old contact's weight to fade, but the weight only
faded with phase, and phase couldn't advance against the held contact. Once a contact is
on the fading side of a crossover (dw/dφ < 0, from the new dweight channel in
`_weightBuf`), its weight also decays by time — min of the two. A stalled swap now
resolves in ~3 frames (reads as a weight shift); at healthy cadence the phase feather
completes faster and the time floor never engages.

**Design invariant confirmed in the process (see _scratch's declaration):** every
constraint evaluates the FINAL composed, Δθ-corrected pose in ONE solve per frame;
conflicts are resolved by weights, not structure. Three structural alternatives were
tried during diagnosis and REJECTED (kept here as they explain the invariant):
- *Live feathered weight inside the solve* (w(φ+Δφ), the §4.2 idea): the solver games a
  Δφ-dependent weight, advancing to wherever w≈0 to delete its own constraint — a run
  free-ran at constant Δφ with zero foot grip. Weights stay frozen per solve.
- *Contacts on a Δθ-free cadence pose*: kills the stall's Δθ-absorption channel, but the
  drawn foot then slips by exactly the Δθ contribution the constraint can't see, and a
  future contact-vs-no-pen conflict could no longer be weight-arbitrated on one pose.
- *Two-stage cadence/IK split* (externals excluded from the cadence solve): hides
  objective misspecification instead of surfacing it as a weight problem. The
  "pin drags cadence" evidence that motivated it turned out to be dominated by a
  test-oracle artifact (a phase-locked shadow animator diverging at a knife-edge hop
  and then feeding back through the pin target). With the time-fade release alone, the
  full suite — including that test — passes single-stage.

Also fixed: `ActionOverlayTests.LocoKf` authored ABSOLUTE leg rotations (missed in the
joint-chain migration; the file's other helpers are absolute on purpose) — legs
near-horizontal put the walk in the degenerate wrong-stance-foot geometry where a
forward-only cadence correctly freezes; golden's basin-jumping bug had masked it.

A residual (pre-existing) behavior remains: a once-per-stride Δφ hop (~0.15–0.2) at the
handoff, the same signature golden had (~0.18). Acceptable for now; smoothing it is
tuning work (FeatherWidth / ContactReleaseTime / PhaseStepPrior) once item 1 lands.

## 2b. d.x un-deferred — the horizontal root offset (added 2026-07-14)

User insight during review: locking d.x creates a near-singularity wherever a planted
foot's horizontal track turns around (∂slipX/∂Δφ = 0 there — cadence alone cannot track
the body, so slip accumulates and discharges as the per-stride hop), and slight fore-aft
body sway is physically real anyway. So `x = [Δφ, δ, d.x, Δθ…]` now (`IdxDx`;
`IdxTheta0` literals cleaned up in the same pass):

- d.x is RESIDUAL-SIDE like δ (`tip.X + d.x` in contacts H / pins X / no-pen; the aim row
  is invariant — d cancels in pR − pL). Constant Jacobian columns; FD oracle green.
- The §11.1 absorption trap ("free d.x soaks travel, cadence stalls") is guarded twice:
  the ABSOLUTE com row `√ComWeightX·d.x` (0.5, deliberately ≫ ComWeightY — pull toward 0
  charges sustained absorption quadratically, unlike a toward-prev smoothness) and the
  `HorizOffsetLimit` box (±4px) as the hard backstop.
- Host: `CharacterAnimator.HorizontalOffset` added beside `VerticalOffset` in
  `AttackGlowSystem.RigRoot` (com branch).
- Measured on the synthetic walk: gentle ±0.2px sway in stance, a 0.5–0.9px absorption
  spike at each swap, no cadence absorption (speed ratio stays distance-driven), and at
  vx=35 most handoff hops now smooth out entirely (some remain at vx=20 — tuning lever
  is ComWeightX, hot-reloadable).
- `FixedPointSolverTests` re-oracled in the same pass: the phase-locked shadow animator
  was replaced by a self-contained BODY-RELATIVE pin at the arm swing's midpoint — the
  shadow desyncs a frame at a knife-edge foot-swap hop and then its derived target
  fights the main walk's swing in a feedback loop, manufacturing the arm spike the test
  meant to bound.

---

## 3. Dimensionless residuals + fix §11.4 doc drift — DONE (2026-07-14)

Every pixel residual (contacts, pins, no-penetration, both com ties) is divided by the
rig's REACH — the longest root→tip chain × scale, computed once per animator
(`_invCharLen`; ≈ 21.6px for the biped at 0.6). Angle rows were already radians. Through
the lever arms 1 rad ≈ 1 reach of tip error, so config numbers now compare honestly
across row kinds. Px tiers carry the exact ×reach² (≈467) rescale, lightly rounded
(< 1%): TierHard/TierNoPen 10 → 4700, TierContact 1 → 470, ComWeightY 0.05 → 23,
ComWeightX 0.5 → 230; angle tiers unchanged. Behavior identical — full suite green
untouched.

**Finding:** normalization VINDICATED the original §11.4 tier intent (`HARD ~1e3 ≫
CONTACT ≫ priors`) — the tuned weights had implemented that spread all along; the old
config numerals only looked inverted (TierHard 10 < CorePosePrior 60) because px and
radian rows were in different units. The "similar OOM" appearance was a units illusion,
so that preference is moot: the honest numbers show the true ~1000× spread. §11.4 now
carries the live tier table; root `anim_solver_config.json` regenerated.

---

## 4. Document the Δθ release rate (no behavior change)

When a pin/surface disengages, Δθ decays at `λs/(λs+λp)` per frame (≈0.91 today →
~0.3s release at 30fps). Nobody chose that number; retuning smoothness for jitter
silently changes release feel. Add a comment at the smoothness constraint + a line in
`AnimSolverConfig` docs stating the ratio and the resulting time constant. (After item
1, the same emergent ratio ALSO sets clip-switch crossfade length — say so.) Only make
it an explicit tunable if tuning ever actually fights it.

---

## 5. Quality-of-life batch

Small, independent; do opportunistically.

1. **Alloc-free sample surfaces.** `CharacterAnimSample.From` allocates `new[]` for the
   wall-slide surface (and callers may for pins) every frame. Cache a static/host-owned
   scratch array (render-only, single-threaded) or add a small fixed inline buffer.
2. **`AnimationTag` virtual replaces substring matching.** `SelectClip` /
   `ResolveMovementOverlays` / `ResolveMovementPins` / `From` match on
   `MovementState.Contains("Parkour"|"WallSlid"|"Crouch"|"LedgePull"|"LedgeGrab")` —
   fragile to renames and future states (`ParkourRoll` would match "Parkour"). Follow
   the established pattern (`AnimationProgress`, `TryAnimationGrip`): a
   `virtual AnimTag MovementState.AnimationTag` enum (None, Parkour, WallSlide, Crouch,
   LedgeGrab, LedgePull, …) carried on the sample; keep the string only for debugging.
3. **`CadenceCostAt` stackalloc guard.** `stackalloc float[80]` has a comment-only size
   contract; add `Debug.Assert(n <= 80)` (or size from the rig).
4. **`IdxTheta0` literals.** Replace the literal `2` at `AngleCorrection`, the Δθ apply
   loop in `Update`, `SolvePhaseStepLm`'s `_prevTheta` copy, and `BuildSolvePose`.
5. **Diagnostics partial.** Move `MaxJacobianError`, `SolveScaleReport`,
   `SolvedBoneTipWorld`, `AimAngleError`, `IntervalAt`, the `Dbg*` fields (~250 lines)
   to `CharacterAnimator.Diagnostics.cs`.
6. **Editor C1 parity (WYSIWYG).** The runtime samples Catmull-Rom
   (`SampleSmooth`); the editor still samples linearly (`SampleNormalized`) — so
   authored in-betweens don't match what ships, right before a clip-authoring push
   (`ANIMATION_CLIP_GAPS.md`). Switch the editor's playback to `SampleSmooth`
   (scrub/keyframe editing unaffected — keyframes are exact on the spline).

---

## Suggested order

2 (flip default, soak) → 3 (units, small + independent) → 1 (the real change, now on a
single path with legible weights) → 4 (document the ratio item 1 made load-bearing) →
5 throughout. Item 1 should land with the rendered-reach test from 1.5 as its
acceptance gate.
