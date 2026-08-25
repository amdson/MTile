using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Terrain line-of-sight for damage. A hit is a volume of force that radiates from
// an ORIGIN (the attacker's body, a blast center, a projectile); terrain occludes
// it. Something inside a hitbox only takes the hit if the straight segment from
// the origin reaches it without crossing the interior of another Solid cell —
// the same rule as splash damage in a shooter, applied to a tile grid.
//
// Two consumers:
//   CombatSystem  — filters the cells and hurtboxes a published hitbox may touch.
//   DamageDisc    — a direct area-damage entry point (explosions, impact into
//                   terrain) that damages a radius the same way.
//
// Cells are processed NEAREST-FIRST so damage propagates physically: when the
// front cell breaks, the one behind it becomes reachable in the same pass and
// takes damage too. A strong hit therefore chews through several cells at once;
// a weak one only chips the exposed face until it gives way. Nothing here is
// stateful — every query reads live terrain, so it's rollback-safe by
// construction and a wall shot down mid-swing opens the sightline immediately.
public static class TileReach
{
    // Occluders are tested as boxes shrunk by this skin: a rounding guard so a
    // segment that runs exactly along a face or through a corner (the ray to a
    // floor cell's top edge from above, say) can't register a zero-length
    // "crossing" of the neighbour. Kept tiny so a seam between two solid cells
    // isn't a crack a ray can slip down.
    private const float SkinPx = 0.01f;
    // Entity targets are sampled at points of the hurtbox pulled this far inside
    // so no sample sits exactly on a cell boundary shared with a wall the
    // hurtbox is pressed against.
    private const float HurtboxInsetPx = 1f;

    // Sort key for nearest-first processing: squared distance from `origin` to the
    // closest point of the cell. Ties (equidistant cells) fall through to the
    // (gtx, gty) tuple components, so a List<(float, int, int)>.Sort() is total
    // and deterministic.
    public static float DistanceKey(Vector2 origin, int gtx, int gty)
    {
        var p = NearestPointOfCell(origin, gtx, gty);
        return Vector2.DistanceSquared(origin, p);
    }

    // Can a hit from `origin` reach cell (gtx, gty)? A cell is struck on a FACE,
    // and only a face open to a non-solid neighbour can be struck — so the
    // sightline is aimed at exposed faces, never at a corner shared with the
    // cover (a cell buried under a floor is not "visible" through the diagonal
    // seam at its corner). Each exposed face is sampled at its point nearest the
    // origin and at its midpoint, so partial cover over the near end of a face
    // doesn't hide the rest of it; any clear sample makes the cell reachable.
    public static bool IsCellReachable(ChunkMap chunks, Vector2 origin, int gtx, int gty)
    {
        const float ts = Chunk.TileSize;
        float x0 = gtx * ts, x1 = x0 + ts, y0 = gty * ts, y1 = y0 + ts;
        float cx = Math.Clamp(origin.X, x0, x1), cy = Math.Clamp(origin.Y, y0, y1);
        float mx = x0 + ts * 0.5f, my = y0 + ts * 0.5f;

        if (chunks.GetCellState(gtx, gty - 1) != TileState.Solid
            && FaceClear(chunks, origin, new Vector2(cx, y0), new Vector2(mx, y0), gtx, gty)) return true;
        if (chunks.GetCellState(gtx, gty + 1) != TileState.Solid
            && FaceClear(chunks, origin, new Vector2(cx, y1), new Vector2(mx, y1), gtx, gty)) return true;
        if (chunks.GetCellState(gtx - 1, gty) != TileState.Solid
            && FaceClear(chunks, origin, new Vector2(x0, cy), new Vector2(x0, my), gtx, gty)) return true;
        if (chunks.GetCellState(gtx + 1, gty) != TileState.Solid
            && FaceClear(chunks, origin, new Vector2(x1, cy), new Vector2(x1, my), gtx, gty)) return true;
        return false;
    }

    private static bool FaceClear(ChunkMap chunks, Vector2 origin, Vector2 nearest, Vector2 mid,
                                  int gtx, int gty)
        => SegmentClear(chunks, origin, nearest, gtx, gty)
        || (nearest != mid && SegmentClear(chunks, origin, mid, gtx, gty));

