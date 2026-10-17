using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;

namespace MTile;

// Dev tool (CreativeMode, desktop): write the live terrain BACK into the level it was
// loaded from — the level .json plus its chunk .txt files — rather than snapshotting a
// new saved_NNN stage the way StageSaver does. Git is the undo.
//
// Minimal by construction: it rebuilds what the level file would load today (ChunkFiles,
// then Perlin or Rules) and writes a chunk only where the live grid differs from that.
// Untouched rule/noise chunks stay procedural, untouched authored chunks keep their
// files byte-for-byte, and everything else in the .json (Rules, Perlin, spawn, unknown
// keys) is preserved — only Extents and ChunkFiles are rewritten.
public static class LevelWriter
{
    public readonly record struct Result(List<string> WrittenFiles, int ChunksWritten,
                                         int OldExtents, int NewExtents);

    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    public static Result Overwrite(ChunkMap live, string jsonPathAbs)
    {
        string text = File.ReadAllText(jsonPathAbs);
        var cfg  = JsonSerializer.Deserialize<TerrainConfig>(text, ReadOpts) ?? new TerrainConfig();
        if (cfg.WorldGen != null)
            throw new InvalidOperationException("endless (WorldGen) level — its terrain is generated, use Ctrl+M to snapshot instead");
        var root = JsonNode.Parse(text)!.AsObject();
        string dir  = Path.GetDirectoryName(jsonPathAbs)!;
        string stem = Path.GetFileNameWithoutExtension(jsonPathAbs);

        // Painting past the level's box grows it to cover every chunk holding a tile.
        // Chunks outside Extents are never materialized, so they'd load as air.
        int oldExt = cfg.Extents, ext = oldExt;
        foreach (var c in live)
            if (AnySolid(c))
                ext = Math.Max(ext, Math.Max(Math.Abs(c.ChunkPos.X), Math.Abs(c.ChunkPos.Y)));

        // What the file would load with that box: the comparison baseline. Ring chunks
        // added by a grown Extents get Rules/Perlin here, so the live grid (where they
        // were air) differs and they're written out explicitly — a save never changes
        // what you see.
        var reference = new ChunkMap();
        cfg.Extents = ext;
        TerrainLoader.Load(cfg, dir, reference);

        var files = new Dictionary<string, string>(cfg.ChunkFiles);
        var uses  = files.Values.GroupBy(f => f, StringComparer.OrdinalIgnoreCase)
                         .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var written = new List<string>();
        for (int cx = -ext; cx <= ext; cx++)
            for (int cy = -ext; cy <= ext; cy++)
            {
                var pos = new Point(cx, cy);
                live.TryGet(pos, out var lc);
                reference.TryGet(pos, out var rc);
                if (SameContent(lc, rc)) continue;

                string key = $"{cx},{cy}";
                // An authored file shared by several chunk keys can't be rewritten for one
                // of them without changing the others — give this chunk its own file.
                if (!files.TryGetValue(key, out string file) || uses.GetValueOrDefault(file) > 1)
                    file = $"{stem}_{cx}_{cy}.txt";
                files[key] = file;
                string path = Path.Combine(dir, file);
                StageSaver.WriteChunk(lc ?? new Chunk { ChunkPos = pos }, path);
                written.Add(path);
            }

        SetProp(root, "Extents", ext);
        var cf = new JsonObject();
        foreach (var (k, v) in files.OrderBy(kv => kv.Key, StringComparer.Ordinal)) cf[k] = v;
        SetProp(root, "ChunkFiles", cf);
        File.WriteAllText(jsonPathAbs, root.ToJsonString(WriteOpts) + "\n");
        written.Add(jsonPathAbs);
        return new Result(written, written.Count - 1, oldExt, ext);
    }

    // Replace a property under whatever casing the file already uses for it.
    private static void SetProp(JsonObject o, string name, JsonNode value)
    {
        string key = o.Select(kv => kv.Key)
                      .FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) ?? name;
        o[key] = value;
    }

    // Compared as the file would store them (StageSaver.TileChar), so a sprouting cell
    // equals empty and a missing chunk equals an all-air one.
    private static bool SameContent(Chunk a, Chunk b)
    {
        for (int tx = 0; tx < Chunk.Size; tx++)
            for (int ty = 0; ty < Chunk.Size; ty++)
            {
                char ca = a == null ? '.' : StageSaver.TileChar(in a.Tiles[tx, ty]);
                char cb = b == null ? '.' : StageSaver.TileChar(in b.Tiles[tx, ty]);
                if (ca != cb) return false;
            }
        return true;
    }

    private static bool AnySolid(Chunk c)
    {
        for (int tx = 0; tx < Chunk.Size; tx++)
            for (int ty = 0; ty < Chunk.Size; ty++)
                if (c.Tiles[tx, ty].IsSolid) return true;
        return false;
    }
}
