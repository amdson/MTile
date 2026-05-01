using System;
using Microsoft.Xna.Framework;

namespace MTile;

public static class GroundChecker
{
    // How far below body bottom we search for ground beyond the float height.
    private const float ProbeSlack = 20f;

    // Returns true and fills `contact` if flat ground is within probe range below the body.
    // floatHeight is the desired gap between body bottom and surface.
    // minDistance stored on the contact = bodyHalfHeight + floatHeight (body-center to surface).
    public static bool TryFind(
        PhysicsBody body,
        ChunkMap chunks,
        float bodyHalfHeight,
        float floatHeight,
        out FloatingSurfaceDistance contact)
    {
        contact = null;

        var bounds = body.Polygon.GetBounds(body.Position);
        float probeTop = bounds.Bottom;
        float probeBottom = probeTop + floatHeight + ProbeSlack;

        const int chunkPixelSize = Chunk.Size * Chunk.TileSize;

        int cxMin = (int)Math.Floor((float)bounds.Left / chunkPixelSize);
        int cxMax = (int)Math.Floor((float)bounds.Right / chunkPixelSize);
        int cyMin = (int)Math.Floor(probeTop / chunkPixelSize);
        int cyMax = (int)Math.Floor(probeBottom / chunkPixelSize);

        float bestSurfaceY = float.MaxValue;

        for (int cx = cxMin; cx <= cxMax; cx++)
        for (int cy = cyMin; cy <= cyMax; cy++)
        {
            var chunkPos = new Point(cx, cy);
            if (!chunks.TryGet(chunkPos, out var chunk)) continue;

            float chunkOriginX = cx * chunkPixelSize;
            float chunkOriginY = cy * chunkPixelSize;

            int txMin = Math.Max(0, (int)Math.Floor((bounds.Left - chunkOriginX) / Chunk.TileSize));
            int txMax = Math.Min(Chunk.Size - 1, (int)Math.Floor((bounds.Right - chunkOriginX) / Chunk.TileSize));
            int tyMin = Math.Max(0, (int)Math.Floor((probeTop - chunkOriginY) / Chunk.TileSize));
            int tyMax = Math.Min(Chunk.Size - 1, (int)Math.Floor((probeBottom - chunkOriginY) / Chunk.TileSize));

            if (txMin > txMax || tyMin > tyMax) continue;

            for (int tx = txMin; tx <= txMax; tx++)
            for (int ty = tyMin; ty <= tyMax; ty++)
            {
                if (!chunk.Tiles[tx, ty].IsSolid) continue;

                float tileTopY = chunkOriginY + ty * Chunk.TileSize;
                // Only count top faces at or below body bottom (not tiles the body is inside).
                if (tileTopY < probeTop - 1f) continue;
                if (tileTopY < bestSurfaceY)
                    bestSurfaceY = tileTopY;
            }
        }

        if (bestSurfaceY == float.MaxValue) return false;

        contact = new FloatingSurfaceDistance(
            new Vector2(body.Position.X, bestSurfaceY),
            new Vector2(0f, -1f),
            bodyHalfHeight + floatHeight);
        return true;
    }
}
