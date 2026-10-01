# Fighter competition — wave 3

9 submissions scored one-versus-pool against Dev (13) and Holdout (6); 1800 frames per bout, terrains flat, corridor, hills, seeds 100..115. Pool from: Fights/wave2_long_league.md, cut 2026-10-01.

Score: win 1, loss 0, draw 0.4, over bouts played (both spawn orders). Ranked on the holdout score, then the head-to-head score, then name. `gap` = dev − holdout (overfit to the visible field). `±se` is the standard error over seeds. `rating` is a Bradley–Terry fit on the Elo scale over every bout played (the scoring leagues, the head-to-head and a league among the pool), with the pool's mean at 1500.

## Ranking

```
rank  name         author     holdout    ±se     dev    ±se     gap     h2h  rating   W-D-L hold    W-D-L dev
   1  Thornrush    opus-w3      0.983  0.005   0.979  0.005  -0.003   0.686    2179     566-0-10   1218-11-19
   2  Thornbull    opus-w3      0.980  0.006   0.986  0.005  +0.007   0.907    2367     564-1-11    1231-0-17
   3  Ironmaw      fable-w3     0.850  0.017   0.975  0.004  +0.126   0.476    2011     489-1-86    1215-5-28
   4  Twinbarrel   fable-w3     0.842  0.015   0.941  0.004  +0.099   0.376    1969    430-138-8   1126-121-1
   5  Thornback    sonnet-w3    0.704  0.005   0.934  0.004  +0.229   0.463    1971    292-284-0   1110-138-0
   6  Stonecreep   sonnet-w3    0.699  0.001   0.925  0.005  +0.227   0.424    1950    287-289-0   1093-155-0
   7  Ironshot     haiku-w3     0.693  0.004   0.881  0.008  +0.188   0.307    1872    285-286-5  1015-211-22
   8  Arrowhawk    haiku-w3     0.651  0.015   0.862  0.010  +0.211   0.452    1904   257-295-24  1010-164-74
   9  Gravemantle  codex-w3     0.214  0.013   0.345  0.004  +0.131   0.115    1423    0-308-268  10-1050-188
```

## Head-to-head ladder

Submissions only; the tiebreak after the holdout score.

```
rank  name         author      score    ±se      min-max      W-D-L   margin  paid reads/frame
   1  Thornbull    opus-w3     0.907  0.019  0.750-1.000   696-1-71   +0.489             0.978
   2  Thornrush    opus-w3     0.686  0.012  0.613-0.788  497-74-197   +0.386             0.315
   3  Ironmaw      fable-w3    0.476  0.022  0.279-0.613  337-71-360   -0.082             0.417
   4  Thornback    sonnet-w3   0.463  0.016  0.371-0.579  188-418-162   +0.073             0.333
   5  Arrowhawk    haiku-w3    0.452  0.017  0.321-0.529  188-397-183   +0.101             0.596
   6  Stonecreep   sonnet-w3   0.424  0.011  0.342-0.492  158-420-190   -0.046             0.333
   7  Twinbarrel   fable-w3    0.376  0.006  0.329-0.413  162-317-289   +0.066             0.584
   8  Ironshot     haiku-w3    0.307  0.012  0.258-0.425  98-345-325   -0.198             0.172
   9  Gravemantle  codex-w3    0.115  0.008  0.067-0.158  0-221-547   -0.790             0.250
```

## Head-to-head matrices

