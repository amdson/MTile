# Wave 3 + Hollowstep: six fights to watch

Each fight is a typical bout of its pairing in the 29-bot league
([league.md](../league.md): seeds 100..115, 1800 frames), not a highlight. Recorded at
commit `6f055da`. Fighter order is left spawn versus right spawn. The record after each
title is the pairing on that terrain with this spawn order, over 16 seeds (left's
W-D-L).

Run commands from the repository root. **F6** pauses; **F7** advances one frame.

## 1. Hollowstep vs Thornbull, flat, seed 100: Hollowstep wins (8-0-8)

The counter-puncher against the top-rated rammer. Hollowstep backs off while Thornbull
charges and fires whenever its ball will land first. Thornbull deals 7 of its 8 health, and
Hollowstep finishes with 1.0 left. Thornbull wins the other half of these seeds, so this is the
closest pairing at the top of the table.

```bash
dotnet run --project MTile.Desktop -- --fight Fights/competition/wave3_with_hollowstep/showcase/01_Hollowstep_vs_Thornbull_flat_s100.fight.json
```

## 2. Hollowstep vs Phalanx, flat, seed 103: Phalanx wins (0-0-16)

Hollowstep's clean loss. Phalanx's 800 px/s balls can't be hopped from inside 150 px.
With 200 ground power, Hollowstep can't walk forward through the knockback faster than
the next ball arrives. It lands one ball in 13 seconds.

```bash
dotnet run --project MTile.Desktop -- --fight Fights/competition/wave3_with_hollowstep/showcase/02_Hollowstep_vs_Phalanx_flat_s103.fight.json
```

## 3. Arrowhawk vs Hollowstep, flat, seed 102: Arrowhawk wins (9-6-1)

The Haiku bot is Hollowstep's other bad matchup, 179 Elo below it. Arrowhawk deals all
8 damage and takes none.

```bash
dotnet run --project MTile.Desktop -- --fight Fights/competition/wave3_with_hollowstep/showcase/03_Arrowhawk_vs_Hollowstep_flat_s102.fight.json
```

## 4. Thornbull vs Thornrush, flat, seed 100: Thornbull wins (16-0-0)

The top two, both rammers, both by Opus. It's over in 80 frames, so pause early.
Thornrush lands 3 damage; Thornbull lands 5 and wins.

```bash
dotnet run --project MTile.Desktop -- --fight Fights/competition/wave3_with_hollowstep/showcase/04_Thornbull_vs_Thornrush_flat_s100.fight.json
```

## 5. Twinbarrel vs Lancer, flat, seed 100: draw (0-16-0)

A full 30-second timeout. Twinbarrel deals 4 damage and takes none. Even so, a timeout
is a draw (0.4 each), whoever is ahead on health. All 16 seeds of this pairing look
like this.

```bash
dotnet run --project MTile.Desktop -- --fight Fights/competition/wave3_with_hollowstep/showcase/05_Twinbarrel_vs_Lancer_flat_s100.fight.json
```

## 6. Phalanx vs Ironmaw, hills, seed 100: Ironmaw wins (6-0-10)

A shooter duel on uneven ground, and the spawn side matters here. With Phalanx on the
left, Ironmaw wins 10 of 16. With Ironmaw on the left, it loses all 16.

```bash
dotnet run --project MTile.Desktop -- --fight Fights/competition/wave3_with_hollowstep/showcase/06_Phalanx_vs_Ironmaw_hills_s100.fight.json
```
