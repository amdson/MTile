# Goal 4 — Data-Oriented Refactor + Snapshot/Restore

Make the whole simulation (players + entities, **not** terrain) capturable into a
snapshot and restorable exactly, by moving per-activation FSM state out of the
flyweight state/action objects into per-player **plain data**. This is the
rollback prerequisite and the first concrete step toward the eventual ECS.

Terrain (chunks/tiles) is explicitly **out of scope** here — it gets a journal/
revert mechanism in goal 6. Snapshot covers everything else.

---

## The constraint problem: only "maintained" contacts are durable state

Movement states hold **references to constraint objects that are simultaneously in
`Body.Constraints`** and mutate them each frame — naively snapshotting fields would
split the shared object in two. But the resolution falls out of a key fact verified
against `PhysicsWorld.cs`: **there are two kinds of constraint, and only one is
durable state.**

1. **`SurfaceDistance` — hard contacts (durable).** Created by the *solver* in
   `UpdateSurfaceConstraint` during `StepSwept`, persisted across frames by the
   `Constraints.RemoveAll(...)` prune (a resting body doesn't re-collide, so it
   isn't regenerated — it genuinely persists). This is the "is the surface contact
   maintained for this body" case. **No state holds a reference to these** — the
   solver owns them → zero aliasing.

2. **`FloatingSurfaceDistance` / `SteeringRamp` / `PointForceContact` — soft contacts
   (derived).** Added by states, and every one is **fully re-derivable** each frame
   from `{state scalars + PlayerAbilityState (e.g. GrabbedCorner) + body pose + a
   world query}`:
   - `Standing/Crouched._ground` ← `TryGetGround`; `Jumping/RunningJump._source` ←
     `TryFindSource`; `WallSliding._wall/_ground` ← queries; `Parkour` already
     rebuilds its ramps every frame via `Reconcile`; `CoveredJump/Dropdown._ramp`
     ← dir scalar + corner query; `LedgeGrab/LedgePull` pins ← `GrabbedCorner`.
   - None carry information that isn't reconstructible.

### Resolution: `Maintained` flag — snapshot only durable contacts, rebuild the rest

Add `bool Maintained` to `PhysicsContact` (default false). The solver sets it `true`
on the `SurfaceDistance` contacts it persists; state soft contacts leave it false.

- **Snapshot** copies only `Maintained` contacts (deep copy — trivial, no aliasing).
- **Restore** drops all non-maintained contacts.
- States acquire their soft contact **idempotently** (create-if-missing at the top of
  `Update`, not only in `Enter`). In normal play the contact already exists → no-op,
  zero behavior change. On the first frame after a restore (or fresh entry) it
  rebuilds before `StepSwept` reads it.
- Corollary: the FSM clears all non-maintained contacts on **every** state transition
  and the new state re-establishes its own in `Update` — unifying the restore path
  with the normal transition path, so **no state holds a cross-frame constraint
  reference** (exactly what the plain-data refactor wants). No `ConstraintRole`
  machinery needed.

**Caveat — constraint ordering (determinism):** rebuilt soft contacts may land in a
different list position than the original relative to restored hard contacts, and the
`StepSwept` constraint loop applies normal/friction impulses in list order. Usually
self-correcting (one floor contact zeroes `vnRel`, the other sees `vnRel≈0` and
skips), but if goal 5's determinism test shows drift, apply contacts in a canonical
order (sort by normal then position) instead of list order.

---

## Inventory of mutable simulation state

### A. `PlayerCharacter` top-level (Character/PlayerCharacter.cs)
| Field | Type | Notes |
|---|---|---|
| `Body` | PhysicsBody | see §B |
| `_frame` | int | monotonic |
| `_hitInvulnRemaining` | float | |
| `_lastCrushFrame` | int | |
| `Health` | float | |
| `Faction` | enum | |
| `_abilities` | PlayerAbilityState | see §E |
| `_currentState` / `_currentAction` | ref → registry | store as **registry index** |
| `_stateHistory[32]` + `_historyHead` | ring of state refs | store as index ring |
| `_actionHistory[32]` + `_actionHistoryHead` | ring of action refs | store as index ring |
| `_inputParser` | InputParser | see §E |
| `_intents` | IntentBuffer | see §E |
| `_activeBlockType` | TileType | |
| `_eruptionMode` | EruptionPlannerMode | |
| `_wasPDown` | bool | |
| `LastTilePlacedFrame` | int | |
| `HitIds` | HitIdAllocator | **shared** — snapshot once at sim level, not per player |
| `Sprite` | AnimatedSprite | **render-only — EXCLUDE** |

Registry note: states/actions are flyweight singletons constructed in a fixed order
in the ctor. Snapshotting `_currentState` as its index into `_stateRegistry` is
stable across snapshot/restore because the registry is rebuilt identically.

### B. `PhysicsBody` (Physics/PhysicsBody.cs)
| Field | Type | Snapshot |
|---|---|---|
| `Position`, `Velocity`, `AppliedForce` | Vector2 | copy |
| `LastImpulseMagnitude`, `FrictionScale` | float | copy |
| `Polygon` | Polygon | shape — immutable after construction, share/skip |
| `Impact` | ImpactDamage | config — immutable after construction, share/skip |
| `Constraints` | List<PhysicsContact> | **deep copy only `Maintained` (hard) contacts**; soft contacts rebuilt by states (see §central-problem) |

### C. Constraint hierarchy (Physics/PhysicsContact.cs, Character/SteeringRamp.cs)
All reference types with plain fields; deep-copy each, preserve list order.
| Type | Mutable fields |
|---|---|
| `SurfaceContact` (base) | Position, Normal, MinDistance, SurfaceVelocity, Friction |
| `SurfaceDistance` | (inherits; auto-created by collision resolver) |
| `FloatingSurfaceDistance` | (inherits; state-owned) |
| `PointForceContact` | Position |
| `SteeringRamp` | Corner, Sense, ForwardDir, ThetaStar, SurfaceDir, BannedDir, Weight, MaxSpeed |

After adding `ConstraintRole`, each also carries its role tag.

### D. Movement states (Character/Movement.cs, Character/MovementStates.cs)
Only the **scalar** fields are snapshotted. `_wallDir`/dir ctor args are **immutable
config** (the registry holds one instance per direction) — exclude. Constraint refs
(soft contacts, ✎) are **not** snapshotted — rebuilt idempotently by the state's
`Update` per §central-problem; listed here only to mark which `Update`s need the
create-if-missing change.

| State | Scalar fields (snapshot) | Soft contacts to rebuild (✎, not snapshotted) |
|---|---|---|
| StunnedState | — | — |
| FallingState | — | — |
| StandingState | — | `_ground` |
| CrouchedState | — | `_ground` |
| JumpingState | `_jumpReleased`, `_timeInState` | `_source` |
| RunningJumpState | `_jumpReleased`, `_timeInState` | `_source` |
| WallSlidingState | — | `_wall`, `_ground` |
| WallJumpingState | `_timeInState`, `_jumpReleased` | — |
| DoubleJumpingState | `_jumpReleased`, `_timeInState` | — |
| CoveredJumpState | `_openDir`, `_slideSpeed`, `_phase`(enum), `_slideTime`, `_jumpHoldTime`, `_jumpReleased` | `_ramp`, `_ground` |
| ParkourState | `_entrySpeed` | `_overRamp`, `_underRamp` (already `Reconcile`d each frame) |
| LedgeGrabState | `_timeInState` | `_wall`, `_floor` (from `GrabbedCorner`) |
| LedgePullState | `_timeInState` | `_spring`, `_ramp` (from `GrabbedCorner`) |
| DropdownState | `_dropDir`, `_slideSpeed`, `_slideTime`, `_exitingAirborne` | `_ramp` |
| GuidedState (abstract) | `_lastProgressT`, `_stalledFrames`, `Path`, `ProgressT`, `_safety`(List<FSD>) | **DEAD CODE — no subclasses; delete or confirm before planning around it** |

### E. FSM helper objects (per player)
| Object | Mutable state | Snapshot |
|---|---|---|
| `PlayerAbilityState` | TimeInState, HasDoubleJumped, Jump/Up/DownJustPressed, IsLedgeGrabbing, GrabWallDir, GrabbedCorner, Facing, SlashInterrupted | plain copy |
| `ConditionState` | already pure data (8 bool/int + Block* fields) | plain copy |
| `CombatState` | already pure data (hitstun/stun/guard flags + frames) | plain copy |
| `IntentBuffer` | `_intents` List<ActionIntent> (struct) | copy list |
| `InputParser` | `_activePressFrame`, `_activePressMouse`, `_cumAngle`, `_lastDir`, `_hasLastDir`, `_lastPressEdgeEmitted`, `_lastClickEmitted`, `_lastStabEmitted`, `_lastCircleEmitted` | plain copy |
| `Controller` | `_inputBuffer[32]` ring + `_currentIndex` | copy ring (or re-feed from netcode input log) |
| `SmoothPen` | Position, Velocity | plain copy (used by BlockEruptionAction) |

### F. Action states (Character/ActionStates.cs)
Trails are **render-only — EXCLUDE** (`_trail`, `_tipTrail`). `_simResult`,
`_lastBeamDir`, `_lastBeamReach` are Draw-only caches — recomputed each Update,
safe to EXCLUDE.

| Action | Scalar/data fields to extract |
|---|---|
| NullAction, RecoveryAction, GuardAction | — |
| ReadyAction | `_timeInState`, `_isGrounded`, `_facing` |
| SlashLikeAction (base; covers GroundSlash1/2/3, CrouchSlash, AirSlash1/2, AirTurnSlash, GuardRetaliateAction) | `_timeInState`, `_slashDir`, `_hitId` |
| StabAction (+ AirSpinStab) | `_timeInState`, `_stabDir`, `_initialStabAngle`, `_isGrounded`, `_hitId`, `_boost`, `_blockReach`, `_tipExt` (`_blockPoly` is derived geometry — recompute) |
| PulseAction | `_timeInState`, `_isGrounded`, `_hitId` |
| BlockReadyAction | `_chargeTime`, `_originCell`, `_inSolidLastFrame` |
| BlockEruptionAction | `_chargeTime`, `_timeInState`, `_origin`, `_pen`(SmoothPen), `_samples`(List<PathSample>) |
| EnergyBallAction | `_timeInState` |
| BeamAction | `_chargeTime`, `_firingTime`, `_firing`, `_hitId` |
| LobbedAreaAction | `_chargeTime`, `_cursorAtPress` |
| GrenadeAction | `_timeInState` |

### G. Entities (Entities/*.cs)
Polymorphic; need a stable **EntityId** + per-type serialization, plus the ability
to restore the live set (recreate despawned, drop spawned-ahead). Sprites EXCLUDE.

| Type | Base fields (Entity) | Subtype fields |
|---|---|---|
| Entity (base) | Body(§B), Health, MaxHealth, Mass, GravityScale, Color, Faction | — |
| Projectile (base) | + Age, Lifetime | — |
| StalkerEnemy | | `_state`(enum), `_stateTime`, `_facing`, `_hitId` |
| TurretEnemy | | `_state`(enum), `_stateTime`, `_aim`(Vector2) |
| BulletProjectile | | `_hitId`, `_hitIds`(shared ref — don't copy) |
| EnergyBallProjectile | | `_hitId` |
| StickyGrenadeProjectile | | `_hitId`, `_stuck`, `_stuckSince`, `_exploded` |
| LobbedAreaProjectile | | `_hitId`, `_budget`, `_tileType`, `_mode`, `_detonated` (last 3 immutable) |

### H. Simulation-level (Simulation.cs)
| Field | Snapshot |
|---|---|
| `_hitIds` (HitIdAllocator) | single int (`Value`) |
| `_combat` (CombatSystem) | `_hitDedupe` dict (HitId → set of IHittable) — **needs IHittable identity by id**, see open questions |
| `_entities`, `_bodies`, `_hittables` | rebuild from entity snapshots |
| `_lastTilePlacedFrame` | int |
| `_stageTickers` / `_platforms` | platform positions (MovingRectangle.Position/Velocity) — **moving platforms are sim state**; their ticker closures (`t1`, `t2` accumulators in Stage.cs) are hidden state → must be made snapshot-friendly (store accumulator, or make platform motion a pure function of frame) |

### I. Render-only — never snapshot, must never feed back into sim
Particles, `_cursorTrail`, `Camera`, all `Sprite`/`AnimatedSprite`, action `Trail`s,
Beam/MassBall Draw caches, `_wasGroundedLastFrame`.

---

## Snapshot/Restore architecture

- `SimSnapshot` — a POCO holding plain arrays/values for everything above. No
  references into live objects.
- `Simulation.Snapshot() : SimSnapshot` and `Simulation.Restore(SimSnapshot)`.
- Per-player: `PlayerSnapshot` (body, abilities, FSM indices+history, helper data,
  the movement/action data blobs). Per-entity: tagged `EntitySnapshot`.
- Pooling: snapshots are taken every frame in rollback; design for reuse (struct
  buffers / object pool) to avoid GC churn. Not required for first correctness pass.

### The data-oriented shape (what "plain data" means here)
For each FSM, collect the extracted fields into a per-player struct:
- `MovementVars` — superset of all movement-state fields (small: ~6 floats, a few
  ints/bools/enums, plus the constraint **roles are in Body.Constraints, not here**).
- `ActionVars` — superset of all action-state fields.

Only the active state's slice is meaningful; states read/write `ref vars`. Because
the structs are flat value types, snapshot = struct copy. This is the ECS-friendly
arrangement (vars become components later).

States/actions become **stateless logic**: `Enter/Update/Exit(ctx, ref abilities,
ref vars)`. The signatures change across all ~14 action + ~12 movement classes —
this is the bulk of the mechanical work.

---

## Determinism hazards already handled (goals 1-3) / still open
- Handled: fixed dt, input via PlayerInput, HitId/planner/CombatSystem statics,
  config-watcher gate.
- Still to verify under snapshot: `Dictionary` iteration determinism (chunks, combat
  dedupe, score maps in EruptionPlanner) — fine within same build, but the combat
  dedupe set holds `IHittable` references; restoring it needs entity identity by id.
- Float NaN/Inf hygiene (don't normalize zero vectors) — spot-check planners/aim.

---

## Staged execution plan (each stage builds + passes existing tests)

1. ~~**Maintained flag + idempotent soft-contact acquisition.**~~ **DONE.** Added
   `bool Maintained` to `PhysicsContact`; solver sets it on the `SurfaceDistance`
   contacts it persists (`UpdateSurfaceConstraint`). Every stateful movement state's
   soft-contact creation extracted into an idempotent `Ensure…` method called from
   both `Enter` and the top of `Update` (Standing, Crouched, Jumping, RunningJump,
   WallSliding, CoveredJump, LedgeGrab, LedgePull, Dropdown; Parkour already
   `Reconcile`s). No-op in normal play; rebuilds after a restore drops soft contacts.
   Build clean, suite at baseline (88/89, the one fail pre-existing). Notes:
   - Phase-gated rebuilds: CoveredJump (`_phase == SlidingOut`), LedgePull (`_ramp`
     only while body hasn't risen past the corner) — so a restore taken after the
     contact was dropped doesn't wrongly re-add it.
   - CoveredJump/Dropdown re-derive the ramp corner from the dir scalar via the same
     checker `TryPickOpenDir`/`TryPickDropDir` used; identical at enter time.
   - The `Ensure…`-in-`Update` create branches are dead in normal play, so they're
     first *exercised* by the goal-5 restore test — that's the real validation gate.
   - FSM-clear-non-maintained-on-transition deferred to the snapshot stage (only
     needed once Restore exists; Exit-removal still handles normal play).
2. ~~**Delete or confirm `GuidedState`**~~ **DONE.** Confirmed no subclasses; deleted
   `Character/GuidedState.cs` and Game1's dead `is GuidedState` debug-draw blocks +
   `DrawGuidedPath`. Kept `GuidedPath` (immutable utility the content roadmap reuses
   when Parkour becomes a guided state). Build clean, suite at baseline.
3. ~~**Plain-data movement.**~~ **DONE.** Added `MovementVars` (scalar superset) +
   namespace-level `CoveredJumpPhase`. Moved every movement state's scalar fields
   into it; lifecycle methods now take `ref MovementVars` (CheckPreConditions kept
   the lean signature — candidates never read the active vars). `PlayerCharacter`
   owns one `_moveVars` and threads it through the movement FSM loop. Soft-contact
   refs stay as transient instance caches (rebuilt by the stage-1 `Ensure…` methods);
   only re-derivable scalars are in the snapshot blob. Build clean, suite at baseline.
   NOTE: states are now *almost* stateless — the only remaining per-player instance
   data is the transient contact-ref caches, which a restore must null so `Ensure…`
   rebuilds (handled in the snapshot stage, alongside FSM-clear-on-transition).
4. ~~**Plain-data actions.**~~ **DONE.** Added `ActionVars` (scalar superset). Moved
   every action state's value-type fields into it; lifecycle methods now take
   `ref ActionVars` (CheckPreConditions stayed lean) and the read-only hooks
   (`ApplyMovementModifiers`/`ApplyActionForces`/`Draw`) take it by `in`. `Draw` now
   reads its sim state from the vars — `PlayerCharacter` exposes `CurrentActionVars`
   and `Game1` passes it through. `PlayerCharacter` owns one `_actionVars` threaded
   through the action FSM loop. Render-only caches stay on the (now logic-only)
   instances: `Trail _trail`/`_tipTrail`, `BeamAction._lastBeamDir/_lastBeamReach`,
   `BlockEruptionAction._simResult`. `Polygon BlockPoly` (immutable) lives in the
   struct directly. Build clean (all 4 projects), suite at baseline.
   NOTE: one residual reference-type per-activation buffer — `BlockEruptionAction._pen`
   (SmoothPen) + `_samples` (List<PathSample>) — is neither value-copyable nor cheaply
   re-derivable; it stays an instance field and needs a **deep-copy at snapshot time**
   (stage 6), not the trivial struct copy.
5. ~~**Entity ids + snapshots.**~~ **DONE.** Added stable `int Id` (+ `IHittable.HittableId`)
   assigned by `Simulation` from a deterministic `_nextId` counter (players first, then
   entities in spawn order). Per-type snapshot via a flat superset `EntitySnapshot`
   (value struct; reference members are only immutable Polygon/Impact + deep-copied
   Maintained contacts) with `EntityKind` tag. `Entity.Capture()`/`RestoreInto()` fill
   the shared fields and chain to virtual `WriteState`/`ReadState` (Projectile adds
   Age/Lifetime; Stalker/Turret add AI state; each projectile its hit-id/fuse/budget).
   `EntitySnapshot.Rehydrate(hitIds)` reconstructs a despawned entity via its gameplay
   ctor (immutable refs/config restored) then `RestoreInto` overwrites all dynamic
   fields. Shared `BodyState` (Physics/BodyState.cs) captures pose + kinematics +
   deep-copied **Maintained** contacts only (soft contacts rebuilt by states).
   NOTE: a rehydrated Generic entity (balloon/ball) gets no Sprite — render-only, Game1
   falls back to the polygon outline; cosmetic, never feeds the sim.
6. ~~**`SimSnapshot` + `Simulation.Snapshot()/Restore()`.**~~ **DONE.** `SimSnapshot` POCO
   holds sim scalars (HitId counter, id counter, last-tile-placed frame, platform
   clock `_elapsed`), per-player `PlayerSnapshot` (+ each player's `ControllerState`
   ring), `EntitySnapshot[]` in spawn order, the combat dedupe table keyed HitId→
   HittableId[], and `PlatformState[]`. `PlayerCharacter.CaptureState/RestoreState`
   store the FSMs as registry indices (+ history rings as index rings), the
   MovementVars/ActionVars blobs, a cloned `PlayerAbilityState` (incl. Condition/Combat),
   `InputParser`/`IntentBuffer` state, and the BlockEruption gesture buffer
   (`_pen`+`_samples` deep copy via `EruptionGestureState`). Restore drops soft contacts
   (BodyState keeps only Maintained) and calls `MovementState.ResetTransient()` on every
   registry state to null stale soft-contact ref caches so the idempotent `Ensure…`
   rebuilds them next frame. Combat dedupe restores by resolving HittableIds against the
   rebuilt live set. Moving-platform tickers refactored to a **pure function of
   `_elapsed`** (Stage.cs + Simulation), so platforms snapshot with no hidden closure
   accumulator. Added a headless `Simulation(ChunkMap, spawn, populate)` ctor for tests.
7. ~~**Determinism round-trip test.**~~ **DONE** (delivered alongside 5/6 as the gate).
   `MTile.Tests/Sim/SnapshotRoundTripTests.cs`: run→snapshot@K→run→restore→re-run with
   identical inputs, asserting an **exact (raw float-bits)** per-frame trace of players +
   all entities matches frame-for-frame. Covers a falling ball, a chasing stalker, and a
   turret that **fires a bullet after the snapshot frame** (so the drop-and-recreate
   path is exercised), plus a cross-sim restore (snapshot from sim A replayed on a
   separately-advanced sim B). Both pass; suite at 90/91 (the one fail pre-existing).

Milestone: **reached** — the world (minus terrain) round-trips bit-for-bit.

**Terrain (roadmap goal 6) — also DONE** (follow-on to this plan). The dense tile grid
is rolled back via a `TerrainJournal` (inverse-recoverable deltas recorded at the
single `ChunkMap.WriteTile`/`GetOrCreateChunk` choke points that every mutator funnels
through); the sparse side-structures that tick every frame (sprout graph ages, foam
timers, per-cell HP, impact accumulator) are value-snapshotted. `ChunkMap.CaptureTerrain/
RestoreTerrain` combine the two (rewind journal → restore sparse → re-link sprout→tile
refs); wired into `SimSnapshot.Terrain`. Same-instance only (journal marks are
instance-relative). The whole world — players, entities, **and terrain** — now
round-trips bit-for-bit, verified by `MTile.Tests/Sim/SnapshotRoundTripTests.cs`.

---

## Open questions to settle before/within execution
- **Combat dedupe restore:** `_hitDedupe` maps HitId → `HashSet<IHittable>`. To
  snapshot/restore it we need stable entity identity. Resolve via the EntityId from
  stage 5 (store sets of ids, resolve to live objects on restore). Player(s) need
  ids too. Confirm this is acceptable.
- **MovementVars as one superset struct vs. per-state structs:** superset is simplest
  and snapshot-trivial; per-state is cleaner for ECS. Recommend superset now, split
  later.
- **Moving-platform tickers:** make platform motion a pure function of frame number
  (cleanest for snapshot) vs. snapshot the closure accumulators. Recommend the former.
- **Controller buffer:** snapshot the 32-frame ring, or rely on the netcode re-feeding
  inputs on restore? For a self-contained Snapshot/Restore + test, snapshot the ring.