```
[flat] rows spawn left, columns spawn right; row W-D-L over 16 seeds, mean row margin
                   Arrowhawk   Gravemantle       Ironmaw      Ironshot    Stonecreep     Thornback     Thornbull     Thornrush    Twinbarrel
     Arrowhawk             -  0-16-0 +0.52  11-0-5 +0.08  0-16-0  0.00  0-16-0  0.00  0-16-0  0.00  0-0-16 -0.58  4-0-12 -0.57  4-12-0 +0.62
   Gravemantle  0-0-16 -1.00             -  0-0-16 -0.78  0-0-16 -1.00  0-0-16 -1.00  0-0-16 -1.00  0-0-16 -1.00  0-0-16 -0.86  0-0-16 -1.00
       Ironmaw  6-0-10 -0.03   9-7-0 +0.46             -  15-0-1 +0.42  3-0-13 -0.22  13-0-3 +0.02  0-0-16 -0.71  0-11-5 -0.10  16-0-0 +0.41
      Ironshot  0-16-0  0.00  3-13-0 +0.53  6-0-10 -0.15             -  0-16-0  0.00  0-16-0  0.00  0-0-16 -0.52  0-0-16 -0.99  0-1-15 -0.98
    Stonecreep  0-16-0  0.00  0-16-0 +0.37  14-0-2 +0.31  0-16-0  0.00             -  0-16-0  0.00  3-0-13 -0.22  0-0-16 -0.95  0-16-0 -0.43
     Thornback  0-16-0  0.00  0-16-0 +0.57  6-0-10 +0.18  0-16-0  0.00  0-16-0  0.00             -   8-0-8 +0.06  5-0-11 -0.37  1-15-0 -0.38
     Thornbull  16-0-0 +0.59  16-0-0 +0.99  16-0-0 +0.62  16-0-0 +0.53  16-0-0 +0.40   8-0-8 -0.06             -  16-0-0 +0.59  16-0-0 +0.56
     Thornrush  15-0-1 +0.83   8-8-0 +0.77  4-12-0 +0.08  16-0-0 +1.00  16-0-0 +0.89  10-0-6 +0.08  0-0-16 -0.63             -  16-0-0 +0.86
    Twinbarrel  0-13-3 -0.50  0-16-0 +0.36  2-0-14 -0.23  12-4-0 +0.90  0-15-1 +0.40  0-15-1 +0.38  0-0-16 -0.54  0-0-16 -0.95             -
```

```
[corridor] rows spawn left, columns spawn right; row W-D-L over 16 seeds, mean row margin
                   Arrowhawk   Gravemantle       Ironmaw      Ironshot    Stonecreep     Thornback     Thornbull     Thornrush    Twinbarrel
     Arrowhawk             -  0-16-0 +0.52  11-0-5 +0.08  0-16-0  0.00  0-16-0  0.00  0-16-0  0.00  0-0-16 -0.56  16-0-0 +0.76  4-12-0 +0.62
   Gravemantle  0-0-16 -1.00             -  0-0-16 -0.78  0-0-16 -1.00  0-0-16 -1.00  0-0-16 -1.00  0-0-16 -1.00  0-0-16 -0.86  0-0-16 -1.00
       Ironmaw  6-0-10 -0.03   9-7-0 +0.46             -  15-0-1 +0.42  3-0-13 -0.22  13-0-3 +0.02  0-0-16 -0.71  0-11-5 -0.10  16-0-0 +0.41
      Ironshot  0-16-0  0.00  3-13-0 +0.53  6-0-10 -0.15             -  0-16-0  0.00  0-16-0  0.00  0-0-16 -0.53  0-0-16 -1.00  0-1-15 -0.98
    Stonecreep  0-16-0  0.00  0-16-0 +0.37  14-0-2 +0.31  0-16-0  0.00             -  0-16-0  0.00  1-0-15 -0.31  0-0-16 -0.95  0-16-0 -0.43
     Thornback  0-16-0  0.00  0-16-0 +0.57  6-0-10 +0.18  0-16-0  0.00  0-16-0  0.00             -  6-0-10 +0.03   8-0-8 -0.28  1-15-0 -0.38
     Thornbull  16-0-0 +0.59  16-0-0 +0.99  16-0-0 +0.62  16-0-0 +0.53  16-0-0 +0.40   8-0-8 -0.06             -  16-0-0 +0.59  16-0-0 +0.56
     Thornrush  2-0-14 -0.56   8-8-0 +0.77  4-12-0 +0.08  16-0-0 +1.00  15-0-1 +0.83   8-0-8 +0.20  0-0-16 -0.66             -  13-0-3 +0.50
    Twinbarrel  0-13-3 -0.50  0-16-0 +0.36  2-0-14 -0.23  12-4-0 +0.90  0-15-1 +0.40  0-15-1 +0.38  0-0-16 -0.54  0-0-16 -0.95             -
```

