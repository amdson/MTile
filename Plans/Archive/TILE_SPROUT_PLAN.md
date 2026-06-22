# Tile Sprout Plan

## Goal

Place tiles as growing organisms instead of instantaneous boolean flips. Right-clicking adjacent to existing solid terrain spawns a `TileSprout` — a full-size tile-shaped polygon that **starts at the parent tile's position and translates into the target cell** over ~3 frames, then commits itself as a regular solid tile. Because the sprout is always full size and merely translates, it's just a moving rectangle — no special sweep math, no "growing polygon" handling, and `Velocity` reporting is the constant translation rate.

This also introduces a real tile-state machine on the tile array: cells go `Empty → Sprouting → Solid`, with adjacency rules that prevent stacking sprouts on top of in-progress sprouts.

## Constraints / Invariants

1. **No floating placement.** A new sprout must have at least one fully-solid 4-neighbor (N/S/E/W). Diagonals do not count.
2. **No sprout-adjacent placement.** If any 4-neighbor cell is sprouting, reject the new placement. Sidesteps every "two sprouts overlapping while growing" edge case.
3. **One sprout per cell.** Right-clicking inside an active sprout's cell is a no-op.
4. **Sprout reach unchanged.** Same `BuildReach = 64f` as today in [Game1.HandleBuildInput](Game1.cs).
5. **Player-overlap is not blocked.** The growing sprout's translation pushes the body via the existing sweep — direction is structural (set by the parent tile), not body-position-dependent.
6. **Sprouts tick on `dt`.** Same dt the rest of the loop uses, so headless sim and 30fps game stay equivalent.

## Data Model

Tile becomes a tagged state. The sprout reference is non-null only in the `Sprouting` state.

```csharp
public enum TileState : byte { Empty, Sprouting, Solid }

public struct Tile
{
    public TileState State;
    public TileSprout Sprout;   // non-null iff State == Sprouting
    public bool IsSolid => State == TileState.Solid;   // back-compat shorthand
}
```

The struct grows from 1 byte to ~16 bytes (enum + reference + padding). 256 tiles per chunk × 16 bytes = 4 kB/chunk — fine.

`TileSprout` models the sprout as a translating square. No scale, no polygon rebuild — a single constant polygon and a single constant velocity:

```csharp
public sealed class TileSprout
{
    public readonly Point   ChunkPos;     // owning chunk
    public readonly int     Tx, Ty;       // local tile indices in the chunk
    public readonly Vector2 StartCenter;  // parent tile center (where the sprout starts)
    public readonly Vector2 EndCenter;    // target cell center (where it finalizes)
    public readonly Vector2 Velocity;     // (EndCenter - StartCenter) / Lifetime  — constant
    public readonly Polygon Polygon;      // CreateRectangle(TileSize, TileSize) — built once
    public readonly float   Lifetime;
    public float Age;                     // seconds since spawn
    public Vector2 Center =>
        Vector2.Lerp(StartCenter, EndCenter, MathF.Min(1f, Age / Lifetime));
    public bool IsComplete => Age >= Lifetime;
}
```

Frame 0 the sprout's bounds exactly coincide with the parent tile — no new physical surface yet. As `Age` grows, the polygon translates linearly into the target cell, with the moving edges sweeping bodies aside the way `MovingRectangle` does today. At `Age == Lifetime`, sprout's bounds exactly coincide with the target cell; finalize, swap to `TileState.Solid`, drop the sprout.

### Parent-tile direction priority

When a sprout is placed at cell `(tx, ty)`, examine the 4-neighbors in this order. First solid neighbor wins:

1. **Below** (`tx, ty+1` solid) → sprout grows **up**. StartCenter = below-tile center, EndCenter = target center. Velocity y < 0. This is the default for floor-up building.
2. **Left** (`tx-1, ty` solid) → sprout grows **right**. Velocity x > 0.
3. **Right** (`tx+1, ty` solid) → sprout grows **left**. Velocity x < 0.
4. **Above** (`tx, ty-1` solid) → sprout grows **down** (drips from ceiling). Velocity y > 0.

