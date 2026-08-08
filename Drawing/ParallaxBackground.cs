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

    public Color Sky = new Color(96, 148, 204);
    // Fraction of camera motion the strip inherits. Small = far away.
    public float ParallaxX = 0.25f;
    public float ParallaxY = 0.10f;

    public ParallaxBackground(Texture2D tex, Texture2D pixel)
    {
        _tex = tex;
        _pixel = pixel;
        // Bottom-row average = the "infinite brown" under the strip, matched to the art.
        var row = new Color[tex.Width];
        tex.GetData(0, new Rectangle(0, tex.Height - 1, tex.Width, 1), row, 0, tex.Width);
        long r = 0, g = 0, b = 0;
        foreach (var c in row) { r += c.R; g += c.G; b += c.B; }
        _ground = new Color((int)(r / tex.Width), (int)(g / tex.Width), (int)(b / tex.Width));
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
