# Bot behaviour architecture

Status: **approved for implementation** (2026-11-04). Supersedes the 2026-11-04 draft after
review. Nothing here is implemented yet; the migration plan at the end is the work order.

## Why

Today a bot's movement depends on which maneuver is running. `BulldogBot.Poll` picks one
maneuver at a time, and that maneuver either lets the default `Locomote` run alongside it or
shuts it off entirely (`OwnsLocomotion`). As a result:

- Movement isn't one system. Walking in, retreating in lag, stuck hops and air steering live
  in `Locomote`, and every `OwnsLocomotion` maneuver re-implements whatever subset it needs.
  44 maneuvers set `OwnsLocomotion`; most of them only want the body to **hold still** (guard,
  pulse, block throw, crystal wave, anti-air stab), not to drive movement.
- Behaviours can't coexist. A slash and a reposition can't both be "on"; whichever maneuver
  was picked owns the whole bot until it ends.
- Learning (`ManeuverWeights`) weights whole maneuvers in made-up score units, with credit
  going to whichever one started last. Nothing on the movement side is learnable at all.

The goal is one uniform format every bot behaviour plugs into, with movement and actions
arbitrated by explicit rules, and a learning rule that is robust to arbitrarily badly
calibrated behaviours.

## Prior art this follows

- **Layered and reactive control** (subsumption, 3T): a slow layer sets the current goal, a
  fast layer briefly overrides it, and preemption plus resumption replaces any scheduling.
- **Shipped game AI** (Halo, F.E.A.R.): exactly one movement goal at a time. Compromises
  between goals are made inside the behaviour that picks the destination, not by blending.
- **Options** (Sutton, Precup & Singh 1999): temporally extended actions with their own
  ending conditions. Committed moves and relative-position targets are both options, so the
  horizon comes from what is chosen, not from a planner.
- **Resource-claim systems** (Unreal Gameplay Tasks, ros_control): a task acquires named
  resources, and conflicts are resolved per resource.
- **Option-value learning with hand-written priors** (classifier systems, the Hayek machine):
  each behaviour *bets* on the outcome of following it, in the one currency the game scores
  (HP), and the learner corrects the bet from realised outcomes. The hand-written utility is
  the prior, not the truth.

Rejected: real-time scheduling of requests with deadlines, lookahead or planning, and
credit *division* schemes (claims, Shapley-style shares). Division needs every behaviour to
know how much of a hit was its own; bets need no such thing, because every holder is judged
against its own prediction of the whole outcome.

## Roles

| Role | Does | Doesn't |
|---|---|---|
| **Controller** | Collects bets, grants resources, executes positioning targets, runs the bet ledger, masks with constraints, writes the final `PlayerInput`. The only writer of input. | Contain tactics. |
| **Behaviour** | Reads the game state, bets on the outcome of running, and when it holds a resource drives it. | Look at other behaviours. Anything shared goes through the controller. |

A **committed** behaviour is a behaviour that currently holds a resource, not a separate kind
of object.

## Resources and claims

| Resource | Inputs |
|---|---|
| **Move** | Left, Right, Space, Down |
| **Act** | LMB, RMB, Shift, cursor, number keys |

A behaviour declares what it needs when it runs:

- **Act**: Actions hold Act exclusively. Nothing else writes Act inputs while one is committed.
- **Move**, one of:
  - `None`: don't touch Move. Positioning keeps driving.
  - `Exclusive`: I drive Move. Positioning is suspended.
  - `Forbid(keys)`: I don't drive Move, but these keys must not be pressed. `Forbid(all)` is
    **hold still**: positioning is suspended and Move is left empty. This is what guard
    (Shift plus a direction decodes as something else), pulse, the block throw and the crystal
    wave actually want, and it is why value-level claims are *not* an open question any more.

A behaviour that claims several resources gets **all of them or none** (atomic acquisition).

## Categories

Categories are defined by **which resource a behaviour holds and for how long**.

| Category | Holds | At once | Examples |
|---|---|---|---|
| **Positioning** (stances) | Move, softly, long horizon; never commits | one | chase, hold the reach edge, step out in lag, flank, retreat |
| **Reflex** | Move, exclusively, short; preempts positioning *and* committed actions | one | step out from under, corner-escape dash, back off from a seen swing |
| **Action** | Act. Plus a Move claim: `None` for a plain slash, `Forbid(all)` for aimers and guards, `Exclusive` for the few whose input *is* movement (jump slash, dash-in punish, climbs and digs) | one | slashes, stab, guard, block throw, crystal wave, whiff punish, wall climb |
| **Constraint** | nothing; masks only | all | spike guard, don't walk off a ledge |

