using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

public class DepthsLayoutTests(ITestOutputHelper output)
{
    private const int T = Chunk.TileSize;

    private static bool Solid(DepthsMap m, int x, int y) => m.Chunks.GetCellState(x, y) == TileState.Solid;

    private static bool BoxClear(DepthsMap m, Vector2 p, float r)
    {
        for (int a = (int)System.MathF.Floor((p.X - r) / T); a <= (int)System.MathF.Ceiling((p.X + r) / T) - 1; a++)
            for (int b = (int)System.MathF.Floor((p.Y - r) / T); b <= (int)System.MathF.Ceiling((p.Y + r) / T) - 1; b++)
                if (Solid(m, a, b)) return false;
        return true;
    }

    private static HashSet<Point> Flood(DepthsMap m, Vector2 start)
    {
        var seen = new HashSet<Point>(); var q = new Queue<Point>();
        var s = new Point((int)System.MathF.Floor(start.X / T), (int)System.MathF.Floor(start.Y / T));
        q.Enqueue(s); seen.Add(s);
        while (q.Count > 0)
        {
            var p = q.Dequeue();
            foreach (var d in new[] { new Point(1, 0), new Point(-1, 0), new Point(0, 1), new Point(0, -1) })
            {
                var n = new Point(p.X + d.X, p.Y + d.Y);
                if (n.X < 0 || n.Y < 0 || n.X >= m.Width || n.Y >= m.Height) continue;
                if (Solid(m, n.X, n.Y) || !seen.Add(n)) continue;
                q.Enqueue(n);
            }
        }
        return seen;
    }

    private static bool Reaches(HashSet<Point> f, Vector2 p) => f.Contains(new Point((int)System.MathF.Floor(p.X / T), (int)System.MathF.Floor(p.Y / T)));

    [Fact]
    public void SameSeedIsIdentical_DifferentSeedDiffers()
    {
        var a = DepthsLayout.Build(7, 6); var b = DepthsLayout.Build(7, 6); var c = DepthsLayout.Build(8, 6);
        Assert.Equal(DepthsLayout.ToAscii(a), DepthsLayout.ToAscii(b));
        Assert.NotEqual(DepthsLayout.ToAscii(a), DepthsLayout.ToAscii(c));
        for (int i = 0; i < a.Rooms.Count; i++)
        {
            Assert.Equal(a.Rooms[i].FloorSpots, b.Rooms[i].FloorSpots);
            Assert.Equal(a.Rooms[i].Interior, b.Rooms[i].Interior);
        }
    }

    [Theory, InlineData(1), InlineData(2), InlineData(3), InlineData(99)]
    public void SpotsAreClearAndInsideInterior(int seed)
    {
        var m = DepthsLayout.Build(seed, 7);
        foreach (var r in m.Rooms)
        {
            var box = new Rectangle(r.Interior.X * T, r.Interior.Y * T, r.Interior.Width * T, r.Interior.Height * T);
            void Check(List<Vector2> l, float rad, string name)
            {
                foreach (var p in l)
                {
                    Assert.True(BoxClear(m, p, rad), $"room {r.Index} {name} {p} not clear r={rad}");
                    Assert.True(box.Contains(p), $"room {r.Index} {name} {p} outside interior");
                    Assert.True(Vector2.Distance(p, r.Entry) >= 6 * T - 0.01f, $"room {r.Index} {name} {p} near entry");
                }
            }
            Check(r.FloorSpots, 16, "floor"); Check(r.CeilingSpots, 16, "ceiling");
            Check(r.WallSpots, 16, "wall"); Check(r.AirSpots, 16, "air"); Check(r.BossSpots, 26, "boss");
            Assert.True(BoxClear(m, r.Entry, 14f));
            Assert.True(r.FloorSpots.Count >= 8, $"room {r.Index} floor spots {r.FloorSpots.Count}");
            Assert.True(r.CeilingSpots.Count >= 8, $"room {r.Index} ceiling spots {r.CeilingSpots.Count}");
            Assert.True(r.AirSpots.Count >= 8, $"room {r.Index} air spots {r.AirSpots.Count}");
            Assert.True(r.WallSpots.Count >= 4, $"room {r.Index} wall spots {r.WallSpots.Count}");
            if (r.Size == 2) Assert.True(r.BossSpots.Count >= 2, $"room {r.Index} boss spots {r.BossSpots.Count}");
        }
    }

    [Theory, InlineData(5), InlineData(11)]
    public void DoorsBlockUntilBroken(int seed)
    {
        var m = DepthsLayout.Build(seed, 6);
        for (int i = 0; i < m.Rooms.Count - 1; i++)
            foreach (var c in m.Rooms[i].ExitDoor)
            {
                Assert.True(Solid(m, c.X, c.Y)); Assert.Equal(TileType.Hardened, m.Chunks.GetCellType(c.X, c.Y));
            }
        Assert.Empty(m.Rooms[^1].ExitDoor);

        // Open one door at a time: reach exactly the rooms up to that door.
        for (int i = 0; i < m.Rooms.Count; i++)
        {
            var f = Flood(m, m.PlayerSpawn);
            for (int j = 0; j < m.Rooms.Count; j++)
                Assert.Equal(j <= i, Reaches(f, m.Rooms[j].Entry));
            foreach (var c in m.Rooms[i].ExitDoor) Assert.True(m.Chunks.BreakCell(c.X, c.Y));
        }
        var all = Flood(m, m.PlayerSpawn);
        foreach (var r in m.Rooms) Assert.True(Reaches(all, r.Entry));
    }

