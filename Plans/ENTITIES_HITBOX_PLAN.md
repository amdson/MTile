# Entities, Hitboxes, and Hurtboxes

## Context

Three threads converge in this pass:

1. **Vocabulary swap.** The current `Hurtbox` type is mislabeled — in fighting-game terms it's a **hitbox** (an offensive region the attacker broadcasts). A real **hurtbox** is the defensive region a *target* broadcasts. The next steps need both, so we rename now while the surface area is small.
2. **A real combat dispatch step.** Currently `DamageSystem.Apply` walks hitboxes and applies tile damage directly. With entities in the mix, we need a generic intersection pass: for each hitbox × hurtbox pair where owners differ, dispatch an `OnHit` callback to the target. Plus a dedupe table so a multi-frame slash hitbox lands on a balloon **once**, not four times.
3. **Non-player entities.** Balloons (no gravity) and balls (gravity). Both broadcast hurtboxes, take damage, get knocked around, die. Same interface as the player, so the framework doesn't special-case anyone.

The end state of this pass: a few balloons + balls in `Game1.LoadContent`, slashable, knockable, destroyable.

## Vocabulary

| Term | Role | Owner | Broadcast by | Carries |
|---|---|---|---|---|
| **Hitbox** | offensive region (delivers damage) | attacker | actions / AI | damage + knockback + HitId |
| **Hurtbox** | defensive region (receives damage) | target | the target itself, each frame | back-pointer to `IHittable` |
| **Faction** | self-damage filter | both | both | enum (Player/Enemy/Neutral) |
| **HitId** | stable per attack instance | attacker | hitbox | int |

Intersection rule: hitbox H and hurtbox HB collide when `H.Region ∩ HB.Region` and `H.Owner ≠ HB.Owner`. On collision, call `HB.Target.OnHit(H, HB)`. Dedupe per `(HitId, Target)` so the same attack only registers once per target across its broadcast window.

## Core types

```csharp
// Renamed from current Hurtbox + new fields.
public readonly struct Hitbox {
    public readonly BoundingBox Region;
    public readonly int         HitId;             // stable across the attack's broadcast window
    public readonly float       Damage;            // per intersection event (see Combat dispatch)
    public readonly Vector2     KnockbackImpulse;  // impulse (px/s · mass-units); 0 = no knockback
    public readonly Faction     Owner;
    public readonly object      Source;            // attacker back-pointer (SlashAction, ...)
    public readonly Color       DebugColor;
}

// New — receive-side region.
public readonly struct Hurtbox {
    public readonly BoundingBox Region;
    public readonly Faction     Owner;
    public readonly IHittable   Target;            // dispatch back-pointer
    // future: int HurtId for multi-hurtbox entities (head/body/feet). Single-slot for V1.
}

public enum Faction { Player, Enemy, Neutral }
```

`HurtboxFaction` → `Faction`. The enum is shared between hitboxes and hurtboxes (faction is about who-belongs-to-whom, not about which kind of box).

## Shared interface — IHittable

```csharp
public interface IHittable {
    Faction Faction { get; }
    void PublishHurtboxes(HurtboxWorld world);
    void OnHit(in Hitbox hit, in Hurtbox myHurtbox);
}
```

`PublishHurtboxes` is called once per frame for each `IHittable`. The default implementation for body-bound entities (player, balloons, balls) is one line: publish one Hurtbox with `Region = body.Bounds`. A static helper `HurtboxUtils.PublishBodyBounds(world, this, body, faction)` does it so each implementor doesn't repeat.

`OnHit` is the entity's policy. Player stub does nothing; entities apply damage + knockback.

**Why one interface for both player and entities.** The combat surface is the same. They differ in *how they update* (FSM-driven vs. simple physics), but both must broadcast hurtboxes and react to hits identically. Two interfaces would just be a name with no distinct semantics.

## Frame phases

Order inside `Game1.Update`:

