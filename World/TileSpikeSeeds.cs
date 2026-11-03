using System.Collections.Generic;

namespace MTile;

// Spike crystals still on their way to becoming a spike (todo.txt #8). Placing a spike
// no longer spikes the cell instantly: it SEEDS it — a small crystal on the block — and
// the seed runs CrystalLifecycle.SpikePlacement (seed → flash → grown). On its first
// Grown frame ChunkMap.TickSprouts promotes it into the ordinary TileSpikes overlay and
// drops it from here, so every existing spike rule (contact damage, owner immunity)
// applies only from then on. Seeds are harmless, and like a grown spike a hit knocks one
// off (ChunkMap.DamageCell): the 1.25 s telegraph is the counterplay window.
//
// Sparse, keyed on global cell coords, pure value data (side + the TerrainFrame it was
// seeded on), so a dictionary copy is a full deep copy — snapshotted with the terrain
// side-tables and folded into Simulation.Checksum. The phase is derived from the seed
// frame, never stored, so nothing here mutates per tick.
//
// Cleared on every path that empties a cell (ChunkMap.BreakCell — which HarvestCell
// also goes through — and EditorSetCell), exactly like TileSpikes. No refund when the
// block under a seed breaks: the spike was paid for and lost, the same as a grown spike
// whose block breaks (and ChunkMap has no meter to refund to).
public class TileSpikeSeeds
{
    public readonly struct Seed
    {
        public readonly TileSpikes.Side Side;
        public readonly int             SeededFrame;   // ChunkMap.TerrainFrame at seeding
        public Seed(TileSpikes.Side side, int seededFrame) { Side = side; SeededFrame = seededFrame; }
    }

    public static CrystalLifecycle Lifecycle => CrystalLifecycle.SpikePlacement;

    private readonly Dictionary<(int gtx, int gty), Seed> _cells = new();
    private readonly List<(int gtx, int gty)> _due = new();

    // Seed a cell. Returns false (and changes nothing) if it is already seeded.
    public bool Set(int gtx, int gty, TileSpikes.Side side, int frame)
        => _cells.TryAdd((gtx, gty), new Seed(side, frame));

    public bool Has(int gtx, int gty) => _cells.ContainsKey((gtx, gty));

    public bool TryGet(int gtx, int gty, out Seed seed) => _cells.TryGetValue((gtx, gty), out seed);

    public void Clear(int gtx, int gty) => _cells.Remove((gtx, gty));

    public int Count => _cells.Count;

    // Phase of a seed at terrain frame `now` (render, tests). A seed still in the store
    // is Seed or Flash; Grown is reported only for the instant before promotion.
    public static CrystalPhase PhaseOf(in Seed seed, int now) => Lifecycle.PhaseAt(now - seed.SeededFrame);

    // Order unspecified; consumers must be order-independent (checksum XOR, renderer).
    public IEnumerable<KeyValuePair<(int gtx, int gty), Seed>> Entries => _cells;

    // Promote every seed whose lifecycle reached Grown at frame `now` into `spikes`.
    // Promotions of different cells commute, so dictionary order (which differs between
    // a live store and a restored copy) cannot leak into the result.
    public void Tick(int now, TileSpikes spikes)
    {
        if (_cells.Count == 0) return;
        _due.Clear();
        foreach (var (cell, seed) in _cells)
            if (PhaseOf(seed, now) >= CrystalPhase.Grown) _due.Add(cell);
        foreach (var cell in _due)
        {
            var seed = _cells[cell];
            _cells.Remove(cell);
            spikes.Set(cell.gtx, cell.gty, seed.Side);   // no-op if somehow already spiked
        }
    }

    public Dictionary<(int gtx, int gty), Seed> Capture() => new(_cells);

    public void Restore(Dictionary<(int gtx, int gty), Seed> src)
    {
        _cells.Clear();
        if (src == null) return;
        foreach (var kv in src) _cells[kv.Key] = kv.Value;
    }
}
