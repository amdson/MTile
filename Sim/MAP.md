# Sim core map
Verified against: 069e3a3 (2026-11-14)
Covers: Simulation.cs, SimSnapshot.cs, Stage.cs, Sim/, Physics/

## What this area is
`Simulation` is the deterministic world: one `Step` per `FixedDt` (1/60), plus `Snapshot`/`Restore`/`Checksum` for rollback (`Net/RollbackSession.cs:152-229` is the main caller; `Game1.cs:686,1147-1166` steps offline). The sparse-set ECS under `Sim/ECS/` owns identity, iteration order and the snapshotted value components. `Physics/` integrates and collides bodies against terrain (`ChunkMap`/`WorldQuery`, World area) and resolves hit momentum. `Stage.cs` builds a match. Out of scope: combat registries and `CombatSystem` (World/), FSMs (Character/), entity behaviour (Entities/), terrain journal (World/), `Net/`.
All of this is sim-side. Nothing here draws; the events at `Simulation.cs:303-329` are cosmetic hooks that `Game1` subscribes to.

## Files
- `Simulation.cs` (896): the world object. Implements `IEntitySpawner`, `IChunkProvider`, `ITargetSource`.
  - 22-121 fields: `FixedDt`, `Gravity` (0,600), `_world`, `_bodyScratch`/`_entityScratch`, `_combat`, `_spikes`, `_crystals`, `_hitIds`, `_stageTickers`, `_platforms`, `_elapsed`, `_frame`
  - 134 `MarkWorldStores`; 147 `RegisterPlayer`; 160 `ResolveHittable`
  - 168-299 spawner/target API: `Resolve` 186, `NearestPlayer` 193, `TryFindTarget` 224 (sticky 75% rule), `Mines` 261, `Rays` 287
  - 303-359 cosmetic events + read-only views (`Entities`/`Bodies` allocate a fresh list on every access, 339-349)
  - 365 headless ctor (tests/tools); 383 stage ctor; 452 `StreamTerrain`
  - 462 `SpawnEntity`; 474 `AddPlatform`; 480 `AddTicker`; 485 `AddSecondaryPlayer`
  - 508 `Step(p0,p1)`; 518-659 `Step`; 667-772 `Checksum`; 779-816 `Snapshot`; 818-895 `Restore`
- `SimSnapshot.cs` (70): the snapshot DTO, plus `PlatformState`.
- `Stage.cs` (1265): `Stage` sealed class (14-35: `TerrainConfig` | `Terrain` func, `PlayerSpawn`, `SecondSpawn`, `Populate`, `Coop`, `Versus`, `IdleSecondary`) and the `Stages` registry.
  - 41-396 static ctor: registers 26 stages inline (start, arena, plain, training, corridor, warlord, prism, quillback, stormcaller, frogs, spearman, pulser, weights, hover_targets, warden, wizard, gym, stairs, gauntlet, sandbox, aspid, fighters, fighter_gauntlet, hill, flat, spires). 199-201 call the partials `RegisterDepths`/`RegisterDuels`/`RegisterWallDummy` in `Stages/*.cs`.
  - 398 `Register`; 400 `Get` (unknown name falls back to `"start"` silently)
  - 405-1265 populate bodies: Start 405, Arena 457, Training 483, Sandbox 596, FighterGauntlet 649, Fighters 705, Aspid 726, Gauntlet 766, Hill 851, Weights 942, HoverTargets 1019, Warden 1048, Spearman 1079, Pulser 1088 (both via `KeepStocked` 1094), Warlord 1127, Prism 1145, Quillback 1163, Stormcaller 1182, Frogs 1208, Wizard 1226, Plain 1242
