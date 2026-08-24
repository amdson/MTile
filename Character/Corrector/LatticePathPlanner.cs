using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Lattice path planner (Plans/LATTICE_PATH_PLANNER.md) — PHASE 0/1: the
// oracle. Not wired into the live sim; runs in the freeze-frame inspector
// beside the LM probe and the old state-space LatticePlanner, and in tests.
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
//   - cost: per-edge steepness (angle off u) + length, per-node hover toward
//     the surface below (state-supplied on/off flag), seed-edge velocity bias
//     (§3.4–3.5);
//   - goal: cheapest reachable node in the far band, else the furthest
//     reachable node — the honest bonk (§3.4).
//
// Determinism: pooled fixed-size arrays, no allocation per solve (after the
// instance is built), deterministic orders (float-key sort; equal keys can
// permute but equal-p nodes never share an edge, so any tie order yields the
// same DP result). No statics beyond immutable tables. Render/oracle-only
// today — nothing here is snapshot state.
public sealed class LatticePathPlanner
{
    public const int MaxCells = 4096;   // pooled scratch bound; window clamps to fit

    // ── Primitive offset table (§3.3): |dx|,|dy| ≤ 2, gcd = 1 ────────────────
    // Each offset carries the cells its segment crosses (conservative
    // supercover — diagonals require both side cells free, so a body never
    // slips through a checkerboard pinch).
    private struct Offset
    {
        public sbyte Dx, Dy;
        public sbyte C1x, C1y, C2x, C2y;   // crossed cells; 0,0 = none
        public byte  CrossCount;
        public Vector2 Unit;
        public float  Len;                 // in cells
    }
    private static readonly Offset[] AllOffsets = BuildOffsets();

