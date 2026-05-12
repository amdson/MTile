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
        var bounds = body.Polygon.GetBounds(body.Position);

        float probeLeft   = wallDir == 1 ? bounds.Right              : bounds.Left - ProbeSlack;
        float probeRight  = wallDir == 1 ? bounds.Right + ProbeSlack : bounds.Left;
        float probeTop    = body.Position.Y - 2f * PlayerCharacter.Radius;
        float probeBottom = body.Position.Y;

        float playerHead  = body.Position.Y - PlayerCharacter.Radius;
        float bestBottomY = float.MinValue;
        float bestX       = 0f;
        bool  found       = false;

        foreach (var tile in TileQuery.SolidTilesInRect(chunks, probeLeft, probeTop, probeRight, probeBottom))
        {
            if (wallDir ==  1 && tile.WorldLeft  < bounds.Right) continue;
            if (wallDir == -1 && tile.WorldRight > bounds.Left)  continue;

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