```
[hills] rows spawn left, columns spawn right; row W-D-L over 16 seeds, mean row margin
                   Arrowhawk   Gravemantle       Ironmaw      Ironshot    Stonecreep     Thornback     Thornbull     Thornrush    Twinbarrel
     Arrowhawk             -  10-6-0 +0.71  5-0-11 -0.10  0-16-0  0.00  0-16-0  0.00  0-16-0  0.00  0-0-16 -0.56  4-0-12 -0.50  11-5-0 +0.73
   Gravemantle  0-0-16 -1.00             -  0-0-16 -0.76  0-0-16 -1.00  0-0-16 -1.00  0-0-16 -1.00  0-0-16 -0.99  0-0-16 -0.88  0-0-16 -1.00
       Ironmaw  4-0-12 -0.70  12-4-0 +0.64             -  0-0-16 -1.00  0-0-16 -1.00  0-0-16 -1.00  0-0-16 -0.71  1-3-12 -0.21  0-0-16 -1.00
      Ironshot  0-16-0  0.00  10-6-0 +0.68  1-1-14 -0.40             -  0-16-0  0.00  0-16-0  0.00  0-0-16 -0.50  0-0-16 -0.67  0-1-15 -0.98
    Stonecreep  0-16-0  0.00  10-6-0 +0.70   8-0-8 +0.02  0-16-0  0.00             -  0-16-0  0.00  3-0-13 -0.24  4-0-12 -0.41  0-16-0 -0.46
     Thornback  0-16-0  0.00  10-6-0 +0.74  3-0-13  0.00  0-16-0  0.00  0-16-0  0.00             -   8-0-8 +0.02  6-0-10 -0.07  0-16-0 -0.51
     Thornbull  10-0-6 -0.10  16-0-0 +0.99  16-0-0 +0.83  12-1-3 +0.08  12-0-4 +0.04   7-0-9 -0.34             -  16-0-0 +0.69  13-0-3 +0.14
     Thornrush  15-0-1 +0.79  12-4-0 +0.65   8-3-5 +0.01  16-0-0 +0.89  12-0-4 +0.62   7-1-8 +0.01  1-0-15 -0.68             -  15-1-0 +0.62
    Twinbarrel  0-16-0 -0.47  11-5-0 +0.76  1-0-15 -0.38  4-12-0 +0.76  0-16-0 +0.34  0-15-1 +0.21  0-0-16 -0.68  3-0-13 -0.48             -
```

## By author

Mean over the author's bots.

```
author     bots  rating  holdout  names
opus-w3       2    2273    0.981  Thornrush, Thornbull
fable-w3      2    1990    0.846  Ironmaw, Twinbarrel
sonnet-w3     2    1961    0.702  Thornback, Stonecreep
haiku-w3      2    1888    0.672  Ironshot, Arrowhawk
codex-w3      1    1423    0.214  Gravemantle
```

## Ratings

Every participant, pool and submissions, Elo scale, pool mean 1500.

```
Thornbull         2367  submission
Thornrush         2179  submission
Ironmaw           2011  submission
Thornback         1971  submission
Twinbarrel        1969  submission
Stonecreep        1950  submission
Phalanx           1922
Lancer            1921
Arrowhawk         1904  submission
Ironshot          1872  submission
Harrier           1843
Mason             1695
Sentinel          1590
Vanguard          1558
Wraith            1548
Breacher          1538
Brick             1522
Sapper            1457
Skirmish          1438
Gunner            1436
Bulwark           1434
Gravemantle       1423  submission
Turret            1400
Builder           1360
Kestrel           1320
Flyer             1256
ExampleBrawler    1209
Sprinter          1052
```

## Pool

Dev (visible): Lancer, Kestrel, Vanguard, Breacher, Wraith, Brick, Bulwark, Turret, Builder, Gunner, Flyer, ExampleBrawler, Sprinter

Holdout (hidden): Phalanx, Harrier, Sentinel, Mason, Sapper, Skirmish
