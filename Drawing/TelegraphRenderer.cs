using Microsoft.Xna.Framework;

namespace MTile;

// Draws a TelegraphList through DrawContext, inside whatever SpriteBatch pass the
// caller has open. This is the ONLY place telegraph shapes become draw calls — the
// action/enemy/entity code that emitted them never touches a SpriteBatch.
//
// Shapes draw in emission order, so a source can layer (dim fill first, bright core
// on top) exactly as it would with immediate draws.
public static class TelegraphRenderer
{
    public static void Draw(DrawContext ctx, TelegraphList list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            ref readonly var s = ref list[i];
            switch (s.Kind)
            {
                case TelegraphKind.Line:
                    ctx.Line(s.A, s.B, s.Color, s.Thickness);
                    break;
                case TelegraphKind.Box:
                    ctx.Box(s.A, s.B, s.Color);
                    break;
                case TelegraphKind.RotatedRect:
                    ctx.RotatedRect(s.A, s.B, s.Rotation, s.Color);
                    break;
                case TelegraphKind.Ring:
                    ctx.Ring(s.A, s.B.X, s.Color, s.Segments, s.Thickness);
                    break;
            }
        }
    }
}
