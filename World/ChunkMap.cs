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

    // Per-cell accumulated impact impulse, with decay. PhysicsWorld routes
    // contact impulses through this so a spring-padded landing (player) accrues
    // damage over the frames the spring spreads the impulse across — see
    // TileImpactAccumulator for the design rationale.
    public readonly TileImpactAccumulator Impact = new();

    // Per-cell decay timer for Foam tiles. Registered on Foam-sprout finalize,
    // ticked alongside sprouts, cleared on BreakCell so a foam tile broken
    // early (by damage / overwrite) doesn't fire a second BreakCell later.
    public readonly FoamDecay Foam = new();
    // Cached delegate for Foam.Tick so we don't allocate a fresh closure per call.
    private readonly Action<int, int> _breakCellAction;

    // Reversible delta log for the dense tile grid (roadmap goal 6). Every tile write
    // + lazy chunk creation funnels through WriteTile/GetOrCreateChunk, which append
    // here; CaptureTerrain records the mark and RestoreTerrain rewinds to it. The
    // sparse side-structures above (Graph/Damage/Foam/Impact) are value-snapshotted
    // instead (they tick every frame). See TerrainJournal.
    private readonly TerrainJournal _journal = new();

    public ChunkMap() => _breakCellAction = (gx, gy) => BreakCell(gx, gy);

    // Lazily materialize a chunk, journaling the creation so a restore can drop it.
    private Chunk GetOrCreateChunk(Point pos)
    {
        if (_dict.TryGetValue(pos, out var c)) return c;
        c = new Chunk { ChunkPos = pos };
        _dict[pos] = c;
        _journal.RecordChunkCreated(pos);
        return c;
    }

    // The single journaled tile-mutation primitive. Records the cell's prior state +
    // type before overwriting it, so the dense grid is fully roll-back-able. Sprout
    // refs aren't journaled — they're re-linked from the restored graph (see
    // RestoreTerrain), keeping the journal entries purely value data.
    private void WriteTile(Chunk chunk, int tx, int ty, TileState state, TileType type, TileSproutNode sprout)
    {
        ref var t = ref chunk.Tiles[tx, ty];
        _journal.RecordTileWrite(chunk.ChunkPos, tx, ty, t.State, t.Type);
        t.State  = state;
        t.Type   = type;
        t.Sprout = sprout;
    }

    // Fires when BreakCell actually clears a Solid tile. Arguments are the cell's
    // world-space center and its material type at break time. Subscribers (Game1's
    // particle system) react to feedback events without ChunkMap knowing about them.
    public System.Action<Microsoft.Xna.Framework.Vector2, TileType> OnTileBroken;

    // Iteration view used by physics + drawing — only Growing nodes are physically
    // present in the world. Pending nodes live solely in the graph.
    public IReadOnlyList<TileSproutNode> ActiveSprouts => Graph.Growing;

    // Drawing-only view of queued (not yet growing) sprouts, used to render ghost
    // outlines of the build a player has requested. Not physically present.
    public IReadOnlyList<TileSproutNode> PendingSprouts => Graph.Pending;

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
            var chunk = GetOrCreateChunk(chunkPos);
            var node = Graph.AddGrowing(chunkPos, tx, ty, gtx, gty,
                solidParentCenter.Value, CellCenter(gtx, gty),
                MovementConfig.Current.SproutLifetime);
            node.Type = type;
            WriteTile(chunk, tx, ty, TileState.Sprouting, type, node);
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
        // Foam decay runs unconditionally each frame — its lifecycle is
        // independent of sprout finalization. Do it first so a foam cell that
        // expires this frame is broken before subsequent passes (impact / damage)
        // try to read it as solid.
        Foam.Tick(dt, _breakCellAction);

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
                WriteTile(chunk, n.Tx, n.Ty, TileState.Solid, chunk.Tiles[n.Tx, n.Ty].Type, null);
            // Foam tiles get a decay timer registered the moment they finalize;
            // see FoamDecay. Other types never enter the decay map.
            if (n.Type == TileType.Foam)
                Foam.Register(n.Gtx, n.Gty);
            Graph.Remove(n);

            // The parent's age crossed Lifetime this tick. Carry its overshoot
            // (Age − Lifetime ∈ [0, dt)) into the child's starting Age so growth
            // is continuous across the handoff — the child resumes from where
            // the parent left off, not from t=0 (which would put the child's
            // polygon exactly atop the just-Solid parent cell for one frame).
            float overshoot = MathF.Max(0f, n.Age - n.Lifetime);

            var parentCenter = CellCenter(n.Gtx, n.Gty);   // == n.EndCenter (now committed)
            foreach (var child in n.Children)
            {
                if (child.Status != TileSproutStatus.Pending) continue;
                var childCenter = CellCenter(child.Gtx, child.Gty);
                if (!Graph.TryPromote(child, parentCenter, childCenter, MovementConfig.Current.SproutLifetime, overshoot))
                    continue;

                // Materialize the chunk + tile state now that the child is physical.
                var childChunk = GetOrCreateChunk(child.ChunkPos);
                WriteTile(childChunk, child.Tx, child.Ty, TileState.Sprouting, child.Type, child);
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
        // Empty the cell (journaled). Type is preserved in the prior-state record so a
        // restore brings the material back; the live cell's Type is irrelevant once Empty.
        WriteTile(chunk, tx, ty, TileState.Empty, brokenType, null);
        Damage.Clear(gtx, gty);
        // Foam decay entry (if any) is invalidated by the break — without this,
        // a foam tile broken early would still trigger another BreakCell when
        // its timer expires (no-op on an empty cell, but a needless call).
        if (brokenType == TileType.Foam) Foam.Clear(gtx, gty);
        OnTileBroken?.Invoke(CellCenter(gtx, gty), brokenType);
        return true;
    }

    // World-coord shim — kept for existing call sites that work in world space.
    public bool DestroyTile(float worldX, float worldY)
    {
        var (gtx, gty) = WorldToGlobalCell(worldX, worldY);
        return BreakCell(gtx, gty);
    }

    // ── Snapshot / restore (roadmap goal 6) ─────────────────────────────────────
    // Dense tile grid: a journal mark (rewound on restore). Sparse side-structures:
    // value copies. Together these roll the whole terrain back to capture time.
    public TerrainSnapshot CaptureTerrain() => new()
    {
        JournalMark = _journal.Mark,
        Graph       = Graph.Capture(),
        Damage      = Damage.Capture(),
        Foam        = Foam.Capture(),
        Impact      = Impact.Capture(),
    };

    public void RestoreTerrain(TerrainSnapshot s)
    {
        // 1. Roll the dense grid back by undoing journaled writes/creations past the
        //    mark. RevertTile clears Sprout refs (re-linked in step 3).
        _journal.RewindTo(s.JournalMark, RevertTile, pos => _dict.Remove(pos));

        // 2. Restore the sparse structures by value.
        Graph.Restore(s.Graph);
        Damage.Restore(s.Damage);
        Foam.Restore(s.Foam);
        Impact.Restore(s.Impact);

        // 3. Re-link tile→sprout refs. Every Growing node's cell is Sprouting at the
        //    restored frame (grid and graph were captured together), so this rebuilds
        //    the Tile.Sprout pointers the journal deliberately didn't carry.
        foreach (var n in Graph.Growing)
            if (_dict.TryGetValue(n.ChunkPos, out var chunk))
                chunk.Tiles[n.Tx, n.Ty].Sprout = n;
    }

    private void RevertTile(Point chunkPos, int tx, int ty, TileState prevState, TileType prevType)
    {
        if (!_dict.TryGetValue(chunkPos, out var chunk)) return;
        ref var t = ref chunk.Tiles[tx, ty];
        t.State  = prevState;
        t.Type   = prevType;
        t.Sprout = null;   // re-linked in RestoreTerrain step 3 if this cell is Sprouting
    }

    // ── Full dense capture (in-game recorder) ───────────────────────────────────
    // The journal-based snapshot above can't support free back-and-forth scrubbing
    // (RewindTo truncates history), so the recorder captures the whole dense grid each
    // frame instead. See DenseTerrainCapture for the rationale; these bypass the journal
    // entirely (the recorder never re-simulates, so no inverse-delta is needed).
    public DenseTerrainCapture CaptureDense()
    {
        var chunks = new DenseTerrainCapture.ChunkCells[_dict.Count];
        int idx = 0;
        foreach (var kv in _dict)
        {
            var tiles = kv.Value.Tiles;
            var state = new TileState[Chunk.Size * Chunk.Size];
            var type  = new TileType[Chunk.Size * Chunk.Size];
            for (int tx = 0; tx < Chunk.Size; tx++)
                for (int ty = 0; ty < Chunk.Size; ty++)
                {
                    int i = tx * Chunk.Size + ty;
                    state[i] = tiles[tx, ty].State;
                    type[i]  = tiles[tx, ty].Type;
                }
            chunks[idx++] = new DenseTerrainCapture.ChunkCells { Pos = kv.Key, State = state, Type = type };
        }
        return new DenseTerrainCapture { Chunks = chunks };
    }

    // Overwrite the dense grid to exactly match a captured frame, in any order. Direct
    // writes — no journaling. Call AFTER the sparse structures are restored (i.e. after
    // Simulation.Restore → RestoreTerrain) so the sprout-ref relink points at the
    // correct Graph.Growing nodes for this frame.
    public void RestoreDense(DenseTerrainCapture cap)
    {
        // Drop chunks that don't exist in the captured frame (created later).
        var present = new HashSet<Point>();
        foreach (var cc in cap.Chunks) present.Add(cc.Pos);
        var prune = new List<Point>();
        foreach (var pos in _dict.Keys) if (!present.Contains(pos)) prune.Add(pos);
        foreach (var pos in prune) _dict.Remove(pos);

        // Overwrite (or materialize) each captured chunk's cells.
        foreach (var cc in cap.Chunks)
        {
            if (!_dict.TryGetValue(cc.Pos, out var chunk))
            {
                chunk = new Chunk { ChunkPos = cc.Pos };
                _dict[cc.Pos] = chunk;
            }
            for (int tx = 0; tx < Chunk.Size; tx++)
                for (int ty = 0; ty < Chunk.Size; ty++)
                {
                    int i = tx * Chunk.Size + ty;
                    ref var t = ref chunk.Tiles[tx, ty];
                    t.State  = cc.State[i];
                    t.Type   = cc.Type[i];
                    t.Sprout = null;
                }
        }

        // Re-link tile→sprout refs from the restored graph (same as RestoreTerrain step 3).
        foreach (var n in Graph.Growing)
            if (_dict.TryGetValue(n.ChunkPos, out var chunk))
                chunk.Tiles[n.Tx, n.Ty].Sprout = n;
    }
}
