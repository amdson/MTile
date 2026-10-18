using System;
using Microsoft.Xna.Framework;

namespace MTile;

// "Is there anything here the legs could plausibly push a jump off?" — broader than the
// standing ground probe (GroundChecker via EnvironmentContext.TryGetGround), which only
// sees a floor under the body's own inset width. This one accepts any EXPOSED tile top
// (a solid cell with open air above it) whose face comes within reach of the feet:
//
//   - a floor under the body, out to the legs' full reach (CorrectorChannels.LegReach);
//   - the top corner of a ledge BESIDE the body, up to CornerRise above the standing
//     foot line — a body that has slipped just below a ledge's lip, or is rising past
//     it, kicks off that corner (a ground jump), instead of spending the double jump;
//   - either of the above out to SideReach past the body's sides.
//
// The standing foot line is the polygon bottom + FoldHoverOffset (where the floor sits
// under a body at rest). Static tiles only: sprouts and moving shapes are the ground
// probe's, which callers check first.
public static class JumpSupportChecker
{
    // Past the body's left/right faces: a corner flush beside the body, or a lip the
    // body has just run off, is still under a foot.
    public const float SideReach = 4f;
    // Above the standing foot line: the lip of a ledge whose top the feet have just
    // dropped below (or not yet risen to). Small — a ledge at the knees is a climb,
    // not something to jump off.
    public const float CornerRise = 6f;

    public static bool TryFind(PhysicsBody body, ChunkMap chunks, out Vector2 support)
    {
        support = default;
        if (chunks == null) return false;
        var b = body.Bounds;
        const float T = Chunk.TileSize;
        float footY = b.Bottom + MovementConfig.Current.FoldHoverOffset;
        float topMin = footY - CornerRise;
        float topMax = b.Bottom + CorrectorChannels.LegReach;
        float left   = b.Left  - SideReach;
        float right  = b.Right + SideReach;

        int gxMin = (int)MathF.Floor(left / T);
        int gxMax = (int)MathF.Floor(right / T);
        // A top face sits at gty*T: every row whose top lies inside [topMin, topMax].
        int gyMin = (int)MathF.Ceiling(topMin / T);
        int gyMax = (int)MathF.Floor(topMax / T);

        // Nearest the body's center wins, so a floor straight under the body is
        // preferred to a corner off to one side.
        float best = float.MaxValue;
        for (int gy = gyMin; gy <= gyMax; gy++)
        for (int gx = gxMin; gx <= gxMax; gx++)
        {
            float cx = (gx + 0.5f) * T;
            if (!TileQuery.IsSolidAt(chunks, cx, (gy + 0.5f) * T)) continue;
            if (TileQuery.IsSolidAt(chunks, cx, (gy - 0.5f) * T)) continue;   // buried, not a top face
            // The point of the face nearest the body's center line.
            float x = Math.Clamp(body.Position.X, gx * T, (gx + 1) * T);
            var p = new Vector2(x, gy * T);
            float d = Vector2.DistanceSquared(p, new Vector2(body.Position.X, footY));
            if (d < best) { best = d; support = p; }
        }
        return best < float.MaxValue;
    }
}
