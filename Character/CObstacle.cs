using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// One facet of the tile C-obstacle: a half-plane (relative to the TILE CENTER)
// of the Minkowski sum  tile ⊕ (−body). The body center is inside the obstacle
// (i.e. the body overlaps the tile) iff dot(rel, Normal) < Offset for EVERY
// facet. Exposure masks: the facet lies on the interior of the C-space union —
// and must be pruned — when the masked neighbor tile(s) are solid (axis facets:
// the face neighbor; corner bevels: either adjacent axis neighbor).
public struct CFacet
{
    public Vector2 Normal;    // unit, outward
    public float   Offset;    // support of tile + support of reflected body
    public int     MaskDx1, MaskDy1;
    public int     MaskDx2, MaskDy2;
    public bool    TwoMasks;
}

// The C-space obstacle template for a convex body vs the unit tile — computed
// once per body polygon and stamped at every solid tile (the tile grid itself is
// the spatial index; the union boundary stays implicit). Facet set = tile axis
// normals ∪ reflected-body edge normals, offsets = summed supports. For the
// player hexagon this is ~8 facets, four of them the corner bevels that make
// lips and corners visible to planning as real geometry.
public sealed class CObstacleTemplate
{
    public CFacet[] Facets;
    public float    Reach;     // max Offset — cell-gather radius for queries
    public float    XExtent;   // horizontal support — the obstacle's half-width

    // The obstacle's TOP surface (y-down: smallest boundary y) at horizontal
    // offset dx from the tile center, as a center-relative y. This is the height
    // a body center rests at over this tile — flat over the up facet, sloping
    // down the corner bevels near the edges (which is what makes the floor
    // envelope ramp smoothly over lips). Invalid outside the horizontal extent.
    public float TopSurfaceRy(float dx, out bool valid)
    {
        valid = MathF.Abs(dx) <= XExtent;
        if (!valid) return 0f;
        float best = float.MinValue;
        foreach (var f in Facets)
        {
            if (f.Normal.Y > -0.1f) continue;   // upward-ish facets only
            float ry = (f.Offset - f.Normal.X * dx) / f.Normal.Y;
            if (ry > best) best = ry;
        }
        return best;
    }

    // Single-slot memo keyed on the polygon instance. Pure derived data (safe
    // as a static: the result is a pure function of the polygon), rebuilt only
    // when a different polygon reference queries.
    private static Polygon _cachedFor;
    private static CObstacleTemplate _cached;

    public static CObstacleTemplate For(Polygon body)
    {
        if (!ReferenceEquals(_cachedFor, body))
        {
            _cached = Build(body);
            _cachedFor = body;
        }
        return _cached;
    }

    public static CObstacleTemplate Build(Polygon body)
    {
        var verts = body.GetVertices(Vector2.Zero);
        int m = verts.Length;
        float h = Chunk.TileSize * 0.5f;

        var candidates = new List<Vector2>
        {
            new(1f, 0f), new(-1f, 0f), new(0f, 1f), new(0f, -1f),
        };
        // Edge normals of the REFLECTED body (outward w.r.t. origin).
        for (int i = 0; i < m; i++)
        {
            var a = -verts[i];
            var e = -verts[(i + 1) % m] - a;
            var n = new Vector2(e.Y, -e.X);
            if (n.LengthSquared() < 1e-8f) continue;
            n.Normalize();
            if (Vector2.Dot(n, a) < 0f) n = -n;
            candidates.Add(n);
        }

        var facets = new List<CFacet>();
        foreach (var n in candidates)
        {
            bool dup = false;
            foreach (var f in facets)
                if (Vector2.Dot(f.Normal, n) > 0.999f) { dup = true; break; }
            if (dup) continue;

            float supTile = h * (MathF.Abs(n.X) + MathF.Abs(n.Y));
            float supBody = float.MinValue;
            for (int i = 0; i < m; i++)
                supBody = MathF.Max(supBody, Vector2.Dot(-verts[i], n));

            var facet = new CFacet { Normal = n, Offset = supTile + supBody };
            if (MathF.Abs(n.X) > 0.999f)      { facet.MaskDx1 = n.X > 0f ? 1 : -1; }
            else if (MathF.Abs(n.Y) > 0.999f) { facet.MaskDy1 = n.Y > 0f ? 1 : -1; }
            else
            {
                // Corner bevel: interior whenever either adjacent axis neighbor
                // is solid. (Diagonal-only neighbors are not masked — leaving the
                // bevel exposed there is conservative and the body cannot pass
                // through a diagonal gap anyway.)
                facet.MaskDx1 = n.X > 0f ? 1 : -1;
                facet.MaskDx2 = 0; facet.MaskDy2 = n.Y > 0f ? 1 : -1;
                facet.TwoMasks = true;
            }
            facets.Add(facet);
        }

        float reach = 0f, xExtent = 0f;
        foreach (var f in facets)
        {
            reach = MathF.Max(reach, f.Offset);
            if (MathF.Abs(f.Normal.X) > 0.999f) xExtent = MathF.Max(xExtent, f.Offset);
        }
        return new CObstacleTemplate { Facets = facets.ToArray(), Reach = reach, XExtent = xExtent };
    }
}
