using System;
using System.IO;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;

namespace MTile.Tests.Sim;

// StageSaver round-trip: capturing the live sim (player pos + nearby chunks, with
// materials) into Levels/saved-style files must load back to identical terrain via
// the real TerrainLoader path, register a selectable Stage, and drop far chunks.
public class StageSaverTests
{
    [Fact]
    public void SaveThenLoad_RoundTripsTilesMaterialsAndSpawn()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mtile_stagesaver_" + Guid.NewGuid().ToString("N"));
        try
        {
            // Floor slab in chunk (0,0)-ish plus a distinctive material pattern.
            var chunks = SimTerrain.FromAscii(new string('X', 40) + "\n" + new string('X', 40),
                                              originTileX: 0, originTileY: 10);
            chunks[new Point(0, 0)].Tiles[3, 10].Type = TileType.Dirt;
            chunks[new Point(0, 0)].Tiles[4, 10].Type = TileType.Sand;
            chunks[new Point(0, 0)].Tiles[5, 10].Type = TileType.Foam;

            // A far chunk (outside radius 1 of the player's chunk) that must NOT be saved.
            var far = new Chunk { ChunkPos = new Point(9, 0) };
            far.Tiles[0, 0].IsSolid = true;
            chunks[far.ChunkPos] = far;

            var spawn = new Vector2(5 * Chunk.TileSize, 10 * Chunk.TileSize - PlayerCharacter.Radius);
            var sim = new Simulation(chunks, spawn);

            string name = StageSaver.Save(sim, chunkRadius: 1, dir);

            // The stage is registered live with the captured player position as spawn.
            var stage = Stages.Get(name);
            Assert.Equal(name, stage.Name);
            Assert.Equal(spawn, stage.PlayerSpawn);

            // Loading the saved config back reproduces the nearby tiles exactly.
            var loaded = new ChunkMap();
            TerrainLoader.Load(stage.TerrainConfig, loaded);

            for (int gty = 0; gty < 2 * Chunk.Size; gty++)
                for (int gtx = 0; gtx < 2 * Chunk.Size; gtx++)
                {
                    Assert.Equal(chunks.GetCellState(gtx, gty) == TileState.Solid,
                                 loaded.GetCellState(gtx, gty) == TileState.Solid);
                    if (loaded.GetCellState(gtx, gty) == TileState.Solid)
                        Assert.Equal(chunks.GetCellType(gtx, gty), loaded.GetCellType(gtx, gty));
                }

            // Far chunk dropped: its cell loads back empty.
            Assert.Equal(TileState.Empty, loaded.GetCellState(9 * Chunk.Size, 0));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
