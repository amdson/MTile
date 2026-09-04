# Avalanche Direction and Riding — Implementation Sketch

This is an implementation sketch for `todo.txt` item 2. The target feeling is not
"a sequence of blocks bumps the player." It is "the player has entered a moving body
of earth": they are carried with the eruption, may steer inside its frame, and leave
with its momentum when they jump.

The shortest path to that result is to keep the existing sprout collision model and
add two pieces of information that it currently loses:

1. **Avalanche lineage and order**, so an eruption cannot light itself from the far
   side and grow backward.
2. **A coherent, low-frequency flow sample**, used as a moving reference frame for the
   player rather than as a series of individual tile pushes.

The code already contains much of the second half. `TileSproutNode` exposes analytic
volume position and velocity, moving-surface contacts carry `SurfaceVelocity`,
`TerrainCarriedState` averages nearby growth and applies an anchor servo, and ordinary
jumps set vertical launch speed relative to their source surface. This plan extends
those seams instead of creating a second terrain simulation.

---

## 1. Desired behavior

- A released `MassBall` produces one directed avalanche wave.
- Its visible growth normally travels in the same direction as the ball. Existing
  terrain may seed the wave where the ball reaches it, but terrain at the far end must
  not start a second wave traveling backward through already-requested cells.
- A player actually touched by the moving mass enters a ride. Mere proximity does not
  unexpectedly grab them.
- Once attached, short gaps between individual sprout volumes do not drop the ride.
- With no input, the player remains near the same place in the avalanche's flow frame.
- Left/right input moves the player relative to that frame. It does not fight or replace
  the avalanche velocity.
- Jumping detaches immediately and retains the full carrier velocity, then adds the
  normal jump velocity in the carrier's frame.
- Static building, burst blocks, enemy pillars, and unrelated simultaneous avalanches
  retain their current behavior.
- Everything remains deterministic and rollback-safe.

One useful acceptance scenario is a two-cell-thick diagonal eruption. A player at its
base should be carried roughly to its crest with no input, move along the front under
input, and launch cleanly beyond the crest on jump.

---

## 2. What exists and what is missing

### Existing pieces to keep

- `Entities/MassBall.cs` is the live source of eruption position and velocity.
- `World/TileMassField.cs` turns leaked mass into requested sprouts.
- `World/TileSproutNode.cs` has continuous `VolumeCenter` and `VolumeVelocity`; combined
  multi-face sprouts can already report diagonal motion.
- `Physics/PhysicsWorld.cs` and `GroundChecker` propagate moving-surface velocity.
- `Character/Movement/TerrainCarriedState.cs` already has the right broad shape:
  contact-gated entry, nearby-mass continuity, a distance-weighted flow target, steering,
  and one velocity servo.
- `JumpingState` already inherits the source surface's vertical velocity. Horizontal
  body velocity is normally preserved, so much of jump-off momentum works incidentally.

### Information currently discarded

`MassBall.ProjectileUpdate` deposits only `(cell, amount, type)`. By the time
`TileMassField` requests a cell, the request no longer knows:

- which avalanche created it;
- when that avalanche first reached it;
- which direction the ball was traveling;
- the speed of the macroscopic front.

`ChunkMap.TryRequestTile` later derives support from **every** solid neighbor, and a
pending sprout is promoted from whatever neighbors happen to be solid then. That is
excellent for symmetric shell-building, but it permits a pending avalanche cell near
pre-existing terrain to ignite from its far side. The same loss of provenance makes the
ride estimator infer a large-scale stream solely from short, axis-aligned tile motions.

---

## 3. Carry avalanche provenance through the build pipeline

Add an optional value-type tag. `None` means ordinary building and follows today's
rules exactly.

```csharp
public readonly struct AvalancheStamp
{
    public readonly EntityId WaveId;       // MassBall.Id
    public readonly uint     DepositStep;  // increments once per ball deposit
    public readonly Vector2  Direction;    // normalized ball velocity at deposit
    public readonly float    Speed;        // clamped ball speed at deposit
    public readonly float    AlongSweep;   // dot(this cell's center, Direction)
}
```

Store the tag on both the partial mass bucket and the resulting `TileSproutNode`.
Add the corresponding fields to `EntityData`, `SproutNodeData`, and the mass-field
snapshot. `MassBall`'s deposit counter is sim state and must also be snapshotted.

Suggested API changes:

```csharp
// MassBall
chunks.Mass.Deposit(chunks, gtx, gty, leak, _tileType,
    AvalancheSource.For(Id, _depositStep++, Body.Velocity));

// TileMassField
int Deposit(ChunkMap chunks, int gtx, int gty, float amount,
            TileType type, AvalancheSource source = default);

// ChunkMap
TileSproutNode TryRequestTile(int gtx, int gty, TileType type,
                              AvalancheStamp stamp = default);
```

`TileMassField.DepositAt` derives a fresh `AvalancheStamp` for each visited cell from
the source plus that cell's center. In particular, spill recursion preserves wave,
step, direction, and speed but **recomputes `AlongSweep`**; copying the origin cell's
projection into every spill bucket would destroy the directional tie-break.

### Mixed mass

A mass bucket can receive contributions from multiple sources. Do not merge their
directions numerically; that invents a wave that never existed. Use one deterministic
owner per bucket:

- the contribution that crosses `Threshold` owns the committed sprout;
- ties use the lower `EntityId`, then lower `DepositStep`;
- remove one unit from that owner's contribution and spill that same owner/stamp;
- keep sub-threshold contributions separately by `(cell, WaveId)`.

The sparse map therefore becomes approximately:

```csharp
Dictionary<(int gtx, int gty, EntityId wave), MassBucket>
```

Manual paint can use `EntityId.None`. If separating the buckets is too large a first
change, a single winning stamp per cell is an acceptable prototype, but it will make
crossing avalanches order-dependent in ways that are difficult to reason about.

### Order relation

For sprouts in the same wave, define "earlier" lexicographically:

```text
(DepositStep, AlongSweep)
```

Smaller is earlier. `AlongSweep` only breaks equal-deposit-step ties. Cells with the
same step and effectively equal projection are incomparable; they form one transverse
slice of the front. Use an epsilon around the projection comparison so float noise does
not order two cells differently after rollback. Cell coordinates, not live positions,
are used for the projection.

The important invariant is:

```text
an avalanche parent may start child growth only when
parent.WaveId == child.WaveId && parent.Order < child.Order
```

This says the relationship unambiguously: growth travels from an earlier wave slice
into a later one. No wording such as "higher/lower parent" is needed in code.

### Static roots

Pre-existing solid terrain has no avalanche order. Treat it as a possible root only
when it is not ahead of the requested cell in the sweep direction:

```text
dot(childCenter - solidParentCenter, child.Direction) >= -epsilon
```

This admits the floor below an upward eruption and terrain behind/transverse to a wave,
but rejects a wall or platform on the far side as the source of backward growth. Record
the accepted faces as root faces on the node at request time.

Do not let a pending avalanche cell later discover an arbitrary static neighbor during
promotion. Otherwise a wall or summit platform at the far end can ignite a reverse
front. A pending avalanche node may promote from:

- its recorded static root face, if any; or
- an earlier node from the same wave.

Ordinary untagged sprouts continue to use `SolidFaces` and symmetric shell promotion.

### Directionally filtered faces

Replace `SolidFaces` with a policy-aware query for tagged nodes:

```csharp
SproutFaces EligibleFaces(TileSproutNode child)
{
    faces = child.RecordedRootFaces;
    foreach (neighbor in FourNeighbors(child))
        if (neighbor is solid avalanche sprout
            && SameWave(neighbor, child)
            && IsEarlier(neighbor.Stamp, child.Stamp))
            faces |= FaceToward(neighbor);
    return faces;
}
```

Finalized terrain currently forgets its sprout node, so eligibility needs a small sparse
`AvalancheHistory` map from cell to stamp. Keep entries only while their wave still has
pending/growing cells within the spill horizon; then prune the wave wholesale. Include
this map in `TerrainSnapshot` and the deterministic checksum.

This is preferable to encoding provenance forever in dense `Tile`: lineage is temporary
simulation metadata, not a new terrain material property.

---

## 4. Expose a coherent avalanche flow field

Do not estimate flow from `TileMassField.MassAt`. Bucket mass is an accumulation signal:
it decays, jumps at threshold crossings, and may move through occupied cells without a
physical surface. Its time derivative would be noisy and can point somewhere the player
cannot actually be carried.

Instead, use the analytic motion already represented by growing sprout volumes, but
group samples by avalanche and bias their direction with the recorded macro flow.

Add a query owned by `ChunkMap` (or a small `AvalancheFlowField` helper):

