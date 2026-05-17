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

### A. `ISolidShapeProvider` — a registry of shape providers

The world doesn't track "tiles plus a side-list of dynamic surfaces" (which makes `ChunkMap` a special case). It tracks a list of **providers** that can each answer "what solid shapes are in this region?". `ChunkMap` is one provider; future shape-generating entities (growing blocks, moving platforms, destructible debris) each implement the same interface and register.

```
interface ISolidShapeProvider
    IEnumerable<SolidShapeRef> ShapesInRect(BoundingBox region)
    bool                       IsSolidAt(float x, float y)
```

The world-level `SolidShapesInRect` / `IsSolidAt` queries fan out across all registered providers.

`SolidShapeRef` is a small struct carrying everything a caller needs to act on the shape *without reaching back to the provider*:

```
struct SolidShapeRef
    float       WorldLeft, WorldTop, WorldRight, WorldBottom   // AABB, matches existing checker filters
    Vector2     Position                                       // center
    Vector2     Velocity                                       // (0,0) for static tiles; nonzero for moving surfaces
    Polygon     Polygon                                        // for SAT/sweep — singleton TileShape for tiles, per-instance for dynamic
```

Crucially **`Velocity` is part of the ref**, not something the sweep code reaches back to the provider for. The relative-velocity sweep (§2D) needs it for every candidate shape, and we don't want the per-shape callback.

Tiles don't get promoted into heap allocations — `ChunkMap`'s provider implementation materializes a `SolidShapeRef` view on demand from `TileRef` data (singleton `TileWorld.TileShape`, `Velocity = Vector2.Zero`).

### B. Unified spatial query

`TileQuery` (or whatever we end up calling the world-level façade) exposes:

```
SolidShapesInRect(world, BoundingBox region) → IEnumerable<SolidShapeRef>     // fan-out across providers
IsSolidAt(world, x, y)                       → bool                            // fan-out across providers
```

Both go through the same abstraction. The exit-edge / drop-edge scanners use `IsSolidAt` for point probes; the checkers use `SolidShapesInRect` for region probes. Either way, all shape kinds participate equally.

The existing `SolidTilesInRect` (returning `TileRef`) stays as a tile-only fast path for the physics broadphase, which doesn't care about the unified view.

For dynamic-surface storage inside individual providers, a flat list is fine (O(1)–O(10) per provider during normal gameplay). Indexing is a future optimization, not a phase-1 concern.

### C. Constraints carry no parent state — everything re-queries

Constraints stay as absolute `Vector2 Position + Normal + MinDistance`, with **zero identity tied to the surface that spawned them**. Every "is this surface still here?" check goes through the world query. Two consequences:

- **State-owned constraints** (`StandingState._ground`, `WallSlidingState._wall`, etc.) get re-pinned every frame from a fresh `TryGetGround / TryGetWall / TryGetCeiling`. Most states already do this; the audit work (phase 3) is fixing the few that don't — `DropdownState._ground` is the obvious one.
- **Collision-spawned constraints** (`SurfaceDistance` from `PhysicsWorld.UpdateSurfaceConstraint`) get pruned by the same world-query pattern. `WorldHasSurface` already does this for tiles — it just needs to route through the new unified query so it sees dynamic surfaces too.

This means the question "what happens when the surface under me changes or disappears?" has exactly one answer: next query, no surface (or different surface) → state's refresh updates the constraint, or `CheckConditions` returns false and the body falls. No stale handles to drop, no source identity to track.

### D. Sweep against moving surfaces is a single relative-frame swept-collision

`Collision.Swept(polyA, posA, displacementA, polyB, posB)` against a static B is mathematically identical to the same call with B moving at `displacementB`, *provided* `displacementA` is replaced by `displacementA − displacementB`. The frame-of-reference shift makes the moving-B case fall out of the existing routine for free.

Two tiny touches:

1. **Pass `displacementB` to the sweep** (or do the subtraction at the call site — cosmetic). The collision primitive itself doesn't change.
2. **Resolve velocity in the relative frame.** Today: `body.Velocity -= vn * hitNormal` (zero the normal component, body stops dead). Generalized: `body.Velocity -= (vn_body − vn_surface) * hitNormal` — zero the *relative* normal component. For a static surface `vn_surface = 0` and behavior is unchanged. For a rising platform, the body emerges moving at the platform's vy along the contact normal. **That's the carry. It's not a separate step — it's what resolving in the relative frame *means*.**

#### Update ordering