    [Theory, InlineData(5), InlineData(21)]
    public void MapIsEnclosed(int seed)
    {
        var m = DepthsLayout.Build(seed, 6);
        for (int x = 0; x < m.Width; x++) { Assert.True(Solid(m, x, 0)); Assert.True(Solid(m, x, m.Height - 1)); }
        for (int y = 0; y < m.Height; y++) { Assert.True(Solid(m, 0, y)); Assert.True(Solid(m, m.Width - 1, y)); }
        // Every empty cell is inside some room or tunnel region reachable once doors open.
        foreach (var d in m.Rooms) foreach (var c in d.ExitDoor) m.Chunks.BreakCell(c.X, c.Y);
        var f = Flood(m, m.PlayerSpawn);
        for (int x = 0; x < m.Width; x++) for (int y = 0; y < m.Height; y++)
            if (!Solid(m, x, y)) Assert.Contains(new Point(x, y), f);
        // Rock between rooms is Hardened beyond the stone skin: no Stone two columns into a gap away from the room skins.
        for (int i = 0; i < m.Rooms.Count - 1; i++)
        {
            int gapMid = m.Rooms[i].Interior.Right + 2;
            for (int y = 0; y < m.Height; y++)
                if (Solid(m, gapMid, y)) Assert.Equal(TileType.Hardened, m.Chunks.GetCellType(gapMid, y));
        }
    }

    // Every shape, over several seeds: a plain-jump route runs from the entrance to the
    // exit, and walkers' floor spots are on it.
    [Theory, InlineData(1), InlineData(7), InlineData(42), InlineData(1234)]
    public void EveryShape_HasARouteFromEntranceToExit(int seed)
    {
        var shapes = (DepthsShape[])System.Enum.GetValues(typeof(DepthsShape));
        var m = DepthsLayout.Build(seed, shapes);
        bool IsSolid(int x, int y) => Solid(m, x, y);
        foreach (var r in m.Rooms)
        {
            var reach = DepthsLayout.Reach(IsSolid, new Point(r.Interior.Left + 1, r.EntryFloorRow - 1), r.Interior);
            var exit = new Point(r.Interior.Right - 2, r.ExitFloorRow - 1);
            Assert.True(reach.Contains(exit), $"seed {seed} room {r.Index} ({r.Shape}): no route to exit {exit}");
            foreach (var p in r.FloorSpots)
                Assert.Contains(new Point((int)System.MathF.Floor((p.X - T * 0.25f) / T), (int)System.MathF.Floor((p.Y + 16f) / T) - 1), reach);
        }
    }

    // The tall shapes really are tall, the massive one really is massive, and the shaft
    // is left near its top.
    [Fact]
    public void ShapesHaveTheirCharacter()
    {
        var shapes = (DepthsShape[])System.Enum.GetValues(typeof(DepthsShape));
        var m = DepthsLayout.Build(7, shapes);
        DepthsRoom Of(DepthsShape s) => m.Rooms[System.Array.IndexOf(shapes, s)];
        var shaft = Of(DepthsShape.Shaft);
        Assert.True(shaft.Interior.Height >= 40 && shaft.Interior.Width <= 24);
        Assert.True(shaft.EntryFloorRow - shaft.ExitFloorRow >= 30, "a shaft should climb most of its height");
        var massive = Of(DepthsShape.Massive);
        Assert.True(massive.Interior.Width >= 100 && massive.Interior.Height >= 30);
        Assert.True(massive.BossSpots.Count >= 6);
        var gallery = Of(DepthsShape.Gallery);
        Assert.True(gallery.Interior.Width >= 60 && gallery.Interior.Height <= 10);
        var terr = Of(DepthsShape.Terraces);
        Assert.True(terr.EntryFloorRow - terr.ExitFloorRow >= 6);
    }

    [Fact]
    public void DumpAscii()
    {
        var m = DepthsLayout.Build(3, (DepthsShape[])System.Enum.GetValues(typeof(DepthsShape)));
        output.WriteLine($"{m.Width}x{m.Height} tiles");
        foreach (var r in m.Rooms)
            output.WriteLine($"room {r.Index} {r.Shape} interior {r.Interior} floor {r.FloorRow} floorSpots {r.FloorSpots.Count} ceil {r.CeilingSpots.Count} wall {r.WallSpots.Count} air {r.AirSpots.Count} boss {r.BossSpots.Count}");
        output.WriteLine(DepthsLayout.ToAscii(m));
    }
}
