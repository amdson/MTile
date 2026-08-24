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

At 3 tiles × ±2.5 tiles × 5×: **15 × 50 = 750 nodes**, ~7 edges each (§3.3)
≈ **5,000 transitions**. Four tiles ahead is 1,000 nodes. Both well
inside the todo's 5,000-point budget, and a dense-array sweep at that size
should land in the **single-digit µs** — a small fraction of the current 25–184
µs sim step rather than a multiple of it.

Cell size and both window extents should be config knobs from the first commit;
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
   cells that is ~13 × 13 ≈ 170 marks per tile, ~40 tiles in the window → ~7k
   marks. Cheap, and it is the *correct* use of the template.
2. **Distance to the surface below, per node.** One bottom-up sweep per
   x-column of the bitmap: `floorBelow[n]` = distance from `center(n)` down to
   the first blocked cell in its column (∞ if none in the window). ~750 ops
   total. Because the bitmap *is* the C-obstacle — facets, bevels and all —
   this is the floor envelope, evaluated at every node instead of once per
   column, and it is **multi-floor aware**: a ledge top and the ground beneath
   it are both found, which a single-band `FloorEnvelope` query is not.

   Two things read it: the hover term (§3.4), as `floorBelow[n] − hoverOffset`,
   and the support predicate of the edge condition (§3.3),
   `supported[n] = floorBelow[n] ≤ riseReach`. No `FloorEnvelope` calls at all.
   The x-column sweep stays x-indexed whatever `u` is — floors are floors.

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
4. **Actuation** — if `o` rises (`dy < 0`, y-down), `n` must be **supported**:
   `floorBelow[n] ≤ riseReach` (§3.2). Descending and horizontal edges are
   always available — gravity is free, and air control / drive exist in every
   fold regime.

Condition 4 is the todo's "operations available based on the player movement
state and environment," in the only form a velocity-free path can hold it. The
*environment* half is the per-node support predicate. The *state* half is what
the state hands in: `riseReach` (`FoldClimbReachUp` for Stand,
`CrouchClimbReachUp` for Crouch — the existing profile numbers, reused) and,
later, a corner-plant flag that would admit rising edges from nodes with a
convex corner within hand reach (`MarkCornerPlants`' predicate, not built in
v1). No per-node force, no velocity.

Its effect is that a chain of rising edges must stay within `riseReach` of a
surface the whole way. That climbs a 1-tile ledge (the C-obstacle bevel keeps
the body supported up the corner) and **stalls at a 2-tile wall by
construction** — the wall face has no surface within reach — which is today's
elective refusal, produced from geometry rather than from a rollout check.
Without it the DP would draw a path rising through open air over a gap and
lean entirely on the §4.3 give-up to catch it.

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
| **(C) hover** | `w_hover · (floorBelow[n] − hoverOffset)²` | per node, from the column sweep (§3.2); 0 when no floor is in the window; `w_hover` should fade with `|u_y|` — a jump's `u` has no business being pulled to the floor |
| **(D) admissible** | hard prune | blocked cell, or a crossed cell blocked ⇒ no edge |
| **(B) steepness** | `w_steep · (1 − dot(ô, u))` | per edge: cost rises with angle off `u`; may be asymmetric (cheaper below `u` than above — descending is free, climbing is legs) |
| **(A) direction** | implicit | the cone + DAG ordering already forbid backward motion |
| **length** | `w_len · ‖(dx, dy)‖` | per edge; keeps the DP from preferring long diagonals for free |

Note what is *not* here: a curvature term (§4.4).

```
sort nodes by p = dot(center, u)          // once per solve; deterministic tie-break
dp[*] = ∞;  dp[seed] = 0
for n in sorted order:
  if dp[n] == ∞ or blocked[n]: continue
  for o in admittedOffsets:               // §3.3, ~7
    m = n + o
    if outOfWindow(m) or blocked[m] or crossesBlocked(n, o): continue
    if o.dy < 0 and floorBelow[n] > riseReach: continue      // §3.3 condition 4
    c = dp[n] + w_steep·(1 − dot(ô, u)) + w_len·|o| + w_hover·(floorBelow[m] − hoverOffset)²
    if c < dp[m]: dp[m] = c; parent[m] = n
```

