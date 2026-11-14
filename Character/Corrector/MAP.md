# Character/Corrector map
Verified against: 4e34142 (2026-11-14)
Covers: Character/Corrector/*.cs, Character/Corrector/corrector_pseudocode_reference.txt

## What this area is
Solver-driven actuation for movement states. A state ends its Update with `ApplyAmbient(profile)`; a fold profile routes to the lattice engine (spatial DP path -> short-horizon channel QP -> tick-0 force added to `Body.AppliedForce`). Guided moves (ledge pull, dropdown, climb family) follow a retargeted authored clip through the same path tracker with grip/slide channels. Everything here is sim-side, except the capture buffers, freshness markers and the profiling/capture statics, which are write-only diagnostics.
Boundary: state selection, `FoldProfile` choice, `MovementVars` and probes (`EnvironmentContext.Floor/HandCorner/Walls`) live in `Character/Movement/` and `Character/Sensing/`; the physics step that consumes `AppliedForce` lives in `Physics/`.

## Files
- `AmbientCorrector.cs` (176): `FoldProfile` (Fall/Jump/Stand/Stairs/Crouch presets, :40-93); `AmbientCorrector.Apply` (the ambient entry point); `FloorEnvelope` (C-space floor height at x; not called by `Apply` — its callers are `EnvironmentContext.Floor` (`EnvironmentContext.cs:228`) and the `LatticePlanner` prototype).
- `LatticePathPlanner.cs` (654): the live fold engine's spatial DP. Instance-owned pooled scratch, one per `CorrectorScratch`.
  - :48-146 consts (`MaxCells` 4096, `MaxPath` 256, `Clearance` 0.25, offset table radius 3) + `BuildOffsets`/`Supercover`
  - :148-197 pooled scratch, stamp-mask cache fields, debug accessors (`LastReach/LastBonk/LastCost`)
  - :207-441 `Solve`: cone filter -> `BuildWindow` -> `StampObstacles` -> `SweepFloorBelow` -> seed snap -> seed run -> `Flood` -> sort by p -> DP sweep (:300-379) -> argmax goal + bonk (:381-422) -> path recovery (:424-440)
  - :446 `SnapSeed`, :469 `BuildWindow`, :500 `StampObstacles`, :544 `EnsureMask`, :575 `SweepFloorBelow`, :593 `Flood`, :622 `EdgeFree`, :636 `ForcedOffset`
- `LatticeTracker.cs` (332): `Apply` = plan + build rows + `PathTracker.Solve` + apply; `LatticePolicy` (zero-input braking rows, progress authority, `BuildFold` + Redirect retune).
- `PathTracker.cs` (419): shared path-following core. `IPathTrackPolicy`, `PathTrackSettings`, `FreeRollout`, `HoldSamples`, polyline builders, `Solve` (bead passes, :120-280), `FollowPolyline` (guided moves, :287), `ApplyTick0` (:326), `RouteSatisfies`, `ProjectArc`/`PointAt`, `BuildClipPolyline` (:388).
- `CorrectionSolver.cs` (647): `LeverKind`, `ChannelDef`, `CorrectionProblem`, solver.
  - :16-78 `LeverKind` + `ChannelDef` fields; :97-147 `CorrectionProblem` (+ bench-only `Clone`)
  - :149-172 consts (`MaxChannels` 10) and the write-only statics `Profile/Ticks/Calls/Sum*`, `ArmCapture/Captured`
  - :187 `Solve` -> :196 `SolveCore` (precompute :207-315, projected-gradient sweeps :318-407)
  - :428 `ExactSweeps` (exact per-axis-channel coordinate minimisation), :520 `RowSlack`, :540 `ComputeResidual`, :559 `Project` (Thales disc / axis / cap), :626 `RiseHeadroom`, :638 `ClampRise`
- `CorrectorChannels.cs` (611): channel stacks, built into `s.Problem.Channels`.
  - :39-45 consts (`LegReach`, `CatchFadeBand`); :63-172 `BuildFold` (lattice tracker); :180 `AddJump`; :194 `DriveSideCap`; :208 `AddContactPush`; :235 `MarkContactPush`; :270 `AddCornerPlant`
  - :292-377 `BuildManeuver` (trigger probe only); :382-445 `CornerPlantReach` + `MarkCornerPlants`
  - :471-522 `BuildGrip` (+ `GripReach`, `InGripReach`, `GripHoldAxis`); :536-564 `BuildSlide`; :567 `IsNearFloor`; :585 `PaceFade`; :597 `AnchorLeak`; :601 `ComputeTickDv`
- `ClearanceConstraints.cs` (287): `ClearanceRow` (Tick/Normal/Depth/HingeScale/PlantOnly/Reference/Cell) and `ClearanceConstraintBuilder.Build`, which sweeps the C-obstacle along predicted samples and emits rows (`MaxEvents` 48).
- `CObstacle.cs`: `CFacet`, `CObstacleTemplate` (tile ⊕ reflected body facets, `TopSurfaceRy`, static 4-slot memo `For`).
- `CorrectorScratch.cs`: `LatticeOutcome` enum and the per-player pooled buffers (`Samples`, `Rows`, `Z`, `TickDv`, masks/caps, `Lattice`, `LatticePath`, `BeadVerts`, capture buffers, `Ledger`, `ProbeBody`), plus `BeginFrame`.
- `CorrectorLedger.cs`: `CorrectionChannel` actuator ids; `CorrectorLedger.Record`, which logs per-channel and per-contact tick-0 force.
- `GripPathPolicy.cs`, `SlidePathPolicy.cs`: `IPathTrackPolicy` implementations that wrap `BuildGrip` / `BuildSlide`.
- `ReferencePath.cs`: `ReferenceFrame` (two-anchor affine retarget) and `ReferenceClipRegistry` (baked clips ledge_pull/dropdown/parkour/mantle/arcjump, plus `LoadOverrides` from `ReferenceClips/`).
- `HermiteClip.cs`: `HermiteClipDocument`/`HermiteClipKey` (cubic Hermite arc with JSON load/save, `Eval`, `RescaleClipSpace`).
- `BallisticPredictor.cs` (321): `CoastSample`; `SupportReach`, `HoldFullDist`, `MaxHorizon` 48, `BallisticVy`; `Predict` (free coast) and `PredictGuided` (maneuver coast); private `TryProbeFloor`.
- `ManeuverCorrector.cs`: `Run` (two passes of PredictGuided -> rows -> `BuildManeuver` -> solve) and `Apply`.
- `BaselineFeedforward.cs`: `Air` (mirror of AirControl.Apply) and `Ground` (spring + walk, used by `PredictGuided`).
- `CorridorProbe.cs`: `Corridor` (per-column floor/ceil, corners, truncation reason) and `CorridorProbe.ScanInto`.
- `LatticePlanner.cs`: state-space beam-search prototype. Callers: `Game1.cs:670` (freeze-frame) and `MTile.Tests/Sim/ZzzLatticeTiming.cs`.
- `GuidedPath.cs`: piecewise Hermite path. Nothing in the repo references it (dead).
- `corrector_pseudocode_reference.txt`: describes the removed soft-row ambient solve. Stale; does not match `LatticeTracker`.

## Flow
Ambient (fold states), once per player per frame:
1. `PlayerCharacter.cs:789` calls `_correctorScratch.BeginFrame()` (clears capture counts, `LatticeOutcome`, `Ledger`). `PlayerCharacter.cs:964` runs `_currentState.Update`.
2. Each state's Update ends with `ApplyAmbient` (`Movement.cs:115-118`), which calls `AmbientCorrector.Apply` (`AmbientCorrector.cs:165`). It returns early, zeroing anchors, if there is no scratch, `AmbientCorrectorEnabled` is false, or `!fold.Fold` (:168). Profiles: Standing `Stand` (`LocomotionStates.cs:90`), Falling, WallSliding and the Dash tail `Fall` (Dash burst `None`, `DashState.cs:121`), Crouched `Crouch`, Jumping/RunningJump `Jump` while `JumpHoldFrames > 0` then `Fall` with uses `Air|Hands` (`JumpStates.cs:116-119`), DoubleJump and WallJump `Jump`, StairClimb `Stairs`, every other state `None` (incl. CoveredJump, LedgeJump, the guided moves).
3. `LatticeTracker.Apply(ctx, s, fold, uses, ctx.Intent.CurrentHorizontal, ref vars)` (:174 -> `LatticeTracker.cs:42`). `H = min(PathTracker.Horizon=5, 48)` (:49).
4. Probes: `ctx.Floor`, `ctx.LegsInReach`, `ctx.Support` (:53-61). Intent `u` is `(dir,0)`, or tilted up by `riseCeiling` when `fold.Rising` (:76-79).
5. Plan, recomputed every frame when `dir != 0 || fold.Rising`: `s.Lattice.Solve(...)` writes `s.LatticePath` (:85-89). The tracker then sets the freshness markers (:92-93) and `LatticeOutcome` (None/NoRoute/Refused/Route, :112-115).
6. If there is no path, no hover column and no plan, it zeroes anchors and returns, applying no force (:123-127).
7. `PathTracker.FreeRollout` (nominal: baseline `AppliedForce` + gravity) and `HoldSamples` (current state frozen across H) (:130-131). Then hand-corner and contact-push flags (:136-140), the polyline from the plan nodes (the body is not a vertex) with `arcStart` and `t0` (:156-172), `arcCap` and `driveCap` (:184-188).
8. Wall rows: `ClearanceConstraintBuilder.Build` on the free rollout with margin `LatticePathPlanner.Clearance`, capped to leave room for 5H+1 reference rows (:201-205).
9. `PathTracker.Solve` (:228 -> `PathTracker.cs:120`), 3 bead passes. Each pass: project the ticks onto the polyline (monotone bead), keep the wall rows the route itself satisfies (:174-179), add ±band rows and arc-speed rows, then `policy.AddRows` (zero-input braking), then one soft progress row along the chord (:232-250), then `BuildChannels` (`BuildFold` :313), then `CorrectionSolver.Solve`, `ExactSweeps` (3), `ComputeTickDv`, and `TrackDelta` for the next pass. With no polyline it stops after one pass (`PathTracker.cs:270`).
10. Apply: `PathTracker.ApplyTick0` does `body.AppliedForce += TickDv[0]/dt` and `Ledger.Record` (`PathTracker.cs:326-330`). Capture buffers are filled only if `CaptureTrajectories` (`LatticeTracker.cs:236-243`).

Guided moves: `ClimbStates.cs:339-343`, `LedgeStates.cs:399-403` and `LocomotionStates.cs:296-302` call `PathTracker.BuildClipPolyline(s, body.Position, clip, new ReferenceFrame(clip, RefEntry, RefGate), out arcLength)` and then `FollowPolyline(ctx, s, nv, ref GripPathPolicy|SlidePathPolicy)` (Climb and LedgePull grip, Dropdown slide). This is the same `Solve`, with wall rows at margin `LatticePathPlanner.Clearance` (reserve 3H+1, `PathTracker.cs:299-302`), no arc-speed cap and no hover line. Unlike the lattice polyline, a clip polyline starts AT the body (`PathTracker.cs:400`). All three Updates zero `AppliedForce` first (`ClimbStates.cs:334`, `LedgeStates.cs:391`, `LocomotionStates.cs:288`) and end with `ApplyAmbient(None)`.

Climb trigger probe (the only live use of BallisticPredictor's coasts and ManeuverCorrector):
1. `ClimbManeuverBase.CheckPreConditions` positions `s.ProbeBody` with the post-hop velocity and calls `ManeuverCorrector.Run(..., FeasibilityIterations=128)` (`ClimbStates.cs:116-125`).
2. `Run` (`ManeuverCorrector.cs:57`) makes two passes, each `PredictGuided` (H = `CorrectorHorizon`), `MarkCornerPlants`, `Build` (margin `CorrectorMargin`), `BuildManeuver`, then `Solve`. On pass 0 it returns 0 if there are no rows.
3. `Delivers` (`ClimbStates.cs:153-172`) re-rolls `PredictGuided` with `s.TickDv`, then rebuilds rows. It returns true only if a sample before truncation passes the lip at gate height. If the corrected look fails and the solve had rows, a second, uncorrected `Delivers(null)` look decides (`ClimbStates.cs:126-146`). Other BallisticPredictor uses: `ClimbStates.cs:236` (reference capture in `Enter`, gated on `CaptureTrajectories`; writes only `ReferenceTrajectory/Count`, read only by `Game1.cs:1467`), `Game1.cs:1446` (`Predict`, debug overlay), `BallisticVy` for the hop sizing (`ClimbStates.cs:252-287`), and the constants `SupportReach`/`HoldFullDist` (`EnvironmentContext.Support`, `EnvironmentContext.cs:258-268`; `LatticePathPlanner.cs:433`; the `LatticePlanner` prototype). `BaselineFeedforward` is called only from inside `BallisticPredictor`.

## Invariants and traps
- One engine. Fold profiles always reach `LatticeTracker` (`AmbientCorrector.cs:174`). There is no engine switch in `MovementConfig`. `CorrectorVaultEnabled` (named in CLAUDE.md) does not exist; the climb gate is `CorrectorClimbEnabled` (`ClimbStates.cs:68`).
- Config knobs read here (defaults in `MovementConfig.cs`; every one that `configs/movement_config.json` sets carries the same value as its default, and the `Lattice*`, gate, cost and maneuver-probe knobs are absent from the JSON). `FoldCornerPlantForce` defaults to 0, so `AddCornerPlant` omits the CornerPlant channel (`CorrectorChannels.cs:272-273`); in free air the hand corner acts only through the Redirect mask.
  - gates: `AmbientCorrectorEnabled`, `FoldRedirectEnabled` (mask on Redirect, `CorrectorChannels.cs:124`), `CorrectorScenario` ("full" / "redirect-traction" / "legs-only", `:82-117`)
  - planner: `Lattice{LookaheadPx,CellsPerTile,ConeCos,ProgressWeight,HoverWeight,SeedWeight,SeedRunPx,SeedRunMinSpeed}`
  - channel caps: `Fold{Leg,Drive,Tuck,Redirect,ContactPush,CornerPlant}Force`, `FoldLegPushFadeSpeed`, `SlipDriveForce`, `JumpPushForce`, `JumpTopSpeed`, `MaxAssistRiseSpeed`, `MaxGroundEngageVnRel`
  - solver: `FoldIterations`
  - grip / guided moves: `LipLiftForce`, `LipPushForce`
  - profiles: `FoldHoverOffset`, `CrouchHoverOffset`, `Fold/CrouchRiseCost`, `FoldRecoverCost`, `MaxWalkSpeed`, `CrouchMaxWalkSpeed`
  - maneuver probe only: `CorrectorHorizon`, `CorrectorMargin`, `CorrectorDeltaWeight`, `FoldAirLateral/VerticalForce`, `FoldCornerForce`
  - corridor: `Corridor*`
- Sign convention: Y-down, so up is −y everywhere (LegServo axis `(0,-1)`, `RiseHeadroom` `up = -Axis.Y`, `CorrectionSolver.cs:629`; rise = `-o.Dy` in the DP, `LatticePathPlanner.cs:347`).
- Units: `Force` z is an acceleration and `VelocityUpdate` z is a δv. `ComputeTickDv` multiplies only Force by dt (`CorrectorChannels.cs:606-608`); `Ledger.Record` divides only VU by dt (`CorrectorLedger.cs:97`). The fold's Redirect is rebuilt as `LeverKind.Force` by `LatticePolicy` after `BuildFold` (`LatticeTracker.cs:327-328`); `Project` converts it (`CorrectionSolver.cs:570-599`).
- `LatticePolicy.BuildChannels` hard-codes slot 2 = Redirect and copies `ch[0].Weight` (`LatticeTracker.cs:319-328`). `BuildFold`'s slot order (0 LegServo, 1 Drive+x, 2 Redirect, 3 Tuck, 4 Drive−x, then CornerPlant/ContactPush/Jump) is load-bearing; the scenario stacks use a different layout.
- The jump channel replaces LegServo: LegServo is masked while `jumpTicks > 0` (`CorrectorChannels.cs:122`).
- `CorrectionSolver` uses bitmasks: rows ≤ 64 (`MaxEvents` 48) and H ≤ 64 (`MaxHorizon` 48) (`:227-247`). Raising either past 64 breaks the `ulong` masks silently.
- Rise ceilings must also be in `ExactSweeps`' box (`RiseHeadroom`, `CorrectionSolver.cs:455`). `ExactSweeps` replaces `Project`'s output for axis channels and is skipped when `DeltaWeight > 0` (`:430`).
- `PathTracker.Solve` zeroes `PrevApplied` and `DeltaWeight` (`PathTracker.cs:259-260`). So `vars.AmbientChannelPrev`/`AmbientPrevDv` are written (`LatticeTracker.cs:125,232-233`; `AmbientCorrector.cs:170-171`) but read nowhere. `ManeuverChannelPrev` is only reset (`ClimbStates.cs:206`). `ManeuverCorrector.Apply` has no callers.
- Wall rows are filtered by the route (`RouteSatisfies`, `PathTracker.cs:340`): a wall the plan runs into (a refused plan's ray, a jump column into a ceiling) is hit, not braked for. Band rows are dropped for ticks more than one band behind vertex 0 (`:202`).
- Two clearance numbers. The lattice and guided paths use `LatticePathPlanner.Clearance` = 0.25 px (`:75`); `LatticePathPlannerTests` `TwoHighCorridor_*`/`OneWideHole_*` pin it below 0.46. The maneuver probe uses `CorrectorMargin`.
- `LatticePathPlanner.SweepFloorBelow` adds the stamp margin back (`:584`) so the DP hover point matches the tracker's floor.
- `CoastSample.Grounded` from the DP uses `BallisticPredictor.SupportReach` (`LatticePathPlanner.cs:433`). The same constant also drives `EnvironmentContext.Support`.
- The tracker's nominal (`PathTracker.FreeRollout`) is the state's `AppliedForce` + full `ctx.Gravity` only. After `Update` returns, `PlayerCharacter.cs:972-992` adds `ApplyActionForces`, the gravity-scale/`FallGravityScale` counter-force and `FallDrag` to `AppliedForce`, so the solve never sees those (unlike `BallisticPredictor`, which models the fall scale).
- `DriveSideCap` divides by `Simulation.FixedDt`, not `ctx.Dt` (`CorrectorChannels.cs:194-195`).
- Statics:
  - `CObstacleTemplate` memo (`CObstacle.cs:60-75`): a pure function of the polygon, keyed by reference, 4 slots.
  - `ReferenceClipRegistry._clips`: sim-affecting; written only by `LoadOverrides` (`ReferencePath.cs:109-118`; contract at :74-77), called at startup and on hot-reload (`Game1.cs:314,343,355`).
  - `CorrectionSolver.Profile/Ticks/Calls/Sum*`: read only by `MTile.Bench/CorrectorDiag.cs`; `ArmCapture/Captured` and `LatticePlanner.LastDebug`: never read by the sim.
- Nothing in `CorrectorScratch` is snapshot state. Per-frame recompute: the plan, rows, channels and solve (cold start at z = 0, `CorrectionSolver.cs:201`). No corrector-owned state crosses frames: besides body state, the only cross-frame inputs are the caller's snapshot-covered `MovementVars` (the tracker reads `JumpHoldFrames`/`JumpSourceVy`; guided moves pass `RefEntry`/`RefGate`/the corner) and the config-like clip registry. Pure caches (same result on hit or rebuild, so rollback-safe): `LatticePathPlanner`'s stamp mask, rebuilt when template/cells-per-tile/margin change (`:546`), and the template memo. Render-only leftovers that outlive the frame: `LatticePath` (+Count/Frame) and `ReferenceTrajectory`.
- Render-only consumers: `Drawing/LatticePathSampler.cs:38-47` (animation step planner) and `MTile.Demo/SpriteStripGame.cs:519-522` read `LatticePath`/`LatticePathCount`/`LatticePathFrame`; `Game1.cs:1465-1471` draws the capture buffers, which are filled only under `CaptureTrajectories`. `LatticeOutcome` and `ForceLedger` are read only by tests.
- Comment drift: the `ClimbStates.cs:14-18` header says Update runs `ManeuverCorrector`; the code at `:334-343` runs `PathTracker.FollowPolyline` with `GripPathPolicy`. `Movement.cs:23` still lists `ManeuverCorrector.Apply` as a live mode. `CorrectorScratch.cs:12-15` calls the dead `MovementVars` anchors the cross-frame corrector state. `PathTracker.cs:101` says "vertex 0 is the body", which is false for the lattice polyline.

## Where to change X
- What a fold state will or won't climb, its hover height or speed: the `FoldProfile` presets (`AmbientCorrector.cs:40-93`) and `Fold/CrouchRiseCost`, `FoldRecoverCost`.
- Planner cost terms or goal and bonk rule: `LatticePathPlanner.Solve` DP sweep (`:300-379`) and goal (`:381-422`).
- Lattice resolution or lookahead: `LatticeCellsPerTile` / `LatticeLookaheadPx`; check `Clearance` arithmetic (`:52-75`).
- Channel caps, masks or a new actuator in free locomotion: `CorrectorChannels.BuildFold`, then the slot assumptions in `LatticePolicy.BuildChannels`.
- Zero-input braking or the progress drive: `LatticePolicy.AddRows` / `ProgressAuthority` (`LatticeTracker.cs:261-303`).
- Band width, bead passes, wall-row filtering: `PathTracker.Solve`, `FollowBand`, `RouteSatisfies`.
- Ledge pull, dropdown or climb arc shape: `ReferenceClipRegistry` bakes or `ReferenceClips/*.json`, and the grip/slide channels in `BuildGrip`/`BuildSlide`.
- Climb trigger feasibility: `ClimbStates.cs` `CheckPreConditions`/`Delivers`, then `ManeuverCorrector.Run` and `BuildManeuver`.
- Solver convergence or a new admissible-set type: `CorrectionSolver.SolveCore`, `ExactSweeps`, `Project`.
- Terrain rows (facets, exposure, truncation): `ClearanceConstraintBuilder.Build` and `CObstacleTemplate.Build`.

## Tests
Group `corrector` (`scripts/test-group.py corrector`) runs:
- unit tests: `LatticePathPlannerTests`, `CorrectionSolverTests`, `ClearanceConstraintTests`, `BallisticPredictorTests`, `ReferencePathTests`, `CorrectorLedgerTests`, `CorrectorAnchorTests`, `CorrectorHoleJumpTests`, `CorrectorExperimentsTests`, `CorrectorOperationsTests`, `CorrectorSnapshotTests`, `CorrectorTrajectoryCaptureTests`
- lattice and fold scenarios: `FoldLatticeEngineTests`, `FoldScenarioTests`, `LatticeScenarioTests`, `LatticeCorridorEntryTests`, `SolverRestEquilibriumTests`
- terrain scenarios: `CaveMouthTests`, `BumpyTunnelSpeedTests`, `SpeedInvariantTests`, `VaultJumpAndLipReproTests`, `DiveDrillTests`, `StalactiteFallTests`

Only in group `movement` (name matches "Corridor"/"Mantle", not a corrector term): `CorridorProbeTests`, `TwoHighCorridorEntryTests`, `MantleVaultArcSweepTests`. `ZzzLatticeTiming` (LatticePlanner) runs only with `full --slow`. All of these are under `MTile.Tests/Sim/`.
