# Current Physics System

## Overview

The physics system is a tile-world rigid-body simulator using SAT (Separating Axis Theorem) collision detection. A `PlayerCharacter` owns a hexagonal physics body and drives it through a movement state machine. Forces are accumulated by movement states and consumed by the physics step each frame; movement states never write to velocity directly.

---

## Core Data Structures

### `PhysicsBody` ([Physics/PhysicsBody.cs](Physics/PhysicsBody.cs))
Holds the full runtime state of a simulated body:
- `Position` — world-space center point (Vector2, pixels)
- `Velocity` — pixels/second
- `AppliedForce` — force accumulator set by movement logic each frame, applied then cleared by the physics step
- `Polygon` — the collision shape (local-space convex polygon)
- `Constraints` — list of active `PhysicsContact` objects constraining the body

### `Polygon` ([Physics/Polygon.cs](Physics/Polygon.cs))
A convex polygon defined by local-space vertices. Two factory methods: `CreateRectangle(w, h)` and `CreateRegular(radius, sides)`. Key methods:
- `GetVertices(pos, rot)` — transforms local vertices to world space
- `GetAxes(worldVerts)` — returns edge-normal axes for SAT
- `Project(verts, axis)` — projects vertices onto an axis, returning `(min, max)` interval
- `GetBounds(pos, rot)` — returns an axis-aligned bounding box (integer pixel `Rectangle`)

### `PhysicsContact` hierarchy ([Physics/PhysicsContact.cs](Physics/PhysicsContact.cs))
Abstract base for all constraint types:

```
PhysicsContact
├── SurfaceContact(pos, normal, minDist)   — abstract base for surface constraints
│   ├── SurfaceDistance                    — hard contact, created automatically by tile collision
│   └── FloatingSurfaceDistance            — soft contact, owned and managed by movement states
└── PointForceContact(pos)                 — defined, unused
```

Both `SurfaceDistance` and `FloatingSurfaceDistance` expose `Position`, `Normal`, and `MinDistance`. The physics step treats them identically for velocity clamping; they differ only in lifetime management (see below).

---

## Collision Detection ([Physics/Collision.cs](Physics/Collision.cs))

All collision is polygon-based SAT. Two primary entry points:

**`Collision.Check(a, posA, rotA, b, posB, rotB)`** — discrete overlap test. Returns `CollisionResult` with `Intersects`, the minimum translation vector `MTV` (pushes A out of B), and `Depth`.

**`Collision.Swept(a, posA, rotA, displacement, b, posB, rotB)`** — swept polygon-vs-polygon test. Finds the first contact time `T ∈ [0,1]` and surface normal. Used for sub-step accurate collision to prevent tunneling.

---

## Physics Step ([Physics/PhysicsWorld.cs](Physics/PhysicsWorld.cs))

`StepSwept` is the active step function. Per body, each frame:

### 1. Apply accumulated force and gravity
```
velocity += AppliedForce * dt
AppliedForce = Zero
velocity += gravity * dt
```
`AppliedForce` is set by movement logic, then cleared here so it doesn't accumulate.

### 2. Clamp velocity against active surface constraints
For each `SurfaceContact` (both `SurfaceDistance` and `FloatingSurfaceDistance`): if the body is within `minDistance` of the surface and moving toward it, zero out the velocity component along the normal:
```
if dist < minDistance and vn < 0: velocity -= vn * normal
```

### 3. Resolve tile collisions and floating contacts (swept path)
`ResolveChunkCollisionsSwept` moves the body from `pos` toward `pos + velocity*dt` in up to 4 bounce sub-steps:
- Computes a swept AABB over the motion and queries all overlapping solid tiles in `ChunkMap`
- Calls `Collision.Swept` against each tile shape, tracking the earliest hit time `minT` and its normal
- Also sweeps each `FloatingSurfaceDistance` constraint as a plane: `t = (minDistance - distNow) / dot(displacement, normal)`. If this `t` is less than the current `minT`, the floating contact "wins" as the earliest hit
- Slides the body to the earliest hit position plus `normal * Epsilon`, removes the velocity/displacement component along the normal
- If the hit was a tile (not a floating contact), calls `UpdateSurfaceConstraint` to register or refresh a `SurfaceDistance` for that surface
- Repeats with remaining displacement until no hits or bounce limit reached

The floating contact sweep is why landing is crisp: the body stops at exactly the standing height in the same frame it arrives, rather than overshooting and being sprung back.

### 4. Expire stale hard constraints
Auto-removes `SurfaceDistance` constraints (not `FloatingSurfaceDistance`) when:
- Body has drifted more than `2 * Epsilon` from the constraint's recorded position along the normal, OR
- `WorldHasSurface` detects no solid tiles adjacent to the body's face in the normal direction (surface was destroyed)

`FloatingSurfaceDistance` constraints are removed only by movement state logic, never auto-expired.

### Key constants
- `Epsilon = 0.5f` — separation buffer added after collision resolution; also the stale-constraint threshold

---

## Chunk/Tile World

Tiles are organized in `Chunk`s (16×16 tiles, each tile 16px). `ChunkMap` is a dictionary of `Point → Chunk`. `TileWorld.TileCenter` and `TileWorld.TileShape` provide the geometry for tile collision. Only `IsSolid` tiles participate in collision.

---

## Input & Controller ([Character/Controller.cs](Character/Controller.cs))

`Controller` maintains a 32-frame ring buffer of `PlayerInput` snapshots. Each frame: `Left`, `Right`, `Up`, `Down`, `Space`, `Shift`, mouse position. `Current` is the latest; `GetPrevious(n)` reaches back n frames.

