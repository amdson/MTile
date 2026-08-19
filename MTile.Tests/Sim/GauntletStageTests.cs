using System;
using System.IO;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Integrity gates for the authored gauntlet level. A hand-drawn stage can be
// broken in ways no unit test of its enemies would catch — a chunk file one row
// short, a floor gap that drops the player into bedrock, an obstacle taller than
// a jump — and all of those are silent at load time. These run the real
// TerrainLoader over the real files and then walk a real player through them.
//
// The stage files live at the repo root, not next to the test binary, so the
// fixture walks up from the output directory to find them (same approach as
// ClipBindingTests.StatesDir). If they can't be found the tests skip rather than
// fail, so a packaging change doesn't masquerade as a level regression.
public class GauntletStageTests(ITestOutputHelper output)
{
    private const int   ChunkCount   = 8;                     // cx 0..7
    private const int   LastTileX    = ChunkCount * Chunk.Size - 1;   // 127
    private const int   FloorTileY   = 12;
    private const float FloorTopY    = FloorTileY * Chunk.TileSize;   // 192

    private static string LevelsDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            string c = Path.Combine(d.FullName, "Levels");
            if (File.Exists(Path.Combine(c, "gauntlet.json"))) return c;
            d = d.Parent;
        }
        return null;
    }

    private static ChunkMap LoadGauntlet(string levels)
    {
        var chunks = new ChunkMap();
        TerrainLoader.Load(Path.Combine(levels, "gauntlet.json"), chunks);
        return chunks;
    }

    [Fact]
    public void EveryChunkFileIsExactlySixteenBySixteen()
    {
        var levels = LevelsDir();
        if (levels == null) { output.WriteLine("Levels/ not found — skipping."); return; }

        foreach (var path in Directory.GetFiles(levels, "gauntlet_*.txt"))
        {
            var lines = File.ReadAllLines(path);
            // TerrainLoader silently stops at the first short/missing row, so a
            // truncated file reads as "the rest is empty sky" — exactly the kind
            // of hole that only shows up when a player falls through it.
            Assert.True(lines.Length >= Chunk.Size,
                $"{Path.GetFileName(path)} has {lines.Length} rows, expected {Chunk.Size}.");
            for (int i = 0; i < Chunk.Size; i++)
                Assert.True(lines[i].Length == Chunk.Size,
                    $"{Path.GetFileName(path)} row {i} is {lines[i].Length} chars, expected {Chunk.Size}.");
        }
    }

    [Fact]
    public void FloorLineIsUnbrokenAcrossTheWholeRun()
    {
        var levels = LevelsDir();
        if (levels == null) { output.WriteLine("Levels/ not found — skipping."); return; }
        var chunks = LoadGauntlet(levels);

        // The one invariant every gauntlet chunk shares. If a future edit breaks
        // it the run stops being a run, and the failure is a soft one — the
        // player just gets stuck somewhere in the middle.
        for (int tx = 0; tx <= LastTileX; tx++)
            Assert.True(chunks.GetCellState(tx, FloorTileY) == TileState.Solid,
                $"Floor gap at tile x {tx} (world x {tx * Chunk.TileSize}).");

        // …and bedrock below it, so a rail bolt that punches through the slab
        // makes a pit rather than a bottomless hole.
        for (int tx = 0; tx <= LastTileX; tx += 7)
            Assert.True(chunks.GetCellState(tx, 20) == TileState.Solid,
                $"No bedrock under tile x {tx}.");

        // …and solid rock past both ends. Every wall in this game is
        // destructible, so the end walls are a speed bump, not a boundary — a
        // player who slams into one long enough digs through it. What matters is
        // that what's on the other side is rock, not an unrecoverable void.
        for (int ty = 0; ty <= 15; ty += 3)
        {
            Assert.True(chunks.GetCellState(LastTileX + 4, ty) == TileState.Solid,
                $"No bedrock past the right end at tile y {ty}.");
            Assert.True(chunks.GetCellState(-4, ty) == TileState.Solid,
                $"No bedrock past the left end at tile y {ty}.");
        }
    }

    [Fact]
    public void PlayerCanWalkFromSpawnToTheFarWall()
    {
        var levels = LevelsDir();
        if (levels == null) { output.WriteLine("Levels/ not found — skipping."); return; }
        var chunks = LoadGauntlet(levels);

        var stage = Stages.Get("gauntlet");
        Assert.Equal("gauntlet", stage.Name);

        // Terrain only — no Populate. This isolates level geometry from combat:
        // a traversal failure here is a level bug, full stop.
        var sim = new Simulation(chunks, stage.PlayerSpawn);

        // Hold right, tap jump on a fixed cadence. Nothing clever — if a plain
        // hold-and-hop can't clear the level, neither obstacle heights nor the
        // tunnel clearances are right.
        PlayerInput At(int f) => new() { Right = true, Space = f % 24 < 4 };

        const float ArrivedX = 1980f;           // inside the final chamber, at the end wall
        float furthest  = sim.Player.Body.Position.X;
        int   arrivedAt = -1;

        for (int f = 0; f < 3600 && arrivedAt < 0; f++)   // up to 60s of sim
        {
            sim.Step(At(f));
            float x = sim.Player.Body.Position.X;
            if (x > furthest) furthest = x;
            if (x > ArrivedX) arrivedAt = f;
            // The floor is at y 192; anything far below it means the player
            // fell out of the level rather than merely into a dip.
            Assert.True(sim.Player.Body.Position.Y < FloorTopY + 200f,
                $"Player fell out of the world at frame {f} (x {x:F0}, y {sim.Player.Body.Position.Y:F0}).");
        }

        // Stops the moment the far end is reached. Running on would just measure
        // how long a jump-mashing player takes to excavate the end wall — real
        // behaviour, but not what this test is about.
        Assert.True(arrivedAt > 0,
            $"Player only reached world x {furthest:F0} of ~2030 — the run is blocked somewhere.");
        output.WriteLine($"Reached world x {furthest:F0} at frame {arrivedAt} ({arrivedAt / 60f:F1}s).");
    }

    [Fact]
    public void FullEncounterRunsAndTheEnemiesEngage()
    {
        var levels = LevelsDir();
        if (levels == null) { output.WriteLine("Levels/ not found — skipping."); return; }
        var chunks = LoadGauntlet(levels);
        var stage  = Stages.Get("gauntlet");

        var sim = new Simulation(chunks, stage.PlayerSpawn, stage.Populate);

        int bastions = 0, pouncers = 0, latchers = 0;
        foreach (var e in sim.Entities)
        {
            if (e.Kind == EntityKind.Bastion) bastions++;
            if (e.Kind == EntityKind.Pouncer) pouncers++;
            if (e.Kind == EntityKind.Latcher) latchers++;
        }
        Assert.Equal(2, bastions);
        Assert.Equal(4, pouncers);
        Assert.Equal(4, latchers);

        // Advance with the player pushing right into the gallery. This is a
        // smoke run: the assertions are "nothing throws" and "the Bastion
        // actually shoots", which together exercise spawn → charge → fire →
        // terrain damage → despawn on a real level.
        bool sawBolt = false;
        for (int f = 0; f < 1200; f++)
        {
            sim.Step(new PlayerInput { Right = f % 40 < 25, Space = f % 24 < 4 });
            if (!sawBolt)
                foreach (var e in sim.Entities)
                    if (e.Kind == EntityKind.RailBolt) { sawBolt = true; break; }
        }

        Assert.True(sawBolt, "No Bastion fired during a 20-second approach.");
        output.WriteLine($"Player ended at x {sim.Player.Body.Position.X:F0}; " +
                         $"{CountAlive(sim)} entities alive.");
    }

    private static int CountAlive(Simulation sim)
    {
        int n = 0;
        foreach (var e in sim.Entities) if (!e.IsDead) n++;
        return n;
    }
}
