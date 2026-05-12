using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile.Tests.Sim;

// Builds a ChunkMap from a multi-line ASCII string.
// 'X' = solid tile, any other printable character = empty.
// Each character is one tile (16×16 px). Leading whitespace is stripped.
// originTileX/Y are the absolute tile coordinates of the top-left ASCII character.
public static class SimTerrain
{
    public static ChunkMap FromAscii(string ascii, int originTileX = 0, int originTileY = 0)
    {
        var chunks = new ChunkMap();
        var lines = ParseLines(ascii);

        for (int row = 0; row < lines.Count; row++)
        {
            string line = lines[row];
            for (int col = 0; col < line.Length; col++)
            {
                bool solid = line[col] == 'X' || line[col] == 'x';
                if (!solid) continue;

                // Absolute tile coordinate
                int tx = originTileX + col;
                int ty = originTileY + row;

                // Chunk coord (floor division, handles negatives correctly)
                int cx = FloorDiv(tx, Chunk.Size);
                int cy = FloorDiv(ty, Chunk.Size);

                // Within-chunk tile coord
                int ltx = tx - cx * Chunk.Size;
                int lty = ty - cy * Chunk.Size;

                var chunkPos = new Point(cx, cy);
                if (!chunks.TryGet(chunkPos, out var chunk))
                {
                    chunk = new Chunk { ChunkPos = chunkPos };
                    chunks[chunkPos] = chunk;
                }
                chunk.Tiles[ltx, lty].IsSolid = true;
            }
        }

        return chunks;
    }

    // Returns world-space pixel position of the top-left of tile (originTileX+col, originTileY+row).
    public static Vector2 TileWorldPos(int tileX, int tileY)
        => new Vector2(tileX * Chunk.TileSize, tileY * Chunk.TileSize);

    // Floor-divides n by d, rounding toward negative infinity.
    private static int FloorDiv(int n, int d)
    {
        int q = n / d;
        // Adjust if signs differ and there's a remainder
        if ((n ^ d) < 0 && q * d != n) q--;
        return q;
    }

    private static List<string> ParseLines(string ascii)
    {
        var result = new List<string>();
        foreach (var raw in ascii.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            // Strip common leading whitespace (first non-empty line sets the indent)
            result.Add(line);
        }

        // Remove common leading whitespace
        int indent = int.MaxValue;
        foreach (var l in result)
        {
            int spaces = 0;
            while (spaces < l.Length && l[spaces] == ' ') spaces++;
            if (spaces < l.Length) indent = Math.Min(indent, spaces);
        }
        if (indent == int.MaxValue) indent = 0;

        var trimmed = new List<string>(result.Count);
        foreach (var l in result)
            trimmed.Add(l.Length > indent ? l.Substring(indent) : "");
        return trimmed;
    }
}