- `Sim/ECS/World.cs`: entity slots + generations (slot 0 reserved = `EntityId.None`), free-list recycling, `Add/Get/Has/Remove`, `Query<T1..T3>`, `MarkLiveOnly`, `SetCloner`, `Capture`/`Restore`.
- `Sim/ECS/ComponentStore.cs`: sparse-set store per component type; order-preserving removal; snapshot with an optional `Cloner`.
- `Sim/ECS/Query.cs`: alloc-free ref-struct enumerators. They iterate T1's packed order and gate T2/T3 on `Has`.
- `Sim/ECS/WorldSnapshot.cs`: generations, alive flags, free stack, per-store blobs.
- `Sim/ECS/Components/EcsComponents.cs`: live-only refs `PhysicsBodyComponent`, `EntityRef`, `PlayerRef`. Snapshotted values `BodyStateComp`, `EntityData` (unioned over every entity kind), `PlayerData` (+ `DeepCopy`). The fighter value structs `BrainScratch`, `TargetHistory`, `SenseMemory` are fields embedded in `EntityData` (189-195), not stores of their own.
- `Sim/ECS/Components/PeelGroupComp.cs`: sparse snapshotted component (InlineArray of 25 `PeelMember`). Only `PullPointEntity` carries it.
- `Physics/PhysicsWorld.cs` (900): static solver.
  - 11-87 `Step` (discrete; no callers anywhere, tests included)
  - 89-182 `ResolveChunkCollisions` (discrete push-out, deepest first, 12 iterations); 187 `CrushOverlappingSprouts`
  - 233 `TryBounce`; 244 `RecordImpulse`; 252-260 quadratic drag
  - 262-377 `StepSwept`; 388 `ProbeWavePin`; 421 `NotePinContact`
  - 459-687 `ResolveChunkCollisionsSwept`; 694 `ClipToFirstContact`; 728 `ComputeImpactCells`
  - 767 `UpdateSurfaceConstraint`; 801-838 friction; 847 `TryFindContactSurface`
- `Physics/PhysicsBody.cs`: body data plus per-step outputs (`LastImpulseMagnitude`, `LastCrushImpulse`, `LastCrushMovingSurface`, `SproutCrushCount`, `WavePinned`, `ContactEvents`) and the launch fields (`Launch`, `ImpactMassScale`, `LaunchBounce`, `HeldBounce`, `Frozen`).
- `Physics/BodyState.cs`: per-body snapshot unit (`Capture`, `RestoreInto`, `DeepCopy`, contact clone).
- `Physics/Launch.cs`: `Launch` struct + `LaunchSystem` (`Hit`, `Claim`, `StepSwept` = PreStep → `PhysicsWorld.StepSwept` → PostStep).
- `Physics/HitResolver.cs`: `KnockbackMode`, `HitResult`, `Resolve` (Impulse / Collision), `ImpactStrength`, `NominalStrength`, `TileRecoil`.
- `Physics/ImpactSpringField.cs`: Jacobi spring-field crater model; `Apply` chips and breaks cells and reports `PathCleared`/`HpBroken`.
- `Physics/ImpactDamage.cs`, `ImpactProfiles.cs`: the per-body impact config, and the named profiles loaded from `configs/impact_profiles.json`.
- `Physics/PhysicsContact.cs`: `PhysicsContact` → `SurfaceContact` → `SurfaceDistance` (solver, `Maintained`) / `FloatingSurfaceDistance` (state-owned); `PointForceContact`.
- `Physics/Collision.cs`, `Polygon.cs`, `BoundingBox.cs`, `CollisionResult.cs`, `SweptResult.cs`: SAT overlap + swept SAT, convex polygons, float AABB with `Strip*`/`Inset*` probe builders.
- `Physics/ContactEvent.cs`: per-impact record (`Stop`/`Bounce`/`BreakThrough`). `Physics/Bezier.cs`: scalar cubic only.

## Flow
Match build (`Simulation(GameConfig, Stage)`, `Simulation.cs:383-444`; called from `Game1.cs:511` with `Stages.Get(...)` from `Game1.cs:301`):
1. Terrain comes from `stage.Terrain()` or from `TerrainLoader.Load("Levels/"+TerrainConfig)` (a rooted path is used as-is) (385-401).
2. Set `Coop`/`Versus` (403-405). On a `WorldGenerator` level the spawn is snapped to the surface (411-414).
3. `MarkWorldStores` (416). Register the primary player (417-418). If `config.SpawnSecondPlayer`, call `AddSecondaryPlayer` at `SecondSpawn ?? spawn+offset` (421-426). Apply `StartingBlockType` (430-434).
4. `stage.Populate(this)` (437) → `SpawnEntity`/`AddPlatform`/`AddTicker`/`AddSecondaryPlayer`. Then `StreamTerrain` (441) and `RecomputeTerrainHash` (443).

