# Todo #2 audit — does the lattice engine do what the todo asked for?

Written 2026-08-28 against `main` @ c0a4a3a. Companion to
[LATTICE_PATH_PLANNER.md](LATTICE_PATH_PLANNER.md) (the design, with its
revision history) and [LATTICE_SCENARIOS.md](LATTICE_SCENARIOS.md) (the
acceptance table and per-pass measurements).

**The short version.** Todo #2 is not a plan waiting to be started — it is
the item `LATTICE_PATH_PLANNER.md` was written from, and that plan has been
built through phase 2 and ~15 revision passes. `configs/movement_config.json`
ships `"FoldEngine": "lattice"`, so it is the engine the game runs today. The
lattice test slices pass (38 / 0 failed / 3 skipped on c0a4a3a). What has
**not** happened is phase 3 onward: playtest tuning, the engine decision (§4.9
of the plan — four fold engines still exist), the perf gate, and the cleanup.
The doc drift is bad enough that the plan itself still opens with
"Status: plan only — nothing built".

This document does three things: maps every bullet of the todo to what was
built (§1), says where the build deliberately or accidentally departs from the
todo's wording (§2), and re-cuts what remains into a plan that can actually be
started (§3).

---

## 1. Bullet-by-bullet: todo #2 vs. what exists

