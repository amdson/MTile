using System.Collections.Generic;

namespace MTile;

// Sparse per-cell "charged" flag. A charged tile is one the player spent a full
// avalanche meter on (RMB-charge, then double-RMB on the block — see
// BlockEruptionHelpers.TryChargeBlock). The state is binary: a cell is charged or it
// isn't, there is no partial charge, so this is a set rather than a value store.
//
// Sparse for the same reason TileDamage is: almost no tile is charged at any moment,
// and paying a byte on every Tile in every chunk to say "no" is the wrong trade. Keyed
// on global cell coords so it survives a chunk being materialized lazily around it.
//
// Nothing in the sim reads the flag yet — it renders as a white tint (ChunkRenderer)
// and is the hook the charged-block payload will hang off. It is still full sim state,
// not a render flag: it is set by a player input, so it has to snapshot and roll back
// with the rest of the terrain, which is why it lives here and not in Drawing/.
public class TileCharge
{
    private readonly HashSet<(int gtx, int gty)> _charged = new();

    // Mark a cell charged. Returns false if it was already charged, so a caller can
    // decline to spend the meter twice on the same block.
    public bool Set(int gtx, int gty) => _charged.Add((gtx, gty));

    public bool IsCharged(int gtx, int gty) => _charged.Contains((gtx, gty));

    // Called when a cell becomes Empty, so a tile later rebuilt at the same coords
    // doesn't inherit a ghost charge. Mirrors TileDamage.Clear.
    public void Clear(int gtx, int gty) => _charged.Remove((gtx, gty));

    public int Count => _charged.Count;

    // Snapshot/restore. Membership only and value-typed keys, so a set copy is a full
    // deep copy with no aliasing into the live store.
    public HashSet<(int gtx, int gty)> Capture() => new(_charged);

    public void Restore(HashSet<(int gtx, int gty)> src)
    {
        _charged.Clear();
        if (src == null) return;
        foreach (var cell in src) _charged.Add(cell);
    }
}
