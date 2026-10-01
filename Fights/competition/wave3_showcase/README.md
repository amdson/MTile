# Wave 3: five fights to watch

All use scoring seed **100**, a **1800-frame** cap, and the unchanged scored bots.
Recorded at commit `a74e31f`. Listed fighter order is left spawn versus right spawn.
At 60 fps, these fights range from about 2 to 23 seconds.

Run commands from the repository root. **F6** pauses; **F7** advances one frame.
The first fight is especially short, so pause early to inspect it.

## 1. Gravemantle vs Twinbarrel — flat

Champion versus runner-up: inspect the enormous contact field and rapid repeated hits.

[Replay file](01_Gravemantle_vs_Twinbarrel_flat.fight.json)

```bash
dotnet run --project MTile.Desktop -- --fight Fights/competition/wave3_showcase/01_Gravemantle_vs_Twinbarrel_flat.fight.json
```

## 2. Twinbarrel vs Thornbull — corridor

Ranged pressure versus the strongest rammer, under a low ceiling.

[Replay file](02_Twinbarrel_vs_Thornbull_corridor.fight.json)

```bash
dotnet run --project MTile.Desktop -- --fight Fights/competition/wave3_showcase/02_Twinbarrel_vs_Thornbull_corridor.fight.json
```

## 3. Thornrush vs Twinbarrel — hills

A longer fight showing jumps, pursuit, and terrain obstruction.

[Replay file](03_Thornrush_vs_Twinbarrel_hills.fight.json)

```bash
dotnet run --project MTile.Desktop -- --fight Fights/competition/wave3_showcase/03_Thornrush_vs_Twinbarrel_hills.fight.json
```

## 4. Thornbull vs Thornback — hills

A close exchange between a heavy contact hit and a durable shooter.

[Replay file](04_Thornbull_vs_Thornback_hills.fight.json)

```bash
dotnet run --project MTile.Desktop -- --fight Fights/competition/wave3_showcase/04_Thornbull_vs_Thornback_hills.fight.json
```

## 5. Ironmaw vs Phalanx — hills

A submission against a held-out opponent: inspect the hill approach and firing angles.

[Replay file](05_Ironmaw_vs_Phalanx_hills.fight.json)

```bash
dotnet run --project MTile.Desktop -- --fight Fights/competition/wave3_showcase/05_Ironmaw_vs_Phalanx_hills.fight.json
```

<details>
<summary>Recorded outcomes (spoilers)</summary>

| Fight | Winner | Duration | Health left (left / right) |
|---|---|---|---|
| 1 | Gravemantle | 1.70 s | 6.00 / 0.00 |
| 2 | Twinbarrel | 3.27 s | 6.00 / 0.00 |
| 3 | Twinbarrel | 22.52 s | 0.00 / 5.00 |
| 4 | Thornback | 4.68 s | 0.00 / 2.00 |
| 5 | Phalanx | 6.67 s | 0.00 / 6.00 |

</details>
