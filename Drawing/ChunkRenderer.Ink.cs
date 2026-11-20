using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// The ink-and-wash tile look (see TerrainInk). Replaces DrawChunk + the pixel grass
// caps when `Ink` is set. Same inputs as the classic look — palette, variation, light
// field, damage, charge — drawn differently:
//   - fills are a continuous watercolour wash with no gutter;
//   - interior seams are thin, broken, wobbly ink lines in a darker shade of the tile;
//   - the silhouette is a sepia ink contour with pigment pooled inside it;
//   - convex corners are rounded (the fill is cut in wash-texel rows, the arc stroke
//     hides the steps), so a block stops reading as a voxel;
//   - grassed tops are a green wash bleeding down into the dirt, under drawn tufts.
public sealed partial class ChunkRenderer
{
    // Null → the classic pixel look.
    public TerrainInk Ink;
    // Drawn grass tufts for the ink look; null → wash band only.
    public GrassTuftSheet GrassTufts;

    // Palette pulled this far toward its own grey: the characters are muted washes, and
    // full-saturation ground shouts over them.
    private const float InkDesaturate = 0.22f;
    // Contour ink: the material pulled most of the way to a warm sepia (the character
    // line colour), never pure black.
    private static readonly Color InkSepia = new(58, 40, 30);
    private const float InkSepiaAmount = 0.72f;
    private const float InkLineAlpha   = 0.92f;
    // Interior seam: the tile's own colour pulled toward black, drawn translucent.
    private const float SeamDarkness = 0.45f;
    private const float SeamAlpha    = 0.55f;
    // Pigment pooled inside an exposed edge; weaker on tops, stronger on undersides.
    private const float PoolDarkness = 0.55f;
    private const float PoolAlpha    = 0.36f;
    private static readonly float[] PoolSideScale = { 0.45f, 1f, 1.3f, 1f };   // top, right, bottom, left
    // Grassed top: this many wash rows of solid turf colour, then a fade into the dirt.
    private const int TurfRows = 7, TurfFadeRows = 6;
    private static readonly Color TurfWash = new(126, 146, 86);
    private static readonly Color TurfInk  = new(52, 60, 32);

    private const float WashScale = Chunk.TileSize / (float)TerrainInk.WashTexels;
    private const float InkScale  = Chunk.TileSize / (float)TerrainInk.InkTexels;

    // Side s: 0 top, 1 right, 2 bottom, 3 left (quarter turns clockwise from top).
    // Corner c: 0 TL, 1 TR, 2 BR, 3 BL — between side (c+3)%4 and side c.
    private static readonly int[] SideBit = { EdgeTop, EdgeRight, EdgeBottom, EdgeLeft };
    // Wash texels cut from a row's end by a rounded corner, by row from the corner.
    private static readonly int[] CornerInset = BuildCornerInset();

    private static int[] BuildCornerInset()
    {
        const int R = TerrainInk.CornerWash;
        var inset = new int[R];
        for (int r = 0; r < R; r++)
        {
            float dy = R - (r + 0.5f);
            inset[r] = (int)MathF.Round(R - MathF.Sqrt(R * R - dy * dy));
        }
        return inset;
    }

    private static bool Rounded(int flags, int corner)
        => (flags & TileCharged) == 0
        && (flags & SideBit[(corner + 3) & 3]) != 0 && (flags & SideBit[corner]) != 0;

    private static Color Desaturate(Color c, float amount)
    {
        float lum = 0.299f * c.R + 0.587f * c.G + 0.114f * c.B;
        return new Color((int)(c.R + (lum - c.R) * amount), (int)(c.G + (lum - c.G) * amount),
                         (int)(c.B + (lum - c.B) * amount), 255);
    }

    private int InkFlags(ChunkMap chunks, int gtx, int gty)
    {
        int flags = 0;
        if (!_light.IsSolid(gtx - 1, gty)) flags |= EdgeLeft;
        if (!_light.IsSolid(gtx, gty - 1)) flags |= EdgeTop;
        if (!_light.IsSolid(gtx + 1, gty)) flags |= EdgeRight;
        if (!_light.IsSolid(gtx, gty + 1)) flags |= EdgeBottom;
        if (chunks.Charge.IsCharged(gtx, gty)) flags |= TileCharged;
        return flags;
    }

    // Palette (desaturated) × variation × light. Damage and charge go on in the fill.
    private Color InkTileColor(TileType type, int gtx, int gty)
        => Scale(Desaturate(TilePalette.BaseColor(type), InkDesaturate),
                 TileVariation.Brightness(type, gtx, gty) * _light.ShadeAt(gtx, gty));

