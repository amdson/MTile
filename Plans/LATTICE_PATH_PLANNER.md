# Lattice Path Planner (todo #2)

Status: **plan only — nothing built.** Written 2026-08-23 against `main` @ cc61c31.

A short-horizon, configuration-space path planner to replace the hand-written
reference-generation rules in the stand fold. The path is found by dynamic
programming over a world-aligned spatial lattice whose edges are filtered by a
cone around the requested direction — which makes the graph a DAG, ordered by
progress along that direction; a small QP then picks forces to track it.

The trade the design accepts: **the path search knows geometry, not dynamics.**
It does not carry velocity, does not model per-point force availability, and
cannot represent a trajectory that doubles back against the requested direction.
In exchange it
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
plain admissibility, and the give-up becomes "no admissible path reaches the
far side of the window."

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
tile.** A lattice sized to the tracking horizon would be five cells wide and
could not see a two-tile block coming.

That is not a problem, because the path is spatial: it can be planned further
ahead than the servo will ever track. So:

- **Plan window** = the **cone's footprint from the seed**: the bounding box of
  `{ seed + t · R(±φ) u : t ∈ [0, L], |φ| ≤ θ }`, i.e. `L` along `u` and
  `±L · tan θ` across it. `L` (`LatticeLookaheadTiles`) is the one knob; the
  cross extent is *derived* from `L` and `cos θ` (§3.3), never set separately —
  a band narrower than the fan would clip the cone and silently forbid the
  steep routes the cone was opened to admit. At `L ≈ 3.5` tiles and a 45° cone
  that is a **7 × 7 tile** box, which is the right mental size for this.
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

Box and fan sizes at `L = 3.5` tiles, 5× cells, `u = +x`:

| `cos θ` | half-angle | box (tiles) | box cells | fan cells (≈ ½ box) |
|---|---|---|---|---|
| 0.7 | 45° | 3.5 × 7 | ~600 | ~300 |
| 0.5 | 60° | 3.5 × 12 | ~1,050 | ~530 |
| 0.3 | 72° | 3.5 × 22 | ~1,900 | ~970 |

The box is what gets allocated (pooled, fixed-size — pick a `LatticeMaxCells`
the scratch arrays are sized to, ~4k, and clamp the box to it); the **fan is
what the DP actually visits**, after the reachability pass of §3.2 discards
everything outside the cone from the seed and everything behind an obstacle.
Every row is inside the todo's 5,000-point budget, and a dense-array sweep over
a few hundred to a thousand nodes should land in the **single-digit µs** — a
small fraction of the current 25–184 µs sim step rather than a multiple of it.

Cell size and `L` should be config knobs from the first commit;
phase 0 (§5) exists largely to look at the bitmap at a few settings before
committing to any of them.

---

## 3. Concrete design

### 3.1 Lattice, direction, and ordering

**A node is a candidate position of the body center** — one cell of the
world-aligned grid, nothing more. It carries no velocity, no time, and no
incoming direction. `center(n)` is the world point the body center would
occupy; a node is admissible iff that center lies outside every stamped
C-obstacle (the hexagon overlaps no solid tile there); `dp[n]` is the cheapest
cone-admissible polyline from the seed (the cell the body center is in now) to
`center(n)`; `parent[n]` is the previous body-center position on it. The
recovered path is therefore a chain of body-center positions, and time is
stamped onto it afterwards (§3.6). This is the whole difference from the
state-space prototype, whose nodes are `(pos, vel)`.

- **The lattice is world-aligned** (tile-aligned, cell = `TileSize / 5`), not
  aligned to the requested direction. This is what makes the todo's "hexgrid
  later" a one-table change (§3.3) and what lets `u` be any direction.
- **`u` is a unit 2D direction supplied by the state**, not `±x`. Standing /
  crouched pass `(±1, 0)`; a jump state would pass up or up-diagonal; a drop
  passes down. The planner does not know what a state is, only its `u`.
- **Ordering is by projection onto `u`**: `p(node) = dot(cellCenter, u)`. Every
  admitted edge strictly increases `p` (§3.3), so the graph is a DAG and a
  single pass over nodes sorted by `p` (index tie-break, for determinism) is a
  valid DP order. This is the todo's "shells by depth in the DAG" — projection
  bands are the shells, and no explicit topological sort is needed.