Goal: the minimum-cost node among those in the **far band** of the window
(`p ≥ p_max − cell`), so the path is rewarded for progress without a per-node
progress term. If the far band is unreachable, take the reachable node of
greatest `p` — that is the honest bonk (§4.3).

~750 nodes × 7 edges ≈ **5,000 transitions**. Dense arrays, almost no
branching — single-digit µs, a small fraction of the current 25–184 µs sim step.

### 3.5 Seeding with the current velocity

The todo notes this is necessary, and it is the one place a pure spatial DP has
no natural answer. The workable version:

- Seed at the body's actual cell with cost 0.
- **Bias, do not hard-restrict,** the first edge toward the current velocity:
  `+ w_seed · (1 − dot(ô, v̂))` on edges out of the seed only.

Hard-restricting the first edge is tempting and wrong: a body descending fast
has `v̂` outside the cone, and the restriction would strand the seed and fail the
whole solve. A cost bias degrades gracefully.

### 3.6 Time parameterization

The DP gives a polyline. The tracker needs `p_k` per tick. Parameterize by
**arc length along the polyline** at the ramped target speed — reuse
`FoldReference`'s walk-accel ramp (`FoldReference.cs:82-85`) verbatim, but let
its output be distance-along-path rather than `x`:

```
s_k = <existing walk-accel ramp, as arc length>
p_k = polyline.At(s_k)
```

Arc length, not `u`-projection, on purpose: on a steep segment the body's
progress along `u` slows while its speed along the path holds — the honest
"you slow down when you climb" — whereas projecting at full speed would demand
2× walk speed up a slope-2 edge. Over 10 ticks at ≤ `MaxWalkSpeed` the ramp
covers ≤ 17 px, so the tracker consumes only the first tile of a three-tile
plan; the rest exists so the first tile is chosen with foresight (§2.1).

The `Grounded` / `FloorY` fields the downstream rows and channels read are
filled from `floorBelow` at the nearest node (`Grounded` = supported). This is why the integration point in §1 matters:
the piece the todo's algorithm does not produce is the piece the existing code
already computes.

### 3.7 Tracking

The todo asks for "a simplified version of the current QP." The honest
simplification is that **the tracker gets dumber as the path gets smarter** —
that is the whole point of moving intelligence into the path.

- **v1: reuse `CorrectionProblem` exactly as `FoldReference` already does** —
  one `PathDeform` position-offset channel with `SlewCap`, then servo tick-0.
  Zero new solver code. One generalization: `FoldReference` locks the deform
  axis to vertical (`FoldReference.cs:162`) so a deform can never cancel the
  carry; with a general `u`, lock it to **`u⊥`** instead — the same doctrine
  ("deform never brakes progress"), stated in the planner's frame.
- Only if that proves insufficient, expand to a small per-tick least-squares
  over the available force ops (`BuildFold`'s stack minus the redirect disc).

Do not write a new QP for v1. `CorrectionSolver` already does this, and the
deform channel is already the right shape.

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
**~7× state blowup** (one state per admitted offset): ~750 nodes → ~5,000
states, ~5,000 transitions → ~37,000. That likely lands at 50–100 µs/solve, and
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

Less than the first two drafts claimed. The todo passes "a list of operations
available based on player movement state and environment" into the path solve;
this plan does too, but reduced to what a velocity-free path can use: a
per-node support predicate (environment) and the state's `riseReach` plus a
corner-plant flag (state) — §3.3 condition 4. What it does *not* do is model
per-node force magnitudes or which channel would deliver an edge; that stays
with the tracker (§3.7) and the give-up (§4.3).

An earlier draft kept the ops list out of the path entirely on the grounds
that per-node support needs a per-node `FloorEnvelope` call. That was wrong:
once the bitmap exists, support is a column sweep (§3.2), and the objection
evaporates. Recorded so it is not re-argued.

---

## 5. Phasing

| phase | deliverable | gate |
|---|---|---|
| **0** | Lattice geometry: window, admissibility bitmap, `floorBelow` column sweep, admitted-offset cone. Drawn in the freeze-frame inspector. No DP, no sim wiring. | bitmap visually matches terrain at a few cell sizes; supported nodes highlighted correctly on a ledge and beside a 2-tile wall; cone overlay matches `u` |
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
