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

    public static bool IsTopExposed(ChunkMap chunks, TileRef tile)
        => !IsSolidAt(chunks, tile.WorldCenterX, tile.WorldTop    - Chunk.TileSize * 0.5f);

    public static bool IsBottomExposed(ChunkMap chunks, TileRef tile)
        => !IsSolidAt(chunks, tile.WorldCenterX, tile.WorldBottom + Chunk.TileSize * 0.5f);

    // ── Fluent query layer ────────────────────────────────────────────────
    //
    // Tiles / Edges / Corners return small builder structs that wrap the
    // seed enumeration plus a ChunkMap reference. Callers chain Where(...)
    // with named filters from TileFilters / EdgeFilters / CornerFilters and
    // close with a reduction (MaxBy / MinBy / FirstOrDefault / Any) or a
    // plain foreach. The aim is one composable, individually-testable rule
    // per Where — see Plans / discussion of the inset-corner bug for why
    // inline foreach-and-if blocks were fragile.

    // Solid tiles overlapping `region`. Asymmetric on purpose: tiles have a
    // clear existence boolean (IsSolid), so it's wasteful to enumerate empty
    // cells. Edges and corners are geometric constructs that belong to a
    // solid tile, so they enumerate all four faces / corners of every solid
    // and let predicates decide which are interesting.
    public static TileQueryChain Tiles(ChunkMap chunks, BoundingBox region)
        => new(chunks, SolidTilesInRect(chunks, region));

    // All four edges of every solid tile in `region`. Filter with
    // EdgeFilters.IsOpen for outward-facing wall / floor / ceiling faces,
    // or EdgeFilters.Type(...) to keep one side only.
    public static EdgeQueryChain Edges(ChunkMap chunks, BoundingBox region)
        => new(chunks, EnumerateEdges(chunks, region));

    // All four corners of every solid tile in `region`. CornerFilters.IsOpen
    // narrows to convex (outward) corners — the precondition for vault /
    // overcrop ramps in ParkourState.
    public static CornerQueryChain Corners(ChunkMap chunks, BoundingBox region)
        => new(chunks, EnumerateCorners(chunks, region));

    private static IEnumerable<EdgeRef> EnumerateEdges(ChunkMap chunks, BoundingBox region)
    {
        foreach (var tile in SolidTilesInRect(chunks, region))
        {
            yield return new EdgeRef(tile, EdgeType.Top);
            yield return new EdgeRef(tile, EdgeType.Bottom);
            yield return new EdgeRef(tile, EdgeType.Left);
            yield return new EdgeRef(tile, EdgeType.Right);
        }
    }

    private static IEnumerable<CornerRef> EnumerateCorners(ChunkMap chunks, BoundingBox region)
    {
        foreach (var tile in SolidTilesInRect(chunks, region))
        {
            yield return new CornerRef(tile, CornerType.TopLeft);
            yield return new CornerRef(tile, CornerType.TopRight);
            yield return new CornerRef(tile, CornerType.BottomLeft);
            yield return new CornerRef(tile, CornerType.BottomRight);
        }
    }
}
