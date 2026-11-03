# Duelist league — 2026-11-03

Three P2 bots, each built by its own Opus agent with an assigned playstyle and the goal
of beating the other two, arbitrated by the supervising session. Code:
`Net/Bot/Duelists/{Bulldog,Rampart,Riposte}/` (each has a README with its design);
harness `Net/Bot/BotDuel.cs`; runner `MTile.Tests/Sim/ZzzDuelistLeague.cs`; raw bout log
`Plans/DUELIST_LEAGUE_raw.txt`.

**Play one:** set `"BotOpponent": "Bulldog" | "Rampart" | "Riposte" | "Brawler"` in
`configs/game_config.json` (with `"SpawnSecondPlayer": true`).

## Format

- Arenas (the shipped duel stages, Hardened outer walls): `hall_close` (roofed hall,
  spawns 24 tiles apart), `hall_far` (~72 tiles), `plain_close` (open sky, 24 tiles).
- 60 s bouts, Versus respawn. More kills wins; equal kills → ≥ 0.5 HP more damage dealt
  wins; else draw (½ point).
- Every pairing × 3 arenas × **held-out seeds 11–16** (all entrants tuned on 1–4) × both
  spawn sides = 36 bouts per pairing, 108 per entrant. The stock `Brawler`
  (`BrawlerBotInputSource`, the shared 29-maneuver utility brain) entered as reference.

## Rules and audit

Raw `PlayerInput` only; ≤ ~12 presses/s per button; everything about the opponent through
a ≥ 6-frame-delayed view (HP allowed live — it's on the HUD); no game-code or
shared-framework edits; no degenerate stalling; deterministic. All three passed the
audit: each records the opponent's visible state into its own delay line inside its
perception step and reads only the delayed samples (Bulldog 6 frames, Riposte 7, Rampart
8). Riposte's author found that several *shared* maneuvers read the opponent live
(grounded/stun/grab state, current action, grab victim position) and the guard counter
clicked ~30/s; patched for everyone before the league (`0e5824a`).

Ruling: Rampart's spiked carpet around its fort is legitimate — the spikes are visible
through the full seed → flash warning, a defended position is the barricade play the
game wants, and the bot keeps fighting (waves at range, an advance when the opponent
stays away, a push into melee after 5 s without damage).

## Results

| Rank | Bot | Style | Points | W-D-L | K/D | Damage dealt/taken |
|---|---|---|---|---|---|---|
| 1 | **Rampart** | terrain zoner / fortress | 80.5 | 78-5-25 | 291/175 | 1711/1247 |
| 2 | Riposte | counter-fighter / footsies | 68.0 | 67-2-39 | 249/217 | 1541/1420 |
| 3 | Bulldog | rushdown | 64.0 | 61-6-41 | 308/268 | 1784/1628 |
| 4 | Brawler | reference | 3.5 | 2-3-103 | 130/318 | 1086/1828 |

Head to head (row's points vs column, of 36):

|  | Bulldog | Rampart | Riposte | Brawler |
|---|---|---|---|---|
| **Bulldog** | – | **25.5** | 5.0 | 33.5 |
| **Rampart** | 10.5 | – | **35.0** | 35.0 |
| **Riposte** | **31.0** | 1.0 | – | 36.0 |

By arena the order holds everywhere; Rampart is strongest in `hall_close` (29.0).

## Reading it

**A clean rock-paper-scissors cycle** — the three styles counter each other the way the
genre says they should, which is the best outcome the brief could have produced:

- **Rushdown beats the fortress** (Bulldog 25.5–10.5 over Rampart). Bulldog walks straight
  through the waves and the carpet, digs the barricades, and once it's inside the fort
  Rampart's melee fallback loses the exchanges. Rampart's spike attrition doesn't outpace
  a bot that never stops attacking.
- **The fortress crushes footsies** (Rampart 35–1 over Riposte). Riposte's spacing — park
  just outside the opponent's reach — parks it on Rampart's spike carpet, and Riposte
  doesn't read terrain hazards at all. That is Riposte's blind spot, not an exploit.
- **Footsies beats rushdown** (Riposte 31–5 over Bulldog). Bulldog's straight-line
  approach and committed strings are exactly what stab-checks, parries and whiff
  punishes are built to beat.

Rampart takes first on points because its winning matchup is the most lopsided. All
three crush the stock Brawler (≥ 33.5/36), which is now clearly obsolete as an opponent.

## Game findings surfaced along the way (not acted on)

- **The shared `CrystalWaveManeuver` lays only ~6 crystals** — it launches while the
  paint ball is still climbing. Rampart's launcher waits for the ball to settle in the
  first air row and pulls out level: ~37 crystals over 14+ tiles. Worth porting back.
- **Any wall kills a crystal wave**, so `WallUp` cancels most of them; a 2-tall kerb is
  enough (Rampart uses one). Maybe fine — it's the intended counter — but it's cheap.
- **S2 → S3 isn't a true combo** (Bulldog): follow-up hitstun is halved, leaving the
  victim ~10 frames free before S3; a reaction parry catches it.
- **The spike carpet is very strong against ground-bound spacing play.** If that's not
  the intended balance, the lever is spike cost / contact damage / the 1.25 s grow time.
- **Slash trades are even** — neither Riposte nor Bulldog can avoid trading S1-for-S1
  against a spammer that walks them down; retreat is no faster than the chase.

## Next steps if the league continues

- Give Riposte terrain awareness (avoid spiked cells, treat a fort as a zone) — its one
  catastrophic matchup.
- Port Rampart's level wave launcher into the shared `CrystalWaveManeuver`.
- Re-run with each bot allowed one revision against the others' visible code — the
  current cycle is from blind development.
