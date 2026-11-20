using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// The "ink and wash" terrain look: one generated texture holding
//   - a tileable watercolour wash (grayscale modulation for tile fills), laid out so
//     the wash runs CONTINUOUSLY across neighbouring tiles instead of each tile
//     getting its own random patch, and
//   - a handful of hand-wobbled ink strokes (silhouette edges, rounded corner arcs,
//     broken interior seams) and a pigment-pooling gradient, white + premultiplied
//     alpha, tinted at draw time.
// Everything is drawn through the ordinary world SpriteBatch (one texture, no shader),
// so it runs on every host. Render-only; the fixed seeds make it identical every boot.
//
// Resolution matters here: the old tile look drew 1-world-pixel rectangles, which the
// camera zoom turns into fat screen blocks. These sprites carry several texels per
// world pixel, so a line can be a fraction of a world pixel wide.
public sealed class TerrainInk
{
    // ---- wash -----------------------------------------------------------------------
    // Texels of wash per tile, and tiles before the wash repeats.
    public const int WashTexels = 32;
    public const int WashTiles  = 16;
    private const int WashSize  = WashTexels * WashTiles;        // 512
    // The wash sits at (1,1) with a one-texel wrapped border, so bilinear filtering at
    // the repeat edge reads the wash's own far side and not a neighbouring sprite.
    private const int WashX = 1, WashY = 1;

    // ---- ink strokes ----------------------------------------------------------------
    // Texels of ink per tile (strokes are authored one tile long).
    public const int InkTexels = 64;
    // Corner radius, in wash texels (the fill is cut in whole wash-texel rows) and the
    // same length in ink texels.
    public const int CornerWash = 9;
    public const int CornerInk  = CornerWash * InkTexels / WashTexels;   // 18

    public const int EdgeVariants = 4, SeamVariants = 4, CornerVariants = 2, PoolVariants = 2;
    // Edge stroke: InkTexels x EdgeH, the tile boundary on row EdgeRow.
    public const int EdgeH = 16, EdgeRow = 6;
    // Seam stroke: InkTexels x SeamH, centred on row SeamRow.
    public const int SeamH = 8, SeamRow = 4;
    // Corner arc (canonical: top-left): CornerSize square, the tile's corner at
    // (CornerPad, CornerPad).
    public const int CornerPad = 4, CornerSize = CornerInk + 2 * CornerPad + 2;
    // Pooling gradient: InkTexels x PoolH, the tile boundary on row 0, fading inward.
    public const int PoolH = 24;

    private const int AtlasSize = 1024;
    private const int InkY = WashSize + 8;       // first row of the stroke area
    private const int Gap = 4;                   // transparent gap between strokes

    public Texture2D Texture { get; }

    private TerrainInk(Texture2D texture) { Texture = texture; }

    // Whole-tile wash rect for a cell. Adjacent cells get adjacent rects, so the wash
    // is continuous across the seam; it repeats every WashTiles cells.
    public static Rectangle WashSource(int gtx, int gty)
    {
        int cx = ((gtx % WashTiles) + WashTiles) % WashTiles;
        int cy = ((gty % WashTiles) + WashTiles) % WashTiles;
        return new Rectangle(WashX + cx * WashTexels, WashY + cy * WashTexels, WashTexels, WashTexels);
    }

    public static Rectangle Edge(int v)   => new((InkTexels + Gap) * v, InkY, InkTexels, EdgeH);
    public static Rectangle Seam(int v)   => new((InkTexels + Gap) * v, InkY + EdgeH + Gap, InkTexels, SeamH);
    public static Rectangle Pool(int v)   => new((InkTexels + Gap) * v, InkY + EdgeH + SeamH + 2 * Gap, InkTexels, PoolH);
    public static Rectangle Corner(int v) => new((CornerSize + Gap) * v, InkY + EdgeH + SeamH + PoolH + 3 * Gap,
                                                 CornerSize, CornerSize);

    public static TerrainInk Build(GraphicsDevice device)
    {
        var px = new Color[AtlasSize * AtlasSize];
        WriteWash(px);
        for (int v = 0; v < EdgeVariants; v++)   WriteEdge(px, Edge(v), (uint)(101 + v));
        for (int v = 0; v < SeamVariants; v++)   WriteSeam(px, Seam(v), (uint)(211 + v));
        for (int v = 0; v < PoolVariants; v++)   WritePool(px, Pool(v), (uint)(307 + v));
        for (int v = 0; v < CornerVariants; v++) WriteCorner(px, Corner(v), (uint)(401 + v));
        var tex = new Texture2D(device, AtlasSize, AtlasSize);
        tex.SetData(px);
        return new TerrainInk(tex);
    }

