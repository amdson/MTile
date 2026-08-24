# Lattice Path Planner (todo #2)

Status: **plan only — nothing built.** Written 2026-08-23 against `main` @ cc61c31.

A short-horizon, configuration-space path planner to replace the hand-written
reference-generation rules in the stand fold. The path is found by dynamic
programming over a spatial lattice ordered into shells by progress along the
requested direction; a small QP then picks forces to track it.

The trade the design accepts: **the path search knows geometry, not dynamics.**
It does not carry velocity, does not model per-point force availability, and
cannot represent a trajectory that doubles back or goes vertical. In exchange it
is a dense fixed-size DP — deterministic, allocation-free, and roughly three
orders of magnitude cheaper than the state-space search prototype.

---

## 1. The good news: this replaces one function, not the corrector

`FoldReference.TryApply` (`Character/Corrector/FoldReference.cs`, the
`FoldEngine = "ref"` engine) already has exactly the shape this algorithm wants:

```
  rollout   →  rows  →  deform  →  servo tick-0
  (:81-121)    (:131)   (:152)     (:190+)
```

The **rollout** block — 40 lines — is the hand-written reference generator:

- x carries at `dir · MaxSpeed`, ramped by `WalkAccel`;
- y tracks `FloorEnvelope − HoverOffset`, descending no faster than gravity and
  rising no faster than `SpringMaxRiseSpeed`;
- the climb band is clamped to `anchorY ± ClimbReachUp / SupportReach`;
- frontal walls are **classified** at row emission (`wallEscapeUp` /
  `wallEscapeDown`) into duck / step / give-up.

Every one of those bullets is a rule the lattice DP subsumes: the climb band and
descent-rate limit become a steepness bound on edges, wall classification becomes
plain admissibility, and the give-up becomes "no path exists through this shell."

**So the change is: swap the rollout for the DP, keep rows + deform + servo
unchanged.** Rows, the `PathDeform` position-offset channel, the vertical axis
lock, the servo, `CorrectorLedger` attribution and the debug capture buffers all
carry over untouched. Ship it as a fourth `MovementConfig.FoldEngine` value,
`"lattice"`, beside `qp` / `ref` / `lm`, so it can be A/B'd by hot-reload during
a playtest without touching the shipped path.

This is by far the lowest-risk framing available and it should be the plan of
record. Building a new top-level corrector instead would put the deform, the
ledger, the anti-autopilot axis lock and the trajectory capture all back on the
table for no gain.

### 1a. What the existing `LatticePlanner.cs` is, and why it is not this

`Character/Corrector/LatticePlanner.cs` is a **state-space** search: nodes carry
exact `(pos, vel)`, edges integrate 3 ticks of real dynamics under a constant
force, and enumeration is over sampled force outcomes. Measured today
(`MTile.Tests/Sim/ZzzLatticeTiming.cs`, this machine):

| scenario | µs/solve | alloc/solve |
|---|---|---|
| flat walk | **37,869** | 622 KB |
| tunnel | **15,613** | 172 KB |

For scale, from `MTile.Bench/baseline.txt`: a whole sim step is 25–184 µs mean
(651 µs worst), and a rollback frame at window 8 is 250 µs. The prototype is
~200× the worst-case *entire sim step*, and it allocates on the hot path.

That cost is intrinsic to searching state space, and it is precisely what todo #2
proposes to give up. **Do not try to evolve `LatticePlanner.cs` into this.**
Write the new planner clean; keep the old file as a freeze-frame oracle (it is
already wired into `Game1.cs:448` beside the LM oracle) or delete it.

---

## 2. Sizing — the plan window is spatial, not the tracking horizon

Real numbers from the codebase:

| quantity | value |
|---|---|
| `Chunk.TileSize` | 16 px |
| `PlayerCharacter.Radius` | 12 px (hexagon) |
| `MovementConfig.MaxWalkSpeed` | 100 px/s |
| `MovementConfig.AmbientHorizon` | 10 ticks = 0.167 s |
| `FoldHoverOffset` / `FoldClimbReachUp` / `SupportReach` | 10 / 20 / 25 px |

### 2.1 Decouple the planning window from the tracking horizon

The ref rollout's x-ramp is clamped at `MaxWalkSpeed` (`FoldReference.cs:84`),
so over the 10-tick tracking horizon the body advances at most **16.7 px — one
tile.** A lattice sized to the tracking horizon would be five shells wide and
could not see a two-tile block coming.

That is not a problem, because the path is a *height field over x*: it can be
planned further ahead than the servo will ever track. So:

