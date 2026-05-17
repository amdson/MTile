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
        => TryFind(body, chunks, bodyHalfHeight, floatHeight, ProbeSlack, out contact);

    // Probe-slack overload: airborne states (JumpingState etc.) use a wider window
    // so the jump's reference to the source surface persists through the early
    // ascent even though the body has already left the standing-spring's tight band.
    public static bool TryFind(
        PhysicsBody body,
        ChunkMap chunks,
        float bodyHalfHeight,
        float floatHeight,
        float probeSlack,
        out FloatingSurfaceDistance contact)
    {
        contact = null;
        var probe = body.Bounds.StripBelow(floatHeight + probeSlack);

        float   bestSurfaceY   = float.MaxValue;
        Vector2 bestSurfaceVel = Vector2.Zero;
        // Route through WorldQuery so dynamic shapes (moving platforms) participate
        // alongside tiles. Velocity propagates to the returned contact so the body's
        // standing-spring damping uses relative-frame velocity (no fight with carry).
        foreach (var shape in WorldQuery.SolidShapesInRect(chunks, probe))
        {
            if (shape.WorldTop < probe.Top - 1f) continue;
            if (shape.WorldTop < bestSurfaceY)
            {
                bestSurfaceY   = shape.WorldTop;
                bestSurfaceVel = shape.Velocity;
            }
        }

        if (bestSurfaceY == float.MaxValue) return false;

        contact = new FloatingSurfaceDistance(
            new Vector2(body.Position.X, bestSurfaceY),
            new Vector2(0f, -1f),
            bodyHalfHeight + floatHeight)
        {
            SurfaceVelocity = bestSurfaceVel,
            // The state-owned standing-spring contact carries the same default
            // ground friction as collision-spawned floor SDs, so the body brakes
            // when no input is held even in steady-state hovering above the floor.
            Friction = MovementConfig.Current.GroundFriction,
        };
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

        // If the body's center is already over an empty column (it's crossed a drop edge),
        // the edge is at the boundary between this column and the supporting platform. Only
        // a valid drop in direction `dir` if the supporting platform is on the -dir side —
        // otherwise the body is hanging off the *opposite* edge and moving in `dir` would
        // walk back onto a platform, not off one.
        if (!TileQuery.IsSolidAt(chunks, bodyCol * ts + ts * 0.5f, probeY))
        {
            int supportCol = bodyCol - dir;
            if (!TileQuery.IsSolidAt(chunks, supportCol * ts + ts * 0.5f, probeY)) return false;
            float edgeX0 = dir == 1 ? bodyCol * ts : (bodyCol + 1) * ts;
            corner = new Vector2(edgeX0, floorTopY);
            return true;
        }

        // bodyCol is solid above the floor: scan in dir for the first empty column.
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
