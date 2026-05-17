using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

public class ChunkMap : IEnumerable<Chunk>, ISolidShapeProvider
{
    private readonly Dictionary<Point, Chunk> _dict = new();

    // Additional shape providers (moving platforms, growing blocks, …). ChunkMap
    // is itself the implicit first provider; WorldQuery walks self + this list.
    public readonly List<ISolidShapeProvider> Providers = new();

    // All known sprouts (Pending + Growing). Pending nodes wait for at least one
    // parent (Solid tile *or* another sprout) to finalize before growing.
    public readonly TileSproutGraph Graph = new();

    // Sparse per-cell HP. Damaged tiles have an entry until they break or get cleared.
    public readonly TileDamage Damage = new();

    // Fires when BreakCell actually clears a Solid tile. Arguments are the cell's
    // world-space center and its material type at break time. Subscribers (Game1's
    // particle system) react to feedback events without ChunkMap knowing about them.
    public System.Action<Microsoft.Xna.Framework.Vector2, TileType> OnTileBroken;

    // Iteration view used by physics + drawing — only Growing nodes are physically
    // present in the world. Pending nodes live solely in the graph.
    public IReadOnlyList<TileSproutNode> ActiveSprouts => Graph.Growing;

    private const int ChunkPixelSize = Chunk.Size * Chunk.TileSize;

    public Chunk this[Point pos]
    {
        get => _dict[pos];
        set => _dict[pos] = value;
    }

    public bool TryGet(Point pos, out Chunk chunk) => _dict.TryGetValue(pos, out chunk);

    public IEnumerator<Chunk> GetEnumerator() => _dict.Values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    IEnumerable<SolidShapeRef> ISolidShapeProvider.ShapesInRect(BoundingBox region)
    {
        foreach (var t in TileQuery.SolidTilesInRect(this, region))
        {
            float left = t.WorldLeft;
            float top  = t.WorldTop;
            yield return new SolidShapeRef(
                left, top, left + Chunk.TileSize, top + Chunk.TileSize,
                new Vector2(t.WorldCenterX, t.WorldCenterY),
                Vector2.Zero,
                TileWorld.TileShape);
        }

        const float half = Chunk.TileSize * 0.5f;
        foreach (var s in Graph.Growing)
        {
            var c = s.Center;
            if (c.X + half <= region.Left || c.X - half >= region.Right) continue;
            if (c.Y + half <= region.Top  || c.Y - half >= region.Bottom) continue;
            yield return new SolidShapeRef(
                c.X - half, c.Y - half, c.X + half, c.Y + half,
                c, s.Velocity, s.Polygon);
        }
    }

    bool ISolidShapeProvider.IsSolidAt(float worldX, float worldY)
    {
        if (TileQuery.IsSolidAt(this, worldX, worldY)) return true;

        // Growing sprouts: point-in-AABB against current (lerped) position.
        const float half = Chunk.TileSize * 0.5f;
        foreach (var s in Graph.Growing)
        {
            var c = s.Center;
            if (worldX < c.X - half || worldX > c.X + half) continue;
            if (worldY < c.Y - half || worldY > c.Y + half) continue;
            return true;
        }
        return false;
    }

    // Convert world coords → global cell indices (single integer pair across all chunks).
    private static (int gtx, int gty) WorldToGlobalCell(float worldX, float worldY)
        => ((int)Math.Floor(worldX / Chunk.TileSize),
            (int)Math.Floor(worldY / Chunk.TileSize));

    // Convert global cell indices → (chunkPos, localTx, localTy).
    private static (Point chunkPos, int tx, int ty) GlobalCellToChunkLocal(int gtx, int gty)
    {
        int cx = (int)Math.Floor((double)gtx / Chunk.Size);
        int cy = (int)Math.Floor((double)gty / Chunk.Size);
        int tx = gtx - cx * Chunk.Size;
        int ty = gty - cy * Chunk.Size;
        return (new Point(cx, cy), tx, ty);
    }

    private static Vector2 CellCenter(int gtx, int gty)
        => new Vector2(
            gtx * Chunk.TileSize + Chunk.TileSize * 0.5f,
            gty * Chunk.TileSize + Chunk.TileSize * 0.5f);