Advance surface positions *first*, then sweep bodies against them. Conceptual model: "world ticks forward, body reacts." The sweep math then operates on `surface.Position` (post-move) with the body's relative displacement `bodyDisp − surfaceDisp` — equivalent to sweeping against the pre-move position with the surface's velocity, just bookkept the other way. Either works; advancing surfaces first matches "the body cannot interrupt a surface's motion" semantics and avoids the inverse case (body sweeps, ends up where a surface would have moved to, surface's tick blocks itself on the body).

#### Embedded handling (fast surfaces / spawn overlap)

If a body ends up overlapping a surface at the start of a step — surface moved faster than the sweep can chase (relative displacement greater than body width in the sweep direction), or surface spawned inside the body — the existing `hitNormal == Vector2.Zero` fallback in `ResolveChunkCollisionsSwept` already handles this for tiles: it punts to the discrete `ResolveChunkCollisions`, which uses `Collision.Check` for an SAT MTV and pushes the body out by it. Extend the same fallback to dynamic surfaces:

1. Detect overlap via `Collision.Check` against the surface.
2. Push the body out by the MTV.
3. Set the body's velocity along the MTV normal to match the surface's velocity along the same normal (so the body emerges riding the surface, just one frame later than a clean sweep would've placed it).

Costs a frame of visual snap; doesn't blow up. If a real scenario shows tunneling becomes common, the next-step mitigation is substepping the world tick when any surface's per-frame displacement exceeds a fraction of the smallest body dimension. Defer until needed.

#### What this doesn't solve

**Converging surfaces.** Two moving surfaces closing on a body, body squished between. No physical resolution — something has to give. Standard game answers: kill the body, or push it perpendicular to both surfaces' velocities (the remaining free axis). Not relevant to the moving-rectangle scenario; flag and defer.

---

## 3. Changes by component

### `Physics/PhysicsWorld.cs`
- Take a world handle that exposes the registered providers (`ISolidShapeProvider`s) plus a top-level `SolidShapesInRect` / `IsSolidAt`.
- Advance every shape provider's surfaces (call their per-frame tick) **before** sweeping bodies.
- `ResolveChunkCollisionsSwept` iterates the unified shape view from `SolidShapesInRect`. For each candidate, sweep the body's displacement in the relative frame (`bodyDisp − shape.Velocity * dt`) — same `Collision.Swept` routine, just one extra term in the input. Velocity resolution on hit uses the relative normal component (§2D).
- The existing `hitNormal == Vector2.Zero` fallback handles embedded cases; route its discrete push-out through the unified view so it covers dynamic surfaces too.
- `WorldHasSurface` (used to prune stale `SurfaceDistance`s) routes through the unified `IsSolidAt`. No identity bookkeeping on the constraints themselves — they're just position + normal, validated each frame against the world.

### `World/TileQuery.cs`
- `ChunkMap` becomes the first `ISolidShapeProvider`. Its `SolidShapesInRect` wraps the existing `SolidTilesInRect` and materializes a `SolidShapeRef` per `TileRef` (singleton `TileWorld.TileShape`, `Velocity = Vector2.Zero`). Its `IsSolidAt` wraps the existing one.
- Top-level `TileQuery.SolidShapesInRect(world, region)` / `IsSolidAt(world, x, y)` fan out across all registered providers.
- The existing tile-only overloads stay for the physics broadphase fast path.
- `IsTopExposed` / `IsBottomExposed` stay tile-only for now (they're 1-tile-offset point probes). Generalizing them to "is there *any* solid shape touching that face" matters for corner detection on tiles about to be capped by a growing block — flag, don't block. The first growing-block scenarios don't hit this.

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
9. **`PhysicsWorld.WorldHasSurface`** — used to prune dead `SurfaceDistance` constraints. Iterates the chunk grid only; once routed through the unified `IsSolidAt` it'll see dynamic surfaces too. Until then, constraints anchored on departed dynamic surfaces linger one frame and get pruned on the next distance failure. Acceptable.
10. **Converging surfaces.** Two moving surfaces closing on a body with the body squished between them. Not relevant for the first moving-rectangle scenario; will matter once we have multiple independent moving things. Policy is a game-design call (kill the body? push out along the perpendicular axis?) — flag, don't solve preemptively.
11. **Tests** — every sim test stages a static `ChunkMap` and runs the player against it. None of them exercise moving surfaces. The first dynamic-surface scenario needs a fresh test harness with the ability to spawn / move / destroy surfaces alongside the player.

---

## 7. Suggested phasing

Each phase ends with a checkpoint: existing behavior on existing terrain unchanged, plus one new capability.

1. **Provider abstraction, no behavior change.** Introduce `ISolidShapeProvider`, `SolidShapeRef`, and the world-level `SolidShapesInRect` / `IsSolidAt` fan-out. Implement the provider on `ChunkMap`. Register it with the world. Don't change any caller yet. *Checkpoint: tests green, no observable change — every existing query has the same answer it had before.*
2. **Constraint-refresh audit.** Make sure every state-owned ground/wall/ceiling constraint is refreshed from a fresh world query each frame, and that every collision-spawned `SurfaceDistance` is pruned by the unified `IsSolidAt`. `DropdownState._ground` is the known offender; expect to find one or two more. *Checkpoint: still no behavior change, but the invariant "no constraint outlives the world's answer to the query that justified it" holds across all states.*
3. **Tile destruction.** Add `ChunkMap.DestroyTile`. With the audit done, this should just work. *Checkpoint: standing on a tile, destroying it, body falls cleanly. Standing under a ceiling, destroying it, ceiling-dependent constraints drop. WallSliding next to a wall, destroying the wall, body falls.*
4. **Checker generalization + non-grid edge scans.** Route every checker through `SolidShapesInRect`. Verify tile-only scenes are byte-identical. Rewrite `CeilingChecker.TryFindExitEdge` and `GroundChecker.TryFindDropEdge` from "scan tile columns until empty" to "find the bounding-box edge of the surface, in direction `dir`." For tile slabs the result is identical to the column walk; for a single dynamic surface it's `surface.Bounds.Side(dir)`. *Checkpoint: a hand-placed static "phantom block" (a dynamic surface with `Velocity = 0`) is indistinguishable from a real tile to every state — standing, walking, vaulting, dropping off, ducking under all work on it.*
5. **Relative-velocity sweep.** One parameter through `Collision.Swept` (or one subtraction at the call site), one minus sign on the velocity-resolution line in `ResolveChunkCollisionsSwept`, extend the embedded-overlap fallback to dynamic surfaces. *Checkpoint: a hand-built "elevator" surface translating up at fixed velocity carries the body without separating or sinking; the body can walk along the top tangentially; jumping off works; landing on it from above works.*
6. **The moving rectangle as a concrete first scenario.** Wire up a `MovingRectangle` (or equivalent) that oscillates up and down through a sandbox. Player should be able to jump onto it, ride it, jump off, jump back on. *Checkpoint: a play-test scenario where the rectangle is the gameplay element, not just a test fixture.*
7. **Growing block.** With the moving rectangle proven, `GrowingBlock` is "moving rectangle with a one-shot lifecycle" — translates up over N frames, commits to the chunk, removes itself. *Checkpoint: spawning a block underfoot lifts the player; spawning one in clear air is harmless; spawning one against a ceiling stalls.*
8. **Polish.** Crush behavior policy, corner-detection-during-growth edge cases, converging-surface squish handling.

---

## 8. Design decisions

### Resolved

- **Constraint binding.** Query-driven refresh. No identity, no source handles, no parent state on either state-owned or collision-spawned constraints. Validity = "the world still answers yes to the query that justified me."
- **Surface identity.** Not needed in constraints (per above). Providers can use whatever identity they want internally; tile-based callers keep using `TileRef`.
- **Update ordering.** Surfaces tick first, bodies sweep against post-move surface positions using relative-velocity math.
- **Sweep math.** Single relative-frame `Collision.Swept`. Carry is the natural consequence of resolving in the relative frame, not a separate step.
- **Embedded-body handling.** Reuse the existing discrete-push-out fallback (`hitNormal == Vector2.Zero` branch), extended to dynamic surfaces, with velocity matching on push-out so the body emerges riding the surface.

### Still open

- **Polygon scale vs position translate** for the growing block? I lean translate (constant polygon, velocity falls out naturally, single source of truth for sweep math). Decide at phase 7.
- **What happens when growth is blocked?** Block rising into a ceiling, or into a body that can't move. Stall? Crush? Cancel? Probably stall is the right default — `Velocity.Y = 0` if the sweep reports zero clearance — and let policy be a config flag later.
- **Dynamic surface ↔ dynamic surface collisions?** Probably "no, not yet" — but the abstraction shouldn't actively prevent it.
- **Converging-surface squish policy.** What happens to a body between two converging surfaces. Game-design call, not relevant until there are multiple independent moving things.
- **Spatial index for dynamic surfaces.** Flat list inside each provider is fine until there are dozens. Beyond that, a uniform grid (or bucketing by chunk) is the obvious next step. Don't pre-optimize.

---

## TL;DR

`ISolidShapeProvider` is the central abstraction. `ChunkMap` is one provider among many; future dynamic-surface entities register the same way. The world-level `SolidShapesInRect` / `IsSolidAt` fan out across providers, and the result type carries position + velocity + bounds so the sweep doesn't reach back per-shape.

Constraints carry no parent state. Every "is this surface still here?" check goes through the world query — state-owned constraints get re-pinned each frame, collision-spawned ones get pruned by the same mechanism. The audit work in phase 2 is finding the few states that don't already refresh (e.g. `DropdownState._ground`).

The sweep math is not new code, just a frame-of-reference shift: pass `displacementB` to `Collision.Swept`, and zero the *relative* normal velocity on resolution instead of the absolute. Carry falls out for free. Embedded bodies are caught by the existing discrete-push-out fallback, extended to dynamic surfaces.

The phasing chains: abstractions → constraint audit → tile destruction → checker generalization → relative-velocity sweep → moving rectangle (the first real scenario) → growing block (a one-shot lifecycle on top of moving rectangle) → policy polish. The trickiest *gameplay* question isn't in the physics — it's deciding what happens to a body wedged between a growing block and a ceiling. Defer.