    private static Offset[] BuildOffsets()
    {
        (int dx, int dy, (int, int)[] cross)[] baseSet =
        {
            (1, 0, Array.Empty<(int, int)>()),
            (0, 1, Array.Empty<(int, int)>()),
            (1, 1, new[] { (1, 0), (0, 1) }),
            (1, 2, new[] { (0, 1), (1, 1) }),
            (2, 1, new[] { (1, 0), (1, 1) }),
        };
        int[] positive = { 1 }, both = { 1, -1 };
        var list = new Offset[16];
        int n = 0;
        foreach (var (dx, dy, cross) in baseSet)
        foreach (int sx in dx == 0 ? positive : both)
        foreach (int sy in dy == 0 ? positive : both)
        {
            var o = new Offset { Dx = (sbyte)(dx * sx), Dy = (sbyte)(dy * sy) };
            if (cross.Length > 0)
            {
                o.C1x = (sbyte)(cross[0].Item1 * sx); o.C1y = (sbyte)(cross[0].Item2 * sy);
                o.C2x = (sbyte)(cross[1].Item1 * sx); o.C2y = (sbyte)(cross[1].Item2 * sy);
                o.CrossCount = 2;
            }
            o.Len  = MathF.Sqrt(o.Dx * o.Dx + o.Dy * o.Dy);
            o.Unit = new Vector2(o.Dx / o.Len, o.Dy / o.Len);
            list[n++] = o;
        }
        Array.Resize(ref list, n);
        return list;
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

    // ── Debug accessors (freeze-frame overlay; §3.8) ─────────────────────────
    public float DebugCell   => _cell;
    public int   DebugWidth  => _w;
    public int   DebugHeight => _h;
    public Vector2 DebugCellCenter(int cx, int cy) => CellCenter(_x0 + cx, _y0 + cy);
    public bool  DebugBlocked(int cx, int cy)   => _blocked[cy * _w + cx];
    public bool  DebugReachable(int cx, int cy) => _reachable[cy * _w + cx];
    public string LastDebug { get; private set; } = "";

    private Vector2 CellCenter(int gx, int gy) => new((gx + 0.5f) * _cell, (gy + 0.5f) * _cell);

    // Solve. u = requested direction (unit not required; zero → no solve).
    // hover on/off is the state-supplied flag (§3.4). Returns the number of
    // path samples written to outPath (body-center positions, seed → goal);
    // 0 = no solve (zero u, blocked seed, or degenerate window).
    public int Solve(ChunkMap chunks, Polygon body, Vector2 seed, Vector2 vel,
                     Vector2 u, bool hover, float hoverOffset,
                     CoastSample[] outPath, out float cost, out bool bonk)
    {
        cost = 0f; bonk = false;
        var cfg = MovementConfig.Current;
        if (u.LengthSquared() < 1e-8f) return 0;
        u.Normalize();

        float cosTheta = Math.Clamp(cfg.LatticeConeCos, 0.05f, 0.99f);
        int   perTile  = Math.Clamp(cfg.LatticeCellsPerTile, 2, 8);
        _cell = (float)Chunk.TileSize / perTile;
        float L = cfg.LatticeLookaheadTiles * Chunk.TileSize;

        BuildWindow(seed, u, cosTheta, L);
        if (_w <= 0 || _h <= 0) return 0;

        // Admitted offsets under the cone (§3.3).
        _admittedCount = 0;
        foreach (var o in AllOffsets)
            if (Vector2.Dot(o.Unit, u) >= cosTheta)
                _admitted[_admittedCount++] = o;
        if (_admittedCount == 0) return 0;

        StampObstacles(chunks, body, cfg.CorrectorMargin);
        SweepFloorBelow();

        int seedX = (int)MathF.Floor(seed.X / _cell), seedY = (int)MathF.Floor(seed.Y / _cell);
        if (seedX < _x0 || seedX >= _x0 + _w || seedY < _y0 || seedY >= _y0 + _h) return 0;
        int seedIdx = (seedY - _y0) * _w + (seedX - _x0);
        if (_blocked[seedIdx]) { bonk = true; return 0; }

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
        float wSteep = cfg.LatticeSteepWeight, wLen = cfg.LatticeLenWeight;
        float wHover = cfg.LatticeHoverWeight, wSeed = cfg.LatticeSeedWeight;
        Vector2 vHat = vel.LengthSquared() > 1f ? Vector2.Normalize(vel) : Vector2.Zero;
        for (int i = 0; i < reachCount; i++) _dp[_order[i]] = float.PositiveInfinity;
        _dp[seedIdx] = 0f; _parent[seedIdx] = -1;

        for (int i = 0; i < reachCount; i++)
        {
            int n = _order[i];
            float dn = _dp[n];
            if (float.IsPositiveInfinity(dn)) continue;
            int nx = n % _w, ny = n / _w;
            bool atSeed = n == seedIdx;
            for (int a = 0; a < _admittedCount; a++)
            {
                ref var o = ref _admitted[a];
                int mx = nx + o.Dx, my = ny + o.Dy;
                if ((uint)mx >= (uint)_w || (uint)my >= (uint)_h) continue;
                int m = my * _w + mx;
                if (_blocked[m]) continue;
                if (o.CrossCount > 0)
                {
                    int c1 = (ny + o.C1y) * _w + (nx + o.C1x);
                    int c2 = (ny + o.C2y) * _w + (nx + o.C2x);
                    if (_blocked[c1] || _blocked[c2]) continue;   // tunneling (§3.3)
                }
                float c = dn
                    + wSteep * (1f - Vector2.Dot(o.Unit, u))
                    + wLen * o.Len * _cell;
                if (hover && !float.IsPositiveInfinity(_floorBelow[m]))
                {
                    float dev = _floorBelow[m] - hoverOffset;
                    c += wHover * dev * dev;
                }
                if (atSeed && vHat != Vector2.Zero)
                    c += wSeed * (1f - Vector2.Dot(o.Unit, vHat));
                if (c < _dp[m]) { _dp[m] = c; _parent[m] = n; }
            }
        }

        // ── Goal: far band, else furthest reachable (§3.4) ──────────────────
        float pSeed = Vector2.Dot(CellCenter(seedX, seedY), u);
        float pFar  = pSeed + L - _cell;
        int best = -1; float bestP = float.NegativeInfinity, bestCost = float.PositiveInfinity;
        for (int i = 0; i < reachCount; i++)
        {
            int idx = _order[i];
            if (float.IsPositiveInfinity(_dp[idx])) continue;
            float pI = _orderKey[i];
            if (pI >= pFar)
            {
                if (bestP < pFar || _dp[idx] < bestCost) { best = idx; bestCost = _dp[idx]; bestP = pFar; }
            }
            else if (bestP < pFar && (pI > bestP || (pI == bestP && _dp[idx] < bestCost)))
            {
                best = idx; bestP = pI; bestCost = _dp[idx];
            }
        }
        if (best < 0) { bonk = true; return 0; }
        bonk = bestP < pFar;
        cost = _dp[best];
        LastDebug = $"cells={_w}x{_h} reach={reachCount} bonk={bonk} cost={cost:F1}";

        // Recover seed → goal (parents run goal → seed; reverse in place).
        int count = 0, cur = best;
        while (cur >= 0 && count < outPath.Length)
        {
            outPath[count++] = new CoastSample
                { Pos = CellCenter(_x0 + cur % _w, _y0 + cur / _w) };
            cur = _parent[cur];
        }
        for (int i = 0, j = count - 1; i < j; i++, j--)
            (outPath[i], outPath[j]) = (outPath[j], outPath[i]);
        return count;
    }

    // Window = bbox of the cone fan {seed + t·R(±φ)u : t ∈ [0,L], |φ| ≤ θ}
    // (§2.1): the two edge rays, the axis directions inside the cone (the arc's
    // axis-aligned extremes), and the seed itself. Clamped to MaxCells by
    // trimming the side of the longer axis farther from the seed.
    private void BuildWindow(Vector2 seed, Vector2 u, float cosTheta, float L)
    {
        float sinTheta = MathF.Sqrt(MathF.Max(0f, 1f - cosTheta * cosTheta));
        Span<Vector2> dirs = stackalloc Vector2[6];
        int nd = 0;
        dirs[nd++] = new Vector2(u.X * cosTheta - u.Y * sinTheta, u.X * sinTheta + u.Y * cosTheta);
        dirs[nd++] = new Vector2(u.X * cosTheta + u.Y * sinTheta, -u.X * sinTheta + u.Y * cosTheta);
        Span<Vector2> axes = stackalloc Vector2[]
            { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };
        foreach (var a in axes)
            if (Vector2.Dot(a, u) >= cosTheta) dirs[nd++] = a;

        Vector2 min = seed, max = seed;
        for (int i = 0; i < nd; i++)
        {
            var p = seed + dirs[i] * L;
            min = Vector2.Min(min, p); max = Vector2.Max(max, p);
        }
        _x0 = (int)MathF.Floor(min.X / _cell) - 1;
        _y0 = (int)MathF.Floor(min.Y / _cell) - 1;
        _w  = (int)MathF.Floor(max.X / _cell) + 2 - _x0;
        _h  = (int)MathF.Floor(max.Y / _cell) + 2 - _y0;

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
    // footprint overlaps the window, inflated by the corrector margin.
    private void StampObstacles(ChunkMap chunks, Polygon body, float margin)
    {
        Array.Clear(_blocked, 0, _w * _h);
        var t = CObstacleTemplate.For(body);
        int ts = Chunk.TileSize;
        float half = ts * 0.5f, reach = t.Reach + margin;

        float wx0 = _x0 * _cell, wy0 = _y0 * _cell;
        float wx1 = (_x0 + _w) * _cell, wy1 = (_y0 + _h) * _cell;
        int tMinX = (int)MathF.Floor((wx0 - reach) / ts), tMaxX = (int)MathF.Floor((wx1 + reach) / ts);
        int tMinY = (int)MathF.Floor((wy0 - reach) / ts), tMaxY = (int)MathF.Floor((wy1 + reach) / ts);

        for (int gtx = tMinX; gtx <= tMaxX; gtx++)
        for (int gty = tMinY; gty <= tMaxY; gty++)
        {
            float cx = gtx * ts + half, cy = gty * ts + half;
            if (!TileQuery.IsSolidAt(chunks, cx, cy)) continue;

            int sx0 = Math.Max(_x0, (int)MathF.Floor((cx - reach) / _cell));
            int sy0 = Math.Max(_y0, (int)MathF.Floor((cy - reach) / _cell));
            int sx1 = Math.Min(_x0 + _w - 1, (int)MathF.Floor((cx + reach) / _cell));
            int sy1 = Math.Min(_y0 + _h - 1, (int)MathF.Floor((cy + reach) / _cell));
            for (int gy = sy0; gy <= sy1; gy++)
            for (int gx = sx0; gx <= sx1; gx++)
            {
                int idx = (gy - _y0) * _w + (gx - _x0);
                if (_blocked[idx]) continue;
                var rel = CellCenter(gx, gy) - new Vector2(cx, cy);
                bool inside = true;
                foreach (var f in t.Facets)
                    if (Vector2.Dot(rel, f.Normal) >= f.Offset + margin) { inside = false; break; }
                if (inside) _blocked[idx] = true;
            }
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
            for (int a = 0; a < _admittedCount; a++)
            {
                ref var o = ref _admitted[a];
                int mx = nx + o.Dx, my = ny + o.Dy;
                if ((uint)mx >= (uint)_w || (uint)my >= (uint)_h) continue;
                int m = my * _w + mx;
                if (_reachable[m] || _blocked[m]) continue;
                if (o.CrossCount > 0)
                {
                    int c1 = (ny + o.C1y) * _w + (nx + o.C1x);
                    int c2 = (ny + o.C2y) * _w + (nx + o.C2x);
                    if (_blocked[c1] || _blocked[c2]) continue;
                }
                _reachable[m] = true;
                _queue[tail++] = m;
            }
        }
        return count;
    }
}
