using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// A physically-motivated glowing trail: the screened reaction–diffusion field
//
//     ∂u/∂t = D∇²u − λu + s·1_Ω(t)        (glow density u, emitter region Ω)
//
// accumulated in a persistent offscreen target. Each frame the buffer is
//   (1) reprojected by the camera delta  — so a slash mark hangs in the WORLD,
//                                           not smeared across the screen,
//   (2) decayed by e^(−λΔt)              — the −λu term, exact over the step,
//   (3) blurred by a small separable kernel of width √(2DΔt) — the diffusion term,
//   (4) stamped along the emitter's swept path this frame — the source s·1_Ω.
//
// This is operator splitting: the (2)+(4) part is the exact "decay-and-stamp"
// accumulation buffer; the (3) part is one tiny blur. Because variance adds, the
// tiny per-frame blurs integrate — through the decay's exponential memory — into
// the full steady halo of radius ≈ √2·ℓ (ℓ=√(D/λ)): a footprint laid down σ ago
// has been dimmed by e^(−λσ) AND blurred to variance 2Dσ, exactly the spacetime
// Green's function. Visually: sharp head, soft graded tail, for free.
//
// Render-only, downstream of the sim. Fully portable — no custom shaders; the
// blur is a handful of bilinear SpriteBatch taps, the field is stored in color and
// composited additively.
public sealed class GlowTrailField : IDisposable
{
    // ── tuning (artist-facing) ───────────────────────────────────────────────
    public float Lambda            = 6f;   // decay rate λ (1/s). Visible trail ≈ 5/λ s.
    public float HaloWorld         = 6f;   // steady halo radius in world px (≈ √2·ℓ).
    public float SourceFill        = 1f;   // brightness (×glowColor) a fresh stamp deposits.
    public float StampRadiusWorld  = 5f;   // kernel radius = the emitter's (blade) half-thickness.
    public float StampSpacingWorld = 3f;   // c: substep spacing along the path (fast-motion gap fill).
    public int   MaxSubsteps       = 24;

    // TRUE linear add (One,One). NOT BlendState.Additive — that is (SourceAlpha,One),
    // which weights the source by its alpha; through the separable blur that couples
    // rgb to the alpha channel and collapses the whole field to zero in a frame or two.
    // We treat the field as a plain linear quantity, so every accumulate is One+One.
    private static readonly BlendState AddOneOne = new()
    {
        ColorSourceBlend = Blend.One, ColorDestinationBlend = Blend.One,
        AlphaSourceBlend = Blend.One, AlphaDestinationBlend = Blend.One,
    };

    private readonly GraphicsDevice _gd;
    private readonly SpriteBatch    _batch;
    private readonly Texture2D      _kernel;
    private readonly int            _downscale;

    private RenderTarget2D _acc;   // the persistent field (current after Composite).
    private RenderTarget2D _tmp;   // scratch / ping-pong partner.
    private RenderTargetBinding[] _prevTargets;
    private Matrix _prevCam;
    private bool   _hasPrevCam;
    private bool   _stamping;

    public GlowTrailField(GraphicsDevice gd, int downscale = 2)
    {
        _gd        = gd;
        _downscale = Math.Max(1, downscale);
        _batch     = new SpriteBatch(gd);
        _kernel    = BakeRadialKernel(gd, 128);
        EnsureTargets();
    }

    private void EnsureTargets()
    {
        var pp = _gd.PresentationParameters;
        int w = Math.Max(1, pp.BackBufferWidth  / _downscale);
        int h = Math.Max(1, pp.BackBufferHeight / _downscale);
        if (_acc != null && _acc.Width == w && _acc.Height == h) return;

        _acc?.Dispose();
        _tmp?.Dispose();
        // PreserveContents: the field must survive being unbound between BeginFrame
        // (write) and Composite (read), and from one frame to the next (accumulation).
        _acc = new RenderTarget2D(_gd, w, h, false, SurfaceFormat.Color, DepthFormat.None,
                                  0, RenderTargetUsage.PreserveContents);
        _tmp = new RenderTarget2D(_gd, w, h, false, SurfaceFormat.Color, DepthFormat.None,
                                  0, RenderTargetUsage.PreserveContents);
        _hasPrevCam = false;

        var saved = _gd.GetRenderTargets();
        _gd.SetRenderTarget(_acc); _gd.Clear(Color.Transparent);
        _gd.SetRenderTarget(_tmp); _gd.Clear(Color.Transparent);
        Restore(saved);
    }

    // World→buffer transform for camera `cam` (world→screen, then ÷downscale).
    private Matrix BufferXform(Matrix cam) =>
        cam * Matrix.CreateScale(1f / _downscale, 1f / _downscale, 1f);

    // Advance the field one frame: reproject → decay → diffuse (blur). Leaves the
    // field's render target bound with an additive stamp batch open; the caller
    // issues StampSweep(...) calls, then closes with Composite(...).
    public void BeginFrame(Matrix cam, float dt)
    {
        EnsureTargets();
        dt = MathHelper.Clamp(dt, 1f / 240f, 0.1f);   // ignore frame hitches / pauses
        _prevTargets = _gd.GetRenderTargets();

        float a   = MathF.Exp(-Lambda * dt);          // decay factor over the step
        Matrix Bcur = BufferXform(cam);

        // (1)+(2) decay + reproject: _acc(prev field) → _tmp. Opaque write of
        // src·(a,a,a,a); the reproj matrix re-anchors last frame's buffer to where
        // those world points sit under THIS frame's camera.
        Matrix reproj = _hasPrevCam ? Matrix.Invert(BufferXform(_prevCam)) * Bcur : Matrix.Identity;
        _gd.SetRenderTarget(_tmp);
        _gd.Clear(Color.Transparent);
        _batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp,
                     null, null, null, reproj);
        _batch.Draw(_acc, Vector2.Zero, null, new Color(a, a, a, a));
        _batch.End();

