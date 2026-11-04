using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Routing graph + search (Plans/BOT_ROUTING_PLAN.md M2). Built fresh per plan over a
// window of cells around the bot and its target; terrain is on screen, so reading it live
// is within the perception rule.
//
// Nodes are STAND NODES: (x, y) is the cell a standing body's legs occupy — empty, with
// HeadroomCells empty cells from y up (the stand fold hovers the torso a cell above the
// floor, so the body's AABB spans rows y-3..y-1 and the legs reach row y) and a solid,
// non-hostile floor at (x, y + 1).
//
// Edges come only from the measured JumpTable: a → b is an edge when b - a matches an
// entry (mirrored for -x) and every cell the calibration body swept is empty here too.
// The sweep is what makes the measured envelope portable to arbitrary terrain: a ceiling,
// a wall or an intermediate floor that the calibration arc didn't have shows up as a
// swept cell that isn't empty, and the edge is dropped. Likewise every column the body
// may land in (the table's LandMin..LandMax) needs floor at the destination height. Running takeoffs also need their
// run-up: RunUpCells of walkable stand nodes behind the takeoff.
public sealed class NavGrid
{
    public const int HeadroomCells = 4;
    public const int MaxDx = 10, MaxRise = 5, MaxDrop = 19;
    // Overheads on top of the table's press→landed frames.
    public const int StandSettleFrames = 12;   // walk to the spot and come to rest
    public const int RunUpFrames       = 30;   // back off and run in

    public readonly struct Node : IEquatable<Node>
    {
        public readonly int X, Y;
        public Node(int x, int y) { X = x; Y = y; }
        public bool Equals(Node o) => X == o.X && Y == o.Y;
        public override bool Equals(object o) => o is Node n && Equals(n);
        public override int GetHashCode() => X * 73856093 ^ Y * 19349663;
        public override string ToString() => $"({X},{Y})";
    }

    public enum EdgeKind : byte { Jump, Climb, Build }

    public readonly struct Edge
    {
        public readonly Node From, To;
        public readonly int Dir;                // +1 / -1: travel direction
        public readonly EdgeKind Kind;
        public readonly JumpTable.Entry Jump;   // Kind == Jump
        public readonly int Cost;               // frames
        public Edge(Node from, Node to, int dir, JumpTable.Entry jump, int cost)
        { From = from; To = to; Dir = dir; Kind = EdgeKind.Jump; Jump = jump; Cost = cost; }
        public Edge(Node from, Node to, int dir, EdgeKind kind, int cost)
        { From = from; To = to; Dir = dir; Kind = kind; Jump = default; Cost = cost; }
        public override string ToString() => Kind == EdgeKind.Jump ? $"{From}->{To} {Jump.Takeoff}" : $"{From}->{To} {Kind}";
    }

    // Wall climb (WallClimbManeuver's controller, replayed by RouteFollower): up a single
    // face from the cell beside it to the top. ZzzRouteWallClimbCalibration: every face
    // height tried (4–24 cells), pillars 1 cell wide included, in ~55 + 6.2 h frames.
    public const int MinClimb = 3, MaxClimb = 40;
    public static int ClimbCost(int h) => 60 + 7 * h;

    // Build-up (BuildClimbManeuver's elevator, replayed by RouteFollower): stand still and
    // stack the column under your own feet k cells, ~6 frames a cell. The top is a
    // VIRTUAL stand node — its floor doesn't exist yet — so it is recorded in _virtual and
    // counts as a stand node for every later edge out of it. Off when AllowBuild is false.
    public bool AllowBuild = true;
    public const int MinBuild = 2, MaxBuild = 30;
    // Priced above a climb of the same height: a climb spends no blocks and is as fast.
    public static int BuildCost(int k) => 70 + 8 * k;
    private readonly HashSet<Node> _virtual = new();
    private readonly SortedSet<int> _standRows = new();
    public int? GoalRow;

    private readonly Func<int, int, bool> _solid;      // cell is solid
    private readonly Func<int, int, bool> _hostile;    // cell carries an opposing spike
    private int _x0, _y0, _w, _h;
    private bool[] _stand;

    public NavGrid(Func<int, int, bool> solid, Func<int, int, bool> hostile = null)
    {
        _solid = solid;
        _hostile = hostile ?? ((_, _) => false);
    }

    public static NavGrid For(BotView v)
        => new((x, y) => v.Solid(x, y), (x, y) => SpikeGuard.Hostile(v, x, y));

    public bool Empty(int x, int y) => !_solid(x, y);

    public bool IsStandCell(int x, int y)
    {
        if (!_solid(x, y + 1) || _hostile(x, y + 1)) return false;
        for (int k = 0; k < HeadroomCells; k++) if (_solid(x, y - k)) return false;
        return true;
    }

