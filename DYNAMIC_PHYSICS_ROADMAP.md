# Dynamic Physics Roadmap

Goal: support **tile destruction**, **tile creation as a multi-frame growing block** (degenerate line on a parent tile → full tile → committed to chunk), and the generalization that enables both — **moving surfaces as first-class collision participants**.

This roadmap is a survey, not a plan to execute. It maps where the codebase currently bakes in "the world is a static `ChunkMap` of `Tile`s", what would need to grow a moving-surface abstraction, and how the movement system is likely to break along the way.

---

## 1. Where "world = static tiles" lives today

Five places assume the only solid things are `Chunk` tiles, and each one will need to either be generalized or routed through a new abstraction.

| Concern | Implementation | Tile-only because… |
|---|---|---|
| Body↔world collision | [Physics/PhysicsWorld.cs](Physics/PhysicsWorld.cs) — `ResolveChunkCollisions` and `ResolveChunkCollisionsSwept` | Iterates `ChunkMap` chunks directly, builds `nextPos` against `TileWorld.TileShape` at each solid cell. |
| Spatial queries (broadphase) | [World/TileQuery.cs](World/TileQuery.cs) — `SolidTilesInRect`, `IsSolidAt`, `IsTopExposed`, `IsBottomExposed` | Only consults `Chunk.Tiles[x,y].IsSolid`. Returns `TileRef` (16px AABB anchored on the tile grid). |
| Surface detection | [Character/CeilingChecker.cs](Character/CeilingChecker.cs), [GroundChecker.cs](Character/GroundChecker.cs), [WallChecker.cs](Character/WallChecker.cs), [ExposedUpperCornerChecker.cs](Character/ExposedUpperCornerChecker.cs), [ExposedLowerCornerChecker.cs](Character/ExposedLowerCornerChecker.cs) | All five build a region via `body.Bounds.StripXxx(...)` and call `TileQuery.SolidTilesInRect`. None of them know about anything but tiles. |
| Exit-edge / corner scans | `CeilingChecker.TryFindExitEdge`, `GroundChecker.TryFindDropEdge` | Walk integer **tile columns** from `bodyCol = floor(body.X / TileSize)` outward, calling `TileQuery.IsSolidAt(col * TileSize + 8, probeY)`. Tile-grid quantized by construction. |
| Constraint anchoring | [Physics/PhysicsContact.cs](Physics/PhysicsContact.cs) — `SurfaceContact.Position/Normal/MinDistance`, `FloatingSurfaceDistance`, `PointForceContact`, and [Character/SteeringRamp.cs](Character/SteeringRamp.cs) `Corner` | All store **absolute** `Vector2` positions. They never re-resolve against the source surface. |

Two helpers that already point the way toward generalization:

- **[Physics/BoundingBox.cs](Physics/BoundingBox.cs)** — the float-precision AABB / region type already abstracts probe geometry. Adding moving surfaces means giving them a `BoundingBox` too (which `Polygon.GetBoundingBox(pos)` already produces). The checker pipeline of "build a region → query → filter" is shape-agnostic.
- **State-driven constraint refresh** — `StandingState`, `WallSlidingState`, `LedgeGrabState`, `CoveredJumpState`, `DropdownState` already re-read their ground/wall contact each frame and copy `Position/Normal/MinDistance` onto the held constraint. This pattern extends naturally to a moving surface — the source has moved, the copy gets refreshed.

---

## 2. Abstractions to introduce

### A. `ISolidShape` (or `Surface`)

The thing every collider has in common: a polygon at a position with an optional velocity, plus an identity that survives frame to frame.

```
interface ISolidShape
    Polygon  Polygon      // could be a singleton TileShape for tiles, or per-instance for dynamic surfaces
    Vector2  Position     // center, world space
    Vector2  Velocity     // (0,0) for tiles; nonzero for moving surfaces
    BoundingBox Bounds    // typically Polygon.GetBoundingBox(Position) but cacheable
    // identity — used by constraints to verify their source is still alive
```

