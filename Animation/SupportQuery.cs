using System;
using Microsoft.Xna.Framework;

namespace MTile;

// FINITE TREAD QUERY (render-only) — the step planner's terrain boundary
// (Plans/ANIMATION_STEP_PLANNER_IMPL.md §2). Where TerrainSurfaces emits infinite
// no-penetration half-planes around limb tips, this returns FINITE, IDENTIFIED
// support segments a foot could land on: the exposed top face of a solid tile.
//
// One segment per CELL, deliberately unmerged: an 11px tread is already foot-scale,
// identity is just the cell (stable under neighbors appearing/disappearing — a merged
// run would change identity when it grew, defeating the planner's hysteresis), and
// revalidation is a two-cell read. Normals are implicitly straight up (y-down: −Y);
// tile tops are the only supports in this slice.
//
// STATIC SOLID TILES ONLY, by policy (owner's call, 2026-09-05): Sprouting cells and
// moving volumes are never emitted, so growing/moving terrain simply offers no
// support and the planner reports Unplanned rather than tracking it. A cell whose
// ABOVE neighbor is Solid is interior — not a tread.
public struct SupportSegment
{
    public float X0, X1;   // tread endpoints (world px, X0 < X1)
    public float Y;        // tread height (world px, the tile's top edge)
    public long  Id;       // packed cell coords — stable identity for hysteresis/revalidation

    public Vector2 Clamp(float x) => new(MathHelper.Clamp(x, X0, X1), Y);

    public static long PackId(int gtx, int gty) => ((long)gty << 32) | (uint)gtx;
    public static (int gtx, int gty) UnpackId(long id) => ((int)(uint)id, (int)(id >> 32));
}

public static class SupportQuery
{
    // Treads with any part inside [center.X ± radius] horizontally and
    // [center.Y ± radius] vertically. Returns the count written (capped at dst.Length).
    public static int QueryTreads(ChunkMap chunks, Vector2 center, float radius,
                                  Span<SupportSegment> dst)
    {
        if (chunks == null || dst.Length == 0) return 0;
        float ts = Chunk.TileSize;
        int gx0 = (int)MathF.Floor((center.X - radius) / ts);
        int gx1 = (int)MathF.Floor((center.X + radius) / ts);
        int gy0 = (int)MathF.Floor((center.Y - radius) / ts);
        int gy1 = (int)MathF.Floor((center.Y + radius) / ts);
        int count = 0;
        for (int gy = gy0; gy <= gy1; gy++)
            for (int gx = gx0; gx <= gx1; gx++)
            {
                if (!IsTread(chunks, gx, gy)) continue;
                dst[count++] = new SupportSegment
                {
                    X0 = gx * ts, X1 = (gx + 1) * ts, Y = gy * ts,
                    Id = SupportSegment.PackId(gx, gy),
                };
                if (count == dst.Length) return count;
            }
        return count;
    }

    // Does the segment's cell still offer support (still solid, top still exposed)?
    public static bool Revalidate(ChunkMap chunks, in SupportSegment s)
    {
        var (gtx, gty) = SupportSegment.UnpackId(s.Id);
        return chunks != null && IsTread(chunks, gtx, gty);
    }

    private static bool IsTread(ChunkMap chunks, int gtx, int gty)
        => chunks.GetCellState(gtx, gty) == TileState.Solid
        && chunks.GetCellState(gtx, gty - 1) != TileState.Solid;
}
