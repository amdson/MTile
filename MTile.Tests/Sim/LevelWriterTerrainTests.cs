using System;
using System.IO;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;

namespace MTile.Tests.Sim;

// Creative mode's save path: ChunkMap.EditorSetCell edits, then LevelWriter.Overwrite
// writes them back into the level they came from. The reloaded level must equal the live
// grid cell for cell, while untouched rule chunks stay procedural and the rest of the
// .json survives.
public class LevelWriterTerrainTests
{
    private static string NewDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mtile_levelwriter_" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void EditorSetCell_IsInstantAndReportsChanges()
    {
        var map = new ChunkMap();
        Assert.False(map.EditorSetCell(3, 4, TileState.Empty, TileType.Stone));   // nothing there, no chunk made
        Assert.False(map.TryGet(new Point(0, 0), out _));

        Assert.True(map.EditorSetCell(3, 4, TileState.Solid, TileType.Hardened)); // unsupported, still Solid
        Assert.Equal(TileState.Solid, map.GetCellState(3, 4));
        Assert.Equal(TileType.Hardened, map.GetCellType(3, 4));
        Assert.False(map.EditorSetCell(3, 4, TileState.Solid, TileType.Hardened));
        Assert.True(map.EditorSetCell(3, 4, TileState.Solid, TileType.Dirt));      // retype counts

        bool broke = false;
        map.OnTileBroken += (_, _) => broke = true;
        Assert.True(map.EditorSetCell(3, 4, TileState.Empty, TileType.Stone));
        Assert.Equal(TileState.Empty, map.GetCellState(3, 4));
        Assert.False(broke);                                                       // an edit, not a break
    }

    [Fact]
    public void Overwrite_RoundTripsEditsAndKeepsUntouchedChunksProcedural()
    {
        string dir = NewDir();
        try
        {
            string json = Path.Combine(dir, "lvl.json");
            // Chunk (0,0) is authored (a floor row); everything else comes from a rule.
            File.WriteAllText(Path.Combine(dir, "floor.txt"),
                string.Concat(System.Linq.Enumerable.Repeat(new string('.', 16) + "\n", 15)) + new string('X', 16) + "\n");
            File.WriteAllText(json, """
                { "Extents": 1,
                  "ChunkFiles": { "0,0": "floor.txt" },
                  "Rules": [ { "Condition": "y >= 24", "IsSolid": true } ],
                  "Custom": 7 }
                """);

            var live = new ChunkMap();
            TerrainLoader.Load(json, live);
            live.EditorSetCell(2, 15, TileState.Empty, TileType.Stone);       // dig the authored floor
            live.EditorSetCell(5, 5, TileState.Solid, TileType.Sand);         // paint in the authored chunk
            live.EditorSetCell(-10, 26, TileState.Solid, TileType.Hardened);  // retype inside rule chunk (-1,1)
            live.EditorSetCell(3 * 16 + 1, 2, TileState.Solid, TileType.Dirt); // past Extents: chunk (3,0)

            var r = LevelWriter.Overwrite(live, json);
            Assert.Equal(1, r.OldExtents);
            Assert.Equal(3, r.NewExtents);

            var reloaded = new ChunkMap();
            TerrainLoader.Load(json, reloaded);
            for (int gtx = -3 * 16; gtx < 4 * 16; gtx++)
                for (int gty = -3 * 16; gty < 4 * 16; gty++)
                {
                    bool solid = live.GetCellState(gtx, gty) == TileState.Solid;
                    Assert.Equal(solid, reloaded.GetCellState(gtx, gty) == TileState.Solid);
                    if (solid) Assert.Equal(live.GetCellType(gtx, gty), reloaded.GetCellType(gtx, gty));
                }

            var root = JsonNode.Parse(File.ReadAllText(json))!.AsObject();
            Assert.Equal(7, (int)root["Custom"]!);                 // unknown keys survive
            Assert.NotNull(root["Rules"]);
            var files = root["ChunkFiles"]!.AsObject();
            Assert.Equal("floor.txt", (string)files["0,0"]!);      // the authored file is rewritten in place
            Assert.True(files.ContainsKey("-1,1"));                // the edited rule chunk
            Assert.True(files.ContainsKey("3,0"));                 // the new chunk
            Assert.False(files.ContainsKey("1,1"));                // untouched rule chunk stays procedural
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
