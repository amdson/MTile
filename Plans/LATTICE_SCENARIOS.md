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
| `hover` / `hoverOffset` | the state (`FoldProfile.Hover`) | Standing `on / 10`; Crouched `on / 0`; Falling / WallSliding `off` (level line); Jump (ground, double, wall) `off` |
| `Rising` | the state (`FoldProfile.Rising`) | `u = (dir, −1)^` while a jump is held — the launch is the legs along a rising path |
| `RiseCost` | the state (`FoldProfile.RiseCost`) | price per px climbed on the path, traded against `ProgressWeight` at the goal — Standing/Falling 16 (mounts 16 px, refuses 32, even pressed against it), Crouch 30 (never mounts), Jump 0; drops are free |
| progress target | the state | `MaxWalkSpeed`×mods (Standing), `CrouchMaxWalkSpeed` (Crouched), **unbounded** (jumps) |
| window `L` | config `LatticeLookaheadTiles` (3.5) | a state may shorten it; nothing lengthens it (§4.2 — the autopilot dial) |
| cone `cosθ` | config `LatticeConeCos` (0.05 — "90° − ε") | structural (the DAG condition); every forward offset is an edge, steepness is priced not filtered; not a per-state knob |
| weights | config `HoverWeight` 3/px (linear), `ProgressWeight` 7 (the argmax goal's progress worth); the climb price is the state's `RiseCost` (below); steepness, length and seed-velocity costs removed | the §4.1 taste surface |
| seed run / seed bias | — | **both off** (`LatticeSeedRunPx` 0, `LatticeSeedWeight` 0): with a re-planning tracker either one feeds the current velocity back into the target (fourth and tenth passes) |
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
| 1 | **Bumpy corridor** | 3-tile interior, alternating 1-high floor bumps and 1-low ceiling lips; body walking at hover | Standing, anchored | `u=(dir,0)`, hover **on** 10, walk speed, `L` 3.5 | Path alternates over each bump and under each lip in one polyline; no stall at any crossing; no state change (crouch is not needed — the path *is* the duck); average speed near walk speed; body never contacts | ✅ `Row01` 79.2 px/s, `BumpyTunnel` 78.2 (eleventh pass; 89 at RiseCost 6) |
| 2 | **Jump into a 2-high tunnel** | tunnel mouth ahead, body airborne on a jump arc arriving slightly above the mouth's free band | Fall state | `u=(+1,0)` (intent = right, not the arc), hover **off**, progress unbounded, corner channels | Path begins with the player already having jumped, moving right and upwards/downwards in an arc towards the tunnel entrance (the seed run fixes the first stretch to the arc); body enters low and clean, no bonk on the lip | ✅ `Row02` — horizon QP: arrives 4 px above the band and enters (the engine now runs in Falling; §7.1's launched guard is gone with the ref tail) |
| 3 | **Covered jump** | body standing under a 2-high slab with open air very close to one side; player presses jump with no horizontal input | Jump state, grounded under cover | intent pure-vertical → **one solve** `u=(0,−1)` (a different `u`, with the bonk cutoff, if left or right is pressed), hover **off**, unbounded; bonk if too far from an open ceiling | The open side wins **if close** — the tile C-obstacle's corner bevel is a ≈45° ramp the `(±1,−1)` offsets can ride, so a body within a few px of the slab's edge rises out diagonally; deeper under the slab no rising edge exists and it bonks honestly; the leg-impulse channel fires **when the path turns up**, not at the button press; no slide-then-launch logic in the state | near ❌ `Row03_NearEdge` skipped — the DP finds the bevel escape; a neutral press has no x channel to start along it (actuator-list decision, tenth pass); far ✅ `Row03_FarFromEdge` (bonks, no shuffle); `CoveredJumpState` yields on the engine |
| 4 | **Rest** | flat floor, no input, body at hover | Standing, anchored | `dir == 0` → **no DP**; ref hover column, hover on 10 | No bobbing (\|vy\| < 5), no drift (\|vx\| < 5); the engine never fabricates a direction | ✅ `AtRest_StaysPut`, `Row04` |
| 5 | **1-high step while walking** | flat floor, 1-high ledge ahead | Standing | `u=(dir,0)`, hover on 10, walk | Path ramps up the block's C-obstacle bevel (≈45°) and re-hugs hover on top; x carry continues through the climb; on the ledge the body rides one tile higher | ✅ `OneHighStep` |
| 6 | **Tall wall** | wall spanning the whole window ahead | Standing | `u=(dir,0)`, hover on 10, walk | DP bonks (far band unreachable) at the wall; reference carries straight into it; rows truncate; body stops ≈6 px from the face at hover height — **no climb, no planned brake, no push-back** | ✅ `TallWall_HonestStop`, `FullHeightWall_Bonks`, `Row06` |
| 7 | **Free-standing 2-high wall** | 2-high column with open air above | Standing | `u=(dir,0)`, hover on 10, walk | *Path*: routes over (edges are geometry, §3.3 — accepted). *Motion*: the legs cannot deliver a 32 px rise from a walk, so tracking residual grows → **give-up** (§4.3) → honest bonk as in row 6. The path being over the wall must not make the body float up it | ✅ `Row07` (eleventh pass): the argmax refuses the wall at the face (RiseCost 16); 4 px of strain |
| 8 | **Ledge drop while walking** | flat floor ending in a drop of ≥2 tiles | Standing → Falling | `u=(dir,0)`, hover on 10, walk | Reference descends **no faster than gravity** while x carries at full walk speed — no "grab" at the lip (arc-length pacing would halve speed), no dive; once the lower floor enters the window the hover term re-binds and the path hugs it | ✅ `Row08` (eleventh pass): a real fall (max vy 213), caught by Standing, lands at hover |
| 9 | **Neutral jump in open air** | flat floor, jump with no horizontal input, nothing overhead | Jump state | intent pure-vertical: **one solve** `u=(0,−1)` (row 3's rule — there is no tilted fallback) | A vertical path — the body must **not drift sideways** on a neutral jump; row 3's diagonal escape only ever fires against a bevel, never in open air | ✅ `Row09` — on the engine: 31.5 px, 0 drift (61.5 before `LegReach` became the standing probe — the jump-height retune, eleventh pass) |
| 10 | **Diagonal hop over a block** | 1-high block ahead, player holds right + jump | Jump state | `u=(+1,−1)/√2`, hover **off**, unbounded, leg-impulse + air channels | Path rises over the block's C-obstacle and continues; "as fast as possible" spends the leg channel at launch while grounded; lands beyond the block; the same block *walked* into (row 5) is a climb, not a jump — the difference is only `u` and hover | 🟡 `Row10` skipped by 1 px — clears the block and lands at hover, apex 18.8 px against a 20 px bar. Not the leg fade (the neutral jump is 60 px at the same settings): the 45° plan's band couples the rise to the x speed — a plan-shape decision (twelfth pass) |
| 11 | **Crouch at a 1-high block** | crouching under a 2-high ceiling, 1-high block ahead | Crouched | `u=(dir,0)`, hover on **0**, crouch speed | Body stays low and stops at the block (honest bonk) — a crouch never mounts ledges (`CrouchClimbReachUp` 4). **Known gap:** edges carry no climb band, so today's path routes over the block exactly as row 5; needs a per-state rise cap or steepness weight on the solve, not on the edges | ❌ `Row11` skipped — the planner refuses the block (`CrouchRiseCost` 30, bonk); **`MantleState` fires from the crouch and vaults it** — state arbitration, its own thing (eighth pass trace) |
| 12 | **2-wide pit while walking** | flat floor with a 2-tile gap; player holds right | Standing → Falling | `u=(dir,0)`, hover on 10, walk | No auto-jump, no auto-brake: the path continues at hover into the gap (no floor below → no hover cost), x carries at full speed, the body falls at gravity (row 8's rule) and re-binds on the pit floor if it is in the window. A 1-wide gap is not a gap (C-obstacles of the two edge tiles overlap): the path carries straight across | 🟡 follows from rows 6/8; no gate |
| 13 | **Landing on flat, holding right** | body descending onto a floor | Falling → Standing | engine **excluded** while `vy > MaxGroundEngageVnRel` (plunging); re-admits on the anchored frame | Impact honesty: no air-brake softening of a slam; the first admitted frame's path is row-1 shaped (hover re-bind) and the carry resumes at the ramp | ✅ `Row13` (eleventh pass): 260 of 270 — Falling has no hover and no upward air force; the legs catch only where Standing owns the body |
| 14 | **Knockback** | body hit, `PreserveExternalVelocity` set | any | engine **excluded** | No correction at all — the fold does not fight combat momentum | ✅ `Admit` guard |
| 15 | **Wall slide** | airborne beside a tall wall, pressing into it | WallSliding | `Fall` profile: `u=(dir,0)` into the face (the DP finds nothing), hover **off**; the state's drag and FSDs are the baseline | The descent is the state's own (equilibrium 80 px/s from `SlideDrag`), no lift, no push-off; the held direction leans into the wall; lands at hover and stands | ✅ `Row15` (twelfth pass): slide max vy 80.0 = corrector-off control; never left the wall |
| 16 | **Double jump in open air** | falling, press jump with or without a direction | DoubleJumping | `Jump` profile; the impulse + hold force are the state's (nothing to push against in free air); **no engine actuators in the air** | The rise is the state's (58.9 px from the press), no drift when neutral; the engine adds nothing off the ground | ✅ `Row16` both cases (twelfth pass): 58.9 = control. Before the air channels were masked: 39.4 held-right — `AirVertical` bent the arc toward the plan |
| 17 | **Wall jump** | sliding down a tall wall, press jump holding into the wall (kick off, arc back, re-slide) or away | WallJumping | `Jump` profile; kick-off, hold force and the state's air steering are the baseline; no air actuators | The arc is the state's (54.2 px), the into case arcs back and re-enters the slide; the DP planning up the face changes nothing because there is nothing to push with | ✅ `Row17` both cases (twelfth pass): rise and kick-off reach = control, re-slide ✓ |

## Tests

`MTile.Tests/Sim/LatticeScenarioTests.cs` — one test per encoded row (1, 2, 3
near + far, 4, 6, 7, 8, 9, 10, 11, 13, 15, 16, 17), all under `FoldEngine = "lattice"`,
asserting the table's *correct* behavior. Rows 5, 12, 14 are deliberately
not encoded yet. Tests the engine cannot pass today are `Skip`ped with the
row's blocker in the reason — that list is the next cycle's checklist.

Status on 2026-08-24 (at commit): **pass** — 1, 2 (today via the qp airborne
path, not the lattice engine), 3-far, 4, 6, 7, 9. **Skipped** — 3-near, 8, 11,
13.

### Fifth pass (2026-08-24) — path-sampled reference, H = 5, exact step bound — current

Three changes on the fourth pass, all from the design discussion: (1) the
**reference is the polyline sampled at the body's current speed** — `p̂_T`
at arc length `|v|·(T+1)·dt` from the body, band ±½ cell *perpendicular* to
the path's local direction there, progress free along it (the fourth pass
banded a straight line through the first node, which diverged from the
polyline within the horizon); (2) **horizon 5** (stopping times are 1–4
ticks); (3) **the solver's step bound now carries `|n̂_j · axis_c|`** for
axis-only channels — the exact Hessian row-sum entry; without it the 20
vertical band rows shrank the horizontal Drive's step until it output 34 of
a 3000 cap. Masks were already constant. Seed run off. The bound change
touches the `qp` engine's numerics too: its gates were re-run and the
failure set is unchanged (the pre-existing six in that slice).

| | fourth pass (H=10, line band) | (1)+(2) only | **(1)+(2)+(3) — committed** |
|---|---|---|---|
| row 1 corridor | 46.7 ✗ | 30.6 ✗ | **65.8 ✓** |
| engine tunnel (spawn 3.6 px higher) | 45.9 ✗ | 30.6 ✗ | **30.6 ✗ — stuck at x=330, see below** |
| row 2 tunnel entry | ✓ (lip 74) | ✓ (lip 73) | **✓ (lip 73)** |
| row 7 free-standing 2-high wall | strain 7.7 ✓ | 5.1 ✓ | **6.6 ✓** |
| row 8 ledge drop | ✓ (72.0) | ✓ (72.5) | **✓ (73.2, max vy 178, full carry)** |
| row 13 landing (max vy / 270) | 187 ✗ | 202 ✗ | **201 ✗** |
| rest / tall wall / step / duck-in / rollback / rows 3-far, 4, 6, 9 | ✓ | ✓ | **✓** |
| rows 3-near, 11 | ✗ | ✗ | **✗** |
| tunnel sim step (Release, warmed) | 159 µs | — | **47 µs** |

**The engine-test tunnel is a terrain finding, not a tracker one.** Its
`x = 330.3` was bit-identical across the solver change — the body is stuck,
not slow. The corridor's lip at tile 19 (row 3) and bump at tile 21 (row 5)
leave, in C-space with the 2 px margin, a seam exactly one lattice cell
wide: at x = 328 the free column is y ∈ [62, 82]; at x = 324 only y ≥ 75; at
x = 331 only y ≤ 69 — a 12 px rise in 6 px of run that the DP threads only by
two exact `(1,−2)` steps, and a seed one cell off (326, 76) bonks with 4
reachable cells. The old servos shoved the real body (2 px smaller than the
inflated obstacle) through the margin; the horizon tracker honours the path
and, on a bonk, carries straight into the bump. Which spawn threads the seam
is a phase accident (row 1's does, the engine test's doesn't). The design
question it raises is what the margin means to the bitmap versus to the
tracker's band; not decided here.

### Twelfth pass (2026-08-24) — the remaining unguided states; no actuators in free air — current

WallSliding, DoubleJumping and WallJumping join the engine (Dropdown stays as
it is). Each is one line: the profile the state hands `ApplyAmbient` —
`Fall` for the slide (Falling against a wall), `Jump` for the two launches
— with everything the state did before kept as its baseline: the slide's
drag and FSDs, the launches' impulse, hold force and air steering. Unlike
the ground jump, these launches are not the legs': in free air there is
nothing to push against, so the impulse stays the state's own. `OnLattice`
is now one gate on `MovementState`.

With this, no state runs the qp coast/row path on the lattice engine any
more — `AmbientCorrector.Apply` past the dispatch is `qp`/`ref` only.

One principle fell out of the double jump's trace. Held-right, the engine
cut the rise from 58.9 to **39.4 px**: `AirVertical` (300 px/s², now
down-only) pushed down 5 px/s per tick for 16 ticks to bend the arc toward
the plan — the 45° line while DoubleJumping, then Falling's *level* line
while the body was still rising at −180 — and `AirLateral` drove vx to 171
past the state's 150 air cap, after which the state's own air control
dragged it back (two authorities). Neither channel is the engine's to have:
they are the qp fold's flight steering, duplicating the state's air control
minus its speed cap, and a plan that knows nothing of the body's momentum
must not be enforced where the body cannot follow it. **The lattice engine
has no actuators in free air** — `LatticeTracker` masks AirLateral and
AirVertical (the eleventh pass's `airUp` flag is gone); the engine acts
where it can push: near the floor (legs, drive, tuck, redirect) and at a
plantable corner. Off the ground the body follows physics and the state's
air control.

| | eleventh pass | **twelfth pass** |
|---|---|---|
| row 16 double jump held-right / neutral | — (qp coast path) | **58.9 / 58.9 = control** (39.4 held-right before the air masks) |
| row 17 wall jump into / away | — (qp coast path) | **54.2 / 54.2 = control**, re-slide ✓ |
| row 15 wall slide | — (`Off`) | **80.0 = control**, never leaves the wall |
| row 13 landing (max vy / 270) | 260 | **260** |
| row 8 ledge drop | ✓ 73.6 | **✓ 73.1** |
| row 1 corridor | 79.2 | **77.3** |
| row 2 tunnel entry (lip 75.5) | ✓ | **✓ (75.6)** |
| rows 4, 6, 7, 9, 3-far; engine and planner gates | ✓ | **✓** (29 passed / 4 skipped) |
| `qp` / `ref` scenario slices | — | **unchanged** (21 passed; `BuildFold` is back to its pre-eleventh signature) |

**Tuning, from live play (same day).** Two config values, both hot-reloaded,
both shared with `qp` (its slices unchanged, 45 passed):
`FoldLegPushFadeSpeed` 200 → **400** and `FoldTuckForce` 1200 → **3600**.
The leg cap fades to zero at the fade speed (`ChannelCap[0] = LegForce ·
(1 − riseSpeed/fade)`), so on the engine it is the launch's ceiling — the
jump-height retune the eleventh pass left open is this one number, not
`LegReach`: neutral jump 31.5 → **60.1 px** (the old 61.5) with the standing
rest untouched (0.00 deviation at every setting; leg force 9000 would give
75). The corridor's corners on the engine are the legs (bumps) and the tuck
(lips) — `CornerAssist` is masked off and the redirect never fires there
(`SupportReach` 25 > `LegReach` 17, so near ⇒ Grounded); `FoldCornerForce`
1500 → 4000 measured no change at all. Tuck 3600: corridor **77.3 → 92.6
px/s** with no hard vertical events (2400 gave 92.4 with ten frames of
|vy| > 150; drive 4500 on top gives 96.8, not taken). Row 10's hop stays at
18.8 px through all of this — see its row.

Seen in the slide's trace, not acted on: with the DP blocked at the face
the tracker still emits the progress row along `u` (no path → `tLast = u`),
so AirLateral pressed into the wall at its cap before the masks — the same
lean Standing has at a wall (row 6). Standing pressed against the wall
after the landing creeps up at −1.2 px/s (the RiseCost strain at the
face); the `qp` landing beside it dives 8 px below hover and bounces for a
second, the lattice one settles in 15 frames.

### Eleventh pass (2026-08-24) — Falling without hover; legs reach = standing reach — superseded

Decided: whenever the stand channel (legs) is available, Falling is
inactive; then Falling drops hover. Changes, in order:

1. **`LegReach` = the standing ground probe** (`Radius + ProbeSlack` = 17 px
   above the C-space envelope; was 42). Before, a falling body had live
   legs over a 25 px band where Standing was not yet active — the float at
   leg reach measured in earlier passes. Shared with the `qp` engine, whose
   gates *improved* (6 → 4 pre-existing failures in that slice: the
   ballistic-landing and vault-cap tests now pass).
2. **`FoldProfile.Fall`**: hover off (the plan is a level line at the body's
   height — obstacle avoidance only), rise cost the standing one, no speed
   limit; `FallingState` hands it on the lattice engine. And **no upward
   air force**: `AirVertical` is down-only for every lattice profile
   (`BuildFold(airUp: false)`; `qp` keeps its two-sided nudge).
3. **Reference rows.** The band and speed rows are flagged `Reference` and
   `BuildFold`'s corner/redirect feature activation ignores them. They are
   hard rows with non-floor normals, which that heuristic reads as lip
   undersides — the Redirect disc had been "planting" against the band in
   free air and converting horizontal speed into lift (a 4-tile fall held to
   27 px/s). The disc still fires at real corners (the corridor's).
4. **`FoldRiseCost` 6 → 16, `LatticeHoverWeight` 2 → 3.** The binding case
   for the rise price is the body *pressed against* the obstacle, where
   standing still earns nothing and the window's whole worth (392) is the
   climb's reward: 16 mounts a 16 px block (256) and refuses a 32 px wall
   (512) at the face — at 6 the wall was "worth it" from the face and the
   legs strained 13 px. The hover weight follows the rise cost (the
   rise-to-hover trade is `w_rise` vs `w_hover × nodes remaining`).

| | tenth pass | **eleventh pass** |
|---|---|---|
| row 13 landing (max vy / 270) | 203 ✗ | **260 (96%) ✓ — lands at hover, carry intact** |
| row 8 ledge drop | ✓ (73.2, max vy 202 — walked down) | **✓ (73.6, max vy 213 — a real fall, then caught)** |
| row 7 free-standing 2-high wall | strain 8 ✗ | **4 px ✓ — the argmax refuses at the face** |
| row 2 tunnel entry | ✓ | **✓** |
| row 1 corridor / engine tunnel | 84.4 / 85.9 | **79.2 / 78.2** (bumps cost more at 16) |
| row 9 neutral jump | 61.5 px | **31.5 px** — see below (60.1 again after the twelfth pass's leg-fade retune) |
| row 10 diagonal hop | apex 28 px ✓ | **apex 19 px ✗ (bar 20); still clears the block, lands at hover** |
| rest / tall wall / step / duck-in / rollback / rows 3-far, 4, 6 | ✓ | **✓** |
| rows 3-near, 11 | ✗ | **✗** (unchanged: actuator decision; Mantle) |

**The jump-height retune, now concrete.** A launch's powered rise is the
legs' reach past hover: 22 px at `LegReach` 42, 7 px at 17. The neutral
jump went 61.5 → 31.5 px and the hop's apex 28 → 19. `LegReach` is the
right number for *standing* ("legs available ⇔ Standing"); a push-off is a
different quantity — legs extending from crouch to full — and if the old
height is wanted it is the jump profile's own reach (a profile parameter,
like its hover flag), not a longer `LegReach`. Left for the decision.

### Tenth pass (2026-08-24) — jump states on the engine, steps 2–3: running and covered jumps — superseded

On the lattice engine `RunningJumpState` and `CoveredJumpState` yield; both
are `JumpingState` with the same profile — the running jump is `u` tilted
by the held `dir`, the covered jump is `JumpingState` allowed under a low
ceiling with the DP deciding (the bevel escape near an edge, a bonk deeper
in). One more subtraction was needed: **the seed velocity bias is off**
(`LatticeSeedWeight` 20 → 0). It is the soft cousin of the seed run and
makes the same loop at lower gain: the seed's out-edge follows the current
velocity, the beads sit on that first segment, the band holds the body to
it, the velocity stays what it was. The neutral jump was immune only
because `v̂ = 0` at rest; a running hop launched at −45 px/s per tick
instead of the legs' −100 and reached 19 px.

| | before | **tenth pass** |
|---|---|---|
| row 10 diagonal hop (new test) | apex 56.6 (19 px), cleared the block via **Parkour** vaulting it | **apex 47.8 (28 px), clears the block, lands at hover ✓** |
| row 9 neutral jump | 61.5 | **61.5** |
| row 1 corridor / engine tunnel | 89.4 / 87.1 | **84.4 / 85.9** (the bias had been helping the walk a little) |
| rows 2, 8; rest, wall, step, duck-in, rollback, 3-far, 4, 6 | ✓ | **✓** |
| row 3-near covered jump | ✗ | **✗ — planner ✓, actuator missing, see below** |

**Row 3 is now an actuator-list question, not a planner one.** From the
seed 3 px inside the slab's edge the DP finds the escape exactly as the
table describes — `(133,75) → (139,72) → (139,69) → …` up the bevel —
and seeds 7+ px deeper bonk with no reachable cell. The sim does not rise
because the path's *first* segment is 8 px sideways at the same height (the
body's own cell is inside the slab's margin, so the seed snaps beside it),
and a neutral press has no x channel: drive and air-lateral are masked on
`dir ≠ 0`, the redirect on `!Grounded`. Candidates, for the profile's
channel list: (a) the redirect — physically a plant-and-deflect against the
bevel, the honest actuator for "push up into a slope and come out
sideways" — allowed for `Rising` profiles while grounded; (b) the drive
along the plan's first tangent regardless of `dir`. (a) is the physics;
(b) is closer to the old slide phase.

Also in the hop's trace, for the Falling decision (item 4): the moment the
jump ends, `Falling` with hover on pulls the body down at +43 px/s per
tick and the redirect sheds 30 px/s of x near the block's corner — the
descent is a dive, and the block was cleared with help from the climb
family. The hover flag for `Falling` is now visibly the next contract
decision.

### Ninth pass (2026-08-24) — jump states on the engine, step 1: the neutral jump — superseded

`JumpingState` on the lattice engine fires nothing: `Enter` sets no
velocity and adds no source constraint, `Update` applies no hold force.
It hands the tracker `FoldProfile.Jump` — hover **off**, `Rising` (u =
`(dir, −1)^` while the button is held), `RiseCost` 0, no speed limit — and
the legs spend themselves along a rising path. The state ends at the apex
or on release; `Falling` owns the descent as before. `qp`/`ref` paths are
untouched (the engine check gates every change). Two engine changes were
needed, both subtractions:

- **No Δ-smoothing in the lattice tracker.** `CorrectorDeltaWeight` and the
  leaky `PrevApplied` anchors are the `qp` engine's anti-bang-bang
  regularizer; they came along with reusing its `Problem` and are not in
  §3.7's objective. Measured with them on: the launch's tick-0 push was
  −2 px/s against gravity (anchored to `Standing`'s near-zero output) and
  the body fell; the apex rule then ended the jump at 3 px. With them off
  the legs are at their cap on tick 0. Side effect: the **corridor rose
  76.7 → 89.4 px/s** — the smoothing had been throttling the walk too.
- **A lateral tie-break in the DP** (0.05/px perpendicular to `u`, an ε):
  with hover off and rise free every route up costs the same, and the DP
  would have zigzagged a straight-up jump.

| | before (bespoke `JumpingState`) | **engine jump** |
|---|---|---|
| row 9 neutral jump | 60.8 px, 0 drift | **61.5 px, 0 drift** — tick-0 push −100 px/s (legs at cap), asymptote −180 at the fade |
| row 1 corridor / engine tunnel | 76.7 / 75.7 | **89.4 / 87.1** |
| rows 2, 8; rest, wall, step, duck-in, rollback, 3-far, 4, 6 | ✓ | **✓** |
| rows 3-near, 7, 11, 13 | skipped | skipped (unchanged) |

Jump height came out equal by the constants' coincidence: the legs push
until leg reach (42 px) runs out, fading to zero at `FoldLegPushFadeSpeed`
200, so the body leaves at ~180–200 px/s and coasts ~30 px. Next: the
diagonal hop (row 10 — `RunningJumpState` folds into this with `dir` held)
and the covered jump (row 3 — `JumpingState` allowed under a low ceiling,
the bevel escape falling out of the DP).

### Eighth pass (2026-08-24) — rise priced per pixel, per profile — superseded

Three changes on the seventh pass:

1. **Climb cost = pixels climbed** (`w · max(0, −Δy)` per edge), replacing
   the per-edge-angle steepness. Table-invariant: a `(1,3)` edge and three
   `(1,1)` edges cost the same 9.6 px. Drops are free — gravity delivers
   them, and charging them would let the argmax refuse to walk off a ledge.
2. **The price is the state's**: `FoldProfile.RiseCost` (`FoldRiseCost` 6,
   `CrouchRiseCost` 30), passed to `Solve` beside `hover`. The accepted new
   parameter: what a state will and won't climb. Standing's 6 mounts 16 px
   (96) whenever ≥ 14 px of window lie beyond and never mounts 32 px (192)
   at `ProgressWeight` 7; Crouch's 30 makes 16 px (480) never worth a 56 px
   window. `IntentTilt` removed — it could not do this job (it taxed level
   edges as much as climbs) and has no other.
3. **Hover cost made linear** (`w · |dev|`, `LatticeHoverWeight` 0.05/px² →
   2/px). A quadratic hover against a linear rise crossed at ~7 px — inside
   the hover band — so the path tolerated sags up to 7 px and skimmed block
   tops low (measured: the block test rose 9.4 px of 15). With both linear,
   "rise back to hover" vs "stay sagged" compares `w_rise` against
   `w_hover × nodes remaining`, independent of the sag's size; 2 makes
   recovery worth it with > 3 nodes left, while a full-depth duck for the
   whole window (272) is still cheaper than the progress it buys (392).

| | seventh pass | **eighth pass** |
|---|---|---|
| row 1 corridor / engine tunnel | 78.8 / 79.1 | **76.7 / 75.7** ✓ (the approach to each bump creeps: a climb is worth it only once enough window lies beyond it) |
| 2-high wall, planner | routed over (cost 45) | **refused — path stops at the face** ✓ |
| row 7 (sim) | strain 6.9 ✓ | **8.1 ✗** (gate 8; the bevel creep) |
| row 8 ledge drop | ✓ | **✓ (73.1, max vy 201)** |
| row 2 tunnel entry | ✓ | **✓ (lip 74.9)** |
| rest / tall wall / step / duck-in / rollback / rows 3-far, 4, 6, 9 | ✓ | **✓** |
| row 13 | 201 ✗ | **203 ✗** (unchanged) |
| tunnel sim step / flat-ground solve (Release, warmed) | 104 µs / 108 µs | **110 µs / 224 µs** (the smaller margin doubled the reachable cells, 460 → 626) |
| row 11 crouch at a block | mounts ✗ | **mounts ✗ — but not the planner's doing, see below** |

**Row 11 was never a planner question.** The per-frame trace under
`CrouchRiseCost` 30: the crouched body approaches at 52 px/s with the
planner reporting `bonk`, path flat at y 81.6 ending before the face — and
at frame 159 **`MantleState` fires from the crouch** (vx → 100, vy → −156)
and vaults the block. The lattice engine refused; the climb-maneuver family
did the mounting, in every pass. That is state arbitration (Mantle's
preconditions under Crouched), outside this engine — its own thing.

One consequence of the argmax worth naming: a "bonk" now also means "chose
to stop short of the far band", which is a normal outcome in front of a
step while the climb is not yet worth it — the engine test that asserted
"no bonks on an open course" encoded the far-band goal and was relaxed.

**Runtime, first cut (same day): the argmax's exact prune.** A node's value
is `w_prog·(p − p_seed) − dp ≤ w_prog·L − dp`, so once `dp > w_prog·L` (392)
it can never beat the seed and the DP stops relaxing from it. Same result
on every scenario (all printed numbers bit-identical); flat-ground solve
**224 → 109 µs** (Release, warmed). The tunnel step stays ~109 µs because
that window is mostly blocked and the tracker's three passes dominate. Next
on this axis: derive the window's height above the seed from
`w_prog·L / RiseCost` (65 px standing, 13 crouched) so the dense stamp /
sweep / flood stop covering sky no path can afford — and only then is a
steeper lattice (triangular r=2: 12 edges for the ±3 table's 16 at ≈ the
same angular resolution, but a 79° max edge) affordable.

### Seventh pass (2026-08-24) — the argmax goal — superseded

**Goal rule changed** (`LatticePathPlanner`): the path ends at the reachable
node maximizing `w_prog · (p − p_seed) − dp[n]` over *every* reachable node.
The far-band rule ("cheapest node at p ≥ p_seed + L, else the furthest") is
its `w_prog → ∞` limit. A bonk is now a decision the costs make. Length
cost removed (`LatticeLenWeight`): every edge advances `p`, so progress
reward and length cost were one term. `LatticeProgressWeight = 7`.
**Intent tilt**: `FoldProfile.IntentTilt` rotates the state's `u` into the
floor — Standing 0, Crouch 30° — the hypothesis being that a crawling
intent makes a 1-high block "not worth it" with no per-state weight.

| | sixth pass | **seventh pass (argmax, tilt)** |
|---|---|---|
| row 1 corridor / engine tunnel | 78.6 / 78.7 | **78.8 / 79.1** ✓ |
| row 7 free-standing 2-high wall (sim) | strain 9.3 ✗ | **strain 6.9 ✓, ends at hover** — but see below |
| row 11 crouch at a block | mounts ✗ | **mounts ✗** (minY 56.5, unchanged) |
| rows 2, 8, 4, 6, 9, 3-far; rest/wall/step/duck/rollback | ✓ | **✓** |
| rows 3-near, 13 | ✗ | **✗** (unchanged) |

**What the planner-level sweep found** (progress weight 3–12, tilt 0/30/45°):

| route | cost (per-edge-angle steepness, ±3 table) | worth it at `w_prog = 7`? |
|---|---|---|
| 2-high wall, standing | **45** | yes — routed over at every `w ≥ 3` |
| 1-high block, standing | **13** | yes |
| 1-high block, crouch, tilt 30° | 102 | yes (the tilt taxes the *level* run too, ≈4/edge, so stopping short saves nothing) |
| 1-high block, crouch, tilt 45° | 159 / bonk | refused only at `w ≤ 3` |

So (a) the sim's row 7 passes for the old reason — the tracker doesn't
deliver the climb — not because the path stopped; (b) the single-weight
window for "mount 1-high, refuse 2-high" is **(0.5, 1.7)**, not the (5, 9)
estimated from 45° edges of one cell, and at that scale the hover term
(≈1.25/node at 5 px) is the same size — fragile; (c) the estimate was off
because **steepness is priced per edge angle, so the cost of a climb depends
on the offset table**: widening to `±3` made a `(1,3)` edge buy 9.6 px of
rise for 20.5, and a 32 px wall became ~3 edges. That table-dependence is
the actual defect this pass exposed, and it predates the argmax.

The argmax goal and the tilt are kept (the goal is the general rule; at
`w_prog = 7` it reproduces the far-band behaviour on every test); the wall
test that pins "not worth climbing" is skipped on this finding until the
cost structure is decided.

### Sixth pass (2026-08-24) — sliding beads — superseded

The idea below is built: `LatticeTracker` runs three outer passes of
*project → rows → solve*. Bead T is the nearest point on the reference
polyline (body → first node ≥ one cell away → nodes, last segment extended
as a ray) to the iterate's tick-T position — the free rollout on pass 0,
free + the previous solve's Δp after — made monotone along the path. Rows
are the perpendicular band at the bead, the speed rows, and the progress
row along the last bead's tangent; each pass cold-starts the solver
(determinism contract). Nothing else changed from the fifth pass.

| | fifth pass (current-speed sample) | **sixth pass (beads)** |
|---|---|---|
| row 1 corridor | 65.8 | **78.6 ✓** |
| engine tunnel (the seam spawn) | stuck at 330 ✗ | **78.7 ✓** — the bead is wherever the body is, so the seam's phase no longer matters |
| row 2 tunnel entry | ✓ (lip 73) | **✓ (lip 73, ends 77.8)** |
| row 7 free-standing 2-high wall | strain 6.6 ✓ | **strain 9.3 ✗** (gate 8) — the beads follow the over route more faithfully; the give-up question |
| row 8 ledge drop | ✓ (73.2) | **✓ (73.5, max vy 179, full carry)** |
| row 13 landing (max vy / 270) | 201 ✗ | **201 ✗** (unchanged: the path's slope) |
| rest / tall wall / step / duck-in / rollback / rows 3-far, 4, 6, 9 | ✓ | **✓** |
| rows 3-near, 11 | ✗ | **✗** |
| tunnel sim step (Release, warmed) | 47 µs | **104 µs** (three passes) |

**Margin = band (same day, after the beads).** The DP now inflates obstacles
by half a cell — the tracker's band — instead of `CorrectorMargin` (2 px, the
qp/ref engines'): one allowance, counted once, no separate number. Measured
effect on every scenario and engine test: **none** — every printed value is
bit-identical (the DP's chosen cells were never the ones the extra 0.4 px
freed; reachable cells 460 → 626 on flat ground). The corridor seam was
threaded by the beads, not by this. Kept for the principle and the removed
knob; `CorrectorMargin` still serves the qp/ref row builders.

**Linear progress objective (same day) — tried, reverted.** A true linear
term `−w·(t̂ · Δp_{H−1})` was added to `CorrectionSolver` (off unless set)
and used in place of the achievable-at-cap progress hinge:

| `w` | corridor / tunnel | row 7 strain | tall wall |
|---|---|---|---|
| hinge (committed) | 78.6 / 78.7 | 9.3 | ✓ |
| 2·10⁵ | 37.0 / 43.4 | — | ✓ |
| 10⁶ | 82.3 / 83.0 | 8.2 | ✓ |
| 4·10⁶ | 96.9 / 96.8 | **13.8** | **✗ both** (driven up/into the face) |

Why it was reverted despite 10⁶ being 5% faster: the linear pull never
vanishes, so `w / w_H` is a genuine trade between "as fast as possible" and
"stay on the path", with a cliff ~3× above the working value — a weight to
tune, which is what this design avoids. The hinge's depth is the channels'
reach at cap, so its pull dies exactly at saturation and the trade-off is
fixed by physics, not by a number. If the 5% ever matters, `w = w_H` is
the principled pairing (one constant, not two) — but it is still a pairing.

### Idea (2026-08-24) — sliding-bead path loss — BUILT as the sixth pass; kept for the derivation

The fifth pass's reference is the polyline sampled at the body's *current
speed* — a stand-in for where along the path the body will be at tick k.
The path-following formulation makes that a solved quantity instead: a
**bead** `s_k` (arc length along the lattice path) per QP tick, free to
slide, monotone:

```
min over z, s:   Σ_k w_c ‖z_{c,k}‖²  +  w_H Σ_k ‖p_k(z) − P(s_k)‖²  −  w_prog · s_{H−1}
                 s_0 ≤ s_1 ≤ … ≤ s_{H−1},   s_k − s_{k−1} ≤ v_max · dt
```

with `P(s)` the polyline at arc length `s` and `p_k(z) = p̄_k + Δp_k(z)`.
The band becomes exact (distance to the path, not to a line or a pre-timed
sample), progress is the last bead's arc length, and the speed limit is a
bound on bead spacing — one object for all three.

It is biconvex: for fixed `z`, the optimal `s_k` is the nearest-point
projection of `p_k` onto the polyline (closed form) followed by a running
max for monotonicity — that *is* the sliding; for fixed `s`, `P(s_k)` is a
constant point and the loss is quadratic in `z`. In the bead's local frame
the along-path residual is zero after projection, so only the perpendicular
term survives: two rows with normal `n̂_k` at the bead plus the progress row
along `t̂_k` — exactly today's row structure. So the whole change is *where
the reference point comes from*: nearest point on the path to the current
iterate instead of the point at `|v|·(k+1)·dt`. Alternate 2–3 outer passes
(project → rebuild rows → solve, cold-started each pass to keep the solver's
determinism contract) and it converges on the exact constraint.

Notes for when it is weighed: (a) even one pass improves on the current
scheme — projecting the *free rollout* onto the path puts a falling body's
bead below it rather than where the path's timing would; (b) cost is
~×(outer passes) on the 47 µs step; (c) the corridor seam is unchanged by
it — the perpendicular distance to a 63° two-step is the same geometry;
(d) the progress term is still a hinge with an achievable-at-cap depth on
the current solver, not a true linear objective — the same approximation as
now.

### Horizon-QP results (2026-08-24, fourth pass — superseded)

The one-tick tracker's overshoot is H = 1 myopia, so `LatticeTracker` became
the §3.7 solve at a short horizon (`AmbientHorizon`, 10 ticks) on the
existing `CorrectionSolver`: the nominal is the **exact free rollout**
(`v += (F_baseline + g)·dt`, `p += v·dt` — forces enter linearly, nothing is
linearized), the channels are `BuildFold`'s with masks and caps **frozen at
the body's current state**, and the rows are: a **band** (hard, ±½ cell
around the reference line — the path's resolution), a **speed limit** (hard,
displacement along intent ≤ state limit × t), and one **progress** row at the
last tick (soft, along the path direction, depth = what the channels could
add at their caps — it saturates the actuators and rests; no target speed).
The reference line is the path's direction at the first node ≥ one cell from
the body, through that node; `dir == 0` uses the hover column. CornerAssist
is masked off (its meaning was the coast's hard rows). Two runs, differing
only in the seed run:

| | one-tick tracker (3rd pass) | **horizon QP, seed run OFF** (committed) | horizon QP, seed run on |
|---|---|---|---|
| bumpy tunnel | 81.7 px/s | **45.9 ✗** (gate 55) | 41.0 ✗ |
| row 1 corridor | 88.2 | **46.7 ✗** | 45.9 ✗ |
| rest / tall wall / step / duck-in / rollback / row 4, 6, 9 | ✓ | **✓** | ✓ |
| row 2 tunnel entry | ✓ (lip 45) | **✓ (lip 74.0 — arrives 4 px above the band)** | ✗ flies straight off the roof (y −21) |
| row 7 free-standing 2-high wall | strains 11 px ✗ | **strains 7.7 px ✓** | 10 px ✗ |
| row 8 ledge drop | hovers 32 px up ✗ | **✓ — lands at hover (72.0), max vy 151, full carry** | hovers 18 px up, max vy 22 ✗ |
| row 13 landing (max vy / 270) | 138 ✗ | **187 (69%) ✗** | 176 ✗ |
| rows 3-near, 11 | ✗ | **✗** | ✗ |
| tunnel sim step (Release, warmed) | 22 µs | **159 µs** (16 solver sweeps × 31 rows × 7 channels × 10 ticks) | — |

Reading it: the horizon does what it was for — rows 7 and 8 pass for the
first time, the landing and the tunnel entry are the cleanest of any pass,
with no reference-side rules — and it costs corridor speed (46 vs 88) and
~140 µs a step. The two open results are the corridor speed (not yet
diagnosed; candidates are the progress row's soft scale against the band,
the Δ-anchor smoothing, and the band's tightness through a zigzag — all
inspectable, none measured) and row 13, where the band legitimately holds a
falling body to the path's descent slope. The seed-run column settles §3.5
finding 2: with a tracker that re-plans from the body every tick, the run is
a feedback loop — the reference direction is read on the run, so the current
velocity becomes the target (row 2 turned a jump arc into a straight line).
`LatticeSeedRunPx` now defaults to 0.

### One-tick tracker results (2026-08-24, third pass — superseded)

Decided after the second pass: drop the horizon entirely. `LatticeTracker`
takes the path's first step (the first node ≥ one cell from the body),
`v_des = t̂ · progressSpeed`, and projects `(v_des − v_free)/dt` onto the
channels available at the body's current state (BuildFold's caps and masks
evaluated at the body: legs / drive / tuck within LegReach, air-lateral /
air-vertical beyond; drive and air-lateral push only along intent). No
coast, rows, linearization, deform or servo; `dir == 0` and no-path solves
use the hover column (vertical dead-beat toward the hover line if anchored,
no vertical force otherwise). Not carried over: Redirect, CornerAssist, the
ledger.

| | old (ref tail + free servo) | second pass A (qp stack) | **third pass: one-tick tracker** |
|---|---|---|---|
| bumpy tunnel | 97 px/s | 65.8 | **81.7** ✓ |
| row 1 corridor | 94 | 62.3 | **88.2** ✓ |
| rest | ✓ | ✓ | **✓** (settles at y 74.0, vy 0) |
| tall wall | ✓ | ✓ | **✓** |
| row 9 neutral jump | 61 px, 0 drift | 61 | **61, 0 drift** ✓ (lands cleaner than qp: 74.0 steady vs 78→73 wobble) |
| row 7 free-standing 2-high wall | holds | strains 12 px | **strains 11 px ✗** |
| row 8 ledge drop | lands 6 px low | lands 5 px low | **hovers 32 px above the lower floor ✗** |
| row 13 landing (max vy / 270) | 234 (87%) | 250 (92.6%) | **138 (51%) ✗** |
| rows 2, 3-far, 4, 6 | ✓ | ✓ | **✓** |
| rows 3-near, 11 | ✗ | ✗ | **✗** |
| tunnel sim step (Release, warmed) | 48 µs | 123 µs | **22 µs** (the tunnel window is mostly blocked; a flat-ground solve alone is ~108 µs) |

**What the three failures are** (per-frame probe on the drop):

- Rows 8 and 13 are one mechanism, and it is the *target*, not a channel:
  `v_des = t̂ · walkSpeed` caps the desired descent at `walkSpeed · sin φ`
  (≈ 64–95 px/s on the path's 40–72° descents), so a body falling faster
  than that gets "need up" — served by air-vertical (300) beyond leg reach
  and by the **legs (6000)** within it. On the drop the body is caught at
  exactly the LegReach boundary (43.4 px above the floor: legs off above it,
  a 6000 px/s² kick below it) and hovers there. The seed run (§3.5) then
  turns the kicked-up velocity into the next tick's forced first step, so
  the target alternates up/down around that boundary. Both ingredients are
  design choices of the target, not of the actuators: (a) the speed along a
  *descending* tangent, (b) the current-velocity run feeding the target.
- Row 7 is the give-up question (§4.3): the path goes over the wall, the
  legs push toward it up to their cap, the body rises 11 px and stalls.

One wiring bug found and fixed during the pass, recorded so its symptoms are
not mistaken for design results: "no vertical demand" was first coded as
`v_des.y = v.y`, which the projection reads as "hold my vertical velocity" —
i.e. cancel gravity — and made the neutral jump rise 91 px instead of 61.

### Channel stack results (2026-08-24, second pass — superseded)

The engine was re-wired as decided: the `qp` flow unchanged (coast → obstacle
rows → progress rows → `BuildFold` channel stack → solve → tick-0 force) with
one substitution — the hover reference row's target is the lattice path's y
at `xRef` (`AmbientCorrector.EmitLatticeReference`) instead of the floor
envelope's climb-band / down-anchor rules. The free servo is gone
(`FoldLattice.cs` deleted). Two runs, differing only in the LegServo mask:

| | previous (ref tail + free servo) | **A: qp legs mask** (committed) | B: legs at support |
|---|---|---|---|
| bumpy tunnel (`FoldLatticeEngineTests`) | 97 px/s | **65.8** | 36.6 ✗ (gate 55) |
| row 1 corridor | 94 | **62.3** | 24.2 ✗ |
| rest (`AtRest_StaysPut`) | ✓ | **✓** | vy 6.1 ✗ |
| tall wall (engine test) | ✓ | **✓** | 4.6 px low ✗ |
| row 7 free-standing 2-high wall | holds at 74.0 | **strains to 63.3 ✗** | holds at 73.9 ✓ |
| row 8 ledge drop | lands 6 px low | **5 px low** (carry ✓, no dive ✓) | 7 px low |
| row 13 landing brake (max vy / uncorrected 270) | 234 (87%) | **250 (92.6%)** | 250 (92.6%) |
| row 13 rebind | 6 px low | **2 px low** | 7 px high |
| row 2 tunnel entry | ✓ (lip 57) | **✓ (lip 76.9)** | ✓ (lip 74.9) |
| rows 3-far, 4, 6, 9 | ✓ | **✓** | ✓ |
| rows 3-near, 11 | ✗ | **✗** | ✗ |

What the A/B isolates: the legs mask is exactly the row-7-vs-corridor trade
(at support: the legs cannot serve a path above the hover line, so they
neither strain up the wall nor push the body over a bump until it is on it);
the row-13 brake is **not** the legs (identical in A and B) — with the legs
off in the air the remaining actuators are air-vertical (300 px/s²) and
tuck; and the post-landing 5–7 px sag survives the servo's removal, so it
is in the reference/rows, not the actuator. `LegsAtSupport` is a code
constant in `AmbientCorrector`, off; A is committed because B fails three
engine gates.

Cost (Release, JIT warmed): a bumpy-tunnel sim step is **123 µs under lattice
vs 36 µs under ref** (the 119 µs solve is nearly all of it; the qp stack adds
little over ref's tail).

Forced choices in the wiring, none of them rules: `dir == 0` and a solve
with no path (seed pinned in an obstacle's margin) keep the `qp` envelope
reference; past the path's end the last node's y is held (the honest carry);
the at-support tolerance is one lattice cell.

### Findings from wiring the tests (engine, not fixed — first pass)

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
