# Fighter competition — wave 3

9 submissions scored one-versus-pool against Dev (13) and Holdout (6); 1800 frames per bout, terrains flat, corridor, hills, seeds 100..115. Pool from: Fights/wave2_long_league.md, cut 2026-10-01.

Score: win 1, loss 0, draw 0.4, over bouts played (both spawn orders). Ranked on the holdout score, then the head-to-head score, then name. `gap` = dev − holdout (overfit to the visible field). `±se` is the standard error over seeds. `rating` is a Bradley–Terry fit on the Elo scale over every bout played (the scoring leagues, the head-to-head and a league among the pool), with the pool's mean at 1500.

## Ranking

```
rank  name         author     holdout    ±se     dev    ±se     gap     h2h  rating   W-D-L hold    W-D-L dev
   1  Gravemantle  codex-w3     1.000  0.000   1.000  0.000   0.000   1.000    3108      576-0-0     1248-0-0
   2  Twinbarrel   fable-w3     0.992  0.003   0.993  0.003  +0.001   0.803    2517      568-8-0    1233-15-0
   3  Thornbull    opus-w3      0.985  0.006   0.993  0.002  +0.008   0.674    2349      567-1-8     1239-0-9
   4  Thornrush    opus-w3      0.977  0.008   0.979  0.005  +0.002   0.487    2182     563-0-13   1217-13-18
   5  Ironmaw      fable-w3     0.849  0.016   0.967  0.005  +0.118   0.258    1993     489-0-87    1203-8-37
   6  Thornback    sonnet-w3    0.704  0.005   0.934  0.004  +0.229   0.344    1997    292-284-0   1110-138-0
   7  Stonecreep   sonnet-w3    0.699  0.001   0.925  0.005  +0.227   0.266    1956    287-289-0   1093-155-0
   8  Ironshot     haiku-w3     0.693  0.004   0.881  0.008  +0.188   0.206    1899    285-286-5  1015-211-22
   9  Arrowhawk    haiku-w3     0.651  0.015   0.862  0.010  +0.211   0.297    1909   257-295-24  1010-164-74
```

## Head-to-head ladder

Submissions only; the tiebreak after the holdout score.

```
rank  name         author      score    ±se      min-max      W-D-L   margin  paid reads/frame
   1  Gravemantle  codex-w3    1.000  0.000  1.000-1.000    768-0-0   +0.920             0.245
   2  Twinbarrel   fable-w3    0.803  0.009  0.738-0.863  613-9-146   +0.621             0.550
   3  Thornbull    opus-w3     0.674  0.017  0.563-0.771  518-0-250   +0.084             0.980
   4  Thornrush    opus-w3     0.487  0.015  0.383-0.596  355-47-366   +0.019             0.311
   5  Thornback    sonnet-w3   0.344  0.015  0.254-0.421  148-290-330   -0.177             0.333
   6  Arrowhawk    haiku-w3    0.297  0.020  0.171-0.421  112-290-366   -0.302             0.594
   7  Stonecreep   sonnet-w3   0.266  0.009  0.171-0.313  87-294-387   -0.317             0.333
   8  Ironmaw      fable-w3    0.258  0.030  0.029-0.429  179-48-541   -0.428             0.450
   9  Ironshot     haiku-w3    0.206  0.012  0.171-0.317  42-290-436   -0.420             0.174
```

## Head-to-head matrices

