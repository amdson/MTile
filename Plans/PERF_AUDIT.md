# Performance audit — CPU cost of a frame, and what to cut for WASM

Measured 2026-08-14 on the desktop Release build (native x64). Numbers are µs unless
stated. Reproduce with `dotnet run -c Release --project MTile.Bench`.

Methodology note, because it changed the numbers materially: each scenario is the
**minimum over 3 repetitions**, not a single timed pass. A single pass swung 2× between
back-to-back runs on an idle desktop. Everything that perturbs a benchmark only ever adds
time, so the minimum estimates the true cost and converges much faster than a mean over a
heavy right tail. Even so, rows under ~100 µs on this machine are not reliably
measurable — read them as order-of-magnitude only.

The web build measures ~40 fps AOT (`Plans/WEB_PVP.md`), i.e. a **25 ms frame budget**
versus the desktop's 16.7. Mono's WASM AOT runs scalar float code roughly 2–4× slower
than native, so every native µs below is 2–4 µs in the browser. That multiplier is the
whole reason a cost that "hides" on desktop decides playability on the web.

## Headline: the animation solver costs 10–60× the simulation

```
scenario                                µs/frame     worst 60-frame bucket
flat rest (no input)          [sim]          42.1                      88.4
flat run                      [sim]          24.8                     195.6
bumpy corridor                [sim]         305.4                    1064.9   <- corrector
vault-heavy course            [sim]          55.7                     525.2
snapshot+step (pair)          [sim]         138.8
restore                       [sim]          11.4
rollback frame (win=8)        [sim]         333.1
anim biped/run          (+sim)              293.1                    2366.2
anim biped/idle         (+sim)             1272.5                    3003.6
anim biped/idle (no terrain)  (+sim)         69.1                     281.2
anim biped_rabbit/run   (+sim)              567.7                    3046.5
anim biped_rabbit/idle  (+sim)             2209.8                    4403.7
anim biped_rabbit/idle (no terrain)          89.5                     254.1
```

`biped_rabbit` is the rig `game_config.json` actually ships (`"AnimationRig"`).

### Finding 1 — the LM solve costs ~1.3–2.2 ms per character per frame

Compare the last two rows: idle with terrain half-planes fed in = 2210 µs, the same idle
pose with them withheld = 90 µs. That 25× gap is real and reproduced across every run,
but it is **not** the story it looks like. Withholding the planes doesn't make the solve
cheap — it makes the solve *not happen*, because `_surfacesNear` is what triggers the
off-locomotion static solve at all. The right reading is: the solve costs ~2 ms whenever
it runs, and terrain presence is merely what decides whether it runs.

That distinction matters because the obvious fix (make the dormancy gate fire at idle) is
**not sufficient**. It only addresses standing on flat ground. The max case — a character
near real terrain, which is most of actual play — still pays the full ~2 ms, and that is
the number the frame budget has to survive.

`dotnet run -c Release --project MTile.Bench -- --diag` breaks one solve down:

```
scenario         extract   update  srf  live/nopen  solved  iters  evals    m   resid  jacob  algebra
biped/idle          20.9   1079.7  1.0        6/13   100 %    9.4   39.8   46   192.9   91.4    764.1
biped/run           19.4    345.8  1.0     5.84/13    54 %    4.5   23.1   48    74.2   41.3    147.2
biped_rabbit/idle   22.1   1784.8  1.0        6/17   100 %    9.9   39.4   58   247.4   98.3   1412.2
biped_rabbit/run    15.6    703.8  1.0    5.845/17    65 %    5.4   26.8   60   140.5   73.8    393.6
```

(µs/frame. `resid`/`jacob` = time in the animator's residual and Jacobian callbacks;
`algebra` = time inside `LeastSquaresSolver` itself — Jacobian zeroing, normal equations,
Cholesky, trust loop.)

**Half-plane extraction is not the problem.** 16–22 µs, ~1% of the cost. And it is
building the *right* thing: on flat ground it emits exactly **1** merged surface, not a
pile of per-tile duplicates — the coplanar merge in `TerrainSurfaces.Emit` works. Nothing
is wrong on that path.

**The cost is 98% the LM solve, and ~80% of the solve is the solver's own linear
algebra** — not the callbacks, not the pose rebuild. For reference, one `BuildSolvePose`
costs ~4 µs (sample 1.6 + FK 2.5 on the rabbit rig), so all ~40 evaluations of it total
~160 µs of the 1785.

### Finding 1a — it's the JᵀJ accumulator's dependency chain, not the layout

`-- --diag` also runs the solver core in isolation (`SolverCore`, callbacks stubbed to
near-zero) and micro-benchmarks the hot loop (`JtJLayout`), which localises the cost
completely:

```
solver core in isolation          us/Minimize   iters   resid   jacob   algebra
58 x 20 (maxVars 22)                    307.0       3    44.3    13.1     173.2

JtJ accumulation (m=58, n=20, maxVars=22)
  strided (current)  41.21 us    transposed  40.42 us (1.0x)    +Vector<float>  16.46 us (2.5x)
  m=116              90.83 us               127.85 us (0.7x)                    26.66 us (3.4x)
```

Two things fall out. **The `JᵀJ` accumulation alone is 41 µs of the ~58 µs of algebra per
iteration** — it is essentially the whole cost. And **transposing J buys nothing** (1.0×,
worse at m=116), so the strided `_jac[i * _maxVars + a]` access I suspected is *not* the
problem: at 16 KB the whole Jacobian is L1-resident and the prefetcher handles the stride
fine.

