using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

/*
Types of queries to support:
- SolidTilesInRect
- SolidTilesInArea (e.g. circle or capsule) 
- IsSolidAt (point query or tile query)
- Is(Top/Bottom/Left/Right)Exposed
- IntersectsLineSegment
- TileEdgesInRect
- TileCornersInRect
- OpenEdgesInRect (Edge for which parent tile is solid, but no solid tile borders the edge)
- OpenCornersInRect (Corner for which parent tile is solid, but no solid tile borders the corner, including diagonals)
*/

public static class TileQuery
{
    private const int ChunkPixelSize = Chunk.Size * Chunk.TileSize;

    // Convenience overload: query solid tiles overlapping a BoundingBox-defined region.
    public static IEnumerable<TileRef> SolidTilesInRect(ChunkMap chunks, BoundingBox region)
        => SolidTilesInRect(chunks, region.Left, region.Top, region.Right, region.Bottom);

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
                    yield return new TileRef(cx * Chunk.Size + tx, cy * Chunk.Size + ty);
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

    // ── Fluent query layer ────────────────────────────────────────────────
    //
    // Tiles returns a small builder struct that wraps the seed enumeration
    // plus a ChunkMap reference. Callers chain Where(...) with named filters
    // from TileFilters and close with a reduction (MaxBy / MinBy / FirstOrDefault / Any) or a
    // plain foreach. The aim is one composable, individually-testable rule
    // per Where — see Plans / discussion of the inset-corner bug for why
    // inline foreach-and-if blocks were fragile.

    // Solid tiles overlapping `region`. Only solids are enumerated: a tile has
    // a clear existence boolean (IsSolid), so walking empty cells would be waste.
    public static TileQueryChain Tiles(ChunkMap chunks, BoundingBox region)
        => new(chunks, SolidTilesInRect(chunks, region));
}