The "bias up first, then sideways, then down" matches intuition: when you build outward from a platform, the new tile pops up out of the platform rather than sliding sideways out of an adjacent wall.

### Tracking on ChunkMap

Authoritative state lives in `chunk.Tiles[tx,ty].State` / `.Sprout`. Iteration over active sprouts (for ticking, drawing, AABB queries) is faster with a side index than walking every tile in every chunk every frame:

```csharp
private readonly List<TileSprout> _activeSprouts = new();  // mirror of cells in State == Sprouting
public IReadOnlyList<TileSprout> ActiveSprouts => _activeSprouts;
```

Add to the list on spawn, remove on finalize/destroy. The tile array is the source of truth; the list is a derived iteration index. Order is unspecified; the list is small (a handful at a time at most for the foreseeable future).

## Control Flow

### Placement (right-click, frame N)

[Game1.HandleBuildInput](Game1.cs) currently calls `_chunks.CreateTile(x, y)`. Replace with `_chunks.TrySpawnSprout(x, y)`:

```
HandleBuildInput
  edge-trigger check (unchanged)
  reach check (unchanged)
  ChunkMap.TrySpawnSprout(worldX, worldY)
    convert world → global cell (gtx, gty)
    reject if cell state ≠ Empty                          <-- already sprouting or solid
    pick parent by priority (below, left, right, above)
    reject if no solid parent found                       <-- root-adjacency
    reject if any 4-neighbor is Sprouting                 <-- sprout-adjacency
    auto-create chunk(s) if missing
    set Tiles[tx,ty].State = Sprouting
    construct TileSprout from (parent center, target center, Lifetime)
    Tiles[tx,ty].Sprout = sprout
    _activeSprouts.Add(sprout)
```

The "any 4-neighbor is Sprouting" check is independent of the parent — even if there's a solid neighbor that satisfies root-adjacency, a *different* sprouting neighbor still rejects.

Adjacency lookup works in global cell coordinates (single integer pair across all chunks), so neighbor checks across chunk boundaries don't need special-casing. Helper:

```csharp
public static bool IsCellInState(ChunkMap chunks, int gtx, int gty, TileState state);
```

### Tick (each frame, before `PhysicsWorld.StepSwept`)

```
Game1.Update
  ...
  _controller.Update(...)
  HandleBuildInput
  _chunks.TickSprouts(dt)                                 <-- new
    for sprout in _activeSprouts:
      sprout.Age += dt
      if sprout.IsComplete:
        tile.State = Solid
        tile.Sprout = null
        mark for removal from _activeSprouts
    bulk-remove finalized sprouts
  movingRect / ferris ticks (existing)
  _player.Update(...)
  PhysicsWorld.StepSwept(...)
```

Same slot moving platforms occupy. The sprout's `Center` accessor reads `Age`, so once `TickSprouts` returns, every downstream query (collision, draw, etc.) sees the up-to-date position. On the frame a sprout finalizes, the world already presents a regular solid tile to physics — no half-state visible mid-step.

### Physics integration

`ChunkMap` is already `ISolidShapeProvider`. Its `ShapesInRect` yields tile shapes via `TileQuery.SolidTilesInRect`. Extend it to also yield sprout shapes:

```csharp
IEnumerable<SolidShapeRef> ISolidShapeProvider.ShapesInRect(BoundingBox region)
{
    foreach (var t in TileQuery.SolidTilesInRect(this, region)) yield return /* tile shape */;

    const float half = Chunk.TileSize * 0.5f;
    foreach (var s in _activeSprouts)
    {
        var c = s.Center;
        if (c.X + half <= region.Left || c.X - half >= region.Right) continue;
        if (c.Y + half <= region.Top  || c.Y - half >= region.Bottom) continue;
        yield return new SolidShapeRef(
            c.X - half, c.Y - half, c.X + half, c.Y + half,
            c, s.Velocity, s.Polygon);
    }
}
```