`Step(input)` phase order (`Simulation.cs`):
1. Inject input; `_elapsed += dt`; `_frame++` (520-523). `Step(p0,p1)` first injects p1 into `_secondaryPlayers[0].Ctrl` (508-513).
2. `StreamTerrain` (529): endless levels only, using player positions from last frame.
3. Stage tickers `tick(_elapsed)` (538).
4. `_chunks.TickSprouts(dt)` (540); `_chunks.Impact.Tick(dt)` (542).
5. Clear hitboxes, hurtboxes, force fields (547-549).
6. `PublishHurtboxes`: primary, then secondaries, then entities in World order (551-553).
7. `Update` for the primary player (555), then for each secondary (562-563). These publish hitboxes and fields and may spawn entities.
8. Copy the entity set to `_entityScratch`, then `e.Update` each one (567-569).
9. `_combat.Apply` (571): hitboxes resolve here, before physics (the "resolve post-step" remark at 575 and in the overview is stale).
10. `ForceFieldSystem.Apply` (576-585), which flags grabbed/thrown victims.
11. Respawn the dead primary (588-593) and dead secondaries (598-604).
12. `PreStep`: entities (608), then players (610-611). These set `Frozen` and `HeldBounce` and park hitstop/rooted bodies.
13. Project `PhysicsBodyComponent` into `_bodyScratch` in World order (614-615). Run `_spikes.CapturePreStep` (616), then `LaunchSystem.StepSwept` (617).
14. `_spikes.Apply` (623), then `_crystals.Apply` (627).
15. Drain `ChargedBreaks` into `ChargedBlast` entities (643-651).
16. Dead sweep: `_world.Destroy` for each `IsDead` entity (655-658).

Physics (`LaunchSystem.StepSwept`, `Launch.cs:75-81`): `LaunchSystem.PreStep` on every body (sets `ImpactMassScale`/`LaunchBounce`, decays the launch out of `Velocity` unless `Frozen`, 85-102), then `PhysicsWorld.StepSwept` over all bodies, then `PostStep` on every body. Per body inside `PhysicsWorld.StepSwept` (262-377), bodies in list order:
1. Clear `ContactEvents` (270). Then `v += AppliedForce·dt`, `AppliedForce = 0`, `v += g·dt`, drag (272-275).
2. Reset the per-step outputs (279-286). `IgnoreTiles` bodies just integrate and skip the rest (289-293).
3. Zero contact `LastImpulse` (297). Advect each `SurfaceDistance` anchor by its surface velocity (308-309).
4. For every `SurfaceContact` inside `MinDistance`, zero the relative normal velocity, then apply friction (311-333).
5. Discrete depenetration pre-pass (341; ≤12 deepest-first push-outs, and on budget exhaustion `CrushOverlappingSprouts` breaks sprout cells, 180), then the swept resolve (343). The sweep allows ≤4 bounces (461) and treats `FloatingSurfaceDistance` as planes (501-516).
6. Terrain impact (557-610): `ke = ½·Impact.Mass·ImpactMassScale·vn²` goes to `ImpactSpringField.Apply` (579). Cells that break cushion the crush Δv. `PathCleared` lets the body continue with the energy that is left.
7. Bounce (627); push a `ContactEvent` (631); stamp or refresh a `Maintained` `SurfaceDistance` (661, 767-797). If the bounce budget runs out, `ClipToFirstContact` (683-684).
8. Prune stale `SurfaceDistance`, refreshing surface velocity by query (345-373). Then `WavePinned = ProbeWavePin` (375).
9. `LaunchSystem.PostStep` reflects the launch on `Bounce` events, then clamps it to the real velocity (`Launch.cs:106-131`).

Hit momentum: `PlayerCharacter.cs:262`, `EnemyEntity.cs:229` and `Entity.cs:117` call `HitResolver.Resolve`. A launching hit then goes through `LaunchSystem.Hit` (`PlayerCharacter.cs:414-415`, `Entity.cs:121-122`). A throw uses `LaunchSystem.Claim` (`GrabThrowEntity.cs:92`). `CombatSystem.cs:245` calls `TileRecoil`.

