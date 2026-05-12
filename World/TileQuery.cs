using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

public readonly struct TileRef
{
    public readonly float WorldLeft;
    public readonly float WorldTop;

    public float WorldRight   => WorldLeft + Chunk.TileSize;
    public float WorldBottom  => WorldTop  + Chunk.TileSize;
    public float WorldCenterX => WorldLeft + Chunk.TileSize * 0.5f;
    public float WorldCenterY => WorldTop  + Chunk.TileSize * 0.5f;

    public TileRef(float left, float top) { WorldLeft = left; WorldTop = top; }
}

public static class TileQuery
{
    private const int ChunkPixelSize = Chunk.Size * Chunk.TileSize;

    public static IEnumerable<TileRef> SolidTilesInRect(
        ChunkMap chunks, float left, float top, float right, float bottom)
    {
        int cxMin = (int)Math.Floor(left   / ChunkPixelSize);
        int cxMax = (int)Math.Floor(right  / ChunkPixelSize);
        int cyMin = (int)Math.Floor(top    / ChunkPixelSize);
        int cyMax = (int)Math.Floor(bottom / ChunkPixelSize);

        for (int cx = cxMin; cx <= cxMax; cx++)
        for (int cy = cyMin; cy <= cyMax; cy++)
        {
            if (!chunks.TryGet(new Point(cx, cy), out var chunk)) continue;

            float ox = cx * ChunkPixelSize;
            float oy = cy * ChunkPixelSize;

            int txMin = Math.Max(0,              (int)Math.Floor((left   - ox) / Chunk.TileSize));
            int txMax = Math.Min(Chunk.Size - 1, (int)Math.Floor((right  - ox) / Chunk.TileSize));
            int tyMin = Math.Max(0,              (int)Math.Floor((top    - oy) / Chunk.TileSize));
            int tyMax = Math.Min(Chunk.Size - 1, (int)Math.Floor((bottom - oy) / Chunk.TileSize));

            if (txMin > txMax || tyMin > tyMax) continue;

            for (int tx = txMin; tx <= txMax; tx++)
            for (int ty = tyMin; ty <= tyMax; ty++)
            {
                if (chunk.Tiles[tx, ty].IsSolid)
                    yield return new TileRef(ox + tx * Chunk.TileSize, oy + ty * Chunk.TileSize);
            }
        }
    }

    public static bool IsSolidAt(ChunkMap chunks, float worldX, float worldY)
    {
        int cx = (int)Math.Floor(worldX / ChunkPixelSize);
        int cy = (int)Math.Floor(worldY / ChunkPixelSize);
        if (!chunks.TryGet(new Point(cx, cy), out var chunk)) return false;
        int tx = Math.Clamp((int)Math.Floor((worldX - cx * ChunkPixelSize) / Chunk.TileSize), 0, Chunk.Size - 1);
        int ty = Math.Clamp((int)Math.Floor((worldY - cy * ChunkPixelSize) / Chunk.TileSize), 0, Chunk.Size - 1);
        return chunk.Tiles[tx, ty].IsSolid;
    }

    public static bool IsTopExposed(ChunkMap chunks, TileRef tile)
        => !IsSolidAt(chunks, tile.WorldCenterX, tile.WorldTop    - Chunk.TileSize * 0.5f);

    public static bool IsBottomExposed(ChunkMap chunks, TileRef tile)
        => !IsSolidAt(chunks, tile.WorldCenterX, tile.WorldBottom + Chunk.TileSize * 0.5f);
}