Tiles satisfy this implicitly: `Polygon = TileWorld.TileShape`, `Position = TileCenter(chunk, idx)`, `Velocity = 0`. We don't have to *promote* tiles into heap allocations — `TileRef` already carries enough to materialize an `ISolidShape` view on demand.

Dynamic surfaces (growing blocks, eventually moving platforms) satisfy it directly as small classes.

### B. Unified spatial query

```
TileQuery.SolidShapesInRect(world, BoundingBox region) → IEnumerable<SolidShapeRef>
TileQuery.IsSolidAt(world, x, y)                       → bool, considers both tiles and dynamic surfaces
```

`SolidShapeRef` is a small struct with `WorldLeft/Top/Right/Bottom` (so existing checker filters keep working) plus a discriminator + back-reference for identity. The existing `TileRef`-returning overload stays as a fast path for tile-only callers (e.g. the physics broadphase).

For dynamic surfaces, the world needs a spatial index — even a flat list is fine to start (there will be O(1)–O(10) of them at any time during normal gameplay).

### C. Constraint binding to a source

Two viable shapes:

1. **Source handle + offset** — constraint stores `(ISolidShape source, Vector2 localAnchor)`. Each step, world position is `source.Position + localAnchor` (with rotation if ever needed). When `source` is removed, the constraint is auto-detached. Cleanest but requires plumbing a handle through every contact type.
2. **State-owned refresh** — keep constraints as absolute positions, but the owning movement state is responsible for re-pinning them every frame against the current source. Already what `StandingState` does today.

Option 2 is the lower-disturbance path and matches what already exists. Option 1 is cleaner long-term but I'd defer it until the second or third moving-surface feature reveals what handle granularity actually helps.

Either way: the **collision-spawned** constraints (`SurfaceDistance` added by `PhysicsWorld.UpdateSurfaceConstraint`) need to know which surface they came from so they can be invalidated when that surface vanishes (tile destruction) or moves (growing block).

### D. Sweep with relative velocity

`Collision.Swept(polyA, posA, displacementA, polyB, posB, 0)` currently treats the second shape as immobile. For a moving surface the sweep is on the **relative** displacement `displacementA − surface.Velocity * dt`. After resolution, the body's velocity normal component should be matched to the surface's velocity normal component (so a body resting on a rising block rises with it), not zeroed.

This is a small math change to `Collision.Swept`'s call site in `ResolveChunkCollisionsSwept`. The collision primitive itself doesn't need to change — only what we pass into it and how we apply the result.

---

## 3. Changes by component

### `Physics/PhysicsWorld.cs`
- Take a `World` (or expanded `ChunkMap`) that exposes both tiles and dynamic surfaces.
- `ResolveChunkCollisionsSwept` iterates dynamic surfaces alongside tiles. For each, sweep with relative velocity.
- "Carry" behavior: when a sweep collision resolves against a moving surface, set the body's velocity component along the surface's normal to match the surface's velocity component (so the body rides along instead of getting punched off).
- `UpdateSurfaceConstraint` records the source surface on the added `SurfaceDistance`. The stale-removal loop already checks distance + `WorldHasSurface`; extend it to also drop constraints whose source is gone.

### `World/TileQuery.cs`
- Add the `SolidShapesInRect` / `IsSolidAt(world, …)` overloads described in §2B.
- Generalize `IsTopExposed` / `IsBottomExposed` from "is the neighboring tile empty" to "is there any solid shape touching that face." Right now both are 1-tile-offset point probes — they'll work for tile↔tile checks but miss a growing block touching the top of an existing tile. Probably fine to leave them tile-only for now; ramp/corner detection on a tile that's about to be capped by a growing block is an edge case worth flagging but not blocking.

