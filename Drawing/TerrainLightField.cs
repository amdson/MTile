using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Render-only terrain lighting, after Terraria's: one brightness value per cell in the
// visible window. Cells with open sky straight above them are the light source; light
// then spreads cell to cell, losing a little through air and a lot through solid. Tiles
// and the back wall are tinted by it, so a surface is bright, the inside of the rock is
// dark, a shaft fades with depth, and a cave is only as bright as its route to the sky.
//
// Sky light only: nothing in the world emits yet. Characters, attacks and telegraphs are
// NOT lit by this — a fight in a tunnel has to stay readable — and MinLight keeps the
// terrain itself from ever going black.
//
// Rebuilt from scratch every frame rather than invalidated on tile events: the window is
// a few thousand cells, and a field that is a pure function of the terrain it was handed
// cannot go stale across a rollback restore. Reads the sim, writes nothing.
public sealed class TerrainLightField
{
    // Light kept per cell of open air it crosses. 0.9 → half brightness about 7 cells in.
    public const float AirDecay = 0.90f;
    // Light kept per cell of solid it crosses: reaches only a few tiles into rock.
    public const float SolidDecay = 0.62f;
    // Brightness of terrain that receives no light at all (fully lit = 1).
    public const float MinLight = 0.30f;
    // Cells computed beyond the visible window, so light arriving from just off screen is
    // right. Light from further away than this is lost; at AirDecay^Margin it is faint.
    public const int Margin = 16;
    // How far above the window a column is checked for a roof before it is called open sky.
    private const int SkyScanCells = 48;
    // Raster sweeps (each a forward + a backward pass). Every extra round lets light
    // bend round one more corner; three is enough for anything that fits on screen.
    private const int Rounds = 3;

    public const byte Open = 0, Solid = 1, Unloaded = 2;

    private byte[]  _kind  = Array.Empty<byte>();
    private float[] _light = Array.Empty<float>();
    private int _minX, _minY, _w, _h;

    // Diagonal steps are longer, so they lose light as a step of length sqrt(2) would.
    private static readonly float AirDiag   = MathF.Pow(AirDecay, 1.4142f);
    private static readonly float SolidDiag = MathF.Pow(SolidDecay, 1.4142f);

