# MTile — Codebase Overview

A 2D platformer in C#/MonoGame about "the terrain IS the weapon" — the player slashes, stabs, pulses, fires, and erupts blocks to shape the battlefield while moving through chunked tile terrain. Project root: `c:\Users\amdic\dev\MTile`.

The game logic lives at the repo root and is compiled three ways (see `CLAUDE.md` for the project layout and build commands): `MTile.Core` (the library), `MTile.Desktop` (DesktopGL host), `MTile.Web` (KNI/Blazor WASM host), and `MTile.Tests` (xUnit). Fixed timestep of 30 fps (`Simulation.FixedDt = 1/30`).

> **Architecture in one sentence:** [`Simulation`](Simulation.cs) is the deterministic game world that advances on inputs alone via `Step(PlayerInput)`; [`Game1`](Game1.cs) is a thin render/input shell around it; everything is being driven toward snapshot/restore for rollback netcode (see `Plans/ROLLBACK_ROADMAP.md`).

## Top-level architecture

```
              ┌──────────────────────────────────────────┐
              │  Game1  (render + hardware-input shell)   │
              │  gather PlayerInput → Step → render        │
              │  particles · cursor trail · camera · sprites (cosmetic-only)
              └───────────────────┬──────────────────────┘
                                  │ Step(PlayerInput)
              ┌───────────────────▼──────────────────────┐
              │  Simulation  (deterministic world)        │
              │  players · entities · chunks · combat ·   │
              │  platforms · combat dedupe · id counters  │
              └──┬──────────┬───────────┬──────────┬──────┘
                 ▼          ▼           ▼          ▼
          Player FSMs   Entity AI   Physics     Combat
          (movement +   + projec-   (StepSwept  (hitbox ↔ hurtbox
           action)      tiles       resolves    SAT, tile damage)
                                    bodies)
```

**The sim/render split is the load-bearing invariant.** `Simulation.Step` is the only thing that mutates game state, runs on a fixed `dt`, and reads input solely from the `PlayerInput` it's handed. `Game1`'s cosmetic systems (particles, `_cursorTrail`, sprite animation, `Camera`) read sim state but **must never write back into it** — they're downstream of `Step`. This is what makes the sim deterministically replayable.

The data lattice tying the subsystems together:

| Channel | Producer | Consumer |
|---|---|---|
| `EnvironmentContext` | `PlayerCharacter.Update` builds it once/frame | Both FSMs + checkers query it |
| `MovementModifiers` | Current `ActionState.ApplyMovementModifiers` | `MovementState` config reads (WalkAccel, GravityScale, …) |
| `PhysicsBody.AppliedForce` | `MovementState.Update` writes; `ActionState.ApplyActionForces` augments | `PhysicsWorld.StepSwept` integrates |
| `HitboxWorld` (offensive) | Action states + entity AI publish during frame | `CombatSystem.Apply` reads at end of frame |
| `HurtboxWorld` (defensive) | `IHittable.PublishHurtboxes` at frame start | `CombatSystem.Apply` reads |
| `IntentBuffer` (gestures) | `InputParser.Detect` (Click/Stab/Circle/PressEdge) | Action preconditions Peek + Consume |
| `ConditionState` (offensive flags) | Action `Enter`/`Exit` set Slash2Ready/RecoveryActive/etc | Action preconditions; `Tick` expires by frame |
| `CombatState` (defensive flags) | `PlayerCharacter.OnHit` + crush check set Hitstun/Stun/Guard | Jump/attack preconditions |
| `HitIdAllocator` | One per `Simulation`, threaded via `EnvironmentContext.HitIds` / `IEntitySpawner.HitIds` | All hitbox-publishing code mints `HitId`s |
| `OnTileBroken` / `OnPlayerRespawn` events | `ChunkMap.BreakCell` / `Simulation.Step` | `Game1` spawns cosmetic particles |

**Coupling rule**: Actions may read movement state; movement code MUST NOT read action state. `MovementModifiers` and `AppliedForce` are the only channels in that direction.

## Simulation & determinism ([Simulation.cs](Simulation.cs))

