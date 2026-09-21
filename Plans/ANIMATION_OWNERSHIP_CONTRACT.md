# Animation ownership and lifecycle contract (workplan chunk 6, design)

Status: **design draft, 2026-09-10** — written before chunk 5 (timing) and chunk 7
(step-up) are implemented, as the runtime plan's rollout note requires
([ANIMATION_RUNTIME_SIMPLIFICATION_PLAN.md](ANIMATION_RUNTIME_SIMPLIFICATION_PLAN.md) §10,
"Rollout"). It answers two questions those chunks would otherwise each answer
differently: **who owns the phase** and **who owns a foot**. The persistent-contact /
continuous-replanning *implementation* (runtime §6) is the later half of chunk 6 and
waits on chunk 4's endpoint resolver; this doc only fixes the rules it will follow.

The contract is deliberately small and maps onto seams that already exist. Where it
proposes a decision the owner has not made, the item is marked **[owner's call]**.

## 1. What exists today, in the contract's vocabulary

The runtime plan asks for six declarations per clip/layer. Four of them already exist in
code, scattered; the contract names them and stops them drifting.

| Declaration (§10) | Where it lives today | Notes |
|---|---|---|
| Descriptive tags | `AnimTag` (movement) / action class name (action) | Search labels only; acquire nothing. |
| Exclusive slot | `OverlayStack` slot 0 (action) vs. the base clip chosen by the active `IMoveDriver` (locomotion); slots 1–2 for driver overlays | The base clip IS the locomotion slot's owner. |
| Resource claims | Hands: a driver's `FrameInputs.Pins` (the vault grip); feet: `ContactSource.PlannedSupport` labels (planner-owned) vs. `SelfPlant` (animator-owned) | Implicit today — nothing reserves; see §3. |
| Pose contribution | `AnimationDocument.Region` + `OffRegionWeight` (regional override with an off-mask weight); the base clip is full-body | Additive layers do not exist yet (recoil would be the first). |
| Timing source | `ClipTimeMode` — `CadencePhase`, `IdleBob`, `Hold` (locomotion phase), `Progress` (maneuver progress), `Clock` (local clock); the action overlay's τ is `ActionProgress` (action progress) | Exactly the plan's four sources plus Hold. |
| Transition policy | The driver registry ORDER (first `Matches` wins), `ClipChoice.StartT` / `MatchPose` (entry), the overlay ease-in/out rates, the settle tail | No commitment window or interruption reasons yet. |

Lifecycle, mapped the same way:

| Stage (§10) | Today |
|---|---|
| Preconditions | `IMoveDriver.Matches` (gameplay intent via `AnimTag`) + "a clip of that type is bound" (`_clips`); no support/reach/resource check |
| Enter | The `clip != _state.Clip` block in `CharacterAnimator.Update`: `ClipTime = 0`, `_contacts.Clear()`, `Planner.Reset()`, momentum seed, `StartT` / `MatchPose` |
| Conditions | `Select` re-runs every frame (Walk↔Run, Hold↔CadencePhase are per-frame reactive) |
| Exit / handoff | Implicit in the next Enter; overlays fade out on their slot; the settle tail is the one explicit exit behavior |

## 2. The two rules chunks 5 and 7 need

### Rule A — phase has one owner: the timing stage

`CharacterAnimState.Phase` is advanced by exactly one component per frame, chosen by the
active clip's timing source:

| Timing source | Phase owner |
|---|---|
| `CadencePhase` | **The timing stage** (chunk 5, `Animation/GaitTiming.cs`): travel-based advance, optional 1-D refinement, the stopping policy. The pose solve no longer solves Δφ. |
| `IdleBob` | The fixed idle rate (unchanged). |
| `Hold` | Nobody — frozen (unchanged). |
| `Progress` | The sim's `MovementProgress` (unchanged); chunk 7 gives step-up a maneuver-progress source that plugs into the timing stage as a second *source*, not a second architecture. |
| `Clock` | `ClipTime` (unchanged). |

Consequences: the phase-rate floor row, the acceleration prior, the Δφ seed search and the
flight coast are timing-stage responsibilities (rate continuity, nominal rate, flight
coasting) and are retired from the joint solve once the timing stage owns them — see the
disposition list in [ANIMATION_TIMING_STAGE.md](ANIMATION_TIMING_STAGE.md). Nothing else
may write `Phase` mid-frame. The step planner receives the timing stage's rate as
`NominalRate` (today it receives last frame's jointly solved rate).

**Foot feedback (T6, 2026-09-21).** The timing stage now also takes the planner's held
stances as an input and servos the phase rate toward what they imply
(`GaitTiming.Observe` / `Advance`, Plans/ANIMATION_TIMING_STAGE.md T6). Rule A is unchanged:
one writer of the phase, the timing stage; the planner reports, it never sets.

### Rule B — each foot has one support owner per frame

A foot's contact target comes from exactly one of:

| Owner | Declared by | Lifecycle |
|---|---|---|
| Step planner | `PlannedSupport` labels on the clip (the clip *claims* `foot_support_plan` for that node) | `StepPlanner` — touchdown/liftoff from the stride track, targets from terrain treads |
| Animator (legacy) | `SelfPlant` labels | `RefreshContacts` — feathered capture/release at the entry phase |
| A driver | `FrameInputs.Pins` (`External`) | Frozen per frame by the driver |
| Nobody | No label / `Unplanned` | The foot follows the clip; no-pen only |

Mixed sources on one node are already refused at compile (`ClipStrideTrack.TryCompile`).
The rule adds: **the stopping policy does not become a fifth owner.** Settling (runtime §5)
expresses "finish or shorten the active swing toward a reachable landing" through the
existing owner — a planner-owned foot gets a shortened swing schedule from the planner
(driven by the timing stage's phase), a self-plant foot gets it through the ordinary
feathered capture at the settled phase. Both react to the *phase* the timing stage
produces; neither owner is bypassed by the settle.

**[owner's call]** The swing-target body offset (runtime §3, deferred from chunk 2): under
Rule A the joint solve still owns d = (d.x, δ). The plan's "determine the body offset, then
fit swing limbs" is a stage split the one-solve invariant rejects. The timing stage does
not resolve this; the options remain (a) last frame's emitted d as a constant in the swing
rows, (b) d columns in the swing rows, (c) accept the current omission until chunk 8's
reduced solve revisits variable ownership. The build plan assumes (c) and measures.

## 3. Claims: reserve, don't just check

Today a hand is "claimed" by whichever driver pins it and a foot by whichever label
source the clip uses; nothing prevents two layers issuing targets for the same endpoint.
The contract (the plan's §10 table) for the *first* implementation slice:

- A **claim set** per candidate layer: `{LeftHand, RightHand, FootSupportPlan}` plus, per
  planned foot, the foot's identity. Locomotion base clips claim `FootSupportPlan` iff they
  carry `PlannedSupport` labels. The vault's grip pin claims `LeftHand`. Action overlays
  claim the hand(s) their clip drives — **[owner's call]** whether a one-handed slash claims
  `RightHand` only (so run + slash composes, ledge-pull + slash conflicts on the support
  hand) or both hands; the plan's expected combinations imply per-hand.
- **Resolve all claims together, then mutate.** Admission of a frame's layer set is one
  step: base clip, driver overlays/pins, action overlay; a candidate whose claim conflicts
  with a higher-priority admitted layer is rejected whole (no partial acquisition). Priority
  order for the initial set is explicit and stable: driver base clip > driver pins/overlays
  > action overlay, matching today's paint order.
- **Rejection never changes gameplay.** A rejected action overlay reports the missing
  visual (diagnostic) and the base pose plays; the action itself is untouched
  (`ActionState` remains authoritative).

Identity: until chunk 4's endpoint resolver lands, a foot or hand identity is the **bone
index** (`ContactLabel.Node` → `Skeleton.IndexOf`); the resolver replaces it with the
endpoint ID + role, and the adapter keeps `Node` resolving to the same bone.

## 4. Handoff on clip change

Today Enter clears every contact and resets the planner. The contract (planner P4's
by-foot-identity rule, generalized):

1. **Transfer** a stance contact to the incoming clip iff the same foot identity is a
   support owner in the incoming clip (label present, any source), the contact's target is
   still valid support (terrain revalidation for planned feet; nothing for self-plant), and
   it is within reach of the body at the entry phase. Never transfer into an incoming
   *swing* just because the name matches — check the incoming clip's stance interval at the
   entry phase.
2. **Release** everything else promptly (the documented toe-off rule); the emitted-offset
   ease and the Δθ smoothness prior carry the visual continuity, as they do today.
3. The **entry phase** for a cycle clip is chosen for support compatibility first
   (a transferred stance foot must be in stance in the incoming clip at that phase), pose
   continuity second (`MatchPose`), default third (the persisting phase). This is the one
   place the transition policy needs new code; `MatchPose` already provides the scan.
4. Overlays keep today's behavior: an outgoing overlay fades on its slot and issues no
   targets while fading (its pins are gone the frame its driver stops contributing).

Chunk 5's settling needs step 1 for run → walk → idle (retain the settled support points
through the clip switch); chunk 7 needs step 3 (stairs: run ↔ step-up at a compatible
stance). Both are implemented in chunk 5 (build plan T4), the rest of §6 (continuous
replanning, early landing commitment, trajectory reconstruction on replan) stays in
chunk 6's implementation half.

## 5. Selection and persistence — not built yet

The plan's candidate-gathering / ranking / commitment scheme (§10 "Selection and
persistence") replaces the driver registry's first-match order. Not needed for chunks 5
and 7: the registry order plus Rule A/B is sufficient while the candidate set is one base
clip + one action overlay + one driver overlay. Build it when a second competing base
candidate appears (stand vs. step-up beside a step — chunk 7 may be that trigger).

## 6. Diagnostics to add with the first implementation

Per frame, in `AnimFrameDebug`: the phase owner (timing state + rate), each foot's support
owner and reason (`StepReject` already), active claims, and a pending handoff (transferred
/ released contacts at a clip switch). The bench's `--anim-baseline` notes column gains
the timing state mix (traveling/settling/idle %) — that is how "deceleration settles into
idle without a suspended foot" gets measured rather than eyeballed.

## 7. Chunk 6 implementation (2026-09-11) — continuous replanning

What landed in `Animation/StepPlanner.cs` (runtime plan §6's second half; the handoff rules
of §4 were chunk 5's):

- **Commitment.** A swing commits its landing on first selection (`FootState.CommitWish`
  remembers the wish it committed at). Before the late lock it is reconsidered only when the
  predicted wish has moved more than `PlannerReplanDistance` (4 px, the fourth planner knob),
  when the committed path from the foot's current position is obstructed, or when the
  support died / fell out of reach. A reselect that returns the same point is a quiet
  re-commit. Reasons are reported per frame as `FootPlan.Replan`
  (`Prediction | Obstruction | InvalidSupport | Fallback | Reacquired`) with a per-swing
  count.
- **Continuity.** The emitted target is `nominal(u) + offset · (1 − u)/(1 − u0)`, where the
  offset is captured at every source change as the gap between the *carried* position (last
  target plus its last per-frame step) and the new nominal path. Rebasing the chord and this
  decaying offset are the same formula, so one mechanism covers replan, fallback to the
  clip's motion and re-acquire from it, and the offset composes across successive changes.
  Only rotations of the target path change; touchdown still lands exactly on the committed
  point.
- **Clearance of the replacement.** `SwingBlocked` samples the remaining path from the
  carried position (same sample count, samples at or before u0 skipped) — for a fresh swing
  that is the old takeoff→landing check. A first cut that let this permissive check pick any
  clear tread committed three-steps-up landings on the 45° bench stairs (tgt_err_max 7.6 →
  22 px); the fix is `MaxLandingMiss` = 1.5 tiles: a tread farther than that from the
  authored wish is not a plan, for the touchdown selection too (the "do not pin a distant
  point" intent, now enforced).
- **Identity.** Feet are still keyed by bone; on one rig a support point resolves to one
  bone, so `TransferContacts`' bone match is the endpoint identity of §3 (the point id is
  the track's `Node`). A cross-rig transfer would key by point id — not needed yet.

Numbers (`MTile.Bench/baselines/anim_chunk6.txt` vs `anim_chunk4.txt`, game config): flat
scenarios are bit-identical (Run carries no planned support) or within noise; walk-speed and
run-walk-run (planner-owned Walk) keep tgt_err/slip and show `swing_acc_max` 3.9 / 3.5
px/frame² with 76 / 25 replans in 300 / 250 frames — the PWM input makes the velocity-based
touchdown prediction swing ±10 px, each swing a blended replan. Stairs (StepUp) are mixed:
stairs slow tgt_err_max 8.7 → 4.3, stairs run slip_max 3.6 → 4.6 and pen_max 7.3 → 10.6.
The per-frame trace shows both effects are dominated by pre-existing step-up behaviour on
these 45° stairs, not by the replanning: the toe-path clearance check rejects nearly every
swing (`SwingBlocked` on most frames, so most stances come from the touchdown selection),
and both feet get planned onto the same tread point. Both belong to chunk 7's step-up
policy; the planner mechanics here are the substrate it will drive.

Diagnostics landed (the §6 list): `AnimFrameDebug.Feet` (owner state, reject, replan, target,
landing per planner foot); the bench's `swing_acc_max` column (second difference of the
in-flight target) and `replans=` note; `MTILE_ANIM_TRACE=<scenario substring>` prints a
per-frame planner/contact/penetration trace of the first rep. Not landed: active claims and
the pending-handoff record (no claims implementation exists to report yet).
