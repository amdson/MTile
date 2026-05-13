using System;
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
        var probe = body.Bounds.StripBelow(floatHeight + ProbeSlack);

        float bestSurfaceY = float.MaxValue;
        foreach (var tile in TileQuery.SolidTilesInRect(chunks, probe))
        {
            if (tile.WorldTop < probe.Top - 1f) continue;
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

    // Given the body is grounded, locate where that floor's top row stops being solid in the given
    // direction — the platform edge the player would drop off. Returns the corner (edgeX, floorTopY):
    // edgeX is the boundary between the last solid tile column and the first empty one. Mirrors
    // CeilingChecker.TryFindExitEdge but probes the floor instead. Used by DropdownState to anchor
    // its Over SteeringRamp.
    public static bool TryFindDropEdge(PhysicsBody body, ChunkMap chunks, int dir, out Vector2 corner)
    {
        corner = default;
        if (dir != 1 && dir != -1) return false;
        if (!TryFind(body, chunks, PlayerCharacter.Radius, PlayerCharacter.Radius, out var ground)) return false;

        const int MaxScanTiles = 5;
        const int ts = Chunk.TileSize;
        float floorTopY = ground.Position.Y;
        // Sample halfway into the floor tile so a 1-tile-thick platform registers cleanly.
        float probeY = floorTopY + ts * 0.5f;
        int bodyCol = (int)MathF.Floor(body.Position.X / ts);

        for (int k = 1; k <= MaxScanTiles; k++)
        {
            int col = bodyCol + dir * k;
            float cx = col * ts + ts * 0.5f;
            if (TileQuery.IsSolidAt(chunks, cx, probeY)) continue;

            float edgeX = dir == 1 ? col * ts : (col + 1) * ts;
            corner = new Vector2(edgeX, floorTopY);
            return true;
        }
        return false;
    }
}