`Simulation` owns every piece of state the world reads or writes and advances it one fixed step via `Step(PlayerInput)`. Two constructors: a headless one (terrain supplied directly + a `populate` delegate — used by tests) and the real one (`GameConfig` + `Stage`). It implements `IEntitySpawner` (AI/projectiles spawn children) and `IChunkProvider` (entities mutate terrain).

**`Step` phase order** (mirrored exactly by the headless `SimRunner` so tests match real play):
1. Inject `input`; advance the absolute sim clock `_elapsed += FixedDt`.
2. `HandleBuildInput` — RMB drag-to-build sprouts (reach-gated from the player; chains off existing sprouts).
3. Tick dynamic surfaces (`MovingRectangle` platforms) as a **pure function of `_elapsed`** — no hidden accumulator, so they snapshot cleanly.
4. `ChunkMap.TickSprouts(dt)` + `ChunkMap.Impact.Tick(dt)` (decay per-cell impact accumulator).
5. Combat frame: clear hit/hurtbox registries → every `IHittable.PublishHurtboxes` → `_player.Update` (publishes hitboxes) → secondary players → entity AI `Update` → `CombatSystem.Apply`.
6. Player respawn on death (deterministic, inside `Step`; fires `OnPlayerRespawn`).
7. `Entity.PreStep` (gravity-scale opt-out) → `PhysicsWorld.StepSwept` → sweep up dead entities.

### Snapshot / restore (rollback core — roadmap goals 4 & 6)

`Snapshot()` returns a [`SimSnapshot`](SimSnapshot.cs): plain-data value structs and id-keyed maps, no live references, so it can outlive any number of `Step`s and be restored repeatedly (rollback re-restores the same frame).

- **Players** → [`PlayerSnapshot`](Character/PlayerSnapshot.cs): FSM selection as **registry indices** (states/actions are flyweights constructed in fixed order), per-activation data as the [`MovementVars`](Character/MovementVars.cs)/[`ActionVars`](Character/ActionVars.cs) value structs, helper objects deep-cloned (`PlayerAbilityState.Clone`, parser/intents capture, eruption gesture deep-copy).
- **Bodies** → [`BodyState`](Physics/BodyState.cs): pose + kinematics + only the **Maintained** (hard) constraints, deep-cloned. Soft state-owned contacts (`FloatingSurfaceDistance`, `SteeringRamp`, `PointForceContact`) are NOT captured — the owning state's idempotent `Ensure…`/`ResetTransient` rebuilds them next frame.
- **Entities** → [`EntitySnapshot`](Entities/EntitySnapshot.cs): one superset struct unioned across all entity types, tagged with `EntityKind` so a despawned entity can be `Rehydrate`d (reconstructed); a still-live entity is restored in place by id.
- **Combat dedupe** → captured by `HittableId`, resolved back to live objects on restore.
- **Terrain** → [`TerrainSnapshot`](World/TerrainSnapshot.cs): the dense tile grid is too large to copy, so it's rolled back via an inverse-delta journal ([`TerrainJournal`](World/TerrainJournal.cs)) — a snapshot stores `Mark`, restore replays entries past it in reverse. Sparse side-structures (sprout graph, per-cell HP, foam timers, impact accumulator) tick every frame so they're value-snapshotted instead. **Caveat:** journal marks are instance-relative — terrain restore is same-instance only (the rollback case); player/entity snapshots are fully portable.

`Restore(snap)` reverses all of the above and rebuilds the `_bodies`/`_hittables` lists in canonical order (primary, secondaries, entities in spawn order) so iteration order — and therefore stepping — is identical.

