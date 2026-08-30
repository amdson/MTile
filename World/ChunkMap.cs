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

    // Sparse per-cell binary "charged" flag, set by the double-RMB block-charge gesture.
    // Cleared on break alongside Damage so a rebuilt cell starts uncharged.
    public readonly TileCharge Charge = new();

    // Per-cell accumulated impact impulse, with decay. PhysicsWorld routes
    // contact impulses through this so a spring-padded landing (player) accrues
    // damage over the frames the spring spreads the impulse across — see
    // TileImpactAccumulator for the design rationale.
    public readonly TileImpactAccumulator Impact = new();

    // Per-cell accumulating build mass + the spill cascade that turns it into sprouts.
    // Fed live by the RMB paint gesture; see TileMassField.
    public readonly TileMassField Mass = new();

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

    // Set when a break (or a sprout cancellation) may have stranded Pending
    // ghosts. Cleared by PruneOrphanedGhosts at the top of the next TickSprouts,
    // which is the only thing that walks the ghost set.
    private bool _ghostsDirty;

    // Fires when BreakCell actually clears a Solid tile. Arguments are the cell's
    // world-space center and its material type at break time. Subscribers (Game1's
    // particle system) react to feedback events without ChunkMap knowing about them.
    public System.Action<Microsoft.Xna.Framework.Vector2, TileType> OnTileBroken;

    // Fires when a cell first becomes visible terrain — a Growing sprout appearing,
    // whether from an ordinary build or a forced burst. The mirror of OnTileBroken, and
    // like it, purely a feedback channel: ChunkMap knows nothing about its subscribers.
    // Not fired for Pending nodes, which are invisible to the world.
    public System.Action<Microsoft.Xna.Framework.Vector2, TileType> OnTilePlaced;

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
                TileWorld.TileShape,
                SolidShapeSource.Tile, t.Gtx, t.Gty);
        }

        // A growing sprout emits one full-size volume per supporting face, each
        // translating out of that parent's cell. Multi-face sprouts yield several
        // overlapping shapes; collision takes the union, so the overlap is free.
        const float half = Chunk.TileSize * 0.5f;
        foreach (var s in Graph.Growing)
        foreach (var face in TileSproutNode.FaceOrder)
        {
            if ((s.Faces & face) == 0) continue;
            var c = s.VolumeCenter(face);
            if (c.X + half <= region.Left || c.X - half >= region.Right) continue;
            if (c.Y + half <= region.Top  || c.Y - half >= region.Bottom) continue;
            yield return new SolidShapeRef(
                c.X - half, c.Y - half, c.X + half, c.Y + half,
                c, s.VolumeVelocity(face), TileWorld.TileShape,
                SolidShapeSource.Sprout, s.Gtx, s.Gty);
        }
    }

    bool ISolidShapeProvider.IsSolidAt(float worldX, float worldY)
    {
        if (TileQuery.IsSolidAt(this, worldX, worldY)) return true;

        // Growing sprouts: point-in-AABB against each face volume's current position.
        const float half = Chunk.TileSize * 0.5f;
        foreach (var s in Graph.Growing)
        foreach (var face in TileSproutNode.FaceOrder)
        {
            if ((s.Faces & face) == 0) continue;
            var c = s.VolumeCenter(face);
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

    // Every Solid 4-neighbour of (gtx, gty), as a face mask. This is the whole
    // support query — a sprout has no recorded parent, it just asks the grid.
    private SproutFaces SolidFaces(int gtx, int gty)
    {
        var f = SproutFaces.None;
        if (GetCellState(gtx,     gty + 1) == TileState.Solid) f |= SproutFaces.Below;
        if (GetCellState(gtx - 1, gty    ) == TileState.Solid) f |= SproutFaces.Left;
        if (GetCellState(gtx + 1, gty    ) == TileState.Solid) f |= SproutFaces.Right;
        if (GetCellState(gtx,     gty - 1) == TileState.Solid) f |= SproutFaces.Above;
        return f;
    }

    private bool HasSproutNeighbour(int gtx, int gty)
        => Graph.TryGet(gtx,     gty + 1, out _)
        || Graph.TryGet(gtx - 1, gty,     out _)
        || Graph.TryGet(gtx + 1, gty,     out _)
        || Graph.TryGet(gtx,     gty - 1, out _);

    // Request a tile at (gtx, gty). Returns the created node (Pending or Growing)
    // or null if the cell is already occupied / has nothing to grow from.
    //
    // If any 4-neighbour is Solid the cell starts Growing immediately, on *all*
    // of its solid faces at once — one volume pushes out of each. Otherwise, if
    // it touches an existing sprout (Pending or Growing) it becomes a Pending
    // ghost and waits for a neighbour to solidify. With no neighbour of either
    // kind there's nothing to build from and the request is rejected.
    public TileSproutNode TryRequestTile(int gtx, int gty, TileType type = TileType.Stone)
    {
        if (GetCellState(gtx, gty) != TileState.Empty) return null;
        if (Graph.TryGet(gtx, gty, out _)) return null;   // already requested

        var faces = SolidFaces(gtx, gty);
        var (chunkPos, tx, ty) = GlobalCellToChunkLocal(gtx, gty);

        if (faces != SproutFaces.None)
        {
            var chunk = GetOrCreateChunk(chunkPos);
            var node = Graph.AddGrowing(chunkPos, tx, ty, gtx, gty,
                faces, MovementConfig.Current.SproutLifetime);
            node.Type = type;
            WriteTile(chunk, tx, ty, TileState.Sprouting, type, node);
            OnTilePlaced?.Invoke(CellCenter(gtx, gty), type);
            return node;
        }

        if (!HasSproutNeighbour(gtx, gty)) return null;

        // Pending nodes don't touch tile state — they're invisible to the world.
        // Chunk auto-creation deferred to promotion. Type is stamped on the node
        // so it survives the Pending→Growing handoff.
        var pending = Graph.AddPending(chunkPos, tx, ty, gtx, gty);
        pending.Type = type;
        return pending;
    }

    // True iff TryRequestTile would create a node here: the cell is free AND has
    // something (Solid face or existing sprout) to build from. Pure query — it is how
    // BlockBurstAction tells "RMB is painting this cell" from "RMB is over dead air".
    public bool CanRequestTile(int gtx, int gty)
        => GetCellState(gtx, gty) == TileState.Empty
        && !Graph.TryGet(gtx, gty, out _)
        && (SolidFaces(gtx, gty) != SproutFaces.None || HasSproutNeighbour(gtx, gty));

    // Sprout a cell with NO support — the one path that skips the "must touch solid or
    // an existing sprout" rule. Conjuring matter out of nothing is a deliberate ability
    // (BlockBurstAction), not something the ordinary build gesture may do, so it lives
    // in its own method rather than as a flag on TryRequestTile.
    //
    // Faces: any real solid support is honoured, so a burst that clips terrain grows out
    // of it normally; with nothing adjacent it grows on all four at once, which reads as
    // a puff condensing in place. Once it solidifies, ordinary TryRequestTile ghosts
    // parked on its neighbours promote off it like any other terrain.
    public TileSproutNode ForceSprout(int gtx, int gty, TileType type)
    {
        if (GetCellState(gtx, gty) != TileState.Empty) return null;
        if (Graph.TryGet(gtx, gty, out _)) return null;

        var faces = SolidFaces(gtx, gty);
        if (faces == SproutFaces.None)
            faces = SproutFaces.Below | SproutFaces.Left | SproutFaces.Right | SproutFaces.Above;

        var (chunkPos, tx, ty) = GlobalCellToChunkLocal(gtx, gty);
        var chunk = GetOrCreateChunk(chunkPos);
        var node = Graph.AddGrowing(chunkPos, tx, ty, gtx, gty,
            faces, MovementConfig.Current.SproutLifetime);
        node.Type = type;
        WriteTile(chunk, tx, ty, TileState.Sprouting, type, node);
        OnTilePlaced?.Invoke(CellCenter(gtx, gty), type);
        return node;
    }

    // Advance every Growing sprout, finalize the complete ones (cell flips to
    // Solid, sprout dropped from the graph), then promote any Pending ghost that
    // the newly-solid cells now support.
    //
    // Finalization and promotion are two separate passes on purpose. Promoting
    // inside the finalize loop would let a ghost see only the neighbours that
    // happened to be committed earlier in the batch, so a ghost supported from
    // two sides would promote with one face and grow asymmetrically. Committing
    // the whole ring first means every ghost sees the same completed ring, which
    // is what makes the build expand as a symmetric shell.
    public void TickSprouts(float dt)
    {
        // Foam decay runs unconditionally each frame — its lifecycle is
        // independent of sprout finalization. Do it first so a foam cell that
        // expires this frame is broken before subsequent passes (impact / damage)
        // try to read it as solid.
        Foam.Tick(dt, _breakCellAction);
        // Bleed stale partial build mass so the table stays bounded (see TileMassField).
        Mass.Tick(dt);

        // Deferred cleanup from breaks (including Foam's, above): ghosts that are
        // no longer connected to anything that could ever support them.
        PruneOrphanedGhosts();

        List<TileSproutNode> finalize = null;
        foreach (var n in Graph.Growing)
        {
            n.Age += dt;
            if (n.IsComplete)
                (finalize ??= new List<TileSproutNode>()).Add(n);
        }
        if (finalize == null) return;

        // Pass 1 — commit the completed ring.
        float overshoot = 0f;
        foreach (var n in finalize)
        {
            if (_dict.TryGetValue(n.ChunkPos, out var chunk))
                WriteTile(chunk, n.Tx, n.Ty, TileState.Solid, chunk.Tiles[n.Tx, n.Ty].Type, null);
            // Foam tiles get a decay timer registered the moment they finalize;
            // see FoamDecay. Other types never enter the decay map.
            if (n.Type == TileType.Foam)
                Foam.Register(n.Gtx, n.Gty);
            Graph.Remove(n);

            // Age crossed Lifetime this tick. Carry the largest overshoot
            // (Age − Lifetime ∈ [0, dt)) into the next ring's starting Age so
            // growth is continuous across the handoff instead of resuming from
            // t=0 (which would put the new volumes exactly atop the just-Solid
            // cells for one frame). Uniform across a ring, which shares a clock.
            overshoot = MathF.Max(overshoot, n.Age - n.Lifetime);
        }

        // Pass 2 — promote every ghost adjacent to a cell that just solidified.
        // O(4) per completed sprout, so no scan over the ghost set.
        foreach (var n in finalize)
        foreach (var face in TileSproutNode.FaceOrder)
        {
            var o = TileSproutNode.FaceOffset(face);
            int cgtx = n.Gtx + o.X, cgty = n.Gty + o.Y;
            if (!Graph.TryGet(cgtx, cgty, out var ghost)) continue;
            if (ghost.Status != TileSproutStatus.Pending) continue;

            if (!Graph.TryPromote(ghost, SolidFaces(cgtx, cgty),
                                  MovementConfig.Current.SproutLifetime, overshoot))
                continue;

            // Materialize the chunk + tile state now that the ghost is physical.
            var ghostChunk = GetOrCreateChunk(ghost.ChunkPos);
            WriteTile(ghostChunk, ghost.Tx, ghost.Ty, TileState.Sprouting, ghost.Type, ghost);
        }
    }

    // A cell was cleared. Any Growing sprout that was pushing out of it loses that
    // face; a sprout that loses its last face has nothing left to grow from and is
    // cancelled outright (its cell reverts to Empty). Called from BreakCell, so
    // it's O(4) per break rather than a sweep — and it has to be immediate,
    // because physics reads the face volumes every step.
    private void DropSupportFor(int gtx, int gty)
    {
        _ghostsDirty = true;   // a break can orphan ghosts; swept next TickSprouts

        foreach (var face in TileSproutNode.FaceOrder)
        {
            var o = TileSproutNode.FaceOffset(face);
            int ngtx = gtx + o.X, ngty = gty + o.Y;
            if (!Graph.TryGet(ngtx, ngty, out var n)) continue;
            if (n.Status != TileSproutStatus.Growing) continue;

            // The face of the neighbour that points back at the broken cell.
            n.Faces &= ~TileSproutNode.FaceTowards(ngtx, ngty, gtx, gty);
            if (n.Faces != SproutFaces.None) continue;

            if (_dict.TryGetValue(n.ChunkPos, out var chunk))
                WriteTile(chunk, n.Tx, n.Ty, TileState.Empty, chunk.Tiles[n.Tx, n.Ty].Type, null);
            Graph.Remove(n);
        }
    }

    // A ghost is only ever going to grow if it can reach something that can
    // support it — a Solid cell or a Growing sprout — through a chain of ghosts.
    // Anything else (a ring of ghosts left floating after the terrain under it was
    // destroyed) can never promote, so it's deleted.
    //
    // This is a flood fill over the ghost set, but it only runs when a break or a
    // sprout cancellation set the dirty flag, so steady-state cost is zero.
    private void PruneOrphanedGhosts()
    {
        if (!_ghostsDirty) return;
        _ghostsDirty = false;
        if (Graph.Pending.Count == 0) return;

        var reachable = new HashSet<(int, int)>();
        var queue = new Queue<TileSproutNode>();

        // Seeds: ghosts directly touching a Solid cell or a Growing sprout.
        foreach (var g in Graph.Pending)
        {
            if (!TouchesSupport(g)) continue;
            if (reachable.Add((g.Gtx, g.Gty))) queue.Enqueue(g);
        }

        while (queue.Count > 0)
        {
            var g = queue.Dequeue();
            foreach (var face in TileSproutNode.FaceOrder)
            {
                var o = TileSproutNode.FaceOffset(face);
                if (!Graph.TryGet(g.Gtx + o.X, g.Gty + o.Y, out var nb)) continue;
                if (nb.Status != TileSproutStatus.Pending) continue;
                if (reachable.Add((nb.Gtx, nb.Gty))) queue.Enqueue(nb);
            }
        }

        Graph.PrunePending(reachable);

        bool TouchesSupport(TileSproutNode g)
        {
            foreach (var face in TileSproutNode.FaceOrder)
            {
                var o = TileSproutNode.FaceOffset(face);
                int ngtx = g.Gtx + o.X, ngty = g.Gty + o.Y;
                if (GetCellState(ngtx, ngty) == TileState.Solid) return true;
                if (Graph.TryGet(ngtx, ngty, out var nb) && nb.Status == TileSproutStatus.Growing)
                    return true;
            }
            return false;
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
        // A Sprouting cell is destructible too. It used to be exempt purely because the
        // guard tested IsSolid (== State.Solid), so the one tile a body can be actively
        // crushed by was the one tile nothing could break. Cancel the node first: its
        // face volumes are live physics shapes read every step, so leaving it in
        // Graph.Growing would keep the shape colliding after the cell went Empty.
        if (chunk.Tiles[tx, ty].State == TileState.Sprouting)
        {
            var node = chunk.Tiles[tx, ty].Sprout;
            var sproutType = chunk.Tiles[tx, ty].Type;
            if (node != null) Graph.Remove(node);
            WriteTile(chunk, tx, ty, TileState.Empty, sproutType, null);
            Damage.Clear(gtx, gty);
            Charge.Clear(gtx, gty);
            DropSupportFor(gtx, gty);
            OnTileBroken?.Invoke(CellCenter(gtx, gty), sproutType);
            return true;
        }
        if (!chunk.Tiles[tx, ty].IsSolid) return false;
        var brokenType = chunk.Tiles[tx, ty].Type;
        // Empty the cell (journaled). Type is preserved in the prior-state record so a
        // restore brings the material back; the live cell's Type is irrelevant once Empty.
        WriteTile(chunk, tx, ty, TileState.Empty, brokenType, null);
        Damage.Clear(gtx, gty);
        Charge.Clear(gtx, gty);
        // Foam decay entry (if any) is invalidated by the break — without this,
        // a foam tile broken early would still trigger another BreakCell when
        // its timer expires (no-op on an empty cell, but a needless call).
        if (brokenType == TileType.Foam) Foam.Clear(gtx, gty);
        // Sprouts growing out of this cell just lost that face (and are cancelled
        // if it was their last one); ghosts get swept next tick.
        DropSupportFor(gtx, gty);
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
        Charge      = Charge.Capture(),
        Foam        = Foam.Capture(),
        Impact      = Impact.Capture(),
        Mass        = Mass.Capture(),
    };

    public void RestoreTerrain(TerrainSnapshot s)
    {
        // 1. Roll the dense grid back by undoing journaled writes/creations past the
        //    mark. RevertTile clears Sprout refs (re-linked in step 3).
        _journal.RewindTo(s.JournalMark, RevertTile, pos => _dict.Remove(pos));

        // 2. Restore the sparse structures by value.
        Graph.Restore(s.Graph);
        Damage.Restore(s.Damage);
        Charge.Restore(s.Charge);
        Foam.Restore(s.Foam);
        Impact.Restore(s.Impact);
        Mass.Restore(s.Mass);

        // 3. Re-link tile→sprout refs. Every Growing node's cell is Sprouting at the
        //    restored frame (grid and graph were captured together), so this rebuilds
        //    the Tile.Sprout pointers the journal deliberately didn't carry.
        foreach (var n in Graph.Growing)
            if (_dict.TryGetValue(n.ChunkPos, out var chunk))
                chunk.Tiles[n.Tx, n.Ty].Sprout = n;

        // Support is derived from the grid, so a restored graph is self-consistent
        // by construction — but re-arm the sweep rather than trusting that the
        // captured frame's dirty flag was false.
        _ghostsDirty = true;
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
