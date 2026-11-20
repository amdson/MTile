using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// A row of drawn grass tufts on a transparent background, sliced into one source rect
// per tuft by finding the empty columns between them. The image is straight-alpha as
// loaded (Texture2D.FromStream); it is premultiplied here, in place.
public sealed class GrassTuftSheet
{
    public Texture2D Texture { get; }
    // One rect per tuft, tight to its pixels; the tuft's base is the rect's bottom edge.
    public Rectangle[] Tufts { get; }
    public int MaxHeight { get; }

    private GrassTuftSheet(Texture2D texture, Rectangle[] tufts, int maxHeight)
    {
        Texture = texture; Tufts = tufts; MaxHeight = maxHeight;
    }

    // Null when the image holds no tufts.
    public static GrassTuftSheet Build(Texture2D tex)
    {
        const int Solid = 40;       // alpha above this counts as tuft
        const int MinGap = 3;       // empty columns that separate two tufts
        const int MinWidth = 6;     // narrower runs are stray specks
        int w = tex.Width, h = tex.Height;
        var px = new Color[w * h];
        tex.GetData(px);

        var tufts = new List<Rectangle>();
        int maxH = 0, runStart = -1, empty = 0;
        for (int x = 0; x <= w; x++)
        {
            bool any = false;
            if (x < w)
                for (int y = 0; y < h; y++)
                    if (px[y * w + x].A > Solid) { any = true; break; }
            if (any) { if (runStart < 0) runStart = x; empty = 0; continue; }
            if (runStart < 0) continue;
            if (++empty < MinGap && x < w) continue;
            int x0 = runStart, x1 = x - empty;      // inclusive
            runStart = -1; empty = 0;
            if (x1 - x0 + 1 < MinWidth) continue;
            int y0 = h, y1 = -1;
            for (int y = 0; y < h; y++)
                for (int xx = x0; xx <= x1; xx++)
                    if (px[y * w + xx].A > Solid) { if (y < y0) y0 = y; if (y > y1) y1 = y; break; }
            if (y1 < y0) continue;
            tufts.Add(new Rectangle(x0, y0, x1 - x0 + 1, y1 - y0 + 1));
            if (y1 - y0 + 1 > maxH) maxH = y1 - y0 + 1;
        }
        if (tufts.Count == 0) return null;

        for (int i = 0; i < px.Length; i++)
            px[i] = Color.FromNonPremultiplied(px[i].R, px[i].G, px[i].B, px[i].A);
        tex.SetData(px);
        return new GrassTuftSheet(tex, tufts.ToArray(), maxH);
    }
}
