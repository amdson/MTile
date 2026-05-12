using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;

namespace MTile;

public class PerlinConfig
{
    public int   Seed            { get; set; } = 0;
    public float Scale           { get; set; } = 0.04f;
    public int   Octaves         { get; set; } = 4;
    public float Persistence     { get; set; } = 0.5f;
    public float Lacunarity      { get; set; } = 2.0f;
    public int   GroundLevel     { get; set; } = 0;    // baseline in world tile Y coords
    public int   HeightAmplitude { get; set; } = 8;    // ± tiles of variation
}

public class TerrainConfig
{
    public int Extents { get; set; } = 2;
    public Dictionary<string, string> ChunkFiles { get; set; } = new();
    public List<TerrainRule> Rules { get; set; } = new();
    public PerlinConfig Perlin { get; set; } = null; // when set, replaces Rules for unassigned chunks
}

public class TerrainRule
{
    public string Condition { get; set; } = "";
    public bool IsSolid { get; set; } = true;
}

public static class TerrainLoader
{
    public static void Load(string configPath, ChunkMap chunks)
    {
        string dir = Path.GetDirectoryName(configPath);
        string json = File.ReadAllText(configPath);
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var config = JsonSerializer.Deserialize<TerrainConfig>(json, opts);

        for (int cx = -config.Extents; cx <= config.Extents; cx++)
        {
            for (int cy = -config.Extents; cy <= config.Extents; cy++)
            {
                var chunk = new Chunk { ChunkPos = new Point(cx, cy) };
                string key = $"{cx},{cy}";
                
                if (config.ChunkFiles.TryGetValue(key, out string filename))
                    LoadChunkFromFile(chunk, Path.Combine(dir, filename));
                else if (config.Perlin != null)
                    GenerateWithPerlin(chunk, config.Perlin);
                else
                    ApplyRules(chunk, config.Rules);
                chunks[chunk.ChunkPos] = chunk;
            }
        }
    }
    
    private static void LoadChunkFromFile(Chunk chunk, string path)
    {
        if (!File.Exists(path)) return;
        string[] lines = File.ReadAllLines(path);
        for (int ty = 0; ty < Chunk.Size; ty++)
        {
            if (ty >= lines.Length) break;
            string line = lines[ty];
            for (int tx = 0; tx < Chunk.Size; tx++)
            {
                if (tx >= line.Length) break;
                chunk.Tiles[tx, ty].IsSolid = line[tx] == 'X' || line[tx] == 'x';
            }
        }
    }
    
    private static void GenerateWithPerlin(Chunk chunk, PerlinConfig cfg)
    {
        var noise = new PerlinNoise1D(cfg.Seed);
        for (int tx = 0; tx < Chunk.Size; tx++)
        {
            int worldX    = chunk.ChunkPos.X * Chunk.Size + tx;
            float h       = noise.Fbm(worldX * cfg.Scale, cfg.Octaves, cfg.Persistence, cfg.Lacunarity);
            int surfaceY  = cfg.GroundLevel + (int)MathF.Round(h * cfg.HeightAmplitude);

            for (int ty = 0; ty < Chunk.Size; ty++)
            {
                int worldY = chunk.ChunkPos.Y * Chunk.Size + ty;
                chunk.Tiles[tx, ty].IsSolid = worldY >= surfaceY;
            }
        }
    }

    private static void ApplyRules(Chunk chunk, List<TerrainRule> rules)
    {
        for (int tx = 0; tx < Chunk.Size; tx++)
        {
            for (int ty = 0; ty < Chunk.Size; ty++)
            {
                bool isSolid = false;
                
                int worldX = chunk.ChunkPos.X * Chunk.Size + tx;
                int worldY = chunk.ChunkPos.Y * Chunk.Size + ty;
                
                foreach(var rule in rules)
                {
                    if (EvaluateRule(rule.Condition, chunk.ChunkPos.X, chunk.ChunkPos.Y, worldX, worldY))
                    {
                        isSolid = rule.IsSolid;
                    }
                }
                chunk.Tiles[tx, ty].IsSolid = isSolid;
            }
        }
    }
    
    private static bool EvaluateRule(string condition, int cx, int cy, int worldX, int worldY)
    {
        if (string.IsNullOrWhiteSpace(condition)) return false;
        
        var parts = condition.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return false;
        
        string varName = parts[0].ToLower();
        string op = parts[1];
        if (!int.TryParse(parts[2], out int val)) return false;
        
        int left = 0;
        if (varName == "x") left = worldX;
        else if (varName == "y") left = worldY;
        else if (varName == "cx") left = cx;
        else if (varName == "cy") left = cy;
        else return false;
        
        return op switch
        {
            ">" => left > val,
            "<" => left < val,
            ">=" => left >= val,
            "<=" => left <= val,
            "==" => left == val,
            "!=" => left != val,
            _ => false
        };
    }
}