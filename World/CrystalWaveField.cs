using System.Collections.Generic;

namespace MTile;

// The crystal wave's sim state (todo.txt #7; dials and the overview in CrystalWave.cs).
// Three sparse, value-typed tables, snapshotted with the terrain and folded into
// Simulation.Checksum:
//
//   Waves  — which MassBalls are crystal waves, and when each last touched the field.
//            TileMassField asks IsWave to route a wave's commits into crystal requests.
//   Cells  — one Crystal per requested cell. A crystal is PENDING (requested, invisible,
//            harmless) until it is seeded, then runs a CrystalLifecycle from its seed
//            frame: Root (the long warning) or Follow (the fast front). The phase is
//            derived from (TerrainFrame − SeededFrame), never stored, exactly like
//            TileSpikeSeeds — so nothing here mutates per tick except at seeding and
//            pruning. Gone / broken crystals linger as residue (CrystalWave.ResidueFrames).
//   Tally  — HP each wave has taken off each victim, for the per-wave cap.
//
// Crystal cells are EMPTY in the tile grid: a crystal is not terrain, can't be stood on,
// never sprouts, and leaves nothing behind when it vanishes. That is also why the wave
// cannot carry a rider — TerrainCarriedState rides Growing sprouts, and there are none.
// Anything that claims the cell for real terrain (a tile request, a sprout becoming
// visible) evicts the crystal (ChunkMap.EvictCrystal): a wall raised across a pending
// wave cuts it.
//
// Determinism: Tick collects every decision against the pre-tick state, then applies, so
// dictionary order (which differs between a live table and a restored copy) can't leak.
public sealed class CrystalWaveField
{
    public struct WaveRec
    {
        public TileSpikes.Side Side;
        public int             LastTouch;   // TerrainFrame
    }

    public struct Crystal
    {
        public EntityId        Wave;
        public TileSpikes.Side Side;        // launcher's side: immune, like spikes
        public SproutFaces     Base;        // face of this cell the shard stands on
        public int             RequestFrame;
        public int             SeededFrame; // -1 while pending
        public int             BrokenFrame; // -1 unless it broke on a victim
        public bool            Root;        // runs CrystalWave.Root (the warning) vs Follow

        public readonly bool Pending => SeededFrame < 0;
        public readonly bool Broken  => BrokenFrame >= 0;
        public readonly CrystalLifecycle Lifecycle => Root ? CrystalWave.Root : CrystalWave.Follow;

        // Phase at terrain frame `now`. Pending reports Seed (callers check Pending first);
        // a broken crystal is Gone.
        public readonly CrystalPhase PhaseAt(int now)
        {
            if (Broken) return CrystalPhase.Gone;
            if (Pending) return CrystalPhase.Seed;
            return Lifecycle.PhaseAt(now - SeededFrame);
        }

        // Frame the crystal stopped existing (vanished or broke); int.MaxValue if live.
        public readonly int GoneFrame
        {
            get
            {
                if (Broken) return BrokenFrame;
                if (Pending) return int.MaxValue;
                var l = Lifecycle;
                return SeededFrame + l.FramesToGrown + l.GrownFrames;
            }
        }
    }

    public struct TallyRec
    {
        public float Dealt;
        public int   LastHitFrame;
    }

    private readonly Dictionary<EntityId, WaveRec> _waves = new();
    private readonly Dictionary<(int gtx, int gty), Crystal> _cells = new();
    private readonly Dictionary<(EntityId wave, EntityId victim), TallyRec> _tally = new();

    private readonly List<((int gtx, int gty) cell, SproutFaces baseFace)> _seedScratch = new();
    private readonly List<(int gtx, int gty)> _dropScratch = new();
    private readonly List<EntityId> _waveScratch = new();
    private readonly List<(EntityId, EntityId)> _tallyScratch = new();

    public int Count => _cells.Count;
    public IEnumerable<KeyValuePair<(int gtx, int gty), Crystal>> Entries => _cells;
    public IEnumerable<KeyValuePair<EntityId, WaveRec>> WaveEntries => _waves;
    public IEnumerable<KeyValuePair<(EntityId wave, EntityId victim), TallyRec>> TallyEntries => _tally;