| the todo asks for | what exists | status |
|---|---|---|
| "a function callable from within the solve step" | `LatticeTracker.Apply(ctx, s, fold, dir, ref vars)` — reached from `MovementState.ApplyAmbient` (`Character/Movement/Movement.cs:82`) → `AmbientCorrector.Apply` (`AmbientCorrector.cs:194`, dispatch at `:230`). Every fold state's `Update` ends with one `ApplyAmbient` call; output is an acceleration added to `Body.AppliedForce` (`LatticeTracker.cs:346`). | ✅ |
| "for standing, crouching, jumping, etc" | Standing / Crouched / Falling / Jumping / WallSliding / DoubleJump / WallJump all hand a `FoldProfile` (`AmbientCorrector.cs:27-102`: `Stand`, `Crouch`, `Fall`, `Jump`). The maneuver states (Parkour / Mantle / ArcJump / RunningJump / CoveredJump / ledge / dropdown) keep their own reference-trajectory solves — *deliberately*, after gating them off cost the 2-block ArcJump (thirteenth pass). | ✅ ambient; maneuvers excluded on purpose |
| "target direction based on player inputs" | `u` from intent: `(±1,0)` walking, `(0,−1)` neutral jump, the actuators' launch angle (≈76°) for a held jump; `dir == 0` → no DP, hover column. | ✅ |
| "a time horizon" | Two horizons, decoupled: the **path** looks `LatticeLookaheadTiles` = 3.5 tiles (56 px) ahead — spatial, the autopilot dial; the **tracker** solves 5 ticks (`LatticeTracker.cs:47`). | ✅ (spatial, not temporal — see §2.1) |
| "abstract boundaries for the player based on hover constraints" | `FoldProfile.Hover` / `HoverOffset` → per-node `floorBelow − hoverOffset` from a column sweep of the bitmap, priced linearly (`LatticeHoverWeight` 3/px). | ✅ |
| "an abstract boundary definition representing admissible points for the player center (e.g. CObstacleTemplate)" | Exactly that: `CObstacleTemplate` stamped at every solid tile into a cell bitmap, margin ½ cell, node admissible iff its center is outside every stamped obstacle (`LatticePathPlanner.cs:203-206`). | ✅ |
| "a list of operations available based on movement state and environment (redirect, corner force, standing force up, left/right …)" | **Not passed into the path solve.** Edges are pure geometry (plan §3.3, §4.10). The ops list exists only in the tracker as `CorrectorChannels.BuildFold` channel masks (legs / drive / tuck / redirect near support; `CornerAssist`, `AirLateral`, `AirVertical` masked off on the engine). The state does not hand a list; it hands a `FoldProfile` and the tracker derives availability from support geometry. | ⚠️ deliberate deviation — §2.2 |
| "divide tiles in the plausible path into a higher resolution grid, ~5×, hexgrid later" | `LatticeCellsPerTile` 5 → 3.2 px world-aligned cells; window = the cone footprint from the seed, `MaxCells` 4096 pooled. Offset table + cell→center map are the only square-lattice-specific pieces. | ✅ |
| "assume movement never exceeds a threshold cosine distance from the requested direction; edges directed/filtered by it" | `LatticeConeCos` = 0.05 ("90° − ε"): **every forward offset is admitted**; the cone is kept only as the DAG condition (`cos θ > 0`). Steepness is priced (`RiseCost` per px climbed), never filtered. | ⚠️ the threshold was made vestigial on purpose — §2.3 |
| "edges between points within a reasonable neighborhood" | Primitive offsets `|dx|,|dy| ≤ 3`, gcd 1 (32 offsets; 15 forward per node), each with a precomputed supercover crossed-cell list so a long edge cannot tunnel a blocked cell (`LatticePathPlanner.cs:52-110`). | ✅ |
| "dynamic programming over shells by depth in the DAG" | Nodes sorted by `dot(center, u)`, one pass; reachability flood first; exact cost-bound prune from the argmax goal. | ✅ |
| "(A) matches the direction" | Structural (DAG) + the goal `argmax w_prog·(p − p_seed) − dp` over reachable nodes, `bonk` when the far band is unreachable. | ✅ |
| "(B) obeys local curvature constraints (avoid zig-zags); may need direction state" | **Not built.** No direction state; the steepness and length terms were *removed* in the seventh pass. The only lateral shaping is `LateralTieBreak`. Nobody has yet checked the exported path for zigzag, which the plan (§4.4) made the precondition for building it. | ❌ open — §2.4 |
| "(C) maintains hover constraints" | see hover row above | ✅ |
| "(D) stays within admissible points" | see bitmap row above | ✅ |
| "solve over < 5000 points" | Window 22×110 cells at the ±3 table, ~460 reachable; 108 µs / 0 B per solve. Met on node count. But the whole sim step in the bumpy tunnel is **187 µs under lattice vs 25 µs under ref** (Release, warmed) — the tracker's bead passes, wall rows and exact sweeps are most of it. | ⚠️ node budget met, step budget not — §2.5 |
| "account for the current frame player velocity" | Built twice (seed run, seed bias) and **turned off** (`LatticeSeedRunPx` 0, `LatticeSeedWeight` 0): with per-tick re-planning either one fed the current velocity back into the target. Velocity is handled entirely on the tracker side (the nominal is the exact free rollout). The path is momentum-blind by design. | ⚠️ deliberately unmet in the path — §2.6 |
| "a simplified version of the current QP solver … best combination of forces to keep the player on the path" | `LatticeTracker`: 5-tick `CorrectionProblem` on `CorrectionSolver` — hard ½-cell band rows perpendicular to the path (sliding bead, 3 outer passes), arc-length speed rows for hovering profiles / x-cap for air, one soft progress row at the last tick, clearance rows from the free rollout, `BuildFold` channels with `DeltaWeight = 0`, then `ExactSweeps`. | ✅ built — but not "simplified" — §2.7 |
| "export the calculated path for debugging" | The DP path is published as `SolvedTrajectory` (magenta on screen under `lattice`), the free rollout as `BallisticTrajectory`; the freeze-frame inspector draws the oracle path (yellow) and blocked cells (red). No live blocked-bitmap / reachable-set / cone overlay outside freeze-frame. | ✅ path; 🟡 overlays |

---

## 2. Where it departs from the todo — and whether that is a problem

