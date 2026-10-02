# Fighter competition — wave 4

8 submissions scored one-versus-pool against Dev (18) and Holdout (11); 1800 frames per bout, terrains flat, corridor, hills, seeds 100..115. Pool from: Fights/wave3_full_league.md, cut 2026-10-02.

Score: win 1, loss 0, draw 0.4, over bouts played (both spawn orders). Ranked on the holdout score, then the head-to-head score, then name. `gap` = dev − holdout (overfit to the visible field). `±se` is the standard error over seeds. `rating` is a Bradley–Terry fit on the Elo scale over every bout played (the scoring leagues, the head-to-head and a league among the pool), with the pool's mean at 1500.

## Ranking

```
rank  name         author     holdout    ±se     dev    ±se     gap     h2h  rating   W-D-L hold    W-D-L dev
   1  Brambleram   opus-w4      1.000  0.000   0.998  0.001  -0.002   0.590    2715     1056-0-0     1721-7-0
   2  Bramblehorn  opus-w4      0.999  0.001   0.999  0.001  -0.001   0.673    2786     1055-1-0     1725-2-1
   3  Gravemarch   codex-w4     0.994  0.003   0.996  0.002  +0.002   0.960    3120     1048-3-5     1717-9-2
   4  Gritmaul     sonnet-w4    0.989  0.004   0.992  0.002  +0.003   0.150    2355    1044-1-11    1713-2-13
   5  Cinderhoof   opus-w4      0.988  0.004   0.987  0.003   0.000   0.716    2779    1043-0-13   1702-10-16
   6  Bullrush     sonnet-w4    0.969  0.008   0.995  0.002  +0.026   0.100    2305    1022-2-32     1715-9-4
   7  Ironhide     sonnet-w4    0.963  0.009   0.990  0.001  +0.027   0.485    2564    1017-1-38    1710-2-16
   8  Ironwing     sonnet-w4    0.932  0.016   0.987  0.003  +0.055   0.190    2321     984-0-72    1704-3-21
```

## Head-to-head ladder

Submissions only; the tiebreak after the holdout score.

```
rank  name         author      score    ±se      min-max      W-D-L   margin  paid reads/frame
   1  Gravemarch   codex-w4    0.960  0.011  0.862-1.000  638-18-16   +0.631             0.301
   2  Cinderhoof   opus-w4     0.716  0.016  0.633-0.886  457-60-155   +0.298             0.860
   3  Bramblehorn  opus-w4     0.673  0.019  0.538-0.805  419-83-170   +0.317             0.802
   4  Brambleram   opus-w4     0.590  0.008  0.529-0.633  346-126-200   +0.013             0.986
   5  Ironhide     sonnet-w4   0.485  0.011  0.443-0.590  272-134-266   -0.087             0.933
   6  Ironwing     sonnet-w4   0.190  0.007  0.138-0.238  38-224-410   -0.289             0.914
   7  Gritmaul     sonnet-w4   0.150  0.010  0.110-0.238  42-147-483   -0.463             0.889
   8  Bullrush     sonnet-w4   0.100  0.010  0.024-0.157  16-128-528   -0.420             0.934
```

## Head-to-head matrices

```
[flat] rows spawn left, columns spawn right; row W-D-L over 16 seeds, mean row margin
                 Bramblehorn    Brambleram      Bullrush    Cinderhoof    Gravemarch      Gritmaul      Ironhide      Ironwing
   Bramblehorn             -  0-5-11 -0.69  16-0-0 +0.88  13-0-3 +0.63  3-0-13 -0.16  15-1-0 +0.85  15-0-1 +0.88  16-0-0 +0.94
    Brambleram  2-14-0 +0.13             -  16-0-0 +0.70  0-11-5 -0.31  0-0-16 -0.88  16-0-0 +0.67  0-1-15 -0.94  16-0-0 +0.04
      Bullrush  1-1-14 -0.69  0-0-16 -0.76             -  0-0-16 -0.56  0-0-16 -0.85  0-10-6 -0.04  0-0-16 -0.26  0-12-4 -0.06
    Cinderhoof  2-0-14 -0.75   5-8-3 +0.13  16-0-0 +0.56             -  0-0-16 -0.01  16-0-0 +0.70  16-0-0 +0.38  16-0-0 +0.46
    Gravemarch  14-1-1 +0.25  16-0-0 +0.65  16-0-0 +0.93  14-2-0 +0.01             -  16-0-0 +0.89  16-0-0 +0.92  16-0-0 +0.93
      Gritmaul  0-2-14 -0.74  0-0-16 -0.72  6-10-0 +0.04  0-0-16 -0.70  0-0-16 -0.89             -  0-0-16 -0.43  0-16-0  0.00
      Ironhide  0-0-16 -1.00  12-3-1 +0.69  16-0-0 +0.31  1-1-14 -0.32  0-0-16 -0.92  16-0-0 +0.43             -  2-14-0 +0.03
      Ironwing  2-0-14 -0.69  0-0-16 -0.04  4-12-0 +0.06  0-0-16 -0.46  0-0-16 -0.93  0-16-0  0.00  0-15-1 -0.01             -
```

