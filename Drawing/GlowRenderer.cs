using System;
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