- **Plan window** (spatial, a config knob): `LatticeLookaheadTiles` ahead of the
  body along `dir`, ± `LatticeBandTiles` vertically around the current support
  anchor. Start at **3 tiles ahead, ±2.5 tiles**.
- **Tracking horizon** stays `AmbientHorizon` = 10 ticks; §3.6 consumes only the
  first ~17 px of the plan.

This also relocates the autopilot dial of §4.2: it is the *spatial* lookahead,
not the tick count. Three tiles ahead is enough to route a hop over a one-tile
block or under a lip; it is not enough to plan a route, which is the intended
ceiling.

### 2.2 Resolution and node count

Uniform **5× tile** (3.2 px cells) is fine — the anisotropy the first draft of
this plan argued for was built on a bad derivation and is withdrawn (see §3.3).
3.2 px sits under the scales that matter (`FoldHoverOffset` 10 px, a tile step
16 px, the C-obstacle corner bevels at ~4 px granularity via
`CObstacleTemplate.TopSurfaceRy`), so the bitmap resolves every feature the
hover reference can see.

At 3 tiles × ±2.5 tiles × 5×: **15 shells × 50 rows = 750 nodes**, ~5 edges
each (§3.3) ≈ **3,750 transitions**. Four tiles ahead is 1,000 nodes. Both well
inside the todo's 5,000-point budget, and a dense-array sweep at that size
should land in the **single-digit µs** — a small fraction of the current 25–184
µs sim step rather than a multiple of it.

Cell size and both window extents should be config knobs from the first commit;
phase 0 (§5) exists largely to look at the bitmap at a few settings before
committing to any of them.

---

## 3. Concrete design

### 3.1 Frame and shells

- **Progress axis** `u` = the requested direction, in practice `±x` from
  `ctx.Intent.CurrentHorizontal`. Shells are columns perpendicular to `u`.
- Shell `s ∈ [0, S]`, `S = LatticeLookaheadTiles · TileSize / cell` ≈ 15 at
  the §2.2 settings.
- Within a shell, rows index `j` over the vertical band (~50 of them).
- The graph is a DAG by construction: **every edge advances exactly one shell.**
  Acyclicity is free; no topological sort needed, and the DP is a single sweep
  `s = 0 … S`.

Because every edge advances `u`, the result is a **height field `y(x)`, not a
general curve.** See §4.7 — this is the load-bearing limitation.

### 3.2 Precomputation (once per solve, not per node)

Two arrays, both dense and pooled in `CorrectorScratch`:

1. **Admissibility bitmap.** Stamp the `CObstacleTemplate` at every solid tile
   overlapping the window, marking lattice cells inside the C-obstacle as
   blocked. Cost ≈ (solid tiles in window) × (cells per C-obstacle footprint).
   The template's `Reach` is ~20 px, so the footprint is ~40 px across: at 3.2 px
   cells that is ~13 × 13 ≈ 170 marks per tile, ~40 tiles in the window → ~7k
   marks. Cheap, and it is the *correct* use of the template.
2. **Floor envelope per shell.** One `AmbientCorrector.FloorEnvelope` call per
   *column* (~15 calls), not per node.

Getting this wrong is the difference between "fast" and "unusable":
`FloorEnvelope` walks facets over gathered tiles, and calling it per node — which
is what the state-space prototype does — is a large part of why it costs 38 ms.

### 3.3 Edges

From `(s, j)` to `(s+1, j')`, with `|j' − j| ≤ K`. `K` is a **geometric** slope
bound — a config knob, not a derived quantity — and it is the same constraint the
todo phrases as a cosine threshold on the requested direction (§4.5). `K = 2`
admits slopes up to 2 (≈ 63° off horizontal) per edge, which is enough to hop a
one-tile block within a tile of travel; `K = 1` would cap the path at 45°.

**The first draft of this plan derived `K` from `SpringMaxRiseSpeed` and the
current `vx`, and that was wrong on three counts**, recorded here so it is not
re-derived:

1. It floored `rise-per-shell / cellH` to an integer per edge, which reads
   "less than one cell of rise per shell" as "cannot climb." Slope is a property
   of the *path*, not of one edge — 0.8 cells per shell is simply Δj = +1 on
   four shells out of five.
2. `SpringMaxRiseSpeed` (80 px/s) is the launched-vs-supported *classification
   gate* — Standing's entry test, `BallisticPredictor`'s grounded test — which
   the ref engine also adopted as a self-imposed cap on its reference. It is not
   the legs' capacity (`FoldLegForce` is 6000 px/s²), and reusing the ref
   engine's honesty rules is exactly what the DP is meant to stop doing.
