using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// Grayscale modulation atlas for tile fills, built at load from the rock material
// previews in Assets/. Each source PNG is a shaded sphere render, not a tileable
// texture, so we lift a flat patch from the sphere's center, normalize it so its
// mean brightness is 1.0 (tiles keep their palette color on average), and pack
// both patches side by side in one texture — a single texture for every tile fill
// so the SpriteBatch never has to flush mid-chunk.
public sealed class TileTextureAtlas
{
    public const int PatchSize = 96;

    public Texture2D Texture { get; }
    private readonly int _patchCount;

    private TileTextureAtlas(Texture2D texture, int patchCount)
    {
        Texture = texture;
        _patchCount = patchCount;
    }

    public static TileTextureAtlas Build(GraphicsDevice device, params Texture2D[] sources)
    {
        var atlas = new Color[PatchSize * sources.Length * PatchSize];
        for (int i = 0; i < sources.Length; i++)
            WritePatch(sources[i], atlas, i, sources.Length);
        var tex = new Texture2D(device, PatchSize * sources.Length, PatchSize);
        tex.SetData(atlas);
        return new TileTextureAtlas(tex, sources.Length);
    }

    private static void WritePatch(Texture2D src, Color[] atlas, int index, int count)
    {
        // Flat-ish region: the middle 40% of the sphere, away from rim shading.
        int side = (int)(Math.Min(src.Width, src.Height) * 0.4f);
        int x0 = (src.Width - side) / 2, y0 = (src.Height - side) / 2;
        var region = new Color[side * side];
        src.GetData(0, new Rectangle(x0, y0, side, side), region, 0, region.Length);

        // Nearest-neighbor resample to the patch, luminance only.
        var lum = new float[PatchSize * PatchSize];
        float mean = 0f;
        for (int y = 0; y < PatchSize; y++)
            for (int x = 0; x < PatchSize; x++)
            {
                var c = region[(y * side / PatchSize) * side + (x * side / PatchSize)];
                float l = (0.299f * c.R + 0.587f * c.G + 0.114f * c.B) / 255f;
                lum[y * PatchSize + x] = l;
                mean += l;
            }
        mean /= lum.Length;

        // Mean-normalized, contrast-damped, capped at 1 (SpriteBatch tint can only
        // darken) — reads as surface grain without shifting the palette.
        int stride = PatchSize * count;
        for (int y = 0; y < PatchSize; y++)
            for (int x = 0; x < PatchSize; x++)
            {
                float f = MathHelper.Clamp(0.88f + 0.55f * (lum[y * PatchSize + x] / mean - 1f), 0.62f, 1f);
                byte v = (byte)(f * 255f);
                atlas[y * stride + index * PatchSize + x] = new Color(v, v, v);
            }
    }

    // Deterministic per-cell source rect: patch chosen by tile type, offset hashed
    // from the global cell coords so neighboring tiles don't repeat.
    public Rectangle SourceFor(TileType type, int gtx, int gty, int size)
    {
        int patch = type == TileType.Stone ? 0 : _patchCount - 1;
        uint h = (uint)(gtx * 73856093) ^ (uint)(gty * 19349663);
        int range = PatchSize - size;
        int ox = (int)(h % (uint)range);
        int oy = (int)((h >> 8) % (uint)range);
        return new Rectangle(patch * PatchSize + ox, oy, size, size);
    }
}
