using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Plain-data capture of the terrain's *sparse* state plus a mark into the dense-grid
// journal (roadmap goal 6). The dense tile arrays themselves are NOT copied here —
// they're rolled back via TerrainJournal.RewindTo(JournalMark). Everything else (the
// sprout graph, per-cell HP, foam timers, impact accumulator) is small and mutates
// every frame, so it's snapshotted by value.
//
// Caveat: JournalMark is meaningful only against the ChunkMap it was captured from
// (deltas are relative to that instance's grid). Restoring a terrain snapshot onto a
// *different* ChunkMap is only valid if no tiles were journaled (Mark unchanged) —
// the value-copied sparse parts transfer fine, but a non-empty journal cannot. This
// matches the rollback use case (same instance across restores).
public sealed class TerrainSnapshot
{
    public int JournalMark;
    public SproutGraphData Graph;
    public Dictionary<(int gtx, int gty), float> Damage;
    public Dictionary<(int gtx, int gty), float> Foam;
    public Dictionary<(int gtx, int gty), float> Impact;
}

// Flat capture of the TileSproutGraph: every Pending/Growing node's data plus its
// parent/child edges expressed as cell-coord keys (resolved back to live nodes on
// restore). Edges are stored explicitly in both directions because a promoted child
// clears its SproutParents while the parent keeps the forward edge — so neither
// direction can be inferred from the other.
public sealed class SproutGraphData
{
    public SproutNodeData[] Nodes;
}

public struct SproutNodeData
{
    public Point            ChunkPos;
    public int              Tx, Ty, Gtx, Gty;
    public TileType         Type;
    public TileSproutStatus Status;
    public Vector2          StartCenter;
    public Vector2          EndCenter;
    public Vector2          Velocity;
    public float            Lifetime;
    public float            Age;
    public (int gtx, int gty)[] ParentKeys;   // SproutParents
    public (int gtx, int gty)[] ChildKeys;     // Children
}
