# Timing stage — build plan (workplan chunk 5; runtime plan §4, §5)

Status: **in progress on branch `anim-chunk5-timing`, 2026-09-10.** No A/B switch: the
branch is the alternative; the golden-trace test and `MTile.Bench --anim-baseline
--compare` are the comparison. Ownership rules come from
[ANIMATION_OWNERSHIP_CONTRACT.md](ANIMATION_OWNERSHIP_CONTRACT.md) (Rule A: the timing
stage owns the phase; Rule B: it never becomes a foot's support owner).

## What changes, in one paragraph

Today `SolvePhaseStepLm` seeds Δφ from eleven residual evaluations and then solves it
jointly with the root offset and every joint angle, against contacts captured at the
*entry* phase. After this chunk, one module (`Animation/GaitTiming.cs`) produces the phase
advance first — from actual body travel and the clip's authored stride, refined (optionally)
by a bounded one-dimensional match of the supporting foot, with a stateful stopping
policy — then contacts and planner state are updated at the *resulting* phase, and the
joint solve fits the root offset and limbs with Δφ locked. The rate floor, acceleration
prior, seed search and flight coast are retired once the timing stage owns what each did.

## Module: `GaitTiming`

Pure per-frame function plus one small state struct, no reference to `CharacterAnimator`
(same discipline as `StepPlanner`).

Inputs (`TimingInputs`): clip, its **gait track**, entry phase φ, previous rate (cycles/s),
dt, body position now and last frame, velocity, facing, grounded, whether any planted
contact exists, and (for refinement) the frozen contacts + a forward-pass delegate.

Output (`TimingResult`): Δφ (unwrapped, ≥ 0 for forward playback), rate (cycles/s), the
timing state, and diagnostics (travel estimate, refinement delta, cycle distance source).

### T1 — travel-based timing

`Δφ_travel = travel / cycleDistance`, with

- `travel` = this frame's body displacement along facing, `dir · (pos.X − prevPos.X)`
  (actual travel, not velocity·dt — the sim already moved the body).
- `cycleDistance` (signed, world px per cycle), first available of:
  1. `BodyPath.TryCycleDisplacement` (an authored scene path; today 1 locomotion cycle,
     the rabbit crouchwalk) → `D.X · scale`.
  2. The **gait track**: `ClipStrideTrack` compiled over *all* contact sources (a new
     `anySource` compile — `PlannedSupport` and `SelfPlant` alike; ownership is
     untouched because only the timing stage reads this track). During a stance the foot's
     body-relative offset runs `TdOffset → LoOffset`, so the body travels
     `(TdOffset.X − LoOffset.X) · scale` over `Liftoff − Touchdown` cycles. Cycle distance =
     `Σ travel_i / Σ span_i` over every stance of every foot (a mean rate per cycle, which
     is robust to double support and flight — a single foot's stances cover only its
     stance fraction). Sign follows the authored data, so WalkBack (feet moving forward
     under the body) gets a negative distance and advances under backward travel.
  3. Legacy nominal: `1 / PhasePerPixel` = 100 px per cycle, per clip (the current global
     constant becomes the per-clip fallback the runtime plan requires; no clip authors a
     nominal yet).
- Near-zero authored displacement (|cycleDistance| < a few px): the clip does not travel;
  advance at the clip's authored rate `1 / Duration` cycles/s (time-driven, like IdleBob).
- Backward travel against the authored direction gives Δφ_travel < 0: clamp at 0 (the
  cycle never plays backward) and let the stopping policy take over.
- Vertical/lateral maneuvers: *chunk 7 (2026-09-11)* — the cycle displacement is a vector.
  A scene path (`body_path`, the only source that carries a rise) gives D = p(1) − p(0);
  the gait-track fallback gives the run only (a flat cycle's stance Y sweep is bob, not a
  direction). The phase advances by the body's frame motion projected onto D̂ (facing
  applied to x only): a climb counts, motion across the direction does not, motion against
  it clamps at 0. `TimingResult.CycleDistance` is |D| and `Direction` the unit vector. On
  the bench's 45° stairs the sim hops each riser, so the projected rate saws 4.5 → 7.9
  cycles/s per step where the run-only rate was a flat 5.6 (`rate_jump_max` 92 → 213 on
  stairs run, 221 → 133 on stairs slow); the cadence now follows the real climb. Whether
  to low-pass that is an open tuning question, not a structural one.

