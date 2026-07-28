# Pin: graceful failure = mode selection (elective reference refusal)

> **IMPLEMENTED (2026-07-28)** in `AmbientCorrector` (R1/R0 via EmitEnvelope's
> climb band, deliverability on the true corrected rollout, hysteresis latch in
> `MovementVars.AmbientElectiveLatch`); pinned by
> `FoldScenarioTests.OneHighLedgeUnderCeiling_HoldRight_HonestBonk_NoHalfScramble`.

Context (2026-07-27, stand-fold experiments on `corrector`): with the envelope
reference + anchored climb band, a blocked climb produces a "half-scramble" —
the soft tracking optimizer's best compromise is a physically meaningless hover
against the wall. Worst case for predictability.

## The idea

Give-up is not error handling — it is **mode selection**, and it should reuse
the maneuver system's trigger semantics (feasibility-as-trigger, refusal
measured on the TRUE corrected rollout, never the surrogate residual).

- **Elective requests are all-or-nothing.** Split requests into load-bearing
  (support at the current level — always applies) and elective (anything that
  moves the body elsewhere: the climb binding, aggressive progress). An elective
  set either passes its deliverability check or is dropped AS A SET. Bimodal
  outcomes — clean climb or plain full-speed bonk — are what "predictable
  dynamics" means. Soft costs degrade gracefully in L2, which is exactly
  ungraceful in game feel.
- **Fallback = the next reference down, not a behavior.** R1 = envelope with
  climb band; R0 = envelope restricted to the anchor's own surface (progress
  continues straight into the wall; physics delivers the stop). One rule —
  solve R1, and if the true corrected rollout tracks it worse than threshold,
  use R0's solution — covers ledges, ducks, gaps, everything, because all
  references are instances of one construction.
- **Check deliverability on the rollout, not the residual** (the residual is
  the frozen linear model's opinion; the half-scramble is precisely where model
  and reality diverge).
- **Latch the decision with hysteresis.** Per-frame re-deciding chatters between
  climb and bonk at the margin; an accepted R1 persists for a commitment window
  unless deeply violated — the maneuver Enter/exit semantics reappearing.
- **The accept/reject bit IS the classification signal** — "climbing" vs
  "running into a wall" for the animator and future state machinery falls out of
  the same decision (the plan's classifier inversion).

Cost: at most two solves per frame (R1, then R0 on rejection), deterministic.

Related: Plans/BALLISTIC_CORRECTOR_PLAN.md (feasibility-as-trigger, refusal
semantics, classifier inversion).
