using Microsoft.Xna.Framework;

namespace MTile;

public static class GroundChecker
{
    private const float ProbeSlack = 20f;

    public static bool TryFind(
        PhysicsBody body,
        ChunkMap chunks,
        float bodyHalfHeight,
        float floatHeight,
        out FloatingSurfaceDistance contact)
    {
        contact = null;

        var bounds = body.Polygon.GetBounds(body.Position);
        float probeTop    = bounds.Bottom;
        float probeBottom = probeTop + floatHeight + ProbeSlack;

        float bestSurfaceY = float.MaxValue;

        foreach (var tile in TileQuery.SolidTilesInRect(chunks, bounds.Left, probeTop, bounds.Right, probeBottom))
        {
            if (tile.WorldTop < probeTop - 1f) continue;
            if (tile.WorldTop < bestSurfaceY)
                bestSurfaceY = tile.WorldTop;
        }

        if (bestSurfaceY == float.MaxValue) return false;

        contact = new FloatingSurfaceDistance(
            new Vector2(body.Position.X, bestSurfaceY),
            new Vector2(0f, -1f),
            bodyHalfHeight + floatHeight);
        return true;
    }
}
