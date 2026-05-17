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
    public bool TryPromote(TileSproutNode node, Vector2 parentCenter, Vector2 endCenter, float lifetime)
    {
        if (node.Status != TileSproutStatus.Pending) return false;
        node.PromoteToGrowing(parentCenter, endCenter, lifetime);
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
}