### 2.1 The horizon is spatial, not temporal
The todo's "time horizon" became `LatticeLookaheadTiles`. This is right: over
the 10-tick ambient horizon a walking body moves one tile, which cannot see a
two-tile obstacle. The consequence to own is that the lookahead is the autopilot
dial (plan §4.2) — 3.5 tiles was picked, not playtested. Not a defect.

### 2.2 The ops list does not enter the path
The todo passes the available operations *into* the solve so the path only
proposes what the body can do. The build keeps edges pure geometry and lets the
tracker + costs decide deliverability. In practice this held up: row 7 (a
2-high free-standing wall the legs cannot climb) is refused by `RiseCost`
pricing at the goal, not by an edge gate, and the §4.3 tracking-residual
give-up turned out unnecessary for it. Two honest costs of the choice:

- the path can still be *right* while the actuators cannot deliver it, and
  the only thing stopping a mushy half-follow is the cost weights — there is
  no explicit refusal mechanism. Row 18's skipped double-jump case (legs
  re-arm at a pillar top the body is leaving, 4300 px/s² of lift while rising
  at 210) is exactly a "the tracker had an actuator it should not have had"
  bug, i.e. the ops list being derived from geometry rather than from state;
- "which ops are available" is encoded in three places — `FoldProfile`
  fields, `BuildFold`'s masks, and `LatticeTracker`'s overrides of those masks
  (`near` bool, air channels off, redirect re-parametrized). If you want the
  todo's literal shape, that is the refactor: make the ops list a field of
  `FoldProfile` and have the tracker consume it instead of overriding
  `BuildFold`.

Recommendation: keep edges geometric; do the `FoldProfile`-carries-the-ops
refactor when row 18 is fixed, since the leg-mask question *is* that refactor.

### 2.3 The cosine threshold is vestigial
`LatticeConeCos` 0.05 admits every forward offset; steepness is priced by
`RiseCost` rather than filtered. The plan's argument (§4.5) is sound — a hop
over a block is 45–60° off horizontal, so a "tight" cone forbids the hops the
planner exists to find — but it means the todo's knob does nothing and the
window is 3× wider than a 60° cone's (108 µs vs 39 µs per solve). Either
accept it and stop calling it a knob, or bring the cone back as a per-profile
value (Standing 60°, Jump 90°−ε) for the cost. Recommendation: accept; the
cost is on the tracker side anyway (§2.5).

### 2.4 Objective (B) was never built and never evaluated
The zigzag / curvature term is the one todo objective with no code behind it.
The plan's own condition — "build it only if visible zigzag survives in the
export" — has not been checked; the seventh pass removed the steepness and
length costs, so the DP now has *nothing* penalizing lateral wander except a
tie-break. The scenario doc records symptoms that could be this: "bumps are
now small hops" (legs rise 150 px/s at corridor bumps, `qp` 79), the stair
climb hopping before the along-path speed cap. Those were addressed on the
tracker side (speed rows), which is consistent with the plan's philosophy, but
the path itself has still not been looked at for shape. Cheap to check:
freeze-frame a corridor and a staircase and look at the yellow path. If it
zigzags, the cheap fix is a post-hoc smoothing pass, not direction state
(7× state blowup).

### 2.5 The node budget was met; the sim-step budget was not
The todo's <5000-point budget is satisfied at ~460 reachable nodes and 108 µs.
But the plan's promise was "a small fraction of the 25–184 µs sim step", and
the measured tunnel step is **187 µs under lattice vs 25 µs under ref**. The
DP is ~half of that; the rest is `LatticeTracker` (three bead passes × a
5-tick QP with wall rows + exact sweeps). With two players × a rollback window
of 8, a rollback frame is ~3 ms of corrector — likely fine at 60 Hz on
desktop, not obviously fine in the browser AOT build (~40 fps already). The
plan's §6 asked for a `MTile.Bench` regression gate from phase 1; there is a
`CorrectorDiag` hook but no lattice row in `baseline.txt`. Measure before
tuning further.