```
1. _hitboxes.Clear(); _hurtboxes.Clear();
2. Each IHittable publishes its hurtboxes:
     foreach (var h in _hittables) h.PublishHurtboxes(_hurtboxes);
3. Updates run; hitboxes are published as a side effect of updates:
     _player.Update(...)            // action FSM publishes via ctx.Hitboxes
     foreach (var e in _entities) e.Update(...)   // simple physics-prep
4. Combat dispatch:
     CombatSystem.Apply(_chunks, _hitboxes, _hurtboxes);
5. Physics:
     PhysicsWorld.StepSwept(_bodies, _chunks, dt, gravity);
6. Cleanup dead entities + their bodies.
```

Hurtboxes are published *before* updates, hitboxes *during* updates — both reflect start-of-frame positions, so intersection at step 4 is consistent. The dedupe key (HitId, Target) handles the "same hitbox over multiple frames" problem; the same logical slash carrying HitId=N only fires `OnHit` on each Target the first frame its region overlaps.

## Combat dispatch (CombatSystem)

Renamed from `DamageSystem`. Same file, expanded logic:

```csharp
public static class CombatSystem {
    // Persistent across frames so multi-frame hitboxes dedupe correctly.
    private static readonly Dictionary<int, HashSet<IHittable>> _hitDedupe = new();
    private static readonly HashSet<int> _liveHitIds = new();

    public static void Apply(ChunkMap chunks, HitboxWorld hitboxes, HurtboxWorld hurtboxes) {
        _liveHitIds.Clear();
        foreach (var hit in hitboxes.All) {
            _liveHitIds.Add(hit.HitId);

            // Tile path — UNCHANGED from today. Tiles don't dedupe by HitId; multi-frame
            // hitboxes accumulate per-cell damage so the progressive darkening still works.
            ApplyTileDamage(chunks, hit);

            // Entity path — deduped per (HitId, Target).
            if (!_hitDedupe.TryGetValue(hit.HitId, out var alreadyHit)) {
                alreadyHit = new HashSet<IHittable>();
                _hitDedupe[hit.HitId] = alreadyHit;
            }
            foreach (var hb in hurtboxes.All) {
                if (hit.Owner == hb.Owner) continue;
                if (alreadyHit.Contains(hb.Target)) continue;
                if (!Overlaps(hit.Region, hb.Region)) continue;
                hb.Target.OnHit(hit, hb);
                alreadyHit.Add(hb.Target);
            }
        }
        // Prune dedupe entries for HitIds that didn't broadcast this frame — the attack ended.
        PruneDeadHitIds();
    }
}
```

**Asymmetry note**: tiles accumulate per frame (current behavior — drives the darkening), entities one-shot per HitId (new). This is deliberate. The slash deals `Damage = 0.25` four times to a tile = 1.0 total; once to a balloon = 0.25. Tune balloon HP accordingly. If you'd rather have unified semantics, see open questions.

## Entity class

```csharp
public class Entity : IHittable {
    public PhysicsBody Body;
    public float Health;
    public float MaxHealth;
    public float Mass = 1f;        // higher = less knockback (target.Velocity += impulse / Mass)
    public float GravityScale = 1f;
    public Color Color = Color.White;
    public Faction Faction { get; init; } = Faction.Neutral;
    public bool IsDead => Health <= 0f;

    public Entity(PhysicsBody body, float health) {
        Body = body; Health = MaxHealth = health;
    }

    public void PublishHurtboxes(HurtboxWorld world)
        => world.Publish(new Hurtbox(Body.Bounds, Faction, this));

    public virtual void OnHit(in Hitbox hit, in Hurtbox _) {
        Health -= hit.Damage;
        if (Mass > 0f) Body.Velocity += hit.KnockbackImpulse / Mass;
    }

    // Called pre-StepSwept to apply selective gravity. GravityScale = 0 means
    // we apply an equal-and-opposite force to cancel the global gravity.
    public void PreStep(Vector2 globalGravity) {
        if (GravityScale == 1f) return;
        Body.AppliedForce += globalGravity * (GravityScale - 1f);
    }
}
```

Balloon and ball are not subclasses — they're factory-constructed Entities with different parameter values:

```csharp
public static class EntityFactory {
    public static Entity Balloon(Vector2 pos) => new(
        new PhysicsBody(Polygon.CreateRegular(8f, 8), pos), health: 0.5f)
        { Mass = 0.5f, GravityScale = 0f, Color = Color.HotPink };

    public static Entity Ball(Vector2 pos) => new(
        new PhysicsBody(Polygon.CreateRegular(6f, 8), pos), health: 1.0f)
        { Mass = 1.5f, GravityScale = 1f, Color = Color.SteelBlue };
}
```

