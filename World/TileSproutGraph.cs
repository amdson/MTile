using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Owns every known sprout (Pending or Growing), indexed by global cell so a
// new request can dedupe in O(1) and neighbour lookups are cheap.
// Lifecycle:
//   AddGrowing  — request already had a solid neighbour; node starts in Growing.
//   AddPending  — no solid neighbour; the cell is a ghost waiting for one.
//   TryPromote  — a neighbouring cell went Solid; flip to Growing on all its
//                 solid faces at once (this is what grows a shell, not a chain).
//   Remove      — node finalized (Growing → Solid) or was pruned/cancelled.
//
// There are no parent/child edges: promotion is driven by querying the grid.
// Pushing promotion off the finalize event (ChunkMap.TickSprouts) keeps that a
// 4-neighbour check per completed sprout rather than a scan of every ghost.
public sealed class TileSproutGraph
{
    private readonly Dictionary<(int, int), TileSproutNode> _nodes = new();
    private readonly List<TileSproutNode> _growing = new();
    private readonly List<TileSproutNode> _pending = new();

    public bool TryGet(int gtx, int gty, out TileSproutNode node)
        => _nodes.TryGetValue((gtx, gty), out node);

    public IReadOnlyList<TileSproutNode> Growing => _growing;
    public IReadOnlyList<TileSproutNode> Pending => _pending;

    public TileSproutNode AddGrowing(Point chunkPos, int tx, int ty, int gtx, int gty,
                                     SproutFaces faces, float lifetime)
    {
        var node = new TileSproutNode(chunkPos, tx, ty, gtx, gty);
        node.PromoteToGrowing(faces, lifetime);
        _nodes[(gtx, gty)] = node;
        _growing.Add(node);
        return node;
    }

    public TileSproutNode AddPending(Point chunkPos, int tx, int ty, int gtx, int gty)
    {
        var node = new TileSproutNode(chunkPos, tx, ty, gtx, gty);
        _nodes[(gtx, gty)] = node;
        _pending.Add(node);
        return node;
    }

    // initialAge: the completing neighbour's overshoot (Age - Lifetime) at finalize
    // time, so growth is continuous across the handoff instead of restarting at t=0.
    public bool TryPromote(TileSproutNode node, SproutFaces faces, float lifetime, float initialAge = 0f)
    {
        if (node.Status != TileSproutStatus.Pending) return false;
        if (faces == SproutFaces.None) return false;
        node.PromoteToGrowing(faces, lifetime, initialAge);
        _pending.Remove(node);
        _growing.Add(node);
        return true;
    }

    public void Remove(TileSproutNode node)
    {
        _nodes.Remove((node.Gtx, node.Gty));
        if (node.Status == TileSproutStatus.Growing) _growing.Remove(node);
        else                                         _pending.Remove(node);
    }

    // ── Snapshot/restore (roadmap goal 6) ───────────────────────────────────────
    // The graph is small (only live sprouts) but mutates every frame (ages tick), so
    // it's value-snapshotted rather than journaled. With edges gone a node is pure
    // value data, so Capture/Restore is a straight copy — no re-linking pass.
    // Iteration order of _growing/_pending is preserved (it's the order TickSprouts
    // and the prune walk use), so stepping stays deterministic across a restore.
    public SproutGraphData Capture()
    {
        var all = new List<TileSproutNode>(_growing.Count + _pending.Count);
        all.AddRange(_growing);
        all.AddRange(_pending);

        var nodes = new SproutNodeData[all.Count];
        for (int i = 0; i < all.Count; i++)
        {
            var n = all[i];
            nodes[i] = new SproutNodeData
            {
                ChunkPos = n.ChunkPos,
                Tx = n.Tx, Ty = n.Ty, Gtx = n.Gtx, Gty = n.Gty,
                Type     = n.Type,
                Status   = n.Status,
                Faces    = n.Faces,
                Lifetime = n.Lifetime,
                Age      = n.Age,
            };
        }
        return new SproutGraphData { Nodes = nodes };
    }

    public void Restore(SproutGraphData data)
    {
        _nodes.Clear();
        _growing.Clear();
        _pending.Clear();
        if (data?.Nodes == null) return;

        foreach (var d in data.Nodes)
        {
            var n = new TileSproutNode(d.ChunkPos, d.Tx, d.Ty, d.Gtx, d.Gty)
            {
                Type     = d.Type,
                Status   = d.Status,
                Faces    = d.Faces,
                Lifetime = d.Lifetime,
                Age      = d.Age,
            };
            _nodes[(d.Gtx, d.Gty)] = n;
            if (d.Status == TileSproutStatus.Growing) _growing.Add(n); else _pending.Add(n);
        }
    }

    // Drop every Pending node whose cell isn't in `keep`. Order-preserving, so the
    // surviving ghosts keep their insertion sequence. Used by ChunkMap's orphan
    // prune (ghosts no longer connected to anything that can support them).
    internal void PrunePending(HashSet<(int, int)> keep)
    {
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var n = _pending[i];
            if (keep.Contains((n.Gtx, n.Gty))) continue;
            _nodes.Remove((n.Gtx, n.Gty));
            _pending.RemoveAt(i);
        }
    }
}
