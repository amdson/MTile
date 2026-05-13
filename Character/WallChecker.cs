using Microsoft.Xna.Framework;

namespace MTile;

public static class WallChecker
{
    private const float ProbeSlack = 12f;
    // Pull the top/bottom faces inward so floor / ceiling tiles barely touching the body's
    // upper/lower vertices don't get reported as walls.
    private const float VerticalInset = 2f;

    public static bool TryFind(
        PhysicsBody body,
        ChunkMap chunks,
        float bodyHalfWidth,
        float floatWidth,
        int wallDir,
        out FloatingSurfaceDistance contact)
    {
        contact = null;
        var bounds = body.Bounds;
        var probe = bounds.InsetVertical(VerticalInset).StripBeside(wallDir, ProbeSlack);
        float bodyFace = bounds.Side(wallDir);

        float bestX = wallDir == 1 ? float.MaxValue : float.MinValue;
        bool  found = false;

        foreach (var tile in TileQuery.SolidTilesInRect(chunks, probe))
        {
            if (wallDir == 1)
            {
                if (tile.WorldLeft < bodyFace) continue;
                if (tile.WorldLeft < bestX) { bestX = tile.WorldLeft; found = true; }
            }
            else
            {
                if (tile.WorldRight > bodyFace) continue;
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
