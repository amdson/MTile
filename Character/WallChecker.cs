using Microsoft.Xna.Framework;

namespace MTile;

public static class WallChecker
{
    private const float ProbeSlack = 12f;

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

        float bestX = wallDir == 1 ? float.MaxValue : float.MinValue;
        bool  found = false;

        foreach (var tile in TileQuery.SolidTilesInRect(chunks, probeLeft, probeTop, probeRight, probeBottom))
        {
            if (wallDir == 1)
            {
                if (tile.WorldLeft < bounds.Right) continue;
                if (tile.WorldLeft < bestX) { bestX = tile.WorldLeft; found = true; }
            }
            else
            {
                if (tile.WorldRight > bounds.Left) continue;
                if (tile.WorldRight > bestX) { bestX = tile.WorldRight; found = true; }
            }
        }

        if (!found) return false;

        contact = new FloatingSurfaceDistance(
            new Vector2(bestX, body.Position.Y),
            wallDir == 1 ? new Vector2(-1f, 0f) : new Vector2(1f, 0f),
            bodyHalfWidth + floatWidth);
        return true;
    }
}
