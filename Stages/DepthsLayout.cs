using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// The "depths" co-op gauntlet's terrain: a left-to-right chain of underground stone
// rooms joined by short tunnels, each tunnel plugged by a Hardened door that the
// stage opens when the room before it is cleared. Built in code from a seed, so both
// rollback peers build the identical map. See Stages/DepthsStage.cs for the encounters.
//
// Rooms come in shapes (DepthsShape): plain halls, long low galleries, climbing shafts,
// stepped terraces, chasms, domes and massive caverns. A room's entrance (left wall) and
// exit (right wall) need not share a floor height — a shaft is entered at the bottom and
// left at the top — so the chain drifts vertically and the map is as tall as it needs.
// Every room is checked, as it is built, for a plain-jump route from entrance to exit
// (Reach below: steps of up to 3 tiles up, 4 across); a room that fails is rebuilt from
// the next seed.
//
// Coordinates: every Rectangle / Point here is in TILE units (gtx, gty); every Vector2
// is a WORLD-pixel body-centre position. Y-down. Tile (gtx, gty) spans world
// [gtx*TileSize, (gtx+1)*TileSize).
public enum DepthsShape
{
    Small,      // ~30x12 hall
    Medium,     // ~42x16 hall, maybe a pillar with a doorway
    Large,      // ~60x22 hall with ledges (boss-capable)
    Shaft,      // ~22 wide, ~50 tall: climb a zigzag of platforms, exit at the top
    Massive,    // ~125x38 cavern: drop in from a high shelf, ledge towers, arched pillars (boss-capable)
    Gallery,    // ~72x9 long, low, toothy corridor
    Dome,       // ~52x27 arched cavern with side ledges (boss-capable)
    Terraces,   // 3-4 floors stepping up toward the exit
    Chasm,      // high shelves either side of a pit, bridged by floating platforms
}

public sealed class DepthsRoom
{
    public int       Index;
    public DepthsShape Shape;
    // Bounding box of the room's open interior in tiles (walls/floor/ceiling are outside
    // it; irregular shapes leave stone inside it too).
    public Rectangle Interior;
    // The first tile row below the interior (the lowest floor; world px = FloorRow * TileSize).
    public int       FloorRow;
    // Floor rows (solid row under the standing cell) at the entrance and at the exit.
    public int       EntryFloorRow, ExitFloorRow;
    // Cells of the Hardened plug in this room's EXIT tunnel (toward room Index+1).
    // Empty for the last room. Breaking them all opens the way on.
    public List<Point> ExitDoor = new();
    // A clear standing spot just inside the room's entrance, for respawning players.
    public Vector2   Entry;
    // A second clear standing spot a few tiles right of Entry on the same floor, where the
    // co-op partner spawns/respawns. Falls back to Entry when no such spot exists.
    public Vector2   Entry2;
    // Size class (0 small, 1 medium, 2 large / boss-capable).
    public int       Size;
    // World-px centre of the room interior.
    public Vector2   Center;
    // Floor spots with clearance for a radius-26 body (centre = surface - 26).
    public List<Vector2> BossSpots = new();
    // Spawn anchors, all inside Interior and clear of obstacles, for a body of radius
    // up to ~16 px. Floor spots stand on a surface reachable on foot from the entrance;
    // ceiling spots hang under rock; wall spots hug a rock face; air spots are open
    // space (for flyers).
    public List<Vector2> FloorSpots   = new();
    public List<Vector2> CeilingSpots = new();
    public List<Vector2> WallSpots    = new();
    public List<Vector2> AirSpots     = new();
}

public sealed class DepthsMap
{
    public ChunkMap         Chunks;
    public List<DepthsRoom> Rooms = new();
    // Where the players start (room 0's Entry).
    public Vector2          PlayerSpawn;
    // Where the second player starts (room 0's Entry2).
    public Vector2          SecondSpawn;
    // The solid/air grid the chunks were built from (immutable after Build; the room
    // data above is likewise read-only, so one DepthsMap can back many Simulations).
    public char[,]          Grid;
    // Map extent in tiles; the whole box [0,Width) x [0,Height) is solid except rooms and tunnels.
    public int              Width, Height;
}

public static class DepthsLayout
{
    private const int SideSkin = 2, VertSkin = 3, Margin = 6;
    private const char Air = ' ', Stone = 'X', Hard = 'H';
    // Plain-jump reach used by the route check: rise per jump, and run per jump.
    public const int JumpRise = 3, JumpRun = 4;

    // splitmix64: tiny deterministic RNG (no System.Random, no time).
    private struct Rng
    {
        private ulong _s;
        public Rng(ulong seed) { _s = seed; }
        public ulong NextU()
        {
            ulong z = (_s += 0x9E3779B97F4A7C15UL);
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
        // [lo, hi] inclusive.
        public int Range(int lo, int hi) => hi <= lo ? lo : lo + (int)(NextU() % (ulong)(hi - lo + 1));
        public bool Chance(int pct) => Range(0, 99) < pct;
    }

    public static int SizeClass(DepthsShape s) => s switch
    {
        DepthsShape.Small => 0,
        DepthsShape.Large or DepthsShape.Dome or DepthsShape.Massive => 2,
        _ => 1,
    };

