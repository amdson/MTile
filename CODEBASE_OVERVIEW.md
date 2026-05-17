# MTile — Codebase Overview

A 2D platformer in C#/MonoGame about "the terrain IS the weapon" — the player slashes, stabs, pulses, and erupts blocks to shape the battlefield while moving through chunked tile terrain. Project root: `c:\Users\amdic\dev\MTile`. Test project at `MTile.Tests/`. Game loop in [Game1.cs](Game1.cs); fixed timestep 30 fps.

## Top-level architecture

The game is built around four cooperating subsystems that share data via well-defined channels rather than direct calls:

```
                           ┌─────────────────────────┐
                           │      Game1 (loop)       │
                           └──────────┬──────────────┘
                                      │ each frame:
        ┌─────────────┬───────────────┼───────────────┬──────────────┐
        │             │               │               │              │
        ▼             ▼               ▼               ▼              ▼
  Input/Controller  Player FSMs   Physics       Combat         Drawing
  + Intent buffer   (movement +   (StepSwept    (hitbox ↔      (sprites,
                     action)       resolves     hurtbox        particles,
                                   bodies vs    SAT, tile      debug
                                   chunks)      damage)        overlays)
```

The data lattice tying them together:

| Channel | Producer | Consumer |
|---|---|---|
| `EnvironmentContext` | `PlayerCharacter.Update` builds it once | Both FSMs + checkers query it |
| `MovementModifiers` | Current `ActionState.ApplyMovementModifiers` | `MovementState` config reads (WalkAccel, GravityScale, …) |
| `PhysicsBody.AppliedForce` | `MovementState.Update` writes; `ActionState.ApplyActionForces` augments | `PhysicsWorld.StepSwept` integrates |
| `HitboxWorld` (offensive) | Action states publish during frame | `CombatSystem.Apply` reads at end of frame |
| `HurtboxWorld` (defensive) | `IHittable.PublishHurtboxes` at frame start | `CombatSystem.Apply` reads |
| `IntentBuffer` (gestures) | `InputParser.Detect` (Click/Stab/Circle/PressEdge) | Action preconditions Peek + Consume |
| `ConditionState` (flags) | Action `Enter`/`Exit` set Slash2Ready/RecoveryActive/etc | Action preconditions; `Tick` expires by frame |
| `OnTileBroken` event | `ChunkMap.BreakCell` | Game1 spawns debris particles |

**Coupling rule**: Actions may read movement state; movement code MUST NOT read action state. Modifiers and AppliedForce are the only channels in that direction.

## Physics ([Physics/](Physics/))

### `PhysicsBody`
Pure kinematic data: `Position, Velocity, AppliedForce, Polygon, Constraints, Impact`. `AppliedForce` is treated as direct acceleration (no mass term inside the integrator). `Constraints` holds `PhysicsContact`s (see below). `Impact` is nullable — when non-null, the body damages tiles it crashes into ([Physics/ImpactDamage.cs](Physics/ImpactDamage.cs)).

### `PhysicsWorld` ([Physics/PhysicsWorld.cs](Physics/PhysicsWorld.cs))
Two integrators:
- **`Step`** — discrete: move by `velocity * dt`, then iterate up to 8 times pushing the body out of overlapping shapes via MTV.
- **`StepSwept`** — used by the main loop. Sweeps the body's displacement against all shapes via swept-SAT, picks earliest `T`, advances `(1-T)` along the residual displacement, loops up to 4 bounces. Plus a discrete pre-pass for any sprout that flipped solid mid-overlap.

