using System;
using Microsoft.Xna.Framework;

namespace MTile;

public static class CeilingChecker
{
    private const float ProbeSlack = 20f;

    public static bool TryFind(PhysicsBody body, ChunkMap chunks, out FloatingSurfaceDistance contact)
    {
        contact = null;
        var probe = body.Bounds.StripAbove(ProbeSlack);

        float bestCeilY = float.MinValue;
        foreach (var tile in TileQuery.SolidTilesInRect(chunks, probe))
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

    // Given the body is under a ceiling, locate where that ceiling slab's bottom row stops being
    // solid in the given direction. Returns the slab's far edge — the "lower corner" the player
    // will pass under when exiting — as (edgeX, ceilingBottomY). edgeX is the boundary between the
    // last solid tile column and the first empty one, so a SteeringRamp { Sense=Under, ForwardDir=dir,
    // Corner=edgeX } sees the corner ahead-and-up while the body is still under the slab, and behind
    // (⇒ inert) once it has passed. Returns false if the slab doesn't end within MaxScanTiles (so
    // CoveredJumpState's "slide out and jump" wouldn't help — the player is too deep under to exit).
    //
    // Replaces the role ExposedLowerCornerChecker was playing in CoveredJumpState's precondition:
    // that checker can only see slabs whose bottom is within ~Radius of the head, which is
    // unsatisfiable for a grounded body on a tile-aligned floor (the head sits 29px below the
    // nearest tile boundary). This checker derives the corner from the ceiling the body is
    // actually under (which TryFind detects fine at standing-rest height).
    public static bool TryFindExitEdge(PhysicsBody body, ChunkMap chunks, int dir, out Vector2 corner)
    {
        corner = default;
        if (dir != 1 && dir != -1) return false;
        if (!TryFind(body, chunks, out var ceiling)) return false;

        const int MaxScanTiles = 5;
        const int ts = Chunk.TileSize;
        float ceilingBottomY = ceiling.Position.Y;
        // Sample halfway up into the slab so a 1-tile-thick ceiling registers cleanly.
        float probeY = ceilingBottomY - ts * 0.5f;
        int bodyCol = (int)MathF.Floor(body.Position.X / ts);

        // Start at k=0 so the body's own column is checked. If the body has already
        // crossed the corner (its center is in the empty column past the ceiling slab),
        // we still need to report the slab's actual edge — not skip past it to the
        // next-further-out empty column, which would put the reported corner way out
        // in open air and make IsStickingOut spuriously return false.
        for (int k = 0; k <= MaxScanTiles; k++)
        {
            int col = bodyCol + dir * k;
            float cx = col * ts + ts * 0.5f;
            if (TileQuery.IsSolidAt(chunks, cx, probeY)) continue;

            // First empty column in dir — boundary between (col - dir, col).
            float edgeX = dir == 1 ? col * ts : (col + 1) * ts;
            corner = new Vector2(edgeX, ceilingBottomY);
            return true;
        }
        return false;
    }
}