The shape ref carries `s.Velocity` (constant for the sprout's lifetime), so the swept-collision code's existing moving-surface handling — the same one moving platforms use — pushes bodies in the sprout's growth direction. No new sweep math required.

`IsSolidAt(worldX, worldY)`: true if the cell is `Solid`. For sprouting cells, test against the sprout's *current* AABB (which depends on `Age`). Sprouts whose center has not yet left the parent's footprint will report `IsSolidAt = true` only inside the moving square — the world isn't reporting two solids simultaneously, because the sprout's footprint at `Age=0` is the parent cell, not the target cell.

### Visualization

`Game1.DrawChunk` iterates `chunk.Tiles[tx,ty].IsSolid` — switch to `State == Solid`. Add a sprout pass in `Game1.Draw`:

```csharp
foreach (var s in _chunks.ActiveSprouts)
{
    var c = s.Center;
    const float half = Chunk.TileSize * 0.5f;
    _spriteBatch.Draw(_pixel, new Rectangle(
        (int)(c.X - half), (int)(c.Y - half),
        Chunk.TileSize - 1, Chunk.TileSize - 1),
        Color.LightSkyBlue);
}
```

## Config

Add to [MovementConfig](Character/MovementConfig.cs) (the config-via-json layer the rest of the tuning uses):

```csharp
public float SproutLifetime { get; set; } = 0.1f;   // ~3 frames @ 30fps
```

Just the one knob — direction is structural (parent priority) and size is fixed at `TileSize`.

## Edge Cases

1. **Sprout finalizes under a body resting on it.** Frame N: sprout near final position, body riding its top edge via a sweep-spawned `SurfaceDistance` (or a state-owned `FloatingSurfaceDistance` re-probed from `WorldQuery`). Frame N+1: tile state flips to `Solid`, sprout gone. Cell geometry is unchanged (sprout's final position = cell center), so the constraint's `WorldHasSurface` check still passes. ✓ No special handling.

2. **`Velocity` flips to zero on finalize.** During growth, `SolidShapeRef.Velocity = sprout.Velocity`. On finalize the cell becomes a static tile (Velocity = 0). A body that was tracking the sprout's surface velocity will see a single-frame zero-out. Cosmetic, small (160 px/s at default Lifetime), acceptable.

3. **Player builds a wall around themselves.** Each new sprout requires a solid 4-neighbor *and* no sprouting 4-neighbor. They can't fire off four sprouts in one frame to box themselves — they have to wait for each to finalize. Sequential building still works. If they trap themselves, that's a gameplay outcome, not a bug.

4. **Sprout requested on a cell with no loaded chunk.** The adjacency check (which uses `WorldQuery`-style global-cell lookups) is what determines legality. If it passes, auto-create the chunk. Today's unconditional auto-create in `CreateTile` was wrong under these rules and moves into `TrySpawnSprout`.

5. **Right-click held / spammed on the same cell.** First click sprouts (state = Sprouting). Subsequent edge-triggered clicks during growth hit the "state ≠ Empty" guard and no-op. Click after finalize hits the same guard (state = Solid).

6. **Sprout direction with multiple solid neighbors.** Priority order (below > left > right > above) determines a single parent. If a sprout could grow from multiple directions, only the highest-priority one wins. Predictable; no random choice.

7. **Sprout spawned overlapping a moving platform.** Same situation as today's `CreateTile` overlapping a platform — the moving platform's sweep against the new solid surface resolves it. Fine.

8. **Two sprouts that share a corner only (diagonal).** Allowed — 4-neighbor adjacency rule. Lets the player build diagonally without waiting per-tile.

9. **Destroy on a sprouting tile.** Not in scope. Reasonable behavior: `DestroyTile` switches on state — if `Sprouting`, drop the sprout from `_activeSprouts` and reset to `Empty`; if `Solid`, today's behavior. Note it; defer.

10. **Headless sim / tests.** `SimRunner` (used by `MTile.Tests`) doesn't call `TickSprouts`. Add it to the sim step so tests stay equivalent.

11. **Sprout `Center` overshoot.** `Vector2.Lerp` is clamped via `MathF.Min(1f, Age / Lifetime)` so a frame with `dt > Lifetime` doesn't overshoot. The body sees a stationary polygon at `EndCenter` for any frames between "Age first reached Lifetime" and finalization — which is at most one frame given finalization happens the same call.

## New Files / Edits

| File | Change |
|---|---|
| `World/Tile.cs` | Change to `{ TileState State; TileSprout Sprout; }` with `IsSolid` getter. Add `TileState` enum. |
| `World/TileSprout.cs` | **NEW** — translating-square sprout: start/end centers, constant velocity & polygon. |
| `World/ChunkMap.cs` | Remove `CreateTile`. Add `TrySpawnSprout`, `TickSprouts`, `_activeSprouts`, `ActiveSprouts`. Update `ShapesInRect`/`IsSolidAt`. |
| `World/TileQuery.cs` | Update predicates to check `State == Solid` instead of `IsSolid` field. Add `IsCellInState(globalTx, globalTy, state)` helper. |
| `Character/MovementConfig.cs` | `SproutLifetime`. |
| `Game1.cs` | `HandleBuildInput` calls `TrySpawnSprout`. `Update` calls `_chunks.TickSprouts(dt)` before `_player.Update`. `Draw` renders sprouts. `DrawChunk` checks new state field. |
| `MTile.Tests/Sim/SimRunner.cs` | Call `chunks.TickSprouts(dt)` in the sim step. |

Note on the `Tile.IsSolid` getter: keeps the existing call sites compiling. Once the state machine grows further (e.g. damaged tile, growing tile from another mechanic), revisit callers and pick the right state predicate per site.

## Migration Order

1. **Tile state refactor first**, no behavior change. Add `TileState` enum + `Sprout` field, plumb `IsSolid` getter, update every reader (`TileQuery`, `ChunkMap`, drawing). Build + tests green.
2. **Add `TileSprout` + `TrySpawnSprout` + `TickSprouts`** (not yet plugged into physics or input). Sprout exists in memory and progresses, but the world doesn't see it.
3. **Plug sprouts into `ShapesInRect` / `IsSolidAt`.** Bodies see the moving square. Verify: standing on a solid tile, spawn a sprout next to you growing horizontally — body collides with the moving edge. Spawn a sprout growing up from under you — body is lifted.
4. **Wire right-click** to `TrySpawnSprout`, replacing the immediate `CreateTile` call. Walk around, build outward from the level, watch sprouts grow and finalize.
5. **Adjacency stress tests**: spam, build across chunk boundary, build diagonals, build above an opponent. Verify the rules.
6. **Tune `SproutLifetime`** against feel.

## Closed Questions (answered)

- **Lifetime units** — seconds. `SproutLifetime = 0.1f`.
- **Velocity reporting** — constant `(EndCenter - StartCenter) / Lifetime`. Resolved by the moving-square model: nothing special, sweep handles it as a moving rectangle.
- **Push direction** — structural, set by parent-tile priority. No body-position-dependent fallback.

## Still Open

- **Multiple sprouts per click?** Not now. One click → one sprout, edge-triggered.
- **Visual polish on finalize?** Future, not in this plan.
- **Build mode toggle / destroy?** Right-click is unused elsewhere; defer.
- **Should the sprout's polygon participate in the `Friction` cap that `SurfaceContact` carries today?** Default to today's friction for floor-y normals, but worth verifying once we land it — a body riding a sprout that's translating sideways will see the sprout's tangential velocity as carry, which is the desired behavior; no change needed if friction is computed against relative tangential velocity (which is what `PhysicsWorld.ApplyFrictionAtImpact` already does).