```csharp
public FlowSample SampleAvalancheFlow(BoundingBox bodyBounds,
                                      EntityId preferredWave = default);

public readonly struct FlowSample
{
    public readonly bool     HasFlow;
    public readonly EntityId WaveId;
    public readonly Vector2  Velocity;
    public readonly Vector2  AnchorPoint;
    public readonly float    Confidence;
}
```

For each nearby growing node:

1. Compute its current physical volume center and `VolumeVelocity`.
2. Reject a volume moving away from the body.
3. Weight it with a smooth compact kernel based on distance from the volume to the body;
   avoid a hard radius edge.
4. Deduplicate identical volumes from multi-face nodes.
5. Accumulate samples separately by `WaveId`; never average two avalanches together.
6. Blend the local volume velocity toward `stamp.Direction * stamp.Speed` as confidence
   falls. Local motion dominates near an actual face; macro direction bridges the
   horizontal/vertical alternation between cells.
7. Select the contacted/preferred wave first, otherwise the highest-confidence wave.

A simple first-pass kernel is sufficient:

```text
w(d)       = smoothstep(probeRadius, 0, d)
vLocal     = sum(w * volumeVelocity) / (sum(w) + centerBias)
confidence = saturate(sum(w) / fullWeight)
velocity   = lerp(macroVelocity, vLocal, confidence)
```

`AnchorPoint` can be the same weighted average of the leading faces. This gives the
movement state a continuous front position as well as a velocity. The exact field need
not be physically correct; it needs to be spatially smooth, directionally truthful, and
bounded by speeds the terrain actually exhibits.

---

## 5. Make riding an attachment in the flow frame

Keep `TerrainCarriedState`, but make its persistent state explicit:

```csharp
public EntityId CarryWaveId;
public Vector2  CarryAnchorOffset; // player position relative to sampled front
public Vector2  CarryFlowVelocity;
public float    CarryConfidence;
public float    CarryGrace;
```

These belong in `MovementVars` because they affect simulation and rollback.

### Entry

Enter only after an advancing sprout contact supplies meaningful push. On entry:

```text
CarryWaveId       = contact sprout's WaveId
CarryFlowVelocity = sampled flow for that wave
CarryAnchorOffset = body.Position - sample.AnchorPoint
CarryGrace        = RideGraceSeconds
```

This preserves the current good distinction: a vertical elevator can remain ordinary
standing behavior, while a lateral/diagonal eruption becomes a ride. If playtesting says
pure vertical eruptions should also feel rideable, broaden the gate to a minimum push
magnitude; the rest of the design does not change.

### Update

Each frame, sample only `CarryWaveId`. Advect the reference point by flow and allow input
to move the attachment within the front:

```text
relativeVelocity = inputX * RideSteerSpeed * tangent
anchorOffset     += relativeVelocity * dt
desiredPosition  = sample.AnchorPoint + anchorOffset
desiredVelocity  = sample.Velocity + relativeVelocity

force = gravityHold
      + criticallyDampedServo(body.Position, body.Velocity,
                              desiredPosition, desiredVelocity)
```

For a side-scroller, `tangent = Vector2.UnitX` is initially more predictable than the
mathematical tangent of a rapidly changing front. It exactly implements "hold right to
move right relative to the attachment point." A later version can project input onto a
stable front tangent if slopes demand it.

Use one acceleration-capped servo for both axes and leave ambient correction in
clearance-only mode. Multiple simultaneous springs, station friction, and a separate
vertical hover controller will fight and recreate the jitter this mechanic is intended
to remove.

Clamp `CarryAnchorOffset` to a generous capsule around the sampled front. If the player
steers beyond it, let confidence fall and detach instead of snapping the offset back.

### Continuity and exit

- Refresh `CarryGrace` whenever the selected wave has a good nearby sample.
- During a one- or two-sprout handoff gap, advect the last anchor using the last flow
  velocity and decay confidence smoothly.
- Exit when grace expires, the player is knocked/stunned, another higher-priority
  movement state wins, or jump begins.
- Never switch to a different `WaveId` during grace. Require a new physical contact to
  attach to a crossing avalanche.

This produces one movement-state run for an entire ride instead of state flapping at
each cell boundary.

---

## 6. Jump-off momentum

On jump entry, copy the carrier velocity before `TerrainCarriedState.Exit` clears it.
Define the launch in the carrier frame:

```text
launchVelocity.X = body.Velocity.X
launchVelocity.Y = carrierVelocity.Y + JumpVelocity
```