Rate continuity, in cycles/s² and dt-aware (§4): the rate may change by at most
`MaxRateAccel · dt` per frame from the previous rate, symmetric; this is the
acceleration box moved out of the solve, expressed in the right units. The nominal
floor: rate ≥ `0.5 · speed / cycleDistance` (the current floor, re-derived) only while
traveling with contacts — it exists to stop a weak-contact frame collapsing the rate;
under travel-based timing that failure mode disappears, so the floor is retired unless
T2's refinement reintroduces it (it cannot: the refinement is bracketed around
Δφ_travel).

**Found while building T1 (2026-09-10):** advancing the phase before the solve moved the
Δθ-smoothness target's reference base to the advanced phase, which silently charged clip
playback in the smoothing rows (max |Δθ| 0.13 → 0.75 rad on a steady run — the legs
lagged the clip by the ease). The target must be measured against the base pose at the
ENTRY phase (`FreezeProblem`'s `phiEntry`), exactly as the constraint's comment demands;
with that, corrections are back at 0.02–0.11 rad. Any later change to when the phase
advances must keep that reference.

**T1 variants measured (2026-09-10):** (a) one constant rate per cycle (mean stance rate)
and (b) the per-segment travel curve inverted each frame. On the shipped clips (b)'s
within-cycle variation comes from the C1 spline's easing between keys, not from authored
intent: it makes the phase rate swing ~4× inside one cycle (rate_jump_max 0.3 → 34 on a
steady run) with no gain in slip, target error or Δθ. Shipped: (a), one constant rate per
cycle (`ClipStrideTrack.CycleTravel`). T1 landed 2026-09-10 with Δφ locked in the joint
solve, the seed search deleted, and the floor/continuity rows made inert (T5 removes them).
Against the chunk-2 state (game config): solve iterations −20–35%, `rate_jump_max` on
steady scenarios 90–870 → 0.3–90, stairs `tgt_err_max` 9.1 → 2.0 px (rabbit stairs
`foot_acc_p99` 16.5 → 7.3). Regressions carried into T3: planted-foot `slip_max` on the
biped steady run 1.7 → 2.6 px/frame, and every abrupt stop now shows the same instantaneous
rate drop (the stopping policy's job). `PhaseAccelTests`' soft-prior and hard-box cases
were retired with the mechanisms they tested.

### T2 — bounded 1-D refinement (the experiment)

Minimize over Δφ in `[Δφ_travel − w, Δφ_travel + w]`, `w = max(0.5·Δφ_travel, 0.01)`, capped
by `MaxPhaseStep`:

```
J(Δφ) = Σ_planted  w_c · (tipX(φ+Δφ, body_now) − targetX)² · invCharLen²
      + λ_rate · ((Δφ/dt − rate_prev) / RateRef)²
      + λ_nom  · ((Δφ − Δφ_travel) / Δφ_travel)²
```

The first term is the planted contacts' H rows evaluated at d = Δθ = 0 — the same forward
pass (`SolveForward.Run`) on a reduced `SolveProblem` whose block list is
`{PlantedContacts (H only)}`; root and joint corrections stay out (§4). Golden-section on
the bracket, ~8 evaluations, replacing 11 seed evaluations + the LM's Δφ column. The
bracket is what guarantees "cannot jump to another gait solution" and "not a turning-point
derivative alone". Ship T1 first, measure, then T2 on the same scenarios; keep the one
that wins on `rate_jump_max` / `slip_max` / `tgt_err_max` without an `iters` regression.

### T3 — stopping policy (§5)

```
Traveling ──(speed < SettleSpeed && decelerating, hysteresis)──▶ Settling
Settling  ──(active swing finished: all tracked feet in stance, or no swing)──▶ SupportedIdle
Settling  ──(speed > SettleSpeed + hysteresis)──▶ Traveling      (restart: phase continues)
SupportedIdle ──(speed rises)──▶ Traveling                       (phase resumes from the hold)
any ──(no contacts, airborne)──▶ Traveling with the rate coasting (flight)
```

- Settling advances phase on a **time schedule**: `Δφ = max(Δφ_travel, Δφ_finish)` where
  `Δφ_finish` moves the phase to the next touchdown event (from the gait track) over
  `SettleTime` seconds, so residual travel is still honored and a foot never freezes
  mid-swing. The landing itself belongs to the foot's owner (Rule B): the planner shortens
  the swing toward a reachable tread because its phase input says so; a self-plant foot
  captures at the settled phase.
- SupportedIdle holds the phase (`Hold` semantics) at the double-support phase it reached;
  the driver's Idle switch then transfers the stance contacts (T4) and the idle pose blends
  in through the smoothness prior. The simplest pilot per §5: finish the current step, blend
  to idle; dedicated stopping clips only if this fails the quality target.
- A flight-phase stop keeps coasting until support is acquired, then settles.
- Knobs (AnimSolverConfig): `SettleSpeed`, `SettleTime`, `MaxRateAccel`. The retired
  `MaxPhaseAccel`, `PhaseFloorPrior`, `PhaseFloorMode`, `PhaseAccelPrior` go with their
  rows (T5).

### T4 — contact transfer on clip switch (contract §4, planner P4)

Replace the unconditional `_contacts.Clear(); Planner.Reset();` on a locomotion →
locomotion switch with the by-foot-identity transfer rule: keep a stance contact iff the
incoming clip labels the same bone, the entry phase falls inside that bone's stance in the
incoming clip, and the target is still valid support within reach. Everything else
releases. Needed for run → walk → idle settling and for stairs (run ↔ step-up).

**T3 + T4 landed (2026-09-10).** T4: `CharacterAnimator.TransferContacts` +
`StepPlanner.Rebind` (adoption of a transferred support point); `AnimContactTransferTests`
covers run → walk across the self-plant → planner ownership boundary and the release into
Idle. T3: `GaitTiming`'s three-state policy with `SettleSpeed` 20 / `SettleExitSpeed` 30 /
`SettleTime` 0.15 s (AnimSolverConfig), and the core-side hold of the cadence clip through
the driver's Idle choice while settling — the entry test runs on the switch frame's own
speed (`GaitTiming.WantsSettle`), because an abrupt stop drops below the idle band in the
frame Idle is first chosen. Measured (bench notes `timing=` and `idle_switch_swing_u=`):
the stops settle in ~7% of the window and hold; the trailing foot's swing progress at the
Idle switch fell from 0.47–0.86 to ~0.31 — a run clip has no double-support phase, so one
finished step always leaves the other foot mid-swing for the idle blend to bring down (the
plan's "simplest pilot"; a stopping clip is the next step if that reads badly). Costs: the
finish schedule re-accelerates the phase after a dead stop (`rate_jump_max` on the stop rows
70 → 130–195 cycles/s²; rate continuity would cap it), and the settle frames trade some
slip/penetration for the finished step (rabbit abrupt stop +14: `slip_max` 1.2 → 3.0 px,
`pen_max` 0.1 → 2.4 px). The pulse-width walk scenario now settles on 40% of its frames
because its speed chatters through `SettleSpeed` — a tuning question (raise the hysteresis
or lower `SettleSpeed` toward the idle band), left to the owner.

### T5 — retire what the timing stage now owns

| Mechanism | Owner after this chunk | Action |
|---|---|---|
| Δφ seed search (11 evals) | T1 travel estimate (+ T2 bracket) | delete |
| Δφ as a solve variable | timing stage | box `[0,0]` now; the column is removed in chunk 8's diet |
| `PhaseRateFloorConstraint` + `PhaseFloorMode` box | T1 (no collapse mode) | delete row + knobs |
| `PlaybackContinuityConstraint` (acceleration prior) | T1 rate continuity (cycles/s², dt-aware) | delete row + `PhaseAccelPrior` |
| `MaxPhaseAccel` box | `MaxRateAccel` | delete |
| Flight coast in step 2 | T3 (Traveling with no contacts coasts the rate) | move |
| Momentum seed on clip switch (`speed·dt·PhasePerPixel`) | T1 (rate carries across the switch; T4 keeps support) | delete |
| `SwingTargetConstraint` body offset | unchanged (owner's call, contract Rule B) | measure |

`MTile.Bench` tools that read the retired knobs (`ColumnDiag` PhaseFloorMode sweep,
`FtolStudy`) are updated or dropped with them.

**T5 landed (2026-09-11):** `PlaybackContinuityConstraint`, `PhaseRateFloorConstraint`, the
knobs `PhaseAccelPrior` / `PhaseFloorPrior` / `PhaseFloorMode` / `MaxPhaseAccel` (and their
json keys), the clip-switch rate seed, and the bench sweeps over `PhaseFloorMode` are gone.
Δφ's column and box remain (chunk 8). The golden traces were re-recorded for the two-row
layout change; the bench numbers are unchanged (the rows were identically zero).

### T6 — foot-synchronized servo (2026-09-21)

T1 integrates travel and drifts against what the feet actually do: on stairs the sim climbs
in two-tread hops while the clip's com path is a smooth diagonal, so the projected rate
surges and lags inside every hop and the planner is asked for treads the legs cannot reach
(the stairs trace: Unreachable/SwingBlocked rejects, stance targets sliding with the body).

The fix closes the loop on the planted feet. A stance is a fixed world point, so the body's
offset from it reads the phase directly: `GaitTiming.Observe` projects the observed offset
`(S − body)` onto the stance's authored sweep `TdOffset → LoOffset` and reads
`Touchdown + u·(Liftoff − Touchdown)`; every planner-held stance votes, weighted by its
engage/release ramp, as a wrapped difference from the current phase. The stance a foot is
read against is the one nearest the current phase (a foot may plant twice per cycle).

`Advance` then servos the RATE, never the phase: `rate = feedforward + clamp(gain·err,
±maxRate·max(feedforward, 1/Duration))`, followed by a slew limit on the whole rate's change
per frame (feedforward included — the anti-jerk term). Past `PhaseReentryError` the clip and
the feet disagree outright and the phase re-enters AT the observation (signed jump); the
smoothness prior bridges the pose as it does on a clip switch. Traveling only — the stopping
policy owns the phase while settling. Knobs: `PhaseServo*` / `PhaseRateSlew` in
`AnimSolverConfig` (hot-reloaded). Diagnostics: `TimingResult.PhaseError/ServoRate/Reentered`,
`CharacterAnimator.LastObservedResidual`. Tests: `AnimPhaseServoTests`.

Rule A (ANIMATION_OWNERSHIP_CONTRACT.md) holds: the timing stage is still the phase's only
writer; the planner's plans are an INPUT to it (last frame's stances against this frame's
body), and the planner still owns where feet land.

Not yet: rescaling a stance's sweep to the ACTUAL step (the planned next landing) so a
short or long tread reads correctly, and scheduling swing/flight toward the next planned
touchdown with a gate there. Self-plant clips supply no independent observation (their
capture is phase-driven) and stay on T1.

## Order of work and acceptance

1. T1 + Δφ locked + T4 (the transfer is needed before settling can be judged). Golden
   traces re-recorded (intended change, documented in the test header). Bench compare vs.
   the chunk-2 measurement (`582cbce`) and the HEAD baseline.
2. T3. New scenarios already exist in the bench: gradual decel, the three abrupt stops,
   restart while settling, run-walk-run. Acceptance per the runtime plan: deceleration
   settles into idle without a suspended foot (no foot tip left mid-swing at the Idle
   switch — measure: swing-progress at the switch), fewer velocity jumps
   (`foot_acc_p99`, `rate_jump_max`), no new penetration/slip/target regressions.
3. T2 as the measured experiment; keep or drop.
4. T5 deletions, one commit, after the bench confirms nothing regressed with the rows
   inert.

Tests: `AnimGaitTimingTests` (headless: cycle distance from a synthetic gait track,
travel → Δφ, rate box, backward travel, zero-displacement clip, settle state machine on a
scripted speed profile, contact transfer rule), plus the existing `AnimSolverTests`,
`AnimPlannedGaitTests`, `StairAnimationTests` and the golden traces.
