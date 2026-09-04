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
    public HashSet<(int gtx, int gty)>           Charge;
    public Dictionary<(int gtx, int gty), float> Foam;
    public Dictionary<(int gtx, int gty), float> Impact;
    public Dictionary<(int gtx, int gty), TileMassField.MassBucket> Mass;
    public Dictionary<EntityId, AvalancheWaves.WaveInfo>            Waves;
    public float SproutClock;
}

// Flat capture of the TileSproutGraph: every Pending/Growing node's data. Sprouts
// carry no edges — support is re-derived from the grid — so a node is pure value
// data and restore needs no re-linking pass. Per-face volume geometry is derived
// from Faces + the cell coords + Age, so none of it is stored either.
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
    public SproutFaces      Faces;
    public float            Lifetime;
    public float            Age;
    public EntityId         WaveId;
    public float            RequestTime;
}
