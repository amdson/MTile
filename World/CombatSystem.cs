using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Per-frame intersection pass. Walks every published hitbox against every published
// hurtbox; on overlap with mismatched factions, dispatches OnHit to the hurtbox
// owner. Also applies the same hitboxes to tiles (which aren't IHittables — they're
// addressed by cell coords).
//
// Dedupe semantics:
//   Entities: each (HitId, Target) pair fires OnHit exactly once. A slash whose
//             hitbox broadcasts for 4 frames lands on a balloon once. The dedupe
//             table persists across frames while the HitId keeps broadcasting; it
//             prunes when the HitId disappears (attack ended).
//   Tiles:   No dedupe — multi-frame hitboxes accumulate damage on a tile every
//            frame they overlap. Preserves the progressive darkening visual.
public static class CombatSystem
{
    private static readonly Dictionary<int, HashSet<IHittable>> _hitDedupe = new();
    private static readonly HashSet<int> _liveHitIds = new();
    private static readonly List<int> _scratchPrune = new();

    public static void Apply(ChunkMap chunks, HitboxWorld hitboxes, HurtboxWorld hurtboxes)
    {
        _liveHitIds.Clear();

        foreach (var hit in hitboxes.All)
        {
            _liveHitIds.Add(hit.HitId);

            // Narrow-phase polygon shape — compute world-space vertices + edge axes
            // once per hitbox, reused across all tile / entity overlap checks.
            Vector2[] hitVerts = null;
            Vector2[] hitAxes  = null;
            if (hit.Shape != null)
            {
                hitVerts = hit.Shape.GetVertices(hit.ShapePos, hit.ShapeRotation);
                hitAxes  = hit.Shape.GetAxes(hitVerts);
            }

            // --- Tile path (cumulative; same as the old DamageSystem) -----------
            if (hit.Targets != HitTargets.EntitiesOnly)
            {
                int gtx0 = (int)MathF.Floor(hit.Region.Left   / Chunk.TileSize);
                int gtx1 = (int)MathF.Floor(hit.Region.Right  / Chunk.TileSize);
                int gty0 = (int)MathF.Floor(hit.Region.Top    / Chunk.TileSize);
                int gty1 = (int)MathF.Floor(hit.Region.Bottom / Chunk.TileSize);
                for (int gtx = gtx0; gtx <= gtx1; gtx++)
                for (int gty = gty0; gty <= gty1; gty++)
                {
                    if (hit.Shape != null)
                    {
                        var tileAABB = new BoundingBox(
                            gtx * Chunk.TileSize,       gty * Chunk.TileSize,
                            (gtx + 1) * Chunk.TileSize, (gty + 1) * Chunk.TileSize);
                        if (!OverlapsPolyAABB(hitVerts, hitAxes, tileAABB)) continue;
                    }
                    chunks.DamageCell(gtx, gty, hit.Damage);
                }
            }

            // --- Entity path (deduped per (HitId, Target)) ----------------------
            if (hit.Targets != HitTargets.TilesOnly)
            {
                if (!_hitDedupe.TryGetValue(hit.HitId, out var alreadyHit))
                {
                    alreadyHit = new HashSet<IHittable>();
                    _hitDedupe[hit.HitId] = alreadyHit;
                }
                foreach (var hb in hurtboxes.All)
                {
                    if (hit.Owner == hb.Owner) continue;
                    if (alreadyHit.Contains(hb.Target)) continue;
                    if (!HitboxWorld.Overlaps(hit.Region, hb.Region)) continue;
                    if (hit.Shape != null && !OverlapsPolyAABB(hitVerts, hitAxes, hb.Region)) continue;
                    hb.Target.OnHit(hit, hb);
                    alreadyHit.Add(hb.Target);
                }
            }
        }

        // Prune dedupe entries for HitIds that didn't broadcast this frame —
        // their attack is over, future hitboxes will use fresh HitIds anyway.
        _scratchPrune.Clear();
        foreach (var k in _hitDedupe.Keys)
            if (!_liveHitIds.Contains(k)) _scratchPrune.Add(k);
        foreach (var k in _scratchPrune) _hitDedupe.Remove(k);
    }

    // SAT: arbitrary convex polygon (given by its pre-computed world vertices +
    // edge normals) vs world-axis AABB. Returns true if they overlap.
    // Axes tested: polygon's edge normals + world X + world Y. For each, both
    // shapes are projected to a [min, max] interval; any disjoint interval means
    // no overlap.
    private static bool OverlapsPolyAABB(Vector2[] polyVerts, Vector2[] polyAxes, BoundingBox aabb)
    {
        // Polygon-normal axes
        for (int a = 0; a < polyAxes.Length; a++)
        {
            var axis = polyAxes[a];
            var (pmin, pmax) = Polygon.Project(polyVerts, axis);
            // Project the AABB's four corners onto this axis
            float c1 = aabb.Left  * axis.X + aabb.Top    * axis.Y;
            float c2 = aabb.Right * axis.X + aabb.Top    * axis.Y;
            float c3 = aabb.Right * axis.X + aabb.Bottom * axis.Y;
            float c4 = aabb.Left  * axis.X + aabb.Bottom * axis.Y;
            float amin = MathF.Min(MathF.Min(c1, c2), MathF.Min(c3, c4));
            float amax = MathF.Max(MathF.Max(c1, c2), MathF.Max(c3, c4));
            if (pmax < amin || amax < pmin) return false;
        }

        // World X axis: AABB span is [Left, Right]; polygon span = bounds of vertex Xs
        float pminX = polyVerts[0].X, pmaxX = polyVerts[0].X;
        float pminY = polyVerts[0].Y, pmaxY = polyVerts[0].Y;
        for (int i = 1; i < polyVerts.Length; i++)
        {
            var v = polyVerts[i];
            if (v.X < pminX) pminX = v.X; else if (v.X > pmaxX) pmaxX = v.X;
            if (v.Y < pminY) pminY = v.Y; else if (v.Y > pmaxY) pmaxY = v.Y;
        }
        if (pmaxX < aabb.Left || aabb.Right  < pminX) return false;
        if (pmaxY < aabb.Top  || aabb.Bottom < pminY) return false;

        return true;
    }
}
