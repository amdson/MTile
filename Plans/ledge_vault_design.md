# LedgeVault Implementation Design

## Overview

`LedgeVaultState` is a movement state that activates automatically when the player approaches a
vaultable ledge while pressing toward it. A "vaultable ledge" is the topmost exposed solid tile on
a wall face — solid, with no solid tile directly above it, at a height the player can step onto.

When the vault activates:
1. A `PointForceContact` spring-pulls the player toward the landing position above the corner.
2. A 45-degree `FloatingSurfaceDistance` ("pseudo-ramp") intercepts horizontal velocity and
   redirects it upward, so a fast-moving player doesn't stop dead at the wall face but is
   instead launched over it.

---

## Coordinate conventions

- Y increases **downward** (screen space). Gravity is `+Y`.
- "Above" means smaller Y. "Below" means larger Y.
- Player center is `body.Position`. Feet are approximately `body.Position.Y + Radius`.
- `Chunk.Size = 16`, `Chunk.TileSize = 16`. `PlayerCharacter.Radius = 12`.

---

## New file: `Character/ExposedUpperCornerChecker.cs`

Parallel to `GroundChecker` and `WallChecker`. Scans tiles on the indicated side for a vaultable
corner and returns its world-space coordinates.

### What qualifies as a vaultable corner

For `wallDir = 1` (right wall), a tile at chunk-local `(tx, ty)` qualifies when:

1. `tiles[tx, ty].IsSolid` — it is solid.
2. The tile directly above (`ty - 1`, or the chunk above) is **not** solid — the top face is exposed.
3. `tileLeft >= bounds.Right` — the tile faces inward toward the player (not buried behind another
   tile that would already be stopping the player).
