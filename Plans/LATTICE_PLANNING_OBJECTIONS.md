# Lattice movement: design objections and evidence

Date: 2026-09-05. Based on the current working tree, including the recent body-shape changes.

## Purpose and scope

The character should step or jump into a physically passable two-tile-high corridor with little interruption and without requiring a precise starting position or jump timing. Existing jumps, air control, and movement states should be preserved. The aim is to reduce inconsistency inside lattice planning and tracking, without expanding the movement architecture.

This document records objections, their supporting evidence, and the questions a fix must resolve. It does not prescribe a controller rewrite. Full experiment details and reproduction commands are in [the corridor entry report](TWO_HIGH_CORRIDOR_ENTRY_REPORT.md); the probes are in [TwoHighCorridorEntryTests.cs](../MTile.Tests/Sim/TwoHighCorridorEntryTests.cs).

## Current mechanism

The live implementation is [LatticePathPlanner](../Character/Corrector/LatticePathPlanner.cs) followed by [LatticeTracker](../Character/Corrector/LatticeTracker.cs). The older `LatticePlanner` is a debug state-space prototype.

The spatial planner searches body-center positions in a forward-directed grid. It scores progress, rise, and hover preference. It returns an untimed polyline and a bonk flag.

The tracker solves five simulation ticks, approximately 83 ms at 60 Hz, and applies the first tick's forces. Its constraints include a narrow band around the polyline, tile-clearance rows, and speed/progress requirements. Tile rows come from a free rollout: current velocity integrated under gravity and already-applied forces, with zero corrector contribution. The three outer passes update projections onto the path, but keep those tile rows fixed. Force availability is evaluated at the current body state and held across the horizon.

At 100 px/s, five ticks cover approximately 8.3 px before acceleration. The spatial planner's configured lookahead is 56 px. These are different horizons.

## Confirmed objections

### 1. A physically passable corridor is excluded by the planner's clearance model

The current body is 19.2 px tall. Two 11 px tiles provide 22 px of opening, leaving 2.8 px of total vertical slack.

The default lattice inflates obstacles by half a cell on each side. At three cells per tile, the required additional vertical clearance is 3.667 px. The inflated floor and ceiling leave no free center position. A body seeded physically inside the opening gets no lattice path, with hover either enabled or disabled.

This is a representability failure before force optimization begins. More solver iterations or more accurate obstacle selection cannot recover a path that the planner excludes.

**Requirement for a fix:** passages the character is expected to use must be representable. Grid resolution, clearance allowance, and allowable tracking error need a consistent relationship. An exact path-derived corridor is one possible approach, but the tests do not establish that it is necessary.

### 2. The QP's clearance allowance also exceeds the available space

The tile-row builder uses a 2 px margin. Requiring that margin on both floor and ceiling needs 23.2 px of opening for this body, exceeding the actual 22 px.

These rows are finite penalties rather than strict collision guarantees. The body may still enter through residual violation and physics collision handling. Consequently, physical passage does not demonstrate that the planner and QP have a consistent feasible solution.

Reducing the QP margin alone did not improve the eventual-entry counts in the matched experiments.

**Requirement for a fix:** separate actual nonpenetration from preferred extra clearance. Preferred clearance must yield coherently when the physical opening permits less room.

### 3. Standing hover preference can reject travel through a valid opening

With six, seven, or eight cells per tile, the geometry probe can find a route through this corridor with hover disabled. Turning standing hover on reduces the result to one node, despite the available geometric route.

Standing requests a 14.8 px bottom gap where only 2.8 px of total vertical slack exists. A preference that is unattainable in the passage accumulates enough cost to make progress unattractive.

The hover term is also charged per destination node, whereas rise is charged per pixel. Changing resolution or the primitive edge table can therefore change the relative price of hover and climbing, not just approximation quality.

**Requirement for a fix:** distinguish preferred posture from passage feasibility. Hover preference should account for available headroom, and its cost should have a deliberate measure independent of incidental node count.

### 4. Successful traversal often occurs without a usable lattice route

In the baseline one-tile step sweep, all 12 runs eventually enter. Nevertheless, many near-mouth tracker frames have fewer than two path nodes. The character continues through fallback behavior and switches to CrouchedState.

The tracker discards the planner's bonk flag. A path's final segment is extended indefinitely, whether the path ended at the lookahead boundary or because the planner refused further progress. With no usable path, other fallback rules apply.

This weakens the meaning of both success and refusal: a successful traversal may not validate the route planner, and a planner refusal does not directly determine the applied behavior.

**Requirement for a fix:** distinguish a valid route ending at the horizon, deliberate refusal, and failure to produce a usable route. Define their behavior explicitly, preserving the intended distinction between assisted entry and an honest wall impact.

### 5. Eventual entry is too weak an acceptance criterion

