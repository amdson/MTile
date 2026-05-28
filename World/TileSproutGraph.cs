using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Owns every known sprout (Pending or Growing), indexed by global cell so a
// new request can dedupe in O(1) and child→parent links can be followed cheaply.
// Lifecycle:
//   AddGrowing  — request had a Solid parent; node starts in Growing.
//   AddPending  — request only had Pending/Growing sprout parents; node waits.
//   Promote     — a Pending node's first parent finalized; flip to Growing.
//   Remove      — node finalized (Growing → Solid → drop from graph).
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
                                     Vector2 parentCenter, Vector2 endCenter, float lifetime)
    {
        var node = new TileSproutNode(chunkPos, tx, ty, gtx, gty);
        node.PromoteToGrowing(parentCenter, endCenter, lifetime);
        _nodes[(gtx, gty)] = node;
        _growing.Add(node);
        return node;
    }

    public TileSproutNode AddPending(Point chunkPos, int tx, int ty, int gtx, int gty,
                                     IReadOnlyList<TileSproutNode> sproutParents)
    {
        var node = new TileSproutNode(chunkPos, tx, ty, gtx, gty);
        node.SproutParents.AddRange(sproutParents);
        foreach (var p in sproutParents) p.Children.Add(node);
        _nodes[(gtx, gty)] = node;
        _pending.Add(node);
        return node;
    }

    // First-parent-completed-wins. If two parents finalize in the same tick,
    // the first call promotes the child; subsequent calls see Growing and bail.
    // initialAge: the parent's overshoot (Age - Lifetime) at finalize time. Passed
    // through so the child picks up where the parent left off without losing any
    // sub-frame growth budget. Defaults to 0 for non-finalize-driven promotions.
    public bool TryPromote(TileSproutNode node, Vector2 parentCenter, Vector2 endCenter, float lifetime, float initialAge = 0f)
    {
        if (node.Status != TileSproutStatus.Pending) return false;
        node.PromoteToGrowing(parentCenter, endCenter, lifetime, initialAge);
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
    // it's value-snapshotted rather than journaled. Capture flattens each node + its
    // parent/child edges to cell-coord keys; Restore rebuilds fresh node objects and
    // re-links the edges via a key→node map. Iteration order of _growing/_pending is
    // preserved (it's the order TickSprouts/queries walk), so stepping stays
    // deterministic across a restore.
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
                ChunkPos    = n.ChunkPos,
                Tx = n.Tx, Ty = n.Ty, Gtx = n.Gtx, Gty = n.Gty,
                Type        = n.Type,
                Status      = n.Status,
                StartCenter = n.StartCenter,
                EndCenter   = n.EndCenter,
                Velocity    = n.Velocity,
                Lifetime    = n.Lifetime,
                Age         = n.Age,
                ParentKeys  = Keys(n.SproutParents),
                ChildKeys   = Keys(n.Children),
            };
        }
        return new SproutGraphData { Nodes = nodes };

        static (int, int)[] Keys(List<TileSproutNode> list)
        {
            var k = new (int, int)[list.Count];
            for (int i = 0; i < list.Count; i++) k[i] = (list[i].Gtx, list[i].Gty);
            return k;
        }
    }

    public void Restore(SproutGraphData data)
    {
        _nodes.Clear();
        _growing.Clear();
        _pending.Clear();
        if (data?.Nodes == null) return;

        // Pass 1: materialize bare nodes + index by cell key. Growing nodes carry the
        // shared immutable tile polygon; Pending nodes have none (matches the live
        // PromoteToGrowing contract).
        foreach (var d in data.Nodes)
        {
            var n = new TileSproutNode(d.ChunkPos, d.Tx, d.Ty, d.Gtx, d.Gty)
            {
                Type        = d.Type,
                Status      = d.Status,
                StartCenter = d.StartCenter,
                EndCenter   = d.EndCenter,
                Velocity    = d.Velocity,
                Lifetime    = d.Lifetime,
                Age         = d.Age,
                Polygon     = d.Status == TileSproutStatus.Growing ? TileWorld.TileShape : null,
            };
            _nodes[(d.Gtx, d.Gty)] = n;
            if (d.Status == TileSproutStatus.Growing) _growing.Add(n); else _pending.Add(n);
        }

        // Pass 2: re-link edges from the stored keys.
        foreach (var d in data.Nodes)
        {
            var n = _nodes[(d.Gtx, d.Gty)];
            if (d.ParentKeys != null)
                foreach (var k in d.ParentKeys)
                    if (_nodes.TryGetValue(k, out var p)) n.SproutParents.Add(p);
            if (d.ChildKeys != null)
                foreach (var k in d.ChildKeys)
                    if (_nodes.TryGetValue(k, out var c)) n.Children.Add(c);
        }
    }
}
