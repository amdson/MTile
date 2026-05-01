using System;
using Microsoft.Xna.Framework;

namespace MTile;

public static class WallChecker
{
    private const float ProbeSlack = 10f; // Look for a wall within this distance
    
    // Returns 1 if touching right wall, -1 if touching left wall, 0 if neither.
    public static int Check(PhysicsBody body, ChunkMap chunks, float bodyHalfWidth)
    {
        var bounds = body.Polygon.GetBounds(body.Position);
        float probeTop = bounds.Top + 2f;    // Ignore small steps
        float probeBottom = bounds.Bottom - 2f; 
        
        float probeLeft = bounds.Left - ProbeSlack;
        float probeRight = bounds.Right + ProbeSlack;

        const int chunkPixelSize = Chunk.Size * Chunk.TileSize;

        int cxMin = (int)Math.Floor(probeLeft / chunkPixelSize);
        int cxMax = (int)Math.Floor(probeRight / chunkPixelSize);
        int cyMin = (int)Math.Floor(probeTop / chunkPixelSize);
        int cyMax = (int)Math.Floor(probeBottom / chunkPixelSize);

        bool hitLeft = false;
        bool hitRight = false;

        for (int cx = cxMin; cx <= cxMax; cx++)
        for (int cy = cyMin; cy <= cyMax; cy++)
        {
            var chunkPos = new Point(cx, cy);
            if (!chunks.TryGet(chunkPos, out var chunk)) continue;

            float chunkOriginX = cx * chunkPixelSize;
            float chunkOriginY = cy * chunkPixelSize;

            int txMin = Math.Max(0, (int)Math.Floor((probeLeft - chunkOriginX) / Chunk.TileSize));
            int txMax = Math.Min(Chunk.Size - 1, (int)Math.Floor((probeRight - chunkOriginX) / Chunk.TileSize));
            int tyMin = Math.Max(0, (int)Math.Floor((probeTop - chunkOriginY) / Chunk.TileSize));
            int tyMax = Math.Min(Chunk.Size - 1, (int)Math.Floor((probeBottom - chunkOriginY) / Chunk.TileSize));

            if (txMin > txMax || tyMin > tyMax) continue;

            for (int tx = txMin; tx <= txMax; tx++)
            for (int ty = tyMin; ty <= tyMax; ty++)
            {
                if (!chunk.Tiles[tx, ty].IsSolid) continue;

                float tileLeftX = chunkOriginX + tx * Chunk.TileSize;
                float tileRightX = tileLeftX + Chunk.TileSize;

                // Ignore tiles we are inside or above/below
                if (tileRightX <= bounds.Left) 
                {
                    if (tileRightX >= probeLeft) hitLeft = true;
                }
                else if (tileLeftX >= bounds.Right)
                {
                    if (tileLeftX <= probeRight) hitRight = true;
                }
            }
        }

        if (hitRight) return 1;
        if (hitLeft) return -1;
        return 0;
    }
}
