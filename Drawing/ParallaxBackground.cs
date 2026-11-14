using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// Horizontally-tiling parallax backdrop drawn in screen space before the world pass.
// The mountain PNG scrolls at a fraction of the camera's motion; its sky region is
// transparent, so the screen is flood-filled with sky blue first, and everything
// below the strip's bottom edge is extended with the image's own bottom-row color
// so the seam is invisible.
public sealed class ParallaxBackground : IBackdrop
{
    private readonly Texture2D _tex;
    private readonly Texture2D _pixel;
    private readonly Color _ground;

    // ---- Atmospheric perspective (retune by eye) --------------------------------
    // The strip is one image at one depth, so it gets a single uniform push-back,
    // baked into the texture once at load: no per-frame cost and no shader (a
    // sprite tint multiplies, so it can neither desaturate nor lift the darks).
    // Colour the strip fades toward — the same haze TreeParallaxBackground uses.
    private static readonly Color Fog = new Color(146, 172, 176);
    // Fraction lerped toward Fog: lifts the darks and flattens contrast.
    private const float FogAmount = 0.35f;
    // Saturation removed before the fog (0 = art as drawn, 1 = greyscale).
    private const float Desaturate = 0.25f;
    // -----------------------------------------------------------------------------

    public Color Sky = new Color(96, 148, 204);
    // Fraction of camera motion the strip inherits. Small = far away.
    public float ParallaxX = 0.25f;
    public float ParallaxY = 0.10f;

    public ParallaxBackground(Texture2D tex, Texture2D pixel)
    {
        _tex = tex;
        _pixel = pixel;
        ApplyAtmosphere(tex);
        // Bottom-row average = the "infinite brown" under the strip, matched to the art.
        var row = new Color[tex.Width];
        tex.GetData(0, new Rectangle(0, tex.Height - 1, tex.Width, 1), row, 0, tex.Width);
        long r = 0, g = 0, b = 0;
        foreach (var c in row) { r += c.R; g += c.G; b += c.B; }
        _ground = new Color((int)(r / tex.Width), (int)(g / tex.Width), (int)(b / tex.Width));
    }

    // Rewrites the texture in place (this class is its only user). The fog term is
    // scaled by alpha so the transparent sky region stays transparent and
    // antialiased ridge-line texels don't grow a fog-coloured fringe.
    private static void ApplyAtmosphere(Texture2D tex)
    {
        var data = new Color[tex.Width * tex.Height];
        tex.GetData(data);
        for (int i = 0; i < data.Length; i++)
        {
            var c = data[i];
            if (c.A == 0) continue;
            float l = c.R * 0.299f + c.G * 0.587f + c.B * 0.114f;
            float a = c.A / 255f * FogAmount;
            data[i] = new Color(
                (int)(MathHelper.Lerp(c.R, l, Desaturate) * (1f - FogAmount) + Fog.R * a),
                (int)(MathHelper.Lerp(c.G, l, Desaturate) * (1f - FogAmount) + Fog.G * a),
                (int)(MathHelper.Lerp(c.B, l, Desaturate) * (1f - FogAmount) + Fog.B * a),
                c.A);
        }
        tex.SetData(data);
    }

    // Own Begin/End (screen space, wrap sampling for the horizontal tile) — call
    // before the camera-transformed world batch.
    public void Draw(SpriteBatch sb, Camera camera, Vector2 screenCenter)
    {
        int screenW = (int)(screenCenter.X * 2f);
        int screenH = (int)(screenCenter.Y * 2f);

        // Scale the strip to the screen height; vertical parallax slides it, exposing
        // the sky/ground fills above and below.
        float scale = screenH / (float)_tex.Height;
        int drawH = (int)(_tex.Height * scale);
        int y = (int)(-camera.Position.Y * ParallaxY);

        int srcX = (int)(camera.Position.X * ParallaxX / scale % _tex.Width);
        if (srcX < 0) srcX += _tex.Width;
        int srcW = (int)Math.Ceiling(screenW / scale);

        sb.Begin(samplerState: SamplerState.LinearWrap);
        // Sky floods the whole screen: the strip's sky region is transparent, so blue
        // must sit behind it, not just above it.
        sb.Draw(_pixel, new Rectangle(0, 0, screenW, screenH), Sky);
        if (y + drawH < screenH)
            sb.Draw(_pixel, new Rectangle(0, y + drawH, screenW, screenH - (y + drawH)), _ground);
        sb.Draw(_tex, new Rectangle(0, y, screenW, drawH),
                new Rectangle(srcX, 0, srcW, _tex.Height), Color.White);
        sb.End();
    }
}
