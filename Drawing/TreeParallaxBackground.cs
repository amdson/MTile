using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// Screen-space backdrop drawn before the world pass (same contract as ParallaxBackground).
public interface IBackdrop
{
    void Draw(SpriteBatch sb, Camera camera, Vector2 screenCenter);
}

/*
God rays
- angle
- width
- number of gradiations (god rays should be golden light of stacked layers of increasing brightness)
- layer (behind/before some trees)
- distance from screen
- should all appear to come from a point far upper right off screen (we may need trig to calculate start point / angle)
*/

// Procedural forest backdrop: one tree PNG scattered across several depth layers,
// each scrolling at its own fraction of the camera's motion. Tree placement is a
// periodic 1-D lattice of slots per layer; every per-tree choice (skip, height,
// aspect, flip, giant) is hashed from the slot index. Far layers are smaller,
// foggier, and slower; a small chance of "giant" trees on the horizon layers keeps
// the skyline from reading as a uniform hedge. Below the nearest tree line: grass,
// then dirt, then deep-rock fills so digging underground fades to dark.
//
// Each layer is BAKED ONCE into a horizontally-wrapping RenderTarget2D strip and
// then scrolled with 1:1 pixel mapping. Drawing the (huge) tree PNG directly every
// frame minifies it ~10-40x at subpixel positions, which shimmers under camera
// motion; baking rasterizes each tree exactly once so the field is pixel-stable.
public sealed class TreeParallaxBackground : IBackdrop, IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly Texture2D _tree;
    private readonly Texture2D _pixel;
    private readonly float _texAspect; // width / height of the tree art

    public Color SkyTop     = new Color(120, 168, 214);
    public Color SkyHorizon = new Color(196, 214, 226);
    // Distant tint trees fade toward (atmospheric perspective).
    public Color Fog        = new Color(186, 204, 218);
    public Color Grass      = new Color(74, 110, 58);
    public Color Dirt       = new Color(72, 54, 40);
    public Color DeepRock   = new Color(34, 26, 22);

    // Perspective model: pinhole camera, depth measured in focal lengths. A plane
    // at depth Z projects with factor s = 1/(1+Z) (similar triangles) — and that
    // ONE factor is simultaneously the plane's parallax rate, its size scale, and
    // how far its ground line has converged toward the vanishing horizon. Z = 0
    // would be the play plane (s = 1); Z → ∞ pins to the horizon (s → 0).
    private static float Proj(float z) => 1f / (1f + z);

    // Screen fraction of the VANISHING horizon (eye level) — the line all ground
    // planes converge to as Z → ∞.
    public float HorizonFrac = 0.56f;
    // World px the ground plane sits below eye level; each layer's ground line is
    // horizonBase + GroundDrop * Proj(Z), so nearer planes sit proportionally lower.
    public float GroundDrop = 184f;
    // Vertical parallax as a fraction of each plane's projection factor (0 = all
    // ground lines vertically fixed, 1 = full perspective vertical motion).
    public float ParallaxY = 0.25f;
    // Depth shading: how much a plane darkens with distance. A plane at depth Z is
    // scaled by 1 - DepthShade*(1-Proj(Z)) — unshaded at the play plane, approaching
    // full DepthShade darkening at the horizon. 0 disables. Applied to tree tint
    // (bake time) and the grasa s bands, before fog.
    public float DepthShade = 0.75f;

    private Color Shade(Color c, float z)
    {
        float k = 1f - DepthShade * (1f - Proj(z));
        return new Color((int)(c.R * k), (int)(c.G * k), (int)(c.B * k), c.A);
    }

    private struct Layer
    {
        public float Z;          // depth in focal lengths; parallax + scale = Proj(Z)
        public float TreeHeight; // base tree height in WORLD px (play-plane scale)
        public float Spacing;    // slot spacing in WORLD px; on screen it's Spacing*Proj(Z)
        public int SlotCount;    // slots per baked strip; period = Spacing*Proj(Z)*SlotCount
        public int SkipPct;      // % of slots left empty (sparseness)
        public int GiantPct;     // % chance of an oversized horizon tree
        public float FogAmount;  // 0 = near/no fog, 1 = fully fog-colored (art choice, not derived)
        public int Seed;
    }

    // Back to front (descending Z). Everything geometric — parallax, tree size,
    // spacing, ground-line height — derives from Z via Proj; only fog stays as an
    // art-directed knob. Z values converted from the previously tuned parallax
    // factors (Z = 1/Px - 1), so the composition is unchanged.
    private readonly Layer[] _layers =
    {
        new Layer { Z = 5.0f, TreeHeight = 3600f, Spacing = 1400f, SlotCount = 48, SkipPct = 45, GiantPct = 10, FogAmount = 0.70f, Seed = 101 },
        new Layer { Z = 4.5f, TreeHeight = 3600f, Spacing = 1400f, SlotCount = 48, SkipPct = 45, GiantPct = 10, FogAmount = 0.70f, Seed = 131 },
        new Layer { Z =  3.5f, TreeHeight = 1900f, Spacing = 200f, SlotCount = 32, SkipPct = 30, GiantPct = 25,  FogAmount = 0.80f, Seed = 242 },
        new Layer { Z =  3.0f, TreeHeight = 1900f, Spacing = 200f, SlotCount = 32, SkipPct = 50, GiantPct = 25,  FogAmount = 0.80f, Seed = 202 },
        new Layer { Z =  2.3f, TreeHeight = 1250f, Spacing =  100f, SlotCount = 48, SkipPct = 30, GiantPct = 20,  FogAmount = 0.68f, Seed = 303 },
        new Layer { Z =  1.7f, TreeHeight = 1250f, Spacing =  100f, SlotCount = 48, SkipPct = 50, GiantPct = 5,  FogAmount = 0.48f, Seed = 383 },
        new Layer { Z =  0.7f, TreeHeight = 1050f, Spacing =  100f, SlotCount = 48, SkipPct = 50, GiantPct = 5,  FogAmount = 0.40f, Seed = 386 },
        new Layer { Z =  0.3f, TreeHeight = 1050f, Spacing =  100f, SlotCount = 48, SkipPct = 50, GiantPct = 0, FogAmount = 0.330f, Seed = 404 },
    };

    // public BuildLayers

    private RenderTarget2D[] _strips;
    private int _builtScreenH; // rebuild trigger — strips are sized off the screen height

    public TreeParallaxBackground(GraphicsDevice device, Texture2D tree, Texture2D pixel)
    {
        _device = device;
        _tree = tree;
        _pixel = pixel;
        _texAspect = tree.Width / (float)tree.Height;
    }

    // God rays: golden beams fanning out from a sun point far off the upper-right.
    // Each ray is a stack of Gradations rotated strips sharing one axis — outer
    // strips wide and dim, inner ones narrow and brighter — drawn additively so
    // the stack reads as a soft-edged shaft of light.
    public struct GodRay
    {
        public float AnchorX;   // where the ray crosses the screen bottom at camera 0, px
        public float Z;         // depth in focal lengths; parallax + width scale = Proj(Z)
        public float Width;     // outer shaft width in WORLD px; on screen it's Width*Proj(Z)
        public int Gradations;  // stacked brightness steps
        public float Alpha;     // total core brightness (summed over gradations)
        public int AfterLayer;  // drawn in front of tree layers <= this index; -1 = behind all
    }

    // Sun position the rays converge on, as fractions of screen size (off-screen
    // upper right). The per-ray angle is derived from this point by trig in DrawRays.
    public Vector2 SunFrac = new Vector2(1.35f, -1.00f);
    // Screen-blended, so this is the hue the light pulls toward (screen saturates
    // toward white gracefully; brighter values than the old additive tuning are fine).
    public Color RayColor = new Color(255, 200, 110);
    // Transmission filter: inside a shaft the scene is multiplied toward this color
    // (light passing through, picking up warmth) before the screen pass adds glow.
    public Color RayTransmission = new Color(236, 202, 148);
    // 0 = no filtering, 1 = full multiply by RayTransmission inside the shaft.
    public float RayFilter = 0.20f;

    // Screen: out = src*(1-dst) + dst — light-on-light that saturates toward white
    // asymptotically instead of clipping like plain additive.
    private static readonly BlendState ScreenBlend = new BlendState
    {
        ColorSourceBlend      = Blend.InverseDestinationColor,
        ColorDestinationBlend = Blend.One,
        AlphaSourceBlend      = Blend.InverseDestinationAlpha,
        AlphaDestinationBlend = Blend.One,
    };
    // Multiply shaped by the (premultiplied) wedge alpha: out = dst*(a*tint + (1-a))
    // — full tint filter at the shaft core, untouched scene outside it.
    private static readonly BlendState MultiplyBlend = new BlendState
    {
        ColorSourceBlend      = Blend.DestinationColor,
        ColorDestinationBlend = Blend.InverseSourceAlpha,
        AlphaSourceBlend      = Blend.DestinationAlpha,
        AlphaDestinationBlend = Blend.InverseSourceAlpha,
    };
    // Scroll-space period the ray anchors repeat over, in screens.
    public float RayPeriodScreens = 2.5f;
    // How much the innermost (brightest) gradation narrows relative to the outer
    // cone: 0 = all gradations full width, 1 = innermost pinches to nothing.
    public float GradationTaper = 0.45f;

    // Hand-tunable, like _layers. Z pairs each ray with a tree-plane depth (same
    // Proj factor drives its parallax and on-screen widdth); AfterLayer slots it
    // between planes (in front of layers <= AfterLayer). Z and world Width values
    // converted from the previous screen-space tuning, so the look is unchanged.
    private readonly GodRay[] _rays =
    {
        new GodRay { AnchorX =  150f, Z = 1.4f, Width = 3400f, Gradations = 4, Alpha = 0.9f,  AfterLayer = 3 },
        new GodRay { AnchorX =  700f, Z = 1.3f, Width = 2250f, Gradations = 4, Alpha = 0.90f, AfterLayer = 3 },
        new GodRay { AnchorX = 1250f, Z =  1.3f, Width = 1600f, Gradations = 3, Alpha = 0.95f, AfterLayer = 3 },
        new GodRay { AnchorX =  450f, Z =  3.3f, Width = 1320f, Gradations = 5, Alpha = 0.98f, AfterLayer = 3 },
        new GodRay { AnchorX = 1600f, Z =  3.3f, Width =  630f, Gradations = 4, Alpha = 0.90f, AfterLayer = 3 },
        new GodRay { AnchorX = 1000f, Z =  3.3f, Width =  930f, Gradations = 5, Alpha = 0.95f, AfterLayer = 1 },
    };

    private bool HasRays(int afterLayer)
    {
        foreach (var r in _rays) if (r.AfterLayer == afterLayer) return true;
        return false;
    }

    // Two passes, each in its own batch (callers End the main wrap batch around
    // this): first MULTIPLY filters the scene inside each shaft toward the warm
    // transmission color (light passing through, picking up hue), then SCREEN lays
    // the golden glow on top without clipping to white.
    private void DrawRays(SpriteBatch sb, int afterLayer, float camX, int screenW, int screenH)
    {
        _wedge ??= BuildWedge();
        var sun = new Vector2(SunFrac.X * screenW, SunFrac.Y * screenH);
        float period = RayPeriodScreens * screenW;

        // Per-ray geometry is recomputed in both passes; cache it once.
        Span<float> rots = stackalloc float[_rays.Length];
        Span<float> lens = stackalloc float[_rays.Length];
        for (int i = 0; i < _rays.Length; i++)
        {
            ref readonly var ray = ref _rays[i];
            if (ray.AfterLayer != afterLayer) continue;

            // Parallax-scrolled anchor (rate = Proj(Z), same as a tree plane at
            // this depth), wrapped so rays recur every period of scroll-space;
            // centered so the visible window is always populated.
            float ax = ray.AnchorX - camX * Proj(ray.Z);
            ax = ((ax % period) + period) % period - (period - screenW) / 2f;

            // The trig: aim from the off-screen sun at the anchor point. Every ray
            // shares the same origin, so the fan converges on the sun.
            var d = new Vector2(ax, screenH) - sun;
            rots[i] = MathF.Atan2(d.Y, d.X);
            lens[i] = d.Length() * 1.3f; // overshoot past the anchor / screen edge
        }

        // Pass 1 — transmission: multiply the shaft interior toward RayTransmission,
        // full width only (gradations belong to the glow, not the filter).
        if (RayFilter > 0f)
        {
            var filter = Color.Lerp(Color.White, RayTransmission, RayFilter);
            sb.Begin(blendState: MultiplyBlend);
            for (int i = 0; i < _rays.Length; i++)
            {
                ref readonly var ray = ref _rays[i];
                if (ray.AfterLayer != afterLayer) continue;
                sb.Draw(_wedge, sun, null, filter, rots[i],
                        new Vector2(0f, WedgeH / 2f),
                        new Vector2(lens[i] / WedgeW, ray.Width * Proj(ray.Z) * 1.3f / WedgeH),
                        SpriteEffects.None, 0f);
            }
            sb.End();
        }

        // Pass 2 — glow: screen-blend the gradation stack.
        sb.Begin(blendState: ScreenBlend);
        for (int i = 0; i < _rays.Length; i++)
        {
            ref readonly var ray = ref _rays[i];
            if (ray.AfterLayer != afterLayer) continue;
            float rs = Proj(ray.Z);

            for (int g = 0; g < ray.Gradations; g++)
            {
                // Inner gradations narrow only subtly (GradationTaper), not to zero.
                float w = ray.Width * rs * (1f - GradationTaper * g / (float)ray.Gradations);
                float a = ray.Alpha / ray.Gradations;
                // The wedge's apex sits at the sun; Width is the cone's width at the
                // anchor distance (len overshoots by 1.3x, so scale w to match).
                sb.Draw(_wedge, sun, null, RayColor * a, rots[i],
                        new Vector2(0f, WedgeH / 2f),
                        new Vector2(lens[i] / WedgeW, w * 1.3f / WedgeH), SpriteEffects.None, 0f);
            }
        }
        sb.End();
    }

    // A horizontal wedge: apex at the left edge (the sun), linearly widening to the
    // full texture height at the right, with a ~1.5px antialiased edge. Drawn scaled
    // per gradation, so every ray is a true cone radiating from the sun point.
    private const int WedgeW = 256, WedgeH = 128;
    private Texture2D _wedge;

    private Texture2D BuildWedge()
    {
        var tex = new Texture2D(_device, WedgeW, WedgeH);
        var data = new Color[WedgeW * WedgeH];
        for (int x = 0; x < WedgeW; x++)
        {
            // Half-width of the cone at this distance from the apex, in px.
            float hw = (x + 0.5f) / WedgeW * (WedgeH / 2f);
            for (int y = 0; y < WedgeH; y++)
            {
                float dy = MathF.Abs(y + 0.5f - WedgeH / 2f);
                float a = Math.Clamp((hw - dy) / 1.5f + 0.5f, 0f, 1f);
                data[y * WedgeW + x] = Color.White * a; // premultiplied
            }
        }
        tex.SetData(data);
        return tex;
    }

    // Largest height multiplier a tree in this layer can get (jitter × giant).
    private static float MaxScale(in Layer layer) =>
        1.4f * (layer.GiantPct > 0 ? 3.2f : 1f);

    // Rasterize each layer's trees once into a wrapping strip. Called from Draw
    // (needs the screen height); re-baked only when the window is resized.
    private void BuildStrips(SpriteBatch sb, int screenH)
    {
        Dispose();
        _strips = new RenderTarget2D[_layers.Length];
        _builtScreenH = screenH;

        for (int li = 0; li < _layers.Length; li++)
        {
            ref readonly var layer = ref _layers[li];
            // Project world-space sizes to screen px at this plane's depth.
            float s = Proj(layer.Z);
            float baseH = layer.TreeHeight * s;
            float spacing = layer.Spacing * s;
            // Clamp the strip to the device's texture cap (Reach — the KNI/WebGL
            // profile — allows only 2048). The lattice is periodic, so dropping
            // slots just makes the strip repeat sooner instead of overflowing.
            int maxTex = _device.GraphicsProfile == GraphicsProfile.Reach ? 2048 : 4096;
            int slots  = layer.SlotCount;
            if ((int)(spacing * slots) > maxTex)
                slots = Math.Max(1, (int)(maxTex / spacing));
            int stripW = Math.Min(maxTex, (int)(spacing * slots));
            int stripH = Math.Min(maxTex, (int)MathF.Ceiling(baseH * MaxScale(layer)));

            var rt = new RenderTarget2D(_device, stripW, stripH);
            _strips[li] = rt;
            _device.SetRenderTarget(rt);
            _device.Clear(Color.Transparent);
            sb.Begin(samplerState: SamplerState.LinearClamp);

            var tint = Shade(Color.Lerp(Color.White, Fog, layer.FogAmount), layer.Z);
            for (int i = 0; i < slots; i++)
            {
                uint h = Hash(layer.Seed, i);
                if ((int)(h % 100) < layer.SkipPct) continue;

                float jitter = (Rand01(h, 1) - 0.5f) * spacing * 0.8f;
                float x = i * spacing + jitter;

                float height = baseH * (0.40f + Rand01(h, 2) * 0.70f);
                if (layer.GiantPct > 0 && (int)(Hash(layer.Seed ^ 0x5bd1, i) % 100) < layer.GiantPct)
                    height *= 1.2f + Rand01(h, 3) * 1.0f;

                float aspect = 0.9f + Rand01(h, 4) * 0.2f;
                float width = height * _texAspect * aspect;
                var fx = (h & 0x1000) != 0 ? SpriteEffects.FlipHorizontally : SpriteEffects.None;

                // Bottom-aligned to the strip's bottom edge; the ground-line sink is
                // applied when the strip is placed. A tree straddling the strip edge
                // is drawn again shifted by one period so the wrap seam is sd eamless.
                var dest = new Rectangle((int)(x - width / 2f), (int) (stripH - height),
                                         (int)width, (int)height);
                sb.Draw(_tree, dest, null, tint, 0f, Vector2.Zero, fx, 0f);
                if (dest.Left < 0)
                    sb.Draw(_tree, new Rectangle(dest.X + stripW, dest.Y, dest.Width, dest.Height),
                            null, tint, 0f, Vector2.Zero, fx, 0f);
                if (dest.Right > stripW)
                    sb.Draw(_tree, new Rectangle(dest.X - stripW, dest.Y, dest.Width, dest.Height),
                            null, tint, 0f, Vector2.Zero, fx, 0f);
            }

            sb.End();
        }
        // Backbuffer contents are undefined after the target swap, but Draw floods
        // the full screen with sky right after.
        _device.SetRenderTarget(null);
    }

    public void Draw(SpriteBatch sb, Camera camera, Vector2 screenCenter)
    {
        int screenW = (int)(screenCenter.X * 2f);
        int screenH = (int)(screenCenter.Y * 2f);
        if (screenW <= 0 || screenH <= 0) return;
        if (_strips == null || _builtScreenH != screenH) BuildStrips(sb, screenH);

        // Each layer is a rigid plane at depth Z: ground line and trees share the
        // one projection factor Proj(Z), so they move together, and the lines
        // converge toward the vanishing horizon as Z grows.
        float horizonBase = HorizonFrac * screenH;
        Span<float> groundYs = stackalloc float[_layers.Length];
        float minGroundY = float.MaxValue;
        for (int li = 0; li < _layers.Length; li++)
        {
            groundYs[li] = horizonBase
                           + (GroundDrop - camera.Position.Y * ParallaxY) * Proj(_layers[li].Z);
            minGroundY = MathF.Min(minGroundY, groundYs[li]);
        }

        // Clamp sampling + manual tiling: Reach (the KNI/WebGL profile) forbids Wrap
        // on non-power-of-two textures, so the strips are drawn in segments instead
        // of via a source rect that runs past the texture edge.
        sb.Begin(samplerState: SamplerState.LinearClamp);

        // Sky gradient, top of screen down to the topmost ground line (banded
        // strips — cheap and invisible at this contrast). Every layer's band fills
        // to the screen bottom, so sky only needs to reach the highest line.
        const int SkyBands = 48;
        int skyBottom = Math.Max(0, (int)minGroundY);
        if (skyBottom > 0)
        {
            float bandH = skyBottom / (float)SkyBands;
            for (int i = 0; i < SkyBands; i++)
            {
                var c = Color.Lerp(SkyTop, SkyHorizon, i / (float)(SkyBands - 1));
                int y0 = (int)(i * bandH);
                int y1 = (int)((i + 1) * bandH);
                sb.Draw(_pixel, new Rectangle(0, y0, screenW, Math.Max(1, y1 - y0)), c);
            }
        }

        // Rays behind every tree plane (AfterLayer = -1) go right after the sky.
        if (HasRays(-1))
        {
            sb.End();
            DrawRays(sb, -1, camera.Position.X, screenW, screenH);
            sb.Begin(samplerState: SamplerState.LinearClamp);
        }

        for (int li = 0; li < _layers.Length; li++)
        {
            ref readonly var layer = ref _layers[li];
            float groundY = groundYs[li];

            // This layer's terrain band first, so its trees stand on it and it hides
            // the feet of the layer behind.
            var bandColor = Shade(Color.Lerp(Grass, Fog, layer.FogAmount), layer.Z);
            int bandTop = (int)groundY;
            if (bandTop < screenH)
                sb.Draw(_pixel, new Rectangle(0, Math.Max(0, bandTop), screenW,
                        screenH - Math.Max(0, bandTop)), bandColor);

            // Scroll the baked strip at 1:1 pixels — no per-frame rescaling.
            var rt = _strips[li];
            const int SinkPx = 3; // bury the art's flat bottom edge in the band
            int srcX = (int)(camera.Position.X * Proj(layer.Z)) % rt.Width;
            if (srcX < 0) srcX += rt.Width;
            int destTop = (int)(groundY + SinkPx) - rt.Height;
            if (destTop < screenH && destTop + rt.Height > 0)
            {
                // Tile manually: segments of the strip laid end to end across the
                // screen (Wrap sampling is unavailable on NPOT textures in Reach).
                int destX = 0, sx = srcX;
                while (destX < screenW)
                {
                    int take = Math.Min(screenW - destX, rt.Width - sx);
                    sb.Draw(rt, new Rectangle(destX, destTop, take, rt.Height),
                            new Rectangle(sx, 0, take, rt.Height), Color.White);
                    destX += take;
                    sx = 0;
                }
            }

            // Rays slotted in front of this plane (additive pass needs its own batch).
            if (HasRays(li))
            {
                sb.End();
                DrawRays(sb, li, camera.Position.X, screenW, screenH);
                sb.Begin(samplerState: SamplerState.LinearClamp);
            }
        }

        // Underground: dirt just below the nearest (last) layer's ground line, deep
        // rock further down — riding the same plane as the nearest trees.
        float nearGroundY = groundYs[_layers.Length - 1];
        int dirtTop = (int)(nearGroundY + 34f);
        int rockTop = (int)(nearGroundY + 260f);
        if (dirtTop < screenH)
            sb.Draw(_pixel, new Rectangle(0, Math.Max(0, dirtTop), screenW,
                    screenH - Math.Max(0, dirtTop)), Dirt);
        if (rockTop < screenH)
            sb.Draw(_pixel, new Rectangle(0, Math.Max(0, rockTop), screenW,
                    screenH - Math.Max(0, rockTop)), DeepRock);

        sb.End();
    }

    public void Dispose()
    {
        _wedge?.Dispose();
        _wedge = null;
        if (_strips == null) return;
        foreach (var rt in _strips) rt?.Dispose();
        _strips = null;
    }

    // Small integer mixer (xorshift-style); stable across runs and platforms.
    private static uint Hash(int seed, int i)
    {
        uint h = (uint)(seed * 374761393 + i * 668265263);
        h = (h ^ (h >> 13)) * 1274126177u;
        return h ^ (h >> 16);
    }

    // Salted [0,1) derived from a tree's hash — one independent value per property.
    private static float Rand01(uint h, int salt)
    {
        uint v = (h ^ (uint)(salt * 2654435761)) * 2246822519u;
        v ^= v >> 15;
        return (v & 0xFFFFFF) / (float)0x1000000;
    }
}