```
[corridor] rows spawn left, columns spawn right; row W-D-L over 16 seeds, mean row margin
                 Bramblehorn    Brambleram      Bullrush    Cinderhoof    Gravemarch      Gritmaul      Ironhide      Ironwing
   Bramblehorn             -  0-5-11 -0.69  16-0-0 +0.88  13-0-3 +0.63  3-0-13 -0.16  15-1-0 +0.85  15-0-1 +0.88  16-0-0 +0.94
    Brambleram  2-14-0 +0.13             -  16-0-0 +0.70   1-7-8 -0.44  0-0-16 -0.88  16-0-0 +0.67  0-5-11 -0.69  16-0-0 +0.04
      Bullrush  1-1-14 -0.69  0-0-16 -0.76             -  0-0-16 -0.56  0-0-16 -0.85  0-10-6 -0.04  0-0-16 -0.26  0-12-4 -0.06
    Cinderhoof  2-0-14 -0.75   4-9-3 +0.06  16-0-0 +0.56             -  0-0-16 -0.01  16-0-0 +0.70  16-0-0 +0.38  16-0-0 +0.46
    Gravemarch  14-1-1 +0.20  16-0-0 +0.65  16-0-0 +0.93  14-2-0 +0.01             -  16-0-0 +0.89  16-0-0 +0.92  16-0-0 +0.93
      Gritmaul  0-2-14 -0.74  0-0-16 -0.72  6-10-0 +0.04  0-0-16 -0.70  0-0-16 -0.89             -  0-0-16 -0.43  0-16-0  0.00
      Ironhide  0-0-16 -1.00   6-9-1 +0.31  16-0-0 +0.31  1-1-14 -0.32  0-0-16 -0.92  16-0-0 +0.43             -  2-14-0 +0.03
      Ironwing  2-0-14 -0.69  0-0-16 -0.10  4-12-0 +0.06  0-0-16 -0.46  0-0-16 -0.93  0-16-0  0.00  0-15-1 -0.01             -
```

```
[hills] rows spawn left, columns spawn right; row W-D-L over 16 seeds, mean row margin
                 Bramblehorn    Brambleram      Bullrush    Cinderhoof    Gravemarch      Gritmaul      Ironhide      Ironwing
   Bramblehorn             -   3-8-5 -0.13  16-0-0 +0.11  0-3-13 -0.81  1-0-15 -0.77  16-0-0 +0.31  0-10-6 -0.38  16-0-0 +0.07
    Brambleram   5-4-7 -0.13             -  15-0-1 +0.02  1-3-12 -0.69  0-0-16 -0.82  16-0-0 +0.29  0-10-6 -0.38  16-0-0 +0.10
      Bullrush  0-0-16 -0.70  0-0-16 -0.45             -  0-0-16 -0.45  0-0-16 -0.96   2-7-7 -0.02  0-0-16 -0.21   5-8-3 -0.03
    Cinderhoof  12-1-3 +0.56   5-4-7 -0.13  16-0-0 +0.51             -  0-3-13 -0.01  16-0-0 +0.66  16-0-0 +0.38  16-0-0 +0.46
    Gravemarch  15-0-1 +0.67  15-0-1 +0.46  14-2-0 +0.47   8-4-4 -0.25             -  16-0-0 +0.58  14-2-0 +0.47  14-1-1 +0.45
      Gritmaul  0-0-16 -0.77  0-0-16 -0.58  2-12-2 -0.02  0-0-16 -0.58  0-0-16 -0.94             -  0-0-16 -0.39   5-8-3 -0.04
      Ironhide   1-9-6 -0.31   5-6-5  0.00  16-0-0 +0.26  0-1-15 -0.38  0-0-16 -0.92  16-0-0 +0.43             -   7-9-0 +0.10
      Ironwing  0-0-16 -0.53  0-0-16 -0.40   3-9-4 +0.03  0-0-16 -0.46  0-0-16 -0.86  2-10-4 +0.02   6-9-1 +0.04             -
```

## By author

Mean over the author's bots.

```
author     bots  rating  holdout  names
codex-w4      1    3120    0.994  Gravemarch
opus-w4       3    2760    0.996  Brambleram, Bramblehorn, Cinderhoof
sonnet-w4     4    2386    0.963  Gritmaul, Bullrush, Ironhide, Ironwing
```

## Ratings

Every participant, pool and submissions, Elo scale, pool mean 1500.

```
Gravemarch        3120  submission
Bramblehorn       2786  submission
Cinderhoof        2779  submission
Brambleram        2715  submission
Ironhide          2564  submission
Gritmaul          2355  submission
Ironwing          2321  submission
Bullrush          2305  submission
Thornbull         2074
Thornrush         1967
Hollowstep        1924
Thornback         1843
Ironmaw           1821
Twinbarrel        1791
Stonecreep        1783
Arrowhawk         1780
Phalanx           1779
Lancer            1769
Ironshot          1706
Harrier           1686
Mason             1537
Sentinel          1430
Vanguard          1397
Wraith            1389
Breacher          1378
Brick             1356
Sapper            1299
Gunner            1287
Skirmish          1280
Gravemantle       1279
Bulwark           1276
Turret            1243
Builder           1211
Kestrel           1167
Flyer             1102
ExampleBrawler    1053
Sprinter           896
```

## Pool

Dev (visible): Thornbull, Ironmaw, Twinbarrel, Phalanx, Arrowhawk, Harrier, Mason, Breacher, Wraith, Brick, Bulwark, Turret, Gunner, Gravemantle, Builder, Flyer, ExampleBrawler, Sprinter

Holdout (hidden): Thornrush, Hollowstep, Thornback, Stonecreep, Lancer, Ironshot, Sentinel, Vanguard, Sapper, Skirmish, Kestrel