    // Legacy entry point: room i gets size class `sizes[i]` (0 Small, 1 Medium, 2 Large)
    // when provided; with no sizes every room gets a random shape (first Small, last Massive).
    public static DepthsMap Build(int seed, int roomCount, IReadOnlyList<int> sizes = null)
    {
        if (roomCount < 1) throw new System.ArgumentOutOfRangeException(nameof(roomCount));
        DepthsShape[] shapes = null;
        if (sizes != null)
        {
            shapes = new DepthsShape[roomCount];
            for (int i = 0; i < roomCount; i++)
                shapes[i] = (i < sizes.Count ? System.Math.Clamp(sizes[i], 0, 2) : 1) switch
                {
                    0 => DepthsShape.Small, 1 => DepthsShape.Medium, _ => DepthsShape.Large,
                };
        }
        return BuildCore(seed, roomCount, shapes);
    }

    public static DepthsMap Build(int seed, IReadOnlyList<DepthsShape> shapes)
    {
        if (shapes == null || shapes.Count < 1) throw new System.ArgumentException("need at least one room", nameof(shapes));
        var a = new DepthsShape[shapes.Count];
        for (int i = 0; i < a.Length; i++) a[i] = shapes[i];
        return BuildCore(seed, a.Length, a);
    }

    // Doors: each ExitDoor cell is Hardened, but ChunkMap.BreakCell only tests
    // IsSolid (not the type), so BreakCell(x, y) removes Hardened cells fine.
    private static DepthsMap BuildCore(int seed, int roomCount, DepthsShape[] shapes)
    {
        var rng = new Rng((ulong)(uint)seed * 0x2545F4914F6CDD1DUL + 0x1234567UL);

        // ── Plan rooms (rows relative to room 0's entrance floor) ──
        var shp = new DepthsShape[roomCount];
        for (int i = 0; i < roomCount; i++)
            shp[i] = shapes != null ? shapes[i]
                   : i == 0 ? DepthsShape.Small
                   : i == roomCount - 1 ? DepthsShape.Massive
                   : (DepthsShape)rng.Range(0, 8);

        var loc = new Local[roomCount];
        var x0 = new int[roomCount]; var top = new int[roomCount]; var tun = new int[roomCount];
        int x = Margin + SideSkin, entryRel = 0;
        for (int i = 0; i < roomCount; i++)
        {
            loc[i] = MakeRoom(shp[i], rng.NextU());
            x0[i]  = x;
            top[i] = entryRel - loc[i].EntryFloor;
            tun[i] = rng.Range(6, 10);
            x += loc[i].W + (i < roomCount - 1 ? tun[i] : 0);
            entryRel = top[i] + loc[i].ExitFloor + rng.Range(-3, 3);
        }
        int minRow = int.MaxValue, maxRow = int.MinValue;
        for (int i = 0; i < roomCount; i++)
        {
            minRow = System.Math.Min(minRow, top[i] - VertSkin);
            maxRow = System.Math.Max(maxRow, top[i] + loc[i].H + VertSkin);
        }
        int shift = Margin - minRow;
        for (int i = 0; i < roomCount; i++) top[i] += shift;

        int rawW = x + SideSkin + Margin;
        int W = (rawW + 15) / 16 * 16, H = (maxRow + shift + Margin + 15) / 16 * 16;
        var g = new char[W, H];
        for (int a = 0; a < W; a++) for (int b = 0; b < H; b++) g[a, b] = Hard;

        // ── Stone skin around each room, then the room's own grid ──
        for (int i = 0; i < roomCount; i++)
        {
            var L = loc[i];
            for (int a = x0[i] - SideSkin; a < x0[i] + L.W + SideSkin; a++)
                for (int b = top[i] - VertSkin; b < top[i] + L.H + VertSkin; b++)
                    g[a, b] = Stone;
            for (int a = 0; a < L.W; a++)
                for (int b = 0; b < L.H; b++)
                    g[x0[i] + a, top[i] + b] = L.G[a, b];
        }

        // ── Tunnels + doors ──
        var doors = new List<Point>[roomCount];
        for (int i = 0; i < roomCount; i++) doors[i] = new List<Point>();
        for (int i = 0; i < roomCount - 1; i++)
        {
            int L = tun[i], tx0 = x0[i] + loc[i].W;
            int f0 = top[i] + loc[i].ExitFloor, f1 = top[i + 1] + loc[i + 1].EntryFloor;
            int d = f1 - f0, sgn = System.Math.Sign(d), ad = System.Math.Abs(d);
            for (int c = 0; c < L; c++)
            {
                int f = f0 + sgn * System.Math.Min(c, ad);
                for (int b = f - 4; b < f; b++) g[tx0 + c, b] = Air;
            }
            for (int c = L / 2 - 1; c <= L / 2; c++)
            {
                int f = f0 + sgn * System.Math.Min(c, ad);
                for (int b = f - 4; b < f; b++) { g[tx0 + c, b] = Hard; doors[i].Add(new Point(tx0 + c, b)); }
            }
        }

        // ── Rooms, spots ──
        var map = new DepthsMap();
        int T = Chunk.TileSize;
        for (int i = 0; i < roomCount; i++)
        {
            var L = loc[i];
            var r = new DepthsRoom
            {
                Index = i, Shape = shp[i], Size = SizeClass(shp[i]),
                FloorRow = top[i] + L.H,
                EntryFloorRow = top[i] + L.EntryFloor, ExitFloorRow = top[i] + L.ExitFloor,
                Interior = new Rectangle(x0[i], top[i], L.W, L.H),
                ExitDoor = doors[i],
            };
            r.Center = new Vector2((r.Interior.X + L.W * 0.5f) * T, (r.Interior.Y + L.H * 0.5f) * T);
            r.Entry  = new Vector2((x0[i] + 2) * T + T * 0.5f, r.EntryFloorRow * T - 16f);
            r.Entry2 = CompanionSpot(g, r);
            Spots(g, r);
            map.Rooms.Add(r);
        }
        map.PlayerSpawn = map.Rooms[0].Entry;
        map.SecondSpawn = map.Rooms[0].Entry2;
        map.Grid = g;
        map.Width = W; map.Height = H;

        map.Chunks = MakeChunks(g, W, H);
        return map;
    }

