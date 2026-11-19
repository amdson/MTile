# Entities map
Verified against: e5b75c6 (2026-11-19)
Covers: Entities/*.cs, Entities/Plants/*.cs, Entities/Projectiles/*.cs, Entities/Enemies/*.cs, Entities/Enemies/Types/*.cs, Entities/Enemies/Fighters/**/*.cs

## What this area is
Every non-player sim object: the `Entity` base (body, health, hit-feel, snapshot hooks), `EntityKind` (snapshot identity), `EntityFactory` (spawn + rehydrate), projectiles/block entities, and the enemy framework (`EnemyEntity` host, movement/action FSM states, brains, blueprints) with the per-enemy types and the fighter (compiled-spec) system.
All of it is sim-side except `Telegraph(...)` / `SyncSprite()` / `Color` / `Sprite` (render-only). Boundaries: `Simulation.cs` owns the ECS `World` and calls in; `Sim/ECS/Components/EcsComponents.cs` owns `EntityData` (the snapshot struct this area writes into); `World/Hitbox.cs` + `World/CombatSystem.cs` consume the hitboxes entities publish; `Presentation/TelegraphList.cs` + `Drawing/TelegraphRenderer` draw what entities declare; `Stage.cs` / `Stages/` decide what spawns.

## Files
Entities/ (root)
- `Entity.cs` base class (hurtbox, `OnHit`, `PreStep`, hitstop hold, `Rooted`, Capture/RestoreState, `WriteState`/`ReadState` hooks); `IEntitySpawner` (what `Update` may ask of the sim) at :265.
- `EntityKind.cs` the enum (:11) + `EntityKinds.IsFighter` range check (:166). `EntityFactory.cs` balloon/ball/stalker/turret ctors, `CreateEnemy` (:61), `SubclassEnemy` table (:74), `Rehydrate` (:102).
- `ChargedBlast.cs` fuse then crater for a broken charged tile. `MassBall.cs` coasting build-mass eruption payload. `PullPointEntity.cs` (479 lines) block-grab pulling point, owns peel group (only entity overriding `CaptureState`/`RestoreState`, :450/:457). `GrabThrowEntity.cs` released player-grab throw carrying a victim. `HoverTarget.cs` / `PracticeBall.cs` test/practice targets. `RayEmitter.cs` planted cone ray (Prism's). `EnemySpawner.cs` rooted nest that keeps N of a kind alive (plain `Entity`).
Plants/ (the plants feature, `Plans/PLANT_SEEDS_PLAN.md`; overview section "Plants")
- `PlantEntity.cs` abstract base (a `Rooted` `Entity` + `ITelegraphSource`): root check and burial in `Update`, growth, `Pose`, `SyncSprite` (rotates the figure onto the root face), shared burial/growth overlay. Its header comment is the subclass contract. `PlantTuning.cs`: `SeedKind` enum, `PlantPose`, `PlantData` (the rollback block in `EntityData.Plant`; field meanings per kind), `PlantRoot`, and every plant number (`PlantTuning`).
- `PlantRules.cs` pure legality: `PlantSite`, `PlantRequest`, `PlantVerdict`, `PlantRules.Evaluate` (reach, root, occupancy, caps, cost), close-reach rule `InReach` / `TryDirtSiteInReach` (root beside or beneath the body box), dirt-root rule, rotation/up helpers. `SeedPlanting.cs` the one entry point `TryPlant` (+ `IsImplemented`, `RequestFrom`); the crouched planting channel lives in `BlockPlaceAction`.
- `SpitterPlant.cs`, `SnapperPlant.cs`, `SiegePodPlant.cs` the three entity kinds (each has `FromState` for `EntityFactory.Rehydrate`, EntityFactory.cs:143-149). `SiegeBlast.cs` the siege shot, stateless: blast disc + per-material-budget lance, `Faction.Hazard`; also the read-only `Preview` the telegraph and the bots use.
- Thorns and block-eater are NOT here: they are tile state (`World/ThornPatches.cs`, `World/BlockEaterInfection.cs`).
Projectiles/
- `Projectile.cs` base (Age, Lifetime, sealed `Update` -> abstract `ProjectileUpdate`). `BulletProjectile`, `EnergyBallProjectile`, `StickyGrenadeProjectile`, `LobbedAreaProjectile` (507 lines; also the thrown clod), `RailBoltProjectile` (tile-breaking, Bastion), `FighterMine`.
Enemies/ framework
- `EnemyEntity.cs` the host (579 lines, see index). `EnemyState.cs` `EnemyContext`, `EnemyState<TVars>`, `EnemyMovementVars`/`EnemyActionVars`, `EnemyMovementState`/`EnemyActionState`.
- `EnemyController.cs` `EnemyInput` + brains: `ChasePlayerController` (default), `ProximityChase`, `MoveTowardPlayer`, `Patrol`, `StationaryAim`. `EnemyTarget.cs` `EnemyTarget` view + `ITargetSource`. `EnemyAim.cs` aim/line-of-sight helpers. `SurfaceProbe.cs` anchor probing for cling.
- `EnemyBlueprint.cs` (419) `EnemyBlueprint`, `BlueprintEnemy`, `EnemyFactory` registry + `RegisterBuiltIns` (:187): Skirmisher, Sparring, Bastion, Pouncer, Latcher, Bird inline; Shrike/Zeus/Template/Aspid via their `Blueprint`; `FighterRoster.RegisterAll()`.
- `ActionSpec.cs` `ActionKind` enum (:9) + `ActionSpec` knob struct + `Default(kind)` table (:100); `EnemyActions.cs` (637) Melee/Contact/Lunge/Slam/Ranged; `GauntletActions.cs` RailShot/PounceSlam/Lash; `EnemyBlockActions.cs` PlaceBlock/SpawnBlockInAir; `EnemyDashMineActions.cs` Dash/Mine (+`MineView`); `EnemyGuardShoveActions.cs` Guard/Shove.
- `EnemyMovementStates.cs` (654): Idle :18, Chase :36, AttackHold :111, Stagger :135, Jump :172, ClingMove :249, Fly :401, Hop :510.
Enemies/Types/ (one per enemy; most headers name the "Run it" stage. `TemplateEnemy`, `ZeusEnemy`, `ShrikeEnemy`, `AspidEnemy` are static classes holding a `Blueprint`, not subclasses)
- `TemplateEnemy` copy-and-edit example (brain + move state + action + `Blueprint`); its header rules 4-5 and stock-state list are stale (see Invariants).
- `BruteEnemy` hand-written reference subclass.
- `StalkerEnemy`, `TurretEnemy` older plain-`Entity` AIs with their own enums.
- `FrogEnemy` plain `Entity` swarm boss (hop/split/tongue/mutant burst).
- `StormcallerEnemy` rooted wind boss (index below).
- `QuillbackEnemy` armored hedgehog: flurry/spin/roll/dig/pillar/storm, shell on the Energy meter.
- `WarlordEnemy` duelist boss: cleave/ram/leap-slam/cage, memory in `Scratch`.
- `ZeusEnemy` rooted statue, bolt/strike/sweep (+`ZeusThunderColumn.cs` falling-column action, listed in Zeus's blueprint).
- `PrismEnemy` dash-only boss that plants `RayEmitter`s.
- `SpearmanEnemy` stab + regrowing spear + parry.
- `WardenEnemy` armored smash.
- `WizardEnemy` orb waves + dirt pillar (+`WizardOrbProjectile`).
- `PulserEnemy` surface crawler + terrain-occluded pulse.
- `ShrikeEnemy` patrol/hover/dive-detonate blueprint.
- `AspidEnemy` hover flier, fireball fans (+`AspidFireballProjectile`).
Enemies/Fighters/ (spec -> priced blueprint pipeline; tool/test code except the compiled output)
- `FighterSpec`, `FighterCompiler` (`Compile` :70, `Register` :148), `FighterCost(s)`/`FighterCosts` (cost model + `configs/fighter_costs.json`, loaded once), `FighterRoster` (6 stock specs, `RegisterAll` :190), `FighterBrains` (Closer/Kiter/HoverDive), `FighterSenses` (`FighterController` :306, `BrainRandom` :272), `FighterArena`/`FightRecord`/`FighterLeague`/`FighterCompetition`/`FighterRating`/`FighterForge`/`FighterSpecPrinter` (match runner, saved fights, league, ratings, search), `TimedController` (wall-clock wrapper, tools only), `Packages/*.cs` (34 `IFighterPackage` files found by reflection; `IFighterPackage.cs` holds `FighterPackages.Discover`).

  EnemyEntity.cs index:
    - fields/properties 19-152 (FSM lists, target memory, fighter attrs, tell props)
    - ctor 179
    - `OnHit` 200 + `TryBlock` 216
    - `TriggerStagger` 245
    - `Update` 268
    - `UpdateTargetMemory` 355
    - `SelectAction` 402
    - `SelectMovement` 469
    - `Telegraph` 506
    - `WriteState` 520 / `ReadState` 547.
  StormcallerEnemy.cs index:
    - Palm 49
    - Updraft 148
    - Lob 241
    - `StormBombProjectile` 328
    - Jet 473
    - `StormBeam` 548
    - `StormIceField` 741
    - `StormWind` clock/lanes 893
    - `StormcallerEnemy` 1046 (ctor 1090, `Update` 1152, `UpdateGale` 1166, `UpdateSchedule` 1218, `UpdateJet` 1267, `Telegraph` 1321)
    - figure drawing 1451.
  QuillbackEnemy.cs index:
    - controller 51
    - legs 212-270
    - `QuillbackAction` base 274
    - Flurry 319, Spin 412, Roll 475, Dig 551, Pillar 664, Storm 769
    - `QuillbackEnemy` 892 (`OnHit` 991, `Update` 1016, `Telegraph` 1028, `SyncSprite` 1046).
  WarlordEnemy.cs index:
    - controller 65
    - legs 222-282
    - `WarlordHits` 284
    - Cleave 313, Ram 446, Leap 553, Cage 758
    - `WarlordEnemy` 905
  ZeusEnemy.cs index:
    - `ZeusBeam` 76
    - controller 197
    - Bolt 265, Strike 408, Sweep 529
    - static `ZeusEnemy` 659 (`Blueprint` 661)
  FrogEnemy.cs index (648):
    - constants 47-117
    - ctor 131
    - `WriteState`/`ReadState` 159/173
    - `OnHit` 187
    - `Update` state switch 194
    - hop planning 314
    - tongue 382
    - `Divide` 459
    - `Burst` 484
    - `Telegraph` (all drawing) 514
  SpearmanEnemy.cs index (607): `Spear` 41, controller 164, Stab 228, Parry 393, body 505.

Flow, plants: `SeedPlanting.TryPlant` (from `BlockPlaceAction`, Character/Action/ActionStates.cs:3299, or a bot) -> `PlantRules.Evaluate` -> `new XPlant(site, owner, faction, team)` -> `spawner.SpawnEntity`. Each frame `PlantEntity.Update`: dead if the root tile is not `Solid`; `Dormant` if its own cell is `Solid` (drain, `OnSuppressed`); else grow to `GrowSeconds`; else the kind's `Act`. Hits: the owner's faction passes through the hurtbox; a `Faction.Hazard` hit kills any plant (`PlantEntity.OnHit`).

## Flow
1. Spawn: `Simulation.SpawnEntity` (Simulation.cs:462) assigns `Id`, adds `EntityRef`, `PhysicsBodyComponent`, `EntityData`, `BodyStateComp`. Stages call it from `Populate` (e.g. Stage.cs:1184, 1204) or `EnemyFactory.Create`; `EnemySpawner` uses `EntityFactory.CreateEnemy` (EnemySpawner.cs:111).
2. Each `Simulation.Step`: every entity's `PublishHurtboxes` (Simulation.cs:553; `EnemyEntity` skips it while `IsIntangible`, EnemyEntity.cs:141) -> players update -> entity `Update` in spawn order over a pre-collected list (Simulation.cs:569; entities spawned during the loop first update next step) -> `CombatSystem.Apply` (:571) calls each hit target's `OnHit` -> force fields -> `Entity.PreStep` for every entity (:608) -> `StepSwept` -> dead sweep (:658).
3. `EnemyEntity.Update` (EnemyEntity.cs:268): bump `_frame`; resolve target via `spawner.Targets.TryFindTarget` (Simulation.cs:224) else the passed player; `UpdateTargetMemory`; push `Targets` history; regen `Energy`; build `EnemyContext`; `_controller.Decide` -> `EnemyInput`; derive facing from `AimWorld` only when no action is running; then `SelectAction` (:339), `SelectMovement` (:340), movement `Update`, then action `Update` (runs last so an action's velocity writes win).
4. Selection rule (both FSMs): candidate's `PassivePriority` must beat the incumbent's `ActivePriority` (:455, :492); movement falls to index 0 when `CheckConditions` fails (:471-477); actions need `Input.WantAttack`, honor `RequestedAction`, and `Spec.EnergyCost <= Energy`, paid at Enter (:425-462).
5. Hitboxes: states/entities call `ctx.Hitboxes.Publish(new Hitbox(...))` during `Update`, HitIds from `ctx.Spawner.HitIds.Next()` (EnemyActions.cs:81 pattern: stored in `v.HitId` at Enter). They resolve the same step in `CombatSystem.Apply`.
6. Hit: `EnemyEntity.OnHit` (:200) guard check -> `Entity.OnHit` (Entity.cs:114: HP, knockback via `HitResolver`/`LaunchSystem`, hitstop, hit-feel stamp) -> `TriggerStagger` if a stagger state is registered (:245).
7. Render (never sim): `Game1.cs:1383` calls `ITelegraphSource.Telegraph(list)` for every entity that implements it; `EnemyEntity.Telegraph` (:506) emits movement then action shapes via each state's `Telegraph(TelegraphList, PhysicsBody, in vars)`. `CosmeticUpdateSystem` (Drawing/CosmeticUpdateSystem.cs:136-138) runs `Art.Sync(e)` then, only if `Sprite != null`, `SyncSprite()`. Bodies draw at Game1.cs:1258-1264: `Sprite.Draw`, else the body polygon outline in `Color` (Frog/Stormcaller/Pulser rely on that or on `Telegraph`).
8. Snapshot: `Simulation.Snapshot` calls `CaptureState` on each entity (Simulation.cs:791) = base fields + `WriteState(ref EntityData)` + `BodyState`. Restore (Simulation.cs:857-877) rebuilds the entity set from the `EntityData` store: live entity with that id -> `RestoreState` in place; absent -> `EntityFactory.Rehydrate(kind)` (ctor) then `RestoreState`.

## Invariants and traps
- Rollback state lives only in `EntityData` slots. An entity's own fields survive restore only if `WriteState`/`ReadState` marshal them (Entity.cs:259-260). `EnemyEntity` round-trips: movement index + `TimeInState` + `SavedGravityScale`, action index/time/`LockedFacing`/`LockedAim` (via `Aim`)/`HitId`/`Committed`, last-seen memory, `TargetId`, `Scratch`, `Energy`, `Targets`, `SenseMem`, `_frame` (EnemyEntity.cs:520-578). `ReadState` resets `_moveVars` and `_actionVars` to default first (:551, :556), so other vars fields are lost; durations are recomputed through `PopulateDurations` (:574-575): an action must implement it if it uses `WindupDuration` etc.
- Movement vars that must outlive a restore need their own `EntityData` slot: `EnemyMovementVars` lives only in the private `EnemyEntity._moveVars`, and `ReadState` resets it to default before restoring `TimeInState` and `SavedGravityScale` (`EntityData.SavedGravityScale`, added after this map's stamp). `EnemyClingMoveState.Enter` saves gravity there (EnemyMovementStates.cs:301) and `Exit` writes it back (:310); before the slot existed, a restore mid-cling left Pulser/Latcher/clinging fighters weightless. Any new field on `EnemyMovementVars` or `EnemyActionVars` has the same requirement.
- `EnemyEntity` subclasses (Brute, Warden, Wizard, Warlord, Prism, Quillback, Stormcaller, Spearman, Pulser) override neither `WriteState` nor `ReadState`; their extra memory is `Scratch` (`BrainScratch`: F0-F3, I0-I1, Sim/ECS/Components/EcsComponents.cs:42), `Energy`, or the frame clock. Their own instance fields are only render-side (`_pose`/`_poseFacing` on Quillback, Prism), call-scoped (`Quillback._shellHolding`) or scans rebuilt each Update before any sim read (`Stormcaller._lanes/_jet/_windDir`). Quillback's shell IS `Energy` (QuillbackEnemy.cs:925, 999); `StormcallerEnemy` schedules off `FrameCount` and `StormWind.At(frame)`.
- Plain-`Entity` subtypes marshal into reused `EntityData` fields (the slot names do not match the meaning): Frog uses `Aim`=launch, `Age`=split timer, `Lifetime`=rest length, `Senses.Rng`=rng (FrogEnemy.cs:159-171); `EnemySpawner` packs kind/cap/radius/interval/countdown/count into `AIState`/`Budget`/`MineRadius`/`MineFuse`/`StateTime`/`HitId` (EnemySpawner.cs:68-76); `Budget` means different things for LobbedArea, RailBolt, StormBomb scale, StormIce seed. Kinds sharing a slot are disjoint.
- State objects are built per entity (subclass ctors; `EnemyBlueprint.Movement/Actions` are factories, EnemyBlueprint.cs:103-104) but are never snapshotted: never store per-activation data on a state, only in vars. Controllers ARE shared (one per blueprint) and must not be written in `Decide` (EnemyController.cs:72-80; fighters get `ref self.Scratch`, FighterSenses.cs:306-316). Randomness must be seeded + snapshotted: Frog's `_rng`, `FighterSenses.Random`, or a pure hash of (frame, entity id) as Zeus does (ZeusEnemy.cs:60-66). `TimedController` reads the wall clock and is tools-only.
- `EnemyInput.WantAttack` defaults false (EnemyController.cs:54-56): a brain that never sets it yields an enemy that never attacks. `Decide` sees `ctx.Facing`/`ctx.Input` unset (EnemyEntity.cs:285-286).
- `TriggerStagger` and `TryBlock` run from `CombatSystem.Apply`, outside `Update`, with a context holding only `Self` + `Facing` (EnemyEntity.cs:247); states' `Enter/Exit` called from there must read nothing else. Stagger entry bypasses preconditions; `EnemyStaggerState.CheckPreConditions` is always false (EnemyMovementStates.cs:143). Enemies without a stagger state (Stormcaller, Warlord, Quillback, Prism, Warden, Pulser) take no stagger.
- Hurtboxes publish before entities update, so `IsIntangible`/`IsGuarding` read last frame's action (EnemyEntity.cs:132-137).
- `Rooted` must be set via the property: it also sets `Body.IgnoreTiles` (Entity.cs:42-49). `PreStep` zeroes velocity and cancels gravity every frame for rooted bodies; position is therefore not snapshot-critical. `GravityScale` is applied only in `PreStep` as a counter-force (Entity.cs:152-183).
- `Frog` is NOT an `EnemyEntity`: it is never an `ITargetSource` candidate (Simulation.cs:246 filters `EnemyEntity`), has no `BossTitle` bar (it uses `ISwarmBoss`, Drawing/BossBarHud.cs:53), and is drawn only through `Telegraph` (Color transparent, no Sprite, FrogEnemy.cs:138, 514). Same plain-`Entity` status: Stalker, Turret, EnemySpawner.
- `Checksum` (Simulation.cs:667-772) covers, per entity, only the entity count, `Id.Index`, body position/velocity and `Health` (:687-696), besides players, id counters and terrain: divergence in AI-only state (FSM indices, vars, `Scratch`, `Energy`, `GravityScale`) is caught only once it moves a body or HP.
- Dispatch order: `CreateEnemy`/`Rehydrate` try the sim's local `Blueprints` table, then `SubclassEnemy`, then (`Rehydrate` only) its explicit projectile/helper kind switch, then the process-wide `EnemyFactory` registry (EntityFactory.cs:64-67, 105-146). `CreateEnemy` throws for any kind none of them resolve (:68). `Rehydrate` throws for an unregistered fighter kind; any other unknown kind becomes a Generic `Entity` (:147), silently losing its behavior on restore.
- `EntityKind` order: fighter kinds `Brick..FighterSlot47` must stay contiguous (EntityKind.cs:107, 166); add new kinds after `FrogMutant`, never inside that range.
- `EnemyBlueprint`'s own header ("two edits") describes the blueprint path only; hand-written subclasses also need the `SubclassEnemy` line (EntityFactory.cs:74-90). A subclass that is also in `RegisterBuiltIns` would be shadowed by `SubclassEnemy`.
- Projectile `Update` is sealed (Projectile.cs:34): per-frame logic goes in `ProjectileUpdate`; `Age` is snapshotted by the base `WriteState`. Constructor-only fields of a projectile (e.g. `StormBombProjectile._hitId/_target/_scale`) must be recoverable from `EntityData` in `Rehydrate` (EntityFactory.cs:128).
- `EntityArt` is keyed on `e.Kind.ToString()` (Drawing/EntityArt.cs:220); `BlobShadowRenderer.CastsShadow` (Drawing/BlobShadowRenderer.cs:108) returns false for Bullet, EnergyBall, RailBolt, AspidFireball, WizardOrb, StormBeam, StormIce, ChargedBlast, RayEmitter, PullPoint, GrabThrow, true otherwise.
- `EntityKind.cs:107` says the fighter block must stay "LAST"; it is not (FighterMine..FrogMutant follow). Only contiguity matters (`IsFighter` range).
- Stale `TemplateEnemy.cs` header: rules 4-5 (:80-87, "nowhere to snapshot brain state", "no randomness") predate `Scratch` and seeded `SenseMem.Rng`; the stock-pool list (:104-108) names states not in the pool (leap, shockwave, spin, pillar, block trail).
- Entities see `IEntitySpawner` queries as read-only sampling; defaults return null/empty for stubs (Entity.cs:280-321), so headless tests may get null `Chunks`/`Hurtboxes`/`Targets`: guard them (FrogEnemy and Stormcaller do).
- Plants: a plant's hurtbox is published from `PlantEntity.PublishHurtboxes` (skipped when `Spent`), but hit occlusion never counts the target's own cell, so a BURIED plant can still be hit through the block over it (plan "Decisions needed"; unfixed).
- Plants: the siege pod fires the first awake frame after ripening, so it lives its whole life on `SeedlingHealth` (0.5): one poke kills it. A fired pod is `Spent`: no hurtbox, not counted by any cap, removed after `SiegePodFireLinger`. Burial freezes growth, so a buried pod never ripens.
- Plants: the sprite is rotated by the base (`PlantRules.RotationOf(Root)`), so a figure is built UPRIGHT in the cell frame with the root face at y = `PlantFigureKit.RootY`, never for wall/ceiling cases.
- Plants: `EntityKind.PlantThorns` / `PlantBlockEater` exist but nothing creates or rehydrates them (EntityFactory.cs:147,149 are commented out).

## Where to change X
- Tune an attack's numbers: `ActionSpec.Default` (ActionSpec.cs:100) for pool actions; the constants atop the class for bespoke ones (e.g. `StormcallerPalmAction`).
- New brain behavior: subclass `EnemyController` (stateless; memory in `Scratch`), or `FighterController` for a fighter.
- **Add a new enemy (state-machine style, as Stormcaller)**: (1) add `EntityKind.X` after `FrogMutant` (EntityKind.cs); (2) write brain + movement/action states + `sealed class XEnemy : EnemyEntity` in `Enemies/Types/XEnemy.cs`: actions set `Committed`/`HitId` in `Enter`, implement `PopulateDurations`, override `Telegraph(TelegraphList, PhysicsBody, in EnemyActionVars)`; give `Kind`, optionally `BossTitle`; memory in `Scratch`/frame count, or override `WriteState`/`ReadState`; (3) add `EntityKind.X => new XEnemy(pos)` to `SubclassEnemy` (EntityFactory.cs:74); (4) ordnance entities: new kind + class + a `Rehydrate` case (EntityFactory.cs:102); (5) visuals: set `Sprite` (or leave null for the `Color` outline) and draw extras in `Telegraph`; nothing in `Drawing/` needs registering (optional `EntityArt` catalog entry by kind name); (6) spawn from a `Stage` `Populate` (Stage.cs:150-160, 1180-1196) and register the stage; (7) a test in `MTile.Tests/Sim/XEnemyTests.cs` (name must match a `test-group.py` term, add one if not; copy StormcallerEnemyTests/TemplateEnemyTests) including a snapshot round-trip.
- **Add a blueprint-only enemy (no subclass)**: compose stock states in an `EnemyBlueprint` with its own `Kind`, expose `static Blueprint` (see `ShrikeEnemy.cs:403`, `TemplateEnemy.cs:377`) and add `Register(...)` in `RegisterBuiltIns` (EnemyBlueprint.cs:187). No factory edit needed. Its look is the blueprint's `Sprite` (radius -> sprite factory, e.g. `Sprites.Latcher`) and `Color`.
- **Add a plain-`Entity` swarm/AI entity (as Frog)**: same kind + `SubclassEnemy` line; own state enum; `WriteState`/`ReadState` for every mutable field; implement `ITelegraphSource` and draw in `Telegraph`; publish hitboxes in `Update`; spawn children via `spawner.SpawnEntity`; add `ISwarmBoss` for a shared boss bar.
- **Add a non-enemy entity kind (projectile/block entity)**: subclass `Projectile` (or `Entity`), `Kind` override, `WriteState`/`ReadState`, a `Rehydrate` case that passes construction inputs from `EntityData`; `BlobShadowRenderer` if it should not cast a shadow.
- New pool action kind: `ActionKind` + `ActionSpec.Default` row + action class under `Enemies/`; fighters also need `FighterCompiler`/`FighterCost` pricing.
- New fighter: package file under `Fighters/Packages/` (`IFighterPackage`, no registration) or a spec in `FighterRoster.All`.
- Boss HUD bar / label: override `BossTitle`/`BossShieldFraction`/`DisplayName` (EnemyEntity.cs:125-130).
- **Add a plant**: subclass `PlantEntity` per its header (ctor, `Seed`, `GrowSeconds`, `Kind`, `Act`, `OnSuppressed`, `ActivePose`, `WritePlant`/`ReadPlant`, `FigureFor`, `FromState`), add the `SeedKind` + `EntityKind` + `EntityFactory.Rehydrate` line + a `TryPlant` case + `IsImplemented`, a cost in `PlantTuning`, a figure in `Drawing/Plants/`, and extend `SnapshotRoundTripTests`. Tuning: `PlantTuning.cs`.

## Tests
Under `MTile.Tests/Sim/` (group `combat` unless noted; terms in `scripts/test-group.py`): `FrogEnemyTests`, `StormcallerEnemyTests`, `QuillbackEnemyTests`, `WarlordEnemyTests`, `ZeusHillTests`, `ZeusBlindFireTests`, `PrismEnemyTests`, `SpearmanEnemyTests`, `WardenEnemyTests`, `WizardEnemyTests`, `PulserEnemyTests`, `ShrikeTests`, `BirdTests`, `AspidEnemyTests`, `TemplateEnemyTests`, `GauntletEnemyTests`, `EnemySpawnerTests` (also covers `CreateEnemy`), all 20 `Fighter*Tests` (ActionSpec/ArenaRules/Arena/BlockAction/Combat/Competition/Cost/DashMine/Determinism/Forge/GauntletStage/GuardShove/LeagueSample/League/Package/Perception/Random/Rating/Record/RollbackKill). `ChargedBlastTests` is in `combat` and `terrain`.
`simcore` group: `SnapshotRoundTripTests`, `RollbackHarnessTests`, `PracticeBallTests`, `GauntletStageTests`, `HoverTargetStageTests`, `DepthsStageTests`.
`terrain` group: `ChargedBlastTests`, `PullPointSnapshotTests` (also `simcore`), `Block*Tests`.
Run `scripts/test-group.py combat` for most changes here; add `simcore` when touching `Entity`/`EntityFactory`/`EntityData` marshalling. Terms are substrings of the fully-qualified name.
Stalker/Turret/Brute/Bullet have no dedicated class: Stalker+Turret ride in `SnapshotRoundTripTests` (:82-83) and `CoopModeTests`; Turret/Brute (and `CreateEnemy(Bullet)` throwing) in `EnemySpawnerTests`. A restore mid-cling is covered by `GauntletEnemyTests.Latcher_RestoredMidCling_GetsItsGravityBackWhenItLetsGo`.
Plants (groups `combat`/`terrain`/`simcore` via the `Sim.Plant` / `Tests.Plant` terms): `PlantRulesTests`, `PlantLifecycleTests`, `PlantSeedBarTests`, `PlantSpitterTests`, `PlantSnapperTests`, `PlantSiegePodTests`, `PlantSiegeBlastTests`, `PlantThornTests`, `PlantBlockEaterTests` (in `MTile.Tests/` root), harness `PlantTestArena.cs`. `ZzzSnapperSheet` is a scratch harness.
