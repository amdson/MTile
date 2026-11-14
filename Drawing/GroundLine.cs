using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Where "underground" starts, per column: the height rule behind the back wall. An open
// cell at or below the line shows dark earth behind it instead of the scenery.
//
// The line is the top of the STARTING terrain's ground mass: in each column, the solid
// run that rises from the bottom of the loaded world, so a ceiling, a floating platform
// or a ledge never counts. It is then median-filtered across columns, which removes
// anything narrower than the filter — a pillar standing on the ground is not a hill, and
// knocking one down must leave open air, not a dark ghost of itself.
//
// Captured the first time a column is asked about and never updated: digging must not
// move the line it is measured against. Render-only.
public sealed class GroundLine
{
    // Median half-width in columns. Features this wide or narrower are ignored.
    private const int SmoothRadius = 8;

    public const int None = int.MaxValue;

    private readonly Dictionary<int, int> _raw = new();
    // Loaded chunk rows per chunk column, refreshed each frame (a few hundred chunks).
    private readonly Dictionary<int, (int Min, int Max)> _extent = new();
    private readonly int[] _sort = new int[2 * SmoothRadius + 1];
    private ChunkMap _map;

    public void Begin(ChunkMap chunks)
    {
        if (!ReferenceEquals(chunks, _map)) { _map = chunks; _raw.Clear(); }
        _extent.Clear();
        foreach (var chunk in chunks)
        {
            var p = chunk.ChunkPos;
            _extent[p.X] = _extent.TryGetValue(p.X, out var e)
                ? (Math.Min(e.Min, p.Y), Math.Max(e.Max, p.Y)) : (p.Y, p.Y);
        }
    }

    // First underground row of the column, or None. Call Begin once per frame first.
    public int At(int gtx)
    {
        for (int k = 0; k < _sort.Length; k++)
        {
            int v = Raw(gtx - SmoothRadius + k);
            int j = k;
            while (j > 0 && _sort[j - 1] > v) { _sort[j] = _sort[j - 1]; j--; }
            _sort[j] = v;
        }
        return _sort[SmoothRadius];
    }

    private int Raw(int gtx)
    {
        if (_raw.TryGetValue(gtx, out int line)) return line;
        int cx = gtx >= 0 ? gtx / Chunk.Size : -((-gtx + Chunk.Size - 1) / Chunk.Size);
        // No chunks here (yet): not cached, so a column streamed in later still gets a line.
        if (!_extent.TryGetValue(cx, out var e)) return None;
        int bottom = (e.Max + 1) * Chunk.Size - 1, top = e.Min * Chunk.Size;
        if (_map.GetCellState(gtx, bottom) != TileState.Solid) return None;
        int gy = bottom;
        while (gy > top && _map.GetCellState(gtx, gy - 1) == TileState.Solid) gy--;
        _raw[gtx] = gy;
        return gy;
    }
}