        // (3) diffusion: separable blur of width σ=√(2DΔt) in buffer pixels, with
        // D = λℓ², ℓ = HaloWorld/√2  ⇒  σ_world = HaloWorld·√(λΔt).
        float ell      = HaloWorld * 0.70710678f;                 // ℓ = Halo/√2
        float sigWorld = ell * MathF.Sqrt(2f * Lambda * dt);      // √(2DΔt)
        float worldToBuf = MathF.Abs(cam.M11) / _downscale;       // zoom / downscale
        float sigBuf   = sigWorld * worldToBuf;
        BlurAxis(_tmp, _acc, new Vector2(sigBuf, 0f));            // H: _tmp → _acc
        BlurAxis(_acc, _tmp, new Vector2(0f, sigBuf));            // V: _acc → _tmp (field now in _tmp)

        // (4) open the source pass: additive stamps in world space into _tmp. A fresh
        // stamp deposits the FULL SourceFill·glowColor (a swipe covers each point too
        // briefly to ramp toward a plateau, so we deposit bright and let decay fade it).
        _gd.SetRenderTarget(_tmp);
        _batch.Begin(SpriteSortMode.Deferred, AddOneOne, SamplerState.LinearClamp,
                     null, null, null, Bcur);
        _stamping = true;

        _prevCam = cam;
        _hasPrevCam = true;
    }

    // 5-tap binomial [1,4,6,4,1]/16 at tap spacing σ has variance exactly σ². For
    // σ ≲ 0.3 px the taps fall within a texel and it reduces to the identity (a
    // sharp, un-diffused trail), which is the correct low-D limit.
    private static readonly float[] BinomW = { 1f / 16f, 4f / 16f, 6f / 16f, 4f / 16f, 1f / 16f };
    private void BlurAxis(RenderTarget2D src, RenderTarget2D dst, Vector2 sigma)
    {
        _gd.SetRenderTarget(dst);
        _gd.Clear(Color.Transparent);
        _batch.Begin(SpriteSortMode.Deferred, AddOneOne, SamplerState.LinearClamp);
        for (int k = 0; k < 5; k++)
        {
            float w = BinomW[k];
            _batch.Draw(src, sigma * (k - 2), null, new Color(w, w, w, w));
        }
        _batch.End();
    }

    // Stamp the moving emitter along its swept segment (world space) this frame,
    // substepped so fast motion doesn't bead: N = ⌈‖cur−prev‖ / spacing⌉ kernels,
    // capped at MaxSubsteps. Each substep is a distinct world location, so it
    // deposits a full dab (brightness is path-length based, not speed-based). Call
    // between BeginFrame and Composite.
    public void StampSweep(Vector2 prevWorld, Vector2 curWorld, Color glowColor)
    {
        if (!_stamping) throw new InvalidOperationException("StampSweep outside BeginFrame/Composite.");

        float len = Vector2.Distance(prevWorld, curWorld);
        int n = (int)MathF.Ceiling(len / MathF.Max(0.5f, StampSpacingWorld));
        n = Math.Clamp(n, 1, MaxSubsteps);

        var   origin = new Vector2(_kernel.Width * 0.5f, _kernel.Height * 0.5f);
        float scale  = StampRadiusWorld * 2f / _kernel.Width;     // kernel spans 2·radius
        Color tint   = glowColor * SourceFill;

        for (int k = 1; k <= n; k++)
        {
            Vector2 p = Vector2.Lerp(prevWorld, curWorld, (float)k / n);
            _batch.Draw(_kernel, p, null, tint, 0f, origin, scale, SpriteEffects.None, 0f);
        }
    }

    // Close the source pass, promote the new field, and additively composite the
    // tone-mapped glow onto whatever target was bound when BeginFrame ran. `intensity`
    // scales the whole effect (the composite is linear additive — bright cores sum
    // like real light; raise to a saturating curve in a shader later if desired).
    public void Composite(float intensity)
    {
        if (_stamping) { _batch.End(); _stamping = false; }

        (_acc, _tmp) = (_tmp, _acc);            // _acc now holds the freshly stamped field
        Restore(_prevTargets);

        _batch.Begin(SpriteSortMode.Deferred, AddOneOne, SamplerState.LinearClamp);
        _batch.Draw(_acc, _gd.Viewport.Bounds, new Color(intensity, intensity, intensity, intensity));
        _batch.End();
    }

    private void Restore(RenderTargetBinding[] targets)
    {
        if (targets == null || targets.Length == 0) _gd.SetRenderTarget(null);
        else _gd.SetRenderTargets(targets);
    }

    // Soft radial falloff baked once. rgb = falloff (so additive draws sum a colored
    // disc weighted by the profile) and a = falloff (for blending). Squared smoothstep
    // gives a broad, soft brush.
    private static Texture2D BakeRadialKernel(GraphicsDevice gd, int size)
    {
        var tex  = new Texture2D(gd, size, size);
        var data = new Color[size * size];
        float c  = (size - 1) * 0.5f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x - c) / c, dy = (y - c) / c;
                float d  = MathF.Sqrt(dx * dx + dy * dy);      // 0 center → 1 edge
                float f  = MathF.Max(0f, 1f - d);
                f = f * f * (3f - 2f * f);                      // smoothstep
                data[y * size + x] = new Color(f, f, f, f);
            }
        tex.SetData(data);
        return tex;
    }

    public void Dispose()
    {
        _acc?.Dispose();
        _tmp?.Dispose();
        _kernel?.Dispose();
        _batch?.Dispose();
    }
}