### Checkers
The five checkers are almost ready for this. Each one:
1. Builds a `BoundingBox` probe via `body.Bounds.StripXxx(...)`.
2. Calls `TileQuery.SolidTilesInRect(chunks, probe)`.
3. Filters tiles (e.g. `tile.WorldBottom > playerHead`, `IsTopExposed`, etc.) and aggregates.

Swap step 2 to `SolidShapesInRect` and the filter in step 3 works on `WorldLeft/Top/Right/Bottom` either way. The tricky bit is that two of the corner-finders (`CeilingChecker.TryFindExitEdge`, `GroundChecker.TryFindDropEdge`) **walk integer tile columns** — they need a redesign for non-grid-aligned surfaces:

> Instead of "scan tile columns until empty," do "find the bounding-box edge of the surface I'm under/on, in direction `dir`." For a tile slab this is identical to the current column walk; for a single dynamic surface it's just `surface.Bounds.Side(dir)`.

That makes them surface-anchored rather than grid-anchored, which is the right model going forward anyway.

### Constraints / `SteeringRamp`
- `SteeringRamp.Corner` is set absolute on Enter and refreshed each frame by the owning state (`ParkourState`, `CoveredJumpState`, `DropdownState`) via the corner-finder checkers. As long as the corner-finder returns the *current* corner position of a moving surface, the ramp follows. Behavior stays the same.
- Same story for `FloatingSurfaceDistance`: the owning state already refreshes `Position/Normal/MinDistance` each frame. If the surface moves, the constraint moves.
- Watch out for `_ground = ground; ctx.Body.Constraints.Add(_ground);` patterns where the constraint is held but never refreshed — `DropdownState` is one of these. Today it doesn't matter because tiles don't move; with moving surfaces it will.

---

## 4. Tile destruction

The smallest change of the three features.

- `ChunkMap.DestroyTile(worldX, worldY)` (or by chunk+index): set `IsSolid = false`. That's it from the world side.
- Constraints anchored on the now-empty tile go stale:
  - The collision-spawned `SurfaceDistance` constraints are pruned by `PhysicsWorld`'s `Constraints.RemoveAll` block, which calls `WorldHasSurface` — already correct for "wall isn't there anymore."
  - `FloatingSurfaceDistance`s held by movement states are refreshed each frame via `TryGetGround / TryGetCeiling / TryGetWall`. When the underlying probe returns false, the state's `CheckConditions` already handles it (e.g. `StandingState` exits to Falling).
- Likely breakage: a state that holds a constraint but doesn't refresh it (see "Watch out for…" above) will keep the body floating on a tile that no longer exists. Audit the `_ground`/`_wall`/`_ceiling` setters across states before flipping the destruction feature on.

---

## 5. Tile creation (growing block)

A `GrowingBlock` is a single-purpose moving surface.

- Spawned at a target chunk cell `(cx, ty)` with the cell below already solid as the parent.
- `Position` lives at the cell's *centerline* (`parent_top - 8` in y).
- `Polygon`: simplest model is a polygon whose vertical half-extent grows from 0 (degenerate line at `parent_top`) to `TileSize/2 = 8` (full tile) over N frames. Equivalent alternative: keep polygon at full size but shift `Position` upward from `parent_top + 8` to `parent_top - 8` — same on-screen shape, no per-frame polygon mutation, and `Velocity = (0, -growthSpeed)` falls out naturally for the carry math. I'd start there.
- On reaching the final `Position`: write `chunk.Tiles[cx,ty].IsSolid = true`, remove the `GrowingBlock` from the world's dynamic-surface list. Bodies riding it transparently stay because the tile occupies the same space the block did at frame N.

