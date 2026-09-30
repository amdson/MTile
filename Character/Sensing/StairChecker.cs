using System;
using Microsoft.Xna.Framework;

namespace MTile;

// STAIR PROBE — is the body still on an intact flight of stairs? (StairClimbState's
// continuation, 2026-09-21.) Pure function of body + tiles + direction, like GroundChecker:
//   Tread     the surface tile under the body's centre column — the highest solid with air
//             above it whose top lies within [bottom − 1, bottom + reach] of the body's
//             bottom edge. A tile deleted from under the body leaves only the buried tile a
//             riser lower, out of reach: the probe fails and the climb is over.
//   NextRiser whether the column ahead carries the next step: solid one tile above the
//             tread with air above that. A missing riser (deleted, or the landing) means
//             the flight ends here and the state yields to Standing.
//   OnFlight  whether the column behind is one tile LOWER — the tread is a step of the
//             flight, not the runway in front of it. On the runway the state is carried by
//             the corridor's view of the flight ahead; on a tread only the next riser counts,
//             so a tread deleted ahead ends the climb instead of being walked into.
// The generic ground probe (GroundChecker) is the wrong tool for this: its window is the
// body's rest band, and a body riding the corner line at hover height drifts out of it for
// a frame at every riser.
public readonly struct StairSupport
{
    public readonly float TreadTop;    // world y of the tread's top edge
    public readonly int   TreadRow;    // its tile row
    public readonly bool  NextRiser;   // the column ahead steps up one tile
    public readonly bool  OnFlight;    // the column behind steps down one tile
    public StairSupport(float treadTop, int treadRow, bool nextRiser, bool onFlight)
    { TreadTop = treadTop; TreadRow = treadRow; NextRiser = nextRiser; OnFlight = onFlight; }
}

public static class StairChecker
{
    // Widest tread (columns) that still counts as a step of the flight. ONE number for both
    // gates: StairClimbState's entry (TryFindFlight's corner spacing) and this probe's
    // continuation reach must accept the same treads, or a flight the state claims is one
    // it cannot stay on.
    public const int MaxTreadColumns = 2;

    // How far below the body's bottom edge a tread still counts as carrying the climb.
    // Riding the corner line at hover height, the bounds' bottom sits 5–21 px above the
    // tread under the centre (measured on the 45° fixture: highest at the tread's far
    // edge, just before the next riser). Two radii covers that; a tread deleted from under
    // the body leaves a surface a riser lower, which is either out of reach or fails the
    // next-riser test.
    public static float TreadReach => 2f * PlayerCharacter.Radius;

    public static bool TryFind(PhysicsBody body, ChunkMap chunks, int dir, out StairSupport support)
    {
        support = default;
        int ts = Chunk.TileSize;
        float bottom = body.Bounds.Bottom;
        int col = (int)MathF.Floor(body.Position.X / ts);
        int rowFrom = (int)MathF.Floor((bottom - 1f) / ts);
        int rowTo   = (int)MathF.Floor((bottom + TreadReach) / ts);
        for (int row = rowFrom; row <= rowTo; row++)
        {
            if (!Solid(chunks, col, row) || Solid(chunks, col, row - 1)) continue;   // not a surface
            float top = row * ts;
            if (top < bottom - 1f) return false;   // the surface here is above the body: it is inside the riser, not on a tread
            bool next = false, on = false;
            // Walk across the current tread, stopping at its first height change or gap.
            // Entry accepts one- and two-column treads; continuation must use that same reach.
            for (int d = 1; d <= MaxTreadColumns; d++)
            {
                int x = col + dir * d;
                if (Solid(chunks, x, row - 1))
                { next = !Solid(chunks, x, row - 2); break; }
                if (!Solid(chunks, x, row)) break;
            }
            for (int d = 1; d <= MaxTreadColumns; d++)
            {
                int x = col - dir * d;
                if (!Solid(chunks, x, row))
                { on = Solid(chunks, x, row + 1); break; }
                if (Solid(chunks, x, row - 1)) break;
            }
            support = new StairSupport(top, row, next, on);
            return true;
        }
        return false;
    }

    private static bool Solid(ChunkMap chunks, int gtx, int gty)
        => TileQuery.IsSolidAt(chunks, (gtx + 0.5f) * Chunk.TileSize, (gty + 0.5f) * Chunk.TileSize);
}
