# Bulldog: rushdown duelist

`BulldogBot.Create` is the `DuelBot` entry point. The brain (`BulldogBot.cs`) perceives and then
steps a `BotController` (see Brain below) over its maneuver list. It emits raw `PlayerInput`
only.

## Brain

`BulldogBot.Poll` is perception plus one `BotController.Step` (`Net/Bot/Control/`, design in
`Plans/BOT_BEHAVIOR_ARCHITECTURE.md`). Two selectors run in parallel:

- **Move:** a reflex, else a committed action's Move claim, else the current stance.
- **Act:** which action fires, with an Idle arm that wins whenever every action's corrected
  bet is negative.

Stances are `ChaseStance` (walk inside `EngageRange`) and `StepOutStance` (back out of reach
during our own end lag). They return a target position; `MoveExecutor` does the walking, stuck
hops and air steering, and `RoutePlanner` takes over when the straight line needs a climb or a
detour. Each maneuver declares a claim next to its code: `None` (a plain slash, so it fires
while the stance keeps walking), `Forbid(all)` (hold still: guards, pulse, aimers, block
throw) or `Exclusive` (its input is movement: jump slash, dash punish, climbs). Constraints
(`SpikeGuardConstraint`) mask the final input. A holder keeps its resource unless beaten by
the preemption gap; the stance gets a staying bonus.

Every behaviour bets `(dealt, taken, horizon)` in HP. The bet ledger (`BetLedger`) corrects
each bet by a learned offset per situation bucket. It is **off by default for plain Bulldog**
(the authored bets run as written) and **on for Ranger**, where it ships with data-calibrated
priors (`BetCalibration.Field`), records that close at the next decision in their category and
bootstrap off the next arm's value (SARSA over options), hierarchical offsets, a sharp softmax,
and the mix-up die switched off so the learner picks among stab / cross-up / low poke / grab
itself. Over 24 seeds against the duelist field the learner beats this hand-tuned brain; the
numbers and the path to them are in `Plans/BOT_BEHAVIOR_ARCHITECTURE.md` ("Learning: what
shipped") and `MTile.Tests/Sim/ZzzLearningEffect.cs`. With a ledger, stances switch on any
improvement but hold at least 30 frames (no value hysteresis). `ControllerTrace` records
occupancy, acquisitions, preemptions and oscillation; the league printout shows it.

## Playstyle

Bulldog walks straight into the opponent's face. `ChaseStance` aims for a spot 22 px
from the opponent, inside slash reach, where the Brawler's stops at 34, and `MoveExecutor`
walks there. It stays there and keeps swinging. `StepOutStance` takes over in our own end lag.

- **Long range (more than about 150 px):** it runs in, hops steps and walls, and uses
  stock `WallClimb` and `Breach` to dig through barricades. Stock `Dash` with a DashSlash
  covers 66–154 px.
- **Mid range (44–150 px):** `BulldogPunish` waits for the opponent to be busy (as seen
  6 frames late: swinging, charging, recovering or flinching), then dashes in for a
  DashSlash, which has a 3 f startup. `BulldogStab` fires from just outside slash reach
  as a lunging launcher.
- **Close range:** `BulldogString` taps the opener and then presses again just before
  each combo gap opens (taps at frames 0, 13 and 28). That chains S1 → S2 → S3 on the
  ground and AirSlash1 → 2 in the air. Each follow-up is hit-confirmed off the HUD HP
  bar, and a whiffed opener into a guard is not continued.
- **The mix:** a die rolled after every maneuver selects one of these:
  - a cross-up hop with a back whip-kick (`BulldogCrossUp`)
  - a crouch slash (`BulldogLowPoke`), which is also the long poke out to about 64 px
  - stock `DownAir` pogo
  - a grab-and-throw into the nearest wall or the floor (`BulldogGrab`), used only
    against a guarding opponent. With the 20 f grab startup it lost value as a blind
    mix-up.
- **Our own end lag:** during the last part of our recovery Bulldog steps just out of
  reach, then walks straight back in. It never stands in the opponent's range unable to
  swing.
- **Defense:**
  - `BulldogBreaker` breaks combos. When an opener hits us, it raises guard on the combo
    rhythm (at hit+10, using guard's 0.2 s stun-escape window), so the parry lands on
    the second hit and arms GuardRetaliate.
  - Stock `Guard` covers seen swings in range. `BulldogGuard` covers slow moves (stab,
    hammer, down-air, S3, pulse) out to 80 px.
  - Stock `EscapeGrab` mashes out of grabs.

**Win condition:** Bulldog wins exchanges at close range on volume and combo damage
(1.5 HP per landed opener), and punishes whiffs with the dash cut. Kills come from
repeated strings.

## Known weaknesses

- Its approach is a straight line. A zoner that walls up and keeps re-walling makes it
  dig, and against crystal waves it relies on the generic hop.
- S3 is not a true combo, because hitstun extension is halved on follow-ups. An opponent
  that parries on reaction can catch the finisher.
- Even trades: it accepts S1-vs-S1 and dash-vs-dash trades.
- Grabs are rare, so a hard turtle that never shows `GuardAction` early enough is only
  opened by the cross-up.

## Record vs the Brawler

This table is from before the controller migration. `MTile.Tests/Sim/ZzzBotControllerParity.cs`'s
header carries the before/after numbers (Bulldog vs Brawler 24 to 23 of 24 points after M3).

Full league format: 60 s bouts, seeds 1–4, both sides (`RoundRobin_BeatsBrawler`).
**23-0-1, K/D 77/38, damage 435.5/271.7.**

| Arena | W-D-L | K/D | Damage dealt/taken |
|---|---|---|---|
| hall_close | 8-0-0 | 27/15 | 153.5/95.6 |
| hall_far | 8-0-0 | 23/11 | 130.3/81.3 |
| plain_close | 7-0-1 | 27/12 | 151.7/94.8 |

## Fairness notes

- Every opponent fact comes from a ring buffer at least `ReactionFrames` (6) old:
  position, velocity, action, flinching, grounded, grabbed and facing. The one exception
  is HP, which is shown on the HUD.
- The bot reads only its own live state: recovery countdown, hit clock, guard flags and
  its own grab victim slot.
- Press rates stay at or below about 12 per second for every button.
- All randomness comes from the seeded `System.Random`.
