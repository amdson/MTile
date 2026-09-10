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
