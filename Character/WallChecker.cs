using System;
using Microsoft.Xna.Framework;

namespace MTile;

public static class WallChecker
{
    private const float ProbeSlack = 12f;

    // Returns true and fills `contact` if a solid wall face is within probe range on the given side.
    // wallDir: 1 = right wall, -1 = left wall.
    // floatWidth is added to bodyHalfWidth to set the contact's MinDistance (typically 0 for walls).
    public static bool TryFind(
        PhysicsBody body,
        ChunkMap chunks,
        float bodyHalfWidth,
        float floatWidth,
        int wallDir,
        out FloatingSurfaceDistance contact)
    {
        contact = null;
        var bounds = body.Polygon.GetBounds(body.Position);

        float probeTop    = bounds.Top    + 2f;
        float probeBottom = bounds.Bottom - 2f;
        float probeLeft   = wallDir == 1 ? bounds.Right              : bounds.Left - ProbeSlack;
        float probeRight  = wallDir == 1 ? bounds.Right + ProbeSlack : bounds.Left;

        const int chunkPixelSize = Chunk.Size * Chunk.TileSize;

        float bestX = wallDir == 1 ? float.MaxValue : float.MinValue;
        bool  found = false;

        int cxMin = (int)Math.Floor(probeLeft  / chunkPixelSize);
        int cxMax = (int)Math.Floor(probeRight / chunkPixelSize);
        int cyMin = (int)Math.Floor(probeTop   / chunkPixelSize);
        int cyMax = (int)Math.Floor(probeBottom / chunkPixelSize);

        for (int cx = cxMin; cx <= cxMax; cx++)
        for (int cy = cyMin; cy <= cyMax; cy++)
        {
            var chunkPos = new Point(cx, cy);
            if (!chunks.TryGet(chunkPos, out var chunk)) continue;

            float chunkOriginX = cx * chunkPixelSize;
            float chunkOriginY = cy * chunkPixelSize;

            int txMin = Math.Max(0, (int)Math.Floor((probeLeft  - chunkOriginX) / Chunk.TileSize));
            int txMax = Math.Min(Chunk.Size - 1, (int)Math.Floor((probeRight - chunkOriginX) / Chunk.TileSize));
            int tyMin = Math.Max(0, (int)Math.Floor((probeTop    - chunkOriginY) / Chunk.TileSize));
            int tyMax = Math.Min(Chunk.Size - 1, (int)Math.Floor((probeBottom - chunkOriginY) / Chunk.TileSize));

            if (txMin > txMax || tyMin > tyMax) continue;

            for (int tx = txMin; tx <= txMax; tx++)
            for (int ty = tyMin; ty <= tyMax; ty++)
            {
                if (!chunk.Tiles[tx, ty].IsSolid) continue;

                float tileLeft  = chunkOriginX + tx * Chunk.TileSize;
                float tileRight = tileLeft + Chunk.TileSize;

                if (wallDir == 1)
                {
                    if (tileLeft < bounds.Right) continue;
                    if (tileLeft < bestX) { bestX = tileLeft; found = true; }
                }
                else
                {
                    if (tileRight > bounds.Left) continue;
                    if (tileRight > bestX) { bestX = tileRight; found = true; }
                }
            }
        }

        if (!found) return false;

        var normal   = wallDir == 1 ? new Vector2(-1f, 0f) : new Vector2(1f, 0f);
        var position = new Vector2(bestX, body.Position.Y);
        contact = new FloatingSurfaceDistance(position, normal, bodyHalfWidth + floatWidth);
        return true;
    }
}