    // State of a cell in the global grid. Returns Empty for unloaded chunks.
    public TileState GetCellState(int gtx, int gty)
    {
        var (chunkPos, tx, ty) = GlobalCellToChunkLocal(gtx, gty);
        if (!_dict.TryGetValue(chunkPos, out var chunk)) return TileState.Empty;
        return chunk.Tiles[tx, ty].State;
    }

    // Material type of a cell. Default Stone for unloaded chunks / non-solid tiles
    // — type is only meaningful when State == Solid.
    public TileType GetCellType(int gtx, int gty)
    {
        var (chunkPos, tx, ty) = GlobalCellToChunkLocal(gtx, gty);
        if (!_dict.TryGetValue(chunkPos, out var chunk)) return TileType.Stone;
        return chunk.Tiles[tx, ty].Type;
    }

    // World-coord shim — kept for HandleBuildInput / tests / existing call sites.
    public bool TrySpawnSprout(float worldX, float worldY)
    {
        var (gtx, gty) = WorldToGlobalCell(worldX, worldY);
        return TryRequestTile(gtx, gty) != null;
    }

    // Request a tile at (gtx, gty). Returns the created node (Pending or Growing)
    // or null if the cell is already occupied / has no candidate parent.
    //
    // Parent priority for the *direction* of growth (solid neighbours only):
    //   below (gty+1) → grows up
    //   left  (gtx-1) → grows right
    //   right (gtx+1) → grows left
    //   above (gty-1) → grows down
    //
    // If no solid neighbour exists but at least one sprout neighbour does
    // (Pending or Growing), the request becomes Pending and waits for the
    // first parent to finalize — "first parent completed wins" defines the
    // growth direction at promotion time.
    public TileSproutNode TryRequestTile(int gtx, int gty, TileType type = TileType.Stone)
    {
        if (GetCellState(gtx, gty) != TileState.Empty) return null;
        if (Graph.TryGet(gtx, gty, out _)) return null;   // already Pending

        Vector2? solidParentCenter = null;
        if      (GetCellState(gtx,     gty + 1) == TileState.Solid) solidParentCenter = CellCenter(gtx,     gty + 1);
        else if (GetCellState(gtx - 1, gty    ) == TileState.Solid) solidParentCenter = CellCenter(gtx - 1, gty    );
        else if (GetCellState(gtx + 1, gty    ) == TileState.Solid) solidParentCenter = CellCenter(gtx + 1, gty    );
        else if (GetCellState(gtx,     gty - 1) == TileState.Solid) solidParentCenter = CellCenter(gtx,     gty - 1);

        var (chunkPos, tx, ty) = GlobalCellToChunkLocal(gtx, gty);

        if (solidParentCenter.HasValue)
        {
            if (!_dict.TryGetValue(chunkPos, out var chunk))
            {
                chunk = new Chunk { ChunkPos = chunkPos };
                _dict[chunkPos] = chunk;
            }
            var node = Graph.AddGrowing(chunkPos, tx, ty, gtx, gty,
                solidParentCenter.Value, CellCenter(gtx, gty),
                MovementConfig.Current.SproutLifetime);
            node.Type = type;
            chunk.Tiles[tx, ty].State  = TileState.Sprouting;
            chunk.Tiles[tx, ty].Type   = type;
            chunk.Tiles[tx, ty].Sprout = node;
            return node;
        }

        // No solid parent — fall through to sprout neighbours (Pending or Growing).
        var sproutParents = new List<TileSproutNode>(4);
        if (Graph.TryGet(gtx,     gty + 1, out var p1)) sproutParents.Add(p1);
        if (Graph.TryGet(gtx - 1, gty,     out var p2)) sproutParents.Add(p2);
        if (Graph.TryGet(gtx + 1, gty,     out var p3)) sproutParents.Add(p3);
        if (Graph.TryGet(gtx,     gty - 1, out var p4)) sproutParents.Add(p4);
        if (sproutParents.Count == 0) return null;

        // Pending nodes don't touch tile state — they're invisible to the world.
        // Chunk auto-creation deferred to promotion. Type is stamped on the node
        // so it survives the Pending→Growing handoff.
        var pending = Graph.AddPending(chunkPos, tx, ty, gtx, gty, sproutParents);
        pending.Type = type;
        return pending;
    }

