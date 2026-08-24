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
| channel list + caps | the state (§3.7) | legs / drive / tuck (fold); air lateral / air vertical / leg-impulse (jumps) |
| deviation band | `FoldReference` deform cap 8 px, slew 150 px/s | shared |

Body facts the rows lean on: hexagon half-width 6, half-height 10.4, margin
2, standing hover 10 → rest center ≈ floor top − 20.4, head ≈ 31 px above the
floor. A 2-high (32 px) opening fits standing by ~1 px; a 1-high (16 px)
opening fits nothing.

## The table

Status is as of 2026-08-24 (phase 2 built). ✅ = passes a gate in
`FoldLatticeEngineTests` / `LatticePathPlannerTests`; 🟡 = the rule exists
but no gate pins it; ❌ = not built or a known gap (says which).

| # | scenario | terrain & seed | owner / regime | solver parameters | correct behavior | status |
|---|---|---|---|---|---|---|
| 1 | **Bumpy corridor** | 3-tile interior, alternating 1-high floor bumps and 1-low ceiling lips; body walking at hover | Standing, anchored | `u=(dir,0)`, hover **on** 10, walk speed, `L` 3.5 | Path alternates over each bump and under each lip in one polyline; no stall at any crossing; no state change (crouch is not needed — the path *is* the duck); average speed near walk speed; body never contacts | ✅ `BumpyTunnel` 97 px/s |
| 2 | **Jump into a 2-high tunnel** | tunnel mouth ahead, body airborne on a jump arc arriving slightly above the mouth's free band | Fall state| `u=(+1,0)` (intent = right, not the arc), hover **off**, progress unbounded, corner channels | Path begins with the player already having jumped, moving right and upwards/downwards in an arc towards the tunnel entrance; body enters low and clean, no bonk on the lip | ❌ launched guard excludes the engine (§7.1); |
| 3 | **Covered jump** | body standing under a 2-high slab with open air very close to one side; player presses jump with no horizontal input | Jump state, grounded under cover | intent pure-vertical → **one solve** `u=(0, -1)` (use different u, bonk cutoff, if left or right is pressed), hover **off**, unbounded; take the cheaper far-band cost, bonk if too far from an open ceiling | The open side wins if close, (the covered side bonks at the slab); path = sideways shuffle along the floor then a rise once clear of the slab edge; the leg-impulse channel fires **when the path turns up**, not at the button press; no slide-then-launch logic in the state | ❌ `CoveredJumpState.TryPickOpenDir` + bespoke launch still own this (§7.3) |
| 4 | **Rest** | flat floor, no input, body at hover | Standing, anchored | `dir == 0` → **no DP**; ref hover column, hover on 10 | No bobbing (\|vy\| < 5), no drift (\|vx\| < 5); the engine never fabricates a direction | ✅ `AtRest_StaysPut` |
| 5 | **1-high step while walking** | flat floor, 1-high ledge ahead | Standing | `u=(dir,0)`, hover on 10, walk | Path ramps up the block's C-obstacle bevel (≈45°) and re-hugs hover on top; x carry continues through the climb; on the ledge the body rides one tile higher | ✅ `OneHighStep` |
| 6 | **Tall wall** | wall spanning the whole window ahead | Standing | `u=(dir,0)`, hover on 10, walk | DP bonks (far band unreachable) at the wall; reference carries straight into it; rows truncate; body stops ≈6 px from the face at hover height — **no climb, no planned brake, no push-back** | ✅ `TallWall_HonestStop`, `FullHeightWall_Bonks` |
| 7 | **Free-standing 2-high wall** | 2-high column with open air above | Standing | `u=(dir,0)`, hover on 10, walk | *Path*: routes over (edges are geometry, §3.3 — accepted). *Motion*: the legs cannot deliver a 32 px rise from a walk, so tracking residual grows → **give-up** (§4.3) → honest bonk as in row 6. The path being over the wall must not make the body float up it | 🟡 path pinned (`FreeStandingTwoHighWall_RoutesOver`); give-up ❌ not built — today the servo strains toward the crest |
| 8 | **Ledge drop while walking** | flat floor ending in a drop of ≥2 tiles | Standing → Falling | `u=(dir,0)`, hover on 10, walk | Reference descends **no faster than gravity** while x carries at full walk speed — no "grab" at the lip (arc-length pacing would halve speed), no dive; once the lower floor enters the window the hover term re-binds and the path hugs it | 🟡 gravity rule implemented in `FoldLattice`; no gate |
| 9 | **Neutral jump in open air** | flat floor, jump with no horizontal input, nothing overhead | Jump state | intent pure-vertical: solve `u=(0,−1)` **first**; only if it bonks at the seed (row 3's slab) fall back to the two tilted solves | A vertical path — the body must **not drift sideways** on a neutral jump. (With `cosθ` 0.5 the tilted `u` admits `(0,−1)` but charges it steepness, so the two-solve rule alone would pick a diagonal in open air — this row is the refinement §7.2 needs) | ❌ jump states not on the engine; rule not yet in the plan text |
| 10 | **Diagonal hop over a block** | 1-high block ahead, player holds right + jump | Jump state | `u=(+1,−1)/√2`, hover **off**, unbounded, leg-impulse + air channels | Path rises over the block's C-obstacle and continues; "as fast as possible" spends the leg channel at launch while grounded; lands beyond the block; the same block *walked* into (row 5) is a climb, not a jump — the difference is only `u` and hover | ❌ jump states not on the engine |
| 11 | **Crouch at a 1-high block** | crouching under a 2-high ceiling, 1-high block ahead | Crouched | `u=(dir,0)`, hover on **0**, crouch speed | Body stays low and stops at the block (honest bonk) — a crouch never mounts ledges (`CrouchClimbReachUp` 4). **Known gap:** edges carry no climb band, so today's path routes over the block exactly as row 5; needs a per-state rise cap or steepness weight on the solve, not on the edges | ❌ design gap, logged in BACKLOG |
| 12 | **2-wide pit while walking** | flat floor with a 2-tile gap; player holds right | Standing → Falling | `u=(dir,0)`, hover on 10, walk | No auto-jump, no auto-brake: the path continues at hover into the gap (no floor below → no hover cost), x carries at full speed, the body falls at gravity (row 8's rule) and re-binds on the pit floor if it is in the window. A 1-wide gap is not a gap (C-obstacles of the two edge tiles overlap): the path carries straight across | 🟡 follows from rows 6/8; no gate |
| 13 | **Landing on flat, holding right** | body descending onto a floor | Falling → Standing | engine **excluded** while `vy > MaxGroundEngageVnRel` (plunging); re-admits on the anchored frame | Impact honesty: no air-brake softening of a slam; the first admitted frame's path is row-1 shaped (hover re-bind) and the carry resumes at the ramp | ✅ regime guard inherited (`Admit`); no lattice-specific gate |
| 14 | **Knockback** | body hit, `PreserveExternalVelocity` set | any | engine **excluded** | No correction at all — the fold does not fight combat momentum | ✅ `Admit` guard |

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
