using System;
using System.Collections.Generic;

namespace MTile;

// The block-eater plant (Plans/PLANT_SEEDS_PLAN.md §4.5): a sparse per-cell "infection"
// table. An eater is planted on a Solid block owned (BlockOwnership) by a team OTHER than
// the planter's; it chews the cell through ChunkMap.DamageCell — so OnTileBroken, the
// journal, charged blasts and any damage reinforcement behave as for every other damage
// source — and on a cadence spreads to 4-neighbour Solid cells owned by another team.
// Unowned (natural / faded) terrain and the eater's own team's blocks are firebreaks, so a
// one-cell gap stops it. Hardened is never infected. It is the one plant that does not
// need dirt: any placeable material is food.
//
// Answers: break the infected blocks yourself (BreakCell clears the entry, so the patch
// loses that cell and everything it would have spread to from it), or cut a gap.
//
// Ownership lapse rule: an infection lives only on a cell that is STILL owned by an enemy
// of the eater. If the cell's claim fades below BlockOwnership.MinStrength (or the cell
// somehow ends up owned by the eater's own team), the infection dies out on that cell at
// the next tick — the damage already dealt stays, nothing else happens. In practice the
// fade (~3.3 min) is far slower than the eat times, so this only bites on old forts.
//
// Sim state: pure value entries keyed on global cell coords, snapshotted with the other
// terrain side-tables and folded into Simulation.Checksum. Iteration that has effects
// (spread, damage) runs over a SORTED copy of the keys, so dictionary bucket order can
// never leak into the outcome. Nothing here is static and mutable.
public sealed class BlockEaterInfection
{
    // ── Tuning: all STARTING POINTS, to be tuned by feel ──────────────────────────
    // Seconds for an infection to eat an untouched cell of each material (converted to a
    // per-frame damage of MaxHP / (seconds × 60), so it tracks material_strengths.json).
    public const float EatSecondsDirt  = 1.5f;
    public const float EatSecondsStone = 6.0f;
    public const float EatSecondsSand  = 1.0f;
    public const float EatSecondsFoam  = 0.75f;
    // Frames between spread attempts of one infected cell (age multiples of this).
    public const int   SpreadIntervalFrames = 60;
    // Simultaneously live plantings ("patches") per team. A patch is live while any cell
    // it infected is still infected; its spread is not capped by count.
    public const int   MaxLivePatchesPerTeam = 1;
    // Build-meter price of planting one (meter units, same scale as
    // MaterialStrengths.BuildCost: stone 1.94, dirt 0.77). The planting hook charges it.
    public const float BuildCost = 3.0f;

    // Where an infected cell's infection came from (render reads it to grow the vines in
    // from that edge; it is also part of the checksummed state).
    public const byte FromHeart = 0, FromBelow = 1, FromLeft = 2, FromRight = 3, FromAbove = 4;

    public struct Cell
    {
        public byte Team;           // BlockOwnership owner id of the eater's planter (1/2)
        public int  InfectedFrame;  // TerrainFrame this cell was infected
        public int  HeartGtx;       // the patch's planted cell — patch identity together
        public int  HeartGty;       //   with PlantFrame
        public int  PlantFrame;
        public byte From;           // FromHeart for the planted cell, else the neighbour side

        public readonly bool IsHeart(int gtx, int gty) => From == FromHeart && gtx == HeartGtx && gty == HeartGty;
    }

    private readonly Dictionary<(int gtx, int gty), Cell> _cells = new();
    private readonly List<(int gtx, int gty)> _order = new();
    private readonly List<(int gtx, int gty, int hgx, int hgy, int pf)> _patchScratch = new();

    // Neighbour order for spread: below, left, right, above (fixed, deterministic).
    private static readonly (int dx, int dy, byte from)[] Neighbours =
    {
        (0, 1, FromAbove), (-1, 0, FromRight), (1, 0, FromLeft), (0, -1, FromBelow),
    };

    public int Count => _cells.Count;
    public bool Has(int gtx, int gty) => _cells.ContainsKey((gtx, gty));
    public bool TryGet(int gtx, int gty, out Cell cell) => _cells.TryGetValue((gtx, gty), out cell);
    public void Clear(int gtx, int gty) => _cells.Remove((gtx, gty));

    // Order unspecified; consumers must be order-independent (checksum XOR, renderer).
    public IEnumerable<KeyValuePair<(int gtx, int gty), Cell>> Entries => _cells;

    public static float EatSecondsFor(TileType type) => type switch
    {
        TileType.Dirt  => EatSecondsDirt,
        TileType.Sand  => EatSecondsSand,
        TileType.Foam  => EatSecondsFoam,
        _              => EatSecondsStone,
    };

    // Per-frame tile damage an infection deals to a cell of `type`. The tiny overshoot
    // makes float accumulation land the break on exactly the intended frame count.
    public static float DamagePerFrame(TileType type)
        => TileDamage.MaxHPFor(type) / (EatSecondsFor(type) * 60f) * 1.0001f;

