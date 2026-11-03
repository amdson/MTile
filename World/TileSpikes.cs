using System.Collections.Generic;

namespace MTile;

// Sparse per-cell spike overlay (Plans/BLOCK_OWNERSHIP_SPIKES.md §2). A spiked cell is
// an ordinary solid tile wearing an overlay that (a) is brittle — any hit that reaches the
// cell knocks the spike off instead of damaging the block (ChunkMap.DamageCell), and
// breaking the block takes it too — and (b) hurts bodies of an OPPOSING side that touch
// it (SpikeContactSystem). The overlay records which side placed it, because
// "opposing" is relative to the placer: the placer's side is never hurt.
//
// Sparse for the same reason TileDamage / TileCharge are: almost no tile is spiked.
// Keyed on global cell coords. Pure value data (the side is a struct), so a dictionary
// copy is a full deep copy — snapshotted with the rest of the terrain's side-tables and
// folded into Simulation.Checksum.
//
// Cleared by ChunkMap.BreakCell (the single break path) and EditorSetCell, so a tile
// rebuilt at the same coords never inherits a ghost overlay.
public class TileSpikes
{
    // Who owns a spiked cell. Faction is the primary side test (players are Player1 /
    // Player2); Team breaks ties between two Enemy-faction parties, mirroring
    // CombatSystem.AcrossTeams.
    public readonly struct Side
    {
        public readonly Faction Faction;
        public readonly int     Team;
        public Side(Faction faction, int team) { Faction = faction; Team = team; }
    }

    private readonly Dictionary<(int gtx, int gty), Side> _cells = new();

    // Spike a cell for `side`. Returns false (and changes nothing) if it was already
    // spiked — by anyone — so a caller can decline to charge twice.
    public bool Set(int gtx, int gty, Side side) => _cells.TryAdd((gtx, gty), side);

    public bool IsSpiked(int gtx, int gty) => _cells.ContainsKey((gtx, gty));

    public bool TryGet(int gtx, int gty, out Side side) => _cells.TryGetValue((gtx, gty), out side);

    public void Clear(int gtx, int gty) => _cells.Remove((gtx, gty));

    public int Count => _cells.Count;

    // Iterate spiked cells (checksum fold, render). Order is unspecified — consumers must
    // be order-independent (the checksum XOR-folds; the renderer draws per cell).
    public IEnumerable<KeyValuePair<(int gtx, int gty), Side>> Entries => _cells;

    public Dictionary<(int gtx, int gty), Side> Capture() => new(_cells);

    public void Restore(Dictionary<(int gtx, int gty), Side> src)
    {
        _cells.Clear();
        if (src == null) return;
        foreach (var kv in src) _cells[kv.Key] = kv.Value;
    }
}
