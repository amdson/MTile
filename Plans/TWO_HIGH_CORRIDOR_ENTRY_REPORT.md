# Two-high corridor entry experiments — 2026-09-05

No production movement changes. Probe: `MTile.Tests/Sim/TwoHighCorridorEntryTests.cs`.

## Setup and definitions

- Current working-tree body and default `MovementConfig`, lattice engine, 60 Hz.
- Tile: 11 px. Physical polygon: 19.2 px tall. Opening: exactly two tiles / 22 px.
- Solid ceiling mass above the opening; solid floor below. No route over the roof.
- Mouth x = 264. Approach floor y = 132; corridor floors at 132, 121, or 110 for level, one-tile, or two-tile rise.
- Settle for 60 frames, then set approach position and horizontal velocity. This isolates approach phase/velocity; it is not a full run-up from rest.
- Step sweep: 50/100/150 px/s initial velocity; 24 px approach distance plus 0/0.9/1.8/2.7 px offsets. Hold Right, no Up/Down/Space.
- Jump sweep: 100 px/s initial velocity; press Space at 12/24/36/48/60/72 px plus 0/1.8 px from the mouth; hold 6 or 12 frames, continue Right.
- Entry success requires the whole body at least two tiles beyond the mouth and vertically inside the opening for six consecutive frames, within a 180-frame observation window. This measures eventual entry, not seamlessness.
- Log entry time, minimum horizontal speed near the mouth, frames below 10 px/s, collision impulse, state transitions, usable-path count, and solver diagnostics.
- Jump inputs can activate RunningJump or WallJump and their own movement logic. Whole-run results measure the integrated character, not an isolated lattice controller. Solver diagnostics are restricted to captured five-tick tracker frames.

## Baseline: 96 entries

| Action | Rise | Eventual entries | Observations |
|---|---:|---:|---|
| Walk | Level | 0/12 | Stops outside opening in Standing. |
| Step up | 1 tile | 12/12 | All auto-crouch; five runs take collision impulses >1 px/s. |
| Jump | Level | 0/24 | Hits ceiling mass / mouth and stalls. |
| Jump | 1 tile | 24/24 | All take 150 px/s peak collision impulse; eventual entry after 105–135 frames. |
| Jump | 2 tiles | 2/24 | Both eventual rescues are slow; all 24 runs take 100–150 px/s peak impulses. |

The one-tile step is phase-sensitive despite its 100% eventual-entry rate. At initial vx=100 and distance=24, offset 0 takes no collision impulse. Offset 1.8 px takes a 132.3 px/s impulse at frame 14 and drops to vx=2.61 px/s. The tracker has only one path node at that frame and a 5.98 px tile-row residual. After entry the character crawls near 49.6 px/s.

The two successful two-tile jump cases both press 24 px from the mouth:

- 6-frame hold, offset 0: reaches the entry criterion at frame 170.
- 12-frame hold, offset 1.8: reaches it at frame 103. Offset 0 with the same hold fails.

These are not seamless jumps into the corridor. They involve impacts, lost momentum, and recovery.

## Three distinct constraints on fitting

1. **Physical fit:** 22 − 19.2 = 2.8 px of total vertical slack.
2. **Default lattice clearance:** half a cell on each side totals 11/3 = 3.667 px. No center position can satisfy both the inflated ceiling and floor. A seed already placed physically inside the corridor returns no path, with hover either on or off.
3. **QP clearance:** 2 px on each side requires 23.2 px of opening. Floor/ceiling requirements cannot both be met when both apply. Rows are penalties, so passage remains physically possible through violation and fallback behavior.

The grid also matters beyond total margin: with 4 or 5 cells per tile, this fixture still has no representable free row. At 6/7/8 cells per tile a route exists with hover disabled (about 59 px forward progress). With standing hover enabled, the same seeds return just one node: the hover objective refuses the passage even after geometry allows it. The requested 14.8 px bottom gap cannot fit within 2.8 px total clearance.

## Controlled experiments

48 matched runs: level / one-tile / two-tile rise, walking / jumping, offsets 0 and 1.8, initial speed 100, approach distance 24, jump hold 12. Compare defaults against QP margin 0.5, lattice density 8, and both. Each setting is restored after the experiment.

- Reducing QP margin alone does not change the eventual-entry counts.
- Finer grid allows a usable path through more of the successful one-tile walking entries, but does not fix the level opening or reliably fix jumping.
- Combining finer grid and smaller margin reduces the worst impulse in the two one-tile walking cases from 132.3 to 87.2 px/s. It still does not produce seamless entry.
- These changes are diagnostic interventions, not proposed production tuning.
- A separate held-Down control enters the level opening successfully with **zero collision impulse**, at the existing crouch speed. The physical corridor is usable.

## What this says about free-rollout obstacle rows

Across 13,628 near-mouth lattice-tracker frames, 244 (1.8%) contain a corrected sample overlapping a physical tile at a tick where the free sample did not, with no row for that tile. This is a predicted overlap, not an actual collision; overlaps use the exact C-obstacle facets without the artificial clearance margin. Discrete samples can miss between-tick intersections, and a row at another tick counts as representing the tile, so this is a screening metric rather than a complete safety audit.

166 frames have a nonzero tile-row push whose normal opposes the first lattice edge. That alone is not a bug: legitimate braking also opposes progress, and the first edge is only a local approximation to later path direction.

The phase-sensitive one-tile-step impact occurs with a known, substantially unsatisfied obstacle row and no usable route. Missing obstacles are therefore not needed to explain that failure. The strongest direct findings are the incompatible margins, absent lattice route, and the standing-hover objective refusing a tight but physically passable space.

## Suggested next experiment

Keep existing jumps and air control. First make this tight passage representable by the planner, and make hover preference yield to the available ceiling clearance. Re-run the same phase sweep before altering obstacle selection or solver formulation. Define the desired crouch/entry speed explicitly: today's successful cases switch to a 50 px/s crouch, so “seamless at running speed” would require an additional behavioral decision.

## Reproduction and artifacts

```sh
dotnet test MTile.Tests/MTile.Tests.csproj --no-restore \
  --filter FullyQualifiedName~TwoHighCorridorEntryTests \
  --logger 'console;verbosity=detailed' -v quiet -p:WarningLevel=0
```

Three diagnostic tests completed successfully; their passing status means the experiments ran, not that movement meets the desired entry contract. They produced 96 baseline runs, 48 matched experiment runs, and one crouch control. CSV traces, representative geometry/prediction JSON, and the config snapshot are written under `Path.GetTempPath()/mtile-corridor-entry`.

This session also generated `/tmp/mtile-corridor-review/playback.html`, a standalone interactive playback of six representative cases, plus copies of the summary CSVs and configuration. Predictions shown are the tracker data from the same frame, not a re-simulation. No browser visual validation was performed.
