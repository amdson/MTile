using System;
using System.Collections.Generic;

namespace MTile;

// Thorns (Plans/PLANT_SEEDS_PLAN.md §4.3): a planted patch that grows ordinary spike
// tiles. A patch is a SEED cell — itself a spike tile, which must be rooted against a
// DIRT face — plus the offshoot spike tiles it has grown since. Every SpreadIntervalFrames
// a live patch asks ChunkMap.TryRequestSpike for ONE more spike on an open cell beside
// one of its cells that touches solid (the ordinary spike rule: any material), nearest
// the seed first. Thorn cells are spike tiles in every other respect: Tile.Team, the
// TileSpikeSeeds arming telegraph, SpikeContactSystem contact damage, knocked off by any
// hit, dropped when their last support breaks.
//
// What this table adds over a plain spike:
//   • spreading, under caps (per patch, per team) — Tick;
//   • reinforcement — a Solid block with a thorn cell on any face takes
//     ReinforceDamageScale of every tile hit (ChunkMap.DamageCell). Hand-placed spikes
//     are not in this table, so they neither spread nor reinforce.
//
// A patch whose seed cell is cleared stops spreading for good; its offshoots stay until
// they are cleared themselves, and the patch record is dropped with its last cell.
//
// Cleanup is driven by ChunkMap.WriteTile: whatever replaces a spike tile (knock-off,
// support break, a block built over it, an editor write) calls OnSpikeCleared, so no
// path can leave a dangling entry. Rollback restores this table by value, alongside the
// journal rewind that restores the tiles.
//
// Determinism: patches live in a List kept in id order (ids are monotonic and
// snapshotted), and every choice made from the cell dictionary is a min over a total
// key, so dictionary bucket order can never leak into the sim. No statics mutate.
public sealed class ThornPatches
{
    // ── Tuning (plan §4.3 "start" numbers — starting points, tune by feel) ─────
    // Frames between growth attempts of one patch (~1.5 s at 60 fps).
    public const int   SpreadIntervalFrames = 90;
    // Cells one patch may hold, seed included.
    public const int   MaxCellsPerPatch     = 10;
    // Furthest a patch cell may be from its seed, in tiles (Manhattan: the distance
    // along the surface around a corner, not through it).
    public const int   MaxRadius            = 4;
    // Thorn cells one team may have alive across all its patches.
    public const int   MaxCellsPerTeam      = 24;
    // Live (still spreading) patches one team may have at once.
    public const int   MaxPatchesPerTeam    = 2;
    // Tile damage multiplier for a Solid block with a thorn on any face.
    public const float ReinforceDamageScale = 0.5f;
    // Planting price, in Stone placements (cf. MaterialStrengths.SpikeCostInStone = 2):
    // a patch grows up to MaxCellsPerPatch spikes, so it costs more than one.
    public const float BuildCostInStone     = 3f;
    public static float BuildCost => BuildCostInStone * MaterialStrengths.BuildCostFor(TileType.Stone);

    public struct Patch
    {
        public int  Id;
        public byte Team;
        public int  SeedGtx, SeedGty;
        public int  PlantedFrame;     // TerrainFrame of planting
        public int  NextGrowFrame;    // TerrainFrame of the next growth attempt
        public bool Spreading;        // false once the seed cell is gone
        public int  CellCount;        // live cells of this patch (seed included)
    }

    public sealed class Data
    {
        public Patch[] Patches;
        public Dictionary<(int gtx, int gty), int> Cells;
        public int NextId;
    }

    private readonly List<Patch> _patches = new();                         // ascending Id
    private readonly Dictionary<(int gtx, int gty), int> _cells = new();   // cell → patch id
    private int _nextId = 1;

    public int CellCount  => _cells.Count;
    public int PatchCount => _patches.Count;
    public IReadOnlyList<Patch> Patches => _patches;
    // Order unspecified; consumers must be order-independent (checksum XOR, renderer).
    public IEnumerable<KeyValuePair<(int gtx, int gty), int>> CellEntries => _cells;

