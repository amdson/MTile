# Fighter league

13 participants, 468 bouts, 720 frames each, terrains flat, corridor, hills; 4.8 s. Compute budget 20 µs per Decide (`DecideBudgetMicros`).

Score: win 1, draw ½, over bouts played; tie-break on mean health-fraction margin.

## Ladder

```
rank  name            author      score      W-D-L   margin  µs/Decide  paid reads/frame
   1  Sentinel        sonnet-w1   0.931     67-0-5   +0.655       0.17             1.000
   2  Harrier         sonnet-w1   0.847    61-0-11   +0.679       0.25             0.238
   3  Mason           sonnet-w1   0.771    55-1-16   +0.424       0.15             0.329
   4  Skirmish        sonnet-w1   0.750    54-0-18   +0.357       0.16             0.398
   5  Vanguard        sonnet-w1   0.639    46-0-26   +0.184       0.23             0.422
   6  Kestrel         sonnet-w1   0.514    37-0-35   +0.095       0.23             0.376
   7  Gunner          roster      0.465    32-3-37   -0.029       0.19             0.711
   8  Builder         roster      0.431    30-2-40   -0.086       0.20             1.000
   9  Brick           roster      0.389    28-0-44   -0.239       0.28             0.000
  10  Turret          roster      0.292    20-2-50   -0.280       0.18             1.000
  11  ExampleBrawler  reference   0.167    12-0-60   -0.566       0.22             0.704
  12  Flyer           roster      0.167    11-2-59   -0.623       0.25             0.749
  13  Sprinter        roster      0.139    10-0-62   -0.571       0.20             0.847
```

## Matrices

```
[flat] rows spawn left, columns spawn right; W = row wins, L = column wins, D = draw; frames, row margin
                ExampleBrawler        Harrier        Kestrel          Mason       Sentinel       Skirmish       Vanguard          Brick       Sprinter         Gunner          Flyer        Builder         Turret
 ExampleBrawler              -   L 207f -1.00   L 356f -0.80   L 178f -1.00   L 322f -1.00   L 177f -0.80   L 237f -0.56   L 259f -0.57   W 335f +0.55   L 720f -0.75   L 451f -1.00   L 720f -0.75   L 391f -1.00
        Harrier   W 207f +1.00              -   W 231f +1.00   L 459f -0.67   L 328f -0.43   W 291f +1.00   W 230f +1.00   W 402f +1.00   W  86f +1.00   W 241f +0.75   W  95f +1.00   W 383f +0.85   W 360f +0.63
        Kestrel   W 356f +0.80   L 231f -1.00              -   W 331f +0.60   L 215f -0.70   L 283f -0.69   L 234f -0.38   W 441f +0.70   W  89f +0.82   L 720f -0.60   W 153f +1.00   L 720f -0.27   W 264f +1.00
          Mason   W 179f +1.00   W 459f +0.67   L 331f -0.60              -   L  65f -0.86   L 328f -0.60   W 720f +0.60   W 338f +1.00   W  75f +1.00   W 140f +1.00   W 158f +1.00   W 130f +1.00   W 283f +0.50
       Sentinel   W 322f +1.00   W 328f +0.43   W 215f +0.70   W  65f +0.86              -   W 321f +0.55   W 369f +0.64   W 572f +0.57   W  63f +1.00   W  67f +1.00   W  65f +1.00   W  65f +1.00   W 720f +0.12
       Skirmish   W 286f +0.56   L 291f -1.00   W 285f +0.69   W 325f +0.60   L 378f -0.33              -   L 344f -0.69   W 283f +1.00   W 166f +0.64   W 720f +0.32   W 400f +0.36   W 270f +0.76   W 359f +0.40
       Vanguard   W 380f +0.80   L 231f -1.00   W 243f +0.38   L 720f -0.60   L 322f -1.00   W 231f +0.38              -   W 380f +0.70   W 154f +0.64   L 720f -0.40   W 369f +0.36   W 358f +0.64   W 482f +0.40
          Brick   W 288f +0.57   L 404f -1.00   L 476f -0.70   L 339f -1.00   L 515f -0.57   L 285f -1.00   L 343f -1.00              -   W 218f +0.77   L 720f -0.38   W 342f +0.60   L 720f -0.75   W 676f +0.44
       Sprinter   L 335f -0.55   L  86f -1.00   L  89f -0.82   L  75f -1.00   L  63f -1.00   L 166f -0.64   L 130f -0.82   L 205f -0.77              -   L 115f -1.00   W 369f +1.00   L 292f -1.00   W 491f +1.00
         Gunner   W 720f +0.75   L 241f -0.75   W 720f +0.60   L 140f -1.00   L  67f -1.00   L 593f -0.60   W 720f +0.40   W 720f +0.38   W 115f +1.00              -   W 127f +1.00   L 720f -0.60   L 244f -0.67
          Flyer   W 456f +1.00   L  95f -1.00   L 153f -1.00   L 194f -1.00   L  66f -1.00   L 399f -0.36   L 248f -0.68   L 316f -0.60   L 369f -1.00   L 127f -1.00              -   L 293f -1.00   W 428f +1.00
        Builder   W 720f +0.75   L 233f -0.85   L 713f -0.16   L 130f -1.00   L  65f -1.00   L 210f -0.88   L 340f -0.64   L 459f -0.78   W 292f +1.00   W 720f +0.60   W 292f +1.00              -   L 551f -0.50
         Turret   W 391f +1.00   L 360f -0.63   L 264f -1.00   L 283f -0.50   L 720f -0.12   L 359f -0.40   L 317f -0.70   L 382f -0.81   L 491f -1.00   W 244f +0.67   L 560f -1.00   W 243f +0.80              -
```

