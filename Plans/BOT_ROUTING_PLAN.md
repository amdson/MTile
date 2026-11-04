# Bot routing — following a player who runs, builds up, and camps

**Goal.** A P2 bot reaches any spot a player can reach — and in particular follows a player
who escapes upward (stacks blocks, camps on a pillar or ledge) — using the same verbs a
player has: walk, jump, wall jump, ledge grab / mantle, drop, **build**, and **dig**.

**Today** (see `Plans/PLAYER_AGENTS.md`): no path planning. Every brain chases the
perceived target in a straight line; reactive fallbacks fire when that stalls —
stuck-jump, `WallClimbManeuver`, `BreachManeuver` (dig), and, in Brawler only,
`BuildClimbManeuver` (elevator column under the feet) and `DropdownManeuver`. Fine on the
three flat league arenas; fails on a high ledge, a pillar with no adjacent wall, a target
behind a wall with a way round, anything that needs moving *away* first.

**Facts that shape the design**
- Placed tiles never collapse: support is checked only at placement
  (`ChunkMap.TryRequestTile`). Digging out a camper's pillar does not bring them down —
  the bot has to get up there.
- Movement is solver-driven (corrector / lattice), so a jump's arc is not exact. The
  planner only trusts **measured, conservative envelopes**; the executor is closed-loop
  and replans on a failed edge. Not built on `BallisticPredictor` (deprecated).
- Terrain is on screen, so the planner may read it live; the target's position stays
  behind the brain's perception delay (league rule 2).

## Architecture

1. **Nav graph over cells.** A *stand node* is an empty body-sized slot over a solid,
   non-hostile floor (opposing spikes excluded — `SpikeGuard.Hostile`). Built in a window
   around bot + target, cached, invalidated by terrain changes (tile events / terrain frame).
2. **Edges**, each a measured envelope plus a cost in frames:
   - walk; step-up (vault / mantle, 1–2 cells);
   - jump to (dx, dy) inside the calibrated table, with a clearance check along a
     conservative corridor around the arc;
   - drop off a ledge (capped by fall-impact damage);
   - wall-jump chain / single-face climb (`WallClimbManeuver`'s controller), ledge pull;
   - **build-up**: raise a column n cells from a stand node (≈ n × 6 frames + material,
     `BuildClimbManeuver`'s Raise);
   - **build-step / bridge**: place one block that creates a new stand node (stairs,
     gap fill);
   - **dig**: break a blocking cell (cost from material strength and swing rate).
   Build and dig are macro-edges at specific places (column at the current x, one step
   block adjacent, one blocking cell on a walk or jump), so the search stays a graph
   search rather than a search over terrain edits.
3. **Planner.** A* from the bot's node to a goal set (stand nodes within melee reach of
   the perceived target). Replan every ~15 frames, when the target moves > 2 cells, or
   when an edge fails; a failed edge is costed up for a while.
4. **Executor.** One controller per edge type, reusing the existing maneuvers' gestures
   (BuildClimb Raise, WallClimb, Breach swings). Watches progress; aborts → replan.
5. **Integration.** A shared, opt-in `RouteManeuver` (owns locomotion) that scores when
   the straight chase can't reach — target out of jump reach above or below, or the
   straight path not walkable. **Decided (user, 2026-11-03): it ships in a NEW routing
   duelist, `Ranger`** (Bulldog's melee + the router, built like Harrier via
   `BulldogBot`'s opt-in `extra` list), so its effect is measurable in isolation against
   plain Bulldog. Existing duelists are not retrofitted.

## Evaluation
- **Scenario suite** (headless, `BotManeuverRoute*Tests`): pillars of 4 / 8 / 16 / 24
  cells; a ledge with an overhang; a target boxed in up high; a target across a pit;
  a target behind a wall with a way round. Plus a **CamperBot** P1 that does what the
  user does — builds up a column when approached, then sits and pokes from the top.
  Metric: success and time-to-contact inside a budget.
- A **vertical league arena** (towers / ledges), then a league re-run.

## Milestones
Run as an autonomous campaign (user, 2026-11-03) on branch `bot-routing`; not pushed.

- [x] **M1 — Calibrate.** (jumps / drops / walk-offs; wall-jump moved to M3 with its executor) Measure the movement envelopes headlessly: jump reach table
  (hold frames × run-up → dx, dy), wall-jump climb rate and limits, mantle / step-up
  heights, safe drop height, column-raise rate. Bake them as constants with a guard test
  that fails if movement tuning drifts.
- [x] **M2 — Walk / jump / drop routing.** Nav graph, A*, executor; static-terrain tests
  (steps, gaps, ledges, down-and-around).
- [~] **M3 — Wall jump and ledge edges.** Calibrated: `WallClimbManeuver`'s controller climbs a single face of ANY tested height (4–24 cells), 1-wide pillars included, in ~80–200 frames (`ZzzRouteWallClimbCalibration`). So the camper's column is climbable today; next is finding why the bots don't (CamperBot baseline, pulled forward from M5), then climb edges in the graph.
- [x] **M4 — Build edges** (dig deferred: Ranger keeps Bulldog's reactive `BreachManeuver` for sealed-in targets; meters aren't binding — a full reservoir is ~300 dirt tiles, the planner caps a column at 30).
- [~] **M5 — CamperBot, scenario suite, integration** as the `Ranger` duelist, vertical arena, league (Ranger vs Bulldog A/B).

## Campaign log
- M1: `Net/Bot/Routing/JumpTable` (+ generated `.g.cs`, 177 entries) from
  `ZzzRouteCalibration`; guard `BotManeuverRouteJumpTableTests` 31/31. Findings: up 4 cells
  max (3–4 takeoff-sensitive); level gap 5; drops to 19 cells for <= 0.3 HP (harness
  limit, not a game one); a standing start at the edge is robust almost everywhere; a
  direction held through the jump overshoots 3–4 columns, so the gesture brakes over the
  destination's first column; many short drops/gaps need no jump (Walk).

- M2: `NavGrid` (stand nodes, table edges with swept-cell, landing-span, walk-off and
  run-up checks; A*), `RouteFollower` (closed-loop gesture replay), `RouteManeuver`.
  `BotManeuverRouteTests` 4/4 (stairs ×3 flights, 4-cell pit, 4-up step, down-and-around),
  0 failed edges. Bugs found on the way: a lip landing read as the floor below (NodeUnder
  now checks the body's width); a periodic replan threw away running take-offs; a stale
  Score-time plan sent the bot back to its spawn. Bot group: 6 reds, all known.

- M4: build edges (virtual column-top nodes, path-local; heights only where there is
  something to step onto or the goal row; a one-cell gap from anything solid all the way
  up, else the body gets shoved off its column at a corner) + `RouteFollower.Build`
  (BuildClimbManeuver's elevator). Priced above an equal climb. `Build_UpToAFloatingPlatform`
  10 / 16 cells pass, 0 failed edges; route suite 9/9.

- M5 (partial): Ranger in `Game1`'s `BotOpponent` switch (`"Ranger"`), README, tests
  (determinism; beats Bulldog vs a 15-cell tower camper). Routing now triggers only on a
  climb or build edge: routing on hops owned the bot mid-fight and cost flat-arena points
  (first cut 74.5 vs Bulldog 95.5 /180; climb-or-build-or-detour-or-reach 85). Vertical
  league arena and a league run not done — stopped here for the user to playtest.

## Decisions needed
