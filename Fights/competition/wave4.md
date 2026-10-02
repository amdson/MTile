# Fighter competition — wave 4

4 submissions scored one-versus-pool against Dev (18) and Holdout (11); 1800 frames per bout, terrains flat, corridor, hills, seeds 100..115. Pool from: Fights/wave3_full_league.md, cut 2026-10-02.

Score: win 1, loss 0, draw 0.4, over bouts played (both spawn orders). Ranked on the holdout score, then the head-to-head score, then name. `gap` = dev − holdout (overfit to the visible field). `±se` is the standard error over seeds. `rating` is a Bradley–Terry fit on the Elo scale over every bout played (the scoring leagues, the head-to-head and a league among the pool), with the pool's mean at 1500.

## Ranking

```
rank  name      author     holdout    ±se     dev    ±se     gap     h2h  rating   W-D-L hold    W-D-L dev
   1  Gritmaul  sonnet-w4    0.989  0.004   0.992  0.002  +0.003   0.342    2402    1044-1-11    1713-2-13
   2  Bullrush  sonnet-w4    0.969  0.008   0.995  0.002  +0.026   0.217    2339    1022-2-32     1715-9-4
   3  Ironhide  sonnet-w4    0.963  0.009   0.990  0.001  +0.027   0.821    2546    1017-1-38    1710-2-16
   4  Ironwing  sonnet-w4    0.932  0.016   0.987  0.003  +0.055   0.424    2358     984-0-72    1704-3-21
```

## Head-to-head ladder

Submissions only; the tiebreak after the holdout score.

```
rank  name      author      score    ±se      min-max      W-D-L   margin  paid reads/frame
   1  Ironhide  sonnet-w4   0.821  0.008  0.778-0.900   206-76-6   +0.237             0.932
   2  Ironwing  sonnet-w4   0.424  0.015  0.322-0.533  33-223-32   +0.012             0.895
   3  Gritmaul  sonnet-w4   0.342  0.021  0.256-0.500  42-141-105   -0.134             0.913
   4  Bullrush  sonnet-w4   0.217  0.021  0.056-0.344  13-124-151   -0.115             0.923
```

## Head-to-head matrices

```
[flat] rows spawn left, columns spawn right; row W-D-L over 16 seeds, mean row margin
                    Bullrush      Gritmaul      Ironhide      Ironwing
      Bullrush             -  0-10-6 -0.04  0-0-16 -0.26  0-12-4 -0.06
      Gritmaul  6-10-0 +0.04             -  0-0-16 -0.43  0-16-0  0.00
      Ironhide  16-0-0 +0.31  16-0-0 +0.43             -  2-14-0 +0.03
      Ironwing  4-12-0 +0.06  0-16-0  0.00  0-15-1 -0.01             -
```

```
[corridor] rows spawn left, columns spawn right; row W-D-L over 16 seeds, mean row margin
                    Bullrush      Gritmaul      Ironhide      Ironwing
      Bullrush             -  0-10-6 -0.04  0-0-16 -0.26  0-12-4 -0.06
      Gritmaul  6-10-0 +0.04             -  0-0-16 -0.43  0-16-0  0.00
      Ironhide  16-0-0 +0.31  16-0-0 +0.43             -  2-14-0 +0.03
      Ironwing  4-12-0 +0.06  0-16-0  0.00  0-15-1 -0.01             -
```

```
[hills] rows spawn left, columns spawn right; row W-D-L over 16 seeds, mean row margin
                    Bullrush      Gritmaul      Ironhide      Ironwing
      Bullrush             -   2-7-7 -0.02  0-0-16 -0.21   5-8-3 -0.03
      Gritmaul  2-12-2 -0.02             -  0-0-16 -0.39   5-8-3 -0.04
      Ironhide  16-0-0 +0.26  16-0-0 +0.43             -   7-9-0 +0.10
      Ironwing   3-9-4 +0.03  2-10-4 +0.02   6-9-1 +0.04             -
```

## By author

Mean over the author's bots.

```
author     bots  rating  holdout  names
sonnet-w4     4    2411    0.963  Gritmaul, Bullrush, Ironhide, Ironwing
```

## Ratings

Every participant, pool and submissions, Elo scale, pool mean 1500.

```
Ironhide          2546  submission
Gritmaul          2402  submission
Ironwing          2358  submission
Bullrush          2339  submission
Thornbull         2085
Thornrush         1973
Hollowstep        1929
Thornback         1840
Ironmaw           1824
Twinbarrel        1792
Stonecreep        1785
Phalanx           1779
Arrowhawk         1778
Lancer            1770
Ironshot          1705
Harrier           1687
Mason             1536
Sentinel          1429
Vanguard          1396
Wraith            1388
Breacher          1378
Brick             1355
Sapper            1298
Gunner            1285
Skirmish          1279
Bulwark           1275
Gravemantle       1275
Turret            1242
Builder           1209
Kestrel           1166
Flyer             1101
ExampleBrawler    1051
Sprinter           895
```

## Pool

Dev (visible): Thornbull, Ironmaw, Twinbarrel, Phalanx, Arrowhawk, Harrier, Mason, Breacher, Wraith, Brick, Bulwark, Turret, Gunner, Gravemantle, Builder, Flyer, ExampleBrawler, Sprinter

Holdout (hidden): Thornrush, Hollowstep, Thornback, Stonecreep, Lancer, Ironshot, Sentinel, Vanguard, Sapper, Skirmish, Kestrel