    // Recompute for the inclusive cell window [min, max]. The caller passes the VISIBLE
    // window; the margin is added here.
    public void Update(ChunkMap chunks, int minGtx, int minGty, int maxGtx, int maxGty)
    {
        _minX = minGtx - Margin;
        _minY = minGty - Margin;
        _w = Math.Max(0, maxGtx - minGtx + 1) + 2 * Margin;
        _h = Math.Max(0, maxGty - minGty + 1) + 2 * Margin;
        if (_kind.Length < _w * _h) { _kind = new byte[_w * _h]; _light = new float[_w * _h]; }

        // Classify chunk by chunk — one dictionary lookup per chunk, not per cell.
        int maxX = _minX + _w - 1, maxY = _minY + _h - 1;
        int cx0 = FloorDiv(_minX, Chunk.Size), cx1 = FloorDiv(maxX, Chunk.Size);
        int cy0 = FloorDiv(_minY, Chunk.Size), cy1 = FloorDiv(maxY, Chunk.Size);
        for (int cy = cy0; cy <= cy1; cy++)
        for (int cx = cx0; cx <= cx1; cx++)
        {
            chunks.TryGet(new Point(cx, cy), out var chunk);
            int gx0 = Math.Max(_minX, cx * Chunk.Size), gx1 = Math.Min(maxX, cx * Chunk.Size + Chunk.Size - 1);
            int gy0 = Math.Max(_minY, cy * Chunk.Size), gy1 = Math.Min(maxY, cy * Chunk.Size + Chunk.Size - 1);
            for (int gy = gy0; gy <= gy1; gy++)
            {
                int row = (gy - _minY) * _w - _minX;
                int ty = gy - cy * Chunk.Size;
                for (int gx = gx0; gx <= gx1; gx++)
                    _kind[row + gx] = chunk == null ? Unloaded
                        : chunk.Tiles[gx - cx * Chunk.Size, ty].State == TileState.Solid ? Solid : Open;
            }
        }

        // Sky: walk each column down from above the window; every cell is lit until the
        // first solid one. An unloaded cell is air here — the space above a stage usually
        // has no chunks at all, and that is exactly where the sky is.
        for (int x = 0; x < _w; x++)
        {
            int gx = _minX + x;
            bool open = true;
            for (int k = 1; k <= SkyScanCells && open; k++)
                if (chunks.GetCellState(gx, _minY - k) == TileState.Solid) open = false;
            for (int y = 0; y < _h; y++)
            {
                int i = y * _w + x;
                if (_kind[i] == Solid) open = false;
                _light[i] = open ? 1f : 0f;
            }
        }

        // Spread. Each cell takes the brightest neighbour's light times what its own
        // material lets through. Forward then backward raster passes, like a chamfer
        // distance transform, with diagonals so the falloff is round, not diamond-shaped.
        for (int round = 0; round < Rounds; round++)
        {
            for (int y = 0; y < _h; y++)
            for (int x = 0; x < _w; x++)
            {
                int i = y * _w + x;
                float l = _light[i];
                if (l >= 1f) continue;
                bool solid = _kind[i] == Solid;
                float o = solid ? SolidDecay : AirDecay, d = solid ? SolidDiag : AirDiag;
                if (x > 0) l = MathF.Max(l, _light[i - 1] * o);
                if (y > 0)
                {
                    l = MathF.Max(l, _light[i - _w] * o);
                    if (x > 0)      l = MathF.Max(l, _light[i - _w - 1] * d);
                    if (x < _w - 1) l = MathF.Max(l, _light[i - _w + 1] * d);
                }
                _light[i] = l;
            }
            for (int y = _h - 1; y >= 0; y--)
            for (int x = _w - 1; x >= 0; x--)
            {
                int i = y * _w + x;
                float l = _light[i];
                if (l >= 1f) continue;
                bool solid = _kind[i] == Solid;
                float o = solid ? SolidDecay : AirDecay, d = solid ? SolidDiag : AirDiag;
                if (x < _w - 1) l = MathF.Max(l, _light[i + 1] * o);
                if (y < _h - 1)
                {
                    l = MathF.Max(l, _light[i + _w] * o);
                    if (x < _w - 1) l = MathF.Max(l, _light[i + _w + 1] * d);
                    if (x > 0)      l = MathF.Max(l, _light[i + _w - 1] * d);
                }
                _light[i] = l;
            }
        }
    }

    // Open / Solid / Unloaded. Outside the window everything reads as Unloaded.
    public byte KindAt(int gtx, int gty)
    {
        int x = gtx - _minX, y = gty - _minY;
        return (uint)x < (uint)_w && (uint)y < (uint)_h ? _kind[y * _w + x] : Unloaded;
    }

    // "Draws no silhouette toward it": true for solid cells and for the unloaded edge of
    // the world, which is not a surface and must not be outlined like one.
    public bool IsSolid(int gtx, int gty) => KindAt(gtx, gty) != Open;

    // Raw light, 0..1. For an open cell this is how lit the air there is.
    public float LightAt(int gtx, int gty)
    {
        int x = gtx - _minX, y = gty - _minY;
        return (uint)x < (uint)_w && (uint)y < (uint)_h ? _light[y * _w + x] : 1f;
    }

    // Fill brightness for a SOLID cell. A solid cell's own value has already paid one
    // step of SolidDecay just for being solid, so divide it back out: a tile touching
    // full light is fully bright, and the falloff starts at the tile behind it.
    public float ShadeAt(int gtx, int gty)
        => Brightness(MathF.Min(1f, LightAt(gtx, gty) / SolidDecay));

    // Light → on-screen brightness multiplier, with the floor applied.
    public static float Brightness(float light) => MinLight + (1f - MinLight) * light;

    private static int FloorDiv(int n, int d) => n >= 0 ? n / d : -((-n + d - 1) / d);
}
