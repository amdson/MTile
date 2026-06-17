using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// One bone segment to splat: endpoints in world space + a glow tint.
public readonly struct MetaballBone
{
    public readonly Vector2 A, B;
    public readonly Color   Tint;
    public MetaballBone(Vector2 a, Vector2 b, Color tint) { A = a; B = b; Tint = tint; }
}

// Threshold + color knobs for the composite pass.
public struct MetaballStyle
{
    public float Radius;     // capsule kernel radius (world units)
    public float Iso;        // surface threshold on the summed density
    public float Edge;       // half-width of the antialiased iso band
    public Color Inner;      // color at the dense core
    public Color Rim;        // color at the iso edge
    public float ColorMix;   // 0 = Inner/Rim ramp, 1 = the field's own accumulated color

    public static MetaballStyle Default => new()
    {
        Radius   = 16f,
        Iso      = 0.6f,
        Edge     = 0.06f,
        Inner    = new Color(180, 220, 255),
        Rim      = new Color(40, 90, 200),
        ColorMix = 0f,
    };
}

// Segment-metaball renderer: generalizes point metaballs to line-segment bones. Per bone
// it splats a capsule density kernel (CapsuleSplat.fx) additively into an offscreen field,
// then thresholds + colors the merged field to screen (MetaballComposite.fx). Both shaders
// are ps_3_0, so this is portable to KNI/WebGL. Render-only.
public sealed class SkeletonMetaballRenderer : IDisposable
{
    private readonly GraphicsDevice _gd;
    private readonly Effect _splat;
    private readonly Effect _composite;
    private readonly SpriteBatch _batch;
    private readonly Texture2D _pixel;
    private readonly int _downscale;
    private readonly SurfaceFormat _format;
    private RenderTarget2D _field;
    private RenderTargetBinding[] _prev;

    // Debug: composite the raw density field as grayscale (no threshold) to inspect splats.
    public bool DebugRawField;

    // Pure additive (src + dst) — unlike BlendState.Additive, which premultiplies source by
    // its own alpha and would accumulate f² instead of f. We want a true Σ kernel density.
    private static readonly BlendState SumBlend = new()
    {
        ColorSourceBlend = Blend.One, ColorDestinationBlend = Blend.One,
        AlphaSourceBlend = Blend.One, AlphaDestinationBlend = Blend.One,
    };

    public SkeletonMetaballRenderer(GraphicsDevice gd, Effect splat, Effect composite,
                                    int downscale = 2, SurfaceFormat format = SurfaceFormat.Color)
    {
        _gd        = gd;
        _splat     = splat;
        _composite = composite;
        _batch     = new SpriteBatch(gd);
        _pixel     = new Texture2D(gd, 1, 1);
        _pixel.SetData(new[] { Color.White });
        _downscale = Math.Max(1, downscale);
        _format    = format;
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

    // `cam` is Camera.GetTransform (world->screen pixels). Splats every bone into the
    // field, then composites the thresholded blob over the current target.
    public void Render(Matrix cam, IReadOnlyList<MetaballBone> bones, in MetaballStyle style)
    {
        if (bones == null || bones.Count == 0) return;
        EnsureTarget();
        float invDs = 1f / _downscale;

        // ── Accumulate the density field (additive capsule splats via SpriteBatch) ──
        _prev = _gd.GetRenderTargets();
        _gd.SetRenderTarget(_field);
        _gd.Clear(Color.Transparent);

        // This MonoGame/DesktopGL build does NOT auto-set MatrixTransform on custom
        // SpriteBatch effects, so the vertex shader would collapse all geometry. Set it
        // ourselves: ortho over the *field* viewport (pixel coords -> clip).
        SetParam(_splat, "MatrixTransform",
            Matrix.CreateOrthographicOffCenter(0, _field.Width, _field.Height, 0, 0, 1));
        SetParam(_splat, "Radius", style.Radius);
        foreach (var b in bones)
        {
            // World-space AABB of the segment, expanded by the kernel radius.
            float minX = MathF.Min(b.A.X, b.B.X) - style.Radius, maxX = MathF.Max(b.A.X, b.B.X) + style.Radius;
            float minY = MathF.Min(b.A.Y, b.B.Y) - style.Radius, maxY = MathF.Max(b.A.Y, b.B.Y) + style.Radius;
            var worldMin = new Vector2(minX, minY);
            var worldMax = new Vector2(maxX, maxY);

            // Same AABB in field-RT pixels (cam has no rotation, so AABB maps to AABB).
            Vector2 sMin = Vector2.Transform(worldMin, cam) * invDs;
            Vector2 sMax = Vector2.Transform(worldMax, cam) * invDs;
            var dest = new Rectangle((int)MathF.Floor(sMin.X), (int)MathF.Floor(sMin.Y),
                                     (int)MathF.Ceiling(sMax.X - sMin.X), (int)MathF.Ceiling(sMax.Y - sMin.Y));

            SetParam(_splat, "A", b.A);
            SetParam(_splat, "B", b.B);
            SetParam(_splat, "Tint", b.Tint.ToVector3());
            SetParam(_splat, "WorldMin", worldMin);
            SetParam(_splat, "WorldSize", worldMax - worldMin);

            // One Begin/End per bone — the per-bone uniforms can't change inside a batch.
            _batch.Begin(SpriteSortMode.Deferred, SumBlend, SamplerState.PointClamp,
                         null, null, _splat);
            _batch.Draw(_pixel, dest, Color.White);
            _batch.End();
        }
        _gd.SetRenderTargets(_prev);

        // ── Threshold + colormap to screen ──
        var vp = _gd.Viewport;
        SetParam(_composite, "MatrixTransform",
            Matrix.CreateOrthographicOffCenter(0, vp.Width, vp.Height, 0, 0, 1));
        SetParam(_composite, "Iso", style.Iso);
        SetParam(_composite, "Edge", style.Edge);
        SetParam(_composite, "InnerColor", style.Inner.ToVector3());
        SetParam(_composite, "RimColor", style.Rim.ToVector3());
        SetParam(_composite, "ColorMix", style.ColorMix);
        SetParam(_composite, "RawField", DebugRawField ? 1f : 0f);
        _batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp,
                     null, null, _composite);
        _batch.Draw(_field, vp.Bounds, Color.White);
        _batch.End();
    }

    // Null-safe: the effect compiler strips parameters a shader variant doesn't reference.
    private static void SetParam(Effect fx, string name, float v) => fx.Parameters[name]?.SetValue(v);
    private static void SetParam(Effect fx, string name, Vector2 v) => fx.Parameters[name]?.SetValue(v);
    private static void SetParam(Effect fx, string name, Vector3 v) => fx.Parameters[name]?.SetValue(v);
    private static void SetParam(Effect fx, string name, Matrix v) => fx.Parameters[name]?.SetValue(v);

    public void Dispose()
    {
        _field?.Dispose();
        _batch?.Dispose();
        _pixel?.Dispose();
    }
}
