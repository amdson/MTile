using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// Thin wrapper around SpriteBatch + the 1×1 pixel texture. Provides primitive
// draws (line, rect, ring, disc) so callers don't repeat the SpriteBatch.Draw
// transform math. Stateless — SpriteBatch.Begin/End is owned by Game1.
public sealed class DrawContext
{
    public readonly SpriteBatch SpriteBatch;
    public readonly Texture2D   Pixel;

    public DrawContext(SpriteBatch sb, Texture2D pixel)
    {
        SpriteBatch = sb;
        Pixel       = pixel;
    }

    public void Line(Vector2 a, Vector2 b, Color color, float thickness = 1f)
    {
        var edge = b - a;
        float len = edge.Length();
        if (len < 1e-4f) return;
        float angle = MathF.Atan2(edge.Y, edge.X);
        SpriteBatch.Draw(Pixel, a, null, color, angle, Vector2.Zero,
            new Vector2(len, thickness), SpriteEffects.None, 0f);
    }

    // Axis-aligned filled rect centered on `center`.
    public void Rect(Vector2 center, Vector2 size, Color color)
    {
        var r = new Rectangle(
            (int)(center.X - size.X * 0.5f),
            (int)(center.Y - size.Y * 0.5f),
            (int)size.X, (int)size.Y);
        SpriteBatch.Draw(Pixel, r, color);
    }

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
