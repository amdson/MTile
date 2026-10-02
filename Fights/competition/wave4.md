# Fighter competition — wave 4

7 submissions scored one-versus-pool against Dev (18) and Holdout (11); 1800 frames per bout, terrains flat, corridor, hills, seeds 100..115. Pool from: Fights/wave3_full_league.md, cut 2026-10-02.

Score: win 1, loss 0, draw 0.4, over bouts played (both spawn orders). Ranked on the holdout score, then the head-to-head score, then name. `gap` = dev − holdout (overfit to the visible field). `±se` is the standard error over seeds. `rating` is a Bradley–Terry fit on the Elo scale over every bout played (the scoring leagues, the head-to-head and a league among the pool), with the pool's mean at 1500.

## Ranking

```
rank  name         author     holdout    ±se     dev    ±se     gap     h2h  rating   W-D-L hold    W-D-L dev
   1  Brambleram   opus-w4      1.000  0.000   0.998  0.001  -0.002   0.686    2747     1056-0-0     1721-7-0
   2  Bramblehorn  opus-w4      0.999  0.001   0.999  0.001  -0.001   0.766    2811     1055-1-0     1725-2-1
   3  Gritmaul     sonnet-w4    0.989  0.004   0.992  0.002  +0.003   0.175    2366    1044-1-11    1713-2-13
   4  Cinderhoof   opus-w4      0.988  0.004   0.987  0.003   0.000   0.820    2805    1043-0-13   1702-10-16
   5  Bullrush     sonnet-w4    0.969  0.008   0.995  0.002  +0.026   0.115    2313    1022-2-32     1715-9-4
   6  Ironhide     sonnet-w4    0.963  0.009   0.990  0.001  +0.027   0.564    2584    1017-1-38    1710-2-16
   7  Ironwing     sonnet-w4    0.932  0.016   0.987  0.003  +0.055   0.219    2329     984-0-72    1704-3-21
```

## Head-to-head ladder

Submissions only; the tiebreak after the holdout score.

```
rank  name         author      score    ±se      min-max      W-D-L   margin  paid reads/frame
   1  Cinderhoof   opus-w4     0.820  0.017  0.739-1.000  453-49-74   +0.342             0.873
   2  Bramblehorn  opus-w4     0.766  0.017  0.628-0.861  409-81-86   +0.431             0.827
   3  Brambleram   opus-w4     0.686  0.009  0.617-0.739  345-126-105   +0.136             0.995
   4  Ironhide     sonnet-w4   0.564  0.012  0.517-0.689  272-132-172   +0.039             0.949
   5  Ironwing     sonnet-w4   0.219  0.007  0.161-0.267  37-223-316   -0.198             0.926
   6  Gritmaul     sonnet-w4   0.175  0.012  0.128-0.278  42-147-387   -0.399             0.897
   7  Bullrush     sonnet-w4   0.115  0.011  0.028-0.183  16-126-434   -0.351             0.950
```

## Head-to-head matrices

```
[flat] rows spawn left, columns spawn right; row W-D-L over 16 seeds, mean row margin
                 Bramblehorn    Brambleram      Bullrush    Cinderhoof      Gritmaul      Ironhide      Ironwing
   Bramblehorn             -  0-5-11 -0.69  16-0-0 +0.88  13-0-3 +0.63  15-1-0 +0.85  15-0-1 +0.88  16-0-0 +0.94
    Brambleram  2-14-0 +0.13             -  16-0-0 +0.70  0-11-5 -0.31  16-0-0 +0.67  0-1-15 -0.94  16-0-0 +0.04
      Bullrush  1-1-14 -0.69  0-0-16 -0.76             -  0-0-16 -0.56  0-10-6 -0.04  0-0-16 -0.26  0-12-4 -0.06
    Cinderhoof  2-0-14 -0.75   5-8-3 +0.13  16-0-0 +0.56             -  16-0-0 +0.70  16-0-0 +0.38  16-0-0 +0.46
      Gritmaul  0-2-14 -0.74  0-0-16 -0.72  6-10-0 +0.04  0-0-16 -0.70             -  0-0-16 -0.43  0-16-0  0.00
      Ironhide  0-0-16 -1.00  12-3-1 +0.69  16-0-0 +0.31  1-1-14 -0.32  16-0-0 +0.43             -  2-14-0 +0.03
      Ironwing  2-0-14 -0.69  0-0-16 -0.04  4-12-0 +0.06  0-0-16 -0.46  0-16-0  0.00  0-15-1 -0.01             -
```