What's actually happening: 41 µs for 12k multiply-adds is ~12 cycles each, which is
exactly what a **serial floating-point accumulator** costs. `s += jac[…] * jac[…]` makes
every iteration wait on the previous add's ~4-cycle latency, so the loop cannot pipeline,
and with the dependency chain in the way there is nothing for the JIT to vectorize either.
Breaking the chain with `System.Numerics.Vector<float>` measures **2.5–3.4× immediately**,
and multiple independent accumulators would take it further.

Note `Vector<float>.Count` is 8 on this desktop (AVX2, 256-bit) but will be **4** in the
browser — WASM SIMD is 128-bit. Expect meaningfully less than 2.5× there, perhaps 1.5–2×.
The row-count and iteration-count reductions below multiply with it and are portable.

Secondary waste, worth fixing but not the headline:

- **`nopen` vs `live`**: the no-penetration block emits a row per (surface, bone) over the
  whole rig — 17 rows on the rabbit — but terrain planes are only ever extracted for the 7
  tips in `TerrainSurfaces.TipNames`, so only **6** can ever be nonzero. ~11 of 58 rows are
  permanent structural zeros that are still zeroed, multiplied, and accumulated every
  iteration. The fixed layout is deliberate (`CharacterAnimator.Constraints.cs:196`), but
  it can be kept while indexing only masked bones.
- **The static path re-samples an invariant pose.** `SolveStaticPose` pins
  `_solveLo[IdxPhi] = _solveHi[IdxPhi] = 0`, so Δφ never moves — which makes
  `AnimationSampler.SampleSmooth` + `_overlays.Compose` inside `BuildSolvePose` return a
  bit-identical result on all ~40 evaluations. Only the Δθ add and the FK depend on `x`.
  Worth ~1.6 µs × 40 ≈ 65 µs per solve; small next to the algebra, free to fix.
- **Idle runs ~9.9 of a maximum 12 iterations, every frame.** It is grinding to the cap
  rather than converging. Whatever it is chasing at rest is worth understanding before
  tuning the iteration count down.

In the browser the solve is plausibly **4–9 ms of a 25 ms frame, per character** — doubled
in PvP, since P2 gets its own animator. It remains the largest single lever found.

### Finding 1c — the solver has no convergence test, and that's the biggest single win

`LeastSquaresSolver.Minimize` stops on exactly three things: the iteration cap, an
absolute cost floor of 1e-12, and total failure to find any downhill step. It has **none**
of the three convergence tests every standard LM implementation carries — relative cost
reduction (MINPACK `ftol`, Ceres `function_tolerance`, scipy `ftol`), step size (`xtol`),
or gradient norm (`gtol`). So it keeps iterating as long as it can find *any* improvement,
however microscopic.

The cost trace (`-- --diag`) shows what that costs. Cost after each iteration, as a
fraction of the starting cost:

```
biped/idle          1.000  0.001  0.001  0.001  0.001  0.001  0.001  0.001  0.001  0.001
biped_rabbit/idle   1.000  0.001  0.001  0.001  0.001  0.001  0.001  0.001  0.001  0.001
```

**Idle is 99.9% converged after the first iteration and then grinds for nine more.**

`LeastSquaresSolver.Ftol` now exists to measure this (**default 0 = today's behaviour,
bit-for-bit**; it is opt-in, not enabled). The sweep, with `maxdev` = the largest per-bone
angle difference against the `Ftol = 0` pose on the same frame:

```
scenario              ftol   iters   update us  speedup  maxdev deg
biped/idle               0     9.6      1391.8     1.00x       0.000
                    0.0001     4.0       372.6     3.74x       0.053
                     0.001     4.0       390.7     3.56x       0.053
biped_rabbit/idle        0     9.8      1312.7     1.00x       0.000
                    0.0001     6.0       930.8     1.41x       0.008
                     0.001     3.0       697.0     1.88x       0.241
biped_rabbit/run         0     6.0      1066.4     1.00x       0.000
                    0.0001     3.0       483.9     2.20x       2.386
                     0.001     5.0       403.3     2.64x      27.105
```

**On the static path this is free money**: 1.4–3.7× for a pose that moves by
0.008–0.24°, on a character 19 px tall. Nobody will ever see it.

**On the cadence path it cannot be adopted blind.** Those 2–132° deviations are not the
solver going wrong — Δφ is a free variable there and the locomotion phase *persists across
frames*, so stopping a little earlier shifts where in the gait cycle the character is, and
the comparison is measuring drift between two valid runs rather than error in one. It
needs a visual check, not a numeric one. Applying `Ftol` only on the `SolveStaticPose`
path is the safe subset and covers the idle case that motivated all of this.


### Finding 1c/1a — both landed: the static solve is 2.5–3.8× faster (2026-08-17)

Two changes, each measured and each scoped to where it is provably neutral.

**1. A convergence test on the static path** (`AnimSolverConfig.StaticFtol`, default 1e-3).
The static solve had none, so it spent its whole 12-iteration budget every frame while the
cost trace showed it done after one. `LeastSquaresSolver.Minimize` now takes a per-call
`ftol` (negative = process default), so the cadence path is unaffected.

| StaticFtol | iters | update µs | speedup | max pose move |
|---|---|---|---|---|
| 0 (historical) | 9.8 | 940 | 1.00× | — |
| 1e-4 | 6.0 | 470 | 2.00× | 0.008° |
| **1e-3 (default)** | 3.0 | 358 | **2.62×** | 0.240° |
| 1e-2 | 3.0 | 372 | 2.53× | 0.240° |