```
[flat] rows spawn left, columns spawn right; row W-D-L over 16 seeds, mean row margin
                   Arrowhawk   Gravemantle       Ironmaw      Ironshot    Stonecreep     Thornback     Thornbull     Thornrush    Twinbarrel
     Arrowhawk             -  0-0-16 -0.99  10-0-6 +0.07  0-16-0  0.00  0-16-0  0.00  0-16-0  0.00  0-0-16 -0.61  5-0-11 -0.50  0-0-16 -1.00
   Gravemantle  16-0-0 +0.99             -  16-0-0 +1.00  16-0-0 +0.88  16-0-0 +0.86  16-0-0 +0.77  16-0-0 +1.00  16-0-0 +1.00  16-0-0 +0.86
       Ironmaw   7-0-9 -0.03  0-0-16 -1.00             -  13-1-2 +0.38  4-0-12 -0.19  11-0-5 -0.04  0-0-16 -0.62   0-9-7 -0.14  0-0-16 -1.00
      Ironshot  0-16-0  0.00  0-0-16 -0.88  6-0-10 -0.15             -  0-16-0  0.00  0-16-0  0.00  0-0-16 -0.61  0-0-16 -1.00  0-0-16 -1.00
    Stonecreep  0-16-0  0.00  0-0-16 -0.86  15-0-1 +0.35  0-16-0  0.00             -  0-16-0  0.00  0-0-16 -0.37  0-0-16 -0.93  0-0-16 -0.99
     Thornback  0-16-0  0.00  0-0-16 -0.77  10-0-6 +0.39  0-16-0  0.00  0-16-0  0.00             -   7-0-9 -0.01   7-0-9 -0.21  0-0-16 -0.98
     Thornbull  16-0-0 +0.63  0-0-16 -1.00  16-0-0 +0.62  16-0-0 +0.61  16-0-0 +0.33  10-0-6 +0.03             -  16-0-0 +0.63  0-0-16 -1.00
     Thornrush  12-0-4 +0.57  0-0-16 -1.00  5-11-0 +0.10  16-0-0 +1.00  16-0-0 +0.88  13-0-3 +0.14  0-0-16 -0.63             -  12-0-4 +0.25
    Twinbarrel  16-0-0 +1.00  0-0-16 -0.86  16-0-0 +1.00  16-0-0 +1.00  16-0-0 +0.99  16-0-0 +0.98  16-0-0 +1.00   9-0-7 -0.03             -
```

```
[corridor] rows spawn left, columns spawn right; row W-D-L over 16 seeds, mean row margin
                   Arrowhawk   Gravemantle       Ironmaw      Ironshot    Stonecreep     Thornback     Thornbull     Thornrush    Twinbarrel
     Arrowhawk             -  0-0-16 -0.99  10-0-6 +0.07  0-16-0  0.00  0-16-0  0.00  0-16-0  0.00  0-0-16 -0.61  16-0-0 +0.76  0-0-16 -1.00
   Gravemantle  16-0-0 +0.99             -  16-0-0 +1.00  16-0-0 +0.88  16-0-0 +0.86  16-0-0 +0.77  16-0-0 +1.00  16-0-0 +1.00  16-0-0 +0.86
       Ironmaw   7-0-9 -0.03  0-0-16 -1.00             -  13-1-2 +0.38  4-0-12 -0.19  11-0-5 -0.04  0-0-16 -0.62   0-9-7 -0.14  0-0-16 -1.00
      Ironshot  0-16-0  0.00  0-0-16 -0.88  6-0-10 -0.15             -  0-16-0  0.00  0-16-0  0.00  0-0-16 -0.61  0-0-16 -1.00  0-0-16 -1.00
    Stonecreep  0-16-0  0.00  0-0-16 -0.86  15-0-1 +0.35  0-16-0  0.00             -  0-16-0  0.00  0-0-16 -0.37  0-0-16 -0.94  0-0-16 -0.99
     Thornback  0-16-0  0.00  0-0-16 -0.77  10-0-6 +0.39  0-16-0  0.00  0-16-0  0.00             -   7-0-9 -0.01   9-0-7 -0.22  0-0-16 -0.98
     Thornbull  16-0-0 +0.63  0-0-16 -1.00  16-0-0 +0.62  16-0-0 +0.61  16-0-0 +0.33  10-0-6 +0.03             -  16-0-0 +0.63  0-0-16 -1.00
     Thornrush  2-0-14 -0.53  0-0-16 -1.00  5-11-0 +0.10  16-0-0 +1.00  16-0-0 +0.83  3-0-13 -0.09  0-0-16 -0.63             -  0-0-16 -0.90
    Twinbarrel  16-0-0 +1.00  0-0-16 -0.86  16-0-0 +1.00  16-0-0 +1.00  16-0-0 +0.99  16-0-0 +0.98  16-0-0 +1.00   8-0-8 +0.09             -
```