```
[corridor] rows spawn left, columns spawn right; W = row wins, L = column wins, D = draw; frames, row margin
                ExampleBrawler        Harrier        Kestrel          Mason       Sentinel       Skirmish       Vanguard          Brick       Sprinter         Gunner          Flyer        Builder         Turret
 ExampleBrawler              -   L 207f -1.00   L 322f -0.80   L 178f -1.00   L 322f -1.00   L 177f -0.80   L 237f -0.56   L 259f -0.57   W 335f +0.55   L 720f -0.75   W 179f +1.00   L 720f -0.75   L 391f -1.00
        Harrier   W 207f +1.00              -   W 231f +1.00   L 459f -0.67   L 328f -0.43   W 291f +1.00   W 230f +1.00   W 402f +1.00   W  86f +1.00   W 241f +0.75   W  94f +1.00   W 383f +0.85   W 360f +0.63
        Kestrel   W 322f +0.80   L 231f -1.00              -   L 720f -0.20   L 268f -0.55   L 361f -0.58   L 233f -0.38   W 412f +1.00   W  89f +0.82   L 720f -0.60   W 113f +1.00   L 720f -0.27   W 343f +0.70
          Mason   W 179f +1.00   W 459f +0.67   W 720f +0.20              -   L  65f -0.86   L 317f -0.60   W 720f +0.60   W 338f +1.00   W  75f +1.00   W 140f +1.00   W 158f +1.00   W 130f +1.00   W 283f +0.50
       Sentinel   W 322f +1.00   W 328f +0.43   W 268f +0.55   W  65f +0.86              -   W 321f +0.55   W 369f +0.64   W 572f +0.57   W  63f +1.00   W  67f +1.00   W  65f +1.00   W  65f +1.00   W 720f +0.12
       Skirmish   W 286f +0.56   L 291f -1.00   W 361f +0.58   W 320f +0.60   L 378f -0.33              -   L 344f -0.69   W 283f +1.00   W 157f +0.64   W 720f +0.32   W 126f +1.00   W 270f +0.76   W 359f +0.40
       Vanguard   W 380f +0.80   L 231f -1.00   W 242f +0.38   L 720f -0.60   L 322f -1.00   W 225f +0.38              -   W 380f +0.70   W 154f +0.64   L 720f -0.40   W 181f +1.00   L 720f -0.24   W 482f +0.40
          Brick   W 288f +0.57   L 404f -1.00   L 427f -1.00   L 339f -1.00   L 515f -0.57   L 285f -1.00   L 343f -1.00              -   W 218f +0.77   L 720f -0.38   W 442f +0.60   L 720f -0.75   W 676f +0.44
       Sprinter   L 335f -0.55   L  86f -1.00   L  89f -0.82   L  75f -1.00   L  63f -1.00   L 157f -0.64   L 130f -0.82   L 205f -0.77              -   L 115f -1.00   W 252f +1.00   L 292f -1.00   W 491f +1.00
         Gunner   W 720f +0.75   L 241f -0.75   W 720f +0.60   L 140f -1.00   L  67f -1.00   L 593f -0.60   W 720f +0.40   W 720f +0.38   W 115f +1.00              -   W 123f +1.00   L 720f -0.60   L 244f -0.67
          Flyer   L 179f -1.00   L  94f -1.00   L 113f -1.00   L 194f -1.00   L  66f -1.00   L 126f -1.00   L 136f -1.00   L 268f -0.80   L 252f -1.00   L 123f -1.00              -   L 289f -1.00   D 720f  0.00
        Builder   W 720f +0.75   L 233f -0.85   L 706f -0.16   L 130f -1.00   L  65f -1.00   L 210f -0.88   L 340f -0.64   L 459f -0.78   W 292f +1.00   W 720f +0.60   W 289f +1.00              -   L 551f -0.50
         Turret   W 391f +1.00   L 360f -0.63   L 343f -0.70   L 283f -0.50   L 720f -0.12   L 359f -0.40   L 317f -0.70   L 382f -0.81   L 491f -1.00   W 244f +0.67   D 720f  0.00   W 243f +0.80              -
```

