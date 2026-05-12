using Microsoft.Xna.Framework;

namespace MTile;

public static class CeilingChecker
{
    private const float ProbeSlack = 20f;

    public static bool TryFind(PhysicsBody body, ChunkMap chunks, out FloatingSurfaceDistance contact)
    {
        contact = null;
        var bounds = body.Polygon.GetBounds(body.Position);
        float probeBottom = bounds.Top;
        float probeTop    = probeBottom - ProbeSlack;

        float bestCeilY = float.MinValue;

        foreach (var tile in TileQuery.SolidTilesInRect(chunks, bounds.Left, probeTop, bounds.Right, probeBottom))
        {
            if (tile.WorldBottom > bestCeilY)
                bestCeilY = tile.WorldBottom;
        }

        if (bestCeilY == float.MinValue) return false;

        // Constraint: body.Y - bestCeilY >= Radius  →  body center stays Radius below ceiling bottom.
        contact = new FloatingSurfaceDistance(
            new Vector2(body.Position.X, bestCeilY),
            new Vector2(0f, 1f),
            PlayerCharacter.Radius);
        return true;
    }
}
