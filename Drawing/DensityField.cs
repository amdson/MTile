using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// A density field F(p) = Σ kernelᵢ(p) accumulated on the GPU: each splat draws a
// pre-baked radial kernel into an offscreen RenderTarget with additive blending, so the
// blend unit sums the kernels for us — no per-pixel loop, no CPU field. Compositing the
// target additively onto the screen gives a smooth glow. This is the portable core the
// segment-metaball pass (Layer 3) reuses; only the splat brush and the composite change.
//
// Render-only, downstream of the sim.
public sealed class DensityField : IDisposable
{
    private readonly GraphicsDevice _gd;
    private readonly SpriteBatch    _batch;
    private readonly Texture2D      _kernel;
    private readonly int            _downscale;
    private readonly SurfaceFormat  _format;
    private RenderTarget2D _field;
    private RenderTargetBinding[] _prevTargets;
    private bool _accumulating;

    public RenderTarget2D Target => _field;

    // downscale>1 renders the field at a fraction of the backbuffer (cheaper + softer on
    // the upscale). format defaults to 8-bit Color; pass HalfVector4 if additive headroom
    // above 1.0 causes banding (see RENDERING_UPGRADE_PLAN "target-format gotcha").
    public DensityField(GraphicsDevice gd, int kernelSize = 128, int downscale = 1,
                        SurfaceFormat format = SurfaceFormat.Color)
    {
        _gd        = gd;
        _batch     = new SpriteBatch(gd);
        _downscale = Math.Max(1, downscale);
        _format    = format;
        _kernel    = BakeRadialKernel(gd, kernelSize);
        EnsureTarget();
    }

    private void EnsureTarget()
    {
        var pp = _gd.PresentationParameters;
        int w = Math.Max(1, pp.BackBufferWidth  / _downscale);
        int h = Math.Max(1, pp.BackBufferHeight / _downscale);
        if (_field != null && _field.Width == w && _field.Height == h) return;
        _field?.Dispose();
        _field = new RenderTarget2D(_gd, w, h, false, _format, DepthFormat.None);
    }

    // Begin accumulating. `worldTransform` is the camera matrix (Camera.GetTransform);
    // splats are placed in world space and folded into field-RT pixels.
    public void Begin(Matrix worldTransform)
    {
        EnsureTarget();
        // Save whatever's bound (the backbuffer normally, or the screenshot target during
        // a capture) so End restores it rather than forcing the backbuffer.
        _prevTargets = _gd.GetRenderTargets();
        _gd.SetRenderTarget(_field);
        _gd.Clear(Color.Transparent);
        Matrix t = worldTransform * Matrix.CreateScale(1f / _downscale, 1f / _downscale, 1f);
        _batch.Begin(SpriteSortMode.Deferred, BlendState.Additive, SamplerState.LinearClamp,
                     null, null, null, t);
        _accumulating = true;
    }

    // Add one kernel of world-space `radius` centered at `worldPos`, tinted/weighted by
    // `color` (its rgb is the glow color; lower values = a fainter contribution).
    public void Splat(Vector2 worldPos, float radius, Color color)
    {
        if (!_accumulating) throw new InvalidOperationException("DensityField.Splat outside Begin/End.");
        var origin = new Vector2(_kernel.Width * 0.5f, _kernel.Height * 0.5f);
        float scale = radius * 2f / _kernel.Width;   // kernel spans 2*radius across
        _batch.Draw(_kernel, worldPos, null, color, 0f, origin, scale, SpriteEffects.None, 0f);
    }

    public void End()
    {
        if (!_accumulating) throw new InvalidOperationException("DensityField.End without Begin.");
        _batch.End();
        _gd.SetRenderTargets(_prevTargets);   // null/empty → backbuffer
        _accumulating = false;
    }

    // Draw the accumulated field onto the current render target (the backbuffer).
    // Additive by default (glow); pass a different blend for other looks. `tint` scales
    // the whole field. Must be called after End() (the target is resolved by then).
    public void Composite(BlendState blend = null, Color? tint = null)
    {
        _batch.Begin(SpriteSortMode.Deferred, blend ?? BlendState.Additive, SamplerState.LinearClamp);
        _batch.Draw(_field, _gd.Viewport.Bounds, tint ?? Color.White);
        _batch.End();
    }

    // Smooth radial falloff baked once into a texture. rgb = white, alpha = kernel weight
    // (1 at center → 0 at edge), so additive draws sum `color.rgb * tint` weighted by the
    // falloff. A squared smoothstep gives a soft, metaball-friendly profile.
    private static Texture2D BakeRadialKernel(GraphicsDevice gd, int size)
    {
        var tex  = new Texture2D(gd, size, size);
        var data = new Color[size * size];
        float c = (size - 1) * 0.5f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x - c) / c, dy = (y - c) / c;
                float d  = MathF.Sqrt(dx * dx + dy * dy);          // 0 center → 1 edge
                float f  = MathF.Max(0f, 1f - d);
                f = f * f * (3f - 2f * f);                          // smoothstep: soft, broad
                data[y * size + x] = new Color(f, f, f, f);
            }
        tex.SetData(data);
        return tex;
    }

    public void Dispose()
    {
        _field?.Dispose();
        _kernel?.Dispose();
        _batch?.Dispose();
    }
}
