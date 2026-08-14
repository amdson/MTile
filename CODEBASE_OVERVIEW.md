# MTile — Codebase Overview

A 2D platformer in C#/MonoGame about "the terrain IS the weapon" — the player slashes, stabs, pulses, fires, and erupts blocks to shape the battlefield while moving through chunked tile terrain. Project root: `c:\Users\amdic\dev\MTile`.

The game logic lives at the repo root and is compiled by several hosts (see `CLAUDE.md` for the project layout and build commands): `MTile.Core` (the library), `MTile.Desktop` (DesktopGL host), `MTile.Web` (KNI/Blazor WASM host), `MTile.Tests` (xUnit), plus the tooling projects `MTile.Probe`, `MTile.Demo`, `MTile.Bench`, `MTile.FxLab` and the `MTile.Rtc` transport. Fixed timestep of 60 fps (`Simulation.FixedDt = 1/60`).

> **Architecture in one sentence:** [`Simulation`](Simulation.cs) is the deterministic game world that advances on inputs alone via `Step(PlayerInput)`; [`Game1`](Game1.cs) is the render/input shell around it; snapshot/restore and the rollback session on top of it are **built and running** (`Net/`), not aspirational.

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
| `ForceFieldWorld` (fields) | Action states publish during frame (hold/push/grab/throw) | `ForceFieldSystem.Apply`, **before** physics |
| `OnTileBroken` / `OnPlayerRespawn` events | `ChunkMap.BreakCell` / `Simulation.Step` | `Game1` spawns cosmetic particles |

**Coupling rule**: Actions may read movement state; movement code MUST NOT read action state. `MovementModifiers` and `AppliedForce` are the only channels in that direction.

## Simulation & determinism ([Simulation.cs](Simulation.cs))

`Simulation` owns every piece of state the world reads or writes and advances it one fixed step via `Step(PlayerInput)`. Two constructors: a headless one (terrain supplied directly + a `populate` delegate — used by tests) and the real one (`GameConfig` + `Stage`). It implements `IEntitySpawner` (AI/projectiles spawn children) and `IChunkProvider` (entities mutate terrain).