    // ---- wash -----------------------------------------------------------------------
    // Broad pigment blotches, thin darker "tide lines" where a wash dried against
    // another, and fine granulation. Mean brightness sits near 0.9 and never exceeds 1
    // (a SpriteBatch tint can only darken).
    private static void WriteWash(Color[] px)
    {
        var wash = new float[WashSize * WashSize];
        for (int y = 0; y < WashSize; y++)
            for (int x = 0; x < WashSize; x++)
            {
                float u = x / (float)WashSize, v = y / (float)WashSize;
                float blot = 0.50f * Periodic(u, v, 4, 11u) + 0.30f * Periodic(u, v, 8, 12u)
                           + 0.20f * Periodic(u, v, 16, 13u);                       // -1..1
                float mask = Periodic(u, v, 6, 21u) * 0.5f + 0.5f;                  // where tide lines show
                float t = blot / 0.07f;
                float tide = MathF.Exp(-t * t) * MathHelper.Clamp((mask - 0.35f) * 3f, 0f, 1f);
                float grain = 0.6f * Periodic(u, v, 128, 31u) + 0.4f * Periodic(u, v, 256, 32u);
                float streak = Periodic(u, v * 0.25f, 64, 41u);                     // faint brush direction
                float f = 0.90f + 0.085f * blot - 0.075f * tide + 0.045f * grain + 0.02f * streak;
                wash[y * WashSize + x] = MathHelper.Clamp(f, 0.62f, 1f);
            }
        // With the wrapped border: atlas (WashX-1 .. WashX+WashSize) both ways.
        for (int y = -1; y <= WashSize; y++)
            for (int x = -1; x <= WashSize; x++)
            {
                int sx = (x + WashSize) % WashSize, sy = (y + WashSize) % WashSize;
                byte b = (byte)(wash[sy * WashSize + sx] * 255f);
                px[(WashY + y) * AtlasSize + WashX + x] = new Color(b, b, b, (byte)255);
            }
    }

    // ---- strokes --------------------------------------------------------------------
    // 0 within `flat` texels of either end, 1 in the middle: strokes are dead straight
    // and nominal-weight where they meet a neighbour or a corner arc, so they join.
    private static float Envelope(float x, float flat)
    {
        float a = MathHelper.Clamp((x - flat) / 10f, 0f, 1f);
        float b = MathHelper.Clamp((InkTexels - flat - x) / 10f, 0f, 1f);
        float e = MathF.Min(a, b);
        return e * e * (3f - 2f * e);
    }

    private const float EdgeHalfWidth = 1.3f;

    private static void WriteEdge(Color[] px, Rectangle r, uint seed)
    {
        for (int x = 0; x < r.Width; x++)
        {
            float xc = x + 0.5f, env = Envelope(xc, CornerInk);
            float centre = EdgeRow + env * 1.1f * Noise1(xc / 13f, seed);
            float half = EdgeHalfWidth + env * (0.55f * Noise1(xc / 9f, seed + 50u) + 0.25f * Noise1(xc / 3.5f, seed + 60u));
            // Dry-brush: the stroke thins out briefly here and there, never at the ends.
            float dry = 1f - env * 0.45f * MathHelper.Clamp(Noise1(xc / 6f, seed + 70u) * 2.2f - 0.9f, 0f, 1f);
            for (int y = 0; y < r.Height; y++)
            {
                float d = MathF.Abs(y + 0.5f - centre);
                float a = MathHelper.Clamp(half + 0.5f - d, 0f, 1f) * dry;
                px[(r.Y + y) * AtlasSize + r.X + x] = Color.White * a;
            }
        }
    }

    private static void WriteSeam(Color[] px, Rectangle r, uint seed)
    {
        for (int x = 0; x < r.Width; x++)
        {
            float xc = x + 0.5f, env = Envelope(xc, 3f);
            float centre = SeamRow + env * 1.0f * Noise1(xc / 15f, seed);
            float half = 0.55f + 0.3f * Noise1(xc / 7f, seed + 50u);
            // Broken line: the pen lifts for stretches, so the seam reads as a sketched
            // joint rather than a ruled grid.
            float gap = MathHelper.Clamp((Noise1(xc / 11f, seed + 70u) + 0.45f) * 2.5f, 0f, 1f);
            for (int y = 0; y < r.Height; y++)
            {
                float d = MathF.Abs(y + 0.5f - centre);
                float a = MathHelper.Clamp(half + 0.5f - d, 0f, 1f) * gap;
                px[(r.Y + y) * AtlasSize + r.X + x] = Color.White * a;
            }
        }
    }