3. It depended on the current `vx`, which drops during a step-up (unilateral
   drive, wall contact) — so the bound would have been wrong precisely at the
   moment it mattered.

The todo's contract is that the path carries no velocity, and this plan now
honours it: the only velocity term anywhere is the seed bias of §3.5. Whether
the legs can actually deliver a given slope is not the planner's question — it
is answered after the fact by the tracking-residual give-up of §4.3, which is
the mechanism that already exists for it.

Edges per node = `2K + 1` ≈ 5. Asymmetry (steeper descents than climbs) is a
cost-weight matter (§3.4), not an admissibility one.

### 3.4 Cost

| term | form | notes |
|---|---|---|
| **(C) hover** | `w_hover · (y_j − (env_s − hoverOffset))²` | per node; `env_s` from the column cache |
| **(D) admissible** | hard prune | blocked cell ⇒ no node |
| **(B) steepness** | `w_steep · Δj²` | per edge, **needs no state** |
| **(A) direction** | implicit | shell monotonicity already forbids backward motion |

Note what is *not* here: a curvature term. See §4.4.

The DP is the textbook sweep:

```
dp[0][j] = seed cost                        // §3.5
for s in 0..S-1:
  for j in shell s:
    for j' in [j-K, j+K]:
      if blocked[s+1][j']: continue
      c = dp[s][j] + w_steep(Δj)·Δj² + w_hover·(y_j' - ref_{s+1})²   // w_steep may differ for Δj > 0 vs < 0
      if c < dp[s+1][j']: dp[s+1][j'] = c; parent[s+1][j'] = j
```

~750 nodes × 5 edges ≈ **3,750 transitions** at the §2.2 settings. Dense float
arrays, almost no branching — this should land in the **single-digit µs**, i.e. a
small fraction of the current 25–184 µs sim step rather than a multiple of it.

### 3.5 Seeding with the current velocity

The todo notes this is necessary, and it is the one place a pure spatial DP has
no natural answer. The workable version:

- Seed shell 0 at the body's actual cell with cost 0.
- **Bias, do not hard-restrict,** the first edge toward the current velocity:
  `dp[1][j'] += w_seed · (Δj − Δj_vel)²`, where `Δj_vel` is the slope the body's
  present velocity implies over one shell.

Hard-restricting the first edge is tempting and wrong: a body descending fast has
`Δj_vel` outside `K`, and the restriction would empty shell 1 and fail the whole
solve. A quadratic bias degrades gracefully.

### 3.6 Time parameterization — already written

The DP gives `y(x)`. The tracker needs `p_k` per tick. `FoldReference`'s rollout
already computes `x_k` per tick from the walk-accel ramp
(`FoldReference.cs:82-85`), and that block stays. So:

```
x_k = <existing walk-accel ramp>
y_k = latticePath(x_k)          // linear interp between shells
```

That is the whole time parameterization. Over 10 ticks at ≤ `MaxWalkSpeed` the
ramp covers ≤ 17 px, so the tracker only ever consumes the first tile of a
three-tile plan; the rest exists so the first tile is chosen with foresight
(§2.1). The `Grounded` / `FloorY` fields the
downstream rows and channels read are filled from the column caches. **This is
why the integration point in §1 matters so much: the piece the todo's algorithm
does not produce is the piece the existing code already computes.**

### 3.7 Tracking

The todo asks for "a simplified version of the current QP." The honest
simplification is that **the tracker gets dumber as the path gets smarter** —
that is the whole point of moving intelligence into the path.

- **v1: reuse `CorrectionProblem` exactly as `FoldReference` already does** —
  one `PathDeform` position-offset channel, vertical axis lock, `SlewCap`, then
  servo tick-0. Zero new solver code.