There are two entry points: `Step(PlayerInput)` for solo and `Step(p0, p1)` for two players (injects `p1` into the first secondary's `Controller`, then falls through to the same body). Drag-to-build is no longer a `Step` phase — it moved per-player into the terrain-building actions.

**`Step` phase order** (mirrored exactly by the headless `SimRunner` so tests match real play):
1. Inject `input`; advance the absolute sim clock `_elapsed += FixedDt`.
2. Tick dynamic surfaces (`MovingRectangle` platforms) as a **pure function of `_elapsed`** — no hidden accumulator, so they snapshot cleanly.
3. `ChunkMap.TickSprouts(dt)` + `ChunkMap.Impact.Tick(dt)` (decay per-cell impact accumulator).
4. Combat frame: clear hitbox/hurtbox/**force-field** registries → every `IHittable.PublishHurtboxes` (primary, secondaries, entities in spawn order) → `_player.Update` (publishes hitboxes + fields) → secondary players → entity AI `Update` → `CombatSystem.Apply`.
5. `ForceFieldSystem.Apply` — force fields resolve **after** every publisher has updated but **before** physics (unlike hitboxes, which resolve post-step). `onGrabHeld`/`onThrown` callbacks flag victims via `CombatState.MarkGrabbed`/`RegisterThrown`.
6. Player respawn on death (deterministic, inside `Step`; fires `OnPlayerRespawn`).
7. `Entity.PreStep` (gravity-scale opt-out) → `PhysicsWorld.StepSwept` → sweep up dead entities.

`Checksum()` is a cheap order-stable FNV-1a fingerprint over both players' pose/velocity/health, the entity set in spawn order, and the id counters. It's the netcode desync guard, not a snapshot — a mismatch between peers at the same frame is a hard desync.

### The ECS substrate ([Sim/ECS/](Sim/ECS/))

`Simulation` no longer holds `_bodies`/`_entities`/`_hittables` lists. Bodies and entities live in a hand-rolled sparse-set `World` ([Sim/ECS/World.cs](Sim/ECS/World.cs), `ComponentStore.cs`, `Query.cs`, `WorldSnapshot.cs`, `Components/EcsComponents.cs`) keyed by generational `EntityId`s. Stores are marked at construction (`MarkWorldStores`) as either:

- **live-only** — `PlayerRef`, `EntityRef`, `PhysicsBodyComponent`: skipped by capture, re-registered in canonical order on restore.
- **snapshotted value stores** — `PlayerData`, `EntityData`, `BodyStateComp`: plain data, the latter two with deep-clone hooks.

Per step the World is projected into `_bodyScratch`/`_entityScratch` for the solver. The public `Entities`/`Bodies` properties allocate fresh lists per access — render/test-only, off the hot path.

### Snapshot / restore

[`SimSnapshot`](SimSnapshot.cs) is small because the ECS World *is* the player+entity snapshot: `HitIdValue`, `World`, `Elapsed`, primary + secondary `ControllerState`s, `Dedupe`, `HitConfirm`, `Recoil`, `Platforms`, `Terrain`. The old `PlayerSnapshot[]`/`EntitySnapshot[]` arrays are gone.

`Snapshot()` first syncs every live player and entity's serializable state into its World value components (`CaptureState(_world)`), then calls `_world.Capture()`. `Restore(snap)` restores the World's id bookkeeping, rewinds terrain, re-registers the live-only refs in canonical order (primary, secondaries, entities in spawn order) so iteration order — and therefore stepping — is identical, and rehydrates entities that no longer exist via `EntityFactory.Rehydrate`.

- **Bodies** → [`BodyState`](Physics/BodyState.cs): pose + kinematics + only the **Maintained** (hard) constraints, deep-cloned. Soft state-owned contacts (`FloatingSurfaceDistance`, `PointForceContact`) are NOT captured — the owning state's idempotent `Ensure…`/`ResetTransient` rebuilds them next frame.
- **Players** → `PlayerData`: FSM selection as **registry indices** (states/actions are flyweights constructed in fixed order), per-activation data as the [`MovementVars`](Character/MovementVars.cs)/[`ActionVars`](Character/ActionVars.cs) value structs, helper objects deep-cloned (`PlayerAbilityState.Clone`, parser/intents capture, eruption gesture deep-copy).
- **Combat** → dedupe table by `HittableId`, plus the two 1-frame inboxes (`HitConfirm`, `Recoil`).
- **Terrain** → [`TerrainSnapshot`](World/TerrainSnapshot.cs): the dense tile grid is too large to copy, so it's rolled back via an inverse-delta journal ([`TerrainJournal`](World/TerrainJournal.cs)) — a snapshot stores `Mark`, restore replays entries past it in reverse. Sparse side-structures (sprout graph, per-cell HP, foam timers, impact accumulator) tick every frame so they're value-snapshotted instead. **Caveat:** journal marks are instance-relative, so terrain restore is same-instance only (the rollback case). [`DenseTerrainCapture`](World/DenseTerrainCapture.cs) provides a portable full-grid alternative for tooling/tests.

### Rollback netcode ([Net/](Net/))

Built and running, not planned. [`RollbackSession`](Net/RollbackSession.cs) does predict → snapshot-ring → reconcile → replay with `InputFrameDelay = 3`, `StallSlack = 3`, `BufferLen = 60`, and piggybacked `Checksum()` desync detection. Around it: `InputRing`, `SnapshotRing`, `InputCodec`/`InputPacket`, `IRemoteInputSource`, `BotInputSource` (still a seeded-random stub — see `Plans/BOT_AI_PLAN.md`), `NetSetup`. Transport is `MTile.Rtc` (WebRTC, manual copy/paste SDP signaling via `MTile.Desktop -- host|join`).

`Game1` takes an optional `NetSetup`; with one present it drives `_session.TryStep()` instead of stepping the sim directly, and `LocalPlayer` follows `_net.LocalPlayerIndex`. Offline-with-a-second-player uses `BotInputSource` to spoof P2. Stage save (Ctrl+M) and reload (F5) are gated off whenever a session exists.

**`NetSetup` is the transport-agnostic seam** — just `LocalPlayerIndex`, an `Action<byte[]> Send`, and a `ConcurrentQueue` for delivery. This paid off: adding browser multiplayer required **zero changes under `Net/`** (verified — `git diff` over `Net/` across the entire browser-PvP effort is empty). Two transports now meet the sim at that seam:

- **Desktop** — `MTile.Rtc/RtcConnection.cs`, driven by `MTile.Desktop -- host` / `-- join`.
- **Browser** — `MTile.Web/wwwroot/mtileRtc.js`, a hand-written twin. `MTile.Web.csproj` explicitly *excludes* `MTile.Rtc/` from compilation, so these are parallel implementations, not shared code. They are wire-compatible (same blob format, same `"mtile"` channel, `{ordered:false, maxRetransmits:0}`, non-trickle ICE) — but **cross-play is forbidden anyway**, because float determinism doesn't hold across runtimes. Same build on both ends, always.

### Browser PvP ([MTile.Web/](MTile.Web/))

- **Lobby/signaling** — two paths. *Room code*: `wwwroot/signaling.js` writes to Firestore, where the room doc id **is** the code (5 chars from an unambiguous alphabet, no `0/O/1/I/L`), schema `rooms/{CODE} = { offer, createdAt, expireAt }` with the joiner adding `answer`; 1h TTL; `?room=CODE` deep-links into join. *Manual*: the original copy/paste blob exchange, kept as a fallback and as the path the smoke test drives. The Firebase SDK is lazily dynamic-imported, so the page still works offline in solo/manual mode.
- **Security** — `MTile.Web/firestore.rules` allows `get` but not `list` (no room enumeration), restricts `create` to exactly the three expected fields with an 8KB offer cap, and permits exactly one `update` that may only add `answer`. No deletes. Setup steps in `MTile.Web/FIREBASE_SETUP.md`.
- **Interop** — `MTile.Web/Pages/Index.razor.cs` runs a lobby FSM (`Menu → HostGathering/JoinEnterCode → … → Playing/Failed`). JS→C# via `[JSInvokable]` (`OnRtcOpen`, `OnRtcMessage(byte[])`, `OnRtcState`, `TickDotNet`); the send hot path prefers synchronous `IJSInProcessRuntime.InvokeVoid` and latches to base64 if `byte[]` marshaling throws.
- **STUN only, no TURN** — `iceServers` is built with `{urls}` and no credentials, so symmetric-NAT/CGNAT pairs still fail to connect. This is the main thing standing between the current state and "send a stranger a link" (`Plans/INTERNET_READY_PLAN.md` Phase 2).

### Determinism rules (when touching sim code)
- **No sim-affecting mutable statics.** `HitId`s come from the per-`Simulation` [`HitIdAllocator`](World/HitIdAllocator.cs); block type is per-`PlayerCharacter` (driven by that player's own input), not global.
- **All input arrives via `PlayerInput`** — no polling hardware mid-step. Block-picker (1-4) and planner toggle (P) are interpreted inside `PlayerCharacter.Update`.
- **Same iteration order on restore.** Lists are rebuilt deterministically.
- `MovementConfig` hot-reload is gated behind `GameConfig.HotReloadMovementConfig` (off for MP).

Topology target is **same-build P2P** (desktop↔desktop or WASM↔WASM), so `float`/`MathF` determinism is a non-issue (same binary). See `Plans/ROLLBACK_ROADMAP.md`, `Plans/STATE_SNAPSHOT_PLAN.md`, `Plans/GGPO_PLAN.md`.

## Stages ([Stage.cs](Stage.cs))

A `Stage` bundles "what to load at start": `TerrainConfig` (filename in `Levels/`), `PlayerSpawn`, and a `Populate(Simulation)` delegate that spawns entities + registers platform tickers. `Stages` is a code registry (stages contain behavior, not just data); `game_config.json`'s `Stage` field selects one by name. Seven stages today: `start` (the original test world — moving platform, ferris-wheel cluster, balloons/balls, one stalker), `arena` (bounded combat room — stalkers, turrets, ammo balls), `plain`, `training`, `corridor` (the corrector stress harness — pairs with `PlayerCharacter.RestrictToFallAndStand()`, which strips the movement registry to Falling+Standing), `gym`, and `flat`.

## Physics ([Physics/](Physics/))

### `PhysicsBody`
Pure kinematic data: `Position, Velocity, AppliedForce, Polygon, Constraints, Impact, FrictionScale, LastImpulseMagnitude`. `AppliedForce` is treated as direct acceleration (no mass term inside the integrator). `Constraints` holds `PhysicsContact`s. `Impact` is nullable — when non-null, the body damages tiles it crashes into ([Physics/ImpactDamage.cs](Physics/ImpactDamage.cs)). `LastImpulseMagnitude` records the largest `|vnRel|` absorbed last step (read by player crush-damage). `FrictionScale` is captured/restored with the body.

### `PhysicsWorld` ([Physics/PhysicsWorld.cs](Physics/PhysicsWorld.cs))
Two integrators:
- **`Step`** — discrete: move by `velocity * dt`, then iterate up to 8 times pushing the body out of overlapping shapes via MTV.
- **`StepSwept`** — used by the main loop. Sweeps the body's displacement against all shapes via swept-SAT, picks earliest `T`, advances `(1-T)` along the residual displacement, loops up to 4 bounces. Plus a discrete pre-pass for any sprout that flipped solid mid-overlap.

At every impulse site it computes `vnRel = (body.V - shape.V) · normal` and applies `body.V -= vnRel · normal` to zero relative normal velocity — **capped by how much impulse the impacted tile face can absorb** (excess carries through instead of being eaten, which is why `CrushImpulseThreshold` moved 400 → 700). The swept and discrete sites also call `TryApplyImpactDamage` — probes a 1px slab along the impact face via `WorldQuery.SolidShapesInRect`, splits `(impulse − threshold) · scale` damage among the pressed tiles. Friction (`SurfaceContact.Friction`) is Coulomb-ish: caps per-step tangential velocity change at `friction · dt`, gated off when the state pushed along the same tangent.

### `PhysicsContact` hierarchy
- `SurfaceContact` (abstract): position, normal, minDistance, surface velocity, friction. The `Maintained` flag marks hard contacts that survive a snapshot.
- `SurfaceDistance` — hard, stamped by collision resolution to prevent re-penetration (`Maintained == true`). Pruned next frame if the source surface is gone.
- `FloatingSurfaceDistance` — soft, owned by movement states (Standing's `_ground`, ledge states' `_wall`/`_floor`). Spring force toward the floor; soft (not maintained), so it's rebuilt after a restore.
- `PointForceContact` — soft, also rebuilt after restore.

### `SolidShapeRef` ([World/SolidShapeProvider.cs](World/SolidShapeProvider.cs))
Provider-agnostic shape view: AABB + position + velocity + polygon. `ChunkMap` is the first `ISolidShapeProvider`; moving platforms register additional providers. `WorldQuery.SolidShapesInRect` fans out across all of them.

> **`SteeringRamp` is gone.** The steering-ramp stack that once drove parkour/vault was removed wholesale by the ballistic corrector (`Plans/BALLISTIC_CORRECTOR_PLAN.md`); only stale comments still name it. Corner clearance is now solver rows against exact C-space geometry — see the corrector note under the Movement FSM.

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
Owns two parallel FSMs (movement + action), the ability state, intent buffer, and input parser. Body is a half-width hexagon: `Radius = 12f`, `BodyWidthScale = 0.5f` (both carry do-not-change notes — they are load-bearing for every tuned constant in the corrector and impact stacks).

**The combat model is escalation-based (Smash-style), not HP-chipping.** A direct hit does *not* reduce HP. `OnHit` runs, in order: tech i-frames → Guard parry (`CombatState.TryParry`) → grab-struggle (`GrabStrengthDamage` erodes the grabber's hold instead of dealing anything) → otherwise `AddPercent(hit.Damage)` and knockback scaled by the resulting monotonic `DamagePercent` via `HitResolver.Resolve`. So a hit's "damage" stat is its **percent contribution**; only the tile path still reads it as damage.

Real HP loss comes from **crush** — `PhysicsBody.LastImpulseMagnitude` above `CrushImpulseThreshold` (700f) converts the excess into HP loss + hitstun. Low % ⇒ shoved around harmlessly; high % ⇒ flung into terrain hard enough to take crush damage. HP fast-regens after a quiet window; `DamagePercent` is monotonic and resets only on KO. Post-hit invulnerability was **removed** (it made combos impossible) — only spawn protection and tech i-frames remain.

Each `Update`:
1. `_frame++`; tick invuln; apply crush damage.
2. Interpret this player's own block-picker (1-4) + planner-toggle (P) input → `_activeBlockType` / `_eruptionMode`.
3. Tick `ConditionState` (combo windows) + `CombatState` (hitstun/stun) flags.
4. `InputParser.Detect` enqueues gesture intents.
5. Build a fresh `EnvironmentContext` (input + buffers + chunks + spawner + HitIds + condition/combat + frame + dt + `Modifiers = Identity`).
6. Movement FSM: if current state's `CheckConditions` fails → exit + fall back to Falling. Then scan registry for higher-passive-priority candidates passing `CheckPreConditions`; transition if one beats current's `ActivePriority`. Two further gates apply: a candidate's `RequiredCapabilities` must not intersect `CombatState.BlockedCapabilities`, and last frame's state holds a one-frame veto via `owner.Suppresses(state, ctx)`.
7. **Action FSM selection runs *before* `MovementState.Update`** so the newly-selected action's `ApplyMovementModifiers` is in effect when movement reads physics knobs.
8. `MovementState.Update` writes `Body.AppliedForce`; `ActionState.ApplyActionForces` augments it; gravity-scale modifier applied as counter-force.
9. `ActionState.Update` does its FSM work (publishing hitboxes, advancing timers).

**Per-activation FSM state is plain data.** The FSM state/action instances are flyweights (one per registry entry, shared across activations); all mutable per-activation fields live in the [`MovementVars`](Character/MovementVars.cs)/[`ActionVars`](Character/ActionVars.cs) value structs on the player, passed `ref` into lifecycle methods (`Enter`/`Update`/`Exit`/`CheckConditions`) and `in` into the read-only hooks. This is the snapshot unit — a struct copy. The eruption rework removed the last reference-typed action buffer, so there is no longer any per-action deep-copy special case; `BlockGrabAction`'s peel group rides in an `[InlineArray(25)] PeelMemberBuffer` inside `ActionVars` precisely to keep value semantics. `PlayerAbilityState` holds the rest: `Facing`, `HasDoubleJumped`, ledge-grab flags, the [`BuildMeters`](Character/BuildMeters.cs), and the nested `Condition`/`Combat` states.

### Movement FSM ([Character/Movement.cs](Character/Movement.cs))

`MovementStates.cs` no longer exists — the states are split by family across [LocomotionStates.cs](Character/LocomotionStates.cs), [JumpStates.cs](Character/JumpStates.cs), [WallStates.cs](Character/WallStates.cs), [LedgeStates.cs](Character/LedgeStates.cs), [ClimbStates.cs](Character/ClimbStates.cs), and [ReactionStates.cs](Character/ReactionStates.cs). **All arbitration numbers live in [MovementPriorities.cs](Character/MovementPriorities.cs), which is the single source of truth** — prefer it over the band summaries in this doc.

> **The corrector era** (Plans/BALLISTIC_CORRECTOR_PLAN.md +
> Plans/CORRECTOR_CONSOLIDATION_PLAN.md): free-state locomotion is now
> solver-driven — "the solver IS the locomotion controller". The FOLD states
> (Standing/Falling/Crouched) attach **no** ground constraint and no spring;
> they apply only a gravity-hold + station-friction baseline
> (`StandingState.FoldBaseline`) and publish a `FoldProfile` (hover offset,
> climb reach, target speed). Each frame `AmbientCorrector.Apply` predicts a
> correction-free coast (`BallisticPredictor`), emits clearance rows against
> exact per-tile C-space geometry (`CObstacle`, `ClearanceConstraintBuilder`)
> plus soft envelope-reference rows (hover/progress), and solves a restricted
> channel stack (`CorrectorChannels`: LegServo/Drive/CornerAssist/Redirect/
> Tuck — capability = channel restriction, never casework) with the
> preconditioned projected-gradient `CorrectionSolver`. Hover, walking,
> braking, landing catch, 1-high climbs, ducks, and graze deflections are all
> solver output; climb bindings are ALL-OR-NOTHING with rollout-checked
> deliverability and hysteresis (elective refusal — the honest bonk). The
> corrector climb family (`ParkourCorrectorState`/`ArcJumpCorrectorState`/
> `MantleCorrectorState`) owns bigger maneuvers on the same predict→rows→solve
> loop with its own full channel stack (`BuildManeuver`). Channel semantics
> are physical and uniform: legs + plant-and-deflect redirect near the ground
> (redirect only on dynamic, unsupported ticks), air control only in flight
> (lateral + tiny vertical — no redirect against nothing). Non-fold states
> keep their owned servo mechanics and gate ambient assists via
> `AmbientPolicy`.

**Fold states**: `FallingState` (0/0, fallback), `StandingState` (10/10), `CrouchedState` (15/15) — support/locomotion via the ambient fold (above). **Wall**: `WallSlidingState(dir)` (owned; publishes `AmbientPolicy.Off`).
**Stun**: `StunnedState` (25/25) — heavy-hit lockout; muted air control while `Combat.StunActive`. Preempts free air but not active jumps. `TumbleState` (**51 active / 26 passive**) — the airborne heavy-hit launch variant: its high Active keeps it in the launch band so nothing steals the body mid-launch, while its low Passive lets a player hit mid-jump finish the arc first.
**Launch states**: `JumpingState`, `RunningJumpState`, `DoubleJumpingState`, `WallJumpingState(dir)`, `CoveredJumpState` (under low overhangs) — set vY once, hold while button held. Actives 50–60, but **passives are only 30–48** (they're trigger-driven, not assertive).
**Guided traversals**: the corrector climb family above at **29/29** — trigger-by-feasibility, a maneuver fires iff its corrected arc provably delivers. 29 deliberately sits *below* every launch's passive (Jump 30 … CoveredJump 48) so a player's own jump input always wins the same-frame race at a lip, while still outbidding every free state and the stun band. (The old 46/46 was a genuine bug: preemption compares candidate Passive to current Active, so climbs were in fact unbeatable.) Holds are 42–44 (`LedgeGrabState`/`LedgePullState`, wall corners via `FloatingSurfaceDistance` + ability flags), `LedgeJumpState` is 55/44, and `DropdownState` (Down+platform-drop) sits at 20/20 in the free band.

`MovementState` lifecycle methods take `ref MovementVars`; `ResetTransient()` nulls any soft-contact ref cache after a restore so the idempotent `Ensure…` rebuilds it next frame. Cross-frame corrector state (Δu anchors, elective latch) lives in `MovementVars` — snapshot-covered.

### Action FSM ([Character/ActionStates.cs](Character/ActionStates.cs))

Same FSM shape, separate registry/history, lifecycle takes `ref ActionVars`. Each action overrides `ApplyMovementModifiers(ref MovementModifiers, in ActionVars)` (declarative multiplicative scalars) and `ApplyActionForces(ctx, in ActionVars)` (direct `AppliedForce` writes). Registered actions (Active/Passive priority):

- `NullAction` (0/0) fallback; `ReadyAction` (10/15) LMB wind-up; `RecoveryAction` (40/45) post-attack lockout gating combos.
- **Slashes** — `SlashLikeAction` base parametrizes arc shape + damage window. `GroundSlash1/2/3` (combo via `Slash2Ready`/`Slash3Ready`), `CrouchSlash` (crouch-only), `AirSlash1/2`, `AirTurnSlash` (air backward-click turnaround), `GuardRetaliateAction` (counter after a charged parry).
- `StabAction` (30/30) — long thrust with air-stab dive boost; `AirSpinStab` (air backward-swipe variant).
- `PulseAction` (30/30) — Circle gesture; 12-segment expanding knockback ring that carries the caster's momentum.
- **Guard** — `GuardAction` (35/40, Shift held + no L/R) sets `GuardActive`; a weak in-cone hit parries to zero and arms `GuardCharged`, enabling `GuardRetaliateAction`.
- **Grab / throw** — `GrabAction` (46/46) publishes a hold force field; `GrabbedSlash` (36/36) is the victim's exempt struggle attack, which erodes `GrabStrength` rather than dealing knockback. Throws stun the victim into Tumble.
- **Ranged** — `EnergyBallAction` (Shift+LMB tap), `BeamAction` (Shift+LMB hold → sustained beam after charge), `GrenadeAction` (F → sticky grenade). These spawn projectile entities via `ctx.Spawner`. `LobbedAreaAction` (Shift+RMB charge → ranged eruption) still exists but is **deactivated** — its binding became Grab.
- **Terrain building** — see the section below. `BlockPaintAction` (8/10, plain RMB), `BlockPlaceAction` (8/10, Shift+RMB), `BlockBurstAction` (30/30), `BlockGrabAction` (46/46, Shift+LMB into solid). `GrabAction` moved to 48/48 so grabbing a *player* outranks grabbing *terrain*.

### Combat condition state
[`ConditionState`](Character/ConditionState.cs) — *offensive* combo/recovery/guard-window flags, each with an expire frame; `Tick` closes windows.
[`CombatState`](Character/CombatState.cs) — *defensive*: `Hitstun` (every hit briefly locks Jump, with diminishing extensions so stun-locks can't grow unbounded), `Stun` (heavy hits, gates attacks too), and Guard (`GuardActive`/`GuardCharged` + `TryParry`). Exposes `BlocksJump`/`BlocksAttack` gates.

### Terrain building — paint, place, burst, peel

**This replaced the old two-phase Block Eruption model.** `EruptionPlanner.cs` and `MassBallPlanner.cs` are **deleted**, along with `BlockReadyAction`/`BlockEruptionAction`, the per-player `EruptionMode`, and the `P` planner toggle. Building is now a continuous *mass economy* rather than a one-shot plan.

- **The mass field** ([World/TileMassField.cs](World/TileMassField.cs), reachable as `ChunkMap.Mass`) — a sparse per-cell "build mass" table with an N/E/S/W spill cascade (threshold 1, spill share 0.25, max depth 8, exponential decay with prune). A cell that is empty *and* supported commits a sprout via `TryRequestTile`; a cell that is occupied or unsupported **forwards** its unit to neighbours instead. That forwarding is the deliberate change from the old planner: mass flows until it finds somewhere legal to land. It **is** snapshotted (`ChunkMap.CaptureTerrain`/`RestoreTerrain`).
- **The economy** ([Character/BuildMeters.cs](Character/BuildMeters.cs), on `PlayerAbilityState.Meters`, stepped once per frame after the action runs) — three pools: `Build` (reservoir 200, regen 24/s), `BuildMove` (working pool 48, refills from Build at 12/s), and `EruptMove` (charge, max 240, bought from Build at 2:1). Charging ramps to max over 2s, plateaus 0.25s, then overheld charge decays at 60/s while Build bleeds. Per-tile cost comes from `MaterialStrengths.BuildCostFor` — Stone 4.0, Dirt 1.0, Sand 0.5, Foam 0.25 (a 16× spread, vs only 4× on MaxHP). Snapshot-safe via `Clone`/`CopyFrom`.
- **Paint** (`BlockPaintAction`, plain RMB) — spends proportionally and deposits into the mass field. **Eruption is now a release-time upgrade of the paint stroke**, not a separate action: holding charges `EruptMove`, and release spawns a [`MassBall`](Entities/MassBall.cs) — a gravity-free, tile-ignoring projectile that leaks mass into the field as it flies.
- **Place** (`BlockPlaceAction`, Shift+RMB) — all-or-nothing single block.
- **Burst** (`BlockBurstAction`) — LMB press-edge while painting over dead air; uses `ChunkMap.ForceSprout` to place unsupported.
- **Peel** (`BlockGrabAction`, Shift+LMB with the cursor in solid, 6-tile reach) — the block-peel tether. A Gaussian kernel around the cursor admits cells into a group; a spring from cursor to group centre-of-mass pulls with `force = coeff · (dist/TileSize)^power`; force is divided among tether shares, wearing down per-member glue derived from material weight × outward solid edges. Overpull snaps (attempt dies); enough force breaks the group out and it becomes carried blocks, thrown on release as a `LobbedAreaProjectile`. Gated by `MovementConfig.BlockPeelEnabled` (default true; off = legacy drag-rip).

Sprout topology changed to match: parent/child edges are **gone** from [`TileSproutGraph`](World/TileSproutGraph.cs)/`TileSproutNode`, replaced by a `SproutFaces` bitflag — a growing sprout emits one volume per supporting face, with geometry derived rather than stored. `TickSprouts` now runs two passes (commit the whole ring, then promote ghosts) so builds expand as a symmetric shell.

### Input parsing
- [`Controller`](Character/Controller.cs) — 32-frame ring buffer of `PlayerInput` (Left/Right/Up/Down/Space/Shift/F/Num1-4/P/LeftClick/RightClick/MousePosition/MouseWorldPosition). `Poll(mouseWorldPos)` builds one from hardware; `InjectInput` feeds a supplied one (sim/tests). `Capture`/`Restore` for snapshots.
- [`InputParser`](Character/InputParser.cs) — edge-triggered gesture detection: `Click`, `Stab`, `Circle`, `PressEdge`. Snapshot-able (`InputParserState`).
- [`IntentBuffer`](Character/IntentBuffer.cs) — short queue of `ActionIntent`; Peek + explicit Consume, pruned by age.
- [`InputIntent`](Character/InputIntent.cs) — lightweight per-frame intent struct (HeldHorizontal, JumpJustPressed, …) for movement-side use.
- [`SmoothPen`](Character/SmoothPen.cs) — spring-pulled cursor smoother for build-stroke path sampling.

### Surface checkers
[`GroundChecker`](Character/GroundChecker.cs), `CeilingChecker`, `WallChecker`, `ExposedUpperCornerChecker`, `ExposedLowerCornerChecker` — build strip regions via `body.Bounds.StripXxx(thickness)`, call `WorldQuery.SolidShapesInRect`. `EnvironmentContext` caches results within a frame.

### Config
[`MovementConfig`](Character/MovementConfig.cs) hot-reloaded from `movement_config.json` (desktop only, gated by `GameConfig.HotReloadMovementConfig`). Walk/jump speeds, accelerations, frictions, spring constants, sprout lifetime.

## Combat ([World/HitboxWorld.cs](World/HitboxWorld.cs), [HurtboxWorld.cs](World/HurtboxWorld.cs), [CombatSystem.cs](World/CombatSystem.cs), [Hitbox.cs](World/Hitbox.cs), [Hurtbox.cs](World/Hurtbox.cs))

Hitbox-vs-hurtbox model. Per frame: both registries cleared → `IHittable.PublishHurtboxes` populates defensive boxes → action FSM + entity AI publish offensive hitboxes → `CombatSystem.Apply` walks every hitbox × hurtbox; on AABB overlap + faction mismatch + (optional polygon-vs-AABB SAT refinement), dispatches `IHittable.OnHit`. Deduped per `(HitId, Target)` across the broadcast window so a multi-frame slash hits an entity once. The same hitbox also damages tiles via `chunks.DamageCell` — cumulatively, no dedup, so a multi-frame slash progressively chips a tile.

`CombatSystem` is **instance-owned by `Simulation`** (the dedupe table is cross-frame sim state); `CaptureDedupe`/`RestoreDedupe` snapshot it by `HittableId`. `HitTargets.{All, TilesOnly, EntitiesOnly}` filters dispatch.

It also carries two **1-frame inboxes**, both snapshotted alongside the dedupe table:
- `_recoilByHitId` (`PeekRecoil`) — Newton's-third-law back-impulse, so a stab that pogoes off a target recoils the attacker.
- `_entityHitsByHitId` (`PeekHits`) — hit-confirm gating (actions can branch on whether they actually connected).

**Force fields** ([World/ForceField.cs](World/ForceField.cs) + `ForceFieldWorld`) are a third frame-scoped registry — hold/push/grab/throw. Cleared every `Step`, resolved by `ForceFieldSystem.Apply` before physics, and **never snapshotted** (nothing survives the frame).

## Entities ([Entities/](Entities/))

[`Entity`](Entities/Entity.cs) — `IHittable` non-player wrapper around a `PhysicsBody`. Fields: `Health, MaxHealth, Mass, GravityScale, Color, Faction, Sprite, Id`. `PreStep(gravity)` cancels/amplifies gravity by `(GravityScale - 1)`. `OnHit` applies damage + knockback `impulse / Mass`. `Update(dt, player, hitboxes, spawner)` is the AI hook (no-op for passive props). Snapshot via `CaptureState`/`RestoreState` into the `EntityData` value component + virtual `WriteState`/`ReadState`; `Kind` (`EntityKind`) tags the concrete type so `EntityFactory.Rehydrate` can reconstruct a despawned entity on restore.

`IEntitySpawner` (implemented by `Simulation`) lets AI spawn children mid-update and shares the `HitIdAllocator`.

**The enemy layer is now data-driven.** [`EnemyEntity`](Entities/EnemyEntity.cs) runs the same two-FSM shape as the player (movement + action, priority selection) over [`EnemyMovementStates`](Entities/EnemyMovementStates.cs)/[`EnemyActions`](Entities/EnemyActions.cs), driven by a stateless swappable [`EnemyController`](Entities/EnemyController.cs) brain that emits an `EnemyInput`. A new enemy is an [`EnemyBlueprint`](Entities/EnemyBlueprint.cs) + an `EntityKind` registration — **no subclass required**. `BruteEnemy` is kept as the hand-written reference implementation; `StalkerEnemy`/`TurretEnemy` predate the framework and remain as-is.

- [`EntityFactory`](Entities/EntityFactory.cs) — `Balloon` (floating passive target), `Ball` (gravity "crasher" that chips terrain on hard impact), `FloatingBall` (weightless crasher / combat ammo), `PracticeBall`, plus `Stalker`/`Turret`/blueprint enemies.
- [`StalkerEnemy`](Entities/StalkerEnemy.cs) — ground chaser: Chase → Telegraph (visible wind-up) → Lunge (forward hitbox) → Recover, with a Stagger state on hit so knockback isn't clobbered by the AI.
- [`TurretEnemy`](Entities/TurretEnemy.cs) — stationary: Idle → Charging (aim locks, dodgeable line of fire) → fires a `BulletProjectile` at the player's current position → Cooldown; Stagger on hit.
- [`MassBall`](Entities/MassBall.cs) — the eruption payload. A `Projectile` with `IgnoreTiles`, zero gravity and light drag, leaking build mass into `ChunkMap.Mass` every frame. Spawned only from `BlockPaintAction.Exit`.
- [`Projectile`](Entities/Projectile.cs) base + concrete `BulletProjectile`, `EnergyBallProjectile`, `StickyGrenadeProjectile`, `LobbedAreaProjectile` — travel + publish hitboxes; lifetime/fuse handled by the base. `LobbedAreaProjectile` captures eruption mode + block type at launch and runs a planner on detonation.

## Drawing ([Drawing/](Drawing/))

[`DrawContext`](Drawing/DrawContext.cs) wraps `SpriteBatch` + 1×1 pixel, exposes `Line/Rect/Ring/Disc/RotatedRect`. [`Sprite`](Drawing/Sprite.cs) is a `Pose`-based vector sprite; `AnimatedSprite` adds frame timing. [`ParticleSystem`](Drawing/ParticleSystem.cs) is a fixed-capacity pool (2048 in Game1). [`Trail`](Drawing/Trail.cs) is a fading ribbon (cursor trail + slash tip trails). [`Effects`](Drawing/Effects.cs) — preset spawners (`TileBreak`, `Puff`). **All drawing is cosmetic and downstream of the sim.**

The stack has grown well past that base layer:

- **Character rendering** — `SkeletonMetaballRenderer` + `DensityField` + `PrimitiveBatch` drive a RenderTarget density-field pipeline with segment-metaball shaders (`Content/CapsuleSplat.fx`, `Content/MetaballComposite.fx`) for blobby bone rendering.
- **Sprite skin** — [`SpriteSkin`](Drawing/SpriteSkin.cs) + [`SpriteBinding`](Drawing/SpriteBinding.cs) + [`MlsDeformer`](Drawing/MlsDeformer.cs): hand-drawn multi-layer PNG artwork MLS-deformed over the rig, per-player bindings (see `SpriteBindings/`, art in `SkeletonAssets/`).
- **Glow / VFX** — `GlowRenderer`, `GlowTrailField`, `AttackGlowSystem`.
- **World & UI** — `ChunkRenderer`, `TilePalette`/`TileTextureAtlas`, `ParallaxBackground`, `HudRenderer`, `DebugOverlayRenderer`, `DevDemoRenderer`, `ScreenshotSystem`.
- **`CosmeticUpdateSystem`** — the extracted per-frame cosmetic update (sprite sync, secondary animators), keeping that logic out of `Game1`.

## Animation ([Animation/](Animation/))

Render-only, and hot-reloadable freely for that reason. The rig is a pure joint chain (`Drawing/Skeleton.cs`, rigs in `Skeletons/*.json`, clips in `SkeletonStates/<rig>/`, loaded by `SkeletonStore`).

[`CharacterAnimator`](Animation/CharacterAnimator.cs) is the hub (plus `.Constraints` and `.Diagnostics` partials): it selects and blends clips (`AnimationSampler`, `AnimAdditionSampler`, `OverlayStack`, `BoneMask`, `SkeletonComposition`) and then runs a **generalized box-bounded Levenberg–Marquardt least-squares solve** ([`LeastSquaresSolver`](Animation/LeastSquaresSolver.cs)) over clip times, CoM offset, and joint corrections. Constraints are a composable `IConstraint` library — `FixedPoint`/`ExternalPin` (foot plant, climb hand grip), half-plane `NoPenetration` against `TerrainSurfaces`, and `ActionAimConstraint` (re-aims the stab overlay along the runtime input direction). `PoseIk` and `MoveDriver` sit alongside; tuning lives in `anim_solver_config.json`.

### Clip binding

Clips are selected by the JSON **`Type`** field — never the filename or `Name` (`climbhands.json` plays as the `ClimbHands` overlay). Movement goes `MovementState.AnimationTag → AnimTag → IMoveDriver → AnimClip → clip`, where tag→clip is a hardcoded table ([MoveDriver.cs:89-106](Animation/MoveDriver.cs#L89-L106)) and *not* name matching; actions bind by exact ordinal match of the ActionState **class name** to `Type`. **[Plans/ANIMATION_BINDING_MAP.md](Plans/ANIMATION_BINDING_MAP.md) is the full state→clip→arc table** — read it before renaming anything, and note that the old `Vault` naming overload was retired 2026-08-04 (there is no `VaultState`; the clip is `parkour.json`).

[`MotionProbe`](Animation/MotionProbe.cs) converts joint angles to world positions — **use it rather than eyeballing angles** when debugging clips (this is what the `anim-probe` skill and `MTile.Probe` drive).

**Clip binding is by the JSON `Type` field, not the filename** — `Type` either parses as an `AnimClip` enum member (movement clip) or matches an `ActionState` class name exactly (upper-body overlay). There is no compile-time link, so [ClipBindingTests.cs](MTile.Tests/Animation/ClipBindingTests.cs) enforces it in both directions: every `AnimClip` member needs a file **in every rig** (a missing one throws the first frame a driver selects it), every concrete `ActionState` needs a matching clip (a missing one *silently* drops the overlay), and every action-typed clip needs a class. It also enforces the pacing contract: an action must either override `AnimationProgress` or bind a looping clip. [Plans/ANIMATION_BINDING_MAP.md](Plans/ANIMATION_BINDING_MAP.md) is the authoritative map of all three namespaces.

**Action/movement progress** are two mirrored render-only channels: `ActionState.AnimationProgress` (default `-1f` = decline, requires a looping clip) and `MovementState.AnimationProgress`. Both land in `CharacterAnimSample`; the animator remaps the overlay clip onto `ActionProgress`, and `ClipTimeMode.Progress` maps onto `MovementProgress`.

> **"Vault" is retired as a name.** `VaultState` never existed — the at-speed one-block climb is `ParkourState`, and `MantleState` is its at-rest complement. Clips renamed accordingly (`vault.json` → `mantle.json`, `vaulthands.json` → `climbhands.json`), along with config keys (`VaultLiftForce` → `LipLiftForce`, `CorrectorVault*` → `CorrectorClimb*`). Sim test fixture names deliberately kept "vault".

## Recording ([Recording/](Recording/))

`GameRecorder` — in-game animation take capture and scrub (Ctrl+R record, Ctrl+P playback). `AnimTake` serializes the `CharacterAnimSample` stream to `Takes/*.take.json` for replay in the standalone viewer (`MTile.Demo -- --load`). `AnimTraceLogger` (Ctrl+L) dumps CSV traces.

## Render shell ([Game1.cs](Game1.cs))

`Initialize`: load `GameConfig`, resolve the `Stage`, load `MovementConfig` (+ desktop hot-reload watcher), construct `Simulation`, subscribe `OnPlayerRespawn`/`OnTileBroken` to cosmetic particle spawners.

`Update`: read keyboard/mouse, compute `mouseWorldPos` from the camera, `Controller.Poll` → `_sim.Step(input)` (or `_session.TryStep()` when a `NetSetup` is present). Then **cosmetic-only**: cursor trail, sync sprites to bodies + advance animations, air→ground landing puff, particles, camera tracking. None of this writes sim state.

Also in the shell: a `TimeScale` slow-mo accumulator, a freeze-frame corrector inspector driven by `Testing/freeze.json`, Ctrl+M stage save / F5 reload via `StageSaver.cs` (both disabled under a rollback session), and a worst-of-60-frames timing probe. **`Game1` is not actually thin** — ~870 lines, mostly render and HUD; `Plans/GAME1_REFACTOR_PLAN.md` tracks the remaining extraction.

`Draw`: world transform from camera → chunks (damage-darkened) → platforms → growing sprouts → entities → players → particles/cursor trail → current action overlay → debug overlays (hitboxes, hurtboxes, orientation, constraints, health bars, gated by `GameConfig` toggles) → screen-space UI (state/action names, planner mode, block-picker HUD, health bars).

## Tests ([MTile.Tests/](MTile.Tests/))

xUnit. Categories:
- `PhysicsTests`, `GroundFrictionTests`, `MovingPlatformTests`, `JumpingStateTests` — physics/movement units.
- `Sim/` — scenario-driven simulation tests with deterministic ascii-terrain + scripted input (`SimRunner`, `SimTerrain`, `InputScript`, `SimReport` CSV diffing). `SimRunner.Run` mirrors `Simulation.Step`'s phase order; `SimRunner.RunMulti` runs multiple players sharing terrain + combat registries for cross-player combat tests.
- `SnapshotRoundTripTests` — the rollback gate: snapshot at frame K, run to N, restore K, re-run to N, assert identical traces (incl. terrain — a ball chipping the floor and a foam build straddling the snapshot both replay bit-for-bit). Alongside it: `RollbackHarnessTests`, `InputCodecTests`, `RtcConnectionTests`, `TwoPlayerStepTests`.
- `Animation/` — ~25 files covering the solver (`AnimSolverTests`, `FixedPointSolverTests`, `NoPenetrationSolverTests`, `ParkourGripSolverTests`, `ActionAimSolverTests`), blending/overlays, rig/IK, and sprite skin. Files prefixed `Zzz` are slow soak tests, named to run last.

~470–480 cases across ~100 files. **7 are `Skip`ped, all deliberately**: 4 pending an impact-break retune (the R=12 body impact spread), 1 needing the StandServo root, 1 on the crouched reflex-vault band bug, 1 an assert-free diagnostic sweep.

See `CLAUDE.md` for build/run/test commands and the file-lock gotcha.

## Key conventions

- **Right-handed coords with Y-down** (MonoGame default). World gravity `(0, 600)` px/s² (`Simulation.Gravity`). Tile coords `gtx, gty` are integer global cell indices; cell centers are `gtx * Chunk.TileSize + Chunk.TileSize/2` (nominally 16px tiles — the codebase is parameterized on `Chunk.TileSize`, but px overrides in `movement_config.json` do NOT scale with it).
- **Forces are accelerations**: `PhysicsBody` has no mass; `body.Velocity += body.AppliedForce * dt`. Mass appears only in `ImpactDamage` and `Entity`/player knockback.
- **Modifier scalars are multiplicative on baseline config**, not absolute (`m.MaxWalkSpeed *= 0.6f`).
- **Priorities form bands** — see [MovementPriorities.cs](Character/MovementPriorities.cs) for the authoritative numbers. Roughly: free/ground 0–20, stun 25, Tumble 51/26, climb family 29/29, jump passives 30–48, holds 42–44, launch actives 50–60. Preemption compares the **candidate's Passive** to the **current state's Active** — get that backwards and bands look right while behaving wrong.
- **Per-activation FSM state is plain data** in `MovementVars`/`ActionVars`; the state/action objects are stateless flyweights. This is what makes snapshot a struct copy.
- **`HitId` is monotonic per `Simulation`** via `HitIdAllocator`. CombatSystem dedupes by `(HitId, Target)` so multi-frame hitboxes land once per entity but apply cumulatively to tiles.
- **The sim is deterministic and snapshot-restorable**; render systems are strictly downstream and must never feed back. Terrain mutations funnel through `ChunkMap.WriteTile`/`GetOrCreateChunk` so the journal stays complete.
- **World reactions go through events** (`OnTileBroken`, `OnPlayerRespawn`), not polling.

## Where to start when extending

| I want to… | Look at |
|---|---|
| Add a new player ability | New `ActionState` subclass; add to `_actionRegistry` in [PlayerCharacter.cs](Character/PlayerCharacter.cs); put per-activation fields in `ActionVars`; pick priorities in the right band |
| Add a new movement state | Subclass `MovementState`, add to `_stateRegistry`, put per-activation fields in `MovementVars`, null any soft-contact cache in `ResetTransient`; **add its priorities to [MovementPriorities.cs](Character/MovementPriorities.cs)** and reason about Passive-vs-current-Active, not band membership |
| Add a new enemy | Prefer an [`EnemyBlueprint`](Entities/EnemyBlueprint.cs) + `EntityKind` — no subclass. `BruteEnemy` is the hand-written reference if you need one |
| Add a new projectile | Subclass `Projectile`, add an `EntityKind` + `Rehydrate` case + `WriteState`/`ReadState`; spawn via `Stage.Populate` or `ctx.Spawner` |
| Tune how a hit *feels* | Percent contribution is the hitbox's `Damage`; knockback shape is `HitResolver` + `impact_profiles.json`; HP loss is the crush path (`CrushImpulseThreshold`) |
| Author or debug an animation clip | `MTile.Probe` CLI (`list/new/addkey/ik/contact/rot/retime/…`) against `SkeletonStates/<rig>/`; read geometry with `MotionProbe`, never by eyeballing angles |
| Add a snapshotted per-entity field | Put it in the `EntityData`/`PlayerData` value component in `Sim/ECS/Components/`, not on the live object |
| Make an entity crashable | Set `body.Impact = new ImpactDamage { … }` in its factory |
| Add a new tile type | Extend `TileType`, add HP in `TileDamage.MaxHPFor`, color in `Game1.GetTileBaseColor` (+ picker key if player-selectable) |
| Add a new dynamic surface | Implement `ISolidShapeProvider`, register via `Simulation.AddPlatform`; provide `.Velocity`; drive motion from `_elapsed` for snapshot safety |
| Add a new stage | Register a `Stage` in `Stages` with a `Populate` delegate; add its `Levels/*.json` |
| Make a change snapshot-safe | Put mutable state in a value struct or a `Capture`/`Restore` pair; route terrain writes through `ChunkMap`; verify with a `SnapshotRoundTripTests` case |
| Add a feedback effect on a game event | Fire an event from the sim, subscribe in `Game1`, spawn via `Effects` (never mutate sim from the handler) |
| Tune movement | Edit `movement_config.json` — hot-reload picks it up (desktop) |
| Tune building / eruption | [BuildMeters.cs](Character/BuildMeters.cs) for the economy, [TileMassField.cs](World/TileMassField.cs) for spill/decay, `BuildCost` in `material_strengths.json` for per-material cost |
| Tune the block peel | `Peel*` constants in `BlockGrabAction` ([ActionStates.cs](Character/ActionStates.cs)); `MovementConfig.BlockPeelEnabled` toggles legacy drag-rip |
| Add an `ActionState` | You **must** author a clip whose `Type` equals the class name, or `ClipBindingTests` fails; an action that declines `AnimationProgress` needs a *looping* clip |
| Tune impact / crush damage | `ImpactDamage` in [EntityFactory.cs](Entities/EntityFactory.cs) / [PlayerCharacter.cs](Character/PlayerCharacter.cs) |