    // A FRESH ChunkMap from the grid. Chunks are mutated by play (and F5 reloads), so every
    // Simulation gets its own; the grid itself is never written after Build.
    public static ChunkMap MakeChunks(DepthsMap m) => MakeChunks(m.Grid, m.Width, m.Height);

    private static ChunkMap MakeChunks(char[,] g, int W, int H)
    {
        var chunks = new ChunkMap();
        for (int cx = 0; cx < W / 16; cx++)
            for (int cy = 0; cy < H / 16; cy++)
            {
                var ch = new Chunk { ChunkPos = new Point(cx, cy) };
                for (int lx = 0; lx < 16; lx++)
                    for (int ly = 0; ly < 16; ly++)
                    {
                        char c = g[cx * 16 + lx, cy * 16 + ly];
                        if (c == Air) continue;
                        ch.Tiles[lx, ly].IsSolid = true;
                        ch.Tiles[lx, ly].Type = c == Hard ? TileType.Hardened : TileType.Stone;
                    }
                chunks[ch.ChunkPos] = ch;
            }
        return chunks;
    }

    // First standing spot 2..8 tiles right of the room's Entry on the entry floor: solid
    // underfoot, clear for a radius-16 body. Falls back to Entry.
    private static Vector2 CompanionSpot(char[,] g, DepthsRoom r)
    {
        int T = Chunk.TileSize;
        int fr = r.EntryFloorRow;
        for (int dx = 2; dx <= 8; dx++)
        {
            float x = r.Entry.X + dx * T, y = fr * T - 16f;
            int tx = (int)System.MathF.Floor(x / T);
            if (tx + 1 >= g.GetLength(0) || fr >= g.GetLength(1)) break;
            if (g[tx, fr] != Air && ClearBox(g, x, y, 16f)) return new Vector2(x, y);
        }
        return r.Entry;
    }

