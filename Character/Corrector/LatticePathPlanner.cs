using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Lattice path planner (Plans/LATTICE_PATH_PLANNER.md): the spatial DP that
// generates the fold's reference path. Phase 0/1 built it as a freeze-frame
// oracle (Game1, beside the LM probe and the old state-space LatticePlanner);
// phase 2 wires it as the FoldEngine "lattice" reference generator
// (FoldLattice) in front of FoldReference's rows → deform → servo tail.
//
// The solve, per the plan:
//   - a world-aligned cell grid (TileSize / LatticeCellsPerTile) over the
//     cone's footprint from the seed (§2.1): L along u, ±L·tanθ across;
//   - nodes are candidate BODY-CENTER positions; a node is admissible iff the
//     center lies outside every stamped C-obstacle (§3.1);
//   - edges are primitive lattice offsets filtered by the cone
//     dot(ô, u) ≥ cosθ > 0 — pure geometry, no actuation, no velocity (§3.3);
//     cosθ > 0 is what makes the graph a DAG (projection onto u strictly
//     increases along every edge), so one pass over nodes sorted by p is a
//     valid DP order;
//   - cost: per-edge RISE (px climbed — table-invariant; drops are free),
//     per-node hover toward the surface below (state-supplied on/off flag),
//     seed-edge velocity bias (§3.4–3.5);
//   - goal: the reachable node maximizing progress·w_prog − cost; a bonk is
//     a route that is not worth its cost (§3.4, revised).
//
// Output samples carry the plan's §3.6 support fields: FloorY = the C-space
// surface below the node (Pos.Y + floorBelow), Grounded = floorBelow within
// BallisticPredictor.SupportReach — the same test the coast predictor uses.
// No timing: Vel is left zero; the caller time-parameterizes the polyline.
//
// Determinism: pooled fixed-size arrays, no allocation per solve (after the
// instance is built), deterministic orders (float-key sort; equal keys can
// permute but equal-p nodes never share an edge, so any tie order yields the
// same DP result). No statics beyond immutable tables. Scratch only, fully
// rewritten every solve — never snapshot state.
public sealed class LatticePathPlanner
{
    public const int MaxCells = 4096;   // pooled scratch bound; window clamps to fit
    public const int MaxPath  = 256;    // ≥ any monotone path across a MaxCells window
    private const float LateralTieBreak = 0.05f;   // per px perpendicular to u — an ε, not a knob

    // ── Primitive offset table (§3.3): |dx|,|dy| ≤ Radius, gcd = 1 ───────────
    // Each offset carries the cells its segment crosses (conservative
    // supercover — a segment through a cell corner requires both side cells
    // free, so a body never slips through a checkerboard pinch). Radius 3
    // (decided 2026-08-24): 32 offsets, steepest slope 3 (≈ 72°), 15 forward
    // edges per node under a near-90° cone.
    private const int Radius = 3;
    private struct Offset
    {
        public sbyte Dx, Dy;
        public sbyte[] Cross;              // crossed cells as (x,y) pairs, excluding the ends
        public int    CrossCount;          // number of pairs
        public Vector2 Unit;
        public float  Len;                 // in cells
    }
    private static readonly Offset[] AllOffsets = BuildOffsets();

    private static Offset[] BuildOffsets()
    {
        var list = new System.Collections.Generic.List<Offset>();
        for (int dx = 0; dx <= Radius; dx++)
        for (int dy = 0; dy <= Radius; dy++)
        {
            if (dx == 0 && dy == 0) continue;
            if (Gcd(dx, dy) != 1) continue;            // primitive only
            var cross = Supercover(dx, dy);
            foreach (int sx in dx == 0 ? new[] { 1 } : new[] { 1, -1 })
            foreach (int sy in dy == 0 ? new[] { 1 } : new[] { 1, -1 })
            {
                var o = new Offset { Dx = (sbyte)(dx * sx), Dy = (sbyte)(dy * sy) };
                o.CrossCount = cross.Count;
                o.Cross = new sbyte[2 * cross.Count];
                for (int i = 0; i < cross.Count; i++)
                {
                    o.Cross[2 * i]     = (sbyte)(cross[i].x * sx);
                    o.Cross[2 * i + 1] = (sbyte)(cross[i].y * sy);
                }
                o.Len  = MathF.Sqrt(o.Dx * o.Dx + o.Dy * o.Dy);
                o.Unit = new Vector2(o.Dx / o.Len, o.Dy / o.Len);
                list.Add(o);
            }
        }
        return list.ToArray();
    }