```
[corridor] rows spawn left, columns spawn right; row W-D-L over 16 seeds, mean row margin
                 Bramblehorn    Brambleram      Bullrush    Cinderhoof      Gritmaul      Ironhide      Ironwing
   Bramblehorn             -  0-5-11 -0.69  16-0-0 +0.88  13-0-3 +0.63  15-1-0 +0.85  15-0-1 +0.88  16-0-0 +0.94
    Brambleram  2-14-0 +0.13             -  16-0-0 +0.70   1-7-8 -0.44  16-0-0 +0.67  0-5-11 -0.69  16-0-0 +0.04
      Bullrush  1-1-14 -0.69  0-0-16 -0.76             -  0-0-16 -0.56  0-10-6 -0.04  0-0-16 -0.26  0-12-4 -0.06
    Cinderhoof  2-0-14 -0.75   4-9-3 +0.06  16-0-0 +0.56             -  16-0-0 +0.70  16-0-0 +0.38  16-0-0 +0.46
      Gritmaul  0-2-14 -0.74  0-0-16 -0.72  6-10-0 +0.04  0-0-16 -0.70             -  0-0-16 -0.43  0-16-0  0.00
      Ironhide  0-0-16 -1.00   6-9-1 +0.31  16-0-0 +0.31  1-1-14 -0.32  16-0-0 +0.43             -  2-14-0 +0.03
      Ironwing  2-0-14 -0.69  0-0-16 -0.10  4-12-0 +0.06  0-0-16 -0.46  0-16-0  0.00  0-15-1 -0.01             -
```

```
[hills] rows spawn left, columns spawn right; row W-D-L over 16 seeds, mean row margin
                 Bramblehorn    Brambleram      Bullrush    Cinderhoof      Gritmaul      Ironhide      Ironwing
   Bramblehorn             -   3-8-5 -0.13  16-0-0 +0.11  0-3-13 -0.81  16-0-0 +0.31  0-10-6 -0.38  16-0-0 +0.07
    Brambleram   5-4-7 -0.13             -  15-0-1 +0.02  1-3-12 -0.69  16-0-0 +0.29  0-10-6 -0.38  16-0-0 +0.10
      Bullrush  0-0-16 -0.70  0-0-16 -0.45             -  0-0-16 -0.45   2-7-7 -0.02  0-0-16 -0.21   5-8-3 -0.03
    Cinderhoof  12-1-3 +0.56   5-4-7 -0.13  16-0-0 +0.51             -  16-0-0 +0.66  16-0-0 +0.38  16-0-0 +0.46
      Gritmaul  0-0-16 -0.77  0-0-16 -0.58  2-12-2 -0.02  0-0-16 -0.58             -  0-0-16 -0.39   5-8-3 -0.04
      Ironhide   1-9-6 -0.31   5-6-5  0.00  16-0-0 +0.26  0-1-15 -0.38  16-0-0 +0.43             -   7-9-0 +0.10
      Ironwing  0-0-16 -0.53  0-0-16 -0.40   3-9-4 +0.03  0-0-16 -0.46  2-10-4 +0.02   6-9-1 +0.04             -
```

## By author

Mean over the author's bots.

```
author     bots  rating  holdout  names
opus-w4       3    2788    0.996  Brambleram, Bramblehorn, Cinderhoof
sonnet-w4     4    2398    0.963  Gritmaul, Bullrush, Ironhide, Ironwing
```

## Ratings

Every participant, pool and submissions, Elo scale, pool mean 1500.

```
Bramblehorn       2811  submission
Cinderhoof        2805  submission
Brambleram        2747  submission
Ironhide          2584  submission
Gritmaul          2366  submission
Ironwing          2329  submission
Bullrush          2313  submission
Thornbull         2076
Thornrush         1968
Hollowstep        1925
Thornback         1842
Ironmaw           1822
Twinbarrel        1792
Stonecreep        1783
Arrowhawk         1780
Phalanx           1778
Lancer            1769
Ironshot          1706
Harrier           1687
Mason             1537
Sentinel          1429
Vanguard          1397
Wraith            1389
Breacher          1378
Brick             1355
Sapper            1298
Gunner            1286
Skirmish          1280
Gravemantle       1277
Bulwark           1276
Turret            1242
Builder           1210
Kestrel           1167
Flyer             1102
ExampleBrawler    1052
Sprinter           896
```

## Pool

Dev (visible): Thornbull, Ironmaw, Twinbarrel, Phalanx, Arrowhawk, Harrier, Mason, Breacher, Wraith, Brick, Bulwark, Turret, Gunner, Gravemantle, Builder, Flyer, ExampleBrawler, Sprinter

Holdout (hidden): Thornrush, Hollowstep, Thornback, Stonecreep, Lancer, Ironshot, Sentinel, Vanguard, Sapper, Skirmish, Kestrel