Snapshot (`Simulation.cs:779-816`): capture the secondary controllers (781-783). Run `CaptureState(_world)` on the primary, the secondaries and every entity (789-791); this writes `PlayerData`/`EntityData`/`BodyStateComp`. Copy platform poses (793-795). Then build the `SimSnapshot` in this order: hit-id, `_world.Capture()`, elapsed, frame, deaths, respawn point, `StageVars` copy, controllers, combat dedupe/hitconfirm/recoil/hitstop, spike cooldowns, platforms, `_chunks.CaptureTerrain()` (797-815; journal mark plus value copies of Graph, Damage, Charge, SpikeSeeds, Crystals, Foam, Impact, Mass, Waves+cells, Ownership, SproutClock, TerrainFrame: `World/ChunkMap.cs:950-966`).
Not captured, by design: hitbox/hurtbox/force-field registries (cleared every Step), `ChargedBreaks` (empty at frame boundaries), ticker closures, `Blueprints`, `_crystals` (stateless; its tallies live in `ChunkMap.Crystals`), `ContactEvents` and the per-step-derived body fields below.
Restore (818-895): scalars (820-825); index live entities by id (829-830); `_world.Restore` (835), which clears the live-only stores; `_chunks.RestoreTerrain` (838); re-add `PlayerRef`/`PhysicsBodyComponent` and `RestoreState` for each player, plus its controller (844-855). For each id in the restored `EntityData` order, reuse the live object or call `EntityFactory.Rehydrate`, then re-add the refs and `RestoreState` (863-878). Finish with platforms (881-885) and the combat and spike tables (888-894).