    private static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }

    // Cells the center-to-center segment (0,0) → (dx,dy) passes through,
    // excluding both ends, for dx,dy ≥ 0 with gcd 1. Exact: the segment
    // crosses the x boundary before cell i at t = (2i−1)/(2dx) and the y
    // boundary before cell j at t = (2j−1)/(2dy); compare as integers. A
    // simultaneous crossing is a corner — both side cells are added
    // (conservative), then the diagonal cell.
    private static System.Collections.Generic.List<(int x, int y)> Supercover(int dx, int dy)
    {
        var cells = new System.Collections.Generic.List<(int x, int y)>();
        int cx = 0, cy = 0, i = 1, j = 1;
        while (i <= dx || j <= dy)
        {
            long a = i <= dx ? (long)(2 * i - 1) * dy : long.MaxValue;
            long b = j <= dy ? (long)(2 * j - 1) * dx : long.MaxValue;
            if (a < b)      { cx++; i++; }
            else if (b < a) { cy++; j++; }
            else            { cells.Add((cx + 1, cy)); cells.Add((cx, cy + 1)); cx++; cy++; i++; j++; }
            cells.Add((cx, cy));
        }
        cells.RemoveAt(cells.Count - 1);               // the end cell itself
        return cells;
    }

    // ── Pooled per-solve scratch ─────────────────────────────────────────────
    private readonly bool[]  _blocked    = new bool[MaxCells];
    private readonly bool[]  _reachable  = new bool[MaxCells];
    private readonly float[] _floorBelow = new float[MaxCells];
    private readonly float[] _dp         = new float[MaxCells];
    private readonly int[]   _parent     = new int[MaxCells];
    private readonly int[]   _queue      = new int[MaxCells];
    private readonly int[]   _order      = new int[MaxCells];
    private readonly float[] _orderKey   = new float[MaxCells];
    private readonly Offset[] _admitted  = new Offset[AllOffsets.Length];
    private int _admittedCount;

    // Window in cell coords (world-anchored: cell i covers [i·cell, (i+1)·cell)).
    private float _cell;
    private int _x0, _y0, _w, _h;

    // Seed run (§3.5): nodes seed + j·o for j < _runLen may leave only along
    // admitted offset _runOffset (−1 = no run). Seed in window-cell coords.
    private int _runOffset = -1, _runLen, _seedCx, _seedCy;

    // ── Stamp mask cache ─────────────────────────────────────────────────────
    // The grid is world-aligned and the cell divides the tile exactly, so a
    // cell center's offset from a tile center depends only on
    // (cellIndex − tileIndex·perTile): one (2R+1)² boolean mask per
    // (template, perTile, margin) covers every solid tile. Rebuilt only when
    // the key changes (a hot-reload of the cell count / margin).
    private CObstacleTemplate _maskTemplate;
    private int _maskPerTile = -1;
    private float _maskMargin = float.NaN;
    private bool[] _mask = Array.Empty<bool>();
    private int _maskR;          // mask covers k ∈ [−R, R] on each axis
    private int _maskW;          // 2R + 1

    // ── Debug accessors (freeze-frame overlay; §3.8) ─────────────────────────
    public float DebugCell   => _cell;
    public int   DebugWidth  => _w;
    public int   DebugHeight => _h;
    public Vector2 DebugCellCenter(int cx, int cy) => CellCenter(_x0 + cx, _y0 + cy);
    public bool  DebugBlocked(int cx, int cy)   => _blocked[cy * _w + cx];
    public bool  DebugReachable(int cx, int cy) => _reachable[cy * _w + cx];
    // Last solve's stats; the string is built on demand (nothing allocates
    // per solve on the sim path).
    public int   LastReach { get; private set; }
    public bool  LastBonk  { get; private set; }
    public float LastCost  { get; private set; }
    public string LastDebug => $"cells={_w}x{_h} reach={LastReach} bonk={LastBonk} cost={LastCost:F1}";

    private Vector2 CellCenter(int gx, int gy) => new((gx + 0.5f) * _cell, (gy + 0.5f) * _cell);

    // Solve. u = requested direction (unit not required; zero → no solve).
    // hover on/off and riseCost (price per px climbed; drops are free) are
    // the state-supplied parameters (§3.4). Returns the number of
    // path samples written to outPath (body-center positions, seed → goal,
    // FloorY/Grounded filled per §3.6, Vel zero); 0 = no solve (zero u, seed
    // inside an obstacle with no not-behind free neighbour, degenerate window).
    public int Solve(ChunkMap chunks, Polygon body, Vector2 seed, Vector2 vel,
                     Vector2 u, bool hover, float hoverOffset, float riseCost,
                     CoastSample[] outPath, out float cost, out bool bonk)
    {
        cost = 0f; bonk = false;
        LastReach = 0; LastBonk = false; LastCost = 0f;
        var cfg = MovementConfig.Current;
        if (u.LengthSquared() < 1e-8f) return 0;
        u.Normalize();

        float cosTheta = Math.Clamp(cfg.LatticeConeCos, 0.05f, 0.99f);
        int   perTile  = Math.Clamp(cfg.LatticeCellsPerTile, 2, 8);
        _cell = (float)Chunk.TileSize / perTile;
        float L = cfg.LatticeLookaheadTiles * Chunk.TileSize;

        // Admitted offsets under the cone (§3.3). The cone is nearly 90° by
        // default (cosθ 0.05): every forward offset in the table is an edge and
        // steepness is priced by SteepWeight, not filtered. cosθ > 0 stays
        // structural — it is the DAG condition.
        _admittedCount = 0;
        foreach (var o in AllOffsets)
            if (Vector2.Dot(o.Unit, u) >= cosTheta)
                _admitted[_admittedCount++] = o;
        if (_admittedCount == 0) return 0;

        BuildWindow(seed, u, L);
        if (_w <= 0 || _h <= 0) return 0;

        // The obstacle margin IS the tracker's band (half a cell): the DP keeps
        // path nodes this far outside the true C-obstacle, and the tracker
        // lets the body stray this far from the path — one allowance, counted
        // once. (CorrectorMargin, the qp/ref engines' 2 px, had been added on
        // top of the band: two allowances, and a corridor seam narrowed to a
        // single cell — LATTICE_SCENARIOS.md fifth pass.)
        StampObstacles(chunks, body, perTile, 0.5f * _cell);
        SweepFloorBelow();

        int seedX = (int)MathF.Floor(seed.X / _cell), seedY = (int)MathF.Floor(seed.Y / _cell);
        if (seedX < _x0 || seedX >= _x0 + _w || seedY < _y0 || seedY >= _y0 + _h) return 0;
        int seedIdx = (seedY - _y0) * _w + (seedX - _x0);
        if (_blocked[seedIdx])
        {
            // The body sits inside the margin (flush with a floor in a squeeze,
            // pressed to a lip). Snap to the nearest free cell that is NOT
            // behind the body along u — a path may start beside the body but
            // never by pulling it back off an obstacle (that would be a planned
            // brake; the wall bonk stays honest via the caller's fallback).
            seedIdx = SnapSeed(seed, u, seedX, seedY);
            if (seedIdx < 0) { bonk = true; LastBonk = true; return 0; }
            seedX = _x0 + seedIdx % _w; seedY = _y0 + seedIdx / _w;
        }

        // Seed run (§3.5): the body's actual direction of travel is fixed for
        // the first SeedRunPx — the current velocity quantized to the nearest
        // admitted offset — so the path starts where the body is GOING, not
        // where the cost surface would like it to be. Only when the body is
        // moving (≥ SeedRunMinSpeed) and that direction is representable in
        // the cone (dot ≥ 0.85 with the best offset — a vertical fall under a
        // horizontal u is not forced into a 45° diagonal); a blocked run is
        // forced as far as it fits. Everything else falls back to the soft
        // seed bias below, so the seed is never stranded.
        _runOffset = -1; _runLen = 0;
        _seedCx = seedX - _x0; _seedCy = seedY - _y0;
        float runPx = cfg.LatticeSeedRunPx, runMin = cfg.LatticeSeedRunMinSpeed;
        if (runPx > 0f && vel.LengthSquared() >= runMin * runMin)
        {
            var vh = Vector2.Normalize(vel);
            int bestA = -1; float bestDot = 0.85f;
            for (int a = 0; a < _admittedCount; a++)
            {
                float d = Vector2.Dot(_admitted[a].Unit, vh);
                if (d > bestDot) { bestDot = d; bestA = a; }
            }
            if (bestA >= 0)
            {
                ref var o = ref _admitted[bestA];
                int want = Math.Max(1, (int)MathF.Ceiling(runPx / (o.Len * _cell)));
                int cx = _seedCx, cy = _seedCy, k = 0;
                while (k < want && EdgeFree(cx, cy, ref o)) { cx += o.Dx; cy += o.Dy; k++; }
                if (k > 0) { _runOffset = bestA; _runLen = k; }
            }
        }

        int reachCount = Flood(seedIdx);
        // Sort reachable nodes by p = dot(center, u). Equal-p nodes never share
        // an edge (every edge strictly increases p), so sort instability among
        // ties cannot change the DP result.
        for (int i = 0; i < reachCount; i++)
        {
            int idx = _order[i];
            _orderKey[i] = Vector2.Dot(CellCenter(_x0 + idx % _w, _y0 + idx / _w), u);
        }
        Array.Sort(_orderKey, _order, 0, reachCount);

        // ── The DP sweep (§3.4) ──────────────────────────────────────────────
        float wRise = riseCost;                                 // the state's price per px climbed
        var uPerp = new Vector2(-u.Y, u.X);
        // Exact prune from the argmax goal: a node's value is
        // w_prog·(p − p_seed) − dp ≤ w_prog·L − dp, so once dp exceeds
        // w_prog·L the node can never beat the seed (value 0) and nothing
        // reached through it can either — stop relaxing from it. Same
        // result on every solve; the sky above a hover path dies within a
        // few nodes of rise/hover cost instead of being swept to the box.
        float bound = cfg.LatticeProgressWeight * L;
        float wHover = cfg.LatticeHoverWeight, wSeed = cfg.LatticeSeedWeight;
        Vector2 vHat = vel.LengthSquared() > 1f ? Vector2.Normalize(vel) : Vector2.Zero;
        for (int i = 0; i < reachCount; i++) _dp[_order[i]] = float.PositiveInfinity;
        _dp[seedIdx] = 0f; _parent[seedIdx] = -1;

        for (int i = 0; i < reachCount; i++)
        {
            int n = _order[i];
            float dn = _dp[n];
            if (float.IsPositiveInfinity(dn) || dn > bound) continue;
            int nx = n % _w, ny = n / _w;
            bool atSeed = n == seedIdx;
            int forced = ForcedOffset(nx, ny);
            for (int a = 0; a < _admittedCount; a++)
            {
                if (forced >= 0 && a != forced) continue;        // seed run
                ref var o = ref _admitted[a];
                if (!EdgeFree(nx, ny, ref o)) continue;          // blocked / tunneling (§3.3)
                int m = (ny + o.Dy) * _w + (nx + o.Dx);
                // Climb cost = height climbed (px of rise), so a route's price
                // is the geometry's, not the offset table's — a (1,3) edge
                // and three (1,1) edges cost the same 9.6 px. Drops are free:
                // gravity delivers them, and charging them would let the
                // argmax goal refuse to walk off a ledge.
                float c = dn
                    + wRise * MathF.Max(0f, -o.Dy) * _cell
                    // Tie-break on excursion perpendicular to u: with hover off
                    // and rise free every route up costs the same, and the DP
                    // would zigzag a straight-up jump sideways. Negligible
                    // against every priced term (a 48 px drop costs 2.4).
                    + LateralTieBreak * MathF.Abs(o.Dx * uPerp.X + o.Dy * uPerp.Y) * _cell;
                if (hover && !float.IsPositiveInfinity(_floorBelow[m]))
                {
                    float dev = _floorBelow[m] - hoverOffset;
                    // Linear, like the rise cost: the "rise back to hover" vs
                    // "stay sagged" trade then compares w_rise against
                    // w_hover × nodes, independent of how large the sag is (a
                    // quadratic hover against a linear rise crossed at ~7 px —
                    // inside the hover band, measured 2026-08-24).
                    c += wHover * MathF.Abs(dev);
                }
                if (atSeed && vHat != Vector2.Zero)
                    c += wSeed * (1f - Vector2.Dot(o.Unit, vHat));
                if (c > bound) continue;                          // never worth it: prune
                if (c < _dp[m]) { _dp[m] = c; _parent[m] = n; }
            }
        }

        // ── Goal: progress worth its cost (§3.4, revised 2026-08-24) ────────
        // argmax over every reachable node of  w_prog·(p − p_seed) − dp.  The
        // far-band rule ("cheapest node at p ≥ p_seed + L, else the furthest")
        // is this rule's w_prog → ∞ limit. A bonk is now a decision the costs
        // make — "the rest of the window is not worth its climb" — so the
        // state's intent direction decides what it will and won't climb (a
        // crouch's u tilts down, and a 1-high block stops being worth it),
        // and nothing needs a give-up. Length cost is gone: every edge
        // advances p, so progress reward and length cost were one term.
        float pSeed = Vector2.Dot(CellCenter(seedX, seedY), u);
        float pFar  = pSeed + L - _cell;
        float wProg = cfg.LatticeProgressWeight;
        int best = -1; float bestVal = float.NegativeInfinity, bestP = float.NegativeInfinity;
        for (int i = 0; i < reachCount; i++)
        {
            int idx = _order[i];
            if (float.IsPositiveInfinity(_dp[idx])) continue;
            float pI = _orderKey[i];
            float val = wProg * (pI - pSeed) - _dp[idx];
            if (val > bestVal || (val == bestVal && pI > bestP)) { best = idx; bestVal = val; bestP = pI; }
        }
        if (best < 0) { bonk = true; LastBonk = true; return 0; }
        bonk = bestP < pFar;                                     // did not find the far band worth reaching
        cost = _dp[best];
        LastReach = reachCount; LastBonk = bonk; LastCost = cost;

        // Recover seed → goal (parents run goal → seed; reverse in place).
        int count = 0, cur = best;
        while (cur >= 0 && count < outPath.Length)
        {
            float below = _floorBelow[cur];
            var pos = CellCenter(_x0 + cur % _w, _y0 + cur / _w);
            outPath[count++] = new CoastSample
            {
                Pos      = pos,
                Grounded = below <= BallisticPredictor.SupportReach,
                FloorY   = float.IsPositiveInfinity(below) ? float.PositiveInfinity : pos.Y + below,
            };
            cur = _parent[cur];
        }
        for (int i = 0, j = count - 1; i < j; i++, j--)
            (outPath[i], outPath[j]) = (outPath[j], outPath[i]);
        return count;
    }

    // Nearest free cell within a 2-cell Chebyshev radius of the blocked seed
    // cell whose center is not behind the seed along u. Ties break on scan
    // order (deterministic). −1 = none.
    private int SnapSeed(Vector2 seed, Vector2 u, int seedX, int seedY)
    {
        int best = -1; float bestD = float.PositiveInfinity;
        float behind = -0.25f * _cell;
        for (int dy = -2; dy <= 2; dy++)
        for (int dx = -2; dx <= 2; dx++)
        {
            int gx = seedX + dx, gy = seedY + dy;
            if (gx < _x0 || gx >= _x0 + _w || gy < _y0 || gy >= _y0 + _h) continue;
            int idx = (gy - _y0) * _w + (gx - _x0);
            if (_blocked[idx]) continue;
            var rel = CellCenter(gx, gy) - seed;
            if (Vector2.Dot(rel, u) < behind) continue;
            float d = rel.LengthSquared();
            if (d < bestD) { bestD = d; best = idx; }
        }
        return best;
    }

    // Window = bbox of everything a monotone path can reach before the far
    // band: for each admitted offset ô, the point where a straight run along
    // it crosses p = pSeed + L (seed + ô·L/dot(ô,u)). A path mixing offsets
    // never leaves the hull of those extremes, so this is exact for the
    // offset table — and it is what keeps a near-90° cone affordable: the
    // lateral extent is L·(steepest admitted slope), not L·tanθ.
    private void BuildWindow(Vector2 seed, Vector2 u, float L)
    {
        Vector2 min = seed, max = seed;
        for (int a = 0; a < _admittedCount; a++)
        {
            var o = _admitted[a];
            var p = seed + o.Unit * (L / Vector2.Dot(o.Unit, u));
            min = Vector2.Min(min, p); max = Vector2.Max(max, p);
        }
        // Two cells of slack on every side: one for the bbox floor, one so the
        // seed snap has room when the body sits at the window's edge.
        _x0 = (int)MathF.Floor(min.X / _cell) - 2;
        _y0 = (int)MathF.Floor(min.Y / _cell) - 2;
        _w  = (int)MathF.Floor(max.X / _cell) + 3 - _x0;
        _h  = (int)MathF.Floor(max.Y / _cell) + 3 - _y0;

        int seedX = (int)MathF.Floor(seed.X / _cell), seedY = (int)MathF.Floor(seed.Y / _cell);
        while (_w * _h > MaxCells)
        {
            if (_w >= _h)
            {
                if (seedX - _x0 >= _x0 + _w - 1 - seedX) _x0++;   // trim the far-left side
                _w--;
            }
            else
            {
                if (seedY - _y0 >= _y0 + _h - 1 - seedY) _y0++;
                _h--;
            }
        }
    }

    // §3.2 item 1: stamp the C-obstacle template at every solid tile whose
    // footprint overlaps the window, inflated by the corrector margin. Tiles
    // buried on all four sides are skipped: their C-obstacle is covered by the
    // neighbours' (the nearest point of a buried tile to any body center lies
    // on a face it shares with a solid neighbour).
    private void StampObstacles(ChunkMap chunks, Polygon body, int perTile, float margin)
    {
        Array.Clear(_blocked, 0, _w * _h);
        var t = CObstacleTemplate.For(body);
        EnsureMask(t, perTile, margin);
        int ts = Chunk.TileSize;
        float half = ts * 0.5f, reach = t.Reach + margin;

        float wx0 = _x0 * _cell, wy0 = _y0 * _cell;
        float wx1 = (_x0 + _w) * _cell, wy1 = (_y0 + _h) * _cell;
        int tMinX = (int)MathF.Floor((wx0 - reach) / ts), tMaxX = (int)MathF.Floor((wx1 + reach) / ts);
        int tMinY = (int)MathF.Floor((wy0 - reach) / ts), tMaxY = (int)MathF.Floor((wy1 + reach) / ts);

        // Mask cell (mx, my) ↔ grid cell gx = gtx·perTile + kBase + mx (see
        // EnsureMask for the offset algebra).
        int kBase = (perTile - 1) / 2 - _maskR;
        for (int gtx = tMinX; gtx <= tMaxX; gtx++)
        for (int gty = tMinY; gty <= tMaxY; gty++)
        {
            float cx = gtx * ts + half, cy = gty * ts + half;
            if (!TileQuery.IsSolidAt(chunks, cx, cy)) continue;
            if (TileQuery.IsSolidAt(chunks, cx - ts, cy) && TileQuery.IsSolidAt(chunks, cx + ts, cy)
                && TileQuery.IsSolidAt(chunks, cx, cy - ts) && TileQuery.IsSolidAt(chunks, cx, cy + ts))
                continue;                                                   // buried

            int gx0 = gtx * perTile + kBase, gy0 = gty * perTile + kBase;
            int mx0 = Math.Max(0, _x0 - gx0), mx1 = Math.Min(_maskW - 1, _x0 + _w - 1 - gx0);
            int my0 = Math.Max(0, _y0 - gy0), my1 = Math.Min(_maskW - 1, _y0 + _h - 1 - gy0);
            for (int my = my0; my <= my1; my++)
            {
                int row = (gy0 + my - _y0) * _w + (gx0 - _x0);
                int mrow = my * _maskW;
                for (int mx = mx0; mx <= mx1; mx++)
                    if (_mask[mrow + mx]) _blocked[row + mx] = true;
            }
        }
    }

    // Build the per-tile stamp mask for (template, perTile, margin). Mask cell
    // (mx, my) is the grid cell gx = gtx·perTile + kBase + mx; its center's
    // offset from the tile center is (gx + 0.5)·cell − (gtx + 0.5)·ts
    // = (kBase + mx + 0.5 − perTile/2)·cell, independent of gtx. kBase puts
    // mask index R on the tile's central cell (odd perTile) or the cell just
    // left/above center (even), so the mask spans ≥ reach both ways.
    private void EnsureMask(CObstacleTemplate t, int perTile, float margin)
    {
        if (ReferenceEquals(t, _maskTemplate) && perTile == _maskPerTile && margin == _maskMargin)
            return;
        _maskTemplate = t; _maskPerTile = perTile; _maskMargin = margin;
        float reach = t.Reach + margin;
        _maskR = (int)MathF.Ceiling(reach / _cell) + 1;
        _maskW = 2 * _maskR + 1;
        if (_mask.Length < _maskW * _maskW) _mask = new bool[_maskW * _maskW];
        int kBase = (perTile - 1) / 2 - _maskR;
        for (int my = 0; my < _maskW; my++)
        for (int mx = 0; mx < _maskW; mx++)
        {
            var rel = new Vector2((kBase + mx + 0.5f - perTile * 0.5f) * _cell,
                                  (kBase + my + 0.5f - perTile * 0.5f) * _cell);
            bool inside = true;
            foreach (var f in t.Facets)
                if (Vector2.Dot(rel, f.Normal) >= f.Offset + margin) { inside = false; break; }
            _mask[my * _maskW + mx] = inside;
        }
    }

    // §3.2 item 2: per x-column bottom-up sweep — distance from each free
    // cell's center down to the top edge of the first blocked cell below.
    private void SweepFloorBelow()
    {
        for (int gx = 0; gx < _w; gx++)
        {
            float blockTop = float.PositiveInfinity;
            for (int gy = _h - 1; gy >= 0; gy--)
            {
                int idx = gy * _w + gx;
                float cyC = (_y0 + gy + 0.5f) * _cell;
                if (_blocked[idx]) { blockTop = cyC - 0.5f * _cell; _floorBelow[idx] = 0f; }
                else _floorBelow[idx] = blockTop - cyC;
            }
        }
    }

    // §3.2 item 3: forward reachability flood over the same edge set as the DP.
    // Fills _order[0..count) with reachable node indices (BFS order; re-sorted
    // by p before the sweep).
    private int Flood(int seedIdx)
    {
        Array.Clear(_reachable, 0, _w * _h);
        int head = 0, tail = 0, count = 0;
        _reachable[seedIdx] = true;
        _queue[tail++] = seedIdx;
        while (head < tail)
        {
            int n = _queue[head++];
            _order[count++] = n;
            int nx = n % _w, ny = n / _w;
            int forced = ForcedOffset(nx, ny);
            for (int a = 0; a < _admittedCount; a++)
            {
                if (forced >= 0 && a != forced) continue;        // seed run
                ref var o = ref _admitted[a];
                if (!EdgeFree(nx, ny, ref o)) continue;
                int m = (ny + o.Dy) * _w + (nx + o.Dx);
                if (_reachable[m]) continue;
                _reachable[m] = true;
                _queue[tail++] = m;
            }
        }
        return count;
    }

    // Edge (nx,ny) → (nx,ny)+o is in the window, lands on a free cell and
    // crosses only free cells (the supercover tunneling check, §3.3).
    private bool EdgeFree(int nx, int ny, ref Offset o)
    {
        int mx = nx + o.Dx, my = ny + o.Dy;
        if ((uint)mx >= (uint)_w || (uint)my >= (uint)_h) return false;
        if (_blocked[my * _w + mx]) return false;
        var cross = o.Cross;
        for (int c = 0; c < o.CrossCount; c++)
            if (_blocked[(ny + cross[2 * c + 1]) * _w + (nx + cross[2 * c])]) return false;
        return true;
    }

    // The seed run's constraint at a node: the run offset if (nx,ny) is the
    // j-th node of the run (seed + j·o, j < _runLen), else −1 (free choice).
    private int ForcedOffset(int nx, int ny)
    {
        if (_runOffset < 0) return -1;
        ref var o = ref _admitted[_runOffset];
        int dx = nx - _seedCx, dy = ny - _seedCy, j;
        if (o.Dx != 0)
        {
            if (dx % o.Dx != 0) return -1;
            j = dx / o.Dx;
            if (dy != j * o.Dy) return -1;
        }
        else
        {
            if (dx != 0 || dy % o.Dy != 0) return -1;
            j = dy / o.Dy;
        }
        return j >= 0 && j < _runLen ? _runOffset : -1;
    }
}
