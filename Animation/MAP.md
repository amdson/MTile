# Animation map
Verified against: 4e34142 (2026-11-14)
Covers: Animation/*.cs, Skeletons/*.json, SkeletonStates/<rig>/*.json, configs/anim_solver_config.json

## What this area is
Render-only skeletal animation. Each frame, a `CharacterAnimator` reads a `CharacterAnimSample` (a read-only snapshot pulled from a `PlayerCharacter`), picks a full-body clip through a move-driver registry, layers action and driver overlays on top, then runs one box-bounded Levenberg-Marquardt solve for the root offset and per-bone corrections, and emits a `SkeletonPose`. Nothing here writes sim state. The only dependency in the other direction is the `AnimTag` enum: it is defined in `CharacterAnimSample.cs:59`, and sim movement states return it from `Character/Movement/Movement.cs:124`. Drawing the pose and skins belongs to `Drawing/` (`SkeletonRenderer`, `AttackGlowSystem.RigRoot`, `CosmeticUpdateSystem`). The rig types `Skeleton`, `SkeletonPose` and `Affine2` also live in `Drawing/`. Authoring tools (`MTile.Probe`, `MTile.Demo`) use the clip-model and scene files listed under "Authoring-only" below.

## Files
Runtime (per-frame path):
- `CharacterAnimator.cs` (1581): the hub. It owns clip binding, the per-frame pipeline, the contact lifecycle and solve orchestration.
  - 15 `AnimClip` enum · 23 `CharacterAnimState` · 75-353 fields and diagnostics accessors · 355 ctor (362 composes ExtraBones, 391-400 sizes the solver, 409-419 computes reach → `_invCharLen`, 439-452 core blocks, 460-470 binds clips, 474 builds drivers)
  - 501 `Update` (resolves FlipAt → `DisplayFacing`) · 525-890 `UpdateCore` (numbered steps 0-4) · 896 `Draw` · 919 `SampleAttachments` · 933 `TryBoneOrigin` · 947 `TryBoneEnd` · 963 `TryComReference`
  - 978 `TryActionOverlay` · 1015 `IsOverlayAction` · 1028 `SettleWanted` · 1040 `ResolveSettle` · 1065 `SampleT` · 1086 `TravelRate` · 1093 `BestMatchingPhase`
  - 1128 `WeightedContactsAtPhase` · 1153 `RefreshContacts` · 1220 `MirrorPlans` · 1254 `TransferContacts` · 1291 `SnapToSupport`
  - 1316 `SolvePoseLm` · 1351 `SolveStaticPose` · 1412 `ApplyJointBounds` · 1441 `FreezeProblem` · 1495 `ResolveActionAim` · 1544 `FillSmoothTargets` · 1557 `CaptureAimTarget`
- `CharacterAnimator.Diagnostics.cs`: test and tuning hooks (`MaxJacobianError` is the FD-vs-analytic oracle, plus `SolveScaleReport`, `CaptureDebug`, and the residual/column breakdown gated by the static `CaptureResidualBreakdown`). `CharacterAnimator.Baseline.cs`: read-only accessors for `MTile.Bench`.
- `CharacterAnimSample.cs`: `ExternalPin`, `SolverSurface`, `AnimTag`, the sample struct, and `From(PlayerCharacter, …)` (219), which builds the sample and runs the stair probe, the ceiling probe and the ground-gap probe.
- `MoveDriver.cs` (581): `ClipTimeMode` 31, `ClipChoice` 42, `FrameInputs` 66, `AnimHistory` 96, `IMoveDriver` 171, the registry `MoveDrivers.CreateDefault` 192-217, then one class per driver (TagClip 221, GroundLocomotion 236, SkidMotion/SkidDriver 348/367, Landing 385, RunStop 424, LaunchClip 445, Airborne 472, Crouch 515, Parkour 536).
- `OverlayStack.cs`: three crossfading overlay slots (slot 0 = the action) composed alpha-over, plus `BaseBlend` = Π(1−w) per bone.
- `BoneMask.cs`: `AnimRegion` → bone mask. UpperBody = the "chest" subtree, LowerBody = its complement. `SkeletonComposition.cs`: unions the `ExtraBones` of every loaded clip whose `Skeleton` is this rig into the rig.
- `AnimationSampler.cs`: C1 Catmull-Rom sampling (`SampleSmooth` 147), its exact derivative (`SampleAngularVelocity` 192), and `IsCyclic`/`HasOpenTail`/`SeamMismatch`. `AnimAdditionSampler.cs`: sampling of the per-keyframe Point/Vector additions (`SamplePoint` is the sparse C1 track behind `com`/`body_path`).
- `BodyPath.cs`: the placement contract. It samples the `com` anchor c(t) and computes `RootOffset` = −facingAndScale(c).
- `SolveProblem.cs`: `ActiveContact`, `ISolveConstraint`, the frozen `SolveProblem` (variable layout at 54), and the `PoseEval` scratch. `SolveObjective.cs`: `SolveForward.Run/RootAt/Velocities`, `SolveObjective.Residuals/Jacobian`, and `PointJacobianColumns` with `Lever`.
- `SolveConstraints.cs`: the eight blocks: PlantedContacts 27, SwingTarget 78, FixedPoint 125, NoPenetration 190, ActionAim 363, ComOffset 436, PosePrior 471, ThetaSmoothness 505.
- `LeastSquaresSolver.cs` (657): delegates 28/35 · FD overload 121 · static knobs 133-182 (`Ftol`, `VectorizeNormalEquations`, `DoubleAccum`, `UseQr`, `JacobiScale`, `ActiveSetBounds`) · `ApplyActiveSet` 189 · `Minimize` (analytic J; null J → FD) 216 (normal equations 275-364, trust loop 366-439) · QR path 486/523/542 · Cholesky 573/624.
- `AnimSolverConfig.cs`: hot-reloaded knobs (static `Current`), `CopyFrom` 177, `Load` 217.
- `GaitTiming.cs`: the timing stage. It owns the locomotion phase advance (`Advance` 107), the settle state machine, and the foot servo (`Observe` 238).
- `ClipStrideTrack.cs`: compiles contact spans into per-foot stances and swings (`TryCompile` 126) and computes the cycle displacement. `StepPlanner.cs`: landing selection and swing paths per foot (`Update` 120, `Rebind` 393). `SupportQuery.cs`: finite tile-top treads for the planner.
- `ClipLoopBack.cs` (`Plan` 41) plus `ClipTransitionGraph.cs`: a cadence clip may jump from its tail to an earlier phase when that pair scores cheaper than its authored seam.
- `TerrainSurfaces.cs`: `Extract` 61 builds body-facing exposed tile and sprout faces around last frame's tips. It runs in the host, before `Update`.
- Clip model: `AnimationDocument.cs` (the document plus `AnimationStore` load/save), `PoseState.cs` (`PoseBoneEntry` Rotation/Stretch, `PoseData.Apply`), `ContactSpan.cs`, `AnimCurve.cs`, `Endpoints.cs` (`NamedPoint`, `EndpointResolver`), `AnimAttachment.cs` (elements and `AttachmentSampling`), `SkeletonStore.cs` (rig JSON ↔ `Skeleton`).

Authoring-only (no game-loop caller): `ClipMotion`, `ClipScene`, `ClipSceneBake`, `ClipArcMap`, `ClipArcs`, `SceneReferences`, `ClipLoopCut`, `MotionProbe`, `PoseIk`. Their callers are `MTile.Probe/Program.cs`, `MTile.Demo/*` and tests.

Data:
- `Skeletons/<rig>.json` = `{Name, Bones[{Name, Parent, Rotation, Length, MinRotation?, MaxRotation?}], Points[{Id, Bone, End, Role, …}]}`.
  - `biped` has 11 bones. Its named points are `support_l`/`support_r` on the `leg_*_lower` End, and `leg_*_lower` carries bounds [0, 3.05].
  - `biped_rabbit` has 17 bones (adds shoulder_*, hip_*, foot_*). Its `support_l`/`support_r` sit on the `foot_*` End, and it has no joint bounds.
  - Parents are resolved by name. An unknown parent makes `Build` throw (`SkeletonStore.cs:132-133`). `Load` catches that and returns null (`:61`), and `SkeletonExamples.Load` then throws `FileNotFoundException`.
- `SkeletonStates/<rig>/*.json` holds one `AnimationDocument` per file (biped: 64, biped_rabbit: 64). `SkeletonStates/rabbit_derived/` holds only a README, and the game does not load it.
  - The library is authored, not stubs: per rig 61 of 64 clips have more than one keyframe (3 are single-key), up to 17 keys (33 for biped_rabbit `Stairs`); 55 carry a `com` anchor; contact spans are on 8 biped clips (Walk, Run, CrouchWalk, Stairs, Parkour, Mantle, ArcJump, LedgePull) and 9 rabbit clips (+StepUp), all `SelfPlant`.
- The filename is irrelevant; `Type` binds (and `Skeleton` must equal the rig name). Main fields, all defaulted (`Type` "Misc", `Skeleton` "biped", `Duration` 1, `Loop` true, `Region` FullBody): `Keyframes[{Time, Bones[{Bone, Rotation, Stretch?}], Additions[]}]` is the only one that must be non-empty.
- Optional clip-level fields: `Contacts`, `Points`, `ExtraBones`, `Attachments`, `SettleShare`/`SettleClip`, `FlipAt`, `InFront`, `AimFrom`/`AimTo`/`AimTo2`, `OffRegionWeight`, `Motion`, `Scene`, `Arcs`.
- Reserved addition names: `com` (the runtime placement anchor) and `body_path` (the authoring scene path, ignored by runtime placement; `BodyPath.cs:19-23`). The runtime does read `body_path` for pacing: its p(1)−p(0) is `GaitTiming.CycleDisplacement`'s first source (`GaitTiming.cs:88`).
- The live rig is `configs/game_config.json` `AnimationRig` (currently `biped_rabbit`).

## Flow
1. Boot: `Game1.cs:764` loads the rig through `SkeletonExamples.Load`. `Game1.cs:778` runs `AnimationStore.LoadAll(SkeletonStates/<rig>)`; the web build uses the `index.json` manifest at `Game1.cs:769`. `Game1.cs:782` constructs `new CharacterAnimator(rig, SkeletonScale, clips)`. Config is loaded at `Game1.cs:313` (web) or `Game1.cs:362`, with a FileSystemWatcher at 363 (reload at 372).
2. The ctor binds clips (`CharacterAnimator.cs:460-470`). It skips any clip whose `Skeleton` differs from the rig name. If `Type` parses as `AnimClip` (case-insensitive), the clip goes into `_clips`; otherwise it goes into `_actionClips` keyed by the exact `Type`. The first match wins in `AnimationStore` sort order (`Type/Name`, ordinal; `AnimationDocument.cs:291`).
3. Per frame: `Drawing/CosmeticUpdateSystem.cs:98` gates on `config.RunAnimationSolver && simDt > 0`. Line 103 calls `TerrainSurfaces.Extract` with last frame's pose and `DisplayFacing`, and line 107 calls `_animator.Update(CharacterAnimSample.From(player, simDt, …))`. The animator therefore ticks on sim time, not render time.
4. `Update` (`CharacterAnimator.cs:501`): while an action clip is still before its `FlipAt`, the whole frame runs with the facing flipped (`WithFacing`).
5. `UpdateCore`, step 1 (`:543-547`): the first driver whose `Matches` returns true selects a `ClipChoice`. A settle override follows at `:560-568`. When the clip changes (`:573-601`), `ClipTime` resets, the phase is set from `MatchPose`/`StartT` (otherwise it persists), and `TransferContacts` runs. Loop-back for cadence clips is at `:616-633`.
6. Step 1.4 (`:638-639`): `driver.Contribute` fills `_frame` with overlay requests, pins, extra constraint blocks, and per-frame `Solver` overrides.
7. Step 1.5 (`:656-663`): the action overlay is resolved (`TryActionOverlay`, or `ResolveSettle` through `RecoveryAction`) and `_overlays.Update` is called. Overlay poses are sampled once here and stay frozen for the solve.
8. Steps 1.6-1.8 (`:672-724`): per-bone ease factor b and smoothness weight λs are computed. Then pins (sample pins plus driver pins, at most 4) and surfaces (at most 16) are collected, the aim is resolved, and `_problem.Blocks` is assembled as core geometric blocks, then driver blocks, then priors.
9. Step 2 (`:732-769`): `GaitTiming.Advance` advances the phase from body travel over the authored cycle displacement. `CadencePhase` clips use this; `IdleBob` advances at 0.30 Hz.
10. Steps 2.1-2.2 (`:774-804`): `StepPlanner.Update` runs if the sample carries chunks, `PlannerEnabled` is set, and the stride track has feet. For a CadencePhase clip that has contact spans (`:794`), `RefreshContacts` then either mirrors the plans or captures self-plants, and `SolvePoseLm` runs if there are contacts or swings. Otherwise `_contacts` is cleared.
11. Step 2.5 (`:816-817`): if no solve ran and pins, nearby surfaces or an aim exist, `SolveStaticPose` runs (with a dormancy pre-check at `:1373-1398`).
12. Each solve: the box is set with Δφ locked to [0,0], `FreezeProblem` copies inputs and captures the smoothness targets and aim target from the x=0 pose, and `ApplyJointBounds` is applied. Then `LeastSquaresSolver.Minimize` (default 12 iterations) is called with closures over `SolveObjective.Residuals/Jacobian` (`:433-434`).
13. Steps 3-4 (`:828-887`): the base clip is sampled at `SampleT`. `_overlays.Compose` runs, then the solved Δθ is added, or the closed-form ease toward `_thetaEmitted` on the fast path. Joint bounds are clamped here only on the fast path (the solve got them as box bounds via `ApplyJointBounds`). `_thetaEmitted`/δ/d.x are stored, and the pose is copied to `_pose`.
14. Hosts place the rig at `AttackGlowSystem.RigRoot` (`Drawing/AttackGlowSystem.cs:202`): body + `RootOffset(com)` + (`HorizontalOffset`, `VerticalOffset`). Without a `com` anchor, the fallback is the sole line.

## Invariants and traps
- **The render-only boundary is a convention, not compiler-enforced.** Animation is compiled into the same `MTile.Core` assembly as the sim. Sim code references only `AnimTag` from this area; the other mentions in `Character/` are comments. `CharacterAnimSample.From` and `SkidMotion` (`MoveDriver.cs:361`) only read sim objects (`MovementConfig.Current`, `GroundChecker`, `CeilingChecker`).
- Per-animator state that rollback does not snapshot, and that may differ between peers: `_state`, `_history`, `_thetaEmitted`, contacts, planner, and `DisplayFacing`. Statics: `AnimSolverConfig._current`, the `LeastSquaresSolver` knobs (`LeastSquaresSolver.cs:133-182`), `CharacterAnimator.CaptureResidualBreakdown`, and `CharacterAnimSample._wallSurfaceScratch` (`:209`). The vectorized normal equations change float results by design (`LeastSquaresSolver.cs:291-294`).
- **Binding failure modes.** A missing movement clip throws on the first frame a driver picks it (`CharacterAnimator.cs:823-826`). A missing action clip silently draws no overlay (`:1002`). A clip with zero keyframes is silently dropped at load (`AnimationDocument.cs:282`).
- `IsOverlayAction` (`CharacterAnimator.cs:1015-1020`) never overlays None/NullAction/ReadyAction/RecoveryAction/BlockPlaceAction/BlockPaintAction. The bound `blockplace.json`/`blockpaint.json` clips (Types `BlockPlaceAction`/`BlockPaintAction`) are therefore never drawn by any path. Only a `"BlockPlaceAction.<Variant>"` phase clip could play, through the variant-only branch, and none exists. `ClipBindingTests` exempts both actions.
- Phase clips are typed `"<Action>.<Variant>"` (`VariantKey`, `:1013`). A variant-only action (no base clip, for example `RecoveryAction.Charge`) plays `<key>Run` when the body is grounded and moving faster than 25 px/s (`:991-999`).
- Overlay time τ is the action's `AnimationProgress` clamped to [0,1] and scaled by `1−SettleShare`. When the action reports progress < 0, τ is the clip's own normalized `ActionTime` instead (`:1003-1005`). `SettleClip` plays a successor clip whole down the recovery countdown (`:1048-1053`).
- Compose order is shared between the solve and the draw: base sample → overlays → +Δθ (`SolveObjective.cs:18-24`, `CharacterAnimator.cs:830-851`). Because Δθ is applied after compose, a pin can bend a bone the overlay owns. Overlay paint goes from back to front with slot 0 last, so the action wins any bone it shares (`OverlayStack.cs:158`). Ease rates are 25/s in and 8/s out (`:37-38`).
- Δφ is always locked to [0,0] in both solves (`CharacterAnimator.cs:1322,1360`), so `GaitTiming` alone owns the phase. The Δφ Jacobian column still exists, scaled by `BaseBlend`.
- Variable layout: x = [Δφ, δ, d.x, Δθ_0…] (`SolveProblem.cs:54`). d=(d.x, δ) is added on the residual side to tips, in world px, unflipped by facing. The FK never sees it (`SolveConstraints.cs:39-40`), and the host adds it the same way (`AttackGlowSystem.cs:211-212`). Swing rows do include the d columns (`SolveConstraints.cs:90-91`), although `Plans/ANIMATION_ANNOTATIONS.md` §3 says they do not.
- Block order in `_problem.Blocks` is the row order the LM core and FD oracle depend on (`CharacterAnimator.cs:436-452,721-724`). Each block's row count must stay constant across one `Minimize`; inactive no-pen rows emit 0 rather than being dropped (`SolveConstraints.cs:186-187`).
- Units: pixel rows are multiplied by `_invCharLen` (1/reach, `CharacterAnimator.cs:418`). Angle rows are radians. Both are dimensionless, so config tiers compare across row kinds.
- Contact weights are frozen per solve (`SolveConstraints.cs:23-26`). `RefreshContacts` reads the sign of dw/dφ to start a time-based release (`CharacterAnimator.cs:1182`). A null span weight is a ramp, not 1.0 (`ContactSpan.cs:69`), and `DefaultWeight` is a shared static, so use `EnsureWeight()` before editing (`:77`).
- Contacts are read only for `CadencePhase` clips (`CharacterAnimator.cs:794`). Spans on Clock clips (parkour/mantle/arcjump/ledgepull) are inert at runtime. No runtime code branches on `ContactSpan.Source`, so an `External` span would behave exactly like `SelfPlant`. Only `MTile.Demo` (`DemoGame.Ui.cs`, `DemoGame.cs`) and `MTile.Probe` read or write it, and no clip on disk uses `External`. `EndpointResolver.BoneOf` throws for non-tip points (`Endpoints.cs:87-99`), so editor and draw paths must use `TryResolvePoint`.
- Per-frame config: solve rows read `_frame.Solver`, which drivers may override (`MoveDriver.cs:71-79`). Some reads go to `AnimSolverConfig.Current` directly, so driver overrides of them have no effect:
  - `LoopBackRegion`/`LoopBackMinLoop` (`CharacterAnimator.cs:293`), `LoopBackEnabled` (`:616,786`), `PlannerEnabled` (`:775,1257`).
  - In the planner: `ContactReleaseTime`, `ContactEngageTime`, `PlannerLateSwingLock`, `PlannerReplanDistance` (`StepPlanner.cs:126`, used at 128/150/236-237), `ContactEngageTime` (`:369`), `PlannerHysteresis` (`:498`).
  - The only override in the code today is `ComWeightY` (`MoveDriver.cs:333`), which goes through `_frame.Solver` and works. A new knob must be added to `CopyFrom` (`AnimSolverConfig.cs:177`), which `AnimSolverOverrideTests` checks.
- Y-down. Bone rotation 0 points along local +X, and negative rotation points up: biped `chest` = −π/2. `BoneTransform.ToAffine` is R·T·S (`Drawing/Affine2.cs:100-106`), so `world[i].Translation` is bone i's far tip, not its joint. "Upward-facing" surfaces test `Normal.Y < -0.7` (`CharacterAnimator.cs:1297`). Facing mirrors through root scale X = dir·scale.
- `TryBoneOrigin` returns `world[b].Translation`, which is the tip (`CharacterAnimator.cs:941`). **Bug:** `TryBoneEnd` (`:953`) evaluates `world[b].TransformPoint((Length, 0))`, which is tip + Length·scale along the bone. That point is one unstretched bone length past the hand, not the hand. Its only caller is `MotionFxSystem.WallHand` (`Drawing/MotionFxSystem.cs:201-202`).
- Comments that name things that do not exist:
  - `CharacterAnimator.Constraints.cs` (`CharacterAnimator.cs:38`). The constraints live in `SolveConstraints.cs`.
  - `CharacterAnimator.SolveRootAt` (`BodyPath.cs:25`). The function is `SolveForward.RootAt`.
- Comments that contradict the code:
  - `CharacterAnimSample.Dt` says "render delta" (`CharacterAnimSample.cs:77`), but the host passes simDt.
  - The `LeastSquaresSolver` header describes finite-difference Jacobians, but the animator always passes an analytic one (`CharacterAnimator.cs:1333,1402`).
  - `ClipStrideTrack`'s header describes per-keyframe labels, but stances come from spans (`ClipStrideTrack.cs:173-193`).
  - The `FixedPointConstraint` header (`SolveConstraints.cs:120`) shows the X row without d.x, but the code adds d.x (`:137,160`).
- `AnimationSampler.IsCyclic` is cached on the doc (`CyclicCache`). The open-tail loop rule applies only to Walk/WalkBack/Run/Idle/CrouchWalk Types (`AnimationSampler.cs:256-259`).

## Where to change X
- Which clip a movement state plays: `MoveDrivers.CreateDefault` order and the driver classes (`MoveDriver.cs:192`). A new state also needs an `AnimTag` value (`CharacterAnimSample.cs:59`) and a `MovementState.AnimationTag` override.
- Add an `AnimClip`: extend the enum (`CharacterAnimator.cs:15`) and author a clip with that `Type` in every rig dir.
- Action overlay timing, settle, variants: `TryActionOverlay`/`ResolveSettle` (`CharacterAnimator.cs:978-1059`), plus the clip fields `SettleShare`/`SettleClip`.
- Overlay blending or region masks: `OverlayStack.cs`, `BoneMask.cs`, and the clip fields `Region`/`OffRegionWeight`.
- A new solver row: implement `ISolveConstraint` in `SolveConstraints.cs`, register it in `_coreGeom`/`_corePriors` (`CharacterAnimator.cs:439-452`) or contribute it per frame through `FrameInputs.Constraints`, raise `MaxContributedRows` (`:396`) if needed, and verify with `MaxJacobianError`.
- Weights, limits, planner and timing knobs: `configs/anim_solver_config.json`, with the matching property and `CopyFrom` in `AnimSolverConfig.cs`.
- Phase pacing and stopping: `GaitTiming.Advance`. Foot landing choice: `StepPlanner`/`SupportQuery`.
- Terrain no-pen sourcing: `TerrainSurfaces.Extract`. Potential shape: `NoPenetrationConstraint.Potential` (`SolveConstraints.cs:218`).
- Sample fields from the sim: `CharacterAnimSample` plus `From` (`:219`).
- Clip file format: the `AnimationDocument` properties plus the `AnimationStore` options (`AnimationDocument.cs:193`).

## Tests
`MTile.Tests/Animation/` (most classes are in namespace `MTile.Tests`). `scripts/test-group.py animation` matches the terms Anim/Pose/Skeleton/BoneMask/CharacterAnimator/MotionProbe/ParkourGrip/TerrainNoPen/NoPenetration/Attachment/Smoothing/SlashCombo/…, which covers e.g. `AnimSolverTests`, `AnimSolverOverrideTests`, `AnimGaitTimingTests`, `AnimStepPlannerTests`, `AnimLoopBackTests`, `AnimContactSpanTests`, `CharacterAnimatorTests`, `PoseIkTests`, `NoPenetrationSolverTests`, `TerrainNoPen*Tests`, `StairAnimationTests`. Classes in that directory that the `animation` group misses, and the group that does run them:
- combat: `ActionAimSolverTests`, `ActionOverlayTests`, `ClipBindingTests`, `SeamGuardTests`
- physics: `FixedPointSolverTests`, `QrStepTests`
- movement: `LandingContinuityTests`, `PhaseAccelTests`, `PreRunAirborneTests`
- terrain: `DenseTerrainScrubTests`, `OpenTailLoopTests`, `SeamGuardTests`
- simcore: `TraceExportTests`

Terms match the full FQN including the method name, so a few methods in these classes also run under `animation`, e.g. `ClipBindingTests.EveryAnimClip_HasAnAuthoredFile` and `OpenTailLoopTests.PoseIsContinuousAcrossSeam`.

For any change to binding or the solver, run `animation combat physics movement`.
