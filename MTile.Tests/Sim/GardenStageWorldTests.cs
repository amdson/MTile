using System;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// The 'garden' stage: adventure's terrain closed by Hardened walls (World/WorldGenerator.cs
// BoundaryHalfWidth, Levels/garden.json).
public class GardenStageWorldTests(ITestOutputHelper output)
{
    private static string Levels()
    {
        for (var d = System.IO.Directory.GetCurrentDirectory(); d != null; d = System.IO.Path.GetDirectoryName(d))
        {
            string c = System.IO.Path.Combine(d, "Levels");
            if (System.IO.File.Exists(System.IO.Path.Combine(c, "garden.json"))) return c;
        }
        throw new System.IO.DirectoryNotFoundException("Levels/garden.json");
    }

    private static WorldGenerator Gen(string file)
    {
        var reg = Stages.Get(file == "garden.json" ? "garden" : "adventure");
        var stage = new Stage { Name = reg.Name, TerrainConfig = System.IO.Path.Combine(Levels(), file),
                                PlayerSpawn = reg.PlayerSpawn, Populate = reg.Populate };
        var sim = new Simulation(new GameConfig(), stage);
        return Assert.IsType<WorldGenerator>(sim.Chunks.Generator);
    }

    private static Chunk Make(WorldGenerator g, int cx, int cy)
    {
        var c = new Chunk { ChunkPos = new Point(cx, cy) };
        g.Generate(c);
        return c;
    }

    private static Tile At(WorldGenerator g, int x, int y)
    {
        int cx = (int)Math.Floor(x / (double)Chunk.Size), cy = (int)Math.Floor(y / (double)Chunk.Size);
        return Make(g, cx, cy).Tiles[x - cx * Chunk.Size, y - cy * Chunk.Size];
    }

    [Fact]
    public void Garden_IsRegistered_WithSpawnsInsideTheArena()
    {
        var s = Stages.Get("garden");
        Assert.Equal("garden", s.Name);
        Assert.NotNull(s.SecondSpawn);
        int half = Gen("garden.json").Config.BoundaryHalfWidth;
        Assert.Equal(90, half);
        Assert.InRange(s.PlayerSpawn.X / Chunk.TileSize, -half, half);
        Assert.InRange(s.SecondSpawn.Value.X / Chunk.TileSize, -half, half);
    }

    [Fact]
    public void Inside_IsDirtOverStone_LikeAdventure()
    {
        var g = Gen("garden.json");
        foreach (int x in new[] { -88, -40, 0, 33, 89 })
        {
            int top = g.SurfaceY(x);
            Assert.Equal(TileState.Empty, At(g, x, top - 1).State);
            for (int d = 0; d < 10; d++) Assert.Equal(TileType.Dirt, At(g, x, top + d).Type);
            for (int d = 10; d < 60; d++) Assert.Equal(TileType.Stone, At(g, x, top + d).Type);
        }
    }

    [Fact]
    public void Beyond_IsHardened_FromHighAboveToDeepBelow()
    {
        var g = Gen("garden.json");
        int top = g.WallTopY;
        Assert.True(top <= -(int)g.Config.MaxHeight - 100, $"wall top {top} not well above MaxHeight");
        foreach (int x in new[] { -400, -91, 91, 92, 5000 })
        {
            Assert.Equal(TileState.Empty, At(g, x, top - 1).State);
            foreach (int y in new[] { top, top + 50, -30, 0, 40, 500 })
            {
                var t = At(g, x, y);
                Assert.Equal(TileState.Solid, t.State);
                Assert.Equal(TileType.Hardened, t.Type);
            }
        }
        // The boundary columns themselves (+-90) are still arena.
        Assert.NotEqual(TileType.Hardened, At(g, 90, g.SurfaceY(90)).Type);
        Assert.NotEqual(TileType.Hardened, At(g, -90, g.SurfaceY(-90)).Type);
        // Chunks wholly above the wall are empty.
        var sky = Make(g, 100, Math.DivRem(top, Chunk.Size, out _) - 2);
        foreach (var t in sky.Tiles) Assert.Equal(TileState.Empty, t.State);
    }

    [Fact]
    public void AdventureGeneration_IsUnchanged_WhenBoundaryAbsent()
    {
        var adv = Gen("adventure.json");
        Assert.Equal(0, adv.Config.BoundaryHalfWidth);
        // Same config as garden minus the boundary must agree cell for cell inside the arena,
        // and a zero boundary must equal the generator with default boundary fields.
        var garden = Gen("garden.json");
        for (int cx = -5; cx <= 4; cx++)
            for (int cy = -2; cy <= 2; cy++)
            {
                var a = Make(adv, cx, cy); var b = Make(garden, cx, cy);
                for (int x = 0; x < Chunk.Size; x++)
                    for (int y = 0; y < Chunk.Size; y++)
                    {
                        Assert.Equal(a.Tiles[x, y].State, b.Tiles[x, y].State);
                        Assert.Equal(a.Tiles[x, y].Type, b.Tiles[x, y].Type);
                    }
            }
        // Far outside, adventure never produces Hardened.
        var far = Make(adv, 20, 0);
        foreach (var t in far.Tiles) Assert.NotEqual(TileType.Hardened, t.Type);
    }

    [Fact]
    public void Generation_IsDeterministic()
    {
        var a = Gen("garden.json"); var b = Gen("garden.json");
        foreach (var (cx, cy) in new[] { (0, 0), (-7, 1), (5, 0), (6, -3), (12, 30), (-6, 2) })
        {
            var ca = Make(a, cx, cy); var cb = Make(b, cx, cy);
            for (int x = 0; x < Chunk.Size; x++)
                for (int y = 0; y < Chunk.Size; y++)
                {
                    Assert.Equal(ca.Tiles[x, y].State, cb.Tiles[x, y].State);
                    Assert.Equal(ca.Tiles[x, y].Type, cb.Tiles[x, y].Type);
                }
        }
    }
}