    // Grass on this tile's top, 0..1 (0 unless it is exposed, uncharged dirt).
    private float InkGrass(TileType type, int gtx, int gty, int flags)
        => type == TileType.Dirt && (flags & (EdgeTop | TileCharged)) == EdgeTop ? _grass.GrowthAt(gtx, gty) : 0f;

    private bool InkWindow(Chunk chunk, out int baseX, out int baseY, out int tx0, out int tx1, out int ty0, out int ty1)
    {
        baseX = chunk.ChunkPos.X * Chunk.Size; baseY = chunk.ChunkPos.Y * Chunk.Size;
        tx0 = Math.Max(0, _visMinX - baseX); tx1 = Math.Min(Chunk.Size - 1, _visMaxX - baseX);
        ty0 = Math.Max(0, _visMinY - baseY); ty1 = Math.Min(Chunk.Size - 1, _visMaxY - baseY);
        return tx0 <= tx1 && ty0 <= ty1;
    }

    // Pass 1: every tile's wash fill. All chunks' fills go down before any stroke, so a
    // seam on a chunk boundary is never painted over by the next chunk.
    private void DrawInkFills(Chunk chunk, ChunkMap chunks)
    {
        if (!InkWindow(chunk, out int baseX, out int baseY, out int tx0, out int tx1, out int ty0, out int ty1)) return;
        const int W = TerrainInk.WashTexels, R = TerrainInk.CornerWash;
        for (int tx = tx0; tx <= tx1; tx++)
            for (int ty = ty0; ty <= ty1; ty++)
            {
                if (!chunk.Tiles[tx, ty].IsSolid) continue;
                int gtx = baseX + tx, gty = baseY + ty;
                var type = chunk.Tiles[tx, ty].Type;
                int flags = InkFlags(chunks, gtx, gty);
                var color = InkTileColor(type, gtx, gty);
                float dmgFrac = MathF.Min(chunks.Damage.Get(gtx, gty) / TileDamage.MaxHPFor(type), 1f);
                if (dmgFrac > 0f) color = Color.Lerp(color, Color.Black, dmgFrac * DamageDarkness);
                if ((flags & TileCharged) != 0) color = Color.Lerp(color, Color.White, ChargeTint);

                float grass = InkGrass(type, gtx, gty, flags);
                var turf = grass > 0f
                    ? Scale(TurfWash, TileVariation.Brightness(type, gtx, gty) * _light.ShadeAt(gtx, gty))
                    : default;

                bool tl = Rounded(flags, 0), tr = Rounded(flags, 1), br = Rounded(flags, 2), bl = Rounded(flags, 3);
                int top = tl || tr ? R : 0, bot = bl || br ? R : 0;
                if (grass > 0f) top = TurfRows + TurfFadeRows;
                var src = TerrainInk.WashSource(gtx, gty);
                float x = gtx * Chunk.TileSize, y = gty * Chunk.TileSize;

                for (int r = 0; r < top; r++)
                {
                    int l  = tl && r < R ? CornerInset[r] : 0;
                    int rr = tr && r < R ? CornerInset[r] : 0;
                    var c = color;
                    if (grass > 0f)
                    {
                        float w = r < TurfRows ? 1f : 1f - (r - TurfRows + 1) / (float)(TurfFadeRows + 1);
                        c = Color.Lerp(color, turf, w * grass);
                    }
                    WashStrip(src, x, y, l, r, W - l - rr, 1, c);
                }
                WashStrip(src, x, y, 0, top, W, W - top - bot, color);
                for (int r = 0; r < bot; r++)
                {
                    int l = bl ? CornerInset[r] : 0, rr = br ? CornerInset[r] : 0;
                    WashStrip(src, x, y, l, W - 1 - r, W - l - rr, 1, color);
                }
            }
    }

    // A sub-rect of a tile's wash, in wash texels from the tile's top-left. Positions
    // are exact multiples of WashScale, so neighbouring strips share their edges.
    private void WashStrip(Rectangle src, float x, float y, int tx, int ty, int w, int h, Color color)
    {
        if (w <= 0 || h <= 0) return;
        _spriteBatch.Draw(Ink.Texture, new Vector2(x + tx * WashScale, y + ty * WashScale),
            new Rectangle(src.X + tx, src.Y + ty, w, h), color, 0f, Vector2.Zero, WashScale, SpriteEffects.None, 0f);
    }