    // Advance every Growing sprout. Finalize complete ones (cell flips to Solid,
    // sprout dropped from the graph) and propagate completion to any Pending
    // children (first parent to finalize wins → promote child to Growing).
    public void TickSprouts(float dt)
    {
        List<TileSproutNode> finalize = null;
        foreach (var n in Graph.Growing)
        {
            n.Age += dt;
            if (n.IsComplete)
                (finalize ??= new List<TileSproutNode>()).Add(n);
        }
        if (finalize == null) return;

        foreach (var n in finalize)
        {
            if (_dict.TryGetValue(n.ChunkPos, out var chunk))
            {
                chunk.Tiles[n.Tx, n.Ty].State  = TileState.Solid;
                chunk.Tiles[n.Tx, n.Ty].Sprout = null;
            }
            Graph.Remove(n);

            var parentCenter = CellCenter(n.Gtx, n.Gty);   // == n.EndCenter (now committed)
            foreach (var child in n.Children)
            {
                if (child.Status != TileSproutStatus.Pending) continue;
                var childCenter = CellCenter(child.Gtx, child.Gty);
                if (!Graph.TryPromote(child, parentCenter, childCenter, MovementConfig.Current.SproutLifetime))
                    continue;

                // Materialize the chunk + tile state now that the child is physical.
                if (!_dict.TryGetValue(child.ChunkPos, out var childChunk))
                {
                    childChunk = new Chunk { ChunkPos = child.ChunkPos };
                    _dict[child.ChunkPos] = childChunk;
                }
                childChunk.Tiles[child.Tx, child.Ty].State  = TileState.Sprouting;
                childChunk.Tiles[child.Tx, child.Ty].Type   = child.Type;
                childChunk.Tiles[child.Tx, child.Ty].Sprout = child;
            }
        }
    }

    // Apply `amount` damage to (gtx, gty). No-op on Empty/Sprouting cells (sprout
    // damage is deferred — see DAMAGE_HURTBOX_PLAN.md). Returns true if the tile
    // crossed the break threshold and was cleared this call.
    public bool DamageCell(int gtx, int gty, float amount)
    {
        if (GetCellState(gtx, gty) != TileState.Solid) return false;
        // Lookup the cell's material type so TileDamage can compare accumulated damage
        // against the per-type max HP (Sand ≈ 0.5, Dirt ≈ 1.0, Stone ≈ 2.0).
        var type = GetCellType(gtx, gty);
        if (!Damage.ApplyDamage(gtx, gty, amount, type)) return false;
        return BreakCell(gtx, gty);
    }

    // Clear a Solid cell to Empty and drop any residual damage entry. Returns true
    // if a tile actually changed. Body-side cleanup happens by the same query-driven
    // mechanism every other surface change relies on: collision-spawned
    // SurfaceDistance constraints get pruned next step via WorldHasSurface (which
    // goes through WorldQuery); state-owned FloatingSurfaceDistance contacts on
    // Standing/Crouched/WallSliding re-probe each frame and their CheckConditions
    // exits when the probe fails.
    public bool BreakCell(int gtx, int gty)
    {
        var (chunkPos, tx, ty) = GlobalCellToChunkLocal(gtx, gty);
        if (!_dict.TryGetValue(chunkPos, out var chunk)) return false;
        if (!chunk.Tiles[tx, ty].IsSolid) return false;
        var brokenType = chunk.Tiles[tx, ty].Type;
        chunk.Tiles[tx, ty].IsSolid = false;
        Damage.Clear(gtx, gty);
        OnTileBroken?.Invoke(CellCenter(gtx, gty), brokenType);
        return true;
    }

    // World-coord shim — kept for existing call sites that work in world space.
    public bool DestroyTile(float worldX, float worldY)
    {
        var (gtx, gty) = WorldToGlobalCell(worldX, worldY);
        return BreakCell(gtx, gty);
    }
}
