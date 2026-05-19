# Tile Sprout Graph Plan

Follow-on to [TILE_SPROUT_PLAN.md](TILE_SPROUT_PLAN.md). That plan covered single-click sprouts with a one-solid-parent rule and a flat active-list. This one extends placement to **drag-to-build** and introduces a real **dependency graph** so sprouts can be requested faster than they grow.

## Goal

While the player drags the mouse with the build button held, every cell the cursor sweeps through becomes a *tile request* — processed in cursor order. A request becomes a sprout immediately if it is in range *and* has a candidate parent (a solid tile **or another sprout**). Parents that are themselves still growing keep the new sprout in a **pending** state; once any parent finalizes, the pending sprout starts growing. Result: a directed graph of dependent sprouts that progresses wavefront-style outward from the player's terrain.

(Note: "tile sprout DAG" — the structure is a DAG, not a tree; a sprout can have multiple parents.)

## What changes vs. the existing plan

| Aspect | Before | After |
|---|---|---|
| Input | Edge-triggered right-click, one cell per click | Continuous: while button is held, every swept cell is requested |
| Parent rule | Must have a Solid 4-neighbor | Must have a Solid **or Sprouting** 4-neighbor |
| Adjacency-to-sprout | Rejected | Allowed; new sprout becomes a child waiting on the sprouting parent |
| Active-set | `List<TileSprout>` of currently-growing sprouts | `TileSproutGraph` — owns all known sprouts (pending + growing), tracks parent/child links |
| Lifecycle | `Sprouting → Solid` | `Pending → Growing → Solid` (Pending node has no physical presence) |
| Reach check | Per click | Per request (each swept cell tested) |

The tile state machine itself (`Empty | Sprouting | Solid` on `Tile.State`) is **unchanged**. Pending nodes live only in the graph; the cell's tile entry stays `Empty` until growth actually starts. This keeps physics / drawing / `IsSolidAt` query rules untouched — pending nodes are invisible to the world.

## Data Model

### `TileSproutGraph` (new, owned by `ChunkMap`)

Replaces `ChunkMap._activeSprouts`. Owns every known node, indexed by global cell so requests can dedupe in O(1):

```csharp
public sealed class TileSproutGraph
{
    private readonly Dictionary<(int gtx, int gty), TileSproutNode> _nodes = new();

    public bool         TryGet(int gtx, int gty, out TileSproutNode node);
    public IEnumerable<TileSproutNode> Growing { get; }       // for physics + draw + tick
    public IEnumerable<TileSproutNode> Pending { get; }       // for tick (parent-readiness)

    public TileSproutNode AddPending(int gtx, int gty, IReadOnlyList<TileSproutNode> sproutParents);
    public TileSproutNode AddGrowing(int gtx, int gty, Vector2 parentCenter);  // direct from a Solid parent
    public void           Remove(TileSproutNode n);
}
```

Two separate iteration views (`Growing`, `Pending`) keep per-frame work tight: physics + draw walk only the Growing set; readiness propagation walks only the Pending set.

### `TileSproutNode`

```csharp
public sealed class TileSproutNode
{
    public readonly Point ChunkPos;
    public readonly int   Tx, Ty;             // local indices
    public readonly int   Gtx, Gty;           // global cell (dedupe key)

    public TileSproutStatus Status;           // Pending | Growing
    public readonly List<TileSproutNode> SproutParents = new();   // pending parents at creation
    public readonly List<TileSproutNode> Children      = new();   // for completion propagation

    // Movement payload — populated on Pending→Growing transition.
    public Vector2 StartCenter;
    public Vector2 EndCenter;
    public Vector2 Velocity;
    public Polygon Polygon;
    public float   Age;
    public float   Lifetime;

    public Vector2 Center =>
        Vector2.Lerp(StartCenter, EndCenter, MathF.Min(1f, Age / Lifetime));
    public bool IsComplete => Age >= Lifetime;
}

public enum TileSproutStatus : byte { Pending, Growing }
```

Pending nodes carry no movement payload; it's stamped on the `Pending → Growing` flip.

### `MouseSweep` (new, owned by `Game1` — input layer)

Per-frame: prev mouse world pos, current mouse world pos. Sample the segment at sub-tile resolution (4 px = quarter of a tile is comfortable) and emit a deduped, ordered list of cells the line passed through. The result is the **request queue** for this frame.

```csharp
public static class MouseSweep
{
    // Yields (gtx, gty) for each unique cell the segment (a, b) enters, in path order.
    public static IEnumerable<(int gtx, int gty)> Cells(Vector2 a, Vector2 b, float step = 4f);
}
```

A click without movement = `a == b` → one cell. A frame-to-frame jump (long line) walks every cell on the line. Step at 4 px ≥ guarantees no cell is skipped at any cursor speed (each cell is at least 16 px wide).

## Control Flow

### Input — `Game1.HandleBuildInput` (rewritten)

