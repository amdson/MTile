using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Sub-tile-resolution segment sampling for drag-to-build. Yields the global
// cell coords the segment (a, b) passes through, in path order, deduped against
// the previous yielded cell. Sampling step defaults to 4 px (¼ of a tile),
// which guarantees no cell is skipped at any realistic cursor speed.
//
// A zero-length segment (a == b) yields exactly one cell — the cell containing
// the point. Consecutive duplicates inside the sweep are dropped, so a long
// segment crossing M cells yields exactly M tuples.
public static class MouseSweep
{
    public static IEnumerable<(int gtx, int gty)> Cells(Vector2 a, Vector2 b, float step = 4f)
    {
        float len = (b - a).Length();
        int samples = Math.Max(1, (int)MathF.Ceiling(len / step));

        int prevGtx = 0, prevGty = 0;
        bool first = true;

        for (int i = 0; i <= samples; i++)
        {
            float t = samples == 0 ? 0f : (float)i / samples;
            Vector2 p = Vector2.Lerp(a, b, t);
            int gtx = (int)MathF.Floor(p.X / Chunk.TileSize);
            int gty = (int)MathF.Floor(p.Y / Chunk.TileSize);
            if (first || gtx != prevGtx || gty != prevGty)
            {
                yield return (gtx, gty);
                prevGtx = gtx;
                prevGty = gty;
                first = false;
            }
        }
    }
}
