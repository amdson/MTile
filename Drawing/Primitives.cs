using System;
using Microsoft.Xna.Framework;

namespace MTile;

// CPU tessellation helpers that emit into a PrimitiveBatch: parametric curves drawn as
// stroked ribbons, and parametric surfaces drawn as vertex-colored quad grids. All
// portable (no shaders). The batch must be in TriangleList mode and already Begun.
public static class Primitives
{
    // Cubic Bezier point at t in [0,1].
    public static Vector2 Bezier(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float u = 1f - t;
        return u * u * u * p0
             + 3f * u * u * t * p1
             + 3f * u * t * t * p2
             + t * t * t * p3;
    }

    // Stroke an arbitrary parametric curve `point(t)`, t in [0,1], as a ribbon. Width and
    // color may taper along the curve via `width(t)` / `color(t)`. Offsets are along the
    // segment normal, so the ribbon hugs curvature without external geometry.
    public static void StrokeCurve(PrimitiveBatch batch, Func<float, Vector2> point,
                                   Func<float, float> width, Func<float, Color> color,
                                   int segments = 48)
    {
        if (segments < 1) segments = 1;
        Vector2 prev = point(0f);
        for (int i = 1; i <= segments; i++)
        {
            float t0 = (i - 1) / (float)segments;
            float t1 = i / (float)segments;
            Vector2 a = prev;
            Vector2 b = point(t1);
            prev = b;

            Vector2 dir = b - a;
            if (dir.LengthSquared() < 1e-8f) continue;
            dir.Normalize();
            var nrm = new Vector2(-dir.Y, dir.X);

            float wa = MathF.Max(0f, width(t0)) * 0.5f;
            float wb = MathF.Max(0f, width(t1)) * 0.5f;
            Color ca = color(t0);
            Color cb = color(t1);

            batch.Quad(a - nrm * wa, b - nrm * wb, b + nrm * wb, a + nrm * wa, ca, cb, cb, ca);
        }
    }

    // Cubic-Bezier convenience over StrokeCurve.
    public static void StrokeBezier(PrimitiveBatch batch, Vector2 p0, Vector2 p1, Vector2 p2,
                                    Vector2 p3, float width, Color a, Color b, int segments = 48)
        => StrokeCurve(batch, t => Bezier(p0, p1, p2, p3, t),
                       _ => width, t => Color.Lerp(a, b, t), segments);

    // Tessellate a parametric surface (u,v in [0,1]) into a vertex-colored quad grid.
    // `pos(u,v)` places each vertex in world space; `color(u,v)` tints it. Use for
    // density-iso previews, deformed sprites, gradient fills over a warped domain, etc.
    public static void Surface(PrimitiveBatch batch, Func<float, float, Vector2> pos,
                               Func<float, float, Color> color, int ucount = 24, int vcount = 24)
    {
        if (ucount < 1) ucount = 1;
        if (vcount < 1) vcount = 1;
        for (int iu = 0; iu < ucount; iu++)
        {
            float u0 = iu / (float)ucount, u1 = (iu + 1) / (float)ucount;
            for (int iv = 0; iv < vcount; iv++)
            {
                float v0 = iv / (float)vcount, v1 = (iv + 1) / (float)vcount;
                Vector2 a = pos(u0, v0), b = pos(u1, v0), c = pos(u1, v1), d = pos(u0, v1);
                batch.Quad(a, b, c, d, color(u0, v0), color(u1, v0), color(u1, v1), color(u0, v1));
            }
        }
    }
}
