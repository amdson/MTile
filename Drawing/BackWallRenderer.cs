using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// Back wall (todo V7): dark earth behind open cells that are underground, so a hole reads
// as a cave cut into a solid mass instead of a window onto the scenery.
//
// Two rules decide where it shows, and neither remembers what was dug:
//   * HEIGHT: an open cell at or below the column's GroundLine always has wall behind it,
//     however wide the excavation gets.
//   * LIGHT: above the line, wall fades in as TerrainLightField says the cell is dark — a
//     roofed tunnel through a hill darkens away from its mouths, while a gap in open air
//     (a destroyed pillar, the space under a ledge) is lit and shows the scenery.
// The wall is one neutral earth colour, tinted by the light. Render-only; drawn by
// ChunkRenderer before the tiles.
public sealed class BackWallRenderer
{
    // ---- Tunables ----------------------------------------------------------------------
    // Wall brightness at full light, as a fraction of the material's palette colour (after desaturation).
    private const float WallBrightness = 0.46f;
    // How far the palette colour is pulled toward its own grey before darkening (0 = full colour).
    private const float WallDesaturate = 0.35f;
    // Cool cast mixed in after darkening, so the wall recedes against the warm play-layer rock.
    private static readonly Color WallCoolTint = new(18, 24, 38);
    // Strength of that cool cast (0 = none, 1 = the wall IS the tint).
    private const float WallCoolAmount = 0.30f;
    // Width in px of the inner shadow a remaining solid tile casts onto the wall beside it.
    private const int EdgeShadowWidth = (Chunk.TileSize + 3) / 4;
    // Opacity of that shadow band (black over the wall).
    private const float EdgeShadowAlpha = 0.35f;
    // Extra opacity on the single px row touching the solid tile: the contact crease.
    private const float EdgeContactAlpha = 0.30f;
    // Grain is sampled continuously across blocks of this many cells (the most that fit in
    // one atlas patch), so the wall has no per-cell seams the way the gridded tiles do.
    private const int GrainBlockCells = TileTextureAtlas.PatchSize / Chunk.TileSize;

    // Above the ground line, wall starts to show once the light drops below this...
    private const float DarkStart = 0.55f;
    // ...and is fully opaque at or below this.
    private const float DarkFull = 0.25f;
    // The material whose palette colour the wall is derived from.
    private const TileType WallMaterial = TileType.Stone;

    private readonly SpriteBatch _spriteBatch;
    private readonly Texture2D   _pixel;
    // Optional grain atlas shared with ChunkRenderer; null → flat fills.
    public TileTextureAtlas Atlas;
    // The ink tile look rounds convex corners, which leaves a small notch of each such
    // tile open. When set, a wall cell also paints a thin strip under each solid
    // neighbour, so a notch underground shows wall and not the sky backdrop. The strips
    // are otherwise hidden by the tile fills drawn after this.
    public bool FillNotches;
    private const int NotchPx = (Chunk.TileSize * TerrainInk.CornerWash + TerrainInk.WashTexels - 1) / TerrainInk.WashTexels;

    private readonly GroundLine _ground = new();
    // Wall cells found by the fill pass (cell + opacity), reused by the shadow pass so
    // each pass stays on one texture. Grows to the high-water mark, then no allocs.
    private readonly List<(int X, int Y, float A)> _wallCells = new();
    private int[] _line = Array.Empty<int>();

    private static readonly Color WallColor = BuildWallColor();

    public BackWallRenderer(SpriteBatch spriteBatch, Texture2D pixel)
    {
        _spriteBatch = spriteBatch;
        _pixel       = pixel;
    }

    private static Color BuildWallColor()
    {
        var c = TilePalette.BaseColor(WallMaterial).ToVector3();
        float lum = 0.299f * c.X + 0.587f * c.Y + 0.114f * c.Z;
        c = Vector3.Lerp(c, new Vector3(lum), WallDesaturate) * WallBrightness;
        c = Vector3.Lerp(c, WallCoolTint.ToVector3(), WallCoolAmount);
        return new Color(c);
    }