(biped_rabbit/idle; biped/idle is 2.0–2.4×.) A quarter of a degree on a 19px-tall character
is not visible. Deliberately **not** applied to the cadence solve: Δφ persists frame to
frame, so stopping earlier shifts the phase rather than the pose, and that wants eyes on it
rather than a number.

**2. Vectorized normal equations on the static path** (`AnimSolverConfig.StaticVectorize`).
J is transposed into a column-major scratch (O(n·m), ~1/6 of what it feeds) so each JᵀJ dot
product walks contiguous memory, then `Vector<float>` carries Count independent
accumulators — which is the actual fix for Finding 1a's serial-accumulator dependency chain.

This one needed a quality check, because reordering the sum changes the LM's exact
trajectory, not just its speed. **Comparing drawn poses is the wrong metric here**: on the
cadence path a hair of Δφ difference shifts the phase and shows up as a 131° per-bone delta
that a viewer would read as identical motion. The right metric is the objective the solver
is minimizing, so `LeastSquaresSolver.LastCost` was added and compared:

| | scalar µs | vector µs | speedup | iters | final cost |
|---|---|---|---|---|---|
| biped/idle | 460 | 348 | 1.32× | 9.6 → 9.4 | 0.1756 → 0.1756 |
| biped_rabbit/idle | 771 | 521 | 1.48× | 9.8 → 9.7 | 0.1368 → 0.1368 |
| biped/run (cadence) | — | — | — | 8.0 → **3.0** | 0.2526 → **0.3148** |

Identical cost on the static path — free. **Not** free on the cadence path: `biped/run` gives
up after 3 iterations instead of 8 and settles ~25% worse, so `CadenceVectorize` defaults
**false**. The reason turned out not to be what was first assumed — see below.

#### Why the cadence path moved: conditioning, not delicate arithmetic

The first explanation offered for the 8→3 iteration drop was a marginal accept/reject flip in
the trust loop. **That was wrong, and the data says so:** the run path's late iterations are
still buying 1e-2 to 3e-1 relative reduction, which nothing at float epsilon can flip.

Nor is it a bug — the two accumulations agree to **4.8e-7 relative** (~4 ulps) on identical
input at real dimensions (`MTile.Bench --ftol`, `JtJDiff`).

The mechanism is the condition number, measured as the worst Cholesky pivot ratio of JᵀJ:

| scenario | typical cond(JᵀJ) | worst frame |
|---|---|---|
| biped/idle | 1.1e3 | 1.1e3 |
| biped_rabbit/idle | 1.2e3 | 1.2e3 |
| biped_rabbit/run | 3.5e3 | 1.5e4 |
| **biped/run** | 2.9e3 | **1.0e6** |

4.8e-7 amplified by 1e6 is an O(1) relative error in the computed STEP. On its worst frames
the solver takes a genuinely different direction and walks to a different answer. The
correlation across the table is exact: `biped/run` is the only scenario reaching 1e6 and the
only one whose final cost moved (0.2526 → 0.3148); `biped_rabbit/run` at 1.5e4 barely budged
(0.4068 → 0.4014, marginally better); the idle paths at ~1e3 are bit-stable in cost.

Forming the normal equations squares cond(J), so a Jacobian with κ ≈ 1e3 — perfectly ordinary
— yields a JᵀJ with κ ≈ 1e6, at the edge of what float32 carries. **This upgrades Finding 1b
from a tidiness argument to a correctness one:** QR of J works with κ(J) rather than κ(J)²,
buying back ~3 digits. The present solver is not wrong, but it is close enough to the edge
that a legal reordering of one sum changes its answer — which is also a warning about any
future change that touches the residual order, the weights, or the row set.

**Combined, same-session A/B on the main bench** (historical settings vs current, back to back):

| | historical | current | |
|---|---|---|---|
| anim biped/idle (+sim) | 466.4 µs | 186.8 µs | **2.50×** |
| anim biped_rabbit/idle (+sim) | 716.8 µs | 191.1 µs | **3.75×** |

The run/cadence rows are unchanged by construction and their movement between runs is machine
noise. Nothing regressed: the suite fails the same 7 pre-existing tests before and after.

**The committed baseline was stale.** Those historical numbers (466/717 µs) are ~3× below what
`baseline.txt` recorded for the identical code path (1272/2210 µs) — the baseline was captured
while the machine was much busier. It has been regenerated. Treat the earlier "animation costs
2.2 ms per character" figure, and the 60fps budget arithmetic built on it, as pessimistic by
roughly that factor; the honest current figure is ~0.2 ms per character on this machine, so
the browser-frame picture wants re-deriving from the new baseline and a real in-browser
profile rather than from the old one.
### Finding 1b — a matrix library is neither needed nor helpful here

The recurring question is whether to pull in a matmul/BLAS library, held back by the web
dependency risk. Framing it as "hand-written loop vs. BLAS" skips the entire middle, which
is where the real prior art is — and that prior art is **algorithmic, not SIMD**.

Small dense nonlinear least squares is thoroughly solved territory: MINPACK's `lmder` /
`lmpar` (public domain, the reference every later implementation descends from), Ceres
Solver, `levmar`, scipy's `least_squares`. Compared against any of them, this solver is
missing four standard things, in descending order of what they're worth here:

1. **Convergence tests** — Finding 1c. Missing entirely; measured 1.4–3.7× on the static
   path for an invisible pose change. Roughly five lines.
