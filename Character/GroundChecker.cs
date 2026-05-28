using System;
using Microsoft.Xna.Framework;

namespace MTile;

public static class GroundChecker
{
    public const float ProbeSlack = 20f;
    // Pull the left/right faces inward so a wall tile the body's side vertex is merely flush
    // against (its column shares the strip-below's boundary) isn't reported as a floor under the
    // body. Mirror of WallChecker.VerticalInset — without it, pressing jump while wall-sliding
    // reads as grounded and fires a normal jump.
    private const float HorizontalInset = 2f;

    public static bool TryFind(
        PhysicsBody body,
        ChunkMap chunks,
        float bodyHalfHeight,
        float floatHeight,
        out FloatingSurfaceDistance contact)
        => TryFind(body, chunks, bodyHalfHeight, floatHeight, ProbeSlack, 0f, float.MaxValue, out contact);

    // Probe-slack overload (no dt prediction) — kept for callers that don't have
    // a meaningful step duration to project against.
    public static bool TryFind(
        PhysicsBody body,
        ChunkMap chunks,
        float bodyHalfHeight,
        float floatHeight,
        float probeSlack,
        out FloatingSurfaceDistance contact)
        => TryFind(body, chunks, bodyHalfHeight, floatHeight, probeSlack, 0f, float.MaxValue, out contact);

    public static bool TryFind(
        PhysicsBody body,
        ChunkMap chunks,
        float bodyHalfHeight,
        float floatHeight,
        float probeSlack,
        float dt,
        out FloatingSurfaceDistance contact)
        => TryFind(body, chunks, bodyHalfHeight, floatHeight, probeSlack, dt, float.MaxValue, out contact);

    // Probe-slack + end-of-step prediction + engage-velocity cap overload.
    //
    // `dt` projects each candidate surface forward by `Velocity.Y * dt` so a
    // moving sprout about to rise past a flush static tile wins the comparison
    // outright (no iteration-order tie-break). The REPORTED FSD still pins to
    // the *current* top — the projection is only used for ranking.
    //
    // `maxEngageVnRel` caps the relative normal speed at which the FSD is
    // allowed to engage. Above this cap the function returns false; the body
    // stays in airborne semantics so the swept-tile-collision path is what
    // actually resolves the impact — letting ImpactDamage.BounceRestitution
    // fire if configured, and not spring-catching a body that has just bounced
    // or jumped away. With cap = float.MaxValue (the default-forwarding
    // overloads above), this gate is off and behavior is identical to before.
    public static bool TryFind(
        PhysicsBody body,
        ChunkMap chunks,
        float bodyHalfHeight,
        float floatHeight,
        float probeSlack,
        float dt,
        float maxEngageVnRel,
        out FloatingSurfaceDistance contact)
    {
        contact = null;
        var probe = body.Bounds.InsetHorizontal(HorizontalInset).StripBelow(floatHeight + probeSlack);

        float   bestProjectedTop = float.MaxValue;
        float   bestSurfaceY     = float.MaxValue;
        Vector2 bestSurfaceVel   = Vector2.Zero;
        // Route through WorldQuery so dynamic shapes (moving platforms) participate
        // alongside tiles. Velocity propagates to the returned contact so the body's
        // standing-spring damping uses relative-frame velocity (no fight with carry).
        foreach (var shape in WorldQuery.SolidShapesInRect(chunks, probe))
        {
            if (shape.WorldTop < probe.Top - 1f) continue;
            float projectedTop = shape.WorldTop + shape.Velocity.Y * dt;
            if (projectedTop < bestProjectedTop)
            {
                bestProjectedTop = projectedTop;
                bestSurfaceY     = shape.WorldTop;
                bestSurfaceVel   = shape.Velocity;
            }
        }

        if (bestSurfaceY == float.MaxValue) return false;

        // Engage cap: relative speed along the ground normal (0,-1). Both
        // directions count — body approaching too hard skips FSD so the
        // swept impact path can fire bounce; body receding too fast (post-
        // bounce / mid-jump) shouldn't be spring-caught either.
        float relSpeed = MathF.Abs(body.Velocity.Y - bestSurfaceVel.Y);
        if (relSpeed > maxEngageVnRel) return false;

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
