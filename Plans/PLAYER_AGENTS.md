# Player agents — building and evaluating a bot opponent

Entry doc for writing a new P2 bot ("duelist") and measuring it against the existing
ones. Self-contained: read this, then `CLAUDE.md` (build/test mechanics, determinism
rules), then the code it points at. Background and history: `Plans/BOT_AI_PLAN.md`
(why bots are raw input), `Plans/DUELIST_LEAGUE.md` (the three existing duelists, their
results, and what the league taught us about the game).

## 1. What a bot is

A bot is an `IRemoteInputSource`: once per sim frame, `Poll(Simulation sim, int frame)`
returns a raw `PlayerInput` — exactly the record a keyboard + mouse produce (`Left`,
`Right`, `Space`, `Shift`, `LeftClick`, `RightClick`, `Num1`–`Num5`,
`MouseWorldPosition`, …). The game's `InputParser` turns that into gestures exactly as it
does for a human. There is no "bot API" into the action system, on purpose: a bot can do
precisely what a player can, and every move it makes is a move a player could make.

A duelist is published as a factory:

```csharp
public static readonly DuelBot Create = (selfIndex, seed) => new MyBot(seed, selfIndex);
```

`selfIndex` −1 drives the primary player (P1) against `SecondaryPlayers[0]`; 0 drives P2
against the primary. Use `BotDuel.Self(sim, selfIndex)` / `BotDuel.Opponent(sim,
selfIndex)` — never `sim.Player` directly — so the same bot plays either side.

## 2. Rules (the league enforces these; the arbiter audits the code)