The path is **monotone along `u` and otherwise free**: with `u = +x` it can
rise steeply at a wall face and then run along the top; with `u` diagonal it
can alternate up and across. It is *not* a height field `y(x)` — the first
draft of this plan had that constraint by carrying the ref engine's `y = f(x)`
rollout shape into the lattice, and it was wrong: it forced a wall climb to
start a tile early at a fixed slope, and it could not express a jump at all.

### 3.2 Precomputation (once per solve, not per node)

Two arrays, both dense and pooled in `CorrectorScratch`:

1. **Admissibility bitmap.** Stamp the `CObstacleTemplate` at every solid tile
   overlapping the window, marking lattice cells inside the C-obstacle as
   blocked. Cost ≈ (solid tiles in window) × (cells per C-obstacle footprint).
   The template's `Reach` is ~20 px, so the footprint is ~40 px across: at 3.2 px
   cells that is ~13 × 13 ≈ 170 marks per tile, ~50 tiles in a 7 × 7 box →
   ~8k marks. Cheap, and it is the *correct* use of the template.
2. **Distance to the surface below, per node.** One bottom-up sweep per
   x-column of the bitmap: `floorBelow[n]` = distance from `center(n)` down to
   the first blocked cell in its column (∞ if none in the window). One op per
   cell of the box. Because the bitmap *is* the C-obstacle — facets, bevels and all —
   this is the floor envelope, evaluated at every node instead of once per
   column, and it is **multi-floor aware**: a ledge top and the ground beneath
   it are both found, which a single-band `FloorEnvelope` query is not.

   The hover term (§3.4) reads it as `floorBelow[n] − hoverOffset`, and the
   downstream `Grounded` / `FloorY` fields (§3.6) are filled from it. No
   `FloorEnvelope` calls at all. The x-column sweep stays x-indexed whatever
   `u` is — floors are floors.
3. **Reachability prune.** A forward flood from the seed over admissible edges
   (§3.3 conditions, bit ops only — no costs), producing a `reachable` bitmap
   and a compact node list. This discards, before the DP runs, everything
   outside the cone-fan from the seed (roughly half the box — the fan is a
   triangle in a rectangle) and everything behind an obstacle. Three things it
   buys:
   - the sort in §3.4 runs over the compact list, not the box;
   - an **early-out**: if no far-band node is reachable, the answer is the
     bonk (§3.4) and the DP is skipped entirely;
   - the overlay (§3.8) can show "reachable" as its own layer, which is the
     first thing to look at when the path does something odd.

   Honest note: the DP's own `dp[n] = ∞` skip already prunes implicitly, so the
   pass is an optimization and a diagnostic, not a correctness step. A
   *backward* prune (nodes that cannot reach the far band) is possible too and
   would shrink the DP further; it never changes the answer, so leave it for
   the profiler to ask for.

Getting this wrong is the difference between "fast" and "unusable": the
state-space prototype's per-node `FloorEnvelope` calls (facet walks over
gathered tiles) are a large part of why it costs 38 ms. Here the equivalent
information is a subtraction per node off a precomputed column sweep.

### 3.3 Edges: a neighborhood table filtered by a cone

An edge is a lattice offset `(dx, dy)` from a fixed **neighborhood table**,
admitted iff

```
dot( normalize(dx, dy), u )  ≥  cos θ,     with  cos θ > 0
```

That is the *cone* condition; the full edge condition `n → m`, `o = m − n`, is:

1. **Neighborhood** — `o` is in the primitive-offset table below.
2. **Cone** — `dot(ô, u) ≥ cos θ`, `cos θ > 0`.
3. **Geometry** — `m` in the window, `m` admissible, and every cell the segment
   `center(n) → center(m)` crosses admissible (tunneling, below).

That is the whole condition: **edges are pure geometry.** Nothing about the
edge asks whether the body could actuate it — no support check, no force
availability, no velocity. A draft of this plan added a fourth condition
(rising edges only from nodes within reach of a floor) and it was deliberately
taken out: the path's job is to say where the body would *like* to go, and
whether the legs can deliver it is the tracker's question (§3.7) and, failing
that, the give-up's (§4.3). Keeping actuation out of the graph keeps the graph
one thing, and keeps every "why won't it climb this" answerable from the
bitmap and the cone alone. Revisit only with a specific failing scenario in
hand.

