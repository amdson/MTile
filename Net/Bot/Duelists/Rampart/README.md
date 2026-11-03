# Rampart: terrain zoner / fortress duelist

`RampartBot.Create` (`DuelBot`), namespace `MTile.Net`. The brain is `RampartBot.cs`, which
also holds the `RampartSense` delayed perception. Tests are in
`MTile.Tests/Sim/BotManeuverDuelistRampartTests.cs` (bot group).

## Playstyle
Rampart picks a spot, fortifies it, and makes every approach cost HP. It holds a **home
column** and rings it with a **spiked floor**: 5 cells toward the opponent and 3 behind.
Crystal waves go out at range. An opponent who comes in to fight stands on the spikes,
taking 0.25 HP and a pop-up hitstun every 0.5 s. Melee then holds them there or knocks
them back across the spikes. The fort is not a bunker. It moves up when the opponent stays
away, and it pushes into melee when nobody has lost HP for 5 s.

| Situation | What it does |
|---|---|
| Opponent far (>6 tiles) | `RampartSpikeMoat` lays the spike carpet around home (Num5 + Shift+RMB drag, one cell per frame). Spikes seed and need about 1.25 s to grow, so the carpet goes down ahead of the approach. It keeps a 60-unit Build reserve. |
| 9–14 tiles, line of sight, wave ready | `RampartCrystalWave`: it charges in the floor, lifts the paint ball to the first air row and waits for it to **settle**, then pulls it level along the row. RMB comes up when the ball is fast and level. That level launch lays about 37 crystals over 14+ tiles. The stock skim releases the ball while it is still climbing, so the stock wave lays about 6. If the opponent closes inside 4.5 tiles during the charge, the charge is banked in the floor. |
| An opposing crystal wave on our floor line, or a ranged charge | `RampartWallUp`. Against a wave it raises a **2-tall kerb**: the wave shatters on it, and it stays jumpable, so nobody gets sealed in. Against a beam or lob it raises a 5-tall column. |
| Opponent in pocket / still / stunned | `RampartAvalancheCrush` (roofed pocket); `RampartEnclose` (stone box with a spiked floor). |
| Opponent on the carpet, in melee | `RampartGuard` (timed parry and counter), stock `Slash` and `Stab` (the stab is the launcher, and it knocks them back across the spikes), `RampartPulse` when hugged and hurt, `RampartBackoff` hit-and-run (it backs onto its own carpet). Locomotion steps back home when knocked off. |
| Opponent stays >16 tiles and isn't coming | After 1.5 s, home relocates to 10 tiles short of them. If they start coming while it walks out, it plants right there. |
| No HP lost on either side for 5 s | **Anti-stall push**: home moves to 3 tiles from the opponent. This is what makes the Rampart mirror fight. |
| Wall in the way | stock `Breach` digs; locomotion stuck-jumps kerbs. |

**Win condition:** attrition on the approach. Spike ticks are typically 15–45 per bout,
about 4–11 HP. Crystal waves add pressure, and the walls they force cost the opponent
meter. Melee then wins exchanges against an opponent who is being popped by the floor.

## Fairness / rules
- Everything about the opponent comes from Rampart's own 8-frame-delayed ring: position,
  velocity, action, grounded, grabbed, helpless. That ring also fills BotView's
  `TargetGrounded`/`TargetGrabbed`/`TargetHelpless` for the stock maneuvers it uses.
  Its only live read of the opponent is `TargetHealth` (the HUD). Its own state (meters, its paint
  ball), the terrain, spikes and visible crystals are read freely.
- Copies of the stock maneuvers are prefixed `Rampart*`, and nothing shared was edited.
  The guard counter taps at 12/s. Every other button runs at stock rates or slower.
- Seeded `System.Random` only, and no mutable statics.

## Known weaknesses
- The carpet only covers the floor. An opponent who fights entirely from the air, or from
  out past ~6 tiles, avoids the ticks. Waves and the advance/push logic are the answer to
  that, and they are weaker than the carpet.
- A wave is blocked by any wall, and the Brawler's WallUp answers it. Expect about 0–1 HP
  per wave against a bot that kerbs.
- An eruption ridge built over the carpet buries it. It gets re-spiked, but only between fights.

## Record vs `BotDuel.Brawler` (RoundRobin, 3 arenas x seeds 1–4 x both sides)
**23-0-1, K/D 62/20, damage 369.1 / 204.8.**
- hall_close: 8-0-0
- hall_far: 7-0-1 (one loss: s3 as P1, 2-2 on kills, 10.7 vs 11.4 damage)
- plain_close: 8-0-0

Proxies run during tuning (2 seeds, all arenas, both sides):
- a Brawler held at 10 tiles (`EngageRange`): 11-0-1
- a melee-only Brawler: 12-0-0
- `RiposteBot`: 12-0-0
- the Rampart mirror: about even, and it fights
