# Corrector Consolidation Plan

> **STATUS (2026-07-28): Phases 1–5 SHIPPED; Phase 6 knobs+profiling done;**
> **suite 419/0.** Commits: Phase 1 triage (850424f — hexagon promoted,
> redirect disc restored, anti-pop killed), Phase 2 invariants (6e55adb —
> fold Δ anchors snapshotted, rollback determinism back), Phase 3 FSM
> reintegration (3de2b1c), Phases 4+5 elective refusal + scenario suite
> (210ca23), Phase 6–7 tuning surface + docs (this commit).
>
> Notable deviations / discoveries beyond the plan text:
> - **The solver needed per-variable preconditioning** (diagonal steps with
>   Gershgorin row-sum bounds). A global step size starved force channels
>   (dt² levers) against velocity channels (dt levers) ~10³× and soft rows
>   against hard rows another 50× — the fold's mixed solves were numerically
>   silent in exactly the mixed cases the design depends on. Side effect: the
>   natural first step is the uniform profile on the constraint surface,
>   which removed bang-bang structurally (oracles re-pinned).
> - **Support semantics needed three mirrored gates** (live + coast share
>   constants): the gravity hold fades across [HoldFullDist, SupportReach]
>   and gates on rise speed; the envelope refuses to bind plunging ticks
>   (descent > MaxGroundEngageVnRel) so plunge landings hit RAW — this alone
>   restored the whole impact-materials spec on unchanged tuning; station-
>   keeping is baseline FRICTION (feedforward), not solver rows.
> - **Anti-autopilot is structural**: Drive unilateral along intent, no x
>   channel at station, CornerAssist lift-only, Redirect/CornerAssist skip
>   soft horizontal rows (SkipSoftHorizontal), leaky Δ anchors (0.7) so the
>   smoothness chain can't act as thrust memory.
> - **The walk-speed equilibrium quirk is retired** — the fold walks at the
>   configured MaxWalkSpeed (pinned by test).
> - Elective refusal shipped per ELECTIVE_REFUSAL_NOTE: R1/R0 references,
>   deliverability on the true corrected rollout, ±8-frame hysteresis latch
>   in MovementVars.AmbientElectiveLatch. The half-scramble is dead
>   (FoldScenarioTests.OneHighLedgeUnderCeiling: stalled tail flat at hover).
> - Phase 3.3 maneuver migration taken as the CHANNEL-TABLE pilot: the climb
>   family builds its redirect-only set through CorrectorChannels; migrating
>   maneuvers onto the full stack remains the long-term bet.
> - Cost: ~457 µs/tick on the vault-heavy course (Debug build, includes
>   maneuver + fold solves + elective rollouts) — inside the test gate;
>   rollback re-sim multiplies this, so Release profiling stays on the list.
>
> **Maneuver migration (§3.3) SHIPPED** in a follow-up commit: the climb
> family solves on the full maneuver stack via CorrectorChannels.BuildManeuver
> (per-channel Δ anchors in MovementVars.ManeuverChannelPrev; CorrectorPrevDv
> retired). With it came a REVISED PHYSICAL CHANNEL SEMANTICS, uniform across
> fold and maneuvers: actuation depends on what the body can push against —
> near the ground (floor within LegReach): legs (LegServo/Tuck), Drive, and
> the redirect disc gated to DYNAMIC ticks (near && !supported — a
> plant-and-deflect needs ground under it but must not eat a supported walk's
> speed); in flight: air control only (AirLateral along intent + a
> deliberately tiny two-sided AirVertical; NO redirect — momentum cannot be
> deflected against nothing). MaxChannels 8. PredictGuided now reports FloorY
> whenever the probe sees a floor (informational, for masks) while Grounded
> alone drives dynamics — lip crossings read as near-ground. Net effect:
> bumpy-corridor traversal improved to full completion (~76 px/s avg) and the
> air-graze preserves full entry speed (150/150).
>
> Remaining (deliberately): lever-normalized hinge weighting (§6 open
> problem), Release-build profiling against the multiplayer rollback budget
> (Debug cost after the migration: ~667 µs/tick on the vault-heavy course,
> inside the test gate).