**Positioning is a set of stances, not one behaviour.** "Stay close but don't get cornered" is
still decided inside one stance's target choice, but *which* stance (press, hold range, step
out, flank) is a selection the controller makes and the learner can correct. With a single
positioning behaviour there is nothing for the learner to do on Move, which is the gap the
first draft had.

Reflex versus Move-holding action (the first draft's open question 3): a **reflex** is
triggered by the opponent's state and exists to not get hit (anti-air sidestep, corner escape).
An **action** is triggered by "I can hit them" (whiff punish, jump slash). Reflexes outrank
committed actions because abort-on-damage already breaks commitment today, and a reflex that
cannot interrupt a committed jump slash means eating the punish.

This makes the bot **two parallel selectors** with a filter over both:

- **Move selector:** reflex, else a committed action's Move claim, else the current stance.
- **Act selector:** which action, if any. The **Idle** arm is a real arm with a bet, so a
  negative-value action never fires just because it was the best of the actions.
- **Constraints** mask the final input of both.

A plain slash and the current stance run at the same time: the stance walks into range, and
the slash fires when in range. Neither knows about the other.

### Order of precedence

1. Constraints mask everything.
2. On Move: reflex, then a committed action holding Move (`Exclusive` or `Forbid`), then the
   stance.
3. Within a category: selection by corrected bet (below). A holder keeps its resource unless a
   candidate beats it by the **preemption gap** (Passive vs Active, as in
   `MovementPriorities`), and the current stance gets a **staying bonus**. No minimum-hold
   timers.

## Behaviour states

| State | Meaning |
|---|---|
| **Dormant** | Not relevant; bets nothing (horizon 0). Costs nothing. |
| **Proposing** | Bets each frame; can lose with no consequence. |
| **Committed** | Holds its claims; loses them only to higher precedence or when its own `Step` ends. |
| **Suspended** | Stance only: a reflex or Move-holding action took Move. Resumes when it is released. |

Lifecycle: `Bet` (every frame while not committed), `Begin`, `Step` (committed frames),
`Aborted` (preempted or hit). Stances have no `Step`; they return a target (below). Suspend /
resume is trivial for stances because a target is recomputed every frame; it is `Aborted` that
matters, for multi-phase actions that leave sticky state (today's `Abort`: re-select the block
picker).

## Outputs

- **Stances** output a **target position relative to the opponent** ("just outside their
  reach", "under them", "behind them", "away"). The controller's **Move executor** turns the
  target into Move inputs: straight-line walk with the stuck-hop and air-steer heuristics from
  today's `Locomote`, and the **route follower** (`RouteFollower`, `NavGrid`) when the straight
  line needs a climb, a build or a detour. The router is execution, not a stance. Ranger's
  `RouteManeuver` becomes the executor's planner.
- **Reflexes and actions** output input sequences for their committed window (today's
  maneuver `Step`).
- **Bets** are the selection signal (next section). The first draft's "what does a score
  mean" question is closed: a score *is* a bet, in HP.

## Learning: bets

The only reward is HP, read from the HUD (own and opponent, the same `v.MyHealth` /
`v.TargetHealth` the brains already use). Behaviours never invent reward and never divide it.
Instead, every behaviour **bets** and is judged against its own bet.

**Bet.** `Bet(view)` returns `(dealt, taken, horizon)`: the HP the behaviour expects to deal
and to take over the next `horizon` frames if it runs now. `horizon ≤ 0` means not applicable
(today's `Score ≤ 0`). Bets are in HP, so a stock in-range slash bets about `(0.5, 0.1, 30)`;
a stance bets something small over a long horizon; a reflex bets `(0, −avoided, 20)` as a
*reduction* in taken relative to standing there, which it expresses as a low `taken`.

**Correction.** The controller keeps an additive offset per `(behaviour, situation bucket,
channel)`, start 0. The **corrected bet** is `bet + offset`. Whatever the author wrote, the
corrected bet converges to the empirical mean outcome in that bucket: an overconfident arm
demotes itself, a modest one promotes itself. This is the robustness property the design is
for, and it is what makes the scheme safe for arbitrary behaviours.

**Bet conventions (settled in M4).** Every bet `Pass`es when its maneuver's `Score` gate
fails, so a gated-off behaviour is never applicable. Defensive moves bet the HP they avoid as a
NEGATIVE `taken`, because the Idle arm bets `(0, 0, 30)` and an action must beat it to fire.
Where today's `Score` ladder has ties, the group is stepped by 0.01 in list order so argmax
reproduces the old brain. The ledger converges every arm, Idle included, toward realised
*absolute* HP, so the authored priors (relative avoided damage) and the learned values differ in
meaning; this is accepted.

**Value and selection.** `value = dealt_c − λ·taken_c` with `λ` a selection-time knob (not
learned). Within a category, pick by softmax over values with a sharpness, as today's
`Pick` does; the holder's value gets the preemption gap and the stance's its staying bonus.
Two channels are learned separately precisely so that how much avoided damage is worth
against dealt damage stays a knob.

**Ledger.** When a behaviour **actually acquires** a resource (not when the learner picked it
and hysteresis vetoed it), the controller opens a record: name, bucket, bet, `settleFrame =
now + horizon`, HP of both sides. Each frame every open record accumulates HP drops on both
sides (a rise, from respawn, counts 0). At `settleFrame` the record settles:
`offset[channel] += η · (realised − (bet + offset))`, clamped. A preempted or finished
behaviour is still scored on what happened through its horizon, which is the option's actual
return including its tail (end lag, a thrown clod in flight). Only executed arms are scored;
there is no counterfactual for the arms that did not run.

**Simultaneous holders.** A stance and an action open records at the same time and both see
the same outcome. Each therefore learns the expected outcome *marginalised over what the other
selector does*, which is exactly the quantity its own selector needs. The known limit is the
independent-learners one: the pair can settle somewhere jointly mediocre. Situation buckets
absorb most of the cross-talk; accept the rest.

**Buckets.** `Situation.Bucket(view)`: horizontal gap band × perceived target state
(attacking / recovering / idle) × grounded. Shared by all behaviours; a behaviour may not
define its own bucket (that is a knob the learner cannot see through).

**Forgetting.** Offsets decay toward 0 by a small fixed share per settle, so an opponent who
adapts (a human) is tracked rather than fitted once.

**Fairness.** The ledger reads only HUD HP and the controller's own ledger entries. A
behaviour's `Bet` gets the same `BotView` as today; it must not read the opponent's live
fields (league rule, unchanged).

**Removed.** `ManeuverWeights` (score × weight, last-started credit) and `TacticBandit`
(per-decision arms with their own windows). The Ranger A/B numbers in `RangerBot.cs` were one
learner shape against five fixed bots; they are not design constraints and are not carried
forward. If their lessons are real the ledger will relearn them.

## Stability

- No minimum-hold timers. Hysteresis is the preemption gap and the staying bonus.
- Oscillation is tracked: the controller counts holder changes per resource per second and
  the trace shows A→B→A flips, so a pair of behaviours fighting is visible in the league
  printout.

## Trace and evaluation

"Good balance of behaviours" is measurable only with occupancy. The controller records, per
frame, the Move holder, the Act holder, and which constraints masked. `BotDuel` and the
league harness (`ZzzDuelistLeague`, `ZzzRangerEval`) print, per bot:

- time share per behaviour on each resource;
- acquisitions, preemptions suffered, and oscillation count;
- the learned offsets per behaviour and bucket at the end of the bout.

Today's `Starts` dictionary is replaced by this.

## Mapping from the current code

| Today | Becomes |
|---|---|
| `BulldogBot.Locomote` walk-in | `ChaseStance` (positioning), target = inside `EngageRange` |
| `Locomote` retreat in lag | `StepOutStance` (positioning), bets high when in recovery and in reach |
| `Locomote` stuck hop, air steer | the Move executor |
| `RouteManeuver` | the Move executor's planner, used when the straight line fails |
| maneuvers without `OwnsLocomotion` (slashes, stab, strings, low poke) | actions, Move `None` |
| hold-still `OwnsLocomotion` maneuvers (guards, pulse, block throws, crystal waves, anti-air stab, escape grab) | actions, Move `Forbid(all)` |
| `OwnsLocomotion` maneuvers whose input is movement (jump slash, whiff punish, Bulldog punish/cross-up, air stabs, down-air, climbs, breach, build-climb, wall-up, enclose) | actions, Move `Exclusive` |
| `OwnsLocomotion` maneuvers that only move (dash, corner escape, anti-air sidestep, backoff) | reflexes |
| `SpikeGuard.Apply` | a constraint |
| `ManeuverWeights`, `TacticBandit`, `RangerBot.Tactics` | the bet ledger; tactic arms that were worth keeping become stances or reflexes |
| abort-on-damage in `Poll` | the controller's `Aborted` on every holder |
| `Score` | `Bet`; the adapter below maps old scores to bets during migration |

**Adapter.** `BotManeuver` stays as a type. A `ManeuverBehaviour` adapter wraps one: category
from a registry table, Move claim `Exclusive` if `OwnsLocomotion` else `None`, bet
`(0.5·Score, 0, 90)`. This lets the controller land first and the 44 maneuvers migrate to
native claims one file at a time, while the other brains keep compiling.

## Scope

**Phase 1 (this plan):** the controller, the ledger, the trace, and the Bulldog family
(`BulldogBot`, with `HarrierBot` and `RangerBot` built on it). Bulldog is the brain with
learning and the largest maneuver set; the Bulldog-vs-Brawler record (23-0-1) and the Ranger
field eval are the regression checks.

**Phase 2 (later):** `BrawlerBotInputSource`, `CitadelBot`, `RiposteBot`, `RampartBot`,
`CamperBot` each have their own copy of the `Poll` / `Locomote` loop. They stay as they are in
phase 1 so the league opponents are stable while the controller is validated, then move onto
the controller one at a time.

## Not in scope

- Lookahead, planning or deadline scheduling of requests.
- Changes to the game's own action or movement code: this is entirely on the bot side.
- Tuning against scripted opponents. Parity with today's records is the bar, not a new high.
- Phase 2 brains.

## Implementation plan

All new code under `Net/Bot/Control/`, `namespace MTile.Net`. Bot code is outside the sim:
no snapshotting concerns, randomness only through `v.Rng`. Tests go in `MTile.Tests/Sim/`
with class names starting `BotManeuver…` so `scripts/test-group.py bot` picks them up.
Commit per milestone; commit only files this work touches (the working tree has unrelated
uncommitted edits in `Simulation.cs`, `configs/game_config.json`, `todo.txt` and
`MTile.Tests/Sim/CoopModeTests.cs` that must stay out of these commits).

[x] **M1. Controller core, no brain changes.**
`BotBehaviour` (Name, Category, MoveClaim, Cooldown, `Bet`, `Begin`, `Step`, `Aborted`),
`Stance : BotBehaviour` (`Target(view) → Vector2`, no Step), `Constraint : BotBehaviour`
(`Mask(view, ref input)`), `MoveClaim` (None / Exclusive / Forbid(keys)), `Bet`,
`Situation.Bucket`, `BetLedger` (open / tick / settle / offsets / forgetting),
`MoveExecutor` (walk + stuck hop + air steer lifted from `BulldogBot.Locomote`; route
follower hook), `BotController` (two selectors, precedence, hysteresis, constraint pass,
trace, oscillation counter), `ManeuverBehaviour` adapter with a category registry.
Tests: ledger settles and corrects an overconfident and an underconfident bet toward the
realised mean; Forbid(all) suspends the stance and leaves Move empty; reflex preempts a
committed Exclusive action and the action gets `Aborted`; a Move-None action and a stance
hold at the same time; idle arm wins over a negative-value action; only executed arms open
records.

[x] **M2. Bulldog on the controller through the adapter.**
`BulldogBot.Poll` becomes perception + `controller.Step(v)`. `Locomote` is split into
`ChaseStance`, `StepOutStance` and the executor. `SpikeGuard` wrapped as a constraint.
`Harrier`/`Ranger` compile unchanged except `RouteManeuver` moves behind the executor.
Checks: `scripts/test-group.py bot` green against the BACKLOG §5 baseline; Bulldog vs
Brawler over the README format (3 arenas × seeds 1–4 × both sides) scores ≥ 20 of 24
points; Ranger vs Brawler and vs legacy Bulldog over seeds 1–4 within 2 points of the
pre-change brain run in the same harness.

[x] **M3. Native claims for the Bulldog family.**
Convert the maneuvers in the mapping table to `BotBehaviour` subclasses with native Move
claims (`Forbid(all)` for the hold-still set, `Exclusive` for movement-input actions,
reflexes for the movers). Delete `OwnsLocomotion` overrides from converted files; leave the
flag on `BotManeuver` for phase-2 brains. Verify in a scenario test that a slash fires while
`ChaseStance` is still walking in, and that guard leaves Move empty.

[x] **M4. Bets and the ledger live.**
Replace adapter bets with hand-authored HP bets on every converted behaviour. Wire the
ledger into Bulldog's controller, `RangerBot.Create` uses it by default; delete
`ManeuverWeights`, `TacticBandit`, `RangerTactics.cs`, `RangerBot.Tactics` and their tests
(fold any tactic arm worth keeping into a stance or reflex first). A light A/B in a `Zzz`
eval: learner on vs off for Ranger over seeds 1–6 vs the field, reported, not gated.

[x] **M5. Trace, league printout, docs.**
Occupancy / preemption / oscillation / offsets in `ZzzDuelistLeague` and `ZzzRangerEval`
output. Update `Net/Bot/Duelists/Bulldog/README.md`, add the controller to
`CODEBASE_OVERVIEW.md`'s bot section if one exists, add a BACKLOG item for phase 2, and
record the regression numbers here under a "Results" heading.

## Campaign log

- 2026-11-04 branch `bot-controller` created from main @1cca026; plan committed @ee57530.
- Baseline before any change: `scripts/test-group.py bot` = 162 pass / 6 fail / 168, 6 m 47 s. The 6 reds are pre-existing and not touched by this work: BotManeuverMovementTests.Dash_ClosesGap_AndDashSlashes, BotManeuverBurstTests.Burst_CloseInBand_ShovesAndChipsP1, BotManeuverSpacingTests.BotManeuverBackoff_StillHits_ButOverlapsLess, BotManeuverStabTests.AirWhipKick_CrossUp_KicksBackward, BotManeuverAvalancheCrushTests.WallPocket_CrushesP1(3,11,6), BotManeuverAvalancheCrushTests.FreeRoof_CrushesP1(2,29).
- M1 shared interface committed @eba4362 (Net/Bot/Control/BotBehaviour.cs). Ledger and controller parcels dispatched to two Opus agents in worktrees.
- M1 done: BetLedger (b362f68), BotController + MoveExecutor + ControllerTrace + ManeuverBehaviour/ManeuverRegistry (5672007), merged; registry trimmed to today's locomotion owners so the adapter is behaviour-neutral (Dash, DownAir, Burst, Brawler BlockThrow, BulldogPunish stay Move=None until M3). 17 new tests green. Controller owns v.FramesSinceManeuver; Idle arm opens ledger records once per horizon when it wins a contest.

- M2 done (6e6674a, merged): Bulldog/Harrier/Ranger run on BotController; Locomote → ChaseStance + StepOutStance + MoveExecutor; SpikeGuardConstraint; RoutePlanner behind the executor. Parity (3 arenas × seeds 1–4 × both sides): Bulldog vs Brawler 24→22 of 24; Ranger vs Brawler 24→24; Ranger vs Riposte 9→11; Ranger NoLearn vs Brawler 24→24. Bot group 180/6/186 with only the 6 known reds. One regression found and fixed: Breach's stuck counter fired mid-route, so Move-claiming actions don't bet while the route planner drives. Supervisor change: Ranger's Lambda left at the controller default (1) instead of the old dealt-only setting.
- M3 groundwork: `BotManeuver` gained native `Category`/`Move`/`Bet` virtuals; the adapter reads them (registry wins transitionally until it is deleted).

- M3 done (5e9cbbe): every maneuver declares its own Category/Move next to OwnsLocomotion; ManeuverRegistry deleted; BotManeuverClaimTests pins the table and proves a slash fires while ChaseStance walks and Guard leaves Move empty. EscapeGrab stays Exclusive (it leans toward the grabber). Parity after M3: Bulldog vs Brawler 23, Ranger vs Brawler 23.5, Ranger vs Riposte 11.5, Ranger-NoLearn vs Brawler 24. Bot group 183/6/189, known reds only.

- M4 cleanup done (ee04720, merged): ManeuverWeights, TacticBandit and RangerBot.Tactics deleted; HoldRangeStance (Ranger) + BulldogBot.ExtraStances; ZzzRangerEval is now the learner-on/off A/B over seeds 1–6. First A/B with the still-derived bets: Ranger 124.5 vs Ranger-NoLearn 132.5 of 180. Reported, not gated; re-run after the authored bets land.

- M4 bets done (90cfa25, merged e415f82): hand-authored HP bets on every Bulldog-family maneuver and both stances, Score gates kept, tie groups stepped 0.01 in list order so argmax reproduces the old brain. Parity after M4: Bulldog vs Brawler 23 (identical bouts to M3), Ranger vs Brawler 24, Ranger vs Riposte 14, Ranger-NoLearn vs Brawler 24 (identical). Bot group 183/6/189, known reds only.
- M5 done (dcf19ad): BotTraceReport shared by ZzzDuelistLeague and ZzzRangerEval; Bulldog README "Brain" section; CODEBASE_OVERVIEW controller paragraph; BACKLOG 5.21 (phase 2) and 5.22 (learning gaps); "Bet conventions" in this doc.

## Results (phase 1, 2026-11-04)

Parity harness: `MTile.Tests/Sim/ZzzBotControllerParity.cs`, points of 24 (3 arenas × seeds 1–4 × both sides, 60 s bouts).

| Matchup | Old brain | On the controller (M4) |
|---|---|---|
| Bulldog vs Brawler | 24.0 | 23.0 |
| Ranger vs Brawler | 24.0 | 24.0 |
| Ranger vs Riposte | 9.0 | 14.0 |
| Ranger-NoLearn vs Brawler | 24.0 | 24.0 |

Learner A/B: `MTile.Tests/Sim/ZzzRangerEval.cs`, points of 36 per opponent (3 arenas × seeds 1–6 × both sides).

| | Rampart | Citadel | Riposte | Bulldog | Brawler | total /180 |
|---|---|---|---|---|---|---|
| Ranger (ledger on) | 28.5 | 25.0 | 14.5 | 29.0 | 36.0 | 133.0 |
| Ranger-NoLearn | 31.5 | 23.5 | 15.0 | 26.5 | 36.0 | 132.5 |

A wash at this horizon: 60 s bouts give a few dozen settles per arm, and the largest learned offsets are on `BulldogBreaker` (taken +0.5 HP under 40 px) and `DownAirManeuver` (dealt −0.2 in the air). Occupancy: ChaseStance holds Move ~54% of the time, no action is committed ~47% of the time, BulldogString is the top action at 14%. HoldRangeStance ran 0.2% (see Decisions needed). Not verified: the KNI web build (`MTile.Web`) was not compiled during the campaign; the new code uses only plain .NET and XNA types.

## Decisions needed

- **Stances cannot explore.** Stance hysteresis compares the softmax-sampled candidate against the holder plus PreemptGap + StayBonus (0.35 HP), so a new stance like HoldRange (value 0 vs Chase's −0.1) is sampled but never acquires Move, never opens a record, and so never learns (0.1% occupancy in the A/B). Options: an epsilon that lets a sampled stance take Move for one horizon without the gap; or an optimistic initial offset for arms with zero settles; or accept that stances are hand-ordered and only actions learn. Not decided in the campaign: it changes what "no minimum-hold timers" means in practice.
- **BulldogBreaker is the learner's clearest signal.** Its dealt offset settles at about −0.8 HP under 40 px against the whole field, i.e. the combo-break guard almost never leads to damage within its horizon. Whether to shorten its horizon, let the ledger keep demoting it, or revisit the maneuver is a design call.

## Deviations from the plan text

- **M3 converts by override, not by subclassing.** Guard, Dash, Breach, WallClimb, WallUp, EscapeGrab, DownAir and the aimers are shared with the phase-2 brains, which still take `List<BotManeuver>`. Re-basing them on `BotBehaviour` would break those brains, so a maneuver declares its claim by overriding `BotManeuver.Category`/`Move` in its own file. Same effect (claims live with the maneuver, the registry table goes away), none of the churn.