`cos θ` is the todo's cosine threshold and is a config knob. **`cos θ > 0` is
structural, not tuning:** it is what makes every edge strictly increase `p` and
therefore what makes the graph a DAG. Admit a perpendicular edge (`cos θ ≤ 0`)
and a vertical up/down pair forms a cycle; the fix would be a step-indexed DP
(`(node, depth)` states, ~20× the work) and it is not worth it — see the wall
argument below.

- **Neighborhood:** the primitive offsets with `|dx|, |dy| ≤ 2` (gcd = 1, so no
  offset is a multiple of a shorter one): `(±1,0) (0,±1) (±1,±1) (±1,±2)
  (±2,±1)` — 16 offsets. With `u = +x` and `cos θ = 0.3` the cone admits
  `(1,0) (1,±1) (1,±2) (2,±1)` = **7 edges per node**, steepest slope 2
  (≈ 63°). Filtering is done once per solve into a small admitted-offset list;
  the DP loop indexes that list.
- **Tunneling:** an offset longer than one cell can jump a blocked cell. Every
  admitted offset carries a precomputed list of the cells its segment crosses
  (supercover; ≤ 3 cells at radius 2), and an edge is dropped if any is
  blocked. Cheap and exact at this radius.
- **Hex later:** only the offset table and the cell→center map change. Nothing
  else in the planner knows the lattice is square.

**Why slope 2 is enough for the fold's regime.** A pure-vertical segment at a
wall face is the one thing `cos θ > 0` cannot express. It is not needed: a
1-tile ledge's C-obstacle corner is already a bevel ramp (`CObstacleTemplate`),
so the true boundary near a lip is ≈ 45°, not vertical; and a 2-tile wall is
beyond `FoldClimbReachUp` (20 px < 32) — the fold gives up on it *today* and
hands it to the maneuver family (vault / mantle), which this planner does not
replace (§4.7). If playtests want steeper, widen the table to radius 3
(`(1,±3)`, slope 3 ≈ 72°) before touching `cos θ`.

**What is deliberately absent:** any velocity-derived slope bound. The first
draft derived one from `SpringMaxRiseSpeed` and the current `vx`; that was wrong
— `SpringMaxRiseSpeed` is the launched/supported classification gate, not leg
capacity, and `vx` collapses during the very step-up the bound would govern.
The todo's contract is that the path carries no velocity, and the only velocity
term anywhere is the seed bias of §3.5. Whether the legs can deliver a slope is
answered after the fact by the tracking-residual give-up of §4.3.

### 3.4 Cost

| term | form | notes |
|---|---|---|
| **(C) hover** | `w_hover · (floorBelow[n] − hoverOffset)²` | per node, from the column sweep (§3.2); 0 when no floor is in the window. **Hover is a per-solve on/off flag the state passes in** (on for Standing / Crouched, off for jump states) — a jump's `u` has no business being pulled to the floor, and a flag is simpler and more legible than a fade |
| **(D) admissible** | hard prune | blocked cell, or a crossed cell blocked ⇒ no edge |
| **(B) steepness** | `w_steep · (1 − dot(ô, u))` | per edge: cost rises with angle off `u`; may be asymmetric (cheaper below `u` than above — descending is free, climbing is legs) |
| **(A) direction** | implicit | the cone + DAG ordering already forbid backward motion |
| **length** | `w_len · ‖(dx, dy)‖` | per edge; keeps the DP from preferring long diagonals for free |

Note what is *not* here: a curvature term (§4.4).

```
nodes = reachable list (§3.2), sorted by p = dot(center, u)   // deterministic tie-break
if no far-band node in nodes: return bonk
dp[*] = ∞;  dp[seed] = 0
for n in nodes:
  if dp[n] == ∞: continue
  for o in admittedOffsets:               // §3.3, ~7
    m = n + o
    if outOfWindow(m) or blocked[m] or crossesBlocked(n, o): continue
    c = dp[n] + w_steep·(1 − dot(ô, u)) + w_len·|o| + w_hover·(floorBelow[m] − hoverOffset)²
    if c < dp[m]: dp[m] = c; parent[m] = n
```

Goal: the minimum-cost node among those in the **far band** of the window
(`p ≥ p_max − cell`), so the path is rewarded for progress without a per-node
progress term. If the far band is unreachable, take the reachable node of
greatest `p` — that is the honest bonk (§4.3).