What needs to be right for this to feel good:
1. **Carry**: a body resting on the block must move up with it. Handled by the relative-velocity sweep in `ResolveChunkCollisionsSwept` (§2D) plus matching the body's vy to the surface's vy on resolution.
2. **Spawn overlap**: on frame 0 the block is *inside* the parent tile (its polygon is below the parent's top). Collision sweep will see the body's bottom in contact with parent_top — that contact already worked before the block existed. The block adds no overlap with the body on frame 0. As it rises into the body, the carry kicks in.
3. **Body trapped above by ceiling**: if there's a ceiling 1 tile up and the player is standing on the parent, the block rising will crush them into the ceiling. Decide: cancel growth, crush, push the body sideways out from under, or just accept the wedge. **Defer the policy decision** — first pass can let the body wedge (it'll stop rising when the ceiling collision zeros vy).

---

## 6. Movement-system breakages to expect

Ordered roughly by how likely you are to hit them when first turning on dynamic surfaces.

1. **Stale `_ground` in DropdownState** — adds the ground constraint and never refreshes it. Fine for static tiles, broken for a tile that's growing under the body or being destroyed under it.
2. **`SteeringRamp.Corner` going stale between frames** when the corner-finder hasn't been re-queried. `ParkourState`, `CoveredJumpState`, `DropdownState` all refresh in `Update`, but if a refresh path early-returns (e.g. `CoveredJumpState` does in phase 2) and the surface keeps moving, the ramp anchors to a dead position. Phase 2 already drops the ramp; double-check the other states.
3. **`CeilingChecker.TryFindExitEdge` / `GroundChecker.TryFindDropEdge` against a single dynamic surface** — current column-walk logic will misreport edges since the surface isn't tile-grid-aligned during growth. Surface-anchored variant fixes this.
4. **`WallChecker`'s vertical y-inset of 2px** — picks "wall" tiles that overlap the body's mid-section. A growing block growing *through* the body's side could read as a wall for one frame. Should be fine functionally (the body gets pushed) but may briefly trigger `WallSlidingState`.
5. **`IsActuallyGrounded` (in `WallSlidingState`)** uses `dist ≤ 2*Radius + 2`. A rising surface raises the body's effective rest height, but the held ground constraint won't reflect that until it's refreshed. `WallSlidingState` doesn't hold a ground constraint, so this is fine — flagging for symmetry.
6. **`EnvironmentContext` caching** — caches all probes for the duration of one `PlayerCharacter.Update`. As long as bodies and surfaces are stepped *between* `Update`s rather than mid-update, the cache is safe. Worth a sanity assertion (positions don't change inside `Update`).
7. **`ExposedUpperCornerChecker.MinBodyDepthBelowCorner` and `MaxTopProbeDistance`** — both are body-position-relative thresholds tuned for static tile geometry. Should still work but if a growing block's top is rising past the body during a vault attempt, the corner will appear to "approach" the body each frame and the vault may engage/disengage rapidly. Worth a play-test on a grow-into-vault scenario.
8. **`SteeringRamp.ResolveVelocity` rescales `|v|` to its own magnitude each step** — if the body's velocity has been augmented by a moving surface's carry (gained vy), the rescale could fight the carry. Probably needs the carry component to be applied *after* the redirect, or excluded from the rescale.
9. **`PhysicsWorld.WorldHasSurface`** — used to prune dead `SurfaceDistance` constraints. Iterates the chunk grid only; won't notice a wall provided by a dynamic surface that just despawned. Constraints anchored on departed dynamic surfaces will linger one frame, then get pruned when their distance test fails. Acceptable; flag if it bites.
10. **Tests** — every sim test stages a static `ChunkMap` and runs the player against it. None of them exercise moving surfaces. The first dynamic-surface scenario needs a fresh test harness with the ability to spawn / move / destroy surfaces alongside the player.

---

## 7. Suggested phasing

Each phase ends with a checkpoint: existing behavior on existing terrain unchanged, plus one new capability.

1. **Surface abstraction, no behavior change.** Introduce `ISolidShape` (or just a `SolidShapeRef` struct), add `SolidShapesInRect` / `IsSolidAt(world, …)` overloads, add an empty dynamic-surface list to the world. Don't change any caller yet. *Checkpoint: tests green, no observable change.*
2. **Dynamic surface collision in the broadphase.** `PhysicsWorld.ResolveChunkCollisionsSwept` iterates dynamic surfaces. Add a temporary test surface — a static "phantom block" — placed manually. Confirm the body collides with it identically to a tile. *Checkpoint: a phantom block at (X,Y) is indistinguishable from a real tile at (X,Y).*
3. **Tile destruction.** Add `ChunkMap.DestroyTile`. Audit movement states for unrefreshed ground/wall/ceiling constraints; fix as found. *Checkpoint: standing on a tile, destroying it, body falls cleanly.*
4. **Checker generalization.** One checker at a time: route through `SolidShapesInRect`. Verify tile-only scenes are byte-identical. Then verify a dynamic surface is detected the same. The exit-edge scanners get the surface-anchored rewrite here. *Checkpoint: standing on a dynamic surface, walking off it, dropping off it, etc. all work.*
5. **Moving surfaces (sweep + carry).** Implement relative-velocity sweep and the carry step in `ResolveChunkCollisionsSwept`. Test with a hand-built "elevator" surface — translate it up at fixed velocity, verify the body rides it without separating or sinking. *Checkpoint: moving platforms work in a sandbox.*
6. **Growing block.** Implement `GrowingBlock` as a `Position`-translating dynamic surface that commits to the chunk on completion. Wire `ChunkMap.CreateTile` to spawn one. *Checkpoint: spawning a block underfoot lifts the player; spawning one next to the player without touching them is harmless.*
7. **Polish.** Decisions about crush behavior, spawn collisions with non-player bodies (when those exist), and the corner-detection-during-growth edge cases.

---

## 8. Open design questions to settle before implementation

- **Dynamic surface ↔ dynamic surface collisions?** Two growing blocks both rising at the same cell, or a growing block intersecting an existing moving platform. Probably "no, not yet" — but the abstraction shouldn't actively prevent it.
- **Constraint binding: handle vs refresh?** Option 1 vs Option 2 from §2C. I'd start with refresh (matches current pattern); revisit when the third moving-feature lands.
- **Polygon scale vs position translate** for the growing block? I lean translate (constant polygon, velocity falls out naturally, single source of truth for sweep math).
- **What happens when growth is blocked?** Block rising into a ceiling, or into a body that can't move. Stall? Crush? Cancel and refund? Probably stall is the right default — `Velocity.Y = 0` if the sweep test reports zero clearance — and let policy be a config flag later.
- **Identity for surfaces.** `object` reference? `int id`? Composite key for tiles like `(chunkPos, tileIndex)`? The constraint-source binding (Option 1) is the only place this matters; tile-based callers can keep using `TileRef` directly.
- **Spatial index for dynamic surfaces.** Flat list is fine until there are dozens. Beyond that, a uniform grid (or just bucket by chunk) is the obvious next step. Don't pre-optimize.

---

## TL;DR

The single highest-leverage change is **giving `TileQuery.SolidShapesInRect` a unified view of tiles + dynamic surfaces** and letting `PhysicsWorld.ResolveChunkCollisionsSwept` consume it. Every checker and every state then becomes shape-agnostic almost for free, because they're already routed through `body.Bounds.StripXxx` → spatial query → filter, and the filter already operates on `WorldLeft/Top/Right/Bottom`. The two scanners that walk integer tile columns (`TryFindExitEdge`, `TryFindDropEdge`) are the only checker code that needs a real rewrite for moving surfaces.

After that, tile destruction is a single `IsSolid = false` plus a constraint-refresh audit; tile creation is a `GrowingBlock` dynamic surface that translates up over N frames and commits itself to the chunk at the end. The trickiest *gameplay* question isn't in the physics — it's deciding what happens to a body wedged between a growing block and a ceiling. Defer.
