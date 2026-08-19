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

        // For wallDir = +1 the nearest wall is the tile with the SMALLEST WorldLeft
        // outside the body's right face; for wallDir = -1 it's the largest WorldRight
        // outside the body's left face.
        var chain = TileQuery.Tiles(chunks, probe)
            .Where(TileFilters.OutsideBodyFace(bodyFace, wallDir));
        var best = wallDir == 1
            ? chain.MinBy(t => t.WorldLeft)
            : chain.MaxBy(t => t.WorldRight);
        if (best is not { } b) return false;

        float bestX = wallDir == 1 ? b.WorldLeft : b.WorldRight;
        contact = new FloatingSurfaceDistance(
            new Vector2(bestX, body.Position.Y),
            wallDir == 1 ? new Vector2(-1f, 0f) : new Vector2(1f, 0f),
            bodyHalfWidth + floatWidth);
        return true;
    }
}