A few hundred to ~1,000 reachable nodes × 7 edges ≈ **2,000–7,000
transitions** depending on the cone (§2.2). Dense arrays, almost no branching —
single-digit µs, a small fraction of the current 25–184 µs sim step.

### 3.5 Seeding with the current velocity

The todo notes this is necessary, and it is the one place a pure spatial DP has
no natural answer. The workable version:

- Seed at the body's actual cell with cost 0.
- **Fix the initial direction to the body's actual direction of travel**
  (decided 2026-08-24, superseding the bias-only rule below): quantize `v̂` to
  the nearest admitted offset `o*` and force the path's first
  `LatticeSeedRunPx` (8 px ≈ 2–3 cells) along it — the nodes `seed + j·o*`
  may leave only along `o*`. No node state is added; it is a per-node
  arithmetic check on the DP's existing loop. Guard rails, so the seed is
  never stranded: the run applies only when the body is moving
  (`LatticeSeedRunMinSpeed`, 20 px/s) *and* `v̂` is representable in the cone
  (`dot(o*, v̂) ≥ 0.85` — a vertical fall under a horizontal `u` is not forced
  into a 45° diagonal); a run that hits an obstacle is forced only as far as it
  fits.
- Below those thresholds, the soft form: `+ w_seed · (1 − dot(ô, v̂))` on
  edges out of the seed only.

The first draft of this section argued against any hard restriction because a
fast-descending body has `v̂` outside the cone and would strand the seed. That
is exactly what the representability test and the fit-as-far-as-possible rule
cover; with them in place, fixing the initial direction is the more honest
model — the path starts where the body is *going*.

### 3.6 Progress along the path is the tracker's output, not an input

The DP gives a spatial polyline with no timing. **Timing is not precomputed** —
it is what the tracker solves for. The state hands the tracker three things
alongside the path:

- a **progress-speed target** along the path: `MaxWalkSpeed` (× modifiers) for
  Standing, `CrouchMaxWalkSpeed` for Crouched, **unbounded — "as fast as
  possible" — for jump states**;