### 2.6 The path is momentum-blind — accept it, and say so
The seed run / seed bias are off because with per-tick re-planning they made
the target chase the body. So the path never knows the body's velocity; the
tracker's exact free rollout does. The consequence that already bit: any
actuator that can bend an airborne body toward a momentum-blind plan is wrong
(the twelfth pass masked `AirLateral` / `AirVertical` for that reason). This is
the correct resolution of the todo's "account for current velocity" bullet —
in the tracker, not the DP — but the two dead knobs (`LatticeSeedWeight`,
`LatticeSeedRunPx`, `LatticeSeedRunMinSpeed`) and the seed code path should be
deleted rather than kept "for the oracle".

### 2.7 "Simplified QP" — it is not simpler
The tracker is the same `CorrectionSolver` as the `qp` fold, with different
rows and an extra exact-sweep phase; it grew wall rows, sliding beads and
`ExactSweeps` in the course of one day. That is not a criticism of the result
(the corridor went from stalling at every face to 0 stalls), but the todo's
"simplified" expectation should be dropped: the simplification achieved is in
the *reference generation* (the 40-line hand-written rollout and its rule set
are gone), not in the QP.

### 2.8 Knobs
The plan repeatedly says "no new knobs"; the engine added `LatticeLookaheadTiles`,
`LatticeCellsPerTile`, `LatticeConeCos`, `LatticeProgressWeight`,
`LatticeHoverWeight`, three `LatticeSeed*`, `FoldRiseCost`, `CrouchRiseCost`,
`FoldRedirectForce`, and the `FoldProfile` fields. Three are dead (seed), one
is vestigial (cone), two are structural (cells, lookahead). The remaining
taste surface is small — `ProgressWeight`, `HoverWeight`, the two `RiseCost`s
— and **none of them appear in `configs/movement_config.json`**, so phase 3's
playtest tuning has not started: everything runs on code defaults chosen from
scenario tests. That is the biggest gap against "does it achieve what I want",
because what you want is a feel, and feel has not been tuned yet.

### 2.9 Hygiene that is not the todo's fault but blocks working on it
- `LATTICE_PATH_PLANNER.md:3` says "plan only — nothing built"; the design is
  buried under fifteen stacked "— current" revision notes. Reading it cold,
  the sixth through twelfth passes contradict each other by design.
- `LATTICE_SCENARIOS.md` had unresolved merge-conflict markers in the
  scenario table (rows 1–4 repeated five times) — fixed alongside this doc,
  keeping the latest version of each row.
- `CODEBASE_OVERVIEW.md:177` lists `LatticePlanner` (the dead prototype) and
  omits `LatticePathPlanner`, `LatticeTracker`, `CorrectorChannels`,
  `CorrectorScratch`, `CorrectorLedger`; its corrector narrative describes
  the `qp` engine only.
- `MovementConfig.cs`'s `FoldEngine` comment still calls `qp` "the default —
  what the test suite pins", while the shipped JSON selects `lattice`.
- BACKLOG 5.12 (delete `LatticePlanner.cs` + `ZzzLatticeTiming`, ~35 s of the
  suite) is still open, and `FoldReference` / `CorrectorChannels` still ship
  `TEMP EXPERIMENT` code (redirect audit counters, `CornerPlant`).
- Uncommitted in the working copy: `FoldLegForce` 6000 → 4000 in
  `movement_config.json` — a tuning result from play that should land or be
  reverted, not sit as a diff.

---

## 3. What remains — the plan you can start on

Phases 0–2 of the original plan are done. This is phase 3 onward, re-cut so
each step has a gate.

### 3.1 Decide the engine, then make everything agree with the decision
Four fold engines exist (`qp`, `ref`, `lm`, `lattice`); the plan's §4.9 said
"if lattice works it replaces `ref` and `qp`; if not, delete it; do not park
it." The JSON already votes `lattice`. Decide it explicitly:

