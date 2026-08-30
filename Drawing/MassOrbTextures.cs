using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// The clod's look (Plans/BLOCK_THROW_PLAN.md T3): a disc sampled from the tile grain
// atlas, one per material, so the held ball, the chasing ball and the flying ball —
// which are the same entity — are also the same picture as the ground it came from.
// Built once at load from TileTextureAtlas (grayscale grain; the material's palette
// color is the draw tint, exactly as tile fills do it). Render-only; null until Game1
// builds it, and MassOrbSprite falls back to its vector pose without it.
public static class MassOrbTextures
{
    public const int Size = 32;

    private static Texture2D[] _byType;

    public static Texture2D For(TileType type)
    {
        if (_byType == null) return null;
        int i = (int)type;
        return i >= 0 && i < _byType.Length ? _byType[i] : null;
    }

    public static void Build(GraphicsDevice device, TileTextureAtlas atlas)
    {
        if (atlas == null) return;
        var atlasPixels = new Color[atlas.Texture.Width * atlas.Texture.Height];
        atlas.Texture.GetData(atlasPixels);
        int stride = atlas.Texture.Width;

        const int typeCount = TileTypes.Count;
        var result = new Texture2D[typeCount];
        var pixels = new Color[Size * Size];
        float half = Size * 0.5f;
        for (int t = 0; t < typeCount; t++)
        {
            // Same patch rule as the chunk renderer; a fixed offset inside it so the
            // grain reads as one clod rather than a random crop per frame.
            var src = atlas.SourceFor((TileType)t, 7, 3, Size);
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                var g = atlasPixels[(src.Y + y) * stride + src.X + x];
                // Soft-edged disc: full inside, one-texel feather at the rim, plus a
                // little rim darkening so it reads as round, not as a cut-out.
                float dx = x + 0.5f - half, dy = y + 0.5f - half;
                float r  = MathF.Sqrt(dx * dx + dy * dy) / half;
                float a  = MathHelper.Clamp((1f - r) * half, 0f, 1f);
                float shade = 1f - 0.25f * MathHelper.Clamp((r - 0.6f) / 0.4f, 0f, 1f);
                byte v = (byte)(g.R * shade);
                pixels[y * Size + x] = new Color(v, v, v) * a;   // premultiplied like SpriteBatch expects
            }
            var tex = new Texture2D(device, Size, Size);
            tex.SetData(pixels);
            result[t] = tex;
        }
        _byType = result;
    }
}

// The ball's sprite: the material's orb texture scaled to the live radius and tinted
// by material, spinning with its travel. Falls back to the vector Pose when the
// textures aren't built (tests, missing assets).
public sealed class MassOrbSprite : Sprite
{
    public TileType Type;
    public float    Radius = 5f;
    public float    Spin;        // rad/s, render-only — set from the body's velocity by SyncSprite

    // Radius the fallback Pose was authored at, so Scale maps radius → pose scale.
    private readonly float _poseRadius;

    public MassOrbSprite(TileType type, Pose fallback, float poseRadius)
    {
        Type        = type;
        Pose        = fallback;
        _poseRadius = MathF.Max(1e-3f, poseRadius);
    }

    public override void Update(float dt) => Rotation += Spin * dt;

    public override void Draw(DrawContext ctx)
    {
        if (!Visible) return;
        var tex = MassOrbTextures.For(Type);
        if (tex == null)
        {
            Scale = Radius / _poseRadius;
            base.Draw(ctx);
            return;
        }
        float scale = Radius * 2f / MassOrbTextures.Size;
        ctx.SpriteBatch.Draw(tex, Position, null, HitFlashTracker.Whiten(Tint, Flash), Rotation,
            new Vector2(MassOrbTextures.Size * 0.5f, MassOrbTextures.Size * 0.5f),
            scale, SpriteEffects.None, 0f);
    }
}