- Only if that proves insufficient, expand to a small per-tick least-squares over
  the available force ops (`BuildFold`'s stack minus the redirect disc).

Do not write a new QP for v1. `CorrectionSolver` already does this, and the
deform channel is already the right shape.

### 3.8 Debug export

`CorrectorScratch` already carries `ReferenceTrajectory` / `BallisticTrajectory`
/ `SolvedTrajectory` behind a `CaptureTrajectories` gate, and `Game1` already
draws oracle paths in the freeze-frame inspector. Publish the lattice path into
the reference buffer and it is drawn for free. Worth adding beyond that: the
per-shell chosen `j` and the blocked bitmap as an overlay — the bitmap is where
the resolution bugs will show up.

---

## 4. Critical input

### 4.1 Cost weights are the actual work, and there is no gradient to guide you

The DP is a day. Making `w_hover` / `w_steep` / `w_seed` trade off so the
character *feels* right is the job, and it is pure playtesting — no test will
tell you the weights are wrong, only a feel that the character is bobbing, or
cutting corners, or refusing a ledge it should take.

**Make every weight hot-reloadable in `configs/movement_config.json` from the
first commit**, exactly like the existing `Fold*` knobs. This is the single
largest schedule risk in the plan, and the mitigation is entirely about iteration
speed.

### 4.2 This *is* autopilot; the lookahead is the dial

The corrector carries explicit anti-autopilot doctrine — no channel may push
against held input (`CorrectorChannels.BuildFold`'s unilateral Drive), the deform
is vertical-only (`FoldReference.cs:150`, with a measured corridor-mouth stall
behind that comment), elective climbs refuse wholesale when undeliverable
(`AmbientCorrector`'s `ElectiveTol` / latch machinery).

A planner that routes around obstacles violates the spirit of all of it.

- The **x half is safe for free**: shell monotonicity means the path can never
  brake or reverse. That is a stronger guarantee than the unilateral channels
  give today, and it is a genuine argument *for* this design.
- The **y half is bought with lookahead.** The dial is `LatticeLookaheadTiles`
  (§2.1), not the tick count: the servo only ever tracks the first tile, but the
  path chooses that tile knowing what is three tiles out. At three tiles a routed
  hop reads as a reflex that happened to be well-timed. At eight it reads as the
  game playing itself.

**Resist extending the lookahead to "make it smarter."** If it looks dumb at
three tiles the fix is the weights, not the window. Any lookahead change should
be a deliberate, playtested decision with that framing written down.

### 4.3 Elective refusal has no equivalent, and you will notice

Today an undeliverable climb is refused *as a whole* — R1 collapses to R0 and the
body runs honestly into the wall. The DP has no such notion: it always returns a
best path, including one the legs cannot possibly track, and the servo will chase
it and produce a mushy half-climb.

**Mitigation:** after the servo, measure the tick-0 tracking residual against the
path; when it exceeds tolerance for N consecutive frames, fall back to the flat
carry (the honest bonk) for a refusal window. This is `ElectiveTol` /
`ElectiveCommitFrames` / `ElectiveRefuseFrames` reapplied one level up, and those
constants are a reasonable starting point. Budget for this — it is not optional
polish, it is what keeps walls feeling solid.

### 4.4 Skip direction state in v1

Adding "incoming edge direction" to the node state to penalize curvature is a
**5× state blowup** (one state per `Δj` value at `K = 2`): ~750 nodes → ~3,750
states, ~3,750 transitions → ~19,000. That likely lands at 30–80 µs/solve, and
with two players × a rollback window of 8 that is on the order of 1 ms/frame on
top of the current 250 µs rollback frame. Real, not fatal, but not free either.

You probably do not need it. Two things already produce smooth paths:

1. the `Δj²` steepness penalty, which needs no state and punishes exactly the
   large-amplitude zigzags that look bad;
2. the reference being tracked is the **bevel-smoothed** floor envelope, so the
   attractor is already smooth.

**Build it without direction state, look at the exported path, and add the state
only if visible zigzag survives.** If it does, note that the cheaper fix is
usually a post-hoc smoothing pass over the recovered path, not a bigger DP.

### 4.5 The cosine threshold and the slope bound are the same knob — keep one

The todo proposes filtering edges by cosine distance from the requested
direction. On a shell-ordered lattice that is exactly the per-edge `|Δj| ≤ K`
bound of §3.3: a cone of half-angle θ about horizontal is `K = tan θ` in cells.
Backward motion is already excluded by shell monotonicity, so the cone's only
remaining job is the slope cap.

Keep the integer `K`; it is cheaper and it is what the DP loop actually indexes
by. Note the cone must be loose — hopping a one-tile block at walk speed is
45–60° off horizontal — so a "tight" cosine threshold in the todo's sense would
forbid the hops the planner exists to find.

### 4.6 `dir == 0` has no progress axis

The fold deliberately runs *unconditionally*, with no input gate, because hover
must hold at rest — `AmbientCorrector`'s header calls out the `vx = 0` liveness
deadlock this fixed. A direction-ordered lattice has no shells when there is no
direction.

**Rule:** below a small target-speed threshold, skip the DP entirely and use a
pure hover column (`y = env − hoverOffset` at the current x), tracked by the same
servo. One branch, but it is a real hole in the algorithm as written, and it
covers a state the player is in constantly.

### 4.7 Regime scope: inherit `FoldReference`'s guards verbatim

`TryApply` already returns false for knockback (`PreserveExternalVelocity`),
launched (`-vy > SpringMaxRiseSpeed`), plunging (`vy > MaxGroundEngageVnRel`),
and unanchored (no floor within `SupportReach`) — falling back to the ballistic
QP path. Keep every one of those.

They are exactly the regimes where a velocity-blind height field is wrong: a fall
is near-vertical (unrepresentable as `y(x)`), and a launch has momentum the path
cannot see. **The planner owns the supported / near-support regime and nothing
else.** That is not a temporary limitation to remove later; it is the scope that
makes the algorithm sound.

### 4.8 Determinism — an improvement, but be deliberate

Requirements (`CLAUDE.md`'s sim rules): no sim-affecting mutable statics, no
hardware polling, identical iteration order on restore, and — because the solve
runs inside `Simulation.Step` — it is replayed on every rollback frame.

A dense-array DP over fixed-size shells satisfies all of this naturally, with no
allocation. Note that the existing `LatticePlanner` does **not**: it iterates
`Dictionary.Values` (`frontier.AddRange(next.Values)`) and allocates 622 KB per
solve. Same-build peers make that survivable today, but it is a genuine hazard
and another reason not to evolve that file.

All scratch goes in `CorrectorScratch` (pooled per player, never snapshot). No
new fields in `MovementVars` unless the refusal latch of §4.3 needs one — and if
it does, it must be snapshot-covered.

### 4.9 Commit up front to what dies

Shipping this makes **four** fold engines: `qp` (default, test-pinned), `ref`,
`lm` (oracle), `lattice`. That is four parallel implementations of one mechanic
to keep compiling and correct, and `Plans/` already shows what happens when
prototype engines accumulate.

Stated intent, written down now rather than discovered later:

- `lm` stays as the offline oracle — already documented as too heavy for the
  rollback loop.
- If `lattice` works it should replace **both** `ref` and `qp`: `ref`'s rollout
  is precisely what it replaces, and `qp`'s channel stack becomes the tracker.
- If it does not work, delete it. Do not leave a fourth engine parked.

### 4.10 Where this plan deviates from the todo

One deliberate deviation. The todo passes "a list of operations available based
on player movement state and environment" **into the path solve**. This plan
keeps that list out of the path entirely and uses it only to configure the
tracker (§3.7).

Reason: knowing which ops are available *at a node* requires knowing whether that
node is near ground, which is a per-node `FloorEnvelope` query. That is exactly
the per-node dynamics lookup that makes the state-space prototype cost 38 ms.
The path is geometry; whether it is deliverable is the tracker's and the
give-up's problem (§4.3).

---

## 5. Phasing

| phase | deliverable | gate |
|---|---|---|
| **0** | Lattice geometry: window, admissibility bitmap, per-shell envelope cache. Drawn in the freeze-frame inspector. No DP, no sim wiring. | bitmap visually matches terrain across a few shell/row resolutions |
| **1** | The DP (no direction state) + path export. Oracle-only, run beside the LM/lattice oracles in `Game1`. | µs/solve measured in `MTile.Bench`; path looks sane over lips, corridors, 1- and 2-tile blocks at `K = 1` and `K = 2` |
| **2** | Wire as `FoldEngine = "lattice"`: replace `FoldReference`'s rollout block, keep rows + deform + servo. Default stays `qp`. | `FoldRefEngineTests`-style scenario tests pass for the new engine |
| **3** | Weight tuning by playtest; the tracking-residual give-up of §4.3. | walls feel solid; no bobbing at rest; no visible autopilot |
| **4** | *Only if needed:* direction state for curvature. Re-measure. | zigzag actually visible in the export first |
| **5** | Decide the fate of `qp` / `ref` / old `LatticePlanner.cs` per §4.9. | one engine fewer than you started this phase with |

Phase 0 is deliberately first and deliberately dumb. The resolution question
(§2), the weight problem (§4.1) and the bitmap-correctness bugs are all cheapest
to find with a picture and no sim wiring.

## 6. Tests

- Pattern to copy: `MTile.Tests/Sim/FoldRefEngineTests.cs` and
  `FoldScenarioTests.cs:219` — flip `MovementConfig.Current.FoldEngine`, run a
  headless `SimRunner` scenario over ascii terrain, assert, **restore**.
- Restoration is mandatory: assembly-wide test parallelization is off precisely
  because `MovementConfig.Current` is process-wide (`TestAssemblySetup.cs`).
- The suite pins `qp` as the default. Do not change that default until the new
  engine wins on feel, not on tests.
- Add to `MTile.Bench` (`CorrectorDiag.cs` is the existing hook) so µs/solve is
  regression-checked against `baseline.txt` from phase 1 onward.
- Determinism: extend `CorrectorSnapshotTests` to cover a rollback across a
  lattice solve.
