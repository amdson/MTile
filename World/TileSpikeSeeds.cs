using System.Collections.Generic;

namespace MTile;

// The arming timer for spike tiles (todo.txt #8). A placed spike tile (TileState.Spike,
// ChunkMap.TryRequestSpike) starts as a SEED — a small crystal clinging to its block —
// and runs CrystalLifecycle.SpikePlacement (seed → flash → grown). On its first Grown
// frame ChunkMap.TickSprouts drops the seed, and the bare spike tile is armed
// (ChunkMap.IsArmedSpike): only from then on does SpikeContactSystem let it bite. The
// 1.25 s telegraph is the counterplay window, and like an armed spike a hit knocks a
// seeding one off (ChunkMap.DamageCell).
//
// Why not the sprout graph: its Growing list is read as live collision volumes by the
// physics, corrector, predictor and ground probes, and its cells are support for
// ghosts. A spike must be none of those, so its timer lives here instead.
//
// Sparse, keyed on global cell coords, pure value data (the TerrainFrame it was seeded
// on), so a dictionary copy is a full deep copy — snapshotted with the terrain
// side-tables and folded into Simulation.Checksum. The phase is derived from the seed
// frame, never stored, so nothing here mutates per tick. Cleared by ChunkMap.WriteTile
// whenever the spike tile is replaced or removed; no refund for a lost seed.
public class TileSpikeSeeds
{
    public static CrystalLifecycle Lifecycle => CrystalLifecycle.SpikePlacement;

    private readonly Dictionary<(int gtx, int gty), int> _cells = new();
    private readonly List<(int gtx, int gty)> _due = new();

    // Seed a cell at terrain frame `frame`. Returns false if it is already seeded.
    public bool Set(int gtx, int gty, int frame) => _cells.TryAdd((gtx, gty), frame);

    public bool Has(int gtx, int gty) => _cells.ContainsKey((gtx, gty));

    public bool TryGet(int gtx, int gty, out int seededFrame) => _cells.TryGetValue((gtx, gty), out seededFrame);

    public void Clear(int gtx, int gty) => _cells.Remove((gtx, gty));

    public int Count => _cells.Count;

    // Phase of a seed at terrain frame `now` (render, tests). A seed still in the store
    // is Seed or Flash; Grown is reported only for the instant before it expires.
    public static CrystalPhase PhaseOf(int seededFrame, int now) => Lifecycle.PhaseAt(now - seededFrame);

    // Order unspecified; consumers must be order-independent (checksum XOR, renderer).
    public IEnumerable<KeyValuePair<(int gtx, int gty), int>> Entries => _cells;

    // Drop every seed whose lifecycle reached Grown at frame `now`, arming its spike.
    // Removals of different cells commute, so dictionary order cannot leak.
    public void Tick(int now)
    {
        if (_cells.Count == 0) return;
        _due.Clear();
        foreach (var (cell, frame) in _cells)
            if (PhaseOf(frame, now) >= CrystalPhase.Grown) _due.Add(cell);
        foreach (var cell in _due) _cells.Remove(cell);
    }

    public Dictionary<(int gtx, int gty), int> Capture() => new(_cells);

    public void Restore(Dictionary<(int gtx, int gty), int> src)
    {
        _cells.Clear();
        if (src == null) return;
        foreach (var kv in src) _cells[kv.Key] = kv.Value;
    }
}