```
HandleBuildInput
  if not input.RightClick: return                          <-- mouse-up: stop emitting requests
  segmentStart = prev.RightClick ? prev.MouseWorldPosition : input.MouseWorldPosition
  segmentEnd   = input.MouseWorldPosition

  for each cell c in MouseSweep.Cells(segmentStart, segmentEnd):
    cellCenter = CellCenter(c)
    if DistSq(player.Position, cellCenter) > BuildReach²: continue   <-- per-request reach
    _chunks.TryRequestTile(c.gtx, c.gty)
```

Edge-triggered click is replaced by "RightClick held, sweep from prev to current". First frame of a press: `segmentStart == segmentEnd` (no previous press position). Subsequent frames: sweep across the segment. Release: nothing emitted.

### Placement — `ChunkMap.TryRequestTile(gtx, gty)`

```
TryRequestTile
  if cell state ≠ Empty:                  return null    <-- already Sprouting or Solid
  if graph already has node at (gtx,gty): return null    <-- already Pending or Growing
  scan 4-neighbors:
    solidParent  = first solid 4-neighbor by priority    (below > left > right > above)
    sproutParents = list of every Sprouting 4-neighbor   (any order)
  if solidParent != null:
    node = graph.AddGrowing(gtx, gty, parentCenter: solidParent.center)
    tile[gtx,gty].State = Sprouting
    tile[gtx,gty].Sprout = node          (existing back-pointer for IsSolidAt)
    return node
  if sproutParents.Count > 0:
    node = graph.AddPending(gtx, gty, sproutParents)
    for p in sproutParents: p.Children.Add(node)
    return node
  return null                              <-- no candidate parent: drop
```

Key invariant: a node has either a solid parent (started growing immediately) or a non-empty `SproutParents` list. **Parents are fixed at creation time.** If a different cell later becomes solid next to this pending node, we ignore it — the node's parent set was decided at request time, and the natural growth wave will eventually trigger it through its registered parents.

### Tick — `ChunkMap.TickSprouts(dt)` (rewritten)

```
TickSprouts(dt)
  // 1. Advance growing nodes.
  for n in graph.Growing:
    n.Age += dt
    if n.IsComplete:
      finalizeQueue.Add(n)

  // 2. Finalize complete nodes (defer mutation to avoid iterating-while-changing).
  for n in finalizeQueue:
    tile[n.Gtx, n.Gty].State = Solid
    tile[n.Gtx, n.Gty].Sprout = null
    graph.Remove(n)
    // Propagate to children: a pending child becomes growing using *this* node
    // as its parent (first-parent-completed wins). Subsequent completions of
    // other parents-of-the-same-child are no-ops.
    for c in n.Children:
      if c.Status == Pending: PromoteToGrowing(c, parentCenter: CellCenter(n.Gtx, n.Gty))
```

`PromoteToGrowing`:

```
PromoteToGrowing(node, parentCenter)
  node.Status      = Growing
  node.StartCenter = parentCenter
  node.EndCenter   = CellCenter(node.Gtx, node.Gty)
  node.Lifetime    = MovementConfig.Current.SproutLifetime
  node.Velocity    = (node.EndCenter - node.StartCenter) / node.Lifetime
  node.Polygon     = TileWorld.TileShape         (or rebuild — same as today)
  node.Age         = 0f
  node.SproutParents.Clear()                     <-- decouple; no longer waiting
  tile[node.Gtx, node.Gty].State  = Sprouting
  tile[node.Gtx, node.Gty].Sprout = node
```

### Physics + Draw

`ChunkMap.ISolidShapeProvider.ShapesInRect`: walk `graph.Growing` (was `_activeSprouts`). Same SolidShapeRef construction. **Pending nodes are not emitted — they have no physical presence.**

`Game1.Draw`: same — only Growing nodes render (was `ActiveSprouts`).

## Direction of growth with multi-parent — "First parent completed wins"

This is the only nontrivial semantic question raised by multi-parent. Considered:

- **First parent to complete wins** *(chosen)* — `StartCenter = parentCenter at promotion time`. The wave's arrival direction defines the visible push direction. Predictable: the closest sprout-parent (which would have started growing earlier) finishes first.
- *Priority order (below > left > right > above) at promotion time* — fine as a tiebreaker if two parents complete in the *same* tick (e.g. both promoted from the same parent in the same frame). Falls out for free since `finalizeQueue` is iterated in graph order; we just process parents-with-the-same-completion-frame consistently.
- *Closest parent by Euclidean distance* — unmotivated; doesn't change anything for 4-neighbor adjacency since all candidates are at the same distance.

Implementation: the first call to `PromoteToGrowing(child, ...)` flips `child.Status` to Growing. Subsequent calls from other completing parents see `child.Status == Growing` and no-op. Single line of guard.

## Open / Design Choices Worth Flagging Before Implementation