    // Pass 2: seams, pooled pigment, contour strokes and corner arcs.
    private void DrawInkLines(Chunk chunk, ChunkMap chunks)
    {
        if (!InkWindow(chunk, out int baseX, out int baseY, out int tx0, out int tx1, out int ty0, out int ty1)) return;
        const float T = Chunk.TileSize;
        const int K = TerrainInk.InkTexels, RK = TerrainInk.CornerInk;
        for (int tx = tx0; tx <= tx1; tx++)
            for (int ty = ty0; ty <= ty1; ty++)
            {
                if (!chunk.Tiles[tx, ty].IsSolid) continue;
                int gtx = baseX + tx, gty = baseY + ty;
                var type = chunk.Tiles[tx, ty].Type;
                int flags = InkFlags(chunks, gtx, gty);
                var color = InkTileColor(type, gtx, gty);
                uint h = TerrainInk.Hash(gtx, gty, 0x51EDu);
                var centre = new Vector2(gtx * T + T * 0.5f, gty * T + T * 0.5f);
                bool charged = (flags & TileCharged) != 0;

                // Interior seams, on the right and bottom boundaries this tile owns.
                var seam = Color.Lerp(color, Color.Black, SeamDarkness) * SeamAlpha;
                var seamOrigin = new Vector2(K * 0.5f, TerrainInk.SeamRow);
                if ((flags & EdgeBottom) == 0 && !charged)
                    _spriteBatch.Draw(Ink.Texture, new Vector2(centre.X, gty * T + T), TerrainInk.Seam((int)(h & 3)), seam,
                        0f, seamOrigin, InkScale, (h & 4) != 0 ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0f);
                if ((flags & EdgeRight) == 0 && !charged)
                    _spriteBatch.Draw(Ink.Texture, new Vector2(gtx * T + T, centre.Y), TerrainInk.Seam((int)((h >> 3) & 3)), seam,
                        MathHelper.PiOver2, seamOrigin, InkScale, (h & 64) != 0 ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0f);

                // A charged tile is outlined white on every side: the fill tint alone
                // cannot carry the state on a near-white material.
                if (charged)
                {
                    for (int s = 0; s < 4; s++)
                        _spriteBatch.Draw(Ink.Texture, centre, TerrainInk.Edge((int)((h >> (8 + 2 * s)) & 3)), Color.White,
                            s * MathHelper.PiOver2, new Vector2(K * 0.5f, TerrainInk.EdgeRow + K * 0.5f), InkScale,
                            SpriteEffects.None, 0f);
                    continue;
                }
                if ((flags & (EdgeLeft | EdgeTop | EdgeRight | EdgeBottom)) == 0) continue;

                float shade = _light.ShadeAt(gtx, gty);
                var baseColor = TilePalette.BaseColor(type);
                var ink  = Scale(Color.Lerp(Scale(baseColor, 0.4f), InkSepia, InkSepiaAmount), shade) * InkLineAlpha;
                var pool = Color.Lerp(color, Color.Black, PoolDarkness);
                bool grassed = InkGrass(type, gtx, gty, flags) > 0f;

                for (int s = 0; s < 4; s++)
                {
                    if ((flags & SideBit[s]) == 0) continue;
                    int clipS = Rounded(flags, s) ? RK : 0, clipE = Rounded(flags, (s + 1) & 3) ? RK : 0;
                    float rot = s * MathHelper.PiOver2;
                    bool turfTop = s == 0 && grassed;
                    if (!turfTop)
                    {
                        var ps = TerrainInk.Pool((int)((h >> (16 + s)) & 1));
                        ps.X += clipS; ps.Width -= clipS + clipE;
                        _spriteBatch.Draw(Ink.Texture, centre, ps, pool * (PoolAlpha * PoolSideScale[s]), rot,
                            new Vector2(K * 0.5f - clipS, K * 0.5f), InkScale, SpriteEffects.None, 0f);
                    }
                    var es = TerrainInk.Edge((int)((h >> (8 + 2 * s)) & 3));
                    es.X += clipS; es.Width -= clipS + clipE;
                    _spriteBatch.Draw(Ink.Texture, centre, es, turfTop ? Scale(TurfInk, shade) * InkLineAlpha : ink, rot,
                        new Vector2(K * 0.5f - clipS, TerrainInk.EdgeRow + K * 0.5f), InkScale, SpriteEffects.None, 0f);
                }
                for (int c = 0; c < 4; c++)
                {
                    if (!Rounded(flags, c)) continue;
                    _spriteBatch.Draw(Ink.Texture, centre, TerrainInk.Corner((int)((h >> (20 + c)) & 1)), ink,
                        c * MathHelper.PiOver2, new Vector2(TerrainInk.CornerPad + K * 0.5f, TerrainInk.CornerPad + K * 0.5f),
                        InkScale, SpriteEffects.None, 0f);
                }
            }
    }

    // Short tufts drawn OVER the characters' feet, so they stand in the grass and not on
    // it. Call inside the world batch, after the characters.
    public void DrawGrassFront(ChunkMap chunks)
    {
        if (Ink == null || GrassTufts == null) return;
        _grass.DrawTufts(_spriteBatch, GrassTufts, chunks, _light, _visMinX, _visMinY, _visMaxX, _visMaxY, front: true);
    }
}