    // ── Waves ───────────────────────────────────────────────────────────────────
    // Register/refresh a crystal wave. Called by its MassBall every frame before it
    // deposits, so the very first deposit already routes into crystals.
    public void TouchWave(EntityId wave, TileSpikes.Side side, int now)
    {
        if (wave.IsNone) return;
        _waves[wave] = new WaveRec { Side = side, LastTouch = now };
    }

    public bool IsWave(EntityId wave) => !wave.IsNone && _waves.ContainsKey(wave);

    // ── Cells ───────────────────────────────────────────────────────────────────
    public bool Has(int gtx, int gty) => _cells.ContainsKey((gtx, gty));
    public bool TryGet(int gtx, int gty, out Crystal c) => _cells.TryGetValue((gtx, gty), out c);
    public void Remove(int gtx, int gty) => _cells.Remove((gtx, gty));

    // Live (seeded, not yet gone) crystals — the visible ones.
    public bool IsVisible(int gtx, int gty, int now)
        => _cells.TryGetValue((gtx, gty), out var c) && !c.Pending && c.PhaseAt(now) != CrystalPhase.Gone;

    // A crystal request from the mass field (via ChunkMap.TryRequestCrystal, which has
    // already checked the cell is empty terrain with no sprout). Mirrors TryRequestTile's
    // accept/reject: support is any solid 4-neighbour (floor, wall or ceiling — the same
    // rule as a tile, so the wave takes the shape an ordinary avalanche would) or any
    // crystal of the same wave beside it; with neither the request fails and the mass
    // spills on. A crystal touching solid grows out of it (SolidBase: floor first).
    //
    // Classification of an accepted request:
    //   no same-wave crystal around          → ROOT, seeded now: the long warning. The
    //                                          wave's first crystal is always one; so is
    //                                          a patch the ball laid down disconnected.
    //   a same-wave neighbour already grown   → seeded now as a Follow: the front has
    //   (or past it)                            already passed here, so continue it.
    //   only pending / not-yet-grown ones     → PENDING: seeded by the front (Tick).
    public bool TryRequest(ChunkMap chunks, int gtx, int gty, EntityId wave, int now)
    {
        if (!_waves.TryGetValue(wave, out var rec)) return false;
        if (_cells.ContainsKey((gtx, gty))) return false;

        var solid = SolidBase(chunks, gtx, gty);
        bool anyCrystal = false;
        SproutFaces passedFace = SproutFaces.None;
        foreach (var face in TileSproutNode.FaceOrder)
        {
            var o = TileSproutNode.FaceOffset(face);
            if (!_cells.TryGetValue((gtx + o.X, gty + o.Y), out var n) || n.Wave != wave) continue;
            anyCrystal = true;
            if (passedFace == SproutFaces.None && !n.Pending && n.PhaseAt(now) >= CrystalPhase.Grown)
                passedFace = face;
        }
        if (solid == SproutFaces.None && !anyCrystal) return false;

        var c = new Crystal
        {
            Wave = wave, Side = rec.Side, RequestFrame = now,
            SeededFrame = -1, BrokenFrame = -1,
        };
        if (!anyCrystal)
        {
            c.Root = true;
            c.SeededFrame = now;
            c.Base = solid;
        }
        else if (passedFace != SproutFaces.None)
        {
            c.SeededFrame = now;
            c.Base = solid != SproutFaces.None ? solid : passedFace;
        }
        _cells[(gtx, gty)] = c;
        return true;
    }

    // The face a crystal grows out of when it touches terrain: the first solid
    // 4-neighbour in FaceOrder (floor, then walls, then ceiling), else None.
    private static SproutFaces SolidBase(ChunkMap chunks, int gtx, int gty)
    {
        foreach (var face in TileSproutNode.FaceOrder)
        {
            var o = TileSproutNode.FaceOffset(face);
            if (chunks.GetCellState(gtx + o.X, gty + o.Y) == TileState.Solid) return face;
        }
        return SproutFaces.None;
    }