Subclasses come in when behavior diverges (an enemy with AI, a projectile that fires hitboxes). For balloons and balls, the same code path suffices.

## Player integration

`PlayerCharacter` already implements `IHurtable` (the old name). The change is:

- Rename `IHurtable` → `IHittable` (or convert in place).
- Replace `Faction Faction` and `BoundingBox Bounds` with the new interface methods.
- `PublishHurtboxes` publishes one hurtbox from `Body.Bounds, Faction.Player, this`.
- `OnHit` stays a stub (or starts a simple "I took damage, log it" path).
- `SlashAction` generates a fresh `HitId` at Enter (`_hitId = NextHitId();`), passes it on each frame's `Publish`, also passes its `_slashDir * KnockbackMagnitude` as `KnockbackImpulse`.

`NextHitId` is a static counter:
```csharp
private static int _nextHitId = 1;
public static int NextHitId() => System.Threading.Interlocked.Increment(ref _nextHitId);
```

Lives on a small `HitIds` utility class. Wraps trivially.

## EnvironmentContext changes

```csharp
public class EnvironmentContext {
    // ...
    public HitboxWorld  Hitboxes;     // renamed from Hurtboxes
    public HurtboxWorld Hurtboxes;    // new — receive-side registry; mostly unused inside action FSM
}
```

Most action code uses `ctx.Hitboxes` (publish offensive regions). `ctx.Hurtboxes` is there for future actions that want to *query* hurtboxes (e.g., a homing projectile picking the nearest enemy).

## HitboxWorld + HurtboxWorld

Parallel registries, identical shape:

```csharp
public class HitboxWorld  { Clear(); Publish(in Hitbox);  Overlapping(...); All; }
public class HurtboxWorld { Clear(); Publish(in Hurtbox); Overlapping(...); All; }
```

A shared generic base (`RegionRegistry<T>`) would dedup the implementation but reduce readability — the existing `HurtboxWorld` is ~30 lines. Two thin wrappers is fine.

## Files

| File | Status | Purpose |
|---|---|---|
| `World/Hurtbox.cs` | RENAME → `Hitbox.cs` | rename struct + class; add `HitId`, `KnockbackImpulse` |
| `World/HurtboxWorld.cs` | RENAME → `HitboxWorld.cs` | rename class only |
| `World/Hurtbox.cs` | NEW (after rename) | the new receive-side struct |
| `World/HurtboxWorld.cs` | NEW (after rename) | receive-side registry |
| `World/DamageSystem.cs` | RENAME → `CombatSystem.cs` | rename + expand to dispatch `OnHit` + dedupe |
| `Character/IHurtable.cs` | RENAME → `IHittable.cs` | rename + new methods (PublishHurtboxes, OnHit) |
| `Entities/Entity.cs` | NEW | Entity class + helpers |
| `Entities/EntityFactory.cs` | NEW | Balloon / Ball constructors |
| `Game1.cs` | EDIT | own `_entities`, `_hittables` (player + entities), wire frame phases, draw entities, instantiate test balloons/balls |
| `Character/PlayerCharacter.cs` | EDIT | implement `IHittable`'s new methods |
| `Character/EnvironmentContext.cs` | EDIT | rename Hurtboxes → Hitboxes, add new Hurtboxes |
| `Character/ActionStates.cs` | EDIT | SlashAction publishes a Hitbox (renamed), assigns HitId at Enter, passes KnockbackImpulse |
| `MTile.Tests/JumpingStateTests.cs` | EDIT | rename HurtboxWorld param to HitboxWorld + new HurtboxWorld |
| `MTile.Tests/Sim/SimRunner.cs` | EDIT | same |

The `Faction` enum lives in `World/Hitbox.cs` (or its own file). `HurtboxFaction` → `Faction` everywhere.

**Naming collision risk**: the rename has a "rename A → B and then create new A" pattern (Hurtbox.cs is both the old file being renamed and the new file being created). Handle carefully: rename in one edit, then write the new file. Git diff will look like a delete + two creates rather than a rename + create — that's fine.