```
[hills] rows spawn left, columns spawn right; row W-D-L over 16 seeds, mean row margin
                   Arrowhawk   Gravemantle       Ironmaw      Ironshot    Stonecreep     Thornback     Thornbull     Thornrush    Twinbarrel
     Arrowhawk             -  0-0-16 -0.99  4-0-12 -0.10  0-16-0  0.00  0-16-0  0.00  0-16-0  0.00  0-0-16 -0.61   7-0-9 -0.37  0-0-16 -0.95
   Gravemantle  16-0-0 +0.99             -  16-0-0 +1.00  16-0-0 +0.88  16-0-0 +0.86  16-0-0 +0.77  16-0-0 +1.00  16-0-0 +0.99  16-0-0 +0.86
       Ironmaw  5-0-11 -0.49  0-0-16 -1.00             -  0-0-16 -1.00  0-0-16 -1.00  0-0-16 -1.00  0-0-16 -0.71  3-3-10 -0.11  0-0-16 -1.00
      Ironshot  0-16-0  0.00  0-0-16 -0.87  4-0-12 -0.26             -  0-16-0  0.00  0-16-0  0.00  0-0-16 -0.61  1-0-15 -0.62  0-0-16 -1.00
    Stonecreep  0-16-0  0.00  0-0-16 -0.87   7-0-9 +0.04  0-16-0  0.00             -  0-16-0  0.00  0-0-16 -0.34  1-0-15 -0.51  0-0-16 -1.00
     Thornback  0-16-0  0.00  0-0-16 -0.79  3-0-13  0.00  0-16-0  0.00  0-16-0  0.00             -   7-0-9 +0.05   9-0-7  0.00  0-0-16 -1.00
     Thornbull   7-0-9 -0.34  0-0-16 -1.00  16-0-0 +0.83  14-0-2 +0.23   9-0-7  0.00  3-0-13 -0.47             -  16-0-0 +0.69  0-0-16 -1.00
     Thornrush  13-0-3 +0.59  0-0-16 -1.00   7-3-6 -0.04  15-0-1 +0.88  13-1-2 +0.64  5-0-11 -0.03  0-0-16 -0.73             -  1-0-15 -0.78
    Twinbarrel  13-2-1 +0.74  0-0-16 -0.92  13-0-3 +0.68  14-0-2 +0.81  11-5-0 +0.77  13-2-1 +0.78  10-0-6 +0.25   7-0-9 -0.36             -
```

## By author

Mean over the author's bots.

```
author     bots  rating  holdout  names
codex-w3      1    3108    1.000  Gravemantle
opus-w3       2    2265    0.981  Thornbull, Thornrush
fable-w3      2    2255    0.920  Twinbarrel, Ironmaw
sonnet-w3     2    1977    0.702  Thornback, Stonecreep
haiku-w3      2    1904    0.672  Ironshot, Arrowhawk
```

## Ratings

Every participant, pool and submissions, Elo scale, pool mean 1500.

```
Gravemantle       3108  submission
Twinbarrel        2517  submission
Thornbull         2349  submission
Thornrush         2182  submission
Thornback         1997  submission
Ironmaw           1993  submission
Stonecreep        1956  submission
Lancer            1932
Phalanx           1932
Arrowhawk         1909  submission
Ironshot          1899  submission
Harrier           1858
Mason             1705
Vanguard          1568
Sentinel          1554
Kestrel           1539
Wraith            1519
Breacher          1510
Brick             1509
Sapper            1463
Gunner            1437
Skirmish          1403
Bulwark           1403
Turret            1382
Builder           1375
Flyer             1244
ExampleBrawler    1166
Sprinter          1000
```

## Pool

Dev (visible): Lancer, Kestrel, Vanguard, Breacher, Wraith, Brick, Bulwark, Turret, Builder, Gunner, Flyer, ExampleBrawler, Sprinter

Holdout (hidden): Phalanx, Harrier, Sentinel, Mason, Sapper, Skirmish
