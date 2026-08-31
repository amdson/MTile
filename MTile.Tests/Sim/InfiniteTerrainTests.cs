using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Gates for the endless streamed world (World/WorldGenerator.cs + ChunkMap streaming,
// Levels/spires.json). Three things have to hold, and each one is load-bearing for a
// different reason:
//
//   1. Generation is PURE. Rollback drops streamed chunks and the replay regenerates
//      them; if generation were not a pure function of chunk position, a rollback would
//      quietly rewrite terrain under the players' feet.
//   2. The heightfield is seamless and closed. A column is solid from its surface down,
//      and two adjacent chunks agree about the column they share — which they only do
//      because each derives the surface from world X rather than from its neighbour.
//   3. The fingerprint survives streaming. A generated chunk's 256 cells are written
//      outside the journal, so ChunkMap folds them into TerrainHash by hand — and has
//      to take them back out when a rewind drops the chunk.
public class InfiniteTerrainTests(ITestOutputHelper output)
{
    private static WorldGenerator Gen(int seed = 1337)
        => new(new WorldGenConfig { Seed = seed });

    private static Chunk Make(WorldGenerator gen, int cx, int cy)
    {
        var c = new Chunk { ChunkPos = new Point(cx, cy) };
        gen.Generate(c);
        return c;
    }

    private static string Signature(Chunk c)
    {
        var sb = new System.Text.StringBuilder();
        for (int tx = 0; tx < Chunk.Size; tx++)
            for (int ty = 0; ty < Chunk.Size; ty++)
                sb.Append((int)c.Tiles[tx, ty].State).Append((int)c.Tiles[tx, ty].Type);
        return sb.ToString();
    }

    // ── 1. Purity ───────────────────────────────────────────────────────────────

    [Fact]
    public void SameSeed_GeneratesTheSameChunk_EveryTime()
    {
        var a = Gen();
        var b = Gen();   // separate instance, same seed
        foreach (var (cx, cy) in new[] { (0, 0), (-3, -1), (17, 4), (-914, -73), (2_000_001, 12) })
        {
            Assert.Equal(Signature(Make(a, cx, cy)), Signature(Make(b, cx, cy)));
            Assert.Equal(Signature(Make(a, cx, cy)), Signature(Make(a, cx, cy)));   // and re-entrant
        }
    }

    [Fact]
    public void DifferentSeeds_GenerateDifferentWorlds()
    {
        var a = Gen(1337);
        var b = Gen(9001);
        int differing = 0;
        for (int cx = -8; cx <= 8; cx++)
            if (Signature(Make(a, cx, 0)) != Signature(Make(b, cx, 0))) differing++;
        Assert.True(differing >= 15, $"only {differing}/17 chunks differed between seeds");
    }

    // ── 2. Shape ────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryColumn_IsSolidBelowItsSurfaceAndOpenAbove()
    {
        var gen = Gen();
        for (int cx = -4; cx <= 4; cx++)
            for (int cy = -3; cy <= 3; cy++)
            {
                var chunk = Make(gen, cx, cy);
                for (int tx = 0; tx < Chunk.Size; tx++)
                {
                    int surfaceY = gen.SurfaceY(cx * Chunk.Size + tx);
                    for (int ty = 0; ty < Chunk.Size; ty++)
                    {
                        int worldY = cy * Chunk.Size + ty;
                        Assert.Equal(worldY >= surfaceY, chunk.Tiles[tx, ty].IsSolid);
                    }
                }
            }
    }

    [Fact]
    public void NeighbouringChunks_AgreeAcrossTheirSeam()
    {
        var gen = Gen();
        // Horizontal seam: the last column of one chunk and the first of the next must
        // read as one continuous heightfield, i.e. their surfaces differ by whatever the
        // noise says and by nothing else.
        for (int cx = -6; cx < 6; cx++)
        {
            var left  = Make(gen, cx, 0);
            var right = Make(gen, cx + 1, 0);
            int lastX  = (cx + 1) * Chunk.Size - 1;
            int firstX = (cx + 1) * Chunk.Size;
            for (int ty = 0; ty < Chunk.Size; ty++)
            {
                Assert.Equal(ty + 0 >= gen.SurfaceY(lastX),  left .Tiles[Chunk.Size - 1, ty].IsSolid);
                Assert.Equal(ty + 0 >= gen.SurfaceY(firstX), right.Tiles[0, ty].IsSolid);
            }
        }

        // Vertical seam: a column solid at the bottom of one chunk is solid at the top
        // of the chunk below it. Ground never re-opens as you go down.
        for (int cx = -3; cx <= 3; cx++)
        {
            var upper = Make(gen, cx, 1);
            var lower = Make(gen, cx, 2);
            for (int tx = 0; tx < Chunk.Size; tx++)
                if (upper.Tiles[tx, Chunk.Size - 1].IsSolid)
                    Assert.True(lower.Tiles[tx, 0].IsSolid,
                        $"column {cx * Chunk.Size + tx} re-opened below the chunk seam");
        }
    }

    [Fact]
    public void SomeSpires_RiseThousandsOfTiles_AndMostOfTheWorldDoesNot()
    {
        var gen = Gen();
        float max = 0;
        int tallColumns = 0, plainColumns = 0;
        const int span = 200_000;
        for (int x = -span / 2; x < span / 2; x++)
        {
            float h = gen.HeightAt(x);
            max = MathF.Max(max, h);
            if (h > 1000f) tallColumns++;
            if (h < 80f) plainColumns++;
        }
        output.WriteLine($"max height {max:F0} tiles; {100f * tallColumns / span:F2}% over 1000; " +
                         $"{100f * plainColumns / span:F1}% under 80");

        Assert.True(max > 2000f, $"tallest column was only {max:F0} tiles");
        // …but the world is still mostly traversable ground, not a wall of towers.
        Assert.True(plainColumns > span * 0.7f, "less than 70% of the world is ordinary ground");
        Assert.True(tallColumns < span * 0.05f, "more than 5% of the world is over 1000 tiles");
    }

    [Fact]
    public void HeightIsClampedToMaxHeight()
    {
        var gen = new WorldGenerator(new WorldGenConfig { MaxHeight = 400f, SpireAmplitude = 50_000f });
        for (int x = -20_000; x < 20_000; x += 7)
            Assert.True(gen.HeightAt(x) <= 400f);
    }

    [Fact]
    public void TerrainMixes_Stone_Dirt_And_Sand()
    {
        var gen = Gen();
        var tally = new int[TileTypes.Count];
        for (int cx = -30; cx <= 30; cx++)
            for (int cy = -4; cy <= 8; cy++)
            {
                var chunk = Make(gen, cx, cy);
                for (int tx = 0; tx < Chunk.Size; tx++)
                    for (int ty = 0; ty < Chunk.Size; ty++)
                        if (chunk.Tiles[tx, ty].IsSolid) tally[(int)chunk.Tiles[tx, ty].Type]++;
            }
        int total = 0;
        foreach (var t in tally) total += t;
        for (int i = 0; i < tally.Length; i++)
            output.WriteLine($"{(TileType)i,-9}: {100f * tally[i] / total:F2}%");

        Assert.True(tally[(int)TileType.Stone] > total * 0.30f, "not enough stone");
        Assert.True(tally[(int)TileType.Dirt]  > total * 0.03f, "not enough dirt");
        Assert.True(tally[(int)TileType.Sand]  > total * 0.01f, "not enough sand");
        // Generation never emits the authored-only materials.
        Assert.Equal(0, tally[(int)TileType.Foam]);
        Assert.Equal(0, tally[(int)TileType.Hardened]);
    }

    [Fact]
    public void SpireFlanks_AreBareStone_WhilePlainsCarrySoil()
    {
        var gen = Gen();
        // Find the tallest column in a wide sample and check its crust is rock, then
        // check a low flat column has a soil crust. Soil that clung to a vertical face
        // would read as floating dirt.
        int peakX = 0; float peak = 0, flatBest = float.MaxValue; int flatX = 0;
        for (int x = -60_000; x < 60_000; x++)
        {
            float h = gen.HeightAt(x);
            if (h > peak) { peak = h; peakX = x; }
            float flatness = h + MathF.Abs(gen.HeightAt(x + 1) - gen.HeightAt(x)) * 50f;
            if (flatness < flatBest) { flatBest = flatness; flatX = x; }
        }
        output.WriteLine($"peak {peak:F0} at x={peakX}; flattest low ground at x={flatX} h={gen.HeightAt(flatX):F1}");

        Assert.Equal(TileType.Stone, CrustAt(gen, peakX));
        Assert.NotEqual(TileType.Stone, CrustAt(gen, flatX));
    }

    private static TileType CrustAt(WorldGenerator gen, int worldX)
    {
        int surfaceY = gen.SurfaceY(worldX);
        int cx = (int)Math.Floor(worldX / (double)Chunk.Size);
        int cy = (int)Math.Floor(surfaceY / (double)Chunk.Size);
        var chunk = Make(gen, cx, cy);
        return chunk.Tiles[worldX - cx * Chunk.Size, surfaceY - cy * Chunk.Size].Type;
    }

    // ── 3. Streaming, fingerprint, and rollback ─────────────────────────────────

    private static ChunkMap StreamedMap(int seed = 1337)
        => new() { Generator = Gen(seed) };

    [Fact]
    public void StreamAround_FillsTheNeighbourhood_AndSkipsWhatIsAlreadyThere()
    {
        var chunks = StreamedMap();
        Assert.Equal(0, chunks.LoadedChunkCount);

        chunks.StreamAround(Vector2.Zero, 2, 1);
        Assert.Equal(5 * 3, chunks.LoadedChunkCount);

        // Idempotent: the same call loads nothing new.
        chunks.StreamAround(Vector2.Zero, 2, 1);
        Assert.Equal(5 * 3, chunks.LoadedChunkCount);

        // And it grows outward as the position moves.
        chunks.StreamAround(new Vector2(100 * Chunk.Size * Chunk.TileSize, 0f), 2, 1);
        Assert.Equal(2 * 5 * 3, chunks.LoadedChunkCount);
    }

    [Fact]
    public void AlreadyLoadedChunks_AreNeverOverwrittenByTheGenerator()
    {
        var chunks = StreamedMap();
        // An authored chunk (as a level's ChunkFiles entry would produce) sitting where
        // the generator would otherwise put ground.
        var authored = new Chunk { ChunkPos = new Point(0, 0) };
        chunks[authored.ChunkPos] = authored;

        chunks.StreamAround(Vector2.Zero, 1, 1);

        Assert.Same(authored, chunks[new Point(0, 0)]);
        for (int tx = 0; tx < Chunk.Size; tx++)
            for (int ty = 0; ty < Chunk.Size; ty++)
                Assert.False(authored.Tiles[tx, ty].IsSolid);
    }

    [Fact]
    public void TerrainHash_TracksStreamedChunks_AndRewindsWithThem()
    {
        var chunks = StreamedMap();
        chunks.StreamAround(Vector2.Zero, 2, 2);
        chunks.RecomputeTerrainHash();

        var snap = chunks.CaptureTerrain();
        ulong before = chunks.TerrainHash;
        int loadedBefore = chunks.LoadedChunkCount;

        // Stream a fresh region in — new chunks, new hash.
        chunks.StreamAround(new Vector2(40 * Chunk.Size * Chunk.TileSize, 0f), 2, 2);
        Assert.NotEqual(before, chunks.TerrainHash);
        Assert.True(chunks.LoadedChunkCount > loadedBefore);

        // Rolling back has to drop them AND take their cells back out of the
        // fingerprint — the bug this test exists for is a hash that stays "dirty"
        // after a rewind, which desyncs peers on the next checksum compare.
        chunks.RestoreTerrain(snap);
        Assert.Equal(loadedBefore, chunks.LoadedChunkCount);
        Assert.Equal(before, chunks.TerrainHash);

        // Independent confirmation that the incremental hash matches a from-scratch one.
        ulong incremental = chunks.TerrainHash;
        chunks.RecomputeTerrainHash();
        Assert.Equal(incremental, chunks.TerrainHash);
    }

    [Fact]
    public void SimStep_StreamsAheadOfAWalkingPlayer_AndTheGroundIsAlwaysThere()
    {
        var chunks = StreamedMap();
        var gen = (WorldGenerator)chunks.Generator;
        // Spawn on the generated surface at x = 0, a few tiles up.
        int spawnTileX = 0;
        var spawn = new Vector2(spawnTileX * Chunk.TileSize,
                                gen.SurfaceY(spawnTileX) * Chunk.TileSize - 4 * Chunk.TileSize);
        var sim = new Simulation(chunks, spawn);

        // Frame 0 must already have ground: the ctor streams before anything steps.
        Assert.True(chunks.LoadedChunkCount > 0);

        float lowest = spawn.Y;
        for (int f = 0; f < 900; f++)
        {
            sim.Step(new PlayerInput { Right = true, Space = f % 24 < 3 });
            lowest = MathF.Max(lowest, sim.Player.Body.Position.Y);

            // Whatever column the player is over, its ground must be loaded — not the
            // empty air an unstreamed chunk reads as.
            int gtx = (int)MathF.Floor(sim.Player.Body.Position.X / Chunk.TileSize);
            Assert.Equal(TileState.Solid, chunks.GetCellState(gtx, gen.SurfaceY(gtx)));
        }

        output.WriteLine($"walked to x={sim.Player.Body.Position.X:F0}px over {chunks.LoadedChunkCount} chunks");
        Assert.True(sim.Player.Body.Position.X > spawn.X + 200f, "player never got moving");
        // The floor held: never fell more than a spire's worth below the start.
        Assert.True(lowest < spawn.Y + 3000f, $"player fell to y={lowest:F0} — streamed floor gave way");
    }

    [Fact]
    public void RollbackAcrossAStreamingBoundary_ReplaysIdentically()
    {
        // The interesting frame range is one where the player crosses into unloaded
        // chunks, so the replay has to regenerate exactly what the live run generated.
        static Simulation Build()
        {
            var chunks = StreamedMap();
            var g = (WorldGenerator)chunks.Generator;
            return new Simulation(chunks,
                new Vector2(0f, g.SurfaceY(0) * Chunk.TileSize - 4 * Chunk.TileSize));
        }
        static PlayerInput In(int f) => new() { Right = true, Space = f % 24 < 3 };

        var sim = Build();
        for (int f = 0; f < 120; f++) sim.Step(In(f));

        var snap = sim.Snapshot();
        var live = new List<(ulong, int)>();
        for (int f = 120; f < 400; f++) { sim.Step(In(f)); live.Add((sim.Checksum(), sim.Chunks.LoadedChunkCount)); }

        sim.Restore(snap);
        var replay = new List<(ulong, int)>();
        for (int f = 120; f < 400; f++) { sim.Step(In(f)); replay.Add((sim.Checksum(), sim.Chunks.LoadedChunkCount)); }

        Assert.Equal(live.Count, replay.Count);
        for (int i = 0; i < live.Count; i++)
            Assert.True(live[i] == replay[i],
                $"frame {120 + i}: live {live[i]} vs replay {replay[i]}");
        output.WriteLine($"{live.Count} frames replayed identically across streaming boundaries " +
                         $"({live[^1].Item2} chunks resident)");
    }

    // Levels/ isn't copied into the test output, so — as the other stage tests do —
    // walk up to the repo copy and feed the ctor an absolute path (the same rooted-path
    // route StageSaver's in-game stages take).
    private static string FindLevels()
    {
        for (var d = System.IO.Directory.GetCurrentDirectory(); d != null; d = System.IO.Path.GetDirectoryName(d))
        {
            string c = System.IO.Path.Combine(d, "Levels");
            if (System.IO.File.Exists(System.IO.Path.Combine(c, "spires.json"))) return c;
        }
        throw new System.IO.DirectoryNotFoundException("Levels/spires.json not found above the test dir");
    }

    [Fact]
    public void SpiresStage_LoadsEndless_AndDropsThePlayerOnTheSurface()
    {
        var registered = Stages.Get("spires");
        Assert.Equal("spires.json", registered.TerrainConfig);

        var stage = new Stage
        {
            Name          = registered.Name,
            TerrainConfig = System.IO.Path.Combine(FindLevels(), "spires.json"),
            PlayerSpawn   = registered.PlayerSpawn,
            Populate      = registered.Populate,
        };
        var sim = new Simulation(new GameConfig(), stage);
        Assert.IsType<WorldGenerator>(sim.Chunks.Generator);
        Assert.True(sim.Chunks.LoadedChunkCount > 100,
            $"stage loaded only {sim.Chunks.LoadedChunkCount} chunks");

        var gen = (WorldGenerator)sim.Chunks.Generator;
        int gtx = (int)MathF.Floor(sim.Player.Body.Position.X / Chunk.TileSize);
        float surfacePx = gen.SurfaceY(gtx) * Chunk.TileSize;
        float above = surfacePx - sim.Player.Body.Position.Y;
        output.WriteLine($"spawned {above:F0}px above the surface at tile x={gtx}");
        Assert.InRange(above, 0f, 20f * Chunk.TileSize);

        // And it settles instead of falling forever.
        for (int f = 0; f < 240; f++) sim.Step(default);
        Assert.True(sim.Player.Body.Position.Y < surfacePx + 4f * Chunk.TileSize,
            "player sank through the generated ground");
    }
}