4. `cornerTopY` (= `tileTop` in world pixels) is within the vaultable height window:
   ```
   body.Position.Y - Radius - MaxVaultHeight  <=  cornerTopY  <=  body.Position.Y + Radius
   ```
   The upper bound ensures the corner is above the player's feet; the lower bound caps how high the
   player can vault (proposed default: `MaxVaultHeight = 1.5 * TileSize = 24px`, roughly a tile and a
   half above the player's midpoint).
5. **Clearance check**: the tile directly above the corner (`tx, ty - 1`) and the tile diagonally
   inward and up (`tx - wallDir, ty - 1`) are both not solid. This confirms there is room for the
   player body to pass through.

Among all qualifying tiles, pick the one with the **largest `cornerTopY`** (the lowest exposed
corner) — this is the first ledge the player's body would actually collide with.

### Return value

```csharp
public struct ExposedCorner
{
    public Vector2 InnerEdge;   // (tileLeft, cornerTopY) for wallDir=1; (tileRight, cornerTopY) for -1
    public Vector2 LandingTarget; // computed target above and past the corner (see below)
}
```

`LandingTarget` is computed as:
```
targetX = innerEdge.X + wallDir * Radius          // one body radius past the inner edge
targetY = cornerTopY - Radius                     // one body radius above the ledge top face
```

The `PointForceContact` aims here. When `body.Position.Y` drops below `targetY`, the vault is
complete.

### API signature (mirrors GroundChecker/WallChecker)

```csharp
public static bool TryFind(
    PhysicsBody body,
    ChunkMap chunks,
    int wallDir,
    out ExposedCorner corner)
```

---

## `PointForceContact` (`Physics/PhysicsContact.cs`)

No changes. `PointForceContact` is used as a **position tracker** only — it stores the corner
`InnerEdge` so the state can read corner coordinates via `_spring.Position` and so the corner
appears in debug rendering. The physics step does not process it; all force logic lives in
`LedgeVaultState.Update`.

No changes to `PhysicsWorld`.

---

## The pseudo-ramp `FloatingSurfaceDistance`

When the player is moving quickly horizontally into the wall, the tile collision would stop them
dead. The ramp intercepts this before collision, converting horizontal momentum to upward momentum.

### Geometry

For `wallDir = 1`:
- **Position**: `corner.InnerEdge` — the upper-left corner of the ledge tile.
- **Normal**: `new Vector2(-√3/2, -0.5f)` — unit vector at 60° from horizontal, pointing up-left.
- **MinDistance**: `1000f` — always active from the first frame of the vault.

For `wallDir = -1`, normal = `new Vector2(√3/2, -0.5f)` (up-right, 60° from horizontal).

Both normals are already unit length: `sqrt(3/4 + 1/4) = 1`.

In code: `new Vector2(-_wallDir * MathF.Sqrt(3f) * 0.5f, -0.5f)`.

### Why it works

With `v = (vx, 0)` (moving purely right, `vx > 0`) and `n = (-0.866, -0.5)`:

```
vn = dot(v, n) = -0.866 * vx   (negative → constraint fires)
v' = (vx - 0.75*vx, 0 + 0.433*vx) = (0.25*vx, -0.433*vx)
```

Result: 75% of horizontal speed removed, 43% converted to upward speed. Steeper than 45° — more
aggressive at braking horizontal entry velocity.

### Lifetime

Created in `Enter`. Removed **at the start of Phase 2** (`Update`, when feet first clear
`cornerTopY`) so it doesn't fight the Phase 2 horizontal push force. `Exit` null-checks before
removing.

---

## `LedgeVaultState` (`Character/MovementStates.cs`)

### Priority

| | Active | Passive |
|---|---|---|
| `LedgeVaultState` | **20** | **45** |
| `JumpingState` | 50 | 30 |
| `WallJumpingState` | 50 | 40 |
| `DoubleJumpingState` | 60 | 40 |
| `WallSlidingState` | 20 | 20 |
| `StandingState` | 10 | 10 |

Passive priority 45 means the vault wins over wall sliding and falling on entry. Active priority 20
means any jump state (all have PassivePriority ≥ 30) can interrupt the vault. WallSlidingState
(PassivePriority = 20) cannot (not strictly greater), so wall-slide re-entry during a vault is
blocked.

### State fields

```csharp
private readonly int _wallDir;
private PointForceContact _spring;
private FloatingSurfaceDistance _ramp;
private float _timeInState;
private float _targetY;          // cleared when body.Position.Y < this
```

### `CheckPreConditions`

1. Player is pressing toward the wall: `(wallDir == 1 && ctx.Input.Right) || (wallDir == -1 && ctx.Input.Left)`
2. Player is **not** grounded (`!ctx.TryGetGround(out _)`)
3. `ExposedUpperCornerChecker.TryFind(ctx.Body, ctx.Chunks, _wallDir, out _)` succeeds

### `CheckConditions`

```csharp
return _timeInState < MaxVaultTime && ctx.Body.Position.Y > _targetY;
```

`MaxVaultTime = 0.5f` (safety cap). The vault naturally ends when the body clears the target Y,
transitioning to `FallingState` → `StandingState` picks up the landing in the same frame.

### `Enter`

```csharp
_timeInState = 0f;
ctx.TryGetExposedCorner(_wallDir, out var corner);
_spring = new PointForceContact(corner.InnerEdge);   // position tracker; InnerEdge = (cornerX, cornerTopY)
var rampNormal = new Vector2(-_wallDir * MathF.Sqrt(3f) * 0.5f, -0.5f);
_ramp = new FloatingSurfaceDistance(corner.InnerEdge, rampNormal, 1000f);
ctx.Body.Constraints.Add(_spring);
ctx.Body.Constraints.Add(_ramp);
abilities.HasDoubleJumped = false;
```

### `Exit`

```csharp
if (_spring != null) ctx.Body.Constraints.Remove(_spring);
if (_ramp   != null) ctx.Body.Constraints.Remove(_ramp);
_spring = null;
_ramp   = null;
```

### `Update`

Two phases, keyed on whether the player's feet (`body.Position.Y + Radius`) are below or above
`cornerTopY` (`_spring.Position.Y`):

**Phase 1** — feet at or below corner:
```csharp
force.Y = -cfg.VaultLiftForce;   // constant upward force, overrides gravity
```

**Phase 2** — feet above corner (ramp removed at this transition):
```csharp
// Kill upward velocity
if (ctx.Body.Velocity.Y < 0f && ctx.Dt > 0f)
    force.Y = Math.Min(-ctx.Body.Velocity.Y / ctx.Dt, cfg.VaultLiftForce);  // positive (downward)
// Push past corner
force.X = _wallDir * cfg.VaultPushForce;
```

`ctx.Body.AppliedForce = force` at end of Update.

---

## `EnvironmentContext` change

Add a cached `TryGetExposedCorner` following the same pattern as `TryGetWall`:

```csharp
// One per wallDir (+1 / -1)
private bool _cornerSearched1, _hasCorner1;
private ExposedCorner _corner1;
// ... same for -1

public bool TryGetExposedCorner(int dir, out ExposedCorner corner)
{
    // cache + call ExposedUpperCornerChecker.TryFind on first access
}
```

`LedgeVaultState.CheckPreConditions` and `Enter` both call `ctx.TryGetExposedCorner`, so the
checker only runs once per frame.

---

## `MovementConfig` additions

```csharp
public float VaultLiftForce  = 2000f;  // phase 1 upward force (overcomes gravity + lifts)
public float VaultPushForce  = 500f;   // phase 2 horizontal push past the corner
public float MaxVaultTime    = 0.5f;   // safety cap on vault duration
public float MaxVaultHeight  = 24f;    // max height above player midpoint for corner to qualify
```

---

## `PlayerCharacter` change

Add two entries to `_stateRegistry`:

```csharp
_stateRegistry.Add(new LedgeVaultState(1));
_stateRegistry.Add(new LedgeVaultState(-1));
```

---

## Files touched

| File | Change |
|---|---|
| `Physics/PhysicsContact.cs` | No change |
| `Physics/PhysicsWorld.cs` | No change |
| `Character/ExposedUpperCornerChecker.cs` | **New file** |
| `Character/EnvironmentContext.cs` | Add `TryGetExposedCorner` cache (~15 lines) |
| `Character/MovementStates.cs` | Add `LedgeVaultState` class |
| `Character/PlayerCharacter.cs` | Register `LedgeVaultState(1)` and `LedgeVaultState(-1)` |
| `Character/MovementConfig.cs` | Add `VaultLiftForce`, `VaultPushForce`, `MaxVaultTime`, `MaxVaultHeight` |

---

## Open questions

**1. Force tuning**
`VaultLiftForce = 2000f` and `VaultPushForce = 500f` are initial estimates. Tune after playtesting.

**2. Vault from wall slide**
With PassivePriority 45 > WallSlidingState ActivePriority 20, a player wall-sliding up to an
exposed corner will automatically vault. To disable, add `!ctx.TryGetWall(_wallDir, out _)` to
`CheckPreConditions`. Deferred for investigation.

**3. Clearance check scope**
The two-tile clearance check (above + diagonally inward-above) is a minimum. A more thorough
check would verify a full player-height column above the landing target. Extend if players clip
into ceilings.