1. **Raw `PlayerInput` only.** No writes to the sim, the player, or any game object.
2. **Perception like a player's.** Everything about the opponent — position, velocity,
   current action, grounded / helpless / grabbed — comes through a delay line at least
   **6 frames** old (~100 ms reaction). Record the opponent's visible state into your own
   ring buffer each `Poll` and read only the delayed samples (see `BulldogBot.Perceive`,
   `RampartBot.Perceive`). Allowed live: the opponent's HP (it's on the HUD), your own
   state, and the terrain (it's on screen). Not allowed: the opponent's input, internal
   timers, `ActionVars`, anything a player couldn't see.
3. **Human-rate input.** At most ~12 presses per second per button; no frame-perfect
   mashing (the guard has a re-entry cooldown that punishes it anyway).
4. **Deterministic.** Same `(arena, seed)` ⇒ same bout. All randomness through the
   seeded `Random` you're constructed with; no wall clock, no static mutable state, no
   threads.
5. **No game-code edits.** Don't change anything under `Character/`, `World/`,
   `Entities/`, `Physics/`, etc. to help your bot. Shared bot code (`Net/Bot/*.cs`,
   `Net/Bot/Maneuvers/`) may only be extended opt-in (a new flag defaulting to the old
   behaviour) and must say so in your report. Copy-and-modify into your own folder is
   always fine.
6. **No stalling.** A bot that wins by never engaging is disqualified. If your style is
   defensive, build in an anti-stall rule (`RampartBot.UpdateHome` is the reference: it
   pushes in after 5 s with no HP lost on either side).

## 3. Building blocks

| Piece | Where | What it gives you |
|---|---|---|
| `BotView` | `Net/Bot/BotView.cs` | The perception record maneuvers read: `Me`, `MyVel`, delayed `TargetPos`/`TargetVel`/`TargetAction`/`TargetGrounded`/`TargetHelpless`, `Dx`/`Dy`/`Toward`, terrain probes (`Solid`, `FirstSolidAlong`, `LineOfSight`), `FramesSinceAttack`. Fill it yourself in `Perceive`. |
| `BotInput` | `Net/Bot/BotView.cs` | Small input helpers (`Move`, …). |
| `BotManeuver` | `Net/Bot/BotManeuver.cs` | One behaviour: `Score(v)` (utility, 0 = not now), `Begin`, `Step(v, ref input)` (return false when done), `Abort`, `Cooldown`, `OwnsLocomotion`. |
| Shared maneuvers | `Net/Bot/Maneuvers/` | ~30 ready-made: slash, stab, air stab, crouch slash, down-air, pulse, guard/parry, grab/escape, block throw, dash, wall climb, dropdown, backoff, breach (dig), build-climb, enclose, wall-up, eruption, block burst, avalanche launch/charge/crush/ride, crystal wave. Each file's header says when it scores and how it synthesizes its gesture. |
| `BarrierPlanner`, `BotTerrainSense` | `Net/Bot/Maneuvers/` | Wall/column planning and terrain queries for building maneuvers. |
| Brains to copy | `Net/Bot/Duelists/{Bulldog,Rampart,Riposte}/` | Three complete utility brains with READMEs. `BrawlerBotInputSource` is the stock all-maneuvers brain (and the weak baseline). |

The brain pattern all four use: each `Poll`, perceive → tick cooldowns → if no active
maneuver, start the highest-scoring one → default locomotion unless the maneuver owns it →
step the maneuver. Abort the active maneuver when you take damage. You don't have to keep
this shape — a different brain entirely is fine — but it is the fastest start.

### Gesture cheat-sheet (what the input parser expects)

- **Click** (slash; context picks the variant): LMB down then up within 0.2 s.
- **Stab**: hold LMB > 0.2 s and swipe the cursor ≥ 12 px (player-relative) before
  release; the swipe direction aims it.
- **Circle** (pulse): hold LMB and sweep the cursor ≥ 270° around the press point at a
  radius ≥ 16 px.
- **Guard / parry**: Shift held on the ground, no Left/Right, cursor aims the cone. Only
  the first ~7 frames after raising it are a perfect parry.
- **Dash**: Space and Shift pressed on the same frame; it throws the body toward the
  cursor (on the ground it's a dash off the take-off). A click during the dash is a dash
  slash. Once per airtime. See `DashManeuver`.
- **Build**: `Num1`–`Num4` pick Stone/Dirt/Sand/Foam, `Num5` picks spikes. Plain RMB
  paints through the build ball; Shift+RMB places one block per cell entered.
- **Avalanche**: hold RMB with the cursor inside solid to charge, then flick the cursor
  out fast and release (`AvalancheLauncher`). With spikes selected the same gesture is a
  crystal wave (`CrystalWaveManeuver`; 8 s cooldown).
- The cursor is in world space (`MouseWorldPosition`); most attacks aim at it.

### Gotchas that cost the last round of authors time

- Two bodies stop each other at ~21 px centre-to-centre; spacing logic below that is moot.
- A player facing away from the target turns with a slower turn-slash. Face first.
- `DamageTaken` resets on respawn (that is how `BotDuel` counts deaths) — reset any
  per-life state when it drops.
- `GrabAction` shadows `BeamAction` (same gesture, higher priority): beam is unreachable.
- Opposing melee hitboxes **clank** (`CombatSystem.ResolveClanks`): within 20 % damage both
  cancel, otherwise the stronger wins. Two identical swings on the same frame do nothing.
- Spikes break off when hit (any hit on a spiked or seeding cell) and when their block
  breaks. Opposing spikes hurt on contact.
- Shared maneuvers read the opponent only through `BotView`'s delayed fields. If you write
  your own, keep to that — `v.Target` is there for HP and nothing else.

## 4. Where things go

- Code: `Net/Bot/Duelists/<Name>/` (namespace `MTile.Net`), with a `README.md` describing
  the playstyle, the maneuver set, and the bot's record.
- Tests: `MTile.Tests/Sim/BotManeuverDuelist<Name>Tests.cs`. The `BotManeuver` prefix puts
  it in the `bot` test group (`scripts/test-group.py bot`). At minimum: it beats
  `BotDuel.Brawler` on the tuning seeds, and a determinism check (two runs, same result).
- Play it in the game: add a case to the `BotOpponent` switch in `Game1.cs` (search
  `BotOpponent`), then set `"BotOpponent": "<Name>"` and `"SpawnSecondPlayer": true` in
  `configs/game_config.json`.
- Work on a branch off `main`. Commit with a clear message.

## 5. Evaluating

`Net/Bot/BotDuel.cs` is the harness:

- `BotDuel.Run(arena, p1, p2, seed, frames = 3600)` — one 60 s bout in a real Versus
  `Simulation`. Returns kills and HP lost per side; more kills wins, equal kills → ≥ 0.5 HP
  more damage dealt wins, else a draw.
- `BotDuel.RoundRobin(bots, seeds, arenas)` — every pair × arena × seed × both spawn
  sides; win 1, draw ½.
- Arenas (`BotDuel.Arenas`): `hall_close` (roofed hall, spawns 24 tiles apart), `hall_far`
  (~72 tiles), `plain_close` (open sky, 24 tiles). All have Hardened outer walls.

**Seeds.** Tune on seeds 1–10. The arbiter evaluates on held-out seeds of its choosing
(11–16 are spent — their results are published). A bot that only wins on its tuning seeds
is overfit.

**The league.** `MTile.Tests/Sim/ZzzDuelistLeague.cs` runs the full round robin and writes
the standings + every bout to `Plans/DUELIST_LEAGUE_raw.txt`. Add your entrant to its bot
list. Run it in Release (≈ 3 minutes for four bots):

```bash
dotnet test MTile.Tests/MTile.Tests.csproj -c Release --filter "FullyQualifiedName~ZzzDuelistLeague"
```

**Diagnostics worth copying.** `Starts` (how often each maneuver began) is on every brain.
`RampartBot` also keeps a damage ledger — HP dealt and taken, credited to the maneuver
started in the last 1.5 s, plus hits bucketed by size (spike ticks are 0.25, a slash 0.5).
That ledger is how the last league found out its fortress bot was winning on melee and
spikes, not on the crystal waves everyone assumed. Know *why* your bot wins before you tune.

**Current field** (re-run after the clank / stab / crystal-wave changes; details in
`Plans/DUELIST_LEAGUE.md`):

| Bot | Style | Points /108 | Known weakness |
|---|---|---|---|
| Rampart | fortress: spike carpet, hit-and-run, crystal waves | 88 | melee fallback once someone is inside; spikes now break on hit (fell to 4th of 5 in the Citadel run) |
| Riposte | counter-fighter: spacing, parries, punishes | 67.5 | no terrain awareness — parks on spikes (2–34 vs Rampart) |
| Bulldog | rushdown: strings, dash punish, mix-ups | 56.5 | straight-line approach loses to stab-checks and parries |
| Brawler | stock baseline (every maneuver, no plan) | 4 | everything |

## 6. Answered: is hit-and-run beatable? (yes — and it was never Rampart's edge)

Rampart's hit-and-run is swing, then walk straight back out of reach (`RampartBackoff`).
Measured 2026-11-03 (`MTile.Tests/Sim/ZzzHitAndRunProbe.cs`, `ZzzHarrierEval.cs`):

- **A whiffed swing is punishable on reaction.** Correction to the earlier version of
  this section: a dropped Slash 1 does *not* lock attacks out for 36 frames — Slash 2 is
  available ~17 frames after the press (S1's 12 + `ComboGapFraction`) and comes out 4
  frames later. Even so, a dash slash chorded 6–8 frames after seeing the swing lands
  from every gap tested (52–90 px) against a runner that keeps walking back; answering
  with Slash 2 only trades into a clank at the long gaps. The runner's real answer is to
  stop and guard on seeing the dash (guard can be entered during own recovery). A missed
  **stab** is the big window: active ends ~18 frames in, then ~60 frames before the next
  attack.
- **The trap is triggering on a stab's wind-up.** The stab has an 11-frame startup and
  ~80 px reach (48 px blade + ~20 px of lunge + the body); a punisher that dashes on
  *seeing* a stab start runs into it. Wait for the attack's active window to end
  (`WhiffPunishManeuver.WhiffAt`), and bait at the edge of stab range, not slash range.
- **The counter works mechanically** — `Net/Bot/Duelists/Harrier/` (Bulldog + a
  bait-and-dash-punish maneuver): against Rampart reduced to melee + guard + backoff,
  242 of 339 punishes landed and none got hit back.
- **But hit-and-run alone is weak.** Plain Bulldog already beats melee-only Rampart
  56.5/60 (tuning seeds 1–10, all arenas, both sides); full Rampart holds it to 49.5.
  Rampart's edge was its terrain game, not the retreat. The earlier ledger credit
  (~170 HP to Backoff) was an attribution artifact: Backoff starts the frame after the
  slash press, so the slash's own damage was billed to it.
- Harrier is not an improvement over Bulldog in the full field (ties Rampart, loses
  ground vs Riposte): the bait costs tempo, and other bots rarely whiff on their own.

Open from here: punishing a runner who *guards* on seeing the dash (dash in and grab,
or dash short and wait out the guard) — the mixup the guard answer creates.

## 7. Report

When done: bot name, files, branch + commit; the playstyle in a few lines; record vs each
existing bot and vs Brawler per arena on the tuning seeds; tests and their results; any
rule-5 opt-in changes to shared code; any game bugs or exploits found (an exploit is a
finding to report, not a strategy to ship); and anything the arbiter should know before
running the held-out evaluation.