```
[hills] rows spawn left, columns spawn right; W = row wins, L = column wins, D = draw; frames, row margin
                ExampleBrawler        Harrier        Kestrel          Mason       Sentinel       Skirmish       Vanguard          Brick       Sprinter         Gunner          Flyer        Builder         Turret
 ExampleBrawler              -   L 186f -1.00   L 384f -1.00   L 720f -0.25   L 720f -0.97   L 178f -0.80   L 720f -0.90   L 309f -0.42   W 200f +0.77   W 601f +0.25   L 375f -0.39   L 720f -0.45   W 369f +1.00
        Harrier   W 335f +1.00              -   W 230f +1.00   L 415f -0.67   L 328f -0.43   W 279f +1.00   W 225f +1.00   W 436f +1.00   W  86f +1.00   W 720f +0.08   W 133f +1.00   W 720f +0.52   W 359f +0.63
        Kestrel   L 481f -0.35   L 234f -1.00              -   W 270f +0.80   L 227f -0.70   L 296f -0.79   W 176f +0.50   L 530f -0.29   W  92f +0.82   W 444f +0.40   W  97f +1.00   L 618f -1.00   W 271f +1.00
          Mason   W 360f +1.00   W 491f +0.33   L 322f -0.60              -   W 300f +0.35   L 305f -0.60   W 196f +1.00   W 444f +1.00   W  76f +1.00   W 720f +0.33   W 116f +1.00   W 720f +0.27   W 323f +0.50
       Sentinel   L 525f -0.03   L 399f -0.51   W 215f +0.70   L 296f -0.35              -   L 263f -0.61   W 283f +0.85   W 405f +0.57   W  63f +1.00   W 720f +0.65   W  63f +1.00   W 720f +0.65   W 720f +0.12
       Skirmish   W 183f +0.80   L 286f -1.00   W 291f +0.69   W 271f +0.80   L 497f -0.33              -   L 264f -0.38   W 272f +1.00   W 116f +0.82   W 436f +0.40   W 243f +0.68   L 720f -0.84   W 199f +1.00
       Vanguard   W 356f +1.00   L 231f -1.00   L 307f -0.28   L 720f -0.20   L 323f -1.00   W 403f +0.06              -   W 237f +1.00   W 111f +0.82   W 233f +0.80   W 162f +0.68   W 201f +0.88   W 214f +1.00
          Brick   W 275f +0.57   L 403f -1.00   W 366f +0.74   L 339f -1.00   L 720f -0.73   L 306f -1.00   L 328f -1.00              -   W 220f +0.77   L 720f -0.38   W 288f +0.60   L 720f -0.75   L 720f -0.75
       Sprinter   L 209f -0.55   L  81f -1.00   L 122f -0.82   L 720f -0.23   L  62f -1.00   L 117f -0.82   L 161f -0.64   L 181f -0.79              -   L 125f -1.00   L 441f -0.15   L 720f -0.90   W 535f +1.00
         Gunner   W 720f +0.50   L 340f -0.75   W 720f +0.40   D 720f  0.00   L  67f -1.00   L 508f -0.60   L 720f -0.08   W 720f +0.38   W 113f +1.00              -   W 184f +1.00   D 720f  0.00   L 244f -0.67
          Flyer   W 480f +1.00   L  95f -1.00   L 243f -1.00   W 392f +0.50   L  65f -1.00   L 261f -0.68   L 205f -1.00   L 335f -0.60   W 440f +0.15   L 127f -1.00              -   L 291f -1.00   W 443f +1.00
        Builder   W 720f +0.09   L 465f -0.70   L 530f -0.40   L 720f -0.07   L 326f -0.74   L 255f -0.76   L 353f -0.64   L 461f -0.78   W 324f +1.00   D 720f  0.00   W 412f +0.47              -   L 396f -0.50
         Turret   W 391f +0.80   L 366f -0.63   L 257f -1.00   W 242f +0.17   L 720f -0.12   L 217f -0.70   W 720f +0.12   L 386f -0.81   L 493f -1.00   W 720f +0.33   L 430f -1.00   W 720f +0.40              -
```