Turning the `corrector_testing` experiments ("the solver IS the locomotion
controller") into the game's real movement system. The experiments proved the
architecture on a fall+stand-only player in the gym/corridor stages; this plan
is the path from there to a shippable, full-FSM, determinism-honest system.

Provenance: everything TEMP-marked landed in commit `6946098`. Grep
`TEMP EXPERIMENT` for the exact sites.

## What the experiments established (keep as design commitments)

- **Gravity-hold baseline.** Sustained support is feedforward (coast and live
  state both hold against gravity when grounded); the solver handles only
  transients. The solver's cost structure (reg→0, Δ-smoothness) is built for
  zero-mean corrections — DC demands belong in the baseline, never in soft rows.
- **Envelope reference with per-direction actuator bands.** One construction
  (C-space floor envelope − hover, tracked by two-sided soft rows) covers
  hover, walk, climb, overshoot damping, and landing catch. Bands encode
  honesty: `ClimbReachUp` = what legs can deliver (1-high yes, 2-high no),
  `TuckReachDown` = what tuck can deliver (tiny adjustments; lips unbind and
  fall ballistically). Support anchor from the *current* pose kills ratcheting.
- **C-space obstacle template** (`CObstacle.cs`): exact per-tile Minkowski
  geometry with exposure masks and corner bevels; the surface the planner sees
  is the surface collision enforces.
- **Emission filter as veto, never preference.** Rows come from the true
  nearest exit or not at all — filters decide actionability, not direction.
  (This is what fixed sideways wall hits reading as climb commands.)
- **Restricted channel stack solved jointly.** Actuators are channels with
  per-tick masks and caps (LegServo/Traction/CornerAssist/Redirect/Tuck);
  capability is expressed by restricting channels, not by casework.
- **Soft vs hard rows via per-row HingeScale**; refusal residual counts hard
  rows only.

## Phase 1 — Triage the TEMP set (decisions, mostly the user's)

Promote, revert, or redo each experiment deliberately:

| Experiment | Recommendation |
|---|---|
| Stand fold (spring/FSD removed) | **Keep** — it's the point. |
| Gravity-hold baseline | **Keep.** |
| Envelope reference + bands + anchor | **Keep.** |
| C-space template + veto row builder | **Keep.** |
| Channel stack + per-tick masks/caps | **Keep** the mechanism; retune membership per state (Phase 3). |
| Half-width collision hexagon | **Decide.** Core gameplay attribute — needs an explicit yes to survive. |
| Redirect disc → free force channel (non-fold states + maneuvers) | **Decide.** The disc was the energy-honesty story; the free channel was a debugging crutch. Likely restore the disc outside the fold. |
| Anti-pop clamp removal | Likely **keep removed** — two-sided rows superseded it — but confirm no residual pop cases. |
| Refusal suspension (always-apply in fold) | **Redo properly** in Phase 4; don't ship "never gives up". |
| Scenario harness (`CorrectorScenario`) | **Keep** as a dev/test tool; not a shipping config. |
| Fall+stand-only player, gym/corridor stages | **Keep** as test stages; restore the full FSM (Phase 3). |
| `ChannelPrev` not snapshotted | **Fix** in Phase 2 — non-negotiable for rollback. |
| Diagnostic tests (bumpy tunnel, hard landing, rest equilibrium) | Convert to asserting scenario tests (Phase 5) or delete. |
| `Character/corrector_pseudocode_reference.txt` | Fold into this plan / BALLISTIC_CORRECTOR_PLAN and delete from `Character/`. |

## Phase 2 — Restore invariants

- **Rollback determinism:** snapshot `ChannelPrev` (and audit all
  `CorrectorScratch` state that outlives a frame). Audit
  `CObstacleTemplate.For`'s static single-slot memo against the "no
  sim-affecting static mutable state" rule (pure function of the polygon, so
  cache is fine — but verify restore-order independence). Extend
  `SnapshotRoundTripTests` to cover corrector state.
- **Web/KNI parity:** the same sources compile under KNI — build `MTile.Web`
  once per phase; `stackalloc`/`Span` use is fine but verify.