    // Inclusive visible cell window; `light` must already be updated for it.
    public void Draw(ChunkMap chunks, TerrainLightField light,
                     int minGx, int minGy, int maxGx, int maxGy)
    {
        const int T = Chunk.TileSize, W = EdgeShadowWidth;
        // One cell of margin: camera shake offsets the draw transform, not the window.
        minGx--; minGy--; maxGx++; maxGy++;
        int cols = maxGx - minGx + 1;
        if (cols <= 0) return;
        if (_line.Length < cols) _line = new int[cols];
        _ground.Begin(chunks);
        for (int x = 0; x < cols; x++) _line[x] = _ground.At(minGx + x);

        _wallCells.Clear();

        // Pass 1: wall fills (one texture: the atlas, or the pixel without one). Full
        // cell, no gutter: the grid is what marks the play layer, so its absence is half
        // of what pushes the wall back.
        for (int gty = minGy; gty <= maxGy; gty++)
            for (int gtx = minGx; gtx <= maxGx; gtx++)
            {
                // Solid covers the wall completely. Empty / Spike / Sprouting all show it.
                if (light.KindAt(gtx, gty) != TerrainLightField.Open) continue;
                float l = light.LightAt(gtx, gty);
                float a = gty >= _line[gtx - minGx] ? 1f
                        : MathHelper.Clamp((DarkStart - l) / (DarkStart - DarkFull), 0f, 1f);
                if (a <= 0f) continue;
                float b = TerrainLightField.Brightness(l);
                var color = new Color(WallColor.R / 255f * b * a, WallColor.G / 255f * b * a,
                                      WallColor.B / 255f * b * a, a);   // premultiplied
                var dest = new Rectangle(gtx * T, gty * T, T, T);
                if (Atlas != null)
                    _spriteBatch.Draw(Atlas.Texture, dest, GrainSource(WallMaterial, gtx, gty), color);
                else
                    _spriteBatch.Draw(_pixel, dest, color);
                _wallCells.Add((gtx, gty, a));
                if (FillNotches)
                {
                    const int N = NotchPx;
                    int left = gtx * T, top = gty * T;
                    if (light.KindAt(gtx, gty + 1) == TerrainLightField.Solid) _spriteBatch.Draw(_pixel, new Rectangle(left, top + T, T, N), color);
                    if (light.KindAt(gtx, gty - 1) == TerrainLightField.Solid) _spriteBatch.Draw(_pixel, new Rectangle(left, top - N, T, N), color);
                    if (light.KindAt(gtx + 1, gty) == TerrainLightField.Solid) _spriteBatch.Draw(_pixel, new Rectangle(left + T, top, N, T), color);
                    if (light.KindAt(gtx - 1, gty) == TerrainLightField.Solid) _spriteBatch.Draw(_pixel, new Rectangle(left - N, top, N, T), color);
                }
            }

        // Pass 2: inner shadow where the wall meets terrain that is still standing, so a
        // carved face reads as having thickness. All on the pixel texture.
        for (int i = 0; i < _wallCells.Count; i++)
        {
            var (gtx, gty, a) = _wallCells[i];
            var band    = Color.Black * (EdgeShadowAlpha * a);
            var contact = Color.Black * (EdgeContactAlpha * a);
            int left = gtx * T, top = gty * T;
            if (light.KindAt(gtx, gty - 1) == TerrainLightField.Solid)
            {
                _spriteBatch.Draw(_pixel, new Rectangle(left, top, T, W), band);
                _spriteBatch.Draw(_pixel, new Rectangle(left, top, T, 1), contact);
            }
            if (light.KindAt(gtx, gty + 1) == TerrainLightField.Solid)
            {
                _spriteBatch.Draw(_pixel, new Rectangle(left, top + T - W, T, W), band);
                _spriteBatch.Draw(_pixel, new Rectangle(left, top + T - 1, T, 1), contact);
            }
            if (light.KindAt(gtx - 1, gty) == TerrainLightField.Solid)
            {
                _spriteBatch.Draw(_pixel, new Rectangle(left, top, W, T), band);
                _spriteBatch.Draw(_pixel, new Rectangle(left, top, 1, T), contact);
            }
            if (light.KindAt(gtx + 1, gty) == TerrainLightField.Solid)
            {
                _spriteBatch.Draw(_pixel, new Rectangle(left + T - W, top, W, T), band);
                _spriteBatch.Draw(_pixel, new Rectangle(left + T - 1, top, 1, T), contact);
            }
        }
    }

    // Grain source rect for a wall cell. ChunkRenderer hashes a fresh offset per cell,
    // which is right for separate blocks but would stamp a faint grid onto the wall; here
    // neighbouring cells sample neighbouring pixels, so the grain runs unbroken across a
    // block of GrainBlockCells cells. The atlas only exposes its patch choice through
    // SourceFor, so the patch column is read back off the rect it returns.
    private Rectangle GrainSource(TileType type, int gtx, int gty)
    {
        int patchX = Atlas.SourceFor(type, gtx, gty, Chunk.TileSize).X
                     / TileTextureAtlas.PatchSize * TileTextureAtlas.PatchSize;
        int bx = ((gtx % GrainBlockCells) + GrainBlockCells) % GrainBlockCells;
        int by = ((gty % GrainBlockCells) + GrainBlockCells) % GrainBlockCells;
        return new Rectangle(patchX + bx * Chunk.TileSize, by * Chunk.TileSize,
                             Chunk.TileSize, Chunk.TileSize);
    }
}
