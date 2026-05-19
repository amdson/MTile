# Damage & Hurtbox Framework

## Context

The slash action has visuals but no effect — it doesn't break anything, doesn't hurt anyone. The next layer up is **damage**: a model where game elements (tiles, entities) accumulate damage, eventually reaching a breakage threshold, driven by short-lived **hurtboxes** spawned by attacks.

Two requirements shape the design:

1. **Damaged-block storage is sparse.** Almost no tiles in the world are damaged at any given moment, so the natural fit is a hashmap keyed on global cell coords, not a field on every `Tile`. This also keeps damage state out of the load/save path for the tile grid.
2. **Hurtbox info should be queryable.** Damage isn't a one-shot event the attacker pushes outward; it's *region-of-effect* state that interested elements (the player checking if they've been hit, an enemy AI deciding to flinch, a tile damage tick walking the world) can ask about. So hurtboxes live in a central registry with a spatial query, and the question "is anything hurting me right now?" has a single answer.
3. **Hurtboxes are re-broadcast every frame by their owner, never live across frames on their own.** No `Lifetime` field, no early-expiry calls. If a move wants a hurtbox to exist this frame, it publishes one this frame; next frame it decides again. This trades a tiny bit of per-frame work (re-publishing identical box geometry) for the elimination of *ownership* — the registry is stateless across frames, and a move that exits or gets interrupted simply stops publishing. No cleanup code to write or forget.

This plan introduces three pieces — `TileDamage` (sparse per-tile HP), `Hurtbox` + `HurtboxWorld` (registry + spatial query), `IHurtable` (entity interface) — and wires SlashAction to spawn a hurtbox as it finishes.

## Design

### 1. TileDamage — sparse per-tile HP

New file `World/TileDamage.cs`:

```csharp
public class TileDamage
{
    // Sparse: only damaged tiles have entries. Empty/undamaged tiles aren't present.
    private readonly Dictionary<(int gtx, int gty), float> _hp = new();
    public const float TileMaxHP = 1.0f;   // tunable; one solid hit from a slash by default

    // Add `amount` damage. Returns true if the tile broke this call.
    public bool ApplyDamage(int gtx, int gty, float amount)
    {
        _hp.TryGetValue((gtx, gty), out float cur);
        cur += amount;
        if (cur >= TileMaxHP) { _hp.Remove((gtx, gty)); return true; }
        _hp[(gtx, gty)] = cur;
        return false;
    }

    public float Get(int gtx, int gty)
        => _hp.TryGetValue((gtx, gty), out float v) ? v : 0f;

    public void Clear(int gtx, int gty) => _hp.Remove((gtx, gty));

    // For debug overlays / cracks rendering: enumerate non-zero entries.
    public IEnumerable<KeyValuePair<(int gtx, int gty), float>> Damaged => _hp;
}
```

Lives on `ChunkMap`:
```csharp
public readonly TileDamage Damage = new();
```

**Cell breakage**: `ChunkMap` gains
```csharp
public bool DamageCell(int gtx, int gty, float amount);
```
which walks `TileDamage.ApplyDamage`, and on break flips the cell state to `Empty` (existing `DestroyTile` logic, hoisted into a `BreakCell(int gtx, int gty)` helper for the global-coord path). Returns true if the tile actually broke.

**Sprouts**: deferred. First pass damages only `TileState.Solid` cells — `DamageCell` early-outs on `Sprouting` / `Empty`. Adding sprout damage means dropping the node from `Graph.Growing` and propagating the cancellation through `n.Children` (orphaned `Pending` children with no remaining parents get dropped from `Graph.Pending`). Note in code, defer.

**Decay**: not in V1. Optional later: a slow regen `dHP/dt` per frame; freshly-damaged tiles "remember" being hit so they don't heal mid-combat (timestamp the last hit, only decay after a grace window).

### 2. Hurtbox — the damage primitive

New file `World/Hurtbox.cs`:

```csharp
public enum HurtboxFaction { Player, Enemy, Neutral }

public readonly struct Hurtbox
{
    public readonly BoundingBox    Region;   // AABB in world coords. Polygon later (see "Future").
    public readonly float          Damage;   // amount applied per *frame the box is published*
    public readonly HurtboxFaction Owner;    // for self-damage filtering
    public readonly object         Source;   // back-pointer to attacker (SlashAction, ...) — opaque
                                              // to the registry; used by AI / damage attribution

    public Hurtbox(BoundingBox region, float damage, HurtboxFaction owner, object source)
    {
        Region = region; Damage = damage; Owner = owner; Source = source;
    }
}
```

**Struct, not class.** Hurtboxes have no identity across frames — the registry is cleared each frame and re-filled by publishers. Value semantics drop GC pressure and make it impossible to accidentally hold a stale reference past the frame it was valid for.

**No `Lifetime` field.** Damage-window duration is owned by the publisher: a move publishes the box for N frames, the registry sees it for N frames, that's the lifetime. A move that exits or gets interrupted simply stops publishing next frame — nothing to clean up.

**Per-frame "tick" semantics still hold.** A box published for 4 frames at `Damage = 0.25` totals 1.0 on a stationary target. A glancing target overlapping for 1 frame takes 0.25. For one-shot semantics, publish at full damage for a single frame.

**AABB only in V1.** Enough for the slash arc (one small box at the apex), trivially fast to test overlap, and the registry's spatial query stays a simple linear scan. Swapping to polygon means adding a `Polygon Shape` field + Position + reusing `Collision.Check`.

### 3. HurtboxWorld — registry, clear, query

New file `World/HurtboxWorld.cs`:

```csharp
public class HurtboxWorld
{
    private readonly List<Hurtbox> _boxes = new();
    public IReadOnlyList<Hurtbox> All => _boxes;

    // Wipe all published hurtboxes. Called once at the start of every frame from
    // Game1.Update, before publishers (movement/action states, AI) get to run.
    public void Clear() => _boxes.Clear();

    // Publish a hurtbox for THIS frame only. Called by anything that wants damage
    // applied this step (SlashAction's damage window, an enemy's attack frame, etc).
    public void Publish(in Hurtbox hb) => _boxes.Add(hb);

    // The query used by everything that needs to know "am I being hurt this frame?".
    // Filter by faction so player slashes don't tag the player.
    public IEnumerable<Hurtbox> Overlapping(BoundingBox region, HurtboxFaction? exclude = null)
    {
        foreach (var hb in _boxes)
        {
            if (exclude.HasValue && hb.Owner == exclude.Value) continue;
            if (Overlaps(hb.Region, region)) yield return hb;
        }
    }

    private static bool Overlaps(BoundingBox a, BoundingBox b)
        => a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
}
```

**Frame phases**: clear → publish → query/react → apply. Game1.Update calls these in that fixed order (see §8). Consumers that *react* (an enemy reading "am I being hit?") run during the query phase, between publish and apply.

**Why a list, not a spatial index.** Hurtbox counts are expected to stay in the low single digits (one slash hurtbox per frame in V1; a few more once enemies attack). A 4-comparison overlap test × N hurtboxes per consumer is fine until N gets large. Upgrade path: uniform grid keyed on cell coords, behind the same `Overlapping` API.

### 4. IHurtable — entity damage interface

New file `Character/IHurtable.cs`:

```csharp
public interface IHurtable
{
    HurtboxFaction Faction { get; }     // own faction — used to filter hurtboxes by owner
    BoundingBox Bounds { get; }         // current AABB in world coords
    void ApplyDamage(float amount, Hurtbox source);
}
```

`PlayerCharacter` implements it:
```csharp
public HurtboxFaction Faction => HurtboxFaction.Player;
public BoundingBox Bounds => Body.Bounds;
public void ApplyDamage(float amount, Hurtbox source) { /* TODO: HP, stun, knockback */ }
```

V1's `ApplyDamage` is a stub — no HP tracking on the player yet. Just exists so the wiring is correct; future enemy types implement the same interface.

### 5. The frame-level damage tick

One central application step, called from `Game1.Update` after all publishers have run:

```csharp
DamageSystem.Apply(_chunks, _hurtboxes, _hurtables);
```

```csharp
public static class DamageSystem
{
    // Applies damage from every hurtbox published this frame to every overlapping target.
    public static void Apply(ChunkMap chunks, HurtboxWorld hurtboxes, IReadOnlyList<IHurtable> hurtables)
    {
        foreach (var hb in hurtboxes.All)
        {
            // Tile damage: walk the cells under the hurtbox AABB.
            int gtx0 = (int)MathF.Floor(hb.Region.Left   / Chunk.TileSize);
            int gtx1 = (int)MathF.Floor(hb.Region.Right  / Chunk.TileSize);
            int gty0 = (int)MathF.Floor(hb.Region.Top    / Chunk.TileSize);
            int gty1 = (int)MathF.Floor(hb.Region.Bottom / Chunk.TileSize);
            for (int gtx = gtx0; gtx <= gtx1; gtx++)
            for (int gty = gty0; gty <= gty1; gty++)
                if (chunks.GetCellState(gtx, gty) == TileState.Solid)
                    chunks.DamageCell(gtx, gty, hb.Damage);

            // Entity damage: query each hurtable.
            foreach (var h in hurtables)
            {
                if (h.Faction == hb.Owner) continue;
                if (Overlaps(h.Bounds, hb.Region)) h.ApplyDamage(hb.Damage, hb);
            }
        }
    }
}
```

**Modularity check**: the tick is the *centralized* damage application, but it's not the only consumer. Entities that want to *react* to incoming damage *before* they take it (a shield state that absorbs it; a flinch animation that needs to fire on first contact, not on death) call `_hurtboxes.Overlapping(self.Bounds, exclude: self.Faction)` in their own update and gate behavior on that. The same `HurtboxWorld` answers both.

### 6. SlashAction broadcasts a hurtbox during its damage window

`SlashAction` already has a `_facing` and a known body position. Each frame, during the **back-half of the swing** (the apex-and-return arc, where the visual is at its full extent), it publishes a fresh hurtbox. No reference tracking, no cleanup — when the slash exits (naturally or via ground-loss interrupt), it simply stops publishing next frame.

- **Damage window**: `_timeInState ∈ [HurtboxStartTime, HurtboxStartTime + HurtboxActiveDuration]`, ≈ [0.20 s, 0.32 s]. About 4 frames at 30 fps.
- **Geometry**: AABB centered ~45° up-and-out from the body along `_facing`, size ≈ `(ArcRadius, ArcRadius * 0.8)`.
- **Per-frame damage**: `SlashDamagePerFrame = TileMaxHP / ExpectedDamageFrames` = `1.0 / 4 = 0.25`. A stationary tile under the box for the whole window takes 4 × 0.25 = 1.0 and breaks; a glancing target takes proportionally less. Tunable.
- **Owner**: `HurtboxFaction.Player`. **Source**: `this`.

In code:
```csharp
private const float HurtboxStartTime      = 0.20f;
private const float HurtboxActiveDuration = 0.12f;
private const float SlashDamagePerFrame   = 0.25f;

// inside Update, after the trail push:
if (_timeInState >= HurtboxStartTime &&
    _timeInState <= HurtboxStartTime + HurtboxActiveDuration)
{
    var apex = ctx.Body.Position + new Vector2(_facing, -1f) * (ArcRadius * 0.7071f);
    var region = new BoundingBox(apex.X - ArcRadius * 0.5f, apex.Y - ArcRadius * 0.4f,
                                  apex.X + ArcRadius * 0.5f, apex.Y + ArcRadius * 0.4f);
    ctx.Hurtboxes.Publish(new Hurtbox(region, SlashDamagePerFrame, HurtboxFaction.Player, this));
}
```

Interrupt handling falls out for free: if `CheckConditions` returns false (ground lost) mid-window, the action FSM exits SlashAction next frame, the next `Update` call doesn't happen, no hurtbox is published. `Exit` has nothing to do. (`ctx.Hurtboxes` — see §7.)

### 7. EnvironmentContext gets the registry handle

```csharp
public class EnvironmentContext
{
    // ...
    public HurtboxWorld Hurtboxes;
}
```

Wired by `PlayerCharacter.Update` from a registry the caller passes in. Signature change:
```csharp
public void Update(Controller controller, ChunkMap chunks, HurtboxWorld hurtboxes, float dt)
```

The action FSM gets `HurtboxWorld` via `ctx.Hurtboxes`. Movement code doesn't read it — coupling rule from the action-FSM plan holds (one-way: actions may peek into world state, movement doesn't peek into actions or actions' world).

### 8. Game1 ownership

```csharp
private readonly HurtboxWorld _hurtboxes = new();
private readonly List<IHurtable> _hurtables = new();

// in LoadContent / startup:
_hurtables.Add(_player);

// in Update:
_hurtboxes.Clear();                                       // start fresh every frame
_player.Update(_controller, _chunks, _hurtboxes, dt);     // publishers run (action FSM, future AI)
// (any react-phase consumers go here — entities asking "is anything hurting me?")
DamageSystem.Apply(_chunks, _hurtboxes, _hurtables);      // apply, break tiles, hit hurtables

// in Draw (debug):
if (DebugDrawHurtboxes)
    foreach (var hb in _hurtboxes.All)
        _spriteBatch.Draw(_pixel, new Rectangle(
            (int)hb.Region.Left, (int)hb.Region.Top,
            (int)(hb.Region.Right - hb.Region.Left),
            (int)(hb.Region.Bottom - hb.Region.Top)),
            Color.Red * 0.25f);
```

Hurtbox debug draw piggybacks on the existing world-space SpriteBatch (under the camera transform).

## Files

| File | Status | Purpose |
|---|---|---|
| `World/TileDamage.cs`        | NEW  | sparse `Dictionary<(int,int), float>` + break logic |
| `World/Hurtbox.cs`           | NEW  | `Hurtbox` struct + `HurtboxFaction` |
| `World/HurtboxWorld.cs`      | NEW  | registry + `Clear` + `Publish` + `Overlapping` query |
| `World/DamageSystem.cs`      | NEW  | central per-frame application from hurtboxes to tiles + hurtables |
| `Character/IHurtable.cs`     | NEW  | interface |
| `World/ChunkMap.cs`          | EDIT | own `TileDamage`; add `DamageCell(gtx, gty, amount)` and `BreakCell(gtx, gty)`; refactor `DestroyTile` to share `BreakCell` |
| `Character/EnvironmentContext.cs` | EDIT | add `Hurtboxes` field |
| `Character/PlayerCharacter.cs` | EDIT | implement `IHurtable`; thread `HurtboxWorld` into `Update` |
| `Character/ActionStates.cs`  | EDIT | `SlashAction` publishes a hurtbox each frame during its damage window |
| `Game1.cs`                   | EDIT | own `HurtboxWorld`; per-frame `Clear` → publishers → `DamageSystem.Apply`; debug draw |

## Migration order (each step playable)

1. **TileDamage + ChunkMap.DamageCell/BreakCell.** No hurtboxes yet — verify by exposing a keyboard shortcut that calls `_chunks.DamageCell(...)` on the cell under the cursor, see it break after N presses.
2. **Hurtbox + HurtboxWorld + debug draw.** Publish a hardcoded hurtbox from Game1.Update at a known position (after Clear, before Apply) to verify the clear/publish/draw loop. Remove the hardcode after.
3. **DamageSystem.Apply** wired to `Game1.Update`. With the same hardcoded hurtbox from step 2 left in place, watch a Solid tile under it tick down and break over a handful of frames.
4. **IHurtable + PlayerCharacter stub.** No visible behavior, but the wiring (faction filter, bounds query, ApplyDamage callback) is exercised.
5. **SlashAction publishes the hurtbox.** Click left, swing, watch a tile under the apex break. The hurtbox is published each frame from `t=0.20 s` to `t=0.32 s` — visible as the red square at the end of the arc.
6. **(Later)** Sprout damage handling. Tile HP per type. Damage decay. Block-break particles / sound. Knockback on hurtable entities. Polygon hurtboxes.

## Confirmed design choices

1. **TileDamage** — sparse `Dictionary<(int,int), float>` on `ChunkMap`, `TileMaxHP = 1.0` default.
2. **No hurtbox lifetimes** — publishers broadcast each frame they want the box live; the registry is cleared at frame start. Ownership becomes a non-issue (a move that exits just stops publishing).
3. **Hurtbox is a struct** — falls out of (2): no cross-frame identity means no reason to heap-allocate.
4. **One hurtbox per slash, at the apex** — visual arc is 0.5 s, damage window is ~0.12 s. Sweep-style multi-box variant deferred.
5. **Per-frame damage = `TileMaxHP / ExpectedDamageFrames` = 0.25** — full slash window deals 1.0 to a stationary tile (breaks it); glancing targets take proportionally less.
6. **AABB hurtboxes only in V1** — polygon support deferred.

## Verification

- `dotnet build` clean, `dotnet test` 79/79 (action system is purely additive — same as the last pass).
- Run game. Walk up to a tile, click. The slash arc visual fires as before; about 0.2 s in, a faint red square appears at the apex; the tile breaks if a Solid cell falls under it.
- Click multiple times into a row of tiles — each click breaks one tile cleanly. Cooldown (0.15 s) still prevents spammed slashes.
- Mid-jump click → slash interrupts (ground check); SlashAction.Update stops being called, no hurtbox published next frame, no orphan damage applied to airborne path.
- Debug HUD: `CurrentActionName` reads `SlashAction` for 0.5 s as before. Toggle `DebugDrawHurtboxes` to verify the red box overlaps where you'd expect.
