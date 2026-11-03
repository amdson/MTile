using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Terrain reads shared by the block-offense maneuvers (Eruption, BlockBurst). Private to
// these maneuvers rather than on BotView so the parallel bot work merges cleanly; a
// candidate to promote onto BotView later.
internal static class BotTerrainSense
{
    // Open rows above a body's feet in its column, capped at `cap`: walk down from the
    // body centre to the first solid cell (the floor), then count empty cells upward
    // from the one just above it until something solid. A standing body needs ~3 rows
    // (StandingHeight ≈ 2R + float ≈ 30 px vs 11 px tiles).
    //
    // Why it matters to a bot: sprouts that grow into a body which CAN'T be pushed out of
    // the way are broken by PhysicsWorld.CrushOverlappingSprouts and billed to the body
    // as SproutCrushDamage (0.5 HP per cell, with stun). In open space the same sprouts
    // just lift or shove. So "how boxed in is the target?" is the difference between a
    // displacement tool and a damage tool for every terrain-growth move.
    public static int Clearance(BotView v, Vector2 body, int cap = 6)
    {
        var c = BotView.Cell(body);
        int floor = c.Y;
        for (int i = 0; i < 4 && !v.Solid(c.X, floor); i++) floor++;
        if (!v.Solid(c.X, floor)) return cap;   // airborne / no floor under them
        int n = 0;
        for (int y = floor - 1; n < cap && !v.Solid(c.X, y); y--) n++;
        return n;
    }

    // Tight enough that growth into the body wedges it against the ceiling: the body
    // fills the gap, so there is nowhere for the sprout to push it.
    public const int ConfinedRows = 3;
    public static bool Confined(BotView v, Vector2 body) => Clearance(v, body) <= ConfinedRows;
}
