using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// Drawing primitives for enemy telegraphs. Render-only: nothing here may read or
// write sim state, and deleting a call changes nothing about what an attack does
// — only whether the player can see it coming.
//
// Enemy state Draw overrides are handed a single 1×1 `pixel` texture, so
// anything that isn't an axis-aligned rectangle has to be built by hand. These
// are the shapes that turned out to be worth sharing.
internal static class EnemyTelegraph
{
    // A line of arbitrary angle, as the 1×1 pixel stretched into a rotated quad.
    // `start` is the line's origin; the quad is offset by half its length along
    // the direction before drawing, because the sprite's own origin is centred.
    public static void Line(SpriteBatch sb, Texture2D pixel, Vector2 start, float angle,
                            float length, float thickness, Color color)
    {
        var mid = start + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (length * 0.5f);
        sb.Draw(pixel, mid, null, color, angle,
                new Vector2(0.5f, 0.5f), new Vector2(length, MathF.Max(thickness, 1f)),
                SpriteEffects.None, 0f);
    }
}