- its **channel list** — the ops the state may use (the todo's "list of
  operations available based on the player movement state"): legs / drive /
  tuck near the ground, air lateral / air vertical in flight, corner plant
  where a convex corner is in reach — with caps. This is the *only* place the
  ops list enters the engine;
- a **deviation band** around the path (the `PathDeform` cap + slew of the
  existing channel).

The ref engine's walk-accel ramp (`FoldReference.cs:82-85`) is then just the
fold's progress target, not a separate mechanism. A first draft of this plan
precomputed `p_k` by walking arc length at the ramped walk speed; that would
have made "as fast as possible" inexpressible and pinned jump timing to a
schedule the path has no business setting.

The `Grounded` / `FloorY` fields the downstream rows and channels read are
filled from `floorBelow` at the nearest node (`Grounded` =
`floorBelow ≤ SupportReach`, the same test `BallisticPredictor` uses).

### 3.7 Tracking: one QP, parameterized by the state

The todo asks for "a simplified version of the current QP." The shape:

```
variables:   per-tick channel forces z_{c,k}, c ∈ state's channel list, k < H
dynamics:    the existing lever model (CorrectionProblem — velocity-update /
             force / position-offset levers, linearized about a rollout)
objective:   − w_prog · progress(H)                 // arc length reached along the path
             + w_dev  · Σ_k dist(p_k, path)²        // stay in the band
             + Σ_c w_c Σ_k ‖z_{c,k}‖²               // effort
constraints: channel caps and masks; progress rate ≤ progress-speed target
```

That is the current `CorrectionProblem` with the soft progress rows pointed
along the path tangent instead of along `x`, and a speed cap that is finite for
fold states and absent for jumps. Nothing else is new: rows, hinge weights,
fixed iteration counts, the `u⊥` deform lock (generalizing `FoldReference`'s
vertical lock at `FoldReference.cs:162` — a deform never brakes progress)
all carry over.

**What falls out of this, and why it matters for §7:** a jump is not a special
event the tracker has to be told about. A jump state's channel list includes a
strong upward leg channel usable while `Grounded`; "as fast as possible" along
a path that turns upward makes the QP spend that channel exactly when the path
rises (scenario 3: after the sideways segment, not before), and along a path
that stays low it makes the QP *not* spend it (scenario 2: the body never gets
the momentum the lip would have to cancel). Launch timing and launch height
emerge from the solve. The state fires nothing bespoke and reads nothing back.

The consequence for `JumpStates.cs` is real and should be said plainly: the
jump's impulse + hold-time physics becomes a channel with a cap, and jump
height in open air is then `cap × grounded ticks` along a straight-up path —
i.e. the same jump, produced by the tracker. That is a retune, not a
behaviour loss, and it is the price of one engine.

- **v1 for the fold states** is still `FoldReference`'s `PathDeform` + servo
  instance of this QP, unchanged.
- **v1 for jump states** needs the progress-along-tangent objective and the
  unbounded speed target — the one genuinely new piece of solver code in the
  plan.

### 3.8 Debug export

`CorrectorScratch` already carries `ReferenceTrajectory` / `BallisticTrajectory`
/ `SolvedTrajectory` behind a `CaptureTrajectories` gate, and `Game1` already
draws oracle paths in the freeze-frame inspector. Publish the lattice path into
the reference buffer and it is drawn for free. Worth adding beyond that: the
blocked bitmap and the admitted-offset cone as an overlay — the bitmap is where
the resolution bugs will show up, and the cone is where the "why won't it climb
this" questions get answered.

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

- The **along-`u` half is safe for free**: DAG ordering means the path can
  never brake or reverse against intent. That is a stronger guarantee than the
  unilateral channels give today, and it is a genuine argument *for* this
  design.
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

Adding "incoming edge offset" to the node state to penalize curvature is a
**~7× state blowup** (one state per admitted offset): ~500–1,000 reachable
nodes → ~3,500–7,000 states, and transitions ×7 with them. That likely lands at
50–100 µs/solve, and
with two players × a rollback window of 8 that is on the order of 1.5 ms/frame
on top of the current 250 µs rollback frame. Real, not fatal, but not free.

You probably do not need it. Two things already produce smooth paths:

1. the per-edge steepness penalty `w_steep · (1 − dot(ô, u))`, which needs no
   state and punishes exactly the large-amplitude zigzags that look bad;
2. the reference being tracked is the **bevel-smoothed** floor envelope, so the
   attractor is already smooth.

**Build it without direction state, look at the exported path, and add the state
only if visible zigzag survives.** If it does, note that the cheaper fix is
usually a post-hoc smoothing pass over the recovered path, not a bigger DP.

### 4.5 The cone is doing two jobs; keep them apart in your head

`cos θ` (§3.3) is simultaneously (a) the thing that makes the graph a DAG
(`cos θ > 0`, structural) and (b) the maximum steepness the path may take
(tuning). Because (a) is a hard floor, the tuning range for (b) is narrow —
roughly `0.3 … 0.7` at radius 2 — and most of the "how steep" question is really
answered by the neighborhood radius and by `w_steep`, not by the cone. Do not
expect to tune feel with `cos θ`; expect to tune it with `w_steep`.

The cone must also be looser than the todo's phrasing suggests: hopping a
one-tile block at walk speed is 45–60° off horizontal, so a "tight" cone would
forbid the hops the planner exists to find.

### 4.6 `dir == 0` has no progress axis

The fold deliberately runs *unconditionally*, with no input gate, because hover
must hold at rest — `AmbientCorrector`'s header calls out the `vx = 0` liveness
deadlock this fixed. A direction-ordered lattice has no ordering when there is
no direction.

**Rule:** below a small target-speed threshold, skip the DP entirely and use a
pure hover column (`y = env − hoverOffset` at the current x), tracked by the same
servo. One branch, but it is a real hole in the algorithm as written, and it
covers a state the player is in constantly.

### 4.7 Regime scope: inherit `FoldReference`'s guards verbatim

`TryApply` already returns false for knockback (`PreserveExternalVelocity`),
launched (`-vy > SpringMaxRiseSpeed`), plunging (`vy > MaxGroundEngageVnRel`),
and unanchored (no floor within `SupportReach`) — falling back to the ballistic
QP path. Keep every one of those.

Not because the path cannot represent those motions — with a general `u` it
can — but because in those regimes there is nothing for the hover term to track
against and the body's momentum, which the path cannot see, dominates where it
goes. **In v1 the planner owns the supported / near-support regime and nothing
else.** Extending it to jump states (a `u` of up or up-diagonal, no hover term)
is a plausible v2, and the direction-general design in §3 is what keeps that
door open; it is not v1 scope.

### 4.8 Determinism — an improvement, but be deliberate

Requirements (`CLAUDE.md`'s sim rules): no sim-affecting mutable statics, no
hardware polling, identical iteration order on restore, and — because the solve
runs inside `Simulation.Step` — it is replayed on every rollback frame.

A dense-array DP over a fixed-size window, with a deterministic node order,
satisfies all of this naturally, with no allocation. Note that the existing `LatticePlanner` does **not**: it iterates
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
keeps that list out of the path entirely: edges are pure geometry (§3.3), and
the ops list configures only the tracker (§3.7).

This is a choice, not a limitation of the DP. Per-node support is cheap once
the bitmap exists (`floorBelow`, §3.2 — a column sweep, not a `FloorEnvelope`
call), so an actuation gate on rising edges *could* be added in a line. It is
left out so the graph stays a single-purpose geometric object and so the
tracker / give-up split (§4.3) is the one place deliverability is judged.

---

## 5. Phasing

> **Status 2026-08-24: phases 0–1 are BUILT** — `Character/Corrector/LatticePathPlanner.cs`
> (window, bitmap, `floorBelow` sweep, flood, DP, path recovery), wired as a
> freeze-frame oracle in `Game1` (yellow path + red blocked-cell ticks) with
> gates in `MTile.Tests/Sim/LatticePathPlannerTests.cs` (flat hover carry,
> block climb, ceiling duck, full-height bonk, free-standing-wall over-route
> pinned as accepted §3.3 behavior, determinism, timing).
>
> **Phase 2 is BUILT (same day)** — `FoldEngine = "lattice"` →
> `Character/Corrector/FoldLattice.cs`. `FoldReference` was split into
> `Admit` (the §4.7 guards) / `Rollout` (the hand-written carry) / `Track`
> (rows → deform → servo); the lattice engine is `Admit` + a rollout that
> time-parameterizes the DP's polyline + the same `Track`. Rules kept from
> ref: `dir == 0` → the ref hover column (§4.6); progress along `u` at the
> fold target ramped by `WalkAccel`; descent no faster than gravity. Rules
> dropped on purpose: the rise cap and climb band (the path's climb is the
> climb — §3.3). Progress is the projection onto `u`, not arc length (arc
> pacing halves the carry on a 60° drop). Seeds inside the margin snap to the
> nearest not-behind free cell; no such cell (flush at a wall) → the ref
> carry, so the bonk stays honest. Gates in
> `MTile.Tests/Sim/FoldLatticeEngineTests.cs`: the `FoldRefEngineTests`
> contracts verbatim (hover + progress, rest, bumpy tunnel at 97 px/s, 1-high
> step, tall-wall honest stop, corridor duck-in, bit-determinism) plus
> engagement (path on 240/240 frames) and a rollback round trip across the
> solve. Measured, Release, JIT warmed: **12.7 µs / 0 B per solve**; a whole
> sim step in the bumpy tunnel is **48 µs under lattice vs 25 µs under ref**
> (the stamp is a cached per-tile mask now; buried tiles are skipped). The
> earlier "~90 µs" figure was tier-0 JIT code — the timing tests now warm
> past tier-up. Default stays `qp`; `configs/movement_config.json` ships
> `ref`; flip to `lattice` to A/B. Open: phase 3 (weights by playtest, the
> §4.3 tracking-residual give-up), crouch mounting 1-high blocks (no climb
> band on edges), and §7 (jump states own a solve).

| phase | deliverable | gate |
|---|---|---|
| **0** | Lattice geometry: window, admissibility bitmap, `floorBelow` column sweep, admitted-offset cone. Drawn in the freeze-frame inspector. No DP, no sim wiring. | bitmap visually matches terrain at a few cell sizes; `floorBelow` shading matches the visible floor under a ledge and in a pit; cone overlay matches `u` |
| **1** | The DP (no direction state) + path export. Oracle-only, run beside the LM/lattice oracles in `Game1`. | µs/solve measured in `MTile.Bench`; path looks sane over lips, corridors, 1- and 2-tile blocks at radius 2 and 3 |
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

## 7. Scenario audit (2026-08-23) — can the design as written do these?

> The per-scenario table — parameters the owning state passes, correct path
> and motion, status — lives in [LATTICE_SCENARIOS.md](LATTICE_SCENARIOS.md).

Three scenarios the engine is expected to handle with **one uniform solve**:
(1) a bumpy corridor, alternating over 1-high blocks and under 1-low lips;
(2) a jump into a 2-high tunnel, pushing slightly under the upper lip and
dipping slightly below hover to get in; (3) a jump from under an overhang,
requiring a sideways shuffle before the body can rise. Body facts that drive
the geometry: hexagon half-width 6, half-height 10.4, standing hover 10 → head
at ~31 px, so a 2-high (32 px) tunnel fits standing by ~1 px.

| | verdict | why |
|---|---|---|
| **1** corridor | **yes** | Admissibility forces the path under lips and over blocks; hover cost pays for the deviation; the far-band goal makes "stop" not an option. No state change to duck — crouch is reference shaping over the same polygon. Needs the cone to admit slope ≈ 1 (the C-obstacle bevels make block corners 45° ramps). Tracker is the ref engine's, which already does this. |
| **2** tunnel entry | **no** | (a) §4.7 keeps the launched guard, so the engine does not run mid-jump. (b) With the guard lifted the *plan* is right — `u = +x`, hover off, admissibility keeps the path under the lip and the far band inside the tunnel pulls it in low — but the jump impulse is fired by the state today, not chosen by a tracker, so the body arrives with momentum nothing planned for and no mid-air channel can cancel (air-vertical 300 px/s²; tuck and redirect are near-ground only). |
| **3** covered jump | **no** | With `u = up`, `cos θ > 0` admits only rising edges; a sideways shuffle is perpendicular to `u` and a shuffle-while-settling is *backward*. Under a 2-high slab there is ~1 px of headroom, so no diagonal fits either. **The DAG cannot express the motion** unless `u` is tilted to the exit side (`u` = up-right makes `(1,0)` then `(0,−1)` legal) — and picking that side is exactly what `CoveredJumpState.TryPickOpenDir` decides today. The launch is also a bespoke impulse today, so even with the right path nothing would time it to the path's turn. |

The finding is that **the solve is uniform; the surrounding contract is not.**
What differs per scenario is (i) how `u` is chosen, (ii) whether hover is on,
and (iii) who consumes the path. Three changes make all three cases run
through the same solve:

1. **Run the engine airborne, with hover as a state-supplied flag.** Drop the
   launched / plunging guards *for this engine*; §4.7 becomes "the engine runs
   in every fold and jump regime; only knockback stays excluded." Standing /
   Crouched pass hover on; jump states pass it off, so an airborne seed is not
   dragged toward the lower floor. This is the todo's "abstract boundaries
   based on hover constraints, passed in" — nothing cleverer.
2. **`u` is intent, and a pure-vertical intent solves twice.** `u` is the
   direction the player wants to *go*, never the jump direction (in scenario 2,
   `u` = up-right would put the lip tuck against `u` and the `u⊥` lock of §3.7
   would forbid it). When intent has no horizontal component, solve for
   `u` = up-left and up-right and take the cheaper far-band cost — ~5 µs each,
   and it replaces `TryPickOpenDir` with the same machinery every other case
   uses.
3. **Jump states own a solve, same as fold states.** A jump state generates
   the path with its own parameters (`u` from intent, hover off, its window)
   and runs the same tracker with its own channel list and an unbounded
   progress target (§3.6–3.7). Launch timing and height are not decisions the
   state makes or reads back — they emerge from "as fast as possible" along a
   path that turns up (scenario 3) or stays low (scenario 2). The jump impulse
   becomes a leg channel with a cap. `CoveredJumpState`'s slide-then-launch
   and `TryPickOpenDir` both dissolve into path + tracker.

   An earlier draft of this section had jump states *reading* the path to time
   and size a bespoke impulse. That was the wrong split: it kept two
   mechanisms where one suffices, and it made the state responsible for a
   dynamics judgment the QP already makes. Withdrawn.

These are not in the v1 scope of §5 as written. If the three scenarios are the
acceptance bar — and they are a good one — then §4.7's scope and §5's phasing
need to be revised to include them. (1) and (2) are corrector-side; (3) is the
`JumpStates.cs` retune of §3.7.