| Entry | Eventual successes | Relevant limitation |
|---|---:|---|
| Step up one tile | 12/12 | Five runs absorb collision impulses greater than 1 px/s; all transition to crouch. |
| Jump up one tile | 24/24 | All absorb a 150 px/s peak collision impulse; entry takes 105–135 frames. |
| Jump up two tiles | 2/24 | Both successes involve slow recovery after impact. |

A particularly useful regression pair starts at 100 px/s, 24 px before the one-tile step. Offset 0 enters without collision impulse. Shifting the starting position by 1.8 px produces a 132.3 px/s impulse and drops horizontal speed to 2.61 px/s. The tracker has one path node and a 5.98 px tile-row residual at the impact frame.

The tests count sustained physical entry within 180 frames. That definition deliberately captures recovery, and must not be mistaken for seamlessness. A held-crouch control enters the level opening without collision impulse, confirming physical usability.

**Requirement for a fix:** measure speed loss, collision impulse, interruption duration, and sensitivity to small approach changes alongside eventual entry. Numerical acceptance thresholds remain to be chosen. Current successful entries travel near the 50 px/s crouch cap, so preserving full running speed is a separate behavioral decision.

## Structural concerns whose contribution is not yet established

### 6. Obstacle selection follows uncorrected motion rather than the selected route

[ClearanceConstraintBuilder](../Character/Corrector/ClearanceConstraints.cs) selects rows from penetrations of its input positions and chooses an exposed escape facet. In the tracker, these positions are the free rollout. A correction can encounter another tile, or the nearest escape from free motion can differ from the route selected by the lattice.

There is evidence that predictions omit some relevant tiles: 244 of 13,628 measured near-mouth tracker frames contain a corrected sample overlapping a tile at a tick where the free sample did not, with no row for that tile. However:

- These are predicted overlaps, not measured collisions.
- The check uses discrete samples and is not a continuous collision audit.
- A row for the tile at any tick counts as representing it.
- A row opposing progress can be legitimate braking, so the separate opposition counter does not establish a wrong constraint.
- The five-tick horizon and per-frame replanning limit the exposure.

The phase-sensitive step impact already has a known, substantially unsatisfied obstacle row. Missing obstacles are not needed to explain that case. This concern should not currently be treated as the primary demonstrated cause of poor corridor entry.

**Important distinction:** free motion is a valid algebraic baseline for additive forces even when it is a poor behavioral prediction. Gravity compensation is representable as a correction. Removing the baseline calculation alone would not improve the solution.

A direct swap from free samples to lattice nodes would be incorrect: lattice nodes have no timing, a clear path emits few penetration rows, and row depths must refer to the positions used by the dynamics. Path-informed boundaries would require a different construction, including per-tick correspondence and correctly expressed depths.

### 7. The tracker assumes current support and force availability persist

The tracker holds current position, velocity, floor, and grounded status when building channel masks and velocity-dependent caps across five future ticks. A ledge departure can remove support within that horizon; a landing can introduce it. Tick-zero decisions may depend on future forces that will not actually be available.

This is a model limitation, but the corridor experiments did not isolate its causal contribution. Increasing the horizon would extend the assumption rather than fix it.

### 8. The executed connection into the lattice is not validated like a lattice edge

The planner can snap a blocked seed to a nearby free node without checking the connecting segment. The tracker may then skip nearby nodes and connect the exact body position to a later node. This connection does not go through the lattice's edge-clearance check.

It is a plausible issue at tight corners and lips, but no isolated reproduction was established in this testing. It should be tested directly before attributing a particular impact to it.

### 9. The path is geometric guidance, not a dynamic feasibility guarantee

The spatial graph does not carry velocity or future support state. Default seed velocity bias and seed run are disabled. Its forward-only edges cannot represent retreat or every lateral recovery.

Those restrictions buy a small deterministic search and may be appropriate for local assistance. They become problematic if a returned polyline is treated as proof of trackability. These tests do not justify replacing it with a full state-space search or rebuilding working jumps and air control.

## Recommended order of work

1. Preserve the current tests as a baseline, especially the 1.8 px step-entry pair and the two-tile jump timing pair.
2. Make the intended passage geometrically representable and reconcile clearance allowances.
3. Make hover preference yield to available headroom; repeat the same approach sweep.
4. Make usable-route versus refusal behavior explicit, so successful fallback does not masquerade as successful planning.
5. Investigate remaining impacts using corrected-trajectory diagnostics. Change obstacle selection or support prediction only where evidence points to them.

Do not simultaneously rewrite the optimizer, split ownership of ordinary movement, replace force channels, or retune jumps. Those changes would expand scope and obscure which correction improved corridor entry.

The strongest current objection is that physical collision, lattice clearance, QP clearance, and hover preference disagree about whether the desired passage is usable. Resolving that disagreement is a narrower next step than redesigning the controller.
