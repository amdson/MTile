using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// Thin wrapper around SpriteBatch + the 1×1 pixel texture. THE primitive API for
// immediate world/screen-space draws (line, box, rect, ring, disc) — nothing else in
// the codebase should be doing SpriteBatch.Draw(pixel, …) transform math by hand.
// Sim-side code never sees this class: it emits shapes into a TelegraphList, which
// Drawing/TelegraphRenderer draws through here. Stateless — SpriteBatch.Begin/End is
// owned by Game1.
public sealed class DrawContext
{
    public readonly SpriteBatch SpriteBatch;
    public readonly Texture2D   Pixel;

    public DrawContext(SpriteBatch sb, Texture2D pixel)
    {
        SpriteBatch = sb;
        Pixel       = pixel;
    }

    // Segment a→b; the thickness straddles the segment (origin at the texel centre,
    // quad centred on the midpoint), so a thick line sits ON its endpoints rather
    // than hanging off one side of them.
    public void Line(Vector2 a, Vector2 b, Color color, float thickness = 1f)
    {
        var edge = b - a;
        float len = edge.Length();
        if (len < 1e-4f) return;
        float angle = MathF.Atan2(edge.Y, edge.X);
        var mid = (a + b) * 0.5f;
        SpriteBatch.Draw(Pixel, mid, null, color, angle, new Vector2(0.5f, 0.5f),
            new Vector2(len, MathF.Max(thickness, 1f)), SpriteEffects.None, 0f);
    }

    // Axis-aligned filled rect from its top-left corner, snapped to whole pixels.
    public void Box(Vector2 topLeft, Vector2 size, Color color)
    {
        var r = new Rectangle((int)topLeft.X, (int)topLeft.Y, (int)size.X, (int)size.Y);
        SpriteBatch.Draw(Pixel, r, color);
    }

    // Axis-aligned filled rect centered on `center`.
    public void Rect(Vector2 center, Vector2 size, Color color)
        => Box(new Vector2(center.X - size.X * 0.5f, center.Y - size.Y * 0.5f), size, color);

    // Rotated filled rect — origin pinned to texel center so rotation is around `center`.
    public void RotatedRect(Vector2 center, Vector2 size, float rotation, Color color)
    {
        SpriteBatch.Draw(Pixel, center, null, color, rotation,
            new Vector2(0.5f, 0.5f), size, SpriteEffects.None, 0f);
    }

    // N-gon outline. Cheap stand-in for a circle when we don't have a real disc draw.
    public void Ring(Vector2 center, float radius, Color color, int segments = 16, float thickness = 1f)
    {
        if (segments < 3) segments = 3;
        float step = MathHelper.TwoPi / segments;
        Vector2 prev = center + new Vector2(radius, 0f);
        for (int i = 1; i <= segments; i++)
        {
            float a = i * step;
            var next = center + new Vector2(MathF.Cos(a) * radius, MathF.Sin(a) * radius);
            Line(prev, next, color, thickness);
            prev = next;
        }
    }

    // "Filled disc" — actually a rotated square of the same diameter. We don't have
    // a triangle renderer; at particle sizes (≤8 px) a square reads as a chunky disc
    // and stays cheap. For larger shapes use Ring instead.
    public void Disc(Vector2 center, float radius, Color color)
    {
        RotatedRect(center, new Vector2(radius * 2f, radius * 2f), 0f, color);
    }
}
