using Microsoft.Xna.Framework;

namespace MTile;

public readonly struct ExposedCorner
{
    public readonly Vector2 InnerEdge; // (tileLeft, tileTop) for wallDir=1; (tileRight, tileTop) for -1
    public ExposedCorner(Vector2 innerEdge) => InnerEdge = innerEdge;
}

public static class ExposedUpperCornerChecker
{
    private const float ProbeSlack          = 18f;
    // Reject corners whose top is more than this far below the body's center.
    // Set to body radius so a body at "raw ground rest" Y (just touching the
    // ground without spring lift) can still vault a 1-block obstacle.
    private const float MaxTopProbeDistance = 16f;
    // Reject corners whose top is at or above the body's bottom — body has
    // already cleared the corner, no vault is needed. Approximates apothem
    // of the hexagonal body (slightly less than radius).
    private const float MinBodyDepthBelowCorner = 10f;

    // Vault range: center down to feet
    public static bool TryFind(PhysicsBody body, ChunkMap chunks, int wallDir, out ExposedCorner corner)
    {
        float probeTop    = body.Position.Y;
        float probeBottom = body.Position.Y + PlayerCharacter.Radius;
        return TryFindInRange(body, chunks, wallDir, probeTop, probeBottom, MaxTopProbeDistance, out corner);
    }

    // Ledge grab range: point probe at head height
    public static bool TryFindAboveHead(PhysicsBody body, ChunkMap chunks, int wallDir, out ExposedCorner corner)
    {
        float probeY = body.Position.Y - PlayerCharacter.Radius;
        return TryFindInRange(body, chunks, wallDir, probeY, probeY, float.MaxValue, out corner);
    }

    private static bool TryFindInRange(
        PhysicsBody body,
        ChunkMap chunks,
        int wallDir,
        float probeTop,
        float probeBottom,
        float maxTopDist,
        out ExposedCorner corner)
    {
        corner = default;
        var bounds = body.Polygon.GetBounds(body.Position);

        float probeLeft  = wallDir == 1 ? bounds.Right              : bounds.Left - ProbeSlack;
        float probeRight = wallDir == 1 ? bounds.Right + ProbeSlack : bounds.Left;

        float bestTopY = float.MinValue;
        float bestX    = 0f;
        bool  found    = false;

        foreach (var tile in TileQuery.SolidTilesInRect(chunks, probeLeft, probeTop, probeRight, probeBottom))
        {
            if (wallDir ==  1 && tile.WorldLeft  < bounds.Right) continue;
            if (wallDir == -1 && tile.WorldRight > bounds.Left)  continue;

            if (!TileQuery.IsTopExposed(chunks, tile)) continue;

            // Clearance: tile diagonally above-inward must also be empty
            if (TileQuery.IsSolidAt(chunks, tile.WorldCenterX - wallDir * Chunk.TileSize, tile.WorldTop - Chunk.TileSize * 0.5f)) continue;

            if (tile.WorldTop > bestTopY)
            {
                bestTopY = tile.WorldTop;
                bestX    = wallDir == 1 ? tile.WorldLeft : tile.WorldRight;
                found    = true;
            }
        }

        if (!found) return false;
        if (probeTop - bestTopY > maxTopDist) return false;
        // Body already above the corner (its bottom edge is past the tile top) —
        // nothing to vault, return false so ParkourState doesn't re-fire after
        // a successful climb.
        if (bestTopY - probeTop >= MinBodyDepthBelowCorner) return false;
        corner = new ExposedCorner(new Vector2(bestX, bestTopY));
        return true;
    }
}