## Invariants and traps
- CODEBASE_OVERVIEW's 7-step Step list is out of date. It omits `StreamTerrain` (529), player `PreStep` (610), spikes and crystals (616-629), charged blasts (643-651), and `LaunchSystem` wrapping the sweep (617). Use the list above.
- The overview (line 82), `PhysicsContact.cs:22-23` and `PhysicsWorld.cs:787-788` say soft contacts and/or `LastImpulse` are not snapshotted. They are: `BodyState.Capture` clones every constraint in order (`BodyState.cs:46-48`) and keeps `LastImpulse` (107-112). Movement states re-take their soft contacts by `Owner` (`PlayerCharacter.RestoreState` → `ResetTransient`).
- Forces are accelerations: `Velocity += AppliedForce * dt` (`PhysicsWorld.cs:272`). `AppliedForce` is zeroed inside the sweep (273), so read it before the sweep (as `SimRunner.cs:124-126` does).
- Normals point from the surface toward the body (`SweptResult.cs:11`, `Collision.cs:34-35`), and `vnRel < 0` means approaching. Y is down, so a floor normal has negative Y. Friction applies only to floors with `normal.Y < -0.7` (`PhysicsWorld.cs:773,838`). Friction is skipped when `|savedForce·tangent| > 1` (825).
- `ImpactDamage.ImpulseThreshold`, `DamagePerUnitImpulse`, `BreakThreshold` and `NormalRetainOnBreak` have no readers. Physics reads only `Mass`, `BounceRestitution` and `BounceImpulseThreshold` (`PhysicsWorld.cs:236,575`). They are only written (`ImpactProfile.ToImpactDamage` copies them from `configs/impact_profiles.json`, and tests set them); the JSON's field comments and `PhysicsWorld.cs:532-543` describe the dead model. `TryApplyImpactDamage` (named at `ImpactDamage.cs:12`) and `ApplyContactDamage` (`PhysicsWorld.cs:155`) do not exist; impact tile damage is `ImpactSpringField.Apply`.
- `PhysicsWorld.Step` (11-87) is dead. The comment at 339-340 says "cap 8", but `ResolveChunkCollisions` iterates 12 times (91).
- Every sim loop calls `LaunchSystem.StepSwept`, not `PhysicsWorld.StepSwept` (`Simulation.cs:617`, `SimRunner.cs:128,264`). Calling the latter directly (many unit tests do) skips launch decay and reconciliation. The launch pauses on `Frozen` bodies (`Launch.cs:96,109`).
- Crush accounting: `LastImpulseMagnitude` is a max over hits, while `LastCrushImpulse` is the length of a vector sum (`PhysicsWorld.cs:244-250`). `PlayerCharacter`'s crush path reads `LastCrushImpulse` (plus `SproutCrushCount`/`WavePinned`) on the NEXT frame's Update (`PlayerCharacter.cs:660`; the comment at 655-657 wrongly names `LastImpulseMagnitude`), so these live in `BodyState`. `ContactEvents` are not snapshotted and must only be read within the same Step (`PhysicsBody.cs:111-114`).
- Not snapshotted on purpose, because they are re-derived every step: `ImpactMassScale`, `LaunchBounce`, `HeldBounce`, `Frozen`, `CrushDvThisStep`, `PinPush/StaticNormals` (`PhysicsBody.cs:62,83-109`). `QuadraticDrag` is not in `BodyState` either; players set it every Update before the sweep (`PlayerCharacter.cs:755`), enemies only in their ctor (`EnemyEntity.cs:185`, `StalkerEnemy.cs:88`, `FrogEnemy.cs:141`), and nothing else writes it, so rehydration reproduces it.
- Snapshot ordering: every `CaptureState` must run before `_world.Capture()` (`Simulation.cs:785-791`). The World stores are the snapshot.
- `ComponentStore` removal shifts the tail rather than swapping (`ComponentStore.cs:94-101`). Spawn order = iteration order = hash and step order. Do not "optimise" this to swap-remove.
- Do not Create/Destroy/Add/Remove during a `Query` (`World.cs:96`). `Restore` collects ids first for this reason (`Simulation.cs:863-864`). The dead sweep collects first too (655-658).
- A component type that holds a mutable class reference must be `MarkLiveOnly` (never captured) or have a `SetCloner` (`Simulation.cs:134-141`). (`EntityData` shallow-copies its `Polygon`/`ImpactDamage` refs with no cloner; that is safe only because the sim never mutates them.) The cloner runs on both capture and restore so one snapshot can be restored repeatedly (`ComponentStore.cs:150-165`). `MarkWorldStores` must run before any registration. New rollback state: a struct field on `EntityData`/`PlayerData`/`BodyState` marshalled in `CaptureState`/`RestoreState`, or a new struct store added via `world.Add` in `CaptureState` (the `PeelGroupComp` pattern); `World.Capture` takes every non-live-only store generically (`World.cs:134-150`).
- Stores are created lazily (`World.cs:106-114`). On restore, a value store that is absent from the snapshot is cleared (`World.cs:172-173`).
- Restore re-registers all players first and entities second (`Simulation.cs:844-878`). Before a restore, `PhysicsBodyComponent` order is registration order. So a secondary added after any `SpawnEntity` sweeps in a different position after a restore than before it, and order matters: the sweep has no body-body interaction, but each body breaks/chips terrain (`ImpactSpringField.Apply`, `CrushOverlappingSprouts`) that later bodies then see, and `ChargedBreaks` order sets blast spawn order and HitIds. A peer that never rolled back would diverge. No shipped stage does it (the config P2 is added before `Populate`; training and wall_dummy add theirs before any entity). Keep it that way, or make Restore re-register in original order.
- Entities spawned during this frame's player or entity Update skip their own `Update` this frame (567-569), but they do get `PreStep` and the sweep (608, 615).
- The `ChargedBreaks` queue is filled and drained within one Step, so it is never in a snapshot (631-651). Anything that breaks tiles after 651 would leak across a frame boundary.
- Respawn happens after combat and before physics (588-604). Tickers normally see `PlayerDeaths` change on the next frame instead of a dead primary, but spike/crystal hits after the respawn (623-629) can leave the primary dead at the next frame's ticker phase, so the "never sees the player dead" comment at 179-181 overstates.
- Stage tickers get absolute `_elapsed`, not dt (538). State that a closure keeps does not roll back; use `StageVars` (175-178, snapshotted at 805).
- The training ticker injects into its dummy's controller (`Stage.cs:505-507`, 592). The dummy is secondary 0 only when `SpawnSecondPlayer` is off (otherwise the config P2 is secondary 0), and only then does `Step(p0,p1)` (net or offline bot) double-inject into the same controller.
- Statics: the `[ThreadStatic]` scratch at `PhysicsWorld.cs:722` and `ImpactSpringField.cs:136-142` is fine. `ImpactProfiles._current` (`ImpactProfiles.cs:52`, loaded at boot) and `MovementConfig.Current` (read at `PhysicsWorld.cs:773,838`; hot-reloadable unless multiplayer) are process-wide. `ImpactSpringField` is Jacobi with fixed rounds, so it does not depend on traversal order (60-62).
- `Checksum` folds sparse terrain side tables with XOR, which makes it order-independent (`Simulation.cs:705-709`). A new Dictionary-backed table must fold the same way, with a fresh salt. Salts used: 0x01-0x05, 0x07-0x0D.
- `HitResolver` Collision mode: the closing speed `u` is clamped at ≥0 and the `MinLaunch` floor applies (77-78, 101-102). `TileRecoil` is not self-limiting (137-144), so `CombatSystem` latches it once per attack (`CombatSystem.cs:243-247`). No production caller passes `scale`.