    // Pigment pools against an edge and thins inward.
    private static void WritePool(Color[] px, Rectangle r, uint seed)
    {
        for (int y = 0; y < r.Height; y++)
            for (int x = 0; x < r.Width; x++)
            {
                float depth = (y + 0.5f) / r.Height;
                float reach = 0.75f + 0.25f * Noise1((x + 0.5f) / 10f, seed);       // uneven inner edge
                float t = MathHelper.Clamp(1f - depth / reach, 0f, 1f);
                float a = MathF.Pow(t, 1.7f) * (0.85f + 0.15f * Noise1((x + 0.5f) / 3f + y * 0.37f, seed + 9u));
                px[(r.Y + y) * AtlasSize + r.X + x] = Color.White * MathHelper.Clamp(a, 0f, 1f);
            }
    }

    // Quarter arc for a top-left convex corner: centre (CornerPad + CornerInk) on both
    // axes, radius CornerInk, so its ends land exactly on the two edge strokes.
    private static void WriteCorner(Color[] px, Rectangle r, uint seed)
    {
        float c = CornerPad + CornerInk;
        for (int y = 0; y < r.Height; y++)
            for (int x = 0; x < r.Width; x++)
            {
                float dx = x + 0.5f - c, dy = y + 0.5f - c;
                float a = 0f;
                if (dx <= 0f && dy <= 0f)
                {
                    float ang = MathF.Atan2(-dy, -dx) / MathHelper.PiOver2;          // 0..1 along the arc
                    float env = MathF.Sin(ang * MathF.PI);
                    float radius = CornerInk - env * (0.5f + 0.5f * Noise1(ang * 2.5f, seed));
                    float half = EdgeHalfWidth + env * 0.4f * Noise1(ang * 3.5f, seed + 50u);
                    float d = MathF.Abs(MathF.Sqrt(dx * dx + dy * dy) - radius);
                    a = MathHelper.Clamp(half + 0.5f - d, 0f, 1f);
                }
                px[(r.Y + y) * AtlasSize + r.X + x] = Color.White * a;
            }
    }

    // ---- noise ----------------------------------------------------------------------
    // Smooth 1D value noise, -1..1.
    private static float Noise1(float x, uint seed)
    {
        float fx = MathF.Floor(x);
        int ix = (int)fx;
        float t = x - fx;
        t = t * t * (3f - 2f * t);
        float a = Signed(Hash(ix, 0, seed)), b = Signed(Hash(ix + 1, 0, seed));
        return a + (b - a) * t;
    }

    // Smooth 2D value noise over the unit square, periodic with `cells` lattice cells
    // per side, -1..1. Periodicity is what makes the wash tile without a seam.
    private static float Periodic(float u, float v, int cells, uint seed)
    {
        float x = u * cells, y = v * cells;
        float fx = MathF.Floor(x), fy = MathF.Floor(y);
        int ix = (int)fx, iy = (int)fy;
        float tx = x - fx, ty = y - fy;
        tx = tx * tx * (3f - 2f * tx);
        ty = ty * ty * (3f - 2f * ty);
        int x0 = Wrap(ix, cells), x1 = Wrap(ix + 1, cells), y0 = Wrap(iy, cells), y1 = Wrap(iy + 1, cells);
        float a = Signed(Hash(x0, y0, seed)), b = Signed(Hash(x1, y0, seed));
        float c = Signed(Hash(x0, y1, seed)), d = Signed(Hash(x1, y1, seed));
        float top = a + (b - a) * tx, bot = c + (d - c) * tx;
        return top + (bot - top) * ty;
    }

    private static int Wrap(int i, int n) => ((i % n) + n) % n;

    private static float Signed(uint h) => (h & 0xFFFFFF) / (float)0x800000 - 1f;

    public static uint Hash(int x, int y, uint seed)
    {
        unchecked
        {
            uint h = seed ^ ((uint)x * 0x27D4EB2Du) ^ ((uint)y * 0x165667B1u + 0x9E3779B9u);
            h ^= h >> 16; h *= 0x7FEB352Du;
            h ^= h >> 15; h *= 0x846CA68Bu;
            h ^= h >> 16;
            return h;
        }
    }
}