    // Can a hit from `origin` reach world point `p`? The cell containing `p` never
    // occludes itself (a target standing half inside a cell is still hittable).
    public static bool IsPointReachable(ChunkMap chunks, Vector2 origin, Vector2 p)
    {
        int gtx = (int)MathF.Floor(p.X / Chunk.TileSize);
        int gty = (int)MathF.Floor(p.Y / Chunk.TileSize);
        return SegmentClear(chunks, origin, p, gtx, gty);
    }

    // Can a hit from `origin` reach any part of `region` (a hurtbox)? Samples the
    // nearest point first (a body peeking around a corner is hittable), then the
    // center, then the corners — so a tall body whose middle is behind a short
    // wall still takes the hit on the part that shows.
    public static bool IsRegionReachable(ChunkMap chunks, Vector2 origin, BoundingBox region)
    {
        var center = new Vector2(region.CenterX, region.CenterY);
        if (region.Width <= 2f * HurtboxInsetPx || region.Height <= 2f * HurtboxInsetPx)
            return IsPointReachable(chunks, origin, center);

        float l = region.Left + HurtboxInsetPx, r = region.Right  - HurtboxInsetPx;
        float t = region.Top  + HurtboxInsetPx, b = region.Bottom - HurtboxInsetPx;
        var nearest = new Vector2(Math.Clamp(origin.X, l, r), Math.Clamp(origin.Y, t, b));
        return IsPointReachable(chunks, origin, nearest)
            || IsPointReachable(chunks, origin, center)
            || IsPointReachable(chunks, origin, new Vector2(l, t))
            || IsPointReachable(chunks, origin, new Vector2(r, t))
            || IsPointReachable(chunks, origin, new Vector2(l, b))
            || IsPointReachable(chunks, origin, new Vector2(r, b));
    }

    // Every Solid cell whose nearest point lies within `radius` of `origin`,
    // appended to `cells` with its DistanceKey. Caller sorts (or calls
    // DamageReachable, which does).
    public static void CollectDisc(ChunkMap chunks, Vector2 origin, float radius,
                                   List<(float key, int gtx, int gty)> cells)
    {
        int gtx0 = (int)MathF.Floor((origin.X - radius) / Chunk.TileSize);
        int gtx1 = (int)MathF.Floor((origin.X + radius) / Chunk.TileSize);
        int gty0 = (int)MathF.Floor((origin.Y - radius) / Chunk.TileSize);
        int gty1 = (int)MathF.Floor((origin.Y + radius) / Chunk.TileSize);
        float r2 = radius * radius;
        for (int gtx = gtx0; gtx <= gtx1; gtx++)
        for (int gty = gty0; gty <= gty1; gty++)
        {
            if (chunks.GetCellState(gtx, gty) != TileState.Solid) continue;
            float key = DistanceKey(origin, gtx, gty);
            if (key > r2) continue;
            cells.Add((key, gtx, gty));
        }
    }

    // Damage every reachable cell in `cells`, nearest-first, so cells freed by a
    // break earlier in the pass are reached later in the same pass. Sorts `cells`
    // in place. Returns the number of cells that broke.
    public static int DamageReachable(ChunkMap chunks, Vector2 origin,
                                      List<(float key, int gtx, int gty)> cells, float damage)
    {
        cells.Sort();
        int broken = 0;
        foreach (var (_, gtx, gty) in cells)
        {
            if (!IsCellReachable(chunks, origin, gtx, gty)) continue;
            if (chunks.DamageCell(gtx, gty, damage)) broken++;
        }
        return broken;
    }

    // Area damage in one call: `damage` to every cell within `radius` of `origin`
    // that the blast can reach. Returns the number of cells broken.
    public static int DamageDisc(ChunkMap chunks, Vector2 origin, float radius, float damage,
                                 List<(float key, int gtx, int gty)> scratch = null)
    {
        scratch ??= new List<(float, int, int)>();
        scratch.Clear();
        CollectDisc(chunks, origin, radius, scratch);
        return DamageReachable(chunks, origin, scratch, damage);
    }

