# ECS Migration Plan

Migrate MTile's entity model from OO (`PlayerCharacter`, `Entity` subclasses,
`IHittable` interface, direct C# references) to a hand-rolled Entity-Component-
System with `EntityId` values and component stores. Driven by:

- **Rollback netcode prerequisites.** Today's snapshot/restore is per-type and
  built around the OO hierarchy (`EntitySnapshot.Rehydrate`, `PlayerSnapshot`,
  `EntityKind` discriminant). A component-keyed world snapshot is a much cleaner
  rollback substrate, and the per-component snapshot format extends to future
  components for free.
- **Cross-entity references are footguns.** `Hitbox.Source` (an `object`),
  `Hurtbox.Target` (an `IHittable`), `CombatSystem._hitDedupe` (holds
  `IHittable` refs), projectile homing targets — all of these go stale when an
  entity is destroyed or restored. Replacing them with `EntityId` (an index +
  generation) makes "is this still alive?" a single bit-check.
- **The roadmap's next features fight the current model.** Attack recoil
  (Newton's-third-law back-impulse) wants a stable reference from the hit-site
  back to the attacker; AI targeting wants the same; multi-frame projectile
  bookkeeping wants it too. Each new feature adds another direct-ref channel
  unless we change the substrate.

The user-locked decisions (recorded so this plan is self-contained):

1. **Scope: full rewrite.** Players, enemies, projectiles, platforms all live
   in the ECS at the end.
2. **FSMs preserved as polymorphic objects, stored inside a component.** The
   `MovementState`/`ActionState` class hierarchies are well-tested combat code
   and don't need flattening to make ECS work. They become fields on
   `MovementFSM` / `ActionFSM` components.
3. **Hand-rolled minimal ECS.** No third-party dependency (KNI/Blazor cross-
   build compatibility, deterministic snapshot ergonomics, total control over
   storage). Target size: ~few hundred LOC of core.

---

## Invariants the refactor must preserve

The migration is gated by *not breaking these* at any phase boundary:

1. **Deterministic step.** [MTile.Tests/Sim/SnapshotRoundTripTests.cs](../MTile.Tests/Sim/SnapshotRoundTripTests.cs)
   stays green after every phase. If it goes red, the phase isn't done.
2. **Cross-build.** `dotnet build MTile.Core.csproj` AND
   `dotnet build MTile.Web/MTile.Web.csproj` both succeed. No
   `FileSystemWatcher`, no threading, no APIs that exist in DesktopGL but not
   KNI (or vice versa).
3. **Phase ordering in `Simulation.Step`.** The existing pipeline
   (tickers → hurtboxes → player.Update → entity.Update → combat → physics) is
   preserved. Systems replace `foreach` loops; the ordering doesn't change.
4. **No new sim-affecting `static` mutable state.** The ECS world lives on
   `Simulation`, never as a static singleton. `MovementConfig.Current` stays
   the only exception (existing, already gated for multiplayer).
5. **Movement code does not read action state.** Same channel rules
   ([CLAUDE.md](../CLAUDE.md)) — actions may read movement; movement may not
   read actions. The component model doesn't make this easier to violate, but
   nothing in the migration should require violating it either.

---

## Core ECS types

Sketch of the hand-rolled core. Lives under a new `Sim/ECS/` directory so it's
discoverable.

```csharp
// EntityId — value identity. Generation bumps on Destroy so a stale
// EntityId held by an old hitbox / dedupe entry fails IsAlive cheaply.
public readonly struct EntityId : IEquatable<EntityId>
{
    public readonly int Index;
    public readonly int Generation;
    public static readonly EntityId None = default;
}

// World — owns entity slots, component stores, and the snapshot API.
// One World per Simulation (instance-owned; no statics).
public sealed class World
{
    public EntityId Create();
    public void     Destroy(EntityId e);
    public bool     IsAlive(EntityId e);

    public ref T Add<T>(EntityId e) where T : struct;   // throws if already present
    public ref T Get<T>(EntityId e) where T : struct;   // throws if absent
    public bool  Has<T>(EntityId e) where T : struct;
    public void  Remove<T>(EntityId e) where T : struct;

    // Queries iterate the smallest component store and gate on Has<T> for the rest.
    // Returns ref structs so iteration is alloc-free.
    public Query<T1>          Query<T1>() where T1 : struct;
    public Query<T1,T2>       Query<T1,T2>() where T1 : struct where T2 : struct;
    public Query<T1,T2,T3>    Query<T1,T2,T3>() where T1 : struct ...;
    public Query<T1,T2,T3,T4> Query<T1,T2,T3,T4>() where T1 : struct ...;

    // Snapshot is a per-store deep copy + the entity-slot generation array.
    public WorldSnapshot Capture();
    public void          Restore(WorldSnapshot snap);
}
```

**Storage:** sparse-set per component type. `ComponentStore<T>` holds:
- `int[] sparse` — entity index → packed slot (-1 if absent)
- `int[] packedEntities` — packed slot → entity index
- `T[] packedData` — packed slot → component value

Iteration walks `packedData` linearly (cache-friendly). Add/Remove are O(1)
with the swap-with-last trick. Snapshot is a per-store struct copy (the three
arrays).

**Why not a third-party ECS:** Arch / Friflo / DefaultEcs are excellent, but:
- KNI runtime compatibility is unverified; testing-then-removing is more work
  than building.
- Snapshot determinism is the load-bearing feature — we want full control over
  ordering and the storage layout.
- ~few hundred LOC for what we need; the maintenance surface is small.

---

## Component catalog

Ordered roughly by which phase introduces them. **Bold** = snapshotted (part
of `WorldSnapshot`); plain = transient/render-only.

### Universal
- **`Transform { Vector2 Position }`**
- **`Velocity { Vector2 V }`**
- **`PhysicsBodyComponent`** — the existing `PhysicsBody` repackaged as a
  component. Holds `Polygon Shape`, `Vector2 AppliedForce`,
  `List<PhysicsContact> Constraints`, `float LastImpulseMagnitude`,
  `ImpactDamage Impact`. (Polygon is immutable; shallow ref-copy is fine.)
- **`Health { float Current, float Max }`**
- **`Mass { float Value }`**
- **`GravityScale { float Value }`**
- **`FactionComponent { Faction Value }`**

### Tags / discriminants
- **`PlayerControlled { int LocalIndex }`** (0 = primary, 1+ = secondary)
- **`Hittable`** — replaces `IHittable`. Combat queries this. Empty marker
  struct; the data lives on `Health`/`Faction`/`PhysicsBodyComponent`.
- **`EnemyAITag`**, **`ProjectileTag`**, **`PlatformTag`** — discriminate
  entity classes for AI/lifetime/etc. systems.

### Player-specific
- **`MovementFSM { MovementState Current, MovementState[] Registry, MovementVars Vars, ... history fields ... }`**
- **`ActionFSM { ActionState Current, ActionState[] Registry, ActionVars Vars, ... history fields ... }`**
- **`PlayerAbilities`** — wraps existing `PlayerAbilityState`
- **`PlayerInputState`** — wraps `InputParser` + `IntentBuffer` + `ConditionState` + `CombatState`
- **`BlockSelection { TileType ActiveType, EruptionPlannerMode Mode }`**

### Entity-specific
- **`Lifetime { float Age, float Max }`** — projectiles, foam
- **`Sprout { ... }`** — for sprout-graph entities
- **`EnemyFSM`** — analogous to player's, owns
  `EnemyMovementState`/`EnemyActionState` instances

### Per-state owned data
These are fields-on-state-subclasses today; in the post-migration world they
become standalone components added on state.Enter and removed on state.Exit.
Deferred to phase 7 cleanup but enumerated here so they're not forgotten:

- `JumpSource { FloatingSurfaceDistance Contact }` (today: `JumpingState._source`)
- `WallContact { FloatingSurfaceDistance Contact, int Dir }` (today: `WallSliding._wall`, etc.)
- `LedgePin { ExposedCorner Corner }` (today: state-owned)
- (others per state — full list assembled in phase 7)

### Render-only (NOT snapshotted)
- `SpriteRef { AnimatedSprite Sprite }`
- `TrailRef { Trail Trail }`
- `ParticleEmitterRef { ... }`

These ride along as plain components but are excluded from `WorldSnapshot`
serialization. The marker for "snapshottable" is opt-in per type at store
construction.

---

## Phases

Each phase is a PR-sized unit. **"Done when"** lists the gate conditions.

### Phase 0 — ECS core, no usage

Build the world. Don't touch production code yet.

**Files (new):**
- `Sim/ECS/EntityId.cs`
- `Sim/ECS/World.cs`
- `Sim/ECS/ComponentStore.cs`
- `Sim/ECS/Query.cs` (generated for 1-4 component arities; a small T4 template
  or hand-written is fine)
- `Sim/ECS/WorldSnapshot.cs`
- `MTile.Tests/Sim/ECS/WorldRoundTripTests.cs`

**Done when:**
- World can Create / Destroy / Add / Get / Has / Remove / Query / Snapshot /
  Restore N synthetic components.
- Generation bump on Destroy invalidates stale `EntityId` (verified by test).
- `MTile.Core` and `MTile.Web` both build.
- Round-trip test deep-copies a populated world and verifies bit-equality.

### Phase 1 — Cross-references become `EntityId`

The lowest-risk, highest-leverage step. No structural change to entities yet —
we just stop storing direct refs to them in the cross-reference channels.

A `Simulation`-owned `Dictionary<EntityId, IHittable> _idBridge` bridges old
and new during this phase. Every existing entity registers in the bridge on
spawn; an `EntityId` resolves to the current `IHittable` ref on demand. The
bridge dies in phase 3.

**Files:**
- [World/Hitbox.cs:44](../World/Hitbox.cs#L44) — `Source: object` → `EntityId Source`
- [World/Hurtbox.cs:14](../World/Hurtbox.cs#L14) — `Target: IHittable` → `EntityId Target`
- [World/CombatSystem.cs:25](../World/CombatSystem.cs#L25) — `Dictionary<int, HashSet<IHittable>>` → `Dictionary<int, HashSet<EntityId>>`
- [World/CombatSystem.cs:103-130](../World/CombatSystem.cs#L103-L130) — simplify
  `CaptureDedupe`/`RestoreDedupe`: no more `idOf`/`resolve` callbacks, ids are
  already values
- [Entities/Entity.cs:34-35](../Entities/Entity.cs#L34-L35) — `int Id` →
  `EntityId Id`; `HittableId => Id` collapses
- [Character/PlayerCharacter.cs:14](../Character/PlayerCharacter.cs#L14) — same
  for the player
- [Simulation.cs:99,112,156](../Simulation.cs) — spawn sites register the
  bridge entry alongside `NextId`
- All call sites that read `Hitbox.Source as PlayerCharacter` / similar casts —
  go through the bridge instead

**Done when:**
- Zero direct entity refs across hitbox/hurtbox/combat boundaries.
- `SnapshotRoundTripTests` green.
- The bridge is the only place that maps `EntityId` → object. Everything else
  uses the `EntityId` directly.

### Phase 2 — World integration: bodies-as-components + identity registry

**Re-scoped during implementation.** Originally split across Phase 2 (bodies)
and Phase 3 (entity registry); merged because they entangle through `EntityId`
allocation and iteration order. Snapshot integration (the original Phase 6) is
deliberately kept *light* here — see below.

**Design decisions (locked with the user):**
- **`PhysicsBody` stays a class.** Everything in the game uses it as one unit and
  mutates it by reference (`ctx.Body.Velocity += force`). It's stored in the World
  wrapped in a struct: `struct PhysicsBodyComponent { PhysicsBody Body; }`. No
  value-ification, no struct conversion — movement/physics code is untouched.
- **The World becomes the `EntityId` authority**, retiring `Simulation._nextId`.
  Players/entities get their id from `world.Create()`.
- **Ref-holding stores are live-only, not World-snapshotted.** `PhysicsBodyComponent`,
  `EntityRef { Entity Obj }`, `PlayerRef { PlayerCharacter Obj }` hold class refs;
  a value-clone snapshot would alias live objects. So the World snapshot owns only
  the **slot/generation/free bookkeeping** (pure value data); entity/player *state*
  stays on the existing `EntitySnapshot`/`PlayerSnapshot` path. On restore, the
  ref stores are rebuilt from the rehydrated entities. (A small reconcile remains;
  it's killed in a later phase when entities decompose into real value components.)
- **ComponentStore removal is order-preserving** (shift, not swap-with-last). The
  sim's determinism — the state hash and snapshot/restore — relies on stable
  spawn-order iteration of bodies/entities, so World iteration must match
  insertion order and reproduce it after a restore.

**Files:**
- `Sim/ECS/ComponentStore.cs` — order-preserving `RemoveEntity` (stable insertion order).
- `Sim/ECS/World.cs` — `MarkLiveOnly<T>()`: such stores are skipped by `Capture`
  and cleared by `Restore`. `WorldSnapshot` then carries only id bookkeeping.
- New: `Sim/ECS/Components/EcsComponents.cs` — `PhysicsBodyComponent`, `EntityRef`,
  `PlayerRef`.
- [Physics/PhysicsWorld.cs](../Physics/PhysicsWorld.cs) — `StepSwept` iterates
  `world.Query<PhysicsBodyComponent>()` instead of `List<PhysicsBody>`.
- [Simulation.cs](../Simulation.cs) — hold a `World`; `NextId` → `world.Create()`;
  register `EntityRef`/`PlayerRef`/`PhysicsBodyComponent` on spawn; mark them
  live-only; replace `_bodies`/`_entities`/`_hittables` iteration with queries and
  the Phase 1 `ResolveHittable` linear scan with a World lookup; fold `WorldSnapshot`
  (id bookkeeping) into `SimSnapshot`; rebuild ref stores on restore.

**Iteration-order contract:** stores are created in canonical order (primary
player, secondaries, then entities in spawn order); order-preserving removal keeps
it; restore re-registers in the same order ⇒ identical stepping + hash.

**Done when:**
- `_bodies`/`_entities`/`_hittables` and `ResolveHittable` are gone.
- `PhysicsWorld.StepSwept` queries the World.
- Full suite green, especially `SnapshotRoundTripTests`.

### Phase 3 — Entity registry replaces `_entities` / `_hittables`

**Absorbed into the re-scoped Phase 2** (the registry and bodies entangle through
id allocation + iteration order, so they landed together). Retained below for the
original rationale.

The OO `Entity` objects still exist, but they're stored in an `EntityRef`
component (`struct EntityRef { Entity Object }`), and `Simulation` iterates
via world queries instead of lists.

**Files:**
- [Simulation.cs:33-34](../Simulation.cs#L33-L34) — delete `_entities`,
  `_hittables`
- [Simulation.cs:207-225](../Simulation.cs#L207-L225) — the foreach-loops in
  `Step` become systems:
  - `HurtboxPublishSystem.Run(world, hurtboxes)` — query `Hittable`
  - `EntityUpdateSystem.Run(world, dt, player, hitboxes, sim)` — query `EnemyAITag` / `ProjectileTag`
  - `PreStepSystem.Run(world, gravity)` — query `GravityScale`
- New: `Sim/ECS/Systems/` directory
- The `_idBridge` from phase 1 collapses into the world (resolve via
  `world.Get<EntityRef>(id).Object`).
- [Entities/Entity.cs](../Entities/Entity.cs) virtual methods
  (`Update`/`OnHit`/`PreStep`/`PublishHurtboxes`) stay — they're invoked from
  the systems. This is the "FSMs preserved as polymorphic objects" pattern
  applied to entities.

**Done when:**
- `Simulation.Step` is a sequence of system invocations, not list iterations.
- The bridge is gone.
- Entity spawn/despawn goes through `World.Create` / `Destroy`.

### Phase 4 — Decompose `Entity` into components

Peel apart the `Entity` base class. Fields move out; the methods either stay
on the object (called from systems) or move into per-subtype systems.

**Files:**
- [Entities/Entity.cs](../Entities/Entity.cs) — `Health`, `Mass`,
  `GravityScale`, `Faction`, `Sprite` → standalone components
- Per subtype (StalkerEnemy, TurretEnemy, BruteEnemy, EnergyBallProjectile,
  BulletProjectile, …): subclass-specific data → dedicated components
  (e.g. `ProjectileHoming { EntityId Target }`)
- [Entities/EnemyEntity.cs:20-32](../Entities/EnemyEntity.cs#L20-L32) — FSM
  registries → `EnemyFSM` component

**Done when:**
- `Entity` is mostly empty (or deleted in phase 7).
- Each subclass's data lives in components; systems query them.

### Phase 5 — Decompose `PlayerCharacter` into components

The biggest single phase. The player has the most fields and the thickest
FSMs.

**Files:**
- [Character/PlayerCharacter.cs](../Character/PlayerCharacter.cs) — every
  field becomes a component (`MovementFSM`, `ActionFSM`,
  `PlayerInputState`, `PlayerAbilities`, `BlockSelection`,
  `CombatSystemRef`, `Frame`, etc.)
- [Character/PlayerCharacter.cs:299-321](../Character/PlayerCharacter.cs#L299-L321)
  — `EnvironmentContext` construction becomes a per-frame helper that gathers
  component refs into the existing ctx struct. **Do not rewrite the FSM
  internals** — they read from ctx, and ctx stays the same shape.
- Player update becomes `PlayerUpdateSystem.Run(world, dt)`.
- `MovementState`/`ActionState` flyweight registries: lived on
  `PlayerCharacter`; now live on the `MovementFSM`/`ActionFSM` components.
- [Character/PlayerSnapshot.cs](../Character/PlayerSnapshot.cs) becomes
  redundant — each constituent is its own snapshotted component.

**Risks:**
- `EnvironmentContext` already pulls together everything the FSM needs. The
  cleanest path is to *keep* `EnvironmentContext` as the FSM's read surface
  and just have the system build it from components each frame. Zero churn
  inside the state classes themselves.
- History rings (`_stateHistory`, `_actionHistory`) — verify whether anything
  in production reads them. If only debug code does, drop them; otherwise
  fold into the FSM component.

**Done when:**
- `PlayerCharacter` is a thin spawn/destroy helper or gone entirely.
- `PlayerUpdateSystem` runs the per-player FSM via `EnvironmentContext` built
  from components.

### Phase 6 — Snapshot/restore migrates to component-typed

The world's per-store deep copy IS the new snapshot. Old types fold in.

**Files:**
- [SimSnapshot.cs](../SimSnapshot.cs) — becomes a thin wrapper around
  `WorldSnapshot` + `TerrainSnapshot` + `CombatDedupeSnapshot`
- [Entities/EntitySnapshot.cs](../Entities/EntitySnapshot.cs) deleted; per-
  component snapshots replace it
- [Character/PlayerSnapshot.cs](../Character/PlayerSnapshot.cs) deleted
- [Entities/EntitySnapshot.cs:9-26](../Entities/EntitySnapshot.cs#L9-L26)
  `EntityKind` discriminant disappears — component-set IS the type
- [MTile.Tests/Sim/SnapshotRoundTripTests.cs](../MTile.Tests/Sim/SnapshotRoundTripTests.cs)
  `Probe()` reads via world queries; `BuildSim()` spawns via ECS API

**Done when:**
- One snapshot format: world component stores + terrain journal + combat
  dedupe.
- `SnapshotRoundTripTests` green.
- No more per-subtype `Capture()`/`RestoreInto()` methods.

### Phase 7 — Cleanup + per-state contacts

Final pass. Delete what's now unused; pull per-state owned contacts into
standalone components.

**Files:**
- Delete `Entity` base class if empty
- Delete `IHittable` interface (`Hittable` component replaced it)
- Delete `HittableId` getter (was `Id`)
- For each movement state that owns a contact field — e.g.
  [Character/MovementStates.cs](../Character/MovementStates.cs)
  `JumpingState._source`, `WallSlidingState._wall`, etc. — extract to
  `JumpSource` / `WallContact` / etc. components, added on Enter, removed on
  Exit. This addresses [STATE_SNAPSHOT_PLAN.md](STATE_SNAPSHOT_PLAN.md)'s
  observation that soft contacts are not snapshotted today; making them
  components makes the rebuild-on-restore behavior explicit and queryable.

**Done when:**
- `Entity` class and `IHittable` interface are gone.
- No movement/action state holds a `PhysicsContact` field; all per-state
  contacts are components.
- Final `SnapshotRoundTripTests` pass.

---

## End-to-end verification recipe

The MTile testing pattern is xUnit sim tests (deterministic ascii terrain +
scripted input). There's no realistic browser/UI e2e for these changes.

After each phase:
1. **Build core:** `dotnet build MTile.Core.csproj`
2. **Build web:** `dotnet build MTile.Web/MTile.Web.csproj` (catches KNI
   incompat fast)
3. **Full test suite:** `dotnet test MTile.Tests/MTile.Tests.csproj`
4. **Snapshot round-trip filter:**
   `dotnet test MTile.Tests/MTile.Tests.csproj --filter "FullyQualifiedName~SnapshotRoundTrip"`
   — this is the load-bearing determinism test.
5. **Manual desktop sanity (recommended at phase 3, 5, 6):**
   `dotnet run --project MTile.Desktop` — play for ~30s, hit some enemies,
   pulse some tiles, jump. Anything that crashes or visibly desyncs is a bug
   the sim tests didn't catch.

---

## Risks and open questions

1. **Component arity in queries.** Generated query types for 1-4 arities should
   cover everything we need. If a system genuinely needs 5+ components, that's
   a smell — split it.

2. **`MovementConfig.Current` static.** Sim-shared singleton; existing
   exception. Don't touch as part of this refactor.

3. **`ChunkMap.OnTileBroken` event subscribers** are render-only (particles).
   Leave as a cosmetic-side event. If something ever needs sim-side reaction
   to tile-broken, that becomes a system reading a `TileBrokenThisStep`
   component or queue.

4. **Performance.** Hand-rolled ECS is ~as fast as today for entity counts in
   the dozens-hundreds. If counts grow to thousands (projectile-spam), the
   sparse-set design holds. No reason to preempt.

5. **`PhysicsContact.LastImpulse`** (per-step transient, just added) survives
   the migration unchanged as a field on `PhysicsBodyComponent`.

6. **KNI/Blazor cross-build.** Nothing in the proposed ECS core uses anything
   host-specific. Snapshot uses generic dict/array copies; safe both ways.
   Verify after phase 0 and at every subsequent phase boundary.

7. **Test harness churn.** `SimRunner.RunMulti` builds its own `CombatSystem`
   and constructs players directly; in the ECS world it'll need to construct
   a `World` instead. Plan to update `SimRunner` at phase 3.

8. **Recoil/attack feedback** (Newton's-third-law, just added) uses
   `Hitbox.Source` indirectly via `CombatSystem.PeekRecoil(hitId)` — keyed by
   `HitId`, not by entity ref. It's already migration-safe; the `Source` field
   change in phase 1 doesn't break it.

---

## Phase plan reference

This migration is downstream of two foundational efforts already landed:

- [STATE_SNAPSHOT_PLAN.md](STATE_SNAPSHOT_PLAN.md) — the data-oriented refactor
  + snapshot/restore that this plan builds on. Per-activation FSM state is
  already plain data (`MovementVars`/`ActionVars`); we just put it in
  components.
- [ROLLBACK_ROADMAP.md](ROLLBACK_ROADMAP.md) — the rollback netcode roadmap
  this refactor unlocks. Component-keyed snapshots are the substrate GGPO-
  style rollback needs.