    // The node a body is standing on (or will land on): the highest solid floor at or
    // below it within a few cells, under any part of the body's width — a body standing
    // on a lip has its centre over the drop, and reading only the centre column would
    // put it on the floor below.
    public const float BodyHalfWidth = 5f;
    public static Node? NodeUnder(Func<int, int, bool> solid, Vector2 pos, int maxCells = 6)
    {
        int y = (int)MathF.Floor(pos.Y / Chunk.TileSize);
        int cx = (int)MathF.Floor(pos.X / Chunk.TileSize);
        int xl = (int)MathF.Floor((pos.X - BodyHalfWidth) / Chunk.TileSize);
        int xr = (int)MathF.Floor((pos.X + BodyHalfWidth) / Chunk.TileSize);
        Node? best = null; int bestK = int.MaxValue;
        for (int x = xl; x <= xr; x++)
        {
            for (int k = 0; k <= maxCells; k++)
            {
                if (!solid(x, y + k)) continue;
                // k == 0: this column is solid at the body's own height (a wall it is
                // pressed against) — not a floor.
                if (k > 0 && (k < bestK || (k == bestK && x == cx))) { bestK = k; best = new Node(x, y + k - 1); }
                break;
            }
        }
        return best;
    }
    public Node? NodeUnder(Vector2 pos, int maxCells = 6) => NodeUnder(_solid, pos, maxCells);

    // Scan the window once for stand cells.
    public void Build(int x0, int y0, int w, int h)
    {
        _x0 = x0; _y0 = y0; _w = w; _h = h;
        _stand = new bool[w * h];
        _virtual.Clear(); _standRows.Clear();
        for (int j = 0; j < h; j++)
        for (int i = 0; i < w; i++)
            if (_stand[j * w + i] = IsStandCell(x0 + i, y0 + j)) _standRows.Add(y0 + j);
    }

    public bool InWindow(int x, int y) => x >= _x0 && x < _x0 + _w && y >= _y0 && y < _y0 + _h;
    public bool Stand(Node n) => _virtual.Contains(n) || (InWindow(n.X, n.Y) && _stand[(n.Y - _y0) * _w + (n.X - _x0)]);
    public bool IsVirtual(Node n) => _virtual.Contains(n);
    public bool StandReal(Node n) => InWindow(n.X, n.Y) && _stand[(n.Y - _y0) * _w + (n.X - _x0)];

    // Every edge out of `a`.
    public void EdgesFrom(Node a, List<Edge> into)
    {
        into.Clear();
        for (int dir = -1; dir <= 1; dir += 2)
        for (int dx = 1; dx <= MaxDx; dx++)
        for (int dy = MaxRise; dy >= -MaxDrop; dy--)
        {
            var b = new Node(a.X + dir * dx, a.Y - dy);
            if (!StandReal(b)) continue;   // never onto another branch's virtual column
            if (!JumpTable.TryGet(dx, dy, out var e)) continue;
            if (!SweepClear(a, dir, e)) continue;
            if (!LandingClear(a, b, dir, e)) continue;
            if (e.Takeoff == JumpTable.Takeoff.Walk && !WalkOffClear(a, dir, e)) continue;
            if (e.Takeoff >= JumpTable.Takeoff.Edge && !RunUpClear(a, dir)) continue;
            into.Add(new Edge(a, b, dir, e, Cost(e)));
        }
        for (int dir = -1; dir <= 1; dir += 2)
            if (TryClimb(a, dir, out var top)) into.Add(new Edge(a, top, dir, EdgeKind.Climb, ClimbCost(a.Y - top.Y)));
        if (AllowBuild) AddBuilds(a, into);
    }

    // Build edges: only to heights worth standing at — level with (or a cell under) the
    // goal, or 0..2 cells under some row that has stand nodes (something to step or jump
    // onto from the top). The column needs its own cells empty, headroom over the top, and
    // empty neighbours beside every new cell: a requested cell grows out of every solid
    // neighbour at once (ChunkMap.TryRequestTile), so one touching a wall shoves the body
    // off its own column (BuildClimbManeuver keeps a one-cell gap for the same reason).
    private readonly List<int> _buildRows = new();
    private void AddBuilds(Node a, List<Edge> into)
    {
        _buildRows.Clear();
        void Want(int r) { if (a.Y - r >= MinBuild && a.Y - r <= MaxBuild && !_buildRows.Contains(r)) _buildRows.Add(r); }
        if (GoalRow is int g) { Want(g); Want(g + 1); }
        foreach (int r in _standRows) { if (r >= a.Y) break; Want(r); Want(r + 1); Want(r + 2); }
        if (_buildRows.Count == 0) return;
        foreach (int r in _buildRows)
        {
            // Cells a.Y..r+1 become the column (empty, with empty neighbours); the body on
            // top then needs rows r..r-3 clear.
            // The neighbours stay clear up through the body's rows at the top as well: a
            // body lifted past a corner beside its column gets shoved off it (the
            // movement layer reacts to the corner), so the column keeps a one-cell gap
            // from anything solid, all the way up.
            bool clear = true;
            for (int y = r - (HeadroomCells - 1); y <= a.Y && clear; y++)
                clear = !_solid(a.X, y) && !_solid(a.X - 1, y) && !_solid(a.X + 1, y);
            if (!clear) continue;
            var top = new Node(a.X, r);
            _virtual.Add(top);
            into.Add(new Edge(a, top, 0, EdgeKind.Build, BuildCost(a.Y - r)));
        }
    }