    // Debug dump: one char per tile, '.' air, 'X' stone, 'H' hardened.
    public static string ToAscii(DepthsMap m, int x0 = 0, int x1 = -1)
    {
        if (x1 < 0) x1 = m.Width;
        var sb = new System.Text.StringBuilder();
        for (int b = 0; b < m.Height; b++)
        {
            for (int a = x0; a < x1; a++)
                sb.Append(m.Chunks.GetCellState(a, b) != TileState.Solid ? '.'
                    : m.Chunks.GetCellType(a, b) == TileType.Hardened ? 'H' : 'X');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // ── Route check ─────────────────────────────────────────────────────────
    // Standing cells (feet cell air, two air cells above it, solid below) reachable from
    // `start` by walking, stepping up one, falling, and jumping up to JumpRise rows and
    // JumpRun columns with a clear arc. Deliberately a little generous (the body is
    // treated as one column wide): players can also dig and build, so this guards the
    // route against the generator, not against the player.
    public static HashSet<Point> Reach(System.Func<int, int, bool> solid, Point start, Rectangle bounds)
    {
        bool Air3(int x, int y) => !solid(x, y) && !solid(x, y - 1) && !solid(x, y - 2);
        bool Stand(int x, int y) => bounds.Contains(x, y) && Air3(x, y) && solid(x, y + 1);
        var seen = new HashSet<Point>();
        var q = new Queue<Point>();
        int sy = start.Y;
        while (sy < bounds.Bottom && !solid(start.X, sy + 1)) sy++;
        if (!Stand(start.X, sy)) return seen;
        seen.Add(new Point(start.X, sy)); q.Enqueue(new Point(start.X, sy));
        void Add(int x, int y) { var p = new Point(x, y); if (seen.Add(p)) q.Enqueue(p); }
        while (q.Count > 0)
        {
            var p = q.Dequeue();
            int x = p.X, y = p.Y;
            for (int s = -1; s <= 1; s += 2)
            {
                int nx = x + s;
                if (Stand(nx, y)) Add(nx, y);
                else if (Air3(nx, y) && bounds.Contains(nx, y))
                {
                    int yy = y;
                    while (yy < bounds.Bottom && !solid(nx, yy + 1)) yy++;
                    if (Stand(nx, yy)) Add(nx, yy);
                }
                if (Stand(nx, y - 1) && !solid(x, y - 3)) Add(nx, y - 1);
            }
            for (int dy = 1; dy <= JumpRise; dy++)
            {
                if (solid(x, y - dy - 2)) break;   // takeoff column blocked from here up
                int ty = y - dy;
                for (int dx = -JumpRun; dx <= JumpRun; dx++)
                {
                    if (dx == 0 || !Stand(x + dx, ty)) continue;
                    bool ok = true;
                    for (int c = x + System.Math.Sign(dx); ok && c != x + dx + System.Math.Sign(dx); c += System.Math.Sign(dx))
                        ok = Air3(c, ty);
                    if (ok) Add(x + dx, ty);
                }
            }
        }
        return seen;
    }

    // ── Local room grid ─────────────────────────────────────────────────────
    // Rows 0..H-1 are the interior; row H is the floor beneath (outside the grid, solid).
    // EntryFloor/ExitFloor are the solid rows under the left/right openings (H = main floor).
    private sealed class Local
    {
        public readonly int W, H;
        public int EntryFloor, ExitFloor;
        public readonly char[,] G;
        public Local(int w, int h)
        {
            W = w; H = h; EntryFloor = ExitFloor = h; G = new char[w, h];
            for (int a = 0; a < w; a++) for (int b = 0; b < h; b++) G[a, b] = Stone;
        }
        public bool In(int x, int y) => x >= 0 && x < W && y >= 0 && y < H;
        public bool Solid(int x, int y) => !In(x, y) || G[x, y] != Air;
        public bool IsAir(int x, int y) => In(x, y) && G[x, y] == Air;
        public void Set(int x, int y, char c) { if (In(x, y)) G[x, y] = c; }
        // Inclusive rectangle.
        public void Fill(int xa, int ya, int xb, int yb, char c)
        {
            for (int a = System.Math.Min(xa, xb); a <= System.Math.Max(xa, xb); a++)
                for (int b = System.Math.Min(ya, yb); b <= System.Math.Max(ya, yb); b++) Set(a, b, c);
        }
        public bool AllAir(int xa, int ya, int xb, int yb)
        {
            for (int a = xa; a <= xb; a++) for (int b = ya; b <= yb; b++) if (!IsAir(a, b)) return false;
            return true;
        }
        // Air rows directly below (dir +1) or above (dir -1) the cell, not counting it.
        public int Run(int x, int y, int dir)
        {
            int n = 0;
            for (int b = y + dir; IsAir(x, b); b += dir) n++;
            return n;
        }
    }

    private static Local MakeRoom(DepthsShape shape, ulong seed)
    {
        Local last = null;
        for (int attempt = 0; attempt < 16; attempt++)
        {
            var rng = new Rng(seed + (ulong)attempt * 0xD1B54A32D192ED03UL);
            var L = shape switch
            {
                DepthsShape.Small    => Hall(ref rng, 0),
                DepthsShape.Medium   => Hall(ref rng, 1),
                DepthsShape.Large    => Hall(ref rng, 2),
                DepthsShape.Shaft    => Shaft(ref rng),
                DepthsShape.Massive  => Massive(ref rng),
                DepthsShape.Gallery  => Gallery(ref rng),
                DepthsShape.Dome     => Dome(ref rng),
                DepthsShape.Terraces => Terraces(ref rng),
                _                    => Chasm(ref rng),
            };
            Finish(L);
            last = L;
            var reach = Reach(L.Solid, new Point(1, L.EntryFloor - 1), new Rectangle(0, 0, L.W, L.H));
            if (reach.Contains(new Point(L.W - 2, L.ExitFloor - 1))) return L;
        }
        return last;
    }

    // Openings, then remove the two kinds of space nobody can use: slots under 3 tiles
    // tall (a body can't stand in them; it just snags) and air pockets sealed off from
    // the entrance (spawn spots and the map-enclosure check both assume one open region).
    private static void Finish(Local L)
    {
        void Open(int xa, int xb, int floor)
        {
            L.Fill(xa, floor - 4, xb, floor - 1, Air);
            if (floor < L.H) L.Fill(xa, floor, xb, floor, Stone);
        }
        Open(0, 3, L.EntryFloor);
        Open(L.W - 4, L.W - 1, L.ExitFloor);

        for (int a = 0; a < L.W; a++)
        {
            int b = 0;
            while (b < L.H)
            {
                if (!L.IsAir(a, b)) { b++; continue; }
                int s = b;
                while (b < L.H && L.IsAir(a, b)) b++;
                if (b - s <= 2) L.Fill(a, s, a, b - 1, Stone);
            }
        }
        Open(0, 3, L.EntryFloor);
        Open(L.W - 4, L.W - 1, L.ExitFloor);

        var seen = new bool[L.W, L.H];
        var q = new Queue<Point>();
        var s0 = new Point(1, L.EntryFloor - 1);
        seen[s0.X, s0.Y] = true; q.Enqueue(s0);
        while (q.Count > 0)
        {
            var p = q.Dequeue();
            foreach (var d in new[] { new Point(1, 0), new Point(-1, 0), new Point(0, 1), new Point(0, -1) })
            {
                int nx = p.X + d.X, ny = p.Y + d.Y;
                if (!L.IsAir(nx, ny) || seen[nx, ny]) continue;
                seen[nx, ny] = true; q.Enqueue(new Point(nx, ny));
            }
        }
        for (int a = 0; a < L.W; a++) for (int b = 0; b < L.H; b++)
            if (L.G[a, b] == Air && !seen[a, b]) L.G[a, b] = Stone;
    }

    // ── Decoration ──────────────────────────────────────────────────────────
    // Tapered spike: root at (c, rootRow) growing toward dir (+1 down, -1 up).
    private static void Spike(Local L, int c, int rootRow, int dir, int len, int rootW)
    {
        for (int k = 0; k < len; k++)
        {
            int ww = System.Math.Max(1, (int)System.MathF.Round(rootW * (1f - k / (float)len)));
            int l = c - (ww - 1) / 2;
            for (int j = 0; j < ww; j++) if (L.IsAir(l + j, rootRow + dir * k)) L.G[l + j, rootRow + dir * k] = Stone;
        }
    }

    // Stalactites hang from any rock overhead, stalagmites rise from any floor. Each keeps
    // at least 4 tiles of headroom in its column, and none sits within 5 columns of a wall
    // opening.
    private static void Decorate(Local L, ref Rng rng, int nStalac, int nMite, int maxStalac = 6, int maxMite = 3)
    {
        var usedC = new List<int>(); var usedF = new List<int>();
        bool Near(List<int> l, int c, int gap) { foreach (var v in l) if (System.Math.Abs(v - c) < gap) return true; return false; }
        for (int n = 0, tries = 0; n < nStalac && tries < nStalac * 12; tries++)
        {
            int c = rng.Range(5, L.W - 6), y = rng.Range(0, L.H - 1);
            if (!L.IsAir(c, y) || !L.Solid(c, y - 1) || Near(usedC, c, 4)) continue;
            int room = L.Run(c, y, +1) + 1;   // air from here down to the next rock
            int len = System.Math.Min(rng.Range(2, maxStalac), room - 5);
            if (len < 1) continue;
            Spike(L, c, y, +1, len, rng.Range(1, 3));
            usedC.Add(c); n++;
        }
        for (int n = 0, tries = 0; n < nMite && tries < nMite * 12; tries++)
        {
            int c = rng.Range(5, L.W - 6), y = rng.Range(0, L.H - 1);
            if (!L.IsAir(c, y) || !L.Solid(c, y + 1) || Near(usedF, c, 5)) continue;
            int room = L.Run(c, y, -1) + 1;
            int len = System.Math.Min(rng.Range(1, maxMite), room - 5);
            if (len < 1) continue;
            Spike(L, c, y, -1, len, rng.Range(len >= 3 ? 2 : 1, 3));
            usedF.Add(c); n++;
        }
    }

    // A chain of 1-thick platforms climbing JumpRise (sometimes 2) rows per step, zigzagging
    // within columns [lo, hi]. Consecutive platforms never overlap horizontally, so each has
    // clear headroom. Starts above the interval [a, b] on row `row`; stops once at or above
    // `stopRow`. Returns the last platform (a, b, row).
    private static (int a, int b, int row) Zigzag(Local L, ref Rng rng, int lo, int hi, int a, int b, int row,
                                                  int stopRow, int minW = 4, int maxW = 7)
    {
        // Gaps are 1-2 columns, and one wider where the chain turns back at a wall: the
        // platform after a turn sits above the one two steps down, and the wider gap keeps
        // it clear of the arc of the jump from there. A platform that would overrun a wall
        // is shrunk to fit first, so two turns never come back to back.
        int dir = +1, prevGap = 1;
        for (int guard = 0; guard < 64; guard++)
        {
            if (row <= stopRow || row - JumpRise < 2) break;
            int nr = row - JumpRise, pw = rng.Range(minW, maxW), gap = rng.Range(1, 2);
            int na, nb;
            if (dir > 0) { na = b + 1 + gap; nb = na + pw - 1; }
            else         { nb = a - 1 - gap; na = nb - pw + 1; }
            // Short of the wall: shrink to fit and keep going; the turn comes next step.
            if (dir > 0 && nb > hi && hi - na + 1 >= 3) nb = hi;
            if (dir < 0 && na < lo && nb - lo + 1 >= 3) na = lo;
            if (na < lo || nb > hi)
            {
                dir = -dir; gap = System.Math.Min(JumpRun - 1, prevGap + 1);
                if (dir > 0) { na = b + 1 + gap; nb = System.Math.Min(hi, na + pw - 1); }
                else         { nb = a - 1 - gap; na = System.Math.Max(lo, nb - pw + 1); }
            }
            if (nb - na + 1 < 3) break;
            L.Fill(na, nr, nb, nr, Stone);
            a = na; b = nb; row = nr; prevGap = gap;
        }
        return (a, b, row);
    }

    // ── Shapes ──────────────────────────────────────────────────────────────
    // Small / Medium / Large: the original rectangular halls.
    private static Local Hall(ref Rng rng, int cls)
    {
        int w, h;
        switch (cls)
        {
            case 0:  w = rng.Range(28, 32); h = rng.Range(11, 13); break;
            case 1:  w = rng.Range(40, 44); h = rng.Range(15, 17); break;
            default: w = rng.Range(56, 62); h = rng.Range(20, 23); break;
        }
        var L = new Local(w, h);
        L.Fill(0, 0, w - 1, h - 1, Air);
        const int clear = 4;
        void Set(int lx, int ly) { if (lx >= clear && lx < w - clear && ly >= 0 && ly < h) L.G[lx, ly] = Stone; }
        var floorX = new List<int>(); var ceilX = new List<int>();
        bool Near(List<int> l, int c, int gap) { foreach (var v in l) if (System.Math.Abs(v - c) < gap) return true; return false; }
        var r = rng;
        int PickX(List<int> used, int gap, int margin = 0)
        {
            for (int t = 0; t < 20; t++)
            {
                int c = r.Range(clear + 2 + margin, w - clear - 3 - margin);
                if (!Near(used, c, gap)) { used.Add(c); return c; }
            }
            return -1;
        }
        int nStalac = cls == 0 ? r.Range(3, 4) : cls == 1 ? r.Range(5, 7) : r.Range(6, 9);
        for (int n = 0; n < nStalac; n++)
        {
            int c = PickX(ceilX, 4); if (c < 0) continue;
            Spike(L, c, 0, +1, r.Range(2, System.Math.Min(6, h / 3)), r.Range(1, 3));
        }
        int nMite = cls == 0 ? r.Range(2, 3) : cls == 1 ? r.Range(3, 5) : r.Range(3, 4);
        for (int n = 0; n < nMite; n++)
        {
            int c = PickX(floorX, 5); if (c < 0) continue;
            int len = r.Range(1, 3);
            Spike(L, c, h - 1, -1, len, r.Range(len >= 3 ? 2 : 1, 3));
        }
        // Partial pillars from the floor (<= 5 high so the floor stays traversable by jumping).
        int nPillar = cls == 0 ? r.Range(0, 1) : r.Range(1, 2);
        for (int n = 0; n < nPillar; n++)
        {
            int c = PickX(floorX, 8, 2); if (c < 0) continue;
            int pw = r.Range(2, 3), ph = r.Range(3, System.Math.Min(5, h - 6));
            // Taller than a jump: give it a step block beside it.
            for (int j = 0; j < pw; j++) for (int k = 0; k < ph; k++) Set(c + j, h - 1 - k);
            if (ph > JumpRise) for (int k = 0; k < JumpRise; k++) Set(c - 1, h - 1 - k);
        }
        // Full floor-to-ceiling pillar with a 4-tall doorway at floor level (never seals the room).
        if (cls >= 1 && r.Chance(cls == 1 ? 60 : 40))
        {
            int c = PickX(floorX, 10, 4);
            if (c >= 0)
            {
                ceilX.Add(c);
                int pw = r.Range(2, 3);
                for (int j = 0; j < pw; j++) for (int k = 0; k < h - 4; k++) Set(c + j, k);
            }
        }
        // Ledges in large rooms, each with a step block under one end so it can be climbed.
        if (cls == 2)
        {
            int nLedge = r.Range(2, 3);
            for (int n = 0; n < nLedge; n++)
            {
                int lw = r.Range(5, 9), c = r.Range(clear + 3, w - clear - 3 - lw), row = h - 1 - 2 * JumpRise;
                if (!L.AllAir(c - 1, row - 4, c + lw, h - 1)) continue;
                for (int j = 0; j < lw; j++) Set(c + j, row);
                int sb = r.Chance(50) ? c - 3 : c + lw + 1;   // the step block, off one end
                for (int j = 0; j < 2; j++) for (int k = 0; k < JumpRise; k++) Set(sb + j, h - 1 - k);
            }
        }
        rng = r;
        return L;
    }

    // Tall and narrow: enter at the bottom left, climb a zigzag of platforms, leave across a
    // shelf on the right wall near the top.
    private static Local Shaft(ref Rng rng)
    {
        int w = rng.Range(20, 24), h = rng.Range(46, 56);
        var L = new Local(w, h);
        L.Fill(0, 0, w - 1, h - 1, Air);
        int stop = rng.Range(10, 12);
        var last = Zigzag(L, ref rng, 1, w - 2, 0, 5, h, stop);
        if (last.b >= w - 8)
        {
            // Finished on the right: run the top platform out to the wall.
            L.Fill(last.b, last.row, w - 1, last.row, Stone);
            L.ExitFloor = last.row;
        }
        else
        {
            // Finished left of centre: a shelf one jump up, from beside it to the wall.
            L.ExitFloor = last.row - JumpRise;
            L.Fill(last.b + 1 + rng.Range(1, 2), L.ExitFloor, w - 1, L.ExitFloor, Stone);
        }
        // Little wall knobs for wall jumps and texture.
        for (int n = rng.Range(3, 6); n > 0; n--)
        {
            int y = rng.Range(8, h - 6), side = rng.Chance(50) ? 0 : w - 1;
            if (L.AllAir(side == 0 ? 0 : w - 3, y - 4, side == 0 ? 2 : w - 1, y + 4)) L.Set(side, y, Stone);
        }
        Decorate(L, ref rng, rng.Range(2, 4), 1, maxStalac: 4, maxMite: 2);
        return L;
    }

    // Long and low, ceiling sagging and toothy: a corridor fight with no room to jump over anyone.
    private static Local Gallery(ref Rng rng)
    {
        int w = rng.Range(64, 80), h = rng.Range(8, 10);
        var L = new Local(w, h);
        L.Fill(0, 0, w - 1, h - 1, Air);
        int sag = 0;
        for (int a = 5; a < w - 5; a++)
        {
            if (rng.Chance(30)) sag = System.Math.Clamp(sag + rng.Range(-1, 1), 0, System.Math.Min(3, h - 7));
            if (sag > 0) L.Fill(a, 0, a, sag - 1, Stone);
        }
        // A couple of one-step humps in the floor.
        for (int n = rng.Range(1, 3); n > 0; n--)
        {
            int a = rng.Range(8, w - 16), len = rng.Range(4, 8);
            L.Fill(a, h - 1, a + len, h - 1, Stone);
        }
        Decorate(L, ref rng, w / 6, w / 10, maxStalac: 3, maxMite: 2);
        return L;
    }

    // Terraced floor stepping up toward the exit, each terrace with a step block and a
    // ledge above it.
    private static Local Terraces(ref Rng rng)
    {
        int n = rng.Range(3, 4), clearance = rng.Range(14, 17);
        var tw = new int[n]; int w = 0;
        for (int i = 0; i < n; i++) { tw[i] = rng.Range(14, 18); w += tw[i]; }
        int h = JumpRise * (n - 1) + clearance;
        var L = new Local(w, h);
        int xa = 0;
        for (int i = 0; i < n; i++)
        {
            int surf = h - JumpRise * i;
            for (int a = xa; a < xa + tw[i]; a++)
            {
                int ceil = System.Math.Max(0, surf - clearance + rng.Range(0, 2));
                L.Fill(a, ceil, a, surf - 1, Air);
            }
            // Step block (3 tall) and a ledge 3 above it, inside the terrace.
            if (tw[i] >= 14)
            {
                int bx = xa + rng.Range(3, 5), lw = rng.Range(4, 5);
                L.Fill(bx, surf - JumpRise, bx + 1, surf - 1, Stone);
                L.Fill(bx + 3, surf - 2 * JumpRise, System.Math.Min(xa + tw[i] - 4, bx + 3 + lw), surf - 2 * JumpRise, Stone);
            }
            xa += tw[i];
        }
        L.ExitFloor = h - JumpRise * (n - 1);
        Decorate(L, ref rng, w / 8, w / 16, maxStalac: 5, maxMite: 2);
        return L;
    }

    // High shelves either side of a pit, a broken bridge of floating platforms across it,
    // and a stair cut into the far shelf for whoever falls in.
    private static Local Chasm(ref Rng rng)
    {
        int w = rng.Range(52, 64), h = rng.Range(26, 30);
        int shelf = h - rng.Range(11, 14), ls = rng.Range(10, 12), rs = rng.Range(13, 15);
        var L = new Local(w, h);
        L.Fill(0, 0, w - 1, h - 1, Air);
        L.Fill(0, shelf, ls - 1, h - 1, Stone);
        L.Fill(w - rs, shelf, w - 1, h - 1, Stone);
        int k = 1;
        for (; h - JumpRise * k > shelf; k++)
        {
            int ca = w - rs + 2 * (k - 1);
            L.Fill(ca, shelf, ca + 1, h - JumpRise * k - 1, Air);
        }
        int shelfTopX = w - rs + 2 * (k - 1);
        int cx = ls, row = shelf;
        for (int guard = 0; guard < 32 && shelfTopX - cx > JumpRun - 1; guard++)
        {
            int a = cx + rng.Range(2, 3), pw = rng.Range(3, 5);
            if (a + pw >= shelfTopX - 1) pw = System.Math.Max(2, shelfTopX - 2 - a);
            int nr = System.Math.Clamp(row + rng.Range(-2, 2), shelf - 3, shelf + 3);
            L.Fill(a, nr, a + pw - 1, nr, Stone);
            cx = a + pw; row = nr;
        }
        L.EntryFloor = L.ExitFloor = shelf;
        Decorate(L, ref rng, w / 7, w / 12, maxStalac: 6, maxMite: 3);
        return L;
    }

    // A tall arched cavern: a level floor band under a half-ellipse dome, a ledge stair up
    // each side and a great stalactite in the middle.
    private static Local Dome(ref Rng rng)
    {
        int w = rng.Range(48, 58), h = rng.Range(24, 30);
        var L = new Local(w, h);
        L.Fill(0, h - 5, w - 1, h - 1, Air);
        float hw = w * 0.5f;
        for (int a = 0; a < w; a++)
        {
            float nx = (a + 0.5f - hw) / hw, jitter = rng.Range(-1, 1);
            float ny = System.MathF.Sqrt(System.MathF.Max(0f, 1f - nx * nx));
            int ceil = System.Math.Clamp((int)System.MathF.Round(h - ny * h + jitter), 0, h - 5);
            L.Fill(a, ceil, a, h - 1, Air);
        }
        // Side stairs: ledges stepping up toward the centre from both walls.
        for (int side = 0; side < 2; side++)
        {
            int x = 5;
            for (int step = 1; step <= 3; step++)
            {
                int pw = rng.Range(4, 5), row = h - JumpRise * step;
                int a = side == 0 ? x : w - 1 - x - (pw - 1);
                L.Fill(a, row, a + pw - 1, row, Stone);
                x += pw + rng.Range(1, 2);
            }
        }
        // The great stalactite.
        int c = w / 2 + rng.Range(-3, 3), top = 0;
        while (top < h && !L.IsAir(c, top)) top++;
        Spike(L, c, top, +1, System.Math.Max(2, (h - top) - 14), rng.Range(4, 6));
        Decorate(L, ref rng, w / 8, w / 14, maxStalac: 5, maxMite: 2);
        return L;
    }

    // A huge cavern: enter on a high shelf at the left and take a stair down; the floor
    // rolls, the ceiling hangs low in places, ledge towers climb toward the roof and a few
    // great pillars stand on arches.
    private static Local Massive(ref Rng rng)
    {
        int w = rng.Range(110, 140), h = rng.Range(34, 42);
        var L = new Local(w, h);
        L.Fill(0, 0, w - 1, h - 1, Air);

        // Ceiling sag and rolling floor.
        int sag = rng.Range(0, 3), hump = 0;
        for (int a = 0; a < w; a++)
        {
            if (rng.Chance(35)) sag = System.Math.Clamp(sag + rng.Range(-1, 1), 0, 7);
            if (sag > 0) L.Fill(a, 0, a, sag - 1, Stone);
            bool flat = a < 20 || a >= w - 8;
            if (rng.Chance(20)) hump = System.Math.Clamp(hump + rng.Range(-1, 1), 0, 2);
            int f = flat ? 0 : hump;
            if (f > 0) L.Fill(a, h - f, a, h - 1, Stone);
        }

        // Entry shelf with a stair down its right face.
        int es = h - rng.Range(15, 19), sw = 16;
        L.Fill(0, es, sw - 1, h - 1, Stone);
        int steps = (h - es) / JumpRise - 1;
        for (int j = 1; j <= steps; j++)
        {
            int ca = sw - 2 * (steps - j + 1);
            L.Fill(ca, es, ca + 1, es + JumpRise * j - 1, Air);
        }
        L.EntryFloor = es;

        // Ledge towers: zigzags climbing from the floor toward the roof.
        int towers = w / 32, span = (w - 26) / System.Math.Max(1, towers);
        var towerBands = new List<(int lo, int hi)>();
        for (int t = 0; t < towers; t++)
        {
            int lo = 22 + t * span, hi = System.Math.Min(w - 10, lo + rng.Range(18, 24));
            int baseA = lo + rng.Range(0, 3);
            Zigzag(L, ref rng, lo, hi, baseA, baseA + 4, h, rng.Range(9, 14), minW: 5, maxW: 9);
            towerBands.Add((lo, hi));
        }

        // Arched pillars in the gaps between towers.
        for (int t = 0; t + 1 < towerBands.Count; t++)
        {
            int ga = towerBands[t].hi + 2, gb = towerBands[t + 1].lo - 2;
            int pw = rng.Range(3, 5);
            if (gb - ga < pw + 2 || !rng.Chance(70)) continue;
            int pa = rng.Range(ga, gb - pw);
            for (int a = pa; a < pa + pw; a++)
            {
                int floorTop = h; while (floorTop > 0 && L.Solid(a, floorTop - 1)) floorTop--;
                L.Fill(a, 0, a, floorTop - 6, Stone);   // 5-tall arch under it
            }
        }

        Decorate(L, ref rng, w / 6, w / 12, maxStalac: 8, maxMite: 3);
        return L;
    }

    // ── Spots, computed against the final grid ──
    private static bool ClearBox(char[,] g, float cx, float cy, float r)
    {
        int T = Chunk.TileSize;
        int a0 = (int)System.MathF.Floor((cx - r) / T), a1 = (int)System.MathF.Ceiling((cx + r) / T) - 1;
        int b0 = (int)System.MathF.Floor((cy - r) / T), b1 = (int)System.MathF.Ceiling((cy + r) / T) - 1;
        for (int a = a0; a <= a1; a++) for (int b = b0; b <= b1; b++)
            if (a < 0 || b < 0 || a >= g.GetLength(0) || b >= g.GetLength(1) || g[a, b] != Air) return false;
        return true;
    }

    private static void Spots(char[,] g, DepthsRoom r)
    {
        int T = Chunk.TileSize;
        var I = r.Interior;
        bool Solid(int a, int b) => a < 0 || b < 0 || a >= g.GetLength(0) || b >= g.GetLength(1) || g[a, b] != Air;
        bool Far(Vector2 p) => Vector2.Distance(p, r.Entry) >= 6 * T;
        var reach = Reach(Solid, new Point(I.Left + 1, r.EntryFloorRow - 1), I);

        // Bigger rooms get more anchors so a wave spreads across them.
        int scale = System.Math.Clamp(I.Width * I.Height / 700, 1, 3);
        List<Vector2> Pick(List<Vector2> cand, int max)
        {
            max *= scale;
            cand.Sort((p, q) => p.X != q.X ? p.X.CompareTo(q.X) : p.Y.CompareTo(q.Y));
            if (cand.Count <= max) return cand;
            var res = new List<Vector2>(max);
            for (int k = 0; k < max; k++) res.Add(cand[(int)((long)k * cand.Count / max)]);
            return res;
        }
        float Cx(int tx) => tx * T + T * 0.5f;

        var floorC = new List<Vector2>(); var bossC = new List<Vector2>();
        var ceilC = new List<Vector2>(); var airC = new List<Vector2>(); var wallC = new List<Vector2>();
        for (int tx = I.Left; tx < I.Right; tx++)
        {
            for (int ty = I.Top; ty < I.Bottom; ty++)
            {
                if (Solid(tx, ty)) continue;
                // Standing surface: solid below, reachable on foot.
                if (Solid(tx, ty + 1) && reach.Contains(new Point(tx, ty)))
                    for (int half = 0; half < 2; half++)   // half-tile steps: stretches are narrow
                    {
                        float sx = Cx(tx) + half * T * 0.5f, surf = (ty + 1) * T;
                        var f = new Vector2(sx, surf - 16f);
                        if (Far(f) && ClearBox(g, f.X, f.Y, 16f)) floorC.Add(f);
                        var bs = new Vector2(sx, surf - 26f);
                        if (r.Size == 2 && Far(bs) && ClearBox(g, bs.X, bs.Y, 26f)) bossC.Add(bs);
                    }
                // Under rock.
                if (Solid(tx, ty - 1))
                {
                    var c = new Vector2(Cx(tx), ty * T + 16f);
                    if (Far(c) && ClearBox(g, c.X, c.Y, 16f)) ceilC.Add(c);
                }
            }
            for (int row = I.Top + 3; row < I.Bottom - 3; row += 2)
            {
                var a = new Vector2(Cx(tx), row * T + T * 0.5f);
                if (Far(a) && ClearBox(g, a.X, a.Y, 16f)) airC.Add(a);
            }
        }
        for (int row = I.Top + 1; row < I.Bottom - 1; row++)
        {
            float cy = row * T + T * 0.5f;
            for (int tx = I.Left; tx < I.Right; tx++)
            {
                if (Solid(tx, row)) continue;
                if (Solid(tx - 1, row)) { var l = new Vector2(tx * T + 16f, cy); if (Far(l) && ClearBox(g, l.X, l.Y, 16f)) wallC.Add(l); }
                if (Solid(tx + 1, row)) { var q = new Vector2((tx + 1) * T - 16f, cy); if (Far(q) && ClearBox(g, q.X, q.Y, 16f)) wallC.Add(q); }
            }
        }
        r.FloorSpots   = Pick(floorC, 16);
        r.BossSpots    = Pick(bossC, 6);
        r.CeilingSpots = Pick(ceilC, 14);
        r.AirSpots     = Pick(airC, 16);
        r.WallSpots    = Pick(wallC, 12);
    }
}
