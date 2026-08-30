using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;

namespace MTile;

// Dev tool (desktop, offline): capture the live scenario — player position + the tile
// contents of nearby chunks — into a stage on disk (Levels/saved/), so a hand-built
// movement-test setup can be re-entered instantly (F5 in Game1) and across sessions
// (game_config.json "Stage": "saved_NNN"). Terrain only: entities/platforms come from
// code (Stage.Populate), so saved stages populate nothing. Saved foam has no decay
// timer (those live in the sparse FoamDecay store, not the tile grid) — it persists
// until broken, which is fine for a test fixture.
public static class StageSaver
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Material → ascii char for saved chunk rows; TerrainLoader.LoadChunkFromFile reads
    // the same map back ('X' doubles as the legacy solid char). Sprouting cells are
    // mid-growth sim state, not terrain — they save as empty.
    public static char TileChar(in Tile t) => !t.IsSolid ? '.' : t.Type switch
    {
        TileType.Dirt => 'D',
        TileType.Sand => 'S',
        TileType.Foam => 'F',
        TileType.Hardened => 'H',
        _             => 'X',
    };

    // Capture chunks within `chunkRadius` (Chebyshev, in chunks) of the player's chunk
    // into `saveDirAbs` as <name>.json + one .txt per non-empty chunk, register the
    // stage live (ready for Stages.Get without a restart), and return its name.
    public static string Save(Simulation sim, int chunkRadius, string saveDirAbs)
    {
        Directory.CreateDirectory(saveDirAbs);
        string name = NextName(saveDirAbs);

        var pos = sim.Player.Body.Position;
        int chunkPx = Chunk.Size * Chunk.TileSize;
        int pcx = (int)Math.Floor(pos.X / chunkPx);
        int pcy = (int)Math.Floor(pos.Y / chunkPx);

        // Extents starts at the player's own chunk ring so the loader always materializes
        // the ground under the spawn even if every nearby chunk happens to be empty.
        var cfg = new TerrainConfig
        {
            Extents     = Math.Max(Math.Abs(pcx), Math.Abs(pcy)),
            PlayerSpawn = new[] { pos.X, pos.Y },
        };
        foreach (var chunk in sim.Chunks)
        {
            if (Math.Abs(chunk.ChunkPos.X - pcx) > chunkRadius ||
                Math.Abs(chunk.ChunkPos.Y - pcy) > chunkRadius) continue;
            if (!AnySolid(chunk)) continue;
            string file = $"{name}_{chunk.ChunkPos.X}_{chunk.ChunkPos.Y}.txt";
            WriteChunk(chunk, Path.Combine(saveDirAbs, file));
            cfg.ChunkFiles[$"{chunk.ChunkPos.X},{chunk.ChunkPos.Y}"] = file;
            cfg.Extents = Math.Max(cfg.Extents,
                Math.Max(Math.Abs(chunk.ChunkPos.X), Math.Abs(chunk.ChunkPos.Y)));
        }

        string jsonPath = Path.Combine(saveDirAbs, name + ".json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(cfg, JsonOpts));
        Register(name, jsonPath, pos);
        return name;
    }

    // Register every <saveDirAbs>/*.json as a selectable stage. TerrainConfig paths are
    // ABSOLUTE — TitleContent falls back to direct file I/O for rooted paths, so stages
    // saved this session load without a rebuild/copy step. Call at startup (before
    // Stages.Get(config.Stage)) and rely on Save() for stages created mid-session.
    public static void RegisterSavedStages(string saveDirAbs)
    {
        if (!Directory.Exists(saveDirAbs)) return;
        foreach (string jsonPath in Directory.GetFiles(saveDirAbs, "*.json"))
        {
            try
            {
                var cfg = JsonSerializer.Deserialize<TerrainConfig>(File.ReadAllText(jsonPath), JsonOpts);
                var spawn = cfg?.PlayerSpawn is { Length: 2 } s ? new Vector2(s[0], s[1]) : Vector2.Zero;
                Register(Path.GetFileNameWithoutExtension(jsonPath), jsonPath, spawn);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StageSaver] skipping '{jsonPath}': {ex.Message}");
            }
        }
    }

    private static void Register(string name, string jsonPathAbs, Vector2 spawn) =>
        Stages.Register(new Stage
        {
            Name          = name,
            TerrainConfig = jsonPathAbs,
            PlayerSpawn   = spawn,
            Populate      = _ => { },
        });

    private static string NextName(string dir)
    {
        int max = 0;
        foreach (string f in Directory.GetFiles(dir, "saved_*.json"))
        {
            string stem = Path.GetFileNameWithoutExtension(f);
            if (int.TryParse(stem.Substring("saved_".Length), out int n)) max = Math.Max(max, n);
        }
        return $"saved_{max + 1:000}";
    }

    private static bool AnySolid(Chunk c)
    {
        for (int tx = 0; tx < Chunk.Size; tx++)
            for (int ty = 0; ty < Chunk.Size; ty++)
                if (c.Tiles[tx, ty].IsSolid) return true;
        return false;
    }

    private static void WriteChunk(Chunk c, string path)
    {
        var sb = new StringBuilder(Chunk.Size * (Chunk.Size + 1));
        for (int ty = 0; ty < Chunk.Size; ty++)
        {
            for (int tx = 0; tx < Chunk.Size; tx++) sb.Append(TileChar(in c.Tiles[tx, ty]));
            sb.Append('\n');
        }
        File.WriteAllText(path, sb.ToString());
    }
}