---

## Ground and Wall Checkers

### `GroundChecker` ([Character/GroundChecker.cs](Character/GroundChecker.cs))
`TryFind(body, chunks, bodyHalfHeight, floatHeight, out contact)` probes tiles downward from the body's bottom edge within `floatHeight + ProbeSlack`. Returns a `FloatingSurfaceDistance` targeting the nearest flat tile top face, with `Normal = (0, -1)` and `MinDistance = bodyHalfHeight + floatHeight`. The float gap keeps the body hovering one `floatHeight` above the ground, which allows stepping over small bumps.

### `WallChecker` ([Character/WallChecker.cs](Character/WallChecker.cs))
`TryFind(body, chunks, bodyHalfWidth, floatWidth, wallDir, out contact)` probes tiles horizontally on the requested side (`wallDir`: 1 = right, -1 = left) within `ProbeSlack`. Returns a `FloatingSurfaceDistance` with a horizontal outward normal and `MinDistance = bodyHalfWidth + floatWidth`. The contact's Y position tracks the body center so it follows the body as it slides down.

---

## Movement State Machine ([Character/Movement.cs](Character/Movement.cs), [Character/MovementStates.cs](Character/MovementStates.cs))

All movement states implement `MovementState.Update(body, input, chunks, dt) → MovementState`. States write forces to `body.AppliedForce` and manage `FloatingSurfaceDistance` contacts. They never write to `body.Velocity` directly (except `JumpingState` and `WallJumpingState` constructors for initial impulses — a known exception).

### State transitions
```
FallingState ──(ground found)──────────────────────► StandingState
             ──(wall found + pressing into wall)───► WallSlidingState

StandingState ──(jump input)───────────────────────► JumpingState
              ──(ground lost)──────────────────────► FallingState

JumpingState ──(jump released or hold expired)─────► FallingState

WallSlidingState ──(jump input)────────────────────► WallJumpingState
                 ──(ground found)──────────────────► StandingState
                 ──(wall lost or input released)───► FallingState

WallJumpingState ──(jump released or hold expired)─► FallingState
```

### `FallingState`
Default airborne state. Each frame:
1. Checks `GroundChecker.TryFind` — if ground found, registers the contact and transitions to `StandingState`
2. If pressing left/right, checks `WallChecker.TryFind` on that side — if wall found, registers the contact and transitions to `WallSlidingState`
3. Otherwise applies air acceleration toward input (`AirAccel = 1500`), caps at `MaxAirSpeed = 150 px/s`, and applies `AirDrag = 500` when no input

### `StandingState`
Holds a `FloatingSurfaceDistance` ground contact. Each frame:
- Jump input: removes ground contact, transitions to `JumpingState`
- `GroundChecker.TryFind` fails: removes ground contact, transitions to `FallingState`
- Otherwise: refreshes the contact position, applies a spring force (`SpringK = 300`) to push the body back up to standing height when it dips below, and applies walk forces (`WalkAccel = 3000`, `MaxWalkSpeed = 100 px/s`, `BrakingForce = 3000`)

### `JumpingState`
Sets an upward velocity impulse (`JumpVelocity = -600 px/s`) in its constructor and clears all `FloatingSurfaceDistance` constraints. While jump is held (up to `MaxJumpHoldTime = 0.25s`), applies a sustained upward force (`JumpHoldForce = -100`). Transitions to `FallingState` when jump is released or the hold window expires.

### `WallSlidingState`
Holds a `FloatingSurfaceDistance` wall contact. Each frame:
1. Jump input: removes wall contact, transitions to `WallJumpingState`
2. Ground found: removes wall contact, adds ground contact, transitions to `StandingState`
3. `WallChecker.TryFind` fails or player releases input toward wall: removes wall contact, transitions to `FallingState`
4. Otherwise: refreshes the contact position, applies **dynamic (velocity-proportional) friction**:
   ```
   force.Y = -(vy / SlideTerminalSpeed) * SlideDrag
   ```
   With `SlideTerminalSpeed = 40 px/s` and `SlideDrag = 300` (equal to gravity), the drag exactly cancels gravity at the terminal speed. Above terminal speed the drag exceeds gravity and the body decelerates; below it the body accelerates naturally.

### `WallJumpingState`
Sets an initial velocity impulse diagonally away from the wall in its constructor. While jump is held (up to `MaxHoldTime = 0.25s`), applies `JumpHoldForce = -300`. Transitions to `FallingState` when released or hold expires. Air control is active.

---

## `PlayerCharacter` ([Character/PlayerCharacter.cs](Character/PlayerCharacter.cs))

Owns a hexagonal `PhysicsBody` (radius 12px, 6 sides) and a `MovementState`. Each frame, `Update` runs the state machine, then `PhysicsWorld.StepSwept` runs the physics step. The body is registered in `_bodies` so the physics step processes it alongside any other bodies.

`IsGrounded` returns true when the current state is `StandingState`.

---

## Current Game Loop ([Game1.cs](Game1.cs))

```
1. Read keyboard / mouse
2. Update Controller buffer
3. PlayerCharacter.Update(input, chunks, dt)   — state machine → AppliedForce + contacts
4. PhysicsWorld.StepSwept(bodies, chunks, dt, gravity)
5. Camera tracks player position
```

Debug rendering draws `FloatingSurfaceDistance` constraint arrows in cyan and `SurfaceDistance` arrows in yellow. Player polygon is green when grounded, orange otherwise.
