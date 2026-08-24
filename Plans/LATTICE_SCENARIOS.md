# Lattice engine — scenario table

Companion to [LATTICE_PATH_PLANNER.md](LATTICE_PATH_PLANNER.md) (§7 is the
audit these grew from). One row per scenario: the terrain and body state at
the seed, the **parameters the owning state hands the solver**, and what
"correct" looks like — as a path shape *and* as tracked motion, since the two
can disagree (the path can be right and the tracker can fail to deliver it).

## The parameters, once

Every solve is the same call. What varies per row is only this list — if a
scenario needs something not on it, that is a design gap, not a parameter.

| parameter | who sets it | values |
|---|---|---|
| `u` | the state, from **intent** (never from the jump direction) | unit vector; `(±1,0)` walking, `(±1,−1)/√2` diagonal hops, `(0,−1)` pure vertical (see row 9 for the tie rule); `dir == 0` → no solve, hover column |
| `hover` / `hoverOffset` | the state | Standing `on / 10`; Crouched `on / 0`; jump states `off` |
| progress target | the state | `MaxWalkSpeed`×mods (Standing), `CrouchMaxWalkSpeed` (Crouched), **unbounded** (jumps) |
| window `L` | config `LatticeLookaheadTiles` (3.5) | a state may shorten it; nothing lengthens it (§4.2 — the autopilot dial) |
| cone `cosθ` | config `LatticeConeCos` (0.5) | structural; not a per-state knob |
| weights | config `LatticeSteepWeight` 30, `LenWeight` 1/px, `HoverWeight` 0.05/px², `SeedWeight` 20 | phase-3 tuning surface; per-state overrides only if a row below forces one |
| seed run | the body's current velocity (not a state choice) | first `LatticeSeedRunPx` (8) forced along the velocity's nearest admitted offset when moving ≥ `SeedRunMinSpeed` (20 px/s) and inside the cone; else the soft `SeedWeight` bias (§3.5) |
| channel list + caps | the state (§3.7) | legs / drive / tuck (fold); air lateral / air vertical / leg-impulse (jumps) |
| deviation band | `FoldReference` deform cap 8 px, slew 150 px/s | shared |

Body facts the rows lean on: hexagon half-width 6, half-height 10.4, margin
2, standing hover 10 → rest center ≈ floor top − 20.4, head ≈ 31 px above the
floor. A 2-high (32 px) opening fits standing by ~1 px; a 1-high (16 px)
opening fits nothing.

## The table

Status is as of 2026-08-24 (phase 2 built). ✅ = passes a gate in
`FoldLatticeEngineTests` / `LatticePathPlannerTests` / `LatticeScenarioTests`; 🟡 = the rule exists
but no gate pins it; ❌ = not built or a known gap (says which).