1. **DestroyTile on a Solid that is a parent of pending nodes.** A buried mid-wave destroy leaves the wave hanging. Cleanest: cascade — when a Solid is destroyed, also drop any pending nodes whose `SproutParents` becomes empty after pruning the destroyed parent. *(I'd defer cascade handling to a later pass — destroy is currently a debug-only path.)*
2. **DestroyTile on a Sprouting node.** Same as in the existing plan — drop the node and any orphaned children. Defer.
3. **Cancellation on mouse-up.** Pending nodes persist after the button releases. Acceptable; the player committed to the request. No-cancel keeps the model simple. *(Easy to change later: drop all Pending nodes on release if it feels bad.)*
4. **Parents fixed at creation.** If a brand-new Solid appears next to an existing Pending node, we do **not** retroactively register it as a parent. Reason: at least one of the node's registered parents will eventually complete (or get destroyed — see #1), so re-evaluation buys nothing in the happy path. Predictable.
5. **Mouse sweep step size.** 4 px (¼ tile) is conservative and cheap (max ~16 sample points per frame at typical cursor speed). Lower if cursor jitter ever causes missed cells; higher if perf needs it.
6. **Reach check per request, not per click.** A fast drag arc that strays out of `BuildReach` mid-sweep silently drops out-of-range cells but resumes within range — feels right.
7. **Pending nodes consume memory but no CPU.** Walked only on parent-completion propagation, not per-frame. Graph size is bounded by what the player chooses to request — not a concern at game scale.
8. **Multi-frame chains starting from a single sweep.** The sweep can enqueue a chain `C0, C1, C2, …, Cn` in one frame. Only `C0` (touching a solid) starts growing this frame. `C1` waits on `C0`, `C2` on `C1`, etc. The visible wave propagates at one sprout per `SproutLifetime` (~3 frames at default tuning). That's an emergent rate limit — desirable: dragging a long line plants a chain that animates outward instead of all-at-once.
9. **Polygon ownership.** Currently `TileWorld.TileShape` is a static singleton. Pending nodes don't need a polygon until promotion. Allocate (or reference the shared singleton) at `PromoteToGrowing`.
10. **Tile state during Pending.** Stays `Empty`. This is intentional and unchanged from existing rules: pending nodes are not physical, not visible, and not solid for `IsSolidAt`. The graph is the only place a pending request exists.

## Migration order (each step compiles + runs)

1. **Introduce `TileSproutGraph` and `TileSproutNode`**, port the existing single-parent flow onto them. `_activeSprouts` → `graph.Growing`. Status stays `Growing` for everything. No DAG behavior yet. *(Pure refactor — verify all tests still green.)*
2. **Add `Pending` status** + parent/child links + `PromoteToGrowing`. Still single-parent at creation (only Solid parents qualify). Pending list is empty in practice but the plumbing is there.
3. **Allow Sprouting neighbors as parents** in `TryRequestTile`. Suddenly Pending nodes can exist; verify completion propagation correctly promotes them.
4. **Replace edge-triggered click with mouse-sweep** in `HandleBuildInput`. Add `MouseSweep.Cells`. Drag-to-build is now live.
5. **Tune** `SproutLifetime` and sweep step against feel; observe long drags don't choke draw / physics.
6. **(Optional follow-up)** DestroyTile cascade through the graph.

## Files

| File | Change |
|---|---|
| `World/TileSproutGraph.cs` | **NEW** — node container + lookup index + Growing/Pending views |
| `World/TileSproutNode.cs` | **NEW** — replaces `TileSprout`; carries `Status` + parent/child lists |
| `World/TileSprout.cs` | **DELETE** (rolled into Node) |
| `World/ChunkMap.cs` | Replace `_activeSprouts` field with `Graph` (instance of TileSproutGraph). `TrySpawnSprout` → `TryRequestTile`. `TickSprouts` rewritten with finalize-then-propagate phase. `ShapesInRect` / `IsSolidAt` switch to `Graph.Growing` |
| `World/MouseSweep.cs` | **NEW** — `IEnumerable<(int gtx, int gty)> Cells(Vector2 a, Vector2 b, float step)` |
| `Game1.cs` | `HandleBuildInput` rewritten — segment sweep, per-cell reach test, per-cell `TryRequestTile`. `Update` calls `_chunks.TickSprouts(dt)` (unchanged). `Draw` walks `Graph.Growing` (unchanged shape) |
| `MTile.Tests/Sim/SimRunner.cs` | No change (already calls `TickSprouts`) |
| `MTile.Tests/Sim/SproutPushTests.cs` | Possibly update if a test names the old API. Add a multi-parent / chain test once #3 lands. |

## Verification

- **Refactor parity** (step 1) — existing SproutPushTests, all four, still green.
- **Drag a line of cells along a flat ground.** Cells light up in sweep order, each growing at ~`SproutLifetime` after its predecessor commits.
- **Drag in midair starting from a solid wall.** First cell adjacent to wall grows; chain follows as wave.
- **Cross-chunk drag.** No special-casing required (graph keys are global cells); confirm.
- **Drag a closed loop back into the player's terrain.** Cells around the loop request fine; closing cells dedupe via `graph.TryGet` and tile state.
- **Drag faster than `SproutLifetime`.** Sweep emits many requests this frame; only those touching a solid grow this frame, the rest pile into Pending. Watch the wave catch up.
- **Drag past the reach boundary.** Out-of-range cells silently drop; coming back in range resumes requests.
