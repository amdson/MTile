# Animation batch retro — the agent-authored clip pipeline

Written 2026-07-19, after the first full cycle of the approach: 29 clips authored,
re-authored, or validated in one day (4 calibration + 22 batch + 3 wrap-up), by
worktree-isolated agent workers driving `MTile.Probe` against the digest gate.
This doc is the honest assessment: what the approach proved, where it is weak,
and where to invest. Pair with `Plans/ANIMATION_BATCH.md` (process mechanics)
and `.claude/skills/anim-probe/SKILL.md` (craft knowledge).

## What the approach proved

- **Measurement beats intuition on this rig, and agents can author through it.**
  The probe/digest loop converts animation into numbers, and the gate (recurvatum,
  STEEP, diff-vs-reference, loop seams) catches real geometric errors. 29 clips
  shipped gate-green; the full test suite stayed green throughout.
- **The docs compound.** Iteration counts fell as the skill absorbed each round's
  lessons: ground slashes cost 9 cycles early; `pulse` took 2 and `land` took 3 at
  the end. Three feedback rounds (calibration → batch → wrap-up) each made the next
  round measurably cheaper.
- **Clips are now cheap, regenerable artifacts** — compiled from intent prose (the
  batch doc rows) + craft rules (the skill) by interchangeable workers. Nothing is
  precious. This inverts animation maintenance: **when a clip is wrong, fix the
  intent text and re-run a worker; do not hand-massage JSON.** The batch doc and
  skill are the real assets — treat them as source code.
- **Model economics** (calibrated, n small): Sonnet handles spec'd UpperBody
  overlays at ~60% of Opus cost; Opus earns its price on FullBody, wiring, and
  unspecced/judgment tasks. Whole-day cost: ~3.3M subagent tokens for 29 clips.

## The honest gaps

1. **Nothing has eyes.** The gate proves geometric validity, not appeal. It cannot
   see timing feel, arc quality, easing character, or personality. Every new clip
   is "correct and plausibly choreographed"; almost none have been *seen moving*.
   The standing review rule (ANIMATION_BATCH.md Group A note) cuts both ways:
   clean digest ≠ good, FLAGS ≠ bad. Human verdicts are the one signal the
   pipeline cannot generate.
2. **Style coherence is unverified.** ~25 clips were authored by ~25 independent
   workers against prose intents. Each is internally sensible; whether they read
   as ONE character (consistent lean magnitudes, arm personality, weight) has not
   been checked. Exemplar-anchoring (`run`, `wallslide`) constrained only a few.
3. **The ceiling is the rig, not the process.** Recurring worker collisions with
   the same walls: no over-shoulder cock-back (STEEP), swing-foot graze (no heel),
   no expressible idle bob (rotation-only bones), vertical body motion only via
   the per-key com channel. Long-term polish pressure points at rig upgrades
   (heel bone, translation channels, angle wrap). The approach's payoff: a rig
   migration that would be catastrophic for hand-authored assets is survivable
   when regeneration costs an afternoon.
4. **A sim/render boundary leak.** Actions with no `OverlayDuration` override
   (e.g. `BlockEruptionAction`) fall back to the clip's `Duration` — so an
   animation edit can change real gameplay pacing. Worth closing (give every
   action an explicit duration) before it surprises someone.

## Where to invest (in order)

1. **A render-to-image probe.** The single highest-leverage gap. If the demo
   viewer (or a headless variant) could dump a pose filmstrip to PNG, workers
   could LOOK at their clips instead of inferring silhouettes from coordinates —
   closing gap #1 without a human in the loop.
   - Token cost is modest: image tokens ≈ (w×h)/750, so an 800×200 8-pose
     filmstrip ≈ ~250 tokens and even a large 1568×400 strip ≈ ~850 — the same
     order as one digest read. A worker averaging ~100k tokens/clip that checks a
     filmstrip at every cycle pays roughly 10–30% more (images persist in context
     across turns; prompt caching blunts most of the resend cost); checking only
     at the gate costs ~5%. If vision saves even one iteration per clip it breaks
     even; if it prevents one post-review re-author round it is strongly net
     positive. Recommendation: one contact-sheet image per verify step, not
     per-frame dumps.
2. **Record human verdicts per clip.** After each in-game/demo review pass,
   extend the exemplar/approved list in ANIMATION_BATCH.md (good / needs-work +
   one line of taste feedback). This is the layer the gate can't provide; making
   it durable turns your taste into batch-worker input.
3. **A style audit.** One agent reading all 42 digests side-by-side for outliers
   in reach magnitudes, chest-lean ranges, and stance conventions — cheap, and
   directly addresses gap #2.
4. **Close the Duration/gameplay leak** (gap #4).

## Standing process rules (learned the expensive way)

- Pre-wire all enums/`SelectClip` branches in ONE commit before fan-out; workers
  author clip JSON only. Placeholder clips must exist before wiring merges
  (selecting an unauthored clip throws at runtime).
- Worker worktrees fork from a STALE HEAD. Expect add/add conflicts on
  placeholders at merge, re-verify clip `Type` fields at merge, and have workers
  confirm handoff-reference clips against the merge target.
- Build the probe inside the worktree; it reads/writes its own DLL's checkout.
- Adding keyframes cannot fix STEEP. Retime, shrink the swing, or unwrap.
