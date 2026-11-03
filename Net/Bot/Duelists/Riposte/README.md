# Riposte: counter-fighter / footsies duelist

`RiposteBot.Create` (`DuelBot`). Files: `RiposteBot.cs` (brain, spacing locomotion,
`RiposteSense` delayed perception), `RiposteModel.cs` (opponent-habit memory),
`RiposteManeuvers.cs` (its own maneuvers). Tests: `MTile.Tests/Sim/BotManeuverDuelistRiposteTests.cs`.

## Playstyle
Riposte stays just outside the opponent's reach. It makes them swing first, then punishes
the whiff or the recovery. It also parries and counters, and checks approaches with the
stab. After every hit it leaves.

| Situation | What it does |
|---|---|
| Neutral, mid range | Holds a gap of the learned threat reach plus about 10 px. A seeded in/out "dance" keeps leaning into their range edge as bait. It never walks into a live swing. |
| They walk into range | Stab check: the stab out-ranges every slash and is armored through its wind-up. The prediction is the delayed position plus velocity over the stab's 11 f startup. |
| They swing, slow wind-up (stab, air hammer, pulse) | Guard on reaction. The delayed pose still leaves time to land inside the 7-frame perfect window. Then it takes the GuardRetaliate counter. |
| They swing, fast slash | It can't react in time, so it guards on a *read*: they are closing into their swing range and the model says they swing on entry. It also guards when stuck in its own end-lag with them closing, and against the follow-up step of a slash chain. |
| They dash in | Guards for the arrival: the dash pose is visible, and the DashSlash comes out in 3 f. |
| Whiff / recovery / flinch / grab wind-up | Punish. It walks in and runs the slash chain. For a long tail at 58–150 px it dashes in and dash-slashes. |
| Zoner charging a ranged/terrain move within 150 px | Dashes in to break the wind-up. Against a zoning opponent it also holds a tighter gap. |
| After its own attack | Hit-and-run retreat, unless the target is helpless. |
| Stale bout / losing late | **Pressing**: smaller gap, more bait steps, neutral stab pokes, and it attacks a guard that has been held too long. A counter-fighter that never presses would only draw against a patient opponent. |

**Adaptation** (`RiposteModel`, built only from delayed visible samples and the two HP
bars). It tracks:
- how often they swing per second while I'm near (aggression);
- the gap their swings start at and the gap their hits land at (threat reach, which sets the spacing);
- their ranged share (zoner, so it tightens the gap);
- their whiff rate and attack rhythm;
- its own stab and slash hit rates.

A stab the opponent keeps parrying stops being thrown outside the sure windows.

**Win condition:** many small, safe exchanges, each one a hit on their recovery, a stab on
their approach or a parry counter. Kills come from attrition and the stab's launch.

## Fairness
- Opponent position, velocity, action and movement pose, flinch, grounded state and facing all come from a 7-frame-delayed ring (`ReactionFrames` = 7).
- Its anticipation is the delayed sample plus velocity × delay.
- It reads the opponent's HP (HUD), plus its own state and the terrain.
- No opponent internals: no cooldowns, guard timers, inputs or RNG.
- The brain caps LMB and Space press edges at one per 5 frames (≤ 12/s).
- Seeded `System.Random` only.

## Known weaknesses
- Slash spammers that walk it down still trade. Retreating is no faster than chasing, and guard reads only catch some swings (vs a slash-spam Brawler variant: 12-0-0 but K/D 43/29).
- It can't react to slashes, only read them, so a varied-rhythm opponent gets more through.
- It is ground-oriented. The guard is ground-only, so it is weak while airborne after a launch.
- Terrain play is defensive only: it breaks wind-ups but never builds.

## Record vs `BotDuel.Brawler`
60 s bouts, seeds 1–4, both sides: **24-0-0, K/D 77/20, damage 438.5 / 193.9.**

| Arena | W-D-L | K/D | Damage dealt / taken |
|---|---|---|---|
| hall_close | 8-0-0 | 27/9 | 158.4 / 74.0 |
| hall_far | 8-0-0 | 23/4 | 132.7 / 57.2 |
| plain_close | 8-0-0 | 27/7 | 147.3 / 62.4 |