2. **Not forming JᵀJ at all.** MINPACK factors J by Householder QR directly (Ceres:
   `DENSE_QR`). The normal equations square the condition number, which is the classic
   thing not to do — and forming them is precisely why the hot loop is a JᵀJ accumulation
   in the first place. Fixing the conditioning and deleting the hot loop are the same edit.
3. **Reusing the factorization across damping retries.** `lmpar` solves the damped
   subproblem for successive λ with Givens rotations on the existing R — O(n²) per retry,
   against the current rebuild of `_A` (n²) plus a fresh Cholesky (n³/6). At ~3 retries per
   iteration that is most of the trust loop.
4. **Gain-ratio trust-region control** (actual vs. predicted reduction) instead of the
   fixed ×4 / ×0.3 damping heuristic — fewer iterations to the same answer.

Two credible middle-ground routes, neither of which is "write your own":

- **Port MINPACK `lmder`/`lmpar`.** ~500 lines, public domain, the best-tested LM code in
  existence. No dependency, no web risk, and it brings 1–4 together.
- **Math.NET Numerics** ships `LevenbergMarquardtMinimizer`. Its *managed* provider runs on
  WASM fine (the MKL/OpenBLAS providers are opt-in native — those are the ones to avoid).
  Worth an evaluation, though its `Matrix<T>` allocation model is aimed at problems much
  larger than 58×20.

*A BLAS specifically still wouldn't help.* The hot operation is a 20×20 Gram matrix from a
58-row Jacobian — 12k multiply-adds over 4.6 KB, orders of magnitude below where a tuned
`ssyrk` starts to win; at this size the call is dominated by dispatch and packing. And with
(2) above, the operation stops existing.

*The dependency risk was real but is now moot.* Native BLAS (OpenBLAS/MKL) genuinely is a
non-starter for the Blazor WASM host — the hesitation was correct. A managed NuGet
(Math.NET etc.) is technically possible but would have to be added to **both** csprojs,
since `MTile.Web` re-globs the root sources rather than ProjectReferencing `MTile.Core`,
and it carries AOT-trimming risk for no gain at this size.

But `System.Numerics.Vector<T>` is **not a dependency at all** — it's BCL, so it compiles
under DesktopGL and KNI alike with nothing added to either csproj. And it is not inert in
the browser: verified against the WebAssembly SDK on this machine
(`packs/Microsoft.NET.Runtime.WebAssembly.Sdk/8.0.27/Sdk/WasmApp.targets:116`),
`WasmEnableSIMD` defaults to `WasmEnableExceptionHandling`, which defaults to `true` — so
`-msimd128` reaches emcc and `mattr=simd` reaches the Mono AOT compiler on a normal
publish. `MTile.Web.csproj` doesn't set the property, so it already gets SIMD; setting it
explicitly would only be for documentation and pinning.

The order of attack, all dependency-free:

