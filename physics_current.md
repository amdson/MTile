# Current Physics System

## Overview

The physics system is a tile-world rigid-body simulator using SAT (Separating Axis Theorem) collision detection. There is currently one physics body in the game: a hexagon controlled by direct arrow-key force input. No movement states are implemented yet.

---

## Core Data Structures

### `PhysicsBody` ([Physics/PhysicsBody.cs](Physics/PhysicsBody.cs))
Holds the full runtime state of a simulated body:
- `Position` — world-space center point (Vector2, pixels)
- `Velocity` — pixels/second
- `Polygon` — the collision shape (local-space convex polygon)
- `Constraints` — list of active `PhysicsContact` objects constraining the body

### `Polygon` ([Physics/Polygon.cs](Physics/Polygon.cs))
A convex polygon defined by local-space vertices. Two factory methods exist: `CreateRectangle(w, h)` and `CreateRegular(radius, sides)`. Key methods:
- `GetVertices(pos, rot)` — transforms local vertices to world space
- `GetAxes(worldVerts)` — returns edge-normal axes for SAT
- `Project(verts, axis)` — projects vertices onto an axis, returning `(min, max)` interval
- `GetBounds(pos, rot)` — returns an axis-aligned bounding box (integer pixel `Rectangle`)

### `PhysicsContact` hierarchy ([Physics/PhysicsContact.cs](Physics/PhysicsContact.cs))
Abstract base for all constraint types. Three subtypes exist:

| Type | Purpose | Used in physics step? |
|---|---|---|
| `SurfaceDistance(pos, normal, minDist)` | Hard constraint: body may not move closer than `minDist` to a surface along its normal | Yes |
| `FloatingSurfaceDistance(pos, normal, minDist)` | Soft/floating constraint: maintain a preferred height above ground | **Defined, not implemented** |
| `PointForceContact(pos)` | Force emitter at a point | **Defined, not implemented** |

---

## Collision Detection ([Physics/Collision.cs](Physics/Collision.cs))

All collision is polygon-based SAT. Three primary entry points:

**`Collision.Check(a, posA, rotA, b, posB, rotB)`** — discrete polygon-vs-polygon overlap test. Returns a `CollisionResult` with `Intersects`, the minimum translation vector `MTV` (pushes A out of B), and `Depth`. Also has `CheckCircle` and `CheckCircles` variants.

**`Collision.Swept(a, posA, rotA, displacement, b, posB, rotB)`** — swept polygon-vs-polygon test. Finds the first time of contact `T ∈ [0,1]` and the surface normal at that contact. Returns `SweptResult.NoHit` if no contact. Used for sub-step accurate collision to prevent tunneling.

---

## Physics Step ([Physics/PhysicsWorld.cs](Physics/PhysicsWorld.cs))

Two step functions exist: `Step` (discrete push-out) and `StepSwept` (swept, the one currently used). Both follow this sequence per body:

### 1. Apply gravity
```
velocity += gravity * dt
```

### 2. Apply hard surface constraints
For each `SurfaceDistance` constraint, if the body is within `minDistance` of the surface and moving toward it, zero out velocity in the normal direction:
```
if dist < minDistance and vn < 0: velocity -= vn * normal
```

### 3. Resolve tile collisions (swept path)
`ResolveChunkCollisionsSwept` moves the body from `pos` to `pos + velocity*dt` using up to 4 "bounce" sub-steps:
- Computes a swept AABB over the motion and queries all overlapping tiles in `ChunkMap`
- Calls `Collision.Swept` against each solid tile's shape
- Finds the earliest hit time `T` and its surface normal
- Slides the body to `pos + displacement*T + normal*Epsilon`, removes velocity/displacement component along the normal
- Calls `UpdateSurfaceConstraint` to register or refresh a `SurfaceDistance` for the surface just contacted
- Repeats with remaining displacement until no hits or 4 bounces used

If the body is already overlapping at the start of a sub-step (T=0, normal=zero), it falls back to discrete push-out via `ResolveChunkCollisions`.

### 4. Expire stale constraints
After moving, constraints are removed if:
- The body has drifted more than `2 * Epsilon` from the constraint's recorded position along the normal (body left the surface), OR
- `WorldHasSurface` returns false — a thin probe strip is cast in the `-normal` direction to check whether any solid tiles still exist adjacent to the body's face. If the floor was destroyed, the constraint is dropped.

### Key constants
- `Epsilon = 0.5f` pixels — used as a small separation buffer added after collision resolution, and as the threshold for stale constraint removal

---

## Chunk/Tile World

Tiles are organized in `Chunk`s (16×16 tiles, each tile 16px). `ChunkMap` is a dictionary of `Point → Chunk`. `TileWorld.TileCenter` and `TileWorld.TileShape` provide the geometry for tile collision. Only `IsSolid` tiles participate in collision.

---

## Input & Controller ([Character/Controller.cs](Character/Controller.cs))

`Controller` maintains a 32-frame ring buffer of `PlayerInput` snapshots. Each frame: `Left`, `Right`, `Up`, `Down`, `Space`, `Shift`, mouse position (screen and world). `Current` is the latest; `GetPrevious(n)` reaches back n frames.

---

## Current Game Loop ([Game1.cs](Game1.cs))

```
1. Read keyboard/mouse
2. Update Controller buffer
3. Apply arrow-key force directly to hexagon velocity
4. PhysicsWorld.StepSwept(bodies, chunks, dt, gravity)
5. Camera tracks hexagon position
```

There is no movement state machine, no character body separate from the hexagon, and no use of `FloatingSurfaceDistance`. The hexagon is pushed around by raw force and constrained only by hard tile collisions.

---

## What Is Stubbed / Not Yet Implemented

- `FloatingSurfaceDistance` — defined but never applied in the physics step
- `PointForceContact` — defined, unused
- `PlayerState.cs` — stub comment only
- `Movement.cs` — empty file
- No `Standing`, `Walking`, or any other movement state
- No ground/wall/ceiling availability checkers
- No character body (the physics body is a raw hexagon)