- Fix the CA2014 stackalloc-in-loop warning in `CorrectionSolver.cs`.
- Re-enable hot-reload safety: solver constants moving to config (Phase 6)
  must not tear mid-step.

## Phase 3 — Reintegrate the full state machine

The experiments amputated everything but Standing/Falling. Bring states back
one at a time, deciding for each: **fold** (state becomes reference-shaping +
channel membership) or **own** (state servos directly, publishes
`AmbientPolicy.Off`).

1. **Crouch** — same fold as stand: lower hover target in the envelope
   (reference shaping, not a new mechanism); still has the old spring today.
2. **Jump / launches** — impulse + air phase; defines how the envelope hands
   off (anchor unbinds on launch, rebinds through the landing-catch path that
   already works).
3. **Maneuvers (vault/mantle/arc family)** — still on `PredictGuided` + the
   old single-channel solve. Either migrate them to the channel stack with
   maneuver-specific masks/caps, or keep them owned and define the
   ambient↔maneuver handoff (policy already exists). Migrating is the
   long-term bet; do one (vault) as the pilot.
4. **Wall family** (slide/hang) — owned states; verify they publish `Off` and
   the fold never fights them.
5. Per-state channel membership table lives in one place (successor to
   `BuildStandChannels`), driven by state + coast, never by action state
   (preserve the movement/action firewall).

## Phase 4 — Failure semantics (refusal, done right)

Currently the fold always applies because support is load-bearing. Split the
plan's roles so refusal can return:

- **Support tier** (hover/catch soft rows + LegServo): always applies —
  refusing support drops the body.
- **Elective tier** (progress, climbs, corner assists): all-or-nothing per
  `Plans/ELECTIVE_REFUSAL_NOTE.md` — R1 (elective) vs R0 (support-only)
  references, rollout-based deliverability, hysteresis latching so refusal
  doesn't flicker.
- 2-high bonk is currently *emergent* (band + veto). Keep the emergent path as
  the primary mechanism; refusal is the backstop for cases geometry doesn't
  catch.

## Phase 5 — Test suite

- Promote the gym scenarios into asserting tests on `SimRunner`: 1-high climb
  succeeds, 2-high bonks (no net height gain), lip step-off is ballistic
  (no down-force during the fall), hard landing recovers to hover, corridor
  and bumpy-tunnel traversal ≥ speed threshold, rest is motionless.
- Re-examine the Skip-attributed `HoldRight_CourseCorridor` (vault exit-carry
  regression) once maneuvers migrate — it may just start passing.
- Keep one golden-trace determinism test: same inputs → bit-identical
  positions across snapshot/restore.

## Phase 6 — Tuning surface & performance

- Move the constants now hardcoded in `AmbientCorrector`/`BuildStandChannels`
  (hover offset, bands, channel weights/caps/reach, `FoldIterations`) into
  `MovementConfig` for hot-reload tuning. Keep solver-structural constants
  (HingeWeight) fixed.
- Profile the per-frame cost: solve (iterations × channels × horizon) +
  `FloorEnvelope` tile scans per tick + row build. Budget it against the
  60 fps step with headroom for 2+ players (rollback re-simulation multiplies
  everything by the rollback window).
- Known open problem, deferred deliberately: lever-normalized hinge
  weighting (soft-row pressure is dimensionally weak at long horizons; the
  gravity-hold sidestepped the one universal case, other sustained demands
  will hit it again — e.g. long ramp tracking).

## Phase 7 — Docs & cleanup

- Strip `TEMP EXPERIMENT` markers as each item is promoted or reverted.
- Update `BALLISTIC_CORRECTOR_PLAN.md` (this architecture supersedes several
  of its steps) and `CODEBASE_OVERVIEW.md`'s movement section.
- Delete the scratch pseudocode file, fold its content here.

## Suggested order

Phase 1 decisions first (cheap, unblocks everything), then 2 (invariants keep
the branch honest while iterating), then 3 state-by-state with Phase 5 tests
written per state as it lands, then 4, then 6–7. Phases 3.3 (maneuver
migration) and 4 (refusal) are the two big design efforts; everything else is
consolidation labor.