### Determinism rules (when touching sim code)
- **No sim-affecting mutable statics.** `HitId`s come from the per-`Simulation` [`HitIdAllocator`](World/HitIdAllocator.cs); block-type/planner-mode are per-`PlayerCharacter` (driven by that player's own input), not globals. The lone surviving static, `EruptionPlanner.DebugDrawMassBall`, is render-only.
- **All input arrives via `PlayerInput`** — no polling hardware mid-step. Block-picker (1-4) and planner toggle (P) are interpreted inside `PlayerCharacter.Update`.
- **Same iteration order on restore.** Lists are rebuilt deterministically.
- `MovementConfig` hot-reload is gated behind `GameConfig.HotReloadMovementConfig` (off for MP).

Topology target is **same-build P2P** (desktop↔desktop or WASM↔WASM), so `float`/`MathF` determinism is a non-issue (same binary). See `Plans/ROLLBACK_ROADMAP.md`, `Plans/STATE_SNAPSHOT_PLAN.md`, `Plans/GGPO_PLAN.md`.

## Stages ([Stage.cs](Stage.cs))

A `Stage` bundles "what to load at start": `TerrainConfig` (filename in `Levels/`), `PlayerSpawn`, and a `Populate(Simulation)` delegate that spawns entities + registers platform tickers. `Stages` is a code registry (stages contain behavior, not just data); `game_config.json`'s `Stage` field selects one by name. Two stages today: `start` (the original test world — moving platform, ferris-wheel cluster, balloons/balls, one stalker) and `arena` (bounded combat room — stalkers, turrets, ammo balls).

## Physics ([Physics/](Physics/))

### `PhysicsBody`
Pure kinematic data: `Position, Velocity, AppliedForce, Polygon, Constraints, Impact, FrictionScale, LastImpulseMagnitude`. `AppliedForce` is treated as direct acceleration (no mass term inside the integrator). `Constraints` holds `PhysicsContact`s. `Impact` is nullable — when non-null, the body damages tiles it crashes into ([Physics/ImpactDamage.cs](Physics/ImpactDamage.cs)). `LastImpulseMagnitude` records the largest `|vnRel|` absorbed last step (read by player crush-damage). `FrictionScale` is captured/restored with the body.

### `PhysicsWorld` ([Physics/PhysicsWorld.cs](Physics/PhysicsWorld.cs))
Two integrators:
- **`Step`** — discrete: move by `velocity * dt`, then iterate up to 8 times pushing the body out of overlapping shapes via MTV.
- **`StepSwept`** — used by the main loop. Sweeps the body's displacement against all shapes via swept-SAT, picks earliest `T`, advances `(1-T)` along the residual displacement, loops up to 4 bounces. Plus a discrete pre-pass for any sprout that flipped solid mid-overlap.

At every impulse site it computes `vnRel = (body.V - shape.V) · normal` and applies `body.V -= vnRel · normal` to zero relative normal velocity. The swept and discrete sites also call `TryApplyImpactDamage` — probes a 1px slab along the impact face via `WorldQuery.SolidShapesInRect`, splits `(impulse − threshold) · scale` damage among the pressed tiles. Friction (`SurfaceContact.Friction`) is Coulomb-ish: caps per-step tangential velocity change at `friction · dt`, gated off when the state pushed along the same tangent.

### `PhysicsContact` hierarchy
- `SurfaceContact` (abstract): position, normal, minDistance, surface velocity, friction. The `Maintained` flag marks hard contacts that survive a snapshot.
- `SurfaceDistance` — hard, stamped by collision resolution to prevent re-penetration (`Maintained == true`). Pruned next frame if the source surface is gone.
- `FloatingSurfaceDistance` — soft, owned by movement states (Standing's `_ground`, ledge states' `_wall`/`_floor`). Spring force toward the floor; soft (not maintained), so it's rebuilt after a restore.
- `PointForceContact`, `SteeringRamp` — soft, also rebuilt after restore.

### `SolidShapeRef` ([World/SolidShapeProvider.cs](World/SolidShapeProvider.cs))
Provider-agnostic shape view: AABB + position + velocity + polygon. `ChunkMap` is the first `ISolidShapeProvider`; moving platforms register additional providers. `WorldQuery.SolidShapesInRect` fans out across all of them.

### `SteeringRamp`
Rotates body velocity onto the shallowest trajectory clearing an upper/lower corner. Reads `ExposedCorner` from checkers; redirects horizontal motion into a slight vertical component when sweeping toward an unblocked corner. Now the primary mechanism behind the parkour/vault states (it replaced the old Hermite-path PD controller).

## World / Tiles ([World/](World/))

### `ChunkMap` ([World/ChunkMap.cs](World/ChunkMap.cs))
Dictionary of `Point → Chunk`. Each chunk = 16×16 tiles; each tile = 16px (`Chunk.TileSize`). API surface:
- **Cell state** — `GetCellState` returns `Empty | Sprouting | Solid`; `GetCellType` returns `Stone | Dirt | Sand | Foam`.
- **Sprouts** — `TryRequestTile(gtx, gty, type)` builds a Growing or Pending `TileSproutNode` in `Graph`.
- **Damage** — `DamageCell` → `TileDamage.ApplyDamage` accumulates per-cell HP; on threshold the cell breaks. `BreakCell` flips to Empty and fires `OnTileBroken(center, type)`.
- **Mutation funnels** — `WriteTile` and `GetOrCreateChunk` are the *only* paths that mutate the dense grid, and both record to the `TerrainJournal`. Everything (break, damage→break, sprout finalize/promote, foam decay) goes through them, so "nothing mutates a chunk outside the journaled path."
- **TickSprouts(dt)** — ages Growing sprouts; finalizes complete ones; promotes Pending children; expires Foam timers via `FoamDecay`.
- **Snapshot** — `CaptureTerrain`/`RestoreTerrain` (journal mark + value-copied sparse structures).

### Tile types & decay
`TileType` (`World/Tile.cs`): `Stone`, `Dirt`, `Sand`, and **`Foam`** — a cheap throwaway material (half Dirt's HP) that decays back to Empty after `FoamDecay.DefaultLifetime` (4s). Foam is player-selectable (block picker key 4) but never produced by terrain gen; useful as temporary scaffolding/cover. [`FoamDecay`](World/FoamDecay.cs) is a sparse per-cell timer map off the normal damage path.

### Side-structures
- [`TileDamage`](World/TileDamage.cs) — per-cell HP accumulator; `MaxHPFor(type)`.
- [`TileImpactAccumulator`](World/TileImpactAccumulator.cs) (`ChunkMap.Impact`) — per-cell impact buildup that bleeds off via `Tick(dt)`.
- [`TileSproutGraph`](World/TileSproutGraph.cs) — DAG of pending/growing sprouts; first parent to finalize promotes a child to Growing and decides its growth direction. Used by drag-build and the eruption planner.

### `TileQuery` / `WorldQuery`
`TileQuery` walks `ChunkMap`'s tile storage directly (cell-aligned, integer-column scans for surface checkers). `WorldQuery` fans out across all `ISolidShapeProvider`s — used by the physics sweep and any code that needs tiles + sprouts + moving rects uniformly.

### `TerrainLoader`
Reads `Levels/*.json` (chunk-position → ASCII filename map + Perlin config). ASCII files use `X` for solid (Stone), anything else empty. Procedural chunks use 1D Perlin height + depth-layered types (Sand crust → Dirt mid → Stone deep).

## Character ([Character/](Character/))

### `PlayerCharacter` ([Character/PlayerCharacter.cs](Character/PlayerCharacter.cs))
Owns two parallel FSMs (movement + action), the ability state, intent buffer, and input parser. Combat stats: `Health`, `Mass` (divides incoming knockback), a post-hit invuln window, and **crush damage** (turns last step's `LastImpulseMagnitude` above `CrushImpulseThreshold` into HP loss + hitstun — a hard fall/wall-slam hurts). `OnHit` runs the Guard parry first (`CombatState.TryParry`), then applies damage/knockback/invuln/hitstun.

Each `Update`:
1. `_frame++`; tick invuln; apply crush damage.
2. Interpret this player's own block-picker (1-4) + planner-toggle (P) input → `_activeBlockType` / `_eruptionMode`.
3. Tick `ConditionState` (combo windows) + `CombatState` (hitstun/stun) flags.
4. `InputParser.Detect` enqueues gesture intents.
5. Build a fresh `EnvironmentContext` (input + buffers + chunks + spawner + HitIds + condition/combat + frame + dt + `Modifiers = Identity`).
6. Movement FSM: if current state's `CheckConditions` fails → exit + fall back to Falling. Then scan registry for higher-passive-priority candidates passing `CheckPreConditions`; transition if one beats current's `ActivePriority`.
7. **Action FSM selection runs *before* `MovementState.Update`** so the newly-selected action's `ApplyMovementModifiers` is in effect when movement reads physics knobs.
8. `MovementState.Update` writes `Body.AppliedForce`; `ActionState.ApplyActionForces` augments it; gravity-scale modifier applied as counter-force.
9. `ActionState.Update` does its FSM work (publishing hitboxes, advancing timers).

**Per-activation FSM state is plain data.** The FSM state/action instances are flyweights (one per registry entry, shared across activations); all mutable per-activation fields live in the [`MovementVars`](Character/MovementVars.cs)/[`ActionVars`](Character/ActionVars.cs) value structs on the player, passed `ref` into lifecycle methods (`Enter`/`Update`/`Exit`/`CheckConditions`) and `in` into the read-only hooks. This is the snapshot unit — a struct copy. (A few genuinely reference-typed buffers, like `BlockEruptionAction`'s gesture `SmoothPen`+`List`, are deep-copied separately.) `PlayerAbilityState` holds the rest: `Facing`, `HasDoubleJumped`, ledge-grab flags, and the nested `Condition`/`Combat` states.

### Movement FSM ([Character/Movement.cs](Character/Movement.cs), [MovementStates.cs](Character/MovementStates.cs))

**Free states**: `FallingState` (0/0, fallback), `StandingState` (10/10, spring-held to ground), `CrouchedState` (15/15), `WallSlidingState(dir)`.
**Stun**: `StunnedState` (25/25) — heavy-hit lockout; muted air control while `Combat.StunActive`. Preempts free/wall states but not active jumps.
**Launch states**: `JumpingState`, `RunningJumpState`, `DoubleJumpingState`, `WallJumpingState(dir)`, `CoveredJumpState` (under low overhangs) — set vY once, hold while button held (priorities 50–60).
**Guided traversals** (`MovementPriorities.GuidedActive/Passive`, band 25–45): `ParkourState(dir)` (vault/duck via `SteeringRamp`), `LedgeGrabState(dir)` + `LedgePullState(dir)` (wall corners via `FloatingSurfaceDistance` + ability flags), `DropdownState` (Down+platform-drop). These are now ordinary `MovementState`s — there is **no** shared `GuidedState` base or Hermite `GuidedPath` controller anymore (`GuidedState.cs` was removed; `GuidedPath.cs` is vestigial).

`MovementState` lifecycle methods take `ref MovementVars`; `ResetTransient()` nulls any soft-contact ref cache after a restore so the idempotent `Ensure…` rebuilds it next frame.

### Action FSM ([Character/ActionStates.cs](Character/ActionStates.cs))

Same FSM shape, separate registry/history, lifecycle takes `ref ActionVars`. Each action overrides `ApplyMovementModifiers(ref MovementModifiers, in ActionVars)` (declarative multiplicative scalars) and `ApplyActionForces(ctx, in ActionVars)` (direct `AppliedForce` writes). Registered actions (Active/Passive priority):

- `NullAction` (0/0) fallback; `ReadyAction` (10/15) LMB wind-up; `RecoveryAction` (40/45) post-attack lockout gating combos.
- **Slashes** — `SlashLikeAction` base parametrizes arc shape + damage window. `GroundSlash1/2/3` (combo via `Slash2Ready`/`Slash3Ready`), `CrouchSlash` (crouch-only), `AirSlash1/2`, `AirTurnSlash` (air backward-click turnaround), `GuardRetaliateAction` (counter after a charged parry).
- `StabAction` (30/30) — long thrust with air-stab dive boost; `AirSpinStab` (air backward-swipe variant).
- `PulseAction` (30/30) — Circle gesture; 12-segment expanding knockback ring that carries the caster's momentum.
- **Guard** — `GuardAction` (35/40, Shift held + no L/R) sets `GuardActive`; a weak in-cone hit parries to zero and arms `GuardCharged`, enabling `GuardRetaliateAction`.
- **Ranged** — `EnergyBallAction` (Shift+LMB tap), `BeamAction` (Shift+LMB hold → sustained beam after charge), `GrenadeAction` (F → sticky grenade), `LobbedAreaAction` (Shift+RMB charge → ranged eruption on landing). These spawn projectile entities via `ctx.Spawner`.
- **Block Eruption** (two-phase) — `BlockReadyAction` (8/10, RMB held with cursor IN solid, accumulates charge) → `BlockEruptionAction` (9/10, armed when cursor exits solid, samples a `SmoothPen`-smoothed path, runs `EruptionPlanner.Plan` on release).

### Combat condition state
[`ConditionState`](Character/ConditionState.cs) — *offensive* combo/recovery/guard-window flags, each with an expire frame; `Tick` closes windows. Also carries the block-eruption hand-off (`BlockEruptionArmed`/`BlockChargeTime`/`BlockChargeOrigin`).
[`CombatState`](Character/CombatState.cs) — *defensive*: `Hitstun` (every hit briefly locks Jump, with diminishing extensions so stun-locks can't grow unbounded), `Stun` (heavy hits, gates attacks too), and Guard (`GuardActive`/`GuardCharged` + `TryParry`). Exposes `BlocksJump`/`BlocksAttack` gates.

### Block Eruption planners
[`EruptionPlanner`](Character/EruptionPlanner.cs) dispatches between two implementations (per-player `EruptionMode`, toggled by P; default MassBall):
- **PriorityField** — scores empty cells by front-loaded weight × radial falloff with area-conserving radius; picks top-K.
- **MassBall** ([Character/MassBallPlanner.cs](Character/MassBallPlanner.cs)) — simulates a spring-pulled ball tracing the recorded path; mass leaks into field cells, threshold-crossing sprouts, excess spills to neighbors recursively.

### Input parsing
- [`Controller`](Character/Controller.cs) — 32-frame ring buffer of `PlayerInput` (Left/Right/Up/Down/Space/Shift/F/Num1-4/P/LeftClick/RightClick/MousePosition/MouseWorldPosition). `Poll(mouseWorldPos)` builds one from hardware; `InjectInput` feeds a supplied one (sim/tests). `Capture`/`Restore` for snapshots.
- [`InputParser`](Character/InputParser.cs) — edge-triggered gesture detection: `Click`, `Stab`, `Circle`, `PressEdge`. Snapshot-able (`InputParserState`).
- [`IntentBuffer`](Character/IntentBuffer.cs) — short queue of `ActionIntent`; Peek + explicit Consume, pruned by age.
- [`InputIntent`](Character/InputIntent.cs) — lightweight per-frame intent struct (HeldHorizontal, JumpJustPressed, …) for movement-side use.
- [`SmoothPen`](Character/SmoothPen.cs) — spring-pulled cursor smoother for block-eruption path sampling.

### Surface checkers
[`GroundChecker`](Character/GroundChecker.cs), `CeilingChecker`, `WallChecker`, `ExposedUpperCornerChecker`, `ExposedLowerCornerChecker` — build strip regions via `body.Bounds.StripXxx(thickness)`, call `WorldQuery.SolidShapesInRect`. `EnvironmentContext` caches results within a frame.

### Config
[`MovementConfig`](Character/MovementConfig.cs) hot-reloaded from `movement_config.json` (desktop only, gated by `GameConfig.HotReloadMovementConfig`). Walk/jump speeds, accelerations, frictions, spring constants, sprout lifetime.

## Combat ([World/HitboxWorld.cs](World/HitboxWorld.cs), [HurtboxWorld.cs](World/HurtboxWorld.cs), [CombatSystem.cs](World/CombatSystem.cs), [Hitbox.cs](World/Hitbox.cs), [Hurtbox.cs](World/Hurtbox.cs))

Hitbox-vs-hurtbox model. Per frame: both registries cleared → `IHittable.PublishHurtboxes` populates defensive boxes → action FSM + entity AI publish offensive hitboxes → `CombatSystem.Apply` walks every hitbox × hurtbox; on AABB overlap + faction mismatch + (optional polygon-vs-AABB SAT refinement), dispatches `IHittable.OnHit`. Deduped per `(HitId, Target)` across the broadcast window so a multi-frame slash hits an entity once. The same hitbox also damages tiles via `chunks.DamageCell` — cumulatively, no dedup, so a multi-frame slash progressively chips a tile.

`CombatSystem` is **instance-owned by `Simulation`** (the dedupe table is cross-frame sim state); `CaptureDedupe`/`RestoreDedupe` snapshot it by `HittableId`. `HitTargets.{All, TilesOnly, EntitiesOnly}` filters dispatch.

## Entities ([Entities/](Entities/))

[`Entity`](Entities/Entity.cs) — `IHittable` non-player wrapper around a `PhysicsBody`. Fields: `Health, MaxHealth, Mass, GravityScale, Color, Faction, Sprite, Id`. `PreStep(gravity)` cancels/amplifies gravity by `(GravityScale - 1)`. `OnHit` applies damage + knockback `impulse / Mass`. `Update(dt, player, hitboxes, spawner)` is the AI hook (no-op for passive props). Snapshot via `Capture`/`RestoreInto` + virtual `WriteState`/`ReadState`; `Kind` (`EntityKind`) tags the concrete type for `EntitySnapshot.Rehydrate`.

`IEntitySpawner` (implemented by `Simulation`) lets AI spawn children mid-update and shares the `HitIdAllocator`.

- [`EntityFactory`](Entities/EntityFactory.cs) — `Balloon` (floating passive target), `Ball` (gravity "crasher" that chips terrain on hard impact), `FloatingBall` (weightless crasher / combat ammo), plus `Stalker`/`Turret`.
- [`StalkerEnemy`](Entities/StalkerEnemy.cs) — ground chaser: Chase → Telegraph (visible wind-up) → Lunge (forward hitbox) → Recover, with a Stagger state on hit so knockback isn't clobbered by the AI.
- [`TurretEnemy`](Entities/TurretEnemy.cs) — stationary: Idle → Charging (aim locks, dodgeable line of fire) → fires a `BulletProjectile` at the player's current position → Cooldown; Stagger on hit.
- [`Projectile`](Entities/Projectile.cs) base + concrete `BulletProjectile`, `EnergyBallProjectile`, `StickyGrenadeProjectile`, `LobbedAreaProjectile` — travel + publish hitboxes; lifetime/fuse handled by the base. `LobbedAreaProjectile` captures eruption mode + block type at launch and runs a planner on detonation.

## Drawing ([Drawing/](Drawing/))

[`DrawContext`](Drawing/DrawContext.cs) wraps `SpriteBatch` + 1×1 pixel, exposes `Line/Rect/Ring/Disc/RotatedRect`. [`Sprite`](Drawing/Sprite.cs) is a `Pose`-based vector sprite; `AnimatedSprite` adds frame timing. [`ParticleSystem`](Drawing/ParticleSystem.cs) is a fixed-capacity pool (2048 in Game1). [`Trail`](Drawing/Trail.cs) is a fading ribbon (cursor trail + slash tip trails). [`Effects`](Drawing/Effects.cs) — preset spawners (`TileBreak`, `Puff`). **All drawing is cosmetic and downstream of the sim.**

## Render shell ([Game1.cs](Game1.cs))

`Initialize`: load `GameConfig`, resolve the `Stage`, load `MovementConfig` (+ desktop hot-reload watcher), construct `Simulation`, subscribe `OnPlayerRespawn`/`OnTileBroken` to cosmetic particle spawners.

`Update`: read keyboard/mouse, compute `mouseWorldPos` from the camera, `Controller.Poll` → `_sim.Step(input)`. Then **cosmetic-only**: cursor trail, sync sprites to bodies + advance animations, air→ground landing puff, particles, camera tracking. None of this writes sim state.

`Draw`: world transform from camera → chunks (damage-darkened) → platforms → growing sprouts → entities → players → particles/cursor trail → current action overlay → debug overlays (hitboxes, hurtboxes, orientation, constraints, health bars, gated by `GameConfig` toggles) → screen-space UI (state/action names, planner mode, block-picker HUD, health bars).

## Tests ([MTile.Tests/](MTile.Tests/))

xUnit. Categories:
- `PhysicsTests`, `GroundFrictionTests`, `MovingPlatformTests`, `JumpingStateTests` — physics/movement units.
- `Sim/` — scenario-driven simulation tests with deterministic ascii-terrain + scripted input (`SimRunner`, `SimTerrain`, `InputScript`, `SimReport` CSV diffing). `SimRunner.Run` mirrors `Simulation.Step`'s phase order; `SimRunner.RunMulti` runs multiple players sharing terrain + combat registries for cross-player combat tests.
- `SnapshotRoundTripTests` — the rollback gate: snapshot at frame K, run to N, restore K, re-run to N, assert identical traces (incl. terrain — a ball chipping the floor and a foam build straddling the snapshot both replay bit-for-bit).

See `CLAUDE.md` for build/run/test commands and the file-lock gotcha.

## Key conventions

- **Right-handed coords with Y-down** (MonoGame default). World gravity `(0, 600)` px/s² (`Simulation.Gravity`). Tile coords `gtx, gty` are integer global cell indices; cell centers are `gtx * 16 + 8`.
- **Forces are accelerations**: `PhysicsBody` has no mass; `body.Velocity += body.AppliedForce * dt`. Mass appears only in `ImpactDamage` and `Entity`/player knockback.
- **Modifier scalars are multiplicative on baseline config**, not absolute (`m.MaxWalkSpeed *= 0.6f`).
- **Priorities form bands**: free states 0–20, stun 25, walls 20–40, guided 25–45, launches 50–60. `Active > Passive` for "sticky" states; `Passive > Active` for preempting states.
- **Per-activation FSM state is plain data** in `MovementVars`/`ActionVars`; the state/action objects are stateless flyweights. This is what makes snapshot a struct copy.
- **`HitId` is monotonic per `Simulation`** via `HitIdAllocator`. CombatSystem dedupes by `(HitId, Target)` so multi-frame hitboxes land once per entity but apply cumulatively to tiles.
- **The sim is deterministic and snapshot-restorable**; render systems are strictly downstream and must never feed back. Terrain mutations funnel through `ChunkMap.WriteTile`/`GetOrCreateChunk` so the journal stays complete.
- **World reactions go through events** (`OnTileBroken`, `OnPlayerRespawn`), not polling.

## Where to start when extending

| I want to… | Look at |
|---|---|
| Add a new player ability | New `ActionState` subclass; add to `_actionRegistry` in [PlayerCharacter.cs](Character/PlayerCharacter.cs); put per-activation fields in `ActionVars`; pick priorities in the right band |
| Add a new movement state | Subclass `MovementState`, add to `_stateRegistry`; put per-activation fields in `MovementVars`; null any soft-contact cache in `ResetTransient` |
| Add a new enemy / projectile | Subclass `Entity`/`Projectile`, add an `EntityKind` + `Rehydrate` case + `WriteState`/`ReadState`; spawn via `Stage.Populate` or `ctx.Spawner` |
| Make an entity crashable | Set `body.Impact = new ImpactDamage { … }` in its factory |
| Add a new tile type | Extend `TileType`, add HP in `TileDamage.MaxHPFor`, color in `Game1.GetTileBaseColor` (+ picker key if player-selectable) |
| Add a new dynamic surface | Implement `ISolidShapeProvider`, register via `Simulation.AddPlatform`; provide `.Velocity`; drive motion from `_elapsed` for snapshot safety |
| Add a new stage | Register a `Stage` in `Stages` with a `Populate` delegate; add its `Levels/*.json` |
| Make a change snapshot-safe | Put mutable state in a value struct or a `Capture`/`Restore` pair; route terrain writes through `ChunkMap`; verify with a `SnapshotRoundTripTests` case |
| Add a feedback effect on a game event | Fire an event from the sim, subscribe in `Game1`, spawn via `Effects` (never mutate sim from the handler) |
| Tune movement | Edit `movement_config.json` — hot-reload picks it up (desktop) |
| Tune block eruption | Constants in [MassBallPlanner.cs](Character/MassBallPlanner.cs) |
| Tune impact / crush damage | `ImpactDamage` in [EntityFactory.cs](Entities/EntityFactory.cs) / [PlayerCharacter.cs](Character/PlayerCharacter.cs) |