## Migration order

Each step compiles and passes tests on its own.

1. **Rename only, no semantic change.** `Hurtbox` → `Hitbox`, `HurtboxFaction` → `Faction`, `HurtboxWorld` → `HitboxWorld`, `DamageSystem` → `CombatSystem`, `IHurtable` → `IHittable`. All call sites get the new names. Tile damage path unchanged. Game still looks and feels identical.
2. **Introduce the (new) Hurtbox + HurtboxWorld.** Empty struct, empty registry. `IHittable` grows `PublishHurtboxes` and `OnHit` methods; player implementations are stubs. Player publishes its bounds-hurtbox each frame. CombatSystem walks hurtboxes too but no one is hit yet (Player is the only Hittable; player slashes are Faction.Player and skip).
3. **Add HitId + dedupe.** SlashAction generates `_hitId` at Enter, passes on each `Publish`. CombatSystem tracks the dedupe table. No visible behavior change (no entities yet).
4. **Add KnockbackImpulse field on Hitbox.** SlashAction populates with `_slashDir * KnockbackMagnitude` (start ~150 px/s × kg). No visible behavior change.
5. **Entity class + EntityFactory.** Instantiate a balloon and a ball in Game1.LoadContent. Draw them as simple colored polygons (mirror `DrawPolygon`). They publish hurtboxes, take damage, die. Game1's main loop adds entity updates + cleanup. **First playable step**: slash a balloon, watch it die. Slash a ball, watch it die. No knockback yet because we haven't enabled it on the player's hits.
6. **Wire knockback in Entity.OnHit.** Slash a balloon: it flies. Slash a ball: it pops sideways then falls. Tune `KnockbackMagnitude` and `Mass` until it feels right.

Stages 1–3 are pure scaffolding and should land in one PR-equivalent. Stages 4–6 are where new behavior shows up.

## Verification

After step 5:
- `dotnet test` passes.
- Three balloons hover at fixed Y, three balls sit on the ground. Slash a balloon → it disappears. Slash a ball → it disappears.

After step 6:
- Slash a balloon to the right of the player → it flies right and stays floating.
- Slash a ball to the right → it accelerates right and falls.
- Slash a balloon multiple times until it dies (HP > one slash's damage).
- Slash an entity through a wall? Hitbox doesn't care about LOS in V1 — note this as a known limitation. Tile occlusion is future work.

## Open questions

1. **Tile damage asymmetry.** Plan keeps tiles cumulative (so 4-frame slash builds up tile damage progressively, same as today) and entities one-shot-per-HitId. Alternative: unify by also deduping tile damage per HitId — then `Damage = TileMaxHP = 1.0` and slashes one-shot tiles. Simpler but loses the visible darkening progression. **Default: keep asymmetric.** Flag if you want to unify.
2. **HitId on the Hurtbox side ("HurtId").** Plan defers this. Single hurtbox per entity for V1 ⇒ no need to disambiguate "which slot got hit". When you want headshots/footshots, add `int HurtId` to Hurtbox and `(HitId, Target, HurtId)` to the dedupe key.
3. **Player knockback.** Stub `OnHit` does nothing. Could plug into the existing movement FSM (a `Stunned` state, knockback applied to velocity). Defer until enemies exist that can hit the player.
4. **Entity gravity model.** Plan applies `(GravityScale - 1) * globalGravity` as a counter-force in `PreStep`. Simple, no PhysicsBody changes. Alternative is to add `GravityScale` to `PhysicsBody` and have `PhysicsWorld.StepSwept` honor it — cleaner but touches physics. **Default: counter-force.** Promote to physics-level scale if it gets ugly.
5. **Entity rendering.** Plan uses simple `DrawPolygon(entity.Body.Polygon, entity.Body.Position, entity.Color)`. Health bars: defer; for V1 the entity simply disappears on death, and the slash visual is enough feedback that something happened.
6. **Mass = 0 / infinity.** Plan has `if (Mass > 0f) Body.Velocity += ...`. Mass = ∞ would be cleaner but the float doesn't represent that well. Convention: `Mass <= 0` ⇒ immovable. Document.