    public bool IsThornCell(int gtx, int gty) => _cells.ContainsKey((gtx, gty));

    public bool TryGetCell(int gtx, int gty, out Patch patch)
    {
        patch = default;
        if (!_cells.TryGetValue((gtx, gty), out int id)) return false;
        int i = IndexOf(id);
        if (i < 0) return false;
        patch = _patches[i];
        return true;
    }

    public bool IsSeedCell(int gtx, int gty)
        => TryGetCell(gtx, gty, out var p) && p.SeedGtx == gtx && p.SeedGty == gty;

    // Live (spreading) patches of a team — what the patch cap counts.
    public int LivePatchesOf(byte team)
    {
        int n = 0;
        foreach (var p in _patches) if (p.Team == team && p.Spreading) n++;
        return n;
    }

    // Thorn cells of a team across all its patches, dead ones' offshoots included.
    public int CellsOf(byte team)
    {
        int n = 0;
        foreach (var p in _patches) if (p.Team == team) n += p.CellCount;
        return n;
    }

    // ── Planting ────────────────────────────────────────────────────────────────

    // Pure: would TryPlant succeed? Same checks, no side effects (cursor ghost).
    public bool CanPlant(ChunkMap chunks, int gtx, int gty, byte team)
    {
        if (chunks.GetCellState(gtx, gty) != TileState.Empty) return false;
        if (chunks.Graph.TryGet(gtx, gty, out _)) return false;
        if (!HasDirtFace(chunks, gtx, gty)) return false;
        if (LivePatchesOf(team) >= MaxPatchesPerTeam) return false;
        if (CellsOf(team) >= MaxCellsPerTeam) return false;
        return true;
    }

    // Plant a patch: the seed cell becomes a spike tile of `team` (through the ordinary
    // spike request, so the journal, hash and arming seed are the spike path's) and the
    // patch is recorded. False, with nothing changed, when illegal.
    public bool TryPlant(ChunkMap chunks, int gtx, int gty, byte team, int frame)
    {
        if (!CanPlant(chunks, gtx, gty, team)) return false;
        if (!chunks.TryRequestSpike(gtx, gty, team)) return false;
        var p = new Patch
        {
            Id = _nextId++, Team = team, SeedGtx = gtx, SeedGty = gty,
            PlantedFrame = frame, NextGrowFrame = frame + SpreadIntervalFrames,
            Spreading = true, CellCount = 1,
        };
        _patches.Add(p);
        _cells[(gtx, gty)] = p.Id;
        return true;
    }

    // ── Spreading ───────────────────────────────────────────────────────────────

    // Called once per TickSprouts. Each due patch makes one growth attempt, then waits
    // a full interval whatever the outcome (a capped or boxed-in patch retries later).
    public void Tick(ChunkMap chunks, int now)
    {
        if (_patches.Count == 0) return;
        for (int i = 0; i < _patches.Count; i++)
        {
            var p = _patches[i];
            if (!p.Spreading || now < p.NextGrowFrame) continue;
            p.NextGrowFrame = now + SpreadIntervalFrames;
            _patches[i] = p;
            if (p.CellCount >= MaxCellsPerPatch) continue;
            if (CellsOf(p.Team) >= MaxCellsPerTeam) continue;
            if (!PickGrowthCell(chunks, p, out int gx, out int gy)) continue;
            if (!chunks.TryRequestSpike(gx, gy, p.Team)) continue;
            _cells[(gx, gy)] = p.Id;
            p.CellCount++;
            _patches[i] = p;
        }
    }

    private static readonly (int dx, int dy)[] Neighbours = { (0, 1), (-1, 0), (1, 0), (0, -1) };