    // Food test shared by planting and spreading: a Solid, non-Hardened cell whose live
    // ownership claim belongs to a team other than `team`, not already infected.
    public bool IsFood(ChunkMap map, int gtx, int gty, byte team)
    {
        if (team == BlockOwnership.None) return false;
        if (map.GetCellState(gtx, gty) != TileState.Solid) return false;
        if (!TileTypes.IsPlaceable(map.GetCellType(gtx, gty))) return false;   // Hardened
        if (_cells.ContainsKey((gtx, gty))) return false;
        return map.Ownership.TryGet(gtx, gty, map.TerrainFrame, out byte owner, out _)
            && owner != team;
    }

    // Number of distinct live patches started by `team`.
    public int LivePatches(byte team)
    {
        _patchScratch.Clear();
        foreach (var (k, c) in _cells)
        {
            if (c.Team != team) continue;
            bool seen = false;
            foreach (var p in _patchScratch)
                if (p.hgx == c.HeartGtx && p.hgy == c.HeartGty && p.pf == c.PlantFrame) { seen = true; break; }
            if (!seen) _patchScratch.Add((k.gtx, k.gty, c.HeartGtx, c.HeartGty, c.PlantFrame));
        }
        return _patchScratch.Count;
    }

    // Pure legality query (cursor ghost): would TryPlant succeed right now?
    public bool CanPlant(ChunkMap map, int gtx, int gty, byte team)
        => IsFood(map, gtx, gty, team) && LivePatches(team) < MaxLivePatchesPerTeam;

    // Plant an eater at (gtx, gty) for `team` (a BlockOwnership owner id). False, with no
    // side effects, when illegal. Does not charge the build meter — the caller does.
    public bool TryPlant(ChunkMap map, int gtx, int gty, byte team)
    {
        if (!CanPlant(map, gtx, gty, team)) return false;
        int now = map.TerrainFrame;
        _cells[(gtx, gty)] = new Cell
        {
            Team = team, InfectedFrame = now, HeartGtx = gtx, HeartGty = gty,
            PlantFrame = now, From = FromHeart,
        };
        return true;
    }

    // One terrain tick (called from ChunkMap.TickSprouts after TerrainFrame advanced).
    //   1. drop entries whose cell is no longer edible ground (not Solid — a safety net
    //      for any emptying path that forgot to clear — or its claim lapsed / turned);
    //   2. spread: every cell whose age is a positive multiple of SpreadIntervalFrames
    //      infects its food neighbours (new entries start next tick);
    //   3. damage: every surviving pre-existing entry takes DamagePerFrame via DamageCell,
    //      which may break the cell — BreakCell then clears its entry.
    // All three passes walk the keys sorted by (gtx, gty).
    public void Tick(ChunkMap map, int now)
    {
        if (_cells.Count == 0) return;
        _order.Clear();
        foreach (var k in _cells.Keys) _order.Add(k);
        _order.Sort(static (a, b) => a.gtx != b.gtx ? a.gtx.CompareTo(b.gtx) : a.gty.CompareTo(b.gty));

        // 1. Lapse / safety net.
        foreach (var k in _order)
        {
            var c = _cells[k];
            bool alive = map.GetCellState(k.gtx, k.gty) == TileState.Solid
                      && map.Ownership.TryGet(k.gtx, k.gty, now, out byte owner, out _)
                      && owner != c.Team;
            if (!alive) _cells.Remove(k);
        }

        // 2. Spread.
        foreach (var k in _order)
        {
            if (!_cells.TryGetValue(k, out var c)) continue;
            int age = now - c.InfectedFrame;
            if (age <= 0 || age % SpreadIntervalFrames != 0) continue;
            foreach (var (dx, dy, from) in Neighbours)
            {
                int nx = k.gtx + dx, ny = k.gty + dy;
                if (!IsFood(map, nx, ny, c.Team)) continue;
                _cells[(nx, ny)] = new Cell
                {
                    Team = c.Team, InfectedFrame = now, HeartGtx = c.HeartGtx, HeartGty = c.HeartGty,
                    PlantFrame = c.PlantFrame, From = from,
                };
            }
        }

        // 3. Damage (only cells that existed at the top of the tick).
        foreach (var k in _order)
        {
            if (!_cells.TryGetValue(k, out var c) || c.InfectedFrame == now) continue;
            map.DamageCell(k.gtx, k.gty, DamagePerFrame(map.GetCellType(k.gtx, k.gty)));
        }
    }

    // Snapshot/restore. Value-typed entries, so a dictionary copy is a deep copy.
    public Dictionary<(int gtx, int gty), Cell> Capture() => new(_cells);

    public void Restore(Dictionary<(int gtx, int gty), Cell> src)
    {
        _cells.Clear();
        if (src == null) return;
        foreach (var kv in src) _cells[kv.Key] = kv.Value;
    }
}
