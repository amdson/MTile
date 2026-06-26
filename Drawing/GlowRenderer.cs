using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// The bright head shape of a glow trail.
public enum GlowCore { Triangle, Sphere }

// A glowing-shape effect: a bright core surrounded by a soft radial aura, leaving a glow
// trail under motion. Built entirely on PrimitiveBatch + additive blending (vertex-colored
// triangle fans for the aura discs, a filled triangle for the core) — no render targets, no
// custom shaders, fully portable. Render-only.
//
// Like PrimitiveBatch/metaballs it issues DrawUserPrimitives, so it must run OUTSIDE any
// SpriteBatch.Begin/End block (its own world-space pass).
public sealed class GlowRenderer
{
    private readonly PrimitiveBatch _prims;
    private readonly List<Vector2>  _ribbonPts = new(256);   // reused ribbon centerline buffer

    public GlowRenderer(GraphicsDevice gd) => _prims = new PrimitiveBatch(gd, capacity: 16384);

    // Soft additive radial glow disc: bright center vertex, transparent rim. Additive
    // blending sums overlapping discs into a smooth halo.
    private void GlowDisc(Vector2 center, float radius, Color color, int segments = 20)
    {
        if (radius <= 0.1f) return;
        Color rim = color * 0f;                       // same hue, alpha 0
        float step = MathHelper.TwoPi / segments;
        Vector2 prev = center + new Vector2(radius, 0f);
        for (int i = 1; i <= segments; i++)
        {
            float a = i * step;
            var next = center + new Vector2(MathF.Cos(a) * radius, MathF.Sin(a) * radius);
            _prims.Triangle(center, prev, next, color, rim, rim);
            prev = next;
        }
    }

    // Filled triangle of "radius" `size`, pointing along `dir` (unit). Flat color.
    private void CoreTriangle(Vector2 center, Vector2 dir, float size, Color color)
    {
        var perp = new Vector2(-dir.Y, dir.X);
        Vector2 tip  = center + dir * size;
        Vector2 baseL = center - dir * (size * 0.6f) + perp * (size * 0.7f);
        Vector2 baseR = center - dir * (size * 0.6f) - perp * (size * 0.7f);
        _prims.Triangle(tip, baseL, baseR, color);
    }

    // Glowing sphere core: a bright radial blob with a hotter center (two stacked discs),
    // reading as a small glowing ball rather than a flat disc.
    private void CoreSphere(Vector2 center, float size, Color color)
    {
        GlowDisc(center, size, color);
        GlowDisc(center, size * 0.5f, color);   // double-up the center for a hot core
    }

    // Render `trail` as a glowing RIBBON that follows the motion path: a soft strip
    // centered on the trail's spline, bright along its mid-line and fading to transparent
    // at both edges, tapering in width + brightness toward the tail, with a rounded cap on
    // the fat head (the blade's current position). Reads as a clean slash/stab streak —
    // the in-game port of the FxLab spline-swoosh. `headWidth` is the ribbon width at the
    // head (world px); `intensity` scales brightness. `cam` is the world→screen matrix.
    public void DrawTrailRibbon(Matrix cam, Trail trail, Color glowColor,
                                float headWidth, float intensity = 0.8f,
                                int subdivisions = 6, float widthTaper = 0.85f, float alphaTaper = 1.1f)
    {
        if (trail == null || trail.Count < 2) return;

        // Centerline: Catmull-Rom through the trail samples (newest = head → oldest = tail),
        // sampled to a smooth polyline. Endpoints clamp their phantom neighbors so the curve
        // passes through the first/last sample instead of spiraling off.
        _ribbonPts.Clear();
        int cp = trail.Count;
        for (int s = 0; s < cp - 1; s++)
        {
            Vector2 p0 = trail.PositionFromNewest(Math.Max(s - 1, 0));
            Vector2 p1 = trail.PositionFromNewest(s);
            Vector2 p2 = trail.PositionFromNewest(s + 1);
            Vector2 p3 = trail.PositionFromNewest(Math.Min(s + 2, cp - 1));
            for (int k = 0; k < subdivisions; k++)
                _ribbonPts.Add(CatmullRom(p0, p1, p2, p3, k / (float)subdivisions));
        }
        _ribbonPts.Add(trail.PositionFromNewest(cp - 1));

        _prims.Begin(cam, PrimitiveType.TriangleList, BlendState.Additive);
        SplineRibbon(_ribbonPts, headWidth, glowColor, intensity, widthTaper, alphaTaper);
        _prims.End();
    }