    // True iff the segment a→b crosses the interior of no Solid cell, ignoring the
    // cell `a` starts in (the attacker's own cell, which may be clipped into
    // terrain) and the target cell (skipGtx, skipGty). Walks the grid cells along
    // the segment (Amanatides–Woo) and confirms each candidate with a strict
    // slab test against the cell shrunk by SkinPx, so a grazing touch — the DDA
    // stepping through a corner the segment only kisses, or a run along a face —
    // doesn't count.
    public static bool SegmentClear(ChunkMap chunks, Vector2 a, Vector2 b, int skipGtx, int skipGty)
    {
        const float ts = Chunk.TileSize;
        int ax = (int)MathF.Floor(a.X / ts), ay = (int)MathF.Floor(a.Y / ts);
        int bx = (int)MathF.Floor(b.X / ts), by = (int)MathF.Floor(b.Y / ts);
        float dx = b.X - a.X, dy = b.Y - a.Y;

        int stepX = dx > 0f ? 1 : dx < 0f ? -1 : 0;
        int stepY = dy > 0f ? 1 : dy < 0f ? -1 : 0;
        // Parametric t (0 at a, 1 at b) of the next vertical / horizontal grid line
        // crossing, and the t advance per cell along each axis.
        float tMaxX = stepX == 0 ? float.PositiveInfinity
            : ((stepX > 0 ? (ax + 1) * ts : ax * ts) - a.X) / dx;
        float tMaxY = stepY == 0 ? float.PositiveInfinity
            : ((stepY > 0 ? (ay + 1) * ts : ay * ts) - a.Y) / dy;
        float tDeltaX = stepX == 0 ? float.PositiveInfinity : ts / MathF.Abs(dx);
        float tDeltaY = stepY == 0 ? float.PositiveInfinity : ts / MathF.Abs(dy);

        int cx = ax, cy = ay;
        // The segment can enter at most this many new cells; the bound also ends
        // the walk when b sits exactly on a grid line and floors into a neighbour.
        int maxSteps = Math.Abs(bx - ax) + Math.Abs(by - ay);
        for (int i = 0; i < maxSteps; i++)
        {
            if (tMaxX < tMaxY) { cx += stepX; tMaxX += tDeltaX; }
            else               { cy += stepY; tMaxY += tDeltaY; }
            // Reaching the target cell ends the segment: b is on or inside it, so
            // nothing past this point is between a and b.
            if (cx == skipGtx && cy == skipGty) return true;
            if (chunks.GetCellState(cx, cy) != TileState.Solid) continue;
            if (SegmentCrossesInterior(a, dx, dy, cx, cy)) return false;
        }
        return true;
    }

    private static Vector2 NearestPointOfCell(Vector2 p, int gtx, int gty)
    {
        const float ts = Chunk.TileSize;
        return new Vector2(
            Math.Clamp(p.X, gtx * ts, (gtx + 1) * ts),
            Math.Clamp(p.Y, gty * ts, (gty + 1) * ts));
    }

    // Slab test of segment a + t·(dx,dy), t ∈ [0,1], against cell (gtx, gty)
    // shrunk by SkinPx. Strict: the segment must spend positive length inside.
    private static bool SegmentCrossesInterior(Vector2 a, float dx, float dy, int gtx, int gty)
    {
        const float ts = Chunk.TileSize;
        float x0 = gtx * ts + SkinPx, x1 = (gtx + 1) * ts - SkinPx;
        float y0 = gty * ts + SkinPx, y1 = (gty + 1) * ts - SkinPx;
        float tMin = 0f, tMax = 1f;

        if (MathF.Abs(dx) < 1e-6f)
        {
            if (a.X <= x0 || a.X >= x1) return false;
        }
        else
        {
            float t1 = (x0 - a.X) / dx, t2 = (x1 - a.X) / dx;
            if (t1 > t2) (t1, t2) = (t2, t1);
            if (t1 > tMin) tMin = t1;
            if (t2 < tMax) tMax = t2;
            if (tMin >= tMax) return false;
        }

        if (MathF.Abs(dy) < 1e-6f)
        {
            if (a.Y <= y0 || a.Y >= y1) return false;
        }
        else
        {
            float t1 = (y0 - a.Y) / dy, t2 = (y1 - a.Y) / dy;
            if (t1 > t2) (t1, t2) = (t2, t1);
            if (t1 > tMin) tMin = t1;
            if (t2 < tMax) tMax = t2;
            if (tMin >= tMax) return false;
        }
        return true;
    }
}