1. Play a session on `lattice` and one on `qp` (hot-reload flip) and write
   down which one is the game. If lattice: code default → `"lattice"`, the
   test suite's `qp` pin moves to an explicit per-test override, `ref` is
   deleted (its `Admit` guard moves to the tracker; its `Track` tail is
   already unused by lattice), and `qp` becomes the next deletion candidate
   once the maneuver states no longer depend on `BuildFold`'s qp form.
2. Either way, delete `LatticePlanner.cs` + `ZzzLatticeTiming` (BACKLOG 5.12)
   and the freeze-frame orange oracle.

Gate: one engine fewer; `FoldEngine` default in code == JSON.

### 3.2 Perf gate before tuning
Add the bumpy-tunnel and a flat-walk lattice row to `MTile.Bench`
`baseline.txt` (`CorrectorDiag` exists). Record µs / sim step per engine.
Then decide whether 187 µs is acceptable for 2 players × rollback 8, and
whether the browser build needs the ±2 offset table (39 µs) instead of ±3.

Gate: `--check` against baseline passes; a number for the browser.

### 3.3 Playtest tuning — the actual todo acceptance
Put `LatticeProgressWeight`, `LatticeHoverWeight`, `FoldRiseCost`,
`CrouchRiseCost`, `LatticeLookaheadTiles` into `movement_config.json` (hot
reload) and tune by feel. Delete the three `LatticeSeed*` knobs and the seed
code path. Open feel decisions the scenario doc left "measured, not decided":

- per-profile leg push fade (Stand ~200 / Jump 400) so bump climbs stop
  reading as small hops;
- whether a descent that hugs terrain holds walk speed (stairs down at 150);
- row 18: the leg mask on support the body is *leaving* (fixes the double
  jump over a pillar; this is the `FoldProfile`-carries-ops refactor of §2.2).

Gate: walls feel solid, no bobbing at rest, no visible autopilot at 3.5
tiles — plan §5 phase 3's wording, unchanged.

### 3.4 Objective (B): look, then decide
Freeze-frame the corridor and the 8-step staircase; inspect the DP path. If
it zigzags, add a post-hoc smoothing pass on the recovered polyline (cheap);
only if that is insufficient add direction state (re-measure µs first).

Gate: a screenshot in the scenario doc either way.

### 3.5 Row 11 is a priority bug, not a lattice bug
Crouch-walking into a 1-high block: the planner correctly refuses
(`CrouchRiseCost` 30), then `MantleState` fires from the crouch and vaults it.
Fix in `MovementPriorities`, not in the engine; un-skip `Row11`.

### 3.6 The margin seam
The engine-test tunnel exposes a one-cell-wide C-space seam at margin 2 that
the DP threads only by two exact `(1,−2)` steps. Decide what the margin means
to the bitmap vs. the tracker's band (fifth pass). Small, but it is a
determinism-adjacent spawn-phase accident and worth closing.

### 3.7 Docs
When 3.1 lands: rewrite `LATTICE_PATH_PLANNER.md` §1–3 as the design *as
built* (fold the revision notes into the body, keep the history in
`LATTICE_SCENARIOS.md`), fix the `CODEBASE_OVERVIEW.md` corrector section and
the `MovementConfig.cs` comment, and move the remaining open items into
`BACKLOG.md`.

---

## 4. Answering the question you actually asked

"I'm not sure if it achieves what I want." Against the todo as written: the
architecture is what the todo describes — a callable solve, a 5× lattice DP
over a C-obstacle bitmap with hover costs and a DAG ordering, a channel QP
tracking it, and an exported path — and it is the live engine with a green
scenario table. Three of the todo's bullets were consciously resolved
differently (ops list out of the path, velocity out of the path, cone
vestigial) and the arguments for each are good. One bullet (curvature) was
never examined. What has not been done at all is the part the todo cannot
specify: tuning by feel, the engine decision, and the cleanup that makes the
result maintainable. That is where to start.