    // Skin a glowing ribbon along an arbitrary centerline polyline `pts` (head = pts[0]).
    // Two soft falloffs make it read as light, not a solid strip:
    //   • across the width — bright mid-line, alpha 0 at both rims,
    //   • along the length — alpha (and width, via `narrow`) tapering head → tail.
    // The head gets a rounded half-disc cap bulging in the direction of travel.
    private void SplineRibbon(List<Vector2> pts, float thick, Color hue, float alpha,
                              float narrow, float alphaTaper)
    {
        int n = pts.Count;
        if (n < 2) return;
        Color rim = hue * 0f;                                 // same hue, alpha 0

        Vector2 pIn = default, pMid = default, pOut = default;
        Color   pCol = default;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)(n - 1);                     // 0 = head, 1 = tail
            // Path tangent from neighbors → normal is the ribbon's width direction.
            Vector2 fwd = pts[Math.Min(i + 1, n - 1)] - pts[Math.Max(i - 1, 0)];
            if (fwd.LengthSquared() < 1e-6f) fwd = new Vector2(1f, 0f);
            fwd.Normalize();
            var nrm = new Vector2(-fwd.Y, fwd.X);

            float w   = thick * 0.5f * (1f - narrow * t);
            float aA  = MathF.Pow(1f - t, alphaTaper);        // 1 at head → 0 at tail
            Color col = hue * (alpha * aA);
            Vector2 mid = pts[i], vIn = mid - nrm * w, vOut = mid + nrm * w;
            if (i > 0)
            {
                _prims.Quad(pIn, vIn, mid, pMid, rim, rim, col, pCol);   // rim → mid-line
                _prims.Quad(pMid, mid, vOut, pOut, pCol, col, rim, rim); // mid-line → rim
            }
            pIn = vIn; pMid = mid; pOut = vOut; pCol = col;
        }

        // Rounded cap at the head: half-disc of radius thick/2 on the head cross-section,
        // bulging in the direction of travel (−tangent at the head).
        Vector2 h0 = pts[0], fwd0 = pts[1] - pts[0];
        if (fwd0.LengthSquared() < 1e-6f) fwd0 = new Vector2(1f, 0f);
        fwd0.Normalize();
        var   nrm0 = new Vector2(-fwd0.Y, fwd0.X);            // cap diameter axis
        var   outward = -fwd0;                                // bulge ahead of the head
        float cr = thick * 0.5f;
        Color capCol = hue * alpha;
        const int cseg = 14;
        Vector2 pr = default;
        for (int i = 0; i <= cseg; i++)
        {
            float phi = MathF.PI * i / cseg;
            var   dir = nrm0 * MathF.Cos(phi) + outward * MathF.Sin(phi);
            Vector2 rimP = h0 + dir * cr;
            if (i > 0) _prims.Triangle(h0, pr, rimP, capCol, rim, rim);
            pr = rimP;
        }
    }

    private static Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * (2f * p1
                       + (-p0 + p2) * t
                       + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                       + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    // Render a glowing triangle trailing along `trail`: an aura disc at each trail sample
    // (shrinking + fading with age) plus a soft triangle core at the head. `intensity`
    // (0..1) scales the whole effect's brightness — keep it low for a subtle glow.
    // `cam` is the world→screen camera matrix (Camera.GetTransform).
    public void DrawTrailGlow(Matrix cam, Trail trail, Color glowColor,
                              float auraRadius, float coreSize, float intensity = 0.45f,
                              GlowCore core = GlowCore.Triangle)
    {
        if (trail == null || trail.Count < 1) return;

        _prims.Begin(cam, PrimitiveType.TriangleList, BlendState.Additive);

        // Trail aura: oldest → newest so the head draws last. fade² so the streak drops
        // off quickly and reads as a faint wisp rather than a solid tail.
        for (int i = trail.Count - 1; i >= 0; i--)
        {
            float fade = 1f - trail.AgeFractionFromNewest(i);     // 1 at head, 0 at tail
            if (fade <= 0f) continue;
            float r = auraRadius * MathHelper.Lerp(0.25f, 0.85f, fade);
            GlowDisc(trail.PositionFromNewest(i), r, glowColor * (fade * fade * 0.3f * intensity));
        }

        Vector2 head = trail.PositionFromNewest(0);
        Vector2 dir = trail.Count >= 2
            ? trail.PositionFromNewest(0) - trail.PositionFromNewest(1)
            : new Vector2(1f, 0f);
        if (dir.LengthSquared() < 1e-4f) dir = new Vector2(1f, 0f);
        dir.Normalize();

        // Head: a soft aura + a modest, only-slightly-brightened core (no white blowout).
        GlowDisc(head, auraRadius, glowColor * (0.55f * intensity));
        GlowDisc(head, auraRadius * 0.45f, Color.Lerp(glowColor, Color.White, 0.3f) * intensity);
        Color coreColor = Color.Lerp(glowColor, Color.White, 0.45f) * intensity;
        if (core == GlowCore.Sphere) CoreSphere(head, coreSize, coreColor);
        else                         CoreTriangle(head, dir, coreSize, coreColor);

        _prims.End();
    }
}