At every impulse site (3 of them: standing-constraint, discrete, swept) it computes `vnRel = (body.V - shape.V) · normal` and applies `body.V -= vnRel · normal` to zero relative normal velocity. The swept and discrete sites also call `TryApplyImpactDamage(body, bounds, normal, vnRel, chunks)` — which probes a 1px slab along the impact face via `WorldQuery.SolidShapesInRect`, collects all tiles pressed against it, and splits `(impulse − threshold) · scale` damage equally among them. Boundary-landing damages both tiles. Friction (`SurfaceContact.Friction`) is Coulomb-ish: caps per-step tangential velocity change at `friction · dt`, gated off when the state pushed along the same tangent (so walking doesn't fight floor friction).

### `PhysicsContact` hierarchy
- `SurfaceContact` (abstract): position, normal, minDistance, surface velocity, friction.
- `SurfaceDistance` — hard, stamped by collision resolution to prevent re-penetration. Pruned next frame if the source surface is gone.
- `FloatingSurfaceDistance` — soft, owned by movement states (Standing's `_ground`, Jumping's `_source`). Movement state writes a spring force toward the floor; the FSD only prevents downward penetration.

### `SolidShapeRef` ([World/SolidShapeProvider.cs](World/SolidShapeProvider.cs))
Provider-agnostic shape view: AABB + position + velocity + polygon. `ChunkMap` is the first `ISolidShapeProvider`; moving platforms (`MovingRectangle`) register additional providers. `WorldQuery.SolidShapesInRect` fans out across all of them — the swept solver and all checkers see one unified surface set.

### `SteeringRamp`
Rotates body velocity onto the shallowest trajectory clearing an upper/lower corner. Reads `ExposedCorner` from checkers; redirects horizontal motion into a slight vertical component when the body is sweeping toward an unblocked corner. Position is still resolved by the tile sweep; only velocity is pre-rotated.

## World / Tiles ([World/](World/))

### `ChunkMap` ([World/ChunkMap.cs](World/ChunkMap.cs))
Dictionary of `Point → Chunk`. Each chunk = 16×16 tiles; each tile = 16px (`Chunk.TileSize`). API surface:
- **Cell state** — `GetCellState(gtx, gty)` returns `Empty | Sprouting | Solid`; `GetCellType` returns `Stone | Dirt | Sand`.
- **Sprouts** — `TryRequestTile(gtx, gty, type)` builds either a Growing or Pending `TileSproutNode` in `Graph`. Growing sprouts have a `Polygon` and `Velocity` lerping from parent to target cell; Pending nodes are invisible until their first parent finalizes.
- **Damage** — `DamageCell(gtx, gty, amount)` → `TileDamage.ApplyDamage` accumulates per-cell HP; on threshold the cell breaks. `BreakCell` flips to Empty and fires `OnTileBroken(center, type)`.
- **TickSprouts(dt)** — ages Growing sprouts; finalizes complete ones (cell flips Solid, dropped from graph); promotes Pending children.

### `TileSproutGraph` ([World/TileSproutGraph.cs](World/TileSproutGraph.cs))
DAG of pending and growing sprouts. Pending nodes have parents; the first parent to finalize promotes the child to Growing. Used by both the player's drag-to-build (`HandleBuildInput`) and the block-eruption planner.

### `TileQuery` / `WorldQuery`
Two-tier query interface. `TileQuery` walks `ChunkMap`'s tile storage directly (cell-aligned, used for surface checkers that need integer-column scans). `WorldQuery` fans out across all `ISolidShapeProvider`s — used by the physics sweep and any code that needs to see tiles, sprouts, and moving rects uniformly.

### `TerrainLoader`
On startup, reads `Levels/terrain.json` (chunk-position → ASCII filename map + Perlin config). ASCII files use `X` for solid (defaults to Stone type), anything else for empty. Procedural chunks use 1D Perlin height + depth-layered types (Sand crust → Dirt mid → Stone deep).

### Tile sprite system
[Drawing/Sprites.cs](Drawing/Sprites.cs) builds `Sprite` instances from `Pose`s (`LineShape`, `RingShape`, `DiscShape`). The player is a hex with a moving eye (`Sprites.Player`); balls/balloons get simple circular sprites.

## Character ([Character/](Character/))

### `PlayerCharacter` ([Character/PlayerCharacter.cs](Character/PlayerCharacter.cs))
Owns two parallel FSMs (movement + action), the ability state, intent buffer, and input parser. Each `Update`:
1. Tick condition flags (combo windows expire).
2. `InputParser.Detect` enqueues gesture intents (Click/Stab/Circle/PressEdge).
3. Build a fresh `EnvironmentContext` (input + buffers + ChunkMap + dt + `ctx.Modifiers = Identity`).
4. Movement FSM: if current state's `CheckConditions` fails → exit + fallback to Falling. Then scan registry for higher-passive-priority candidates that pass `CheckPreConditions`; if one beats current's `ActivePriority`, transition.
5. **Action FSM runs *before* `MovementState.Update`** so the newly-selected action's `ApplyMovementModifiers(ref ctx.Modifiers)` is in effect when movement reads physics knobs.
6. `MovementState.Update` writes `Body.AppliedForce`.
7. `ActionState.ApplyActionForces(ctx)` augments AppliedForce (post-movement, for force-stacking).
8. Apply `Modifiers.GravityScale` as a counter-force on AppliedForce.
9. `ActionState.Update` does its FSM work (publishing hitboxes, advancing timers).

### Movement FSM ([Character/Movement.cs](Character/Movement.cs), [MovementStates.cs](Character/MovementStates.cs), [GuidedState.cs](Character/GuidedState.cs))

**Free states** (input + soft constraints):
- `FallingState` (0/0) — fallback. Air-controlled horizontal, gravity vertical.
- `StandingState` (10/10) — spring-held to ground; friction provides braking.
- `CrouchedState` (15/15) — spring-held at lower float-height; entered via Down.
- `WallSlidingState(dir)` — gravity dampened against a wall.

**Launch states** (set vY once, hold while button held):
- `JumpingState`, `RunningJumpState`, `DoubleJumpingState` (50/30, 55/35, 60/40)
- `WallJumpingState(dir)` (55/40), `CoveredJumpState` (55/32) — under low overhangs

**Guided states** (PD-controlled paths via `GuidedState`):
- `ParkourState(dir)` — vault over obstacles.
- `LedgePullState(dir)`, `LedgeGrabState(dir)` — wall corners.
- `DropdownState` — Down+platform-drop.

Each subclass implements `TryPlan` (build a `GuidedPath` of Hermite segments) + `IntentHeld`. Per-frame: `ProjectOnto` advances `ProgressT`; a PD controller plus gravity-cancel feedforward drives the body toward `Sample(ProgressT + lookahead)`. Phantom corner constraints (60° pseudo-ramps) are kept as safety nets. Stall watchdog aborts after 8 frames of no progress.

### Action FSM ([Character/ActionStates.cs](Character/ActionStates.cs))

Same FSM shape, separate registry/history. Each action overrides two physics hooks:
- `ApplyMovementModifiers(ref MovementModifiers m)` — declarative scalars (`m.MaxWalkSpeed *= 0.6f`, `m.GravityScale *= 0.3f`).
- `ApplyActionForces(EnvironmentContext ctx)` — direct write to `Body.AppliedForce` for impulses or "ensure-at-least" velocity assists.

Registered actions (Active/Passive):
- `NullAction` (0/0) fallback.
- `ReadyAction` (10/15) — wind-up on LMB press. Light slowdown + floaty gravity.
- `RecoveryAction` (40/45) — post-attack lockout; gates combos.
- **Ground slashes** `GroundSlash1/2/3` (30/30, 30/50, 30/50) — combo chain via `Slash2Ready`/`Slash3Ready` flags.
- **Air slashes** `AirSlash1/2` — same shape; `AirSlash2Ready` gates.
- `StabAction` (30/30) — long thrust along captured swipe direction. Air-stab dive boost: clamped lerp of `velocity · stabDir / BoostReferenceSpeed` scales both damage and the tile-shockwave polygon (length × boost, width × √boost). Lunge window during 0.10–0.40s applies a velocity assist + friction dip.
- `PulseAction` (30/30) — Circle gesture. 12-segment expanding ring; each segment publishes a knockback hitbox that picks up `body.Velocity` so a moving caster transfers their momentum.
- **Block Eruption** — two-phase:
  - `BlockReadyAction` (8/10) — RMB held with cursor IN solid. Accumulates `_chargeTime` up to saturation at 2s, with 35% dip past. Visual ring on origin cell.
  - `BlockEruptionAction` (9/10) — armed when cursor exits solid with RMB still held. Samples a `SmoothPen`-smoothed cursor path. On RMB release runs `EruptionPlanner.Plan(chunks, origin, samples, budget)`.

The shared `SlashLikeAction` base parametrizes arc shape (sweep angle, direction, radius scale, color, knockback) and damage-window timing (20%–70% of duration); subclasses just set per-variant knobs and combo-flag transitions.

### Block Eruption planners
[`EruptionPlanner`](Character/EruptionPlanner.cs) dispatches between two implementations (toggle via **P** key in Game1; default is MassBall):
- **PriorityField** — scores empty cells by front-loaded weight × radial falloff with area-conserving radius (slow pen → wide, fast → narrow), picks top-K, spawns in distance-from-origin order.
- **MassBall** ([Character/MassBallPlanner.cs](Character/MassBallPlanner.cs)) — simulates a spring-pulled "ball" tracing the recorded path. Mass leaks into field cells under it; threshold-crossing sprouts; excess spills 25% to each of 4 neighbors recursively (capped by depth + epsilon). When the puller exceeds the recorded path, it extrapolates linearly along the last sample's velocity — a brief sweep keeps depositing mass in the swept direction; a stationary release piles into one cluster.

### Input parsing
- [`Controller`](Character/Controller.cs) — 32-frame ring buffer of `PlayerInput` (Left/Right/Up/Down/Space/LeftClick/RightClick/MousePosition/MouseWorldPosition).
- [`InputParser`](Character/InputParser.cs) — edge-triggered gesture detection: `Click` (LMB short tap), `Stab` (LMB long-hold + swipe on release), `Circle` (RMB-free LMB sweep around press-center with cumulative ≥1.5π angular sweep), `PressEdge` (LMB press edge).
- [`IntentBuffer`](Character/IntentBuffer.cs) — short queue of `Intent { Type, Frame, Direction }`. Peek-check + explicit Consume; pruned by age.
- [`InputIntent`](Character/InputIntent.cs) — lightweight per-frame intent struct (HeldHorizontal, JumpTapped, etc.) for movement-side use.
- [`SmoothPen`](Character/SmoothPen.cs) — spring-pulled cursor smoother for block-eruption path sampling.

### Surface checkers
[`GroundChecker`](Character/GroundChecker.cs), [`CeilingChecker`](Character/CeilingChecker.cs), [`WallChecker`](Character/WallChecker.cs), [`ExposedUpperCornerChecker`](Character/ExposedUpperCornerChecker.cs), [`ExposedLowerCornerChecker`](Character/ExposedLowerCornerChecker.cs) — build strip regions via `body.Bounds.StripXxx(thickness)`, call `WorldQuery.SolidShapesInRect`. `EnvironmentContext` caches results within a frame (`_groundSearched` flags).

### Config
[`MovementConfig`](Character/MovementConfig.cs) hot-reloaded from `movement_config.json` via `FileSystemWatcher`. Holds: walk/jump speeds, accelerations, frictions, spring constants, guided-path knobs (`GuidedSpringK`, `GuidedDamping`, `GuidedMaxForce`, `GuidedLookahead`, `GuidedGravityCancel`), sprout lifetime.

## Combat ([World/HitboxWorld.cs](World/HitboxWorld.cs), [HurtboxWorld.cs](World/HurtboxWorld.cs), [CombatSystem.cs](World/CombatSystem.cs), [Hitbox.cs](World/Hitbox.cs), [Hurtbox.cs](World/Hurtbox.cs))

Hitbox-vs-hurtbox model. Per frame:
1. Both registries cleared.
2. Every `IHittable.PublishHurtboxes(world)` populates defensive boxes (player, entities).
3. Action FSM runs — slashes / stab / pulse publish offensive hitboxes during it.
4. `CombatSystem.Apply` walks every hitbox × every hurtbox; on AABB overlap + faction mismatch + (optional polygon-vs-AABB SAT refinement if `hit.Shape` set), dispatches `IHittable.OnHit`. Deduped per `(HitId, Target)` pair across the broadcast window — a 4-frame slash hits a balloon once. Same hitbox is also applied to tiles via `chunks.DamageCell` — but cumulatively, no dedup, so the same multi-frame slash progressively damages a tile.
5. Physics integrates afterward (so this frame's hitboxes already reflect this frame's body position).

`HitTargets.{All, TilesOnly, EntitiesOnly}` filters the dispatch — useful for "shockwave" effects that reach further into terrain than they do entities.

## Entities ([Entities/](Entities/))

[`Entity`](Entities/Entity.cs) — `IHittable` non-player wrapper around a `PhysicsBody`. Fields: `Health, MaxHealth, Mass, GravityScale, Color, Faction, Sprite`. `PreStep(gravity)` adds a counter-gravity force scaled by `(GravityScale - 1)` before integration. `OnHit` applies damage + knockback `impulse / Mass`.

[`EntityFactory`](Entities/EntityFactory.cs):
- `Balloon(pos)` — `GravityScale=0`, low health, pink. Passive target.
- `Ball(pos)` — `GravityScale=1`, blue. Configured as a "crasher" with `Impact = { Mass=1.5, ImpulseThreshold=200, DamagePerUnitImpulse=0.01 }` — terminal-ish falls (~400 px/s) chip Dirt, harder throws break Stone.
- `FloatingBall(pos)` — same crasher config but `GravityScale=0`, coral color. Used in the impact-damage test chamber.

## Drawing ([Drawing/](Drawing/))

[`DrawContext`](Drawing/DrawContext.cs) — wraps `SpriteBatch` + 1×1 pixel texture, exposes `Line/Rect/Ring/Disc/RotatedRect` primitives.

[`Sprite`](Drawing/Sprite.cs) — `Pose`-based vector sprite (list of `LineShape`/`RingShape`/`DiscShape` in local space). `AnimatedSprite` extends with frame timing.

[`ParticleSystem`](Drawing/ParticleSystem.cs) — fixed-capacity pool (2048 in Game1), swap-remove on death, ring-buffer overflow when saturated. `Particle` has Position/Velocity/Acceleration/Life/StartColor/EndColor/StartSize/EndSize/Rotation/AngularVelocity + `ParticleKind` (Square/Disc/Line).

[`Effects`](Drawing/Effects.cs) — preset spawners: `TileBreak(particles, center, tint)`, `Puff` (landing dust), etc.

## Main loop ([Game1.cs](Game1.cs))

Initialize:
- `TerrainLoader.Load` populates chunks.
- `MovementConfig.Load` + FileSystemWatcher for hot reload.
- Player at `(0, -200)`.
- `MovingRectangle` + ferris-wheel cluster (4 blocks orbiting a center) — both registered as `ISolidShapeProvider`s in `_chunks.Providers`.
- Spawn balloons, balls, and 3 floating balls in the impact-damage test chamber (chamber wall at world `x ∈ [208,256], y ∈ [-256,-192]`).
- Subscribe `_chunks.OnTileBroken` → `Effects.TileBreak`.

Per-frame Update order:
1. Input (esc to exit, P to toggle eruption planner, `_controller.Update`).
2. `HandleBuildInput` — drag-to-build sprouts under RMB (skipped if a BlockReady/Eruption action is current).
3. Tick `MovingRectangle`s + ferris blocks (BEFORE the body sweep, so dynamic surfaces have updated `.Velocity` when shapes are queried).
4. `_chunks.TickSprouts(dt)`.
5. Combat phases: clear hitboxes/hurtboxes → publish hurtboxes → `_player.Update` (publishes hitboxes) → `CombatSystem.Apply` (dispatches OnHit + tile damage).
6. `Entity.PreStep` for gravity scale.
7. `PhysicsWorld.StepSwept`.
8. Sweep dead entities out of `_entities + _hittables + _bodies`.
9. Sync sprites → bodies; advance animations + particles.
10. Detect air→ground transition → spawn landing puff.
11. Camera tracks player.

Draw:
- World transform from camera.
- Chunks: per-tile draw with `Color.Lerp(baseColor, Black, dmgFrac · 0.7)` for damage darkening.
- `MovingRectangle` + ferris blocks.
- Growing sprouts (light-blue ghosts).
- Entities (sprite or polygon outline fallback).
- Player sprite.
- `DebugDrawBodies` — polygon outlines (off by default).
- Particles.
- `Action.Draw(spriteBatch, pixel, body)` — slash trail, stab box, pulse ring.
- Debug overlays: hitboxes, facing arrow, mouse ray, constraint arrows (yellow for hard, cyan for floating), guided-path arc.
- UI: state name, action name, planner mode + P-toggle hint.

## Tests ([MTile.Tests/](MTile.Tests/))

xUnit project. 79 tests as of latest verification. Categories:
- [`PhysicsTests`](MTile.Tests/PhysicsTests.cs) — body-vs-tile collision basics.
- [`GroundFrictionTests`](MTile.Tests/GroundFrictionTests.cs), [`MovingPlatformTests`](MTile.Tests/MovingPlatformTests.cs) — friction + dynamic surface carry.
- [`JumpingStateTests`](MTile.Tests/JumpingStateTests.cs) — jump dynamics.
- `Sim/` — scenario-driven simulation tests with deterministic ascii-terrain + scripted-input (`SimRunner`, `SimTerrain`, `InputScript`, `SimReport`). Covered cases: dropdowns, sprout-push, covered-jump corridors, guided-state vaults.

Building: `dotnet build MTile.Tests/MTile.Tests.csproj`. When the game is running, the main `MTile.exe` is locked and the build's final copy step fails, but the C# compile + test dll succeed — `dotnet test --no-build` works against the already-built `MTile.Tests.dll`.

## Key conventions

- **Right-handed coords with Y-down** (MonoGame default). World gravity is `(0, 600)` px/s². Tile coords `gtx, gty` are integer global cell indices; world coords are `gtx * 16 + 8` for centers.
- **Forces are accelerations**: `PhysicsBody` has no mass; `body.Velocity += body.AppliedForce * dt`. Mass appears only in `ImpactDamage` and `Entity` (for knockback).
- **Modifier scalars are multiplicative on baseline config**, not absolute. `m.MaxWalkSpeed *= 0.6f` halves walking when in Pulse stance.
- **Priorities form bands**: free states 0–20, walls 20–40, guided 25–45, launches 50–60. `Active > Passive` for "sticky" states; `Passive > Active` for states that should preempt others.
- **`HitId` is monotonic per action instance**. CombatSystem dedupes by `(HitId, Target)` across the broadcast window so multi-frame hitboxes land once per entity, but apply cumulatively to tiles.
- **Sprouts use a DAG with promotion semantics**. Pending nodes wait for their first parent to finalize; that parent decides growth direction.
- **Game1 reactions to world events go through events**, not polling. `OnTileBroken` for particles; future hooks should follow the same pattern.

## Where to start when extending

| I want to… | Look at |
|---|---|
| Add a new player ability | New `ActionState` subclass; add to `_actionRegistry` in [PlayerCharacter.cs](Character/PlayerCharacter.cs) constructor; pick priorities in the right band |
| Add a guided traversal (zip-line, dash, …) | Subclass `GuidedState`, implement `TryPlan` + `IntentHeld`, add to `_stateRegistry` |
| Make an entity crashable | Set `body.Impact = new ImpactDamage { … }` in its factory |
| Add a new tile type | Extend `TileType` enum, add HP in `TileDamage.MaxHPFor`, color in `Game1.GetTileBaseColor` |
| Add a new dynamic surface | Implement `ISolidShapeProvider`, register in `_chunks.Providers`. Provide `.Velocity` so carry math works |
| Add a feedback effect on game event | Subscribe to `ChunkMap.OnTileBroken` or fire your own event via similar pattern; spawn via `Effects` |
| Tune movement | Edit `movement_config.json` — hot-reload picks it up |
| Tune block eruption | Constants in [MassBallPlanner.cs](Character/MassBallPlanner.cs) (Threshold, LeakFractionBase, SpringStiffness/Damping) |
| Tune impact damage | `ImpactDamage` fields in [EntityFactory.cs](Entities/EntityFactory.cs) |
