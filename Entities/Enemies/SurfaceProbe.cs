using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Read-only terrain probe shared by every surface-aware movement state
// (EnemyClingMoveState, EnemyLeapState, EnemyHopState). They all key off one
// question — "is a solid tile within AnchorDist of `pos`?" — and if their
// answers ever diverge the states hand off at the wrong moment, so a body
// either falls through the world or sticks to thin air. Keeping the predicate
// in one place makes that invariant a single line of code rather than a
// convention three files have to remember.
//
// Sampling only, never mutating. That is what makes it safe to call from a
// state's precondition scan, where evaluation order isn't guaranteed.
internal static class SurfaceProbe
{
    // 16-sample ring at AnchorDist. Arc gap at radius 14 is ~5.5 px, well
    // below the 16-px tile size, so a tile within AnchorDist of `pos` is
    // always hit by at least one probe.
    public static bool IsAnchored(Vector2 pos, ChunkMap chunks, float anchorDist, int numSamples)
    {
        if (chunks == null) return false;
        for (int i = 0; i < numSamples; i++)
        {
            float a = i * MathHelper.TwoPi / numSamples;
            float px = pos.X + MathF.Cos(a) * anchorDist;
            float py = pos.Y + MathF.Sin(a) * anchorDist;
            if (TileQuery.IsSolidAt(chunks, px, py)) return true;
        }
        return false;
    }
}