    // A face directly beside `a` (solid at leg height) whose column tops out MinClimb..
    // MaxClimb cells up onto a stand cell, with the lane beside it (column a.X) clear all
    // the way up for the climbing body.
    public bool TryClimb(Node a, int dir, out Node top)
    {
        top = default;
        int fx = a.X + dir;
        if (!_solid(fx, a.Y)) return false;
        int y = a.Y;
        while (y > a.Y - MaxClimb - 1 && _solid(fx, y - 1)) y--;
        int h = a.Y - (y - 1);
        if (h < MinClimb || h > MaxClimb) return false;
        top = new Node(fx, y - 1);
        if (!IsStandCell(top.X, top.Y)) return false;
        for (int r = top.Y - HeadroomCells + 1; r <= a.Y; r++) if (_solid(a.X, r)) return false;
        return true;
    }

    public static int Cost(JumpTable.Entry e) => e.Frames + e.Takeoff switch
    {
        JumpTable.Takeoff.Walk  => 0,
        JumpTable.Takeoff.Stand => StandSettleFrames,
        _                       => RunUpFrames,
    } + (int)(e.Hurt * 120f);   // a tenth of an HP is worth ~12 frames of detour

    private bool SweepClear(Node a, int dir, JumpTable.Entry e)
    {
        // Clipped at the landing: the calibration body kept walking after it came down,
        // so the recorded sweep runs into the column past the destination — which next
        // to a wall would reject the last step onto its foot.
        int last = Math.Min(e.SweepColumns - 1, Math.Max(e.Dx, e.LandMax));
        for (int c = 0; c <= last; c++)
        {
            int top = e.SweepTop(c), bottom = e.SweepBottom(c);
            if (bottom < top) continue;                  // column not touched
            int x = a.X + dir * c;
            for (int r = top; r <= bottom; r++)
                if (_solid(x, a.Y + r)) return false;
        }
        return true;
    }

    // Every column the calibrated body can come down in has floor at the destination's
    // height: a narrow top (a 1-cell pillar) is only an edge if the table lands that
    // precisely.
    private bool LandingClear(Node a, Node b, int dir, JumpTable.Entry e)
    {
        for (int c = Math.Min(e.LandMin, e.Dx); c <= Math.Max(e.LandMax, e.Dx); c++)
            if (!StandReal(new Node(a.X + dir * c, b.Y))) return false;
        return true;
    }

    // A Walk entry never leaves the ground on its own: it walks off an edge. Calibration
    // had open pit at the floor row between takeoff and destination, so require it here —
    // with floor there the body just keeps walking and lands short of the plan.
    private bool WalkOffClear(Node a, int dir, JumpTable.Entry e)
    {
        if (e.Dy == 0 && e.Dx == 1) return true;           // plain step along the floor
        for (int c = 1; c < e.Dx; c++)
            if (_solid(a.X + dir * c, a.Y + 1)) return false;
        return true;
    }

    private bool RunUpClear(Node a, int dir)
    {
        int need = (int)MathF.Ceiling(JumpTable.RunUpCells) - 2;   // the generator also flew 3-cell run-ups
        for (int k = 1; k <= need; k++)
            if (!Stand(new Node(a.X - dir * k, a.Y))) return false;
        return true;
    }

    // A* from `start` to any node satisfying `goal`; `h` must not overestimate. Returns
    // the edge list, or — when no goal is reachable — the path to the reachable node with
    // the lowest h (so a caller can still make progress, or build from there).
    public List<Edge> Search(Node start, Func<Node, bool> goal, Func<Node, int> h, out bool reached, int maxExpand = 4000)
    {
        reached = false;
        var came = new Dictionary<Node, Edge>();
        var g = new Dictionary<Node, int> { [start] = 0 };
        var open = new PriorityQueue<Node, int>();
        open.Enqueue(start, h(start));
        var closed = new HashSet<Node>();
        var edges = new List<Edge>();
        Node best = start; int bestH = h(start);
        int expanded = 0;
        while (open.Count > 0 && expanded < maxExpand)
        {
            var n = open.Dequeue();
            if (!closed.Add(n)) continue;
            expanded++;
            if (goal(n)) { reached = true; best = n; break; }
            int hn = h(n);
            if (hn < bestH || (hn == bestH && g[n] < g[best])) { bestH = hn; best = n; }
            EdgesFrom(n, edges);
            foreach (var e in edges)
            {
                int ng = g[n] + e.Cost;
                if (g.TryGetValue(e.To, out int old) && old <= ng) continue;
                g[e.To] = ng;
                came[e.To] = e;
                open.Enqueue(e.To, ng + h(e.To));
            }
        }
        var path = new List<Edge>();
        for (var n = best; came.TryGetValue(n, out var e); n = e.From) path.Add(e);
        path.Reverse();
        return path;
    }
}