## Where to change X
- New per-frame sim phase: `Simulation.Step` (518-659). Also add it to `SimRunner.RunMulti` (`MTile.Tests/Sim/SimRunner.cs`, loop 206-283) if scenario tests should see it.
- New snapshotted entity field: add it to `EntityData` (`EcsComponents.cs:114`) and marshal it in the subtype's `WriteState`/`ReadState` (`Entities/Entity.cs:259-260`). If it also matters for rehydration, see `EntityFactory.Rehydrate` (`Entities/EntityFactory.cs:102`).
- New snapshotted player field: add it to `PlayerData` (`EcsComponents.cs:241`) and to `PlayerCharacter.CaptureState`/`RestoreState` (`Character/PlayerCharacter.cs:1062,1086`). Deep-copy reference members in `PlayerData.DeepCopy`.
- Sparse per-entity state: follow `PeelGroupComp`. Add it on capture if missing, read it on restore (`Entities/PullPointEntity.cs:453-460`). If it wraps a reference, register a `SetCloner` in `MarkWorldStores`.
- Sim-level scalar/table: add a field to `SimSnapshot`, then to `Snapshot()`/`Restore()`, then to `Checksum` if it is gameplay-significant.
- Body field that crosses a frame boundary: `BodyState` fields + `Capture`/`RestoreInto`.
- Collision response, bounce or break-through: `PhysicsWorld.ResolveChunkCollisionsSwept` (459) and `TryBounce` (233). Crater shape: the constants in `ImpactSpringField` (80-121).
- Knockback math: `HitResolver.Resolve`. Launch decay and bounce carry: `LaunchSystem`. Per-body impact tuning: `configs/impact_profiles.json` / `ImpactProfiles.Defaults`.
- New stage: call `Register(new Stage{...})` in the `Stages` static ctor, or add a `Stages/*.cs` partial whose `RegisterX` is called at `Stage.cs:199-201`. Select it with `configs/game_config.json` `Stage`.
- New moving surface: `MovingRectangle` + `AddPlatform`/`AddTicker` driven by `t` (pattern at `Stage.cs:411-419`).

## Tests
- `scripts/test-group.py simcore`: `SnapshotRoundTripTests`, `SimulationTests`, `TwoPlayerStepTests`, `RollbackHarnessTests`, `RollbackNetSweepTests`, `TerrainRollbackTests` (also in `terrain`), `CorrectorSnapshotTests`, `PullPointSnapshotTests`, and the stage tests (`TrainingStage`, `GauntletStage`, `CoopMode`, `DepthsStage`/`DepthsDeterminism`, `DuelStage`, `WallDummyStage`, `HoverTargetStage`, `StageSaver`, `ConfigLayout`).
- `physics`: `PhysicsTests`, `PhysicsLaunchTests`, `PhysicsContactImpulseTests`, `HighSpeedTunnelingTests`.
- `combat`: `HitResolverTests`/`HitResolverStrengthTests`, `ImpactCraterTests`, `SandImpactDamageTests`, `PlayerImpactByVelocityTests`, `RunningOverUnderImpactTests`.
- `movement`: `BounceTests`, `GroundFrictionTests`, `MovingPlatformTests`. `terrain`: `WorldTests` (ECS, `MTile.Tests/Sim/ECS/WorldTests.cs`; matched by "World"), `SproutCrushTests`.
- `FallingImpactDamageTests` matches no group term, so it runs only in `full`. Groups match on substrings of `FullyQualifiedName` (`scripts/test-group.py:95`).
- `SimRunner` (`MTile.Tests/Sim/SimRunner.cs`) is a partial mirror of Step:
  - `RunMulti` (loop 206-283) mirrors the sprout tick and phases 5-13, then the dead sweep (265). It omits streaming, tickers, `Impact.Tick`, respawn, spikes (incl. `CapturePreStep`), crystals and charged blasts, and it injects input per player inside the loop.
  - `Run` (loop 115-139) is sprouts → player Update → `LaunchSystem.StepSwept` (128), with no hurtboxes, entities or combat.
  - So the "mirrors Step's phase order" claims (`CLAUDE.md`, overview lines 56 and 397) are only partly true.