1. Vectorize the JᵀJ accumulation with `Vector<float>` + multiple accumulators (measured
   2.5–3.4× native on the loop that is ~70% of the solver's own time; less in-browser).
2. Cut `m`. Skipping structurally-zero rows is a proportional win on the same loop.
3. Cut iterations. Idle burns ~9.9 of 12 every frame without converging.

### Finding 1b — QR is implemented and switchable. It is better and slower.

`LeastSquaresSolver.UseQr` (default **off**), or per-call `qr:`. Structure follows MINPACK
lmder: factor J once per iteration (Householder, `QrFactor`), then per damping value solve the
small stacked `[R; √µ D]` system (`QrStep`). JᵀJ is never formed. `D` is the same damping
metric the Cholesky path uses, so it is the same damped system by a different route — an
apples-to-apples swap, not a differently-tuned algorithm. Covered by
`MTile.Tests/QrStepTests.cs` (agreement with Cholesky on a well-conditioned problem; box
bounds respected).

**It finds materially better answers where conditioning is bad**, which is the whole thesis:

| | cost, normal equations | cost, QR | iters |
|---|---|---|---|
| biped/idle | 0.1757 | 0.1757 | 4 → 4 |
| biped_rabbit/idle | 0.1371 | 0.1371 | 3 → 3 |
| biped_rabbit/run | 0.4068 | 0.4088 | 6 → 6 |
| **biped/run** | 0.2526 | **0.04757** | 8 → **3** |

`biped/run` — the scenario measured at cond(JᵀJ) ≈ 1e6 — lands on a **5.3× lower cost in
fewer iterations**. The normal-equations path was not merely fragile there; it was converging
to a substantially worse pose and taking longer to do it. The earlier "vectorizing costs 25%"
result should be read in that light: it was noise around an answer that was already ~5× off.

**And it removes the fragility, confirming the diagnosis.** Re-running the scalar-vs-vector
comparison with QR active gives identical cost on all four scenarios (biped/run 0.04757 both
ways, iterations 3 → 3). So the summation-order sensitivity really was the squared condition
number, and under QR `CadenceVectorize` can be on.

**The cost is real**: 1.6–2.9× slower per update even with the factorization hoisted out of
the damping loop and vectorization enabled on both paths.

| | NE µs | QR µs | |
|---|---|---|---|
| biped/idle | 278.5 | 690.4 | 2.5× |
| biped/run | 288.3 | 467.4 | 1.6× |
| biped_rabbit/idle | 267.7 | 787.8 | 2.9× |
| biped_rabbit/run | 444.6 | 883.2 | 2.0× |

Some of that is inherent — a Householder QR of J costs ~2mn² against ~mn²/2 to form JᵀJ, so
~4× on that step, permanently. But **the largest remaining piece is not inherent**: the per-µ
solve currently runs a full O(n³) Householder over the stacked 2n×n block, ~12× a Cholesky.
MINPACK's `lmpar` instead eliminates the √µD block against the triangular R with **Givens
rotations in O(n²)**, exploiting the structure this implementation throws away. That is the
next move if QR is to be the default, and it should close most of the gap on the
damping-heavy scenarios.

Left off by default pending that work and a look on screen — a 5.3× better cost on one
scenario is a large enough behavioural change that it wants eyes, not just a number.

### Finding 2 — the corrector's QP is the sim's only hot path, but it is 20× cheaper per solve

The sim has a **second, unrelated solver**: `CorrectionSolver`, a projected-descent QP over
(channels × horizon) with a fixed 4 inner iterations. It shares nothing with the animation
solver — different algorithm, different file — so none of Finding 1 transfers to it. (The
LM-based `TrajectoryLm` is *not* in this path: `movement_config.json` ships
`FoldEngine: "ref"`, and `"lm"` is a freeze-frame diagnostic.)

`-- --diag` attributes it (`CorrectionSolver.Profile`, write-only counters that nothing in
the sim reads, so they cannot affect a step or a rollback checksum):

```
scenario           us/tick   qp us/tick   share   qp calls/tick   us/call
flat run              13.3          0.5      4 %            0.01     61.44
vault course          17.2          6.6     38 %            0.05    136.54
bumpy corridor       104.8         86.0     82 %            0.99     86.92
```

**One QP solve is ~60–135 µs**, against the animation LM's ~1300–2200 µs — 15–25× cheaper.
And it is *bursty*: it fires on 1% of ticks running flat, 5% on the vault course, and
essentially every tick only in the bumpy corridor, where it is 82% of the tick. The
animation solver, by contrast, runs on 54–100% of frames regardless of terrain.

So the corrector is **not** the priority — but one asymmetry keeps it from being ignorable:

- The corrector is **inside the sim**, so a rollback resim multiplies it by the window.
  Worst case in the corridor: ~86 µs × 8 ≈ **690 µs per visual frame**.
- The animator is **render-only**, called once per visual frame from
  `CosmeticUpdateSystem.Update` after the step loop, so rollback does *not* multiply it —
  but it is per character: ~2.2 ms × 2 players ≈ **4.4 ms per visual frame** in PvP.

Animation still dominates roughly 6:1 in the worst realistic case. Note also that
`game_config.json` ships `"Stage": "corridor"` — the corrector's worst case is the default
stage, so any in-game reading starts from there.

(The committed baseline's 305 µs for `bumpy corridor` was captured while the machine was
busy; quieter runs give 105–131 µs. The *share* — 82% QP — is the stable number.)

### Finding 2a — the QP rebuilt its constant data 16× per solve (fixed, ~15%)

Measured dimensions of the fold solve in `bumpy corridor`: **H = 10 ticks, 7 channels, ~11
rows, 16 sweeps**. Sixteen, not the four in `CorrectionSolver.DefaultInnerIterations` — the
fold path takes `MovementConfig.FoldIterations`, which ships at 16, and the fold path is the
one that fires on ~99% of corridor ticks.

Three things inside the sweep loop did not depend on the iterate `z` at all:

- **The per-variable curvature bound `L`** — an O(C·H·R) triple loop, rebuilt identically on
  every one of the 16 sweeps.
- **`Lever(kind, T, k, dt)`** — recomputed per (channel, tick, row) per sweep, though it is a
  function of the lever kind and the tick gap `T − k` alone. A 2·H table covers every pair.
- **`Skips(channel, row)`** — a branchy row-class predicate, likewise per triple per sweep.
  R is capped at `MaxEvents = 32`, so it fits in one `uint` bitmask per channel.

All three are now hoisted above the sweep loop, and the sweeps read a flattened activation
bitmask (one `ulong` per channel, H ≤ 48) instead of re-reading the large array-carrying
`ChannelDef` struct and dereferencing `ActiveMask` on every triple.

**This is bit-for-bit identical** — same expressions, same accumulation order, so the sim's
behaviour is unchanged. Confirmed: the corrector/fold/ballistic/cave-mouth slice fails
exactly the same 6 tests before and after (all pre-existing).

Measured (min over alternating runs, a noisy desktop — individual runs swing ±30%):

| | µs per solve |
|---|---|
| before | 139.7 |
| after  | 116.7 |

≈ **15%** off the QP, so ≈ 12% off a corridor sim tick. It transfers to WASM directly — these
are plain scalar loops with no SIMD or JIT-specific trick involved, so the saving scales with
whatever penalty the browser is paying.

### Finding 2b — there is no converged tail to skip here

The obvious next move was Finding 1c's trick: stop early. It does not apply. Instrumenting
the per-sweep movement of the iterate (max ‖Δz‖ over the corridor's solves) gives:

```
230.6 178.6 137.1 118.6 108.5 96.4 83.3 78.5 72.6 66.1 61.1 58.0 54.3 50.3 48.7 46.7
```

A factor of 5 over 16 sweeps, decaying roughly geometrically with ratio ~0.9. The QP is
**not converged when it stops** — projected gradient descent on this conditioning is just
slow. Unlike the LM (which hit 0.001 of its starting cost after one iteration and then
flatlined for nine more), every sweep here is still moving the answer, so a tolerance test
would either fire never or change the shipped behaviour.

The honest reading: `FoldIterations` is a straight quality/time dial, and the corrector's
cost is linear in it. Halving it halves the corrector, and it *will* change how the fold
feels — that is a playtest question, not a perf one.

### Finding 2c — the remaining cost is structural, and the next lever is scalar channels

What is left is irreducible under the current formulation: R·C·H ≈ 770 (row, channel, tick)
triples, touched twice per sweep (slack accumulation, then the gradient/projection sweep),
times 16 sweeps ≈ 25k inner steps per solve.

The one real lever left: **6 of the 7 fold channels are `AxisOnly`** — their `z` is
constrained to `λ·Axis`, a scalar, yet it is stored and processed as a `Vector2` throughout.
Every dot product, every add, and the projection itself carry a redundant component. A scalar
path for `AxisOnly` channels would roughly halve the sweep arithmetic.

Unlike Finding 2a, this one is **not free**: `λ·Dot(Axis, n̂)` and `Dot(λ·Axis, n̂)` are not
the same float, so it perturbs the sim and would need to be adopted deliberately (and
re-baselined), not slipped in as an optimization.


### Finding 2d — the QP costs 2.2× more in wasm than native

Measured, not extrapolated: the same frozen subproblem (H=10, 7 channels, 10 rows, 16
sweeps), solved by the same code, on an **AOT** publish in headless Chrome versus a native
Release build, three paired runs back to back on the same busy desktop.

| | µs/solve | µs/sweep |
|---|---|---|
| native (DesktopGL, Release) | 82.7 | 5.17 |
| wasm (AOT, Chrome) | 183.4 | 11.46 |

**≈ 2.2×**, stable across pairs (2.22, 2.20, 2.71 — the outlier is the desktop's noise, not
the browser's). This is the ordinary wasm penalty for scalar float work; nothing about the QP
is pathological in the browser.

What it means for the frame budget: the corrector fires on ~99% of ticks in corridor terrain,
so it alone is ~183 µs of a 16.7 ms browser frame (~1%) in normal play — but rollback
re-simulates, so a worst-case 8-frame rollback is ~1.5 ms of one visual frame. Set against
the animation solver (2.2 ms per character *natively*, render-only so rollback does not
multiply it, but ×2 players), the ordering from Finding 2 survives the trip to wasm: the
animation solver is still where the browser frame goes.

The 15% from Finding 2a is included in the wasm figure above. It was not separately measured
pre/post in the browser (each measurement costs a ~15 minute AOT publish); it is a reduction
in work rather than a JIT-specific trick, so it should carry across at roughly the same
proportion.

**How to re-run it.** `Diagnostics/QpBench.cs` compiles into both hosts. Native:
`dotnet run -c Release --project MTile.Bench -- --corrector`. Browser: publish with AOT, serve
it, and drive it with `MTile.Web/smoke/qp_bench.js` (setup in `MTile.Web/smoke/README.md`).

Three things this measurement needs to stay honest, each of which broke a first attempt:

- **AOT, not the dev server.** `dotnet run --project MTile.Web` is interpreted; it reported
  1173 µs/solve, ~14× the AOT number. Benchmarking against it measures the interpreter.
- **A frozen problem, not a fresh capture.** The first working browser run captured a
  *2-channel* subproblem where the desktop captured 7: float determinism does not hold across
  runtimes (the same reason cross-play is refused), so a few hundred ticks of sim diverge and
  each host times a different problem. The problem is now captured once on the desktop
  (`MTile.Bench -- --dump-problem` → `Diagnostics/QpProblem.g.cs`) and decoded identically by
  both.
- **Batched timing, not per-call.** wasm's `Stopwatch` resolution is coarse enough that
  bracketing a single ~100 µs solve measures the clock. The bench calibrates a batch size and
  times the batch under one clock pair.
## Render-side findings (read, not yet measured — the new profiler will settle them)

**`GlowTrailField` runs a full reaction–diffusion update every frame regardless of
whether anything is glowing.** [GlowTrailField.cs:96](../Drawing/GlowTrailField.cs#L96)
`BeginFrame` unconditionally does: 4 render-target binds, 3 clears, a reproject blit, and
two 5-tap separable blur passes (10 half-res fullscreen draws), then `Composite` adds a
full-res blit. That is ~12 fullscreen passes and 4 RT switches per frame for a field that
is black whenever nobody has swung in the last ~0.8 s. Render-target switches are among
the most expensive things a WebGL context does.

Currently defused: `GameConfig.GlowTrailField` defaults to **false**. This is a landmine,
not a live cost — but turning it on is a one-line config change that would cost the web
build dearly. Gate the whole advance on "a stamp landed within 5/λ seconds"; outside that
window the field is provably zero and every one of those passes is a no-op.

**`ChunkRenderer` submits two `SpriteBatch.Draw` calls per solid tile, with per-chunk
culling only.** [ChunkRenderer.cs:96](../Drawing/ChunkRenderer.cs#L96) A black underlay
pass then a tinted-fill pass, over all 16×16 cells of every chunk whose *bounding box*
intersects the view. At the shipped `CameraZoom: 5.0` the view is ~200×180 world px — a
fraction of one 256 px chunk — so this is cheap today. It scales as 1/zoom²: at zoom 1.55
(the `Camera` default) it is ~10× the draw calls, and at the 8 px tile size being
experimented with, 4× again on top. Worth per-tile culling before either lands.

**`SpriteSkin.Draw` rebuilds and re-uploads its vertex buffer every frame per player**
([SpriteSkin.cs:367](../Drawing/SpriteSkin.cs#L367)): MLS deform over every vertex, then
one `DrawUserIndexedPrimitives` per layer — a dynamic upload plus a draw call per layer,
per player — and it splits the world `SpriteBatch` around itself (`End`/`Begin`), forcing
an extra flush. `DrawPlayerSpriteSkin` is **on** in the shipped config.

**`Console.WriteLine` on the sim hot path.** Four FSM-transition prints in
[PlayerCharacter.cs:504](../Character/PlayerCharacter.cs#L504). On desktop this is an
fprintf; on WASM each is a JS interop hop into devtools. They also fire once per *replay*
of a frame under rollback, so a single real transition prints repeatedly. Now gated by
`SimTrace` (below) — kept as the dev tool it is, off in the browser.

## What was added

### `Diagnostics/FrameProfiler.cs` — per-pass frame timing

Zero-allocation named scopes, mean + worst over a 60-frame window. Wired through
`Game1`'s Update and Draw as: `sim`, `anim`, `cosmetic`, `backdrop`, `chunks`,
`entities`, `skins`, `skeleton`, `fx`, `debugdraw`, `glow`, `hud`, `present`.

Replaces the old three-lump `worst/60f sim/cosmetics/draw` line. Shown on screen under
`GameConfig.DebugFrameTimings` (already `true`), now as fps + frame ms + the per-pass
breakdown. **F11 dumps the last window to the console** — that is the readout for the web
build, where the dump lands in devtools and can be copied out, unlike the on-screen line.

Three caveats worth knowing before reading the numbers:

- Draw-side scopes measure the CPU cost of *building* the batch. `SpriteBatch` is
  deferred, so the GPU submission lands in `present`, at the closing `End()`.
- Scopes are inclusive: `anim` is counted inside `cosmetic`.
- On WASM `Stopwatch` is `performance.now()` — coarse and deliberately jittered by the
  browser. Trust the window mean, not a single frame.

### `MTile.Bench` — animation coverage and a regression gate

- `MTile.Bench/Animation.cs`: the animation solver, driven off a real `Simulation` through
  the exact path `CosmeticUpdateSystem` uses, so what's measured is what ships. This is
  where Finding 1 came from; nothing previously measured the render-side CPU at all.
- `MTile.Bench/BenchReport.cs` + `baseline.txt`: tab-separated committed baseline.
  - `dotnet run -c Release --project MTile.Bench` — table only.
  - `-- --check` — diff against `baseline.txt`, **exit 1** on a scenario ~1.5× slower.
  - `-- --save <path>` — regenerate the baseline (do this deliberately, in its own commit).
- `MTile.Bench/AnimDiag.cs` (`-- --diag`): the structural breakdown above — extract vs
  solve, surface count, live vs emitted rows, iterations, evaluations, and the
  callback/algebra split. Totals alone can't distinguish "extraction is slow" from
  "extraction feeds too many planes" from "the core is slow regardless"; this can, and
  did. Backed by counters on `LeastSquaresSolver` (`ProfileCallbacks`, `LastRows`,
  `LastIterations`, `LastResidual/Jacobian/AlgebraTicks`) surfaced through
  `CharacterAnimator.LastSolveWork` / `LastSolveTicks` / `LastSolveRows`.

### `Diagnostics/SimTrace.cs`

Gates the `[move]`/`[action]` transition prints. Default on for desktop, off in browser
and in the bench. Output-only — it cannot desync a match or change a replay.

## Suggested order of work

1. **Add a convergence test** (Finding 1c) — `Ftol` already exists, default off. Measured
   1.4–3.7× on the static path for a 0.05° pose change. Five lines, biggest single win.
   Cadence path needs a visual check first (Δφ drift), so start with `SolveStaticPose`.
2. **Adopt the MINPACK structure** (Finding 1b) — QR of J instead of normal equations,
   `lmpar`-style Givens retries, gain-ratio trust region. Deletes the hot loop rather than
   optimizing it, and fixes the conditioning.
3. **Then vectorize whatever remains** — `Vector<float>`, BCL only, 2.5× native / ~1.5–2×
   in-browser. Last, not first.
4. **Trim rows and evaluations** — mask-indexed no-pen rows, hoist the invariant
   sample+compose out of the static path, and understand why idle burns ~10 iterations.
5. **Read the browser profile.** Publish, press F11, and let the per-pass dump decide
   between `present`, `skins`, and `anim` rather than guessing.
6. **Make `GlowTrailField` advance lazily** before anyone turns it on.
7. **Per-tile culling in `ChunkRenderer`** if the zoom or tile-size experiments land.
8. **Wire `--check` into whatever runs before a publish**, so the numbers above stay true.

### Finding 1d — the fix is double, and NOT for the reason we predicted (2026-08-17)

Two candidate explanations for why the normal-equations path lands on a worse answer than QR,
and they make different predictions:

- **within-sum cancellation.** The loss weights span ~5..5000, so the SQUARED weights entering
  each dot product of JᵀJ span ~1e6. Low-weight prior terms fall below the float32 epsilon of
  the hard-constraint terms in the same sum and are dropped. Predicts: a wide ACCUMULATOR fixes
  it, and the result survives rounding back to float32.
- **squared conditioning.** cond(JᵀJ) ~ 1e6 means float32's 1e-7 relative representation error
  admits an O(0.1) relative error in the solved step, no matter how the entries were computed.
  Predicts: nothing that ends with a float32 JᵀJ can help.

`LeastSquaresSolver.DoubleAccum` (accumulate + factor in double) and
`DoubleAccumRoundToFloat` (accumulate in double, round to float32, factor in float) separate
them. `MTile.Bench -- --double`, Jacobi scaling forced off so all four routes share one
damping metric:

| scenario | cost f32 | cost f64 | f64→f32 | cost QR | iters f32>f64>f64→f32>QR |
|---|---|---|---|---|---|
| biped/idle | 0.1757 | 0.1757 | 0.1757 | 0.1757 | 4>4>4>4 |
| **biped/run** | 0.2526 | **0.0235** | 0.2573 | 0.04757 | 8>**2**>6>3 |
| biped_rabbit/idle | 0.1371 | 0.1371 | 0.1371 | 0.1371 | 3>3>3>3 |
| biped_rabbit/run | 0.4068 | **0.3344** | 0.4061 | 0.4088 | 6>6>3>6 |

**The cancellation hypothesis is refuted.** Double accumulation rounded back to float32
recovers nothing (0.2573 against 0.2526 — noise). The entire win comes from the double
FACTORIZATION, i.e. from never holding JᵀJ in float32 at all. This is the same defect QR
addresses; double simply out-muscles it, since 1e6 × 1e-16 leaves ten digits of headroom
where 1e6 × 1e-7 leaves none.

Two consequences worth keeping:

- **Double NE beats QR on quality** (0.0235 vs 0.04757 on the worst scenario, in 2 iterations
  rather than 3) — float QR keeps cond(J) ~1e3 against float32's 7 digits, while double NE
  eats cond(JᵀJ) ~1e6 against double's 16. The squared condition number is not the thing to
  avoid; running out of digits is.
- **And it is cheaper than QR**: on `biped/run` 389 µs vs 509 (f32 = 308). Caveat on that
  column — the f32 path is vectorized and the double path deliberately is not (lane-splitting
  would have confounded accumulator width with reordering), so some of the f32→f64 gap is lost
  SIMD rather than double arithmetic. `Vector<double>` at half the lane count would recover
  part of it, and matters more in wasm, where lanes are 4 rather than AVX2's 8.

Note the idle rows: identical cost, ~1.7–2.0× the time. Double is pure loss where conditioning
is fine, which argues for making this conditional rather than global — `LastDiagRatio` is
already computed each iteration and is the natural trigger.

### Finding 1e — the 1e6 condition number was ONE ROW (2026-08-17)

`MTile.Bench -- --columns` dumps the JᵀJ diagonal per column on the worst-conditioned
`biped/run` frame, split by which constraint block contributed it. The result is not diffuse
ill-conditioning:

```
col                      diag         geom        prior  geom share
dphi                8.644E+05        6.371    8.643E+05       0.0 %
delta_y                0.8349       0.7856       0.0493      94.1 %
th:arm_l_upper          14.11            0        14.11       0.0 %
                 ratio max/min:    1.035E+06
```

The whole 1.035e6 is `dphi` (8.64e5) against `delta_y` (0.83), and 99.999% of that column is
contributed by ONE row: `PhaseRateFloorConstraint`, whose Jacobian is `√λ/floor`. With
λ = 60 and floor ≈ 0.008 (one frame's phase step) that entry is ~930, and 930² = 8.6e5. The
row's residual is dimensionless as its comment claims; its *sensitivity* is O(1/floor).

This also explains the double-precision result. That column is 8.643e5 of prior plus **6.371
of geometry** — in float32 the sum's ulp is ~0.06, so the geometric part survives with ~1%
relative precision. A 1e5 magnitude ratio inside a single dot product, which is why only
keeping JᵀJ in double recovered it (the loss is in the float32 STORAGE of the sum, not the
accumulator), and why Jacobi scaling could not: scaling the column multiplies the floor row
and the geometric rows by the same factor, leaving the ratio inside the sum untouched.

`AnimSolverConfig.PhaseFloorMode` switches the enforcement: 0 = relative (historical),
1 = absolute `√λ·(floor − Δφ)`, 2 = box (row deleted, Δφ's lower bound raised instead).

| mode | diag ratio | dphi diag | dphi geom share |
|---|---|---|---|
| 0 relative | 1.035e6 | 8.64e5 | 0.0% |
| 1 absolute | 5613 | 5932 | 99.9% |
| 2 box | 5587 | 5904 | 99.9% |

**185× better conditioning, and the column becomes geometry-dominated.** On
`biped_rabbit/run` all four numerical routes now agree to 0.3% (f32 0.3352, f64 0.3354,
f64→f32 0.3347, QR 0.3356) where they previously spread 1.2× — the precision sensitivity on
that scenario is gone outright.

**Mode 1 is not free**, and the config comment says so: matching mode 0's push requires
λ_abs = λ_rel/floor², which puts the Jacobian back where it started. Mode 1 at λ = 60 is a
genuinely weaker floor, so the "locked mid-flight" collapse it was written to prevent needs
re-checking in game — a steady-state bench cannot reproduce that transient. Mode 2 keeps the
constraint strict but measured WORSE on the shared rows (`biped_rabbit/run` 0.4075 against
mode 1's 0.3352), i.e. forcing Δφ ≥ floor exactly costs the geometric constraints.

**`biped/run` is a separate problem and is NOT fixed by this.** It still spreads 7–9× across
the four routes in every mode. Dumping the solved Δφ shows why: f32 0.0338, f64 0.0174,
f64→f32 0.0275, QR 0.0207 — a 2× spread in the cadence rate itself, not jitter around one
answer. `SolvePhaseStepLm`'s own comment says the objective is NON-CONVEX in Δφ, and the seed
search picks one basin from 10 samples. This is basin selection, not precision, and it means
the earlier "double finds a 10× better answer on biped/run" was partly luck of the draw.

Timing was not measured here: the mode sweep runs the three modes in one process, so mode 0
absorbs JIT warmup and its µs are not comparable to the others'.