Preserving `body.Velocity.X` retains both avalanche momentum and the player's relative
steering. Adding `carrierVelocity.X` again would double-count it. Setting X to the
carrier speed would erase earned relative motion.

The existing surface-relative jump path may already produce this result while contact
is live. The explicit carry handoff is still worthwhile because jumps during a brief
between-cell grace window may have no current `FloatingSurfaceDistance`. Add a transient
`JumpSourceVelocity` to `MovementVars`, populated by the outgoing carried state or read
directly before transition, so the result does not depend on contact timing.

After the initial write, normal air control owns the body. There should be no lingering
flow attraction after detach.

---

## 7. Implementation phases

### Phase A — Direction correctness

1. Add `AvalancheStamp` and snapshot/checksum support.
2. Pass it from `MassBall` through per-wave mass buckets into sprout requests.
3. Add temporary finalized-cell `AvalancheHistory`.
4. Filter avalanche promotion parents with the earlier-than relation and recorded roots.
5. Leave untagged building behavior unchanged.

This phase fixes the backward-growth bug independently of player movement.

### Phase B — Flow query

1. Extract the nearby-volume loop from `TerrainCarriedState.RideTarget` into a reusable
   per-wave query.
2. Return velocity, anchor point, confidence, and wave id.
3. Blend local analytic velocity with stamp macro velocity.
4. Add debug drawing for sample velocity, anchor, selected wave, and probe radius.

### Phase C — Persistent ride attachment

1. Add the carry fields to `MovementVars`.
2. Lock a ride to the contacted wave.
3. Advect `CarryAnchorOffset`, apply relative steering, and use one position/velocity
   servo.
4. Preserve the existing contact-gated entry and grace-based continuity.

### Phase D — Jump and tuning

1. Hand the cached carrier velocity into jump entry.
2. Move ride constants from private constants to `MovementConfig` / JSON for hot reload.
3. Tune with single-block, long diagonal, thinning-front, and crossing-wave scenarios.

---

## 8. Tests that should pin the mechanic

### Ordering

- **Far terrain does not back-ignite:** request an avalanche chain toward a wall/platform;
  only the forward front advances.
- **Equal-time projection follows sweep:** a right-moving ball's same-step cells parent
  from left to right.
- **Transverse slice is simultaneous:** cells with equal order remain incomparable and do
  not acquire arbitrary up/down ancestry.
- **Unrelated waves do not parent each other.**
- **Manual sprouts unchanged:** an untagged request still expands with current symmetric
  shell semantics.
- **Rollback:** capture mid-wave, advance, restore, and replay to the same terrain hash,
  selected faces, and flow samples.

### Riding

- **No proximity theft:** nearby growth without contact does not enter carried state.
- **One continuous ride:** a long diagonal stream produces at most one carried run.
- **No-input transport:** player displacement and wave displacement remain close over a
  15–20 tile ride.
- **Relative steering:** right input changes player position relative to the sampled
  anchor while maintaining most of the wave's travel.
- **Smoothness:** bound frame-to-frame change in displacement during single-block and
  cell-handoff windows.
- **Edge release:** steering beyond the front detaches without a snap or suction backward.
- **Jump inheritance:** compare flat-ground jump, live-contact avalanche jump, and
  handoff-gap avalanche jump; the latter two retain the same carrier momentum.
- **Crossing waves:** a rider stays locked to the contacted wave and does not average or
  switch streams without another contact.
- **End of wave:** confidence/grace decays into normal falling or standing, with no stale
  carry force.

The existing `MTile.Tests/Sim/SproutLiftJumpTests.cs` is the natural home for most ride
tests. Put lineage/order cases in a focused `AvalancheOrderingTests.cs` rather than
overloading generic sprout graph tests.

---

## 9. Recommended first playable version

For the first implementation, do **Phase A**, then make the smallest possible change to
the current carried state: select and lock one stamped wave, blend its local sprout
velocities with its macro direction, and explicitly cache that velocity for jump entry.
Do not begin with a fully persistent positional anchor.

The current anchor-like velocity servo already demonstrates the core ride over long
diagonal streams. Lineage will make its input more coherent, and wave locking will fix
the worst multi-wave ambiguity. Only add the persistent `CarryAnchorOffset` if playtesting
still says the player is being successively pushed rather than inhabiting the flow. That
keeps the first experiment small while leaving a clean route to the stronger attachment
model described above.