| # | scenario | terrain & seed | owner / regime | solver parameters | correct behavior | status |
|---|---|---|---|---|---|---|
| 1 | **Bumpy corridor** | 3-tile interior, alternating 1-high floor bumps and 1-low ceiling lips; body walking at hover | Standing, anchored | `u=(dir,0)`, hover **on** 10, walk speed, `L` 3.5 | Path alternates over each bump and under each lip in one polyline; no stall at any crossing; no state change (crouch is not needed — the path *is* the duck); average speed near walk speed; body never contacts | ✅ `BumpyTunnel` 97 px/s, `Row01` (94 px/s, no crouch) |
| 2 | **Jump into a 2-high tunnel** | tunnel mouth ahead, body airborne on a jump arc arriving slightly above the mouth's free band | Fall state | `u=(+1,0)` (intent = right, not the arc), hover **off**, progress unbounded, corner channels | Path begins with the player already having jumped, moving right and upwards/downwards in an arc towards the tunnel entrance (the seed run fixes the first stretch to the arc); body enters low and clean, no bonk on the lip | 🟡 `Row02` passes today — but via the qp airborne path; the lattice engine is excluded launched (§7.1) |
| 3 | **Covered jump** | body standing under a 2-high slab with open air very close to one side; player presses jump with no horizontal input | Jump state, grounded under cover | intent pure-vertical → **one solve** `u=(0,−1)` (a different `u`, with the bonk cutoff, if left or right is pressed), hover **off**, unbounded; bonk if too far from an open ceiling | The open side wins **if close** — the tile C-obstacle's corner bevel is a ≈45° ramp the `(±1,−1)` offsets can ride, so a body within a few px of the slab's edge rises out diagonally; deeper under the slab no rising edge exists and it bonks honestly; the leg-impulse channel fires **when the path turns up**, not at the button press; no slide-then-launch logic in the state | near ❌ `Row03_NearEdge` skipped (no rise today); far ✅ `Row03_FarFromEdge` (bonks, no shuffle) — `CoveredJumpState` still owns the launch (§7.3) |
| 4 | **Rest** | flat floor, no input, body at hover | Standing, anchored | `dir == 0` → **no DP**; ref hover column, hover on 10 | No bobbing (\|vy\| < 5), no drift (\|vx\| < 5); the engine never fabricates a direction | ✅ `AtRest_StaysPut`, `Row04` |
| 5 | **1-high step while walking** | flat floor, 1-high ledge ahead | Standing | `u=(dir,0)`, hover on 10, walk | Path ramps up the block's C-obstacle bevel (≈45°) and re-hugs hover on top; x carry continues through the climb; on the ledge the body rides one tile higher | ✅ `OneHighStep` |
| 6 | **Tall wall** | wall spanning the whole window ahead | Standing | `u=(dir,0)`, hover on 10, walk | DP bonks (far band unreachable) at the wall; reference carries straight into it; rows truncate; body stops ≈6 px from the face at hover height — **no climb, no planned brake, no push-back** | ✅ `TallWall_HonestStop`, `FullHeightWall_Bonks`, `Row06` |
| 7 | **Free-standing 2-high wall** | 2-high column with open air above | Standing | `u=(dir,0)`, hover on 10, walk | *Path*: routes over (edges are geometry, §3.3 — accepted). *Motion*: the legs cannot deliver a 32 px rise from a walk, so tracking residual grows → **give-up** (§4.3) → honest bonk as in row 6. The path being over the wall must not make the body float up it | ✅ `Row07` — holds at the wall at y=74 today without a give-up (the servo cannot deliver the rise; rows/deform pin it); path pinned by `FreeStandingTwoHighWall_RoutesOver` |
| 8 | **Ledge drop while walking** | flat floor ending in a drop of ≥2 tiles | Standing → Falling | `u=(dir,0)`, hover on 10, walk | Reference descends **no faster than gravity** while x carries at full walk speed — no "grab" at the lip (arc-length pacing would halve speed), no dive; once the lower floor enters the window the hover term re-binds and the path hugs it | ❌ `Row08` skipped — settles 6 px below hover after the drop (findings 1–2) |
| 9 | **Neutral jump in open air** | flat floor, jump with no horizontal input, nothing overhead | Jump state | intent pure-vertical: **one solve** `u=(0,−1)` (row 3's rule — there is no tilted fallback) | A vertical path — the body must **not drift sideways** on a neutral jump; row 3's diagonal escape only ever fires against a bevel, never in open air | ✅ `Row09` (rises 61 px, 0 px drift) — because the jump state still owns the arc |
| 10 | **Diagonal hop over a block** | 1-high block ahead, player holds right + jump | Jump state | `u=(+1,−1)/√2`, hover **off**, unbounded, leg-impulse + air channels | Path rises over the block's C-obstacle and continues; "as fast as possible" spends the leg channel at launch while grounded; lands beyond the block; the same block *walked* into (row 5) is a climb, not a jump — the difference is only `u` and hover | ❌ jump states not on the engine |
| 11 | **Crouch at a 1-high block** | crouching under a 2-high ceiling, 1-high block ahead | Crouched | `u=(dir,0)`, hover on **0**, crouch speed | Body stays low and stops at the block (honest bonk) — a crouch never mounts ledges (`CrouchClimbReachUp` 4). **Known gap:** edges carry no climb band, so today's path routes over the block exactly as row 5; needs a per-state rise cap or steepness weight on the solve, not on the edges | ❌ `Row11` skipped — crouch mounts the block; design gap, logged in BACKLOG |
| 12 | **2-wide pit while walking** | flat floor with a 2-tile gap; player holds right | Standing → Falling | `u=(dir,0)`, hover on 10, walk | No auto-jump, no auto-brake: the path continues at hover into the gap (no floor below → no hover cost), x carries at full speed, the body falls at gravity (row 8's rule) and re-binds on the pit floor if it is in the window. A 1-wide gap is not a gap (C-obstacles of the two edge tiles overlap): the path carries straight across | 🟡 follows from rows 6/8; no gate |
| 13 | **Landing on flat, holding right** | body descending onto a floor | Falling → Standing | engine **excluded** while `vy > MaxGroundEngageVnRel` (plunging); re-admits on the anchored frame | Impact honesty: no air-brake softening of a slam; the first admitted frame's path is row-1 shaped (hover re-bind) and the carry resumes at the ramp | ❌ `Row13` skipped — descent braked (234 vs 270 uncorrected) and lands 6 px low (findings 1–2) |
| 14 | **Knockback** | body hit, `PreserveExternalVelocity` set | any | engine **excluded** | No correction at all — the fold does not fight combat momentum | ✅ `Admit` guard |

## Tests

`MTile.Tests/Sim/LatticeScenarioTests.cs` — one test per encoded row (1, 2, 3
near + far, 4, 6, 7, 8, 9, 11, 13), all under `FoldEngine = "lattice"`,
asserting the table's *correct* behavior. Rows 5, 10, 12, 14 are deliberately
not encoded yet. Tests the engine cannot pass today are `Skip`ped with the
row's blocker in the reason — that list is the next cycle's checklist.

Status on 2026-08-24 (at commit): **pass** — 1, 2 (today via the qp airborne
path, not the lattice engine), 3-far, 4, 6, 7, 9. **Skipped** — 3-near, 8, 11,
13.

### Findings from wiring the tests (engine, not fixed — next cycle)

> Decision after these findings (2026-08-24): the fix for both is **not** a
> reference-side rule. The free servo in `FoldReference.Track` was a debug
> force; the lattice engine is to drive the `qp` channel stack with legs
> meaning *at support* (plan §1, revised note). The "candidate rules" below
> are kept only as a record of what was tried and withdrawn.

Both surfaced by rows 8 and 13 and confirmed with a per-frame probe against
`ref` on the same ledge drop. They are in `FoldLattice`'s rollout, not the DP.

1. **Descents are air-braked.** The reference's y is tied to progress along
   `u` through the path, and the cone limits the path to 45°, so at walk speed
   the reference cannot descend faster than ~100 px/s. A body dropping off a
   ledge at 170 px/s within `SupportReach` of the lower floor is *anchored*
   (the fold runs) and gets braked to the path's slope — the probe showed vy
   170 → 53 in four frames, then hanging in `FallingState` 22 px above the
   floor with the seed run off. `ref` never does this because its x and y
   rollouts are independent (y falls at gravity regardless of x). Candidate
   rule: a path *below* the reference is not a support until it is at hover
   height — the reference falls at gravity (no faster, no slower) and rides
   the path only where the path's node sits on its floor at hover. The
   general answer is the §3.7 tracker with gravity in its dynamics (no rule
   at all). Note also that the *path's* steepness is set by the offset
   neighborhood — a `(1,k)` edge admits `atan(k)` — so the 45°/63° figures
   above are a table choice, not a lattice limit (plan §3.3).
2. **The seed run can lock the tick-0 servo.** With the run on, a landing
   settles at 81.6 (6 px low) and never rises: velocity is `(100, 0)`, the run
   forces a flat 8 px, the servo tracks only tick 0's reference velocity (inside
   the run → vertical 0), the body stays flat, next frame the velocity is still
   flat. The run re-derives itself. Any forced run ≥ one tick of progress hides
   the turn behind it from a tick-0 servo. Candidate fix: servo toward the
   first sample *past* the run (pursuit), or make the run a plan-side bias the
   §3.7 tracker looks through.

Also observed, not a defect: row 7 already holds at the 2-high wall (the servo
cannot deliver the 32 px rise and the rows/deform pin the body at y = 74) — the
§4.3 give-up is not needed for that case as things stand.

## Notes the table can't hold

- **Rows 2, 3, 9, 10 share one prerequisite** — §7's three changes: run the
  engine airborne with hover as a flag, `u` from intent, jump states owning a
  solve with the unbounded progress target. Row 9 adds a rule to §7.2: try the
  exact vertical `u` before the tilted pair.
- **Row 7 is the acceptance test for the give-up** (§4.3). Phase 3 should add
  a gate that runs the sim, not just the planner: the body must end at the
  wall, not above the floor.
- **Row 11 is the one place a state needs a solve-level knob** beyond the
  parameter list above. Options: a per-profile max rise per edge (a cone
  asymmetry — cheap, but it is an actuation gate by another name, which §3.3
  deliberately avoided) or a per-profile `SteepWeight` large enough that
  climbing always loses to bonking. Prefer the weight; it keeps edges pure.
- **Gates to write next**, in the order they pay off: row 8 (ledge drop), row
  12 (pit), row 7 (give-up, blocked on phase 3), then rows 9/10/2/3 as the §7
  work lands.