    // The open cell beside one of the patch's cells that touches solid, within MaxRadius
    // of the seed, minimising (Manhattan distance, squared distance, gty, gtx) from the
    // seed — a total order, so the pick is independent of dictionary iteration order.
    private bool PickGrowthCell(ChunkMap chunks, in Patch p, out int bestX, out int bestY)
    {
        bestX = bestY = 0;
        bool found = false;
        long bestKey = long.MaxValue;
        foreach (var (cell, id) in _cells)
        {
            if (id != p.Id) continue;
            foreach (var (dx, dy) in Neighbours)
            {
                int cx = cell.gtx + dx, cy = cell.gty + dy;
                int ox = cx - p.SeedGtx, oy = cy - p.SeedGty;
                int man = Math.Abs(ox) + Math.Abs(oy);
                if (man > MaxRadius) continue;
                if (chunks.GetCellState(cx, cy) != TileState.Empty) continue;
                if (chunks.Graph.TryGet(cx, cy, out _)) continue;
                if (!TouchesSolid(chunks, cx, cy)) continue;
                // Pack the key: each component is small and non-negative after offsetting.
                long key = ((long)man << 48) | ((long)(ox * ox + oy * oy) << 32)
                         | ((long)(oy + 512) << 16) | (long)(ox + 512);
                if (key < bestKey) { bestKey = key; bestX = cx; bestY = cy; found = true; }
            }
        }
        return found;
    }

    private static bool TouchesSolid(ChunkMap chunks, int gtx, int gty)
    {
        foreach (var (dx, dy) in Neighbours)
            if (chunks.GetCellState(gtx + dx, gty + dy) == TileState.Solid) return true;
        return false;
    }

    // Plants root in dirt only (plan §2.10): the seed needs a Solid Dirt 4-neighbour.
    public static bool HasDirtFace(ChunkMap chunks, int gtx, int gty)
    {
        foreach (var (dx, dy) in Neighbours)
            if (chunks.GetCellState(gtx + dx, gty + dy) == TileState.Solid
                && chunks.GetCellType(gtx + dx, gty + dy) == TileType.Dirt) return true;
        return false;
    }

    // ── Reinforcement ───────────────────────────────────────────────────────────

    // Is this (Solid) cell reinforced — a thorn cell on any of its four faces?
    public bool Reinforces(int gtx, int gty)
    {
        if (_cells.Count == 0) return false;
        foreach (var (dx, dy) in Neighbours)
            if (_cells.ContainsKey((gtx + dx, gty + dy))) return true;
        return false;
    }

    // ── Cleanup ─────────────────────────────────────────────────────────────────

    // A spike tile at (gtx, gty) is being replaced (ChunkMap.WriteTile). Forget it; a
    // lost seed stops its patch spreading; a patch with no cells left is dropped.
    public void OnSpikeCleared(int gtx, int gty)
    {
        if (!_cells.Remove((gtx, gty), out int id)) return;
        int i = IndexOf(id);
        if (i < 0) return;
        var p = _patches[i];
        p.CellCount--;
        if (p.SeedGtx == gtx && p.SeedGty == gty) p.Spreading = false;
        if (p.CellCount <= 0) _patches.RemoveAt(i);
        else _patches[i] = p;
    }

    private int IndexOf(int id)
    {
        // Ids ascend through the list; patches are few (≤ MaxPatchesPerTeam live per team).
        for (int i = 0; i < _patches.Count; i++) if (_patches[i].Id == id) return i;
        return -1;
    }

    // ── Snapshot / restore ──────────────────────────────────────────────────────
    // Value data throughout (Patch is a struct), so array/dictionary copies are deep.
    public Data Capture() => new()
    {
        Patches = _patches.ToArray(),
        Cells   = new Dictionary<(int gtx, int gty), int>(_cells),
        NextId  = _nextId,
    };

    public void Restore(Data src)
    {
        _patches.Clear();
        _cells.Clear();
        _nextId = 1;
        if (src == null) return;
        if (src.Patches != null) _patches.AddRange(src.Patches);
        if (src.Cells != null) foreach (var kv in src.Cells) _cells[kv.Key] = kv.Value;
        _nextId = src.NextId;
    }
}
