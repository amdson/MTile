using Microsoft.Xna.Framework;

namespace MTile;

public readonly struct ExposedLowerCorner
{
    public readonly Vector2 InnerEdge; // (tileLeft, tileBottom) for wallDir=1; (tileRight, tileBottom) for -1
    public ExposedLowerCorner(Vector2 innerEdge) => InnerEdge = innerEdge;
}

public static class ExposedLowerCornerChecker
{
    private const float ProbeSlack = 15f;

    public static bool TryFind(
        PhysicsBody body,
        ChunkMap chunks,
        int wallDir,
        out ExposedLowerCorner corner)
    {
        corner = default;
        var bounds = body.Bounds;
        // Probe a side slab from the body's head down to its center — corners whose bottom is in
        // this y window are at "head height" relative to the body.
        var probe = bounds.StripBeside(wallDir, ProbeSlack).WithVerticalRange(bounds.Top, bounds.CenterY);
        float bodyFace = bounds.Side(wallDir);
        float playerHead = bounds.Top;

        float bestBottomY = float.MinValue;
        float bestX       = 0f;
        bool  found       = false;

        foreach (var tile in TileQuery.SolidTilesInRect(chunks, probe))
        {
            if (wallDir ==  1 && tile.WorldLeft  < bodyFace) continue;
            if (wallDir == -1 && tile.WorldRight > bodyFace) continue;

            if (playerHead >= tile.WorldBottom) continue;

            if (!TileQuery.IsBottomExposed(chunks, tile)) continue;

            // Clearance: tile diagonally below-inward must also be empty
            if (TileQuery.IsSolidAt(chunks, tile.WorldCenterX - wallDir * Chunk.TileSize, tile.WorldBottom + Chunk.TileSize * 0.5f)) continue;

            if (tile.WorldBottom > bestBottomY)
            {
                bestBottomY = tile.WorldBottom;
                bestX       = wallDir == 1 ? tile.WorldLeft : tile.WorldRight;
                found       = true;
            }
        }

        if (!found) return false;
        corner = new ExposedLowerCorner(new Vector2(bestX, bestBottomY));
        return true;
    }
}
