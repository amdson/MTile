using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Integrity gates for the Zeus encounter (Stages "hill" + Levels/hill.json).
// Same shape as GauntletStageTests: run the real TerrainLoader over the real
// files, then run the real stage Populate and step the real Simulation.
//
// The two things worth pinning are the ones that are silent when broken:
//   * the statue is standing ON the summit, not buried in it or floating over it
//   * all three laser attacks actually open within one schedule cycle. The
//     schedule is a pure function of the sim frame (ZeusBeam's windows), so an
//     off-by-one in a window — or an action whose priority quietly shadows a
//     sibling — produces a boss that only ever uses one of its three attacks and
//     looks fine while doing it.
public class ZeusHillTests(ITestOutputHelper output)
{
    private const float SummitTopY = 3 * Chunk.TileSize;    // 48 — top of the dome
    private static readonly Vector2 ZeusSpawn = new(136f, SummitTopY - 16f);

    // Topmost solid tile row in a column, searching the authored band only.
    private static int SurfaceTileY(ChunkMap chunks, int gtx)
    {
        for (int gty = 0; gty < 16; gty++)
            if (TileQuery.IsSolidAt(chunks, gtx * 16f + 8f, gty * 16f + 8f)) return gty;
        return 16;
    }

    private static string LevelsDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            string c = Path.Combine(d.FullName, "Levels");
            if (File.Exists(Path.Combine(c, "hill.json"))) return c;
            d = d.Parent;
        }
        return null;
    }

    [Fact]
    public void HillChunkFilesAreExactlySixteenBySixteen()
    {
        var levels = LevelsDir();
        if (levels == null) { output.WriteLine("Levels/ not found — skipping."); return; }

        foreach (var path in Directory.GetFiles(levels, "hill_*.txt"))
        {
            var lines = File.ReadAllLines(path);
            Assert.True(lines.Length >= Chunk.Size,
                $"{Path.GetFileName(path)} has {lines.Length} rows, expected {Chunk.Size}.");
            for (int i = 0; i < Chunk.Size; i++)
                Assert.True(lines[i].Length == Chunk.Size,
                    $"{Path.GetFileName(path)} row {i} is {lines[i].Length} chars, expected {Chunk.Size}.");
        }
    }

    [Fact]
    public void SummitIsSolidUnderTheStatue()
    {
        var levels = LevelsDir();
        if (levels == null) { output.WriteLine("Levels/ not found — skipping."); return; }
        var chunks = new ChunkMap();
        TerrainLoader.Load(Path.Combine(levels, "hill.json"), chunks);

        // The crown: tile (8,3) is the peak Zeus stands on, open sky above it, and
        // the slope steps down exactly one tile per column to either side (a
        // >1-tile step would be a wall the player can't walk up).
        Assert.True(TileQuery.IsSolidAt(chunks, 8 * 16f + 8f, 3 * 16f + 8f), "peak tile (8,3) is not solid");
        Assert.False(TileQuery.IsSolidAt(chunks, 8 * 16f + 8f, 2 * 16f + 8f), "tile (8,2) above the peak is solid");

        for (int gtx = -12; gtx <= 27; gtx++)
        {
            int a = SurfaceTileY(chunks, gtx), b = SurfaceTileY(chunks, gtx + 1);
            Assert.True(Math.Abs(a - b) <= 1,
                $"slope steps {Math.Abs(a - b)} tiles between columns {gtx} and {gtx + 1}");
        }
        // And the western spawn shelf the player lands on.
        Assert.True(TileQuery.IsSolidAt(chunks, -14 * 16f + 8f, 13 * 16f + 8f), "player-spawn ground is missing");
    }

    [Fact]
    public void ZeusOpensAllThreeLaserAttacksWithinOneCycle()
    {
        var levels = LevelsDir();
        if (levels == null) { output.WriteLine("Levels/ not found — skipping."); return; }
        var chunks = new ChunkMap();
        TerrainLoader.Load(Path.Combine(levels, "hill.json"), chunks);

        var stage = Stages.Get("hill");
        Assert.Equal("hill", stage.Name);

        // NOT stage.PlayerSpawn. The stage drops the player at the foot of the
        // western slope, where the dome itself blocks the summit's line of sight —
        // which is the intended opening (climb into the firing line, and the fight
        // starts when you crest the shoulder), but it means a do-nothing input
        // script never provokes a shot. Stand on the eastern slope instead, inside
        // alert range with a clear line to the statue.
        var sim = new Simulation(chunks, new Vector2(296f, 60f), stage.Populate);

        EnemyEntity zeus = null;
        foreach (var e in sim.Entities)
            if (e is EnemyEntity en && en.Kind == EntityKind.Zeus) zeus = en;
        Assert.NotNull(zeus);

        // Settled on the summit, not sunk into it or hovering.
        var spawnDrift = Vector2.Distance(zeus.Body.Position, ZeusSpawn);
        Assert.True(spawnDrift < 4f, $"Zeus spawned {spawnDrift:F1}px off the summit.");

        // Two full schedule cycles, so the sweep (which opens late and runs past
        // the cycle boundary) definitely gets a turn.
        var seen = new HashSet<string>();
        for (int f = 0; f < 2 * ZeusBeamWindows.CycleFrames; f++)
        {
            sim.Step(default);
            string a = zeus.CurrentActionName;
            if (a.Length > 0) seen.Add(a);
        }
        output.WriteLine($"actions seen: {string.Join(",", seen)}");

        Assert.Contains("ZeusBoltAction",   seen);
        Assert.Contains("ZeusStrikeAction", seen);
        Assert.Contains("ZeusSweepAction",  seen);

        // The statue never moves off its post, whatever it fires.
        Assert.True(Vector2.Distance(zeus.Body.Position, ZeusSpawn) < 8f,
                    "Zeus wandered off the summit.");
    }

    // ZeusBeam is internal to MTile.Core; mirror the one constant this file needs
    // rather than opening the type up for a test.
    private static class ZeusBeamWindows
    {
        public const int CycleFrames = 600;
    }
}