    // A grown crystal landed on a victim: it shatters (stays as residue).
    public void Break(int gtx, int gty, int now)
    {
        if (!_cells.TryGetValue((gtx, gty), out var c) || c.Broken) return;
        c.BrokenFrame = now;
        _cells[(gtx, gty)] = c;
    }

    // ── Tally (per-wave damage cap) ────────────────────────────────────────────
    public TallyRec TallyFor(EntityId wave, EntityId victim)
        => _tally.TryGetValue((wave, victim), out var t) ? t : new TallyRec { LastHitFrame = int.MinValue / 2 };

    public void AddTally(EntityId wave, EntityId victim, float damage, int now)
    {
        var t = TallyFor(wave, victim);
        t.Dealt += damage;
        t.LastHitFrame = now;
        _tally[(wave, victim)] = t;
    }

    // ── Per-frame advance (ChunkMap.TickSprouts) ────────────────────────────────
    // Seed every pending crystal beside a Grown crystal of its wave (the substrate seeds
    // the next one), drop residue and stale requests, prune idle waves and old tallies.
    public void Tick(ChunkMap chunks, int now)
    {
        if (_cells.Count > 0)
        {
            _seedScratch.Clear();
            _dropScratch.Clear();
            foreach (var (cell, c) in _cells)
            {
                if (c.Pending)
                {
                    if (now - c.RequestFrame > CrystalWave.StalePendingFrames) { _dropScratch.Add(cell); continue; }
                    foreach (var face in TileSproutNode.FaceOrder)
                    {
                        var o = TileSproutNode.FaceOffset(face);
                        if (!_cells.TryGetValue((cell.gtx + o.X, cell.gty + o.Y), out var n)) continue;
                        if (n.Wave != c.Wave || n.Pending || n.PhaseAt(now) != CrystalPhase.Grown) continue;
                        var solid = SolidBase(chunks, cell.gtx, cell.gty);
                        _seedScratch.Add((cell, solid != SproutFaces.None ? solid : face));
                        break;
                    }
                }
                else if (now - c.GoneFrame >= CrystalWave.ResidueFrames)
                    _dropScratch.Add(cell);
            }
            foreach (var (cell, baseFace) in _seedScratch)
            {
                var c = _cells[cell];
                c.SeededFrame = now;
                c.Base = baseFace;
                _cells[cell] = c;
            }
            foreach (var cell in _dropScratch) _cells.Remove(cell);
        }

        if (_waves.Count > 0)
        {
            _waveScratch.Clear();
            foreach (var (id, w) in _waves)
                if (now - w.LastTouch > CrystalWave.WaveIdleFrames) _waveScratch.Add(id);
            foreach (var id in _waveScratch) _waves.Remove(id);
        }
        if (_tally.Count > 0)
        {
            _tallyScratch.Clear();
            foreach (var (key, t) in _tally)
                if (now - t.LastHitFrame > CrystalWave.WaveIdleFrames) _tallyScratch.Add(key);
            foreach (var key in _tallyScratch) _tally.Remove(key);
        }
    }

    // ── Snapshot / restore — dict copies are deep copies (value-typed entries) ─────
    public sealed class Data
    {
        public Dictionary<EntityId, WaveRec> Waves;
        public Dictionary<(int gtx, int gty), Crystal> Cells;
        public Dictionary<(EntityId wave, EntityId victim), TallyRec> Tally;
    }

    public Data Capture() => new()
    {
        Waves = new(_waves),
        Cells = new(_cells),
        Tally = new(_tally),
    };

    public void Restore(Data d)
    {
        _waves.Clear(); _cells.Clear(); _tally.Clear();
        if (d == null) return;
        foreach (var kv in d.Waves) _waves[kv.Key] = kv.Value;
        foreach (var kv in d.Cells) _cells[kv.Key] = kv.Value;
        foreach (var kv in d.Tally) _tally[kv.Key] = kv.Value;
    }
}
