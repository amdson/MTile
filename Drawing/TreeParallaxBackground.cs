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
    // Tree art variants. Enum values are indices into _trees, so the order here
    // must match Game1's loading order: tree.png, redwood_tree.png,
    // deadwood_tree.png, trees_all_layers.png. Out-of-range values clamp.
    public enum TreeType
    {
        Pine      = 0, // tree.png — the original spruce/pine
        Broadleaf = 1, // redwood_tree.png — round deciduous canopy
        Snag      = 2, // deadwood_tree.png — bare dead trunk
        Conifer   = 3, // trees_all_layers.png — tall red-trunk conifer
    }

    private readonly GraphicsDevice _device;
    private readonly Texture2D[] _trees; // indexed by TreeType
    private readonly Texture2D _pixel;

    public Color SkyTop     = new Color(120, 168, 214);
    public Color SkyHorizon = new Color(196, 214, 226);
    // Distant tint trees fade toward (atmospheric perspective).
    public Color Fog        = new Color(186, 204, 218);
    // Vertical tilt of the bake-time fog: added to a layer's FogAmount linearly
    // with height, 0 at the strip bottom (ground line) up to +FogSlope at the top,
    // so treetops fade into the sky first. 0 = uniform fog (current look).
    public float FogSlope   = 0.3f;
    public Color Grass      = new Color(74, 110, 58);
    public Color Dirt       = new Color(72, 54, 40);
    public Color DeepRock   = new Color(34, 26, 22);

    public float Z_scale = 1.0f; 


    // Perspective model: pinhole camera, depth measured in focal lengths. A plane
    // at depth Z projects with factor s = 1/(1+Z) (similar triangles) — and that
    // ONE factor is simultaneously the plane's parallax rate, its size scale, and
    // how far its ground line has converged toward the vanishing horizon. Z = 0
    // would be the play plane (s = 1); Z → ∞ pins to the horizon (s → 0).
    private static float Proj(float z) => 1f / (1f + z);

    // Screen fraction of the VANISHING horizon (eye level) — the line all ground
    // planes converge to as Z → ∞.
    public float HorizonFrac = 0.575f;
    // World px the ground plane sits below eye level; each layer's ground line is
    // horizonBase + GroundDrop * Proj(Z), so nearer planes sit proportionally lower.
    public float GroundDrop = 184f;
    // Vertical parallax as a fraction of FULL perspective consistency. The world
    // pass shifts everything by -camY * Zoom screen px, so a consistent plane at
    // depth Z moves at -camY * Zoom * Proj(Z): at 1 a Z=0 plane tracks the real
    // terrain 1:1 (the zoom factor is applied at draw time from the live camera).
    // <1 deliberately damps the skyline's vertical pumping; 0 pins it.
    public float ParallaxY = 1f;
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
        public TreeType Tree;    // which tree art this layer uses
        public float Z;          // depth in focal lengths; parallax + scale = Proj(Z)
        public float TreeHeight; // base tree height in WORLD px (play-plane scale)
        // public float MaxTreeHeight; 
        public float Spacing;    // slot spacing in WORLD px; on screen it's Spacing*Proj(Z)
        public int SlotCount;    // slots per baked strip; period = Spacing*Proj(Z)*SlotCount
        public int SkipPct;      // % of slots left empty (sparseness)
        public float FogAmount;  // 0 = near/no fog, 1 = fully fog-colored (art choice, not derived)
        public int Seed;
    }

    // Back to front (descending Z). Everything geometric — parallax, tree size,
    // spacing, ground-line height — derives from Z via Proj; only fog stays as an
    // art-directed knob. Z values converted from the previously tuned parallax
    // factors (Z = 1/Px - 1), so the composition is unchanged.
    // Tree indices: 0 = pine, 1 = broadleaf (redwood_tree.png), 2 = deadwood snag,
    // 3 = red-trunk conifer (trees_all_layers.png).
    //
    // Organized as depth BANDS, each holding several species at small Z offsets so
    // every distance shows a mixed forest. Within a band the layers use wider
    // spacing + higher skip than a solo layer would, so the combined density stays
    // in the same range while the species interleave.
    private readonly Layer[] _layers =
    {
        // -- Far horizon (Z ~ 5.0-4.2): giant silhouettes against the sky --------
        new Layer { Tree = TreeType.Pine, Z = 14.0f, TreeHeight = 19200f, Spacing = 300f, SlotCount = 48, SkipPct = 85, FogAmount = 0f, Seed = 11 },
    
        new Layer { Tree = TreeType.Pine, Z = 9.0f, TreeHeight = 8200f, Spacing = 300f, SlotCount = 48, SkipPct = 85, FogAmount = 0f, Seed = 101 },
        new Layer { Tree = TreeType.Pine, Z = 9.1f, TreeHeight = 2200f, Spacing = 300f, SlotCount = 48, SkipPct = 25, FogAmount = 0f, Seed = 102 },
        new Layer { Tree = TreeType.Conifer, Z = 9.2f, TreeHeight = 2200f, Spacing = 300f, SlotCount = 48, SkipPct = 25, FogAmount = 0f, Seed = 103 },

        // new Layer { Tree = TreeType.Conifer, Z = 4.7f, TreeHeight = 3600f, Spacing = 500f, SlotCount = 48, SkipPct = 55, GiantPct = 35, FogAmount = 0.71f, Seed = 131 },
        // new Layer { Tree = TreeType.Broadleaf, Z = 4.4f, TreeHeight = 3700f, Spacing = 500f, SlotCount = 40, SkipPct = 60, GiantPct = 35,  FogAmount = 0.70f, Seed = 149 },
        // -- Deep forest (Z ~ 3.6-2.8) -------------------------------------------

        new Layer { Tree = TreeType.Pine, Z = 6.6f, TreeHeight = 3800f, Spacing = 300f, SlotCount = 32, SkipPct = 60, FogAmount = 0f, Seed = 242 },
        // new Layer { Tree = TreeType.Conifer, Z = 3.4f, TreeHeight = 1950f, Spacing = 340f, SlotCount = 32, SkipPct = 45, FogAmount = 0f, Seed = 259 },
        // new Layer { Tree = TreeType.Broadleaf, Z = 3.1f, TreeHeight = 2800f, Spacing = 380f, SlotCount = 32, SkipPct = 55, FogAmount = 0f, Seed = 202 },
        new Layer { Tree = TreeType.Snag, Z = 6.9f, TreeHeight = 3400f, Spacing = 900f, SlotCount = 12, SkipPct = 70, FogAmount = 0f, Seed = 218 },


        new Layer { Tree = TreeType.Pine, Z = 4.6f, TreeHeight = 3300f, Spacing = 300f, SlotCount = 32, SkipPct = 60, FogAmount = 0f, Seed = 242 },
        new Layer { Tree = TreeType.Conifer, Z = 4.4f, TreeHeight = 2950f, Spacing = 340f, SlotCount = 32, SkipPct = 65, FogAmount = 0f, Seed = 259 },
        new Layer { Tree = TreeType.Broadleaf, Z = 4.1f, TreeHeight = 2800f, Spacing = 380f, SlotCount = 32, SkipPct = 65, FogAmount = 0f, Seed = 202 },
        new Layer { Tree = TreeType.Snag, Z = 3.9f, TreeHeight = 2800f, Spacing = 900f, SlotCount = 12, SkipPct = 70, FogAmount = 0f, Seed = 218 },

        // -- Mid-ground (Z ~ 2.4-1.6) --------------------------------------------
        new Layer { Tree = TreeType.Conifer, Z = 2.4f, TreeHeight = 2250f, Spacing = 260f, SlotCount = 48, SkipPct = 45, FogAmount = 0.0f, Seed = 303 },
        new Layer { Tree = TreeType.Pine, Z = 2.2f, TreeHeight = 2300f, Spacing = 280f, SlotCount = 48, SkipPct = 50, FogAmount = 0.0f, Seed = 317 },
        new Layer { Tree = TreeType.Broadleaf, Z = 2.0f, TreeHeight = 2400f, Spacing = 300f, SlotCount = 32, SkipPct = 60, FogAmount = 0.0f, Seed = 331 },
        new Layer { Tree = TreeType.Snag, Z = 1.9f, TreeHeight = 2500f, Spacing = 700f, SlotCount = 16, SkipPct = 70, FogAmount = 0.0f, Seed = 271 },
        new Layer { Tree = TreeType.Conifer, Z = 1.7f, TreeHeight = 1150f, Spacing = 270f, SlotCount = 48, SkipPct = 55, FogAmount = 0.0f, Seed = 383 },
        
        // // -- Near (Z ~ 1.2-0.6) --------------------------------------------------
        new Layer { Tree = TreeType.Conifer, Z = 1.2f, TreeHeight = 1850f, Spacing = 320f, SlotCount = 32, SkipPct = 55, FogAmount = 0.0f, Seed = 359 },
        new Layer { Tree = TreeType.Broadleaf, Z = 0.9f, TreeHeight = 1750f, Spacing = 360f, SlotCount = 32, SkipPct = 60, FogAmount = 0.0f, Seed = 386 },
        new Layer { Tree = TreeType.Pine, Z = 0.7f, TreeHeight = 1700f, Spacing = 340f, SlotCount = 32, SkipPct = 60, FogAmount = 0.0f, Seed = 397 },
        
        // // -- Front (Z ~ 0.45-0.25): the planes framing the play space ------------
        new Layer { Tree = TreeType.Snag, Z = 0.45f, TreeHeight = 900f, Spacing = 400f, SlotCount = 24, SkipPct = 60, FogAmount = 0.0f, Seed = 411 },
        new Layer { Tree = TreeType.Conifer, Z = 0.35f, TreeHeight = 1300f, Spacing = 160f, SlotCount = 48, SkipPct = 65, FogAmount = 0.0f, Seed = 404 },
        new Layer { Tree = TreeType.Pine, Z = 0.25f, TreeHeight =  1200f, Spacing = 300f, SlotCount = 32, SkipPct = 70, FogAmount = 0.0f, Seed = 423 },
        new Layer { Tree = TreeType.Pine, Z = 0.0f, TreeHeight =  1200f, Spacing = 300f, SlotCount = 32, SkipPct = 90, FogAmount = 0.0f, Seed = 423 },

    };

    // public BuildLayers

    private RenderTarget2D[] _strips;
    private int _builtScreenH; // rebuild trigger — strips are sized off the screen height

    public TreeParallaxBackground(GraphicsDevice device, Texture2D[] trees, Texture2D pixel)
    {
        _device = device;
        _trees = trees;
        _pixel = pixel;
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
    // True fog lerp, landing only where the target has content:
    // out = fogColor*f*dstA + dst*(1-f). Unlike a sprite tint (which multiplies and
    // can never brighten), this LIFTS dark texels toward the fog color — outlines
    // and deep greens actually fade with distance. Used on the baked strips.
    private static readonly BlendState FogLerpBlend = new BlendState
    {
        ColorSourceBlend      = Blend.DestinationAlpha,
        ColorDestinationBlend = Blend.InverseSourceAlpha,
        AlphaSourceBlend      = Blend.Zero,
        AlphaDestinationBlend = Blend.One,
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
        new GodRay { AnchorX =  150f, Z = 1.4f, Width = 340f, Gradations = 4, Alpha = 0.9f,  AfterLayer = 6 },
        new GodRay { AnchorX =  700f, Z = 1.3f, Width = 2250f, Gradations = 4, Alpha = 0.90f, AfterLayer = 6 },
        new GodRay { AnchorX = 1250f, Z =  1.3f, Width = 1600f, Gradations = 3, Alpha = 0.95f, AfterLayer = 6 },
        new GodRay { AnchorX =  450f, Z =  3.3f, Width = 1320f, Gradations = 5, Alpha = 0.98f, AfterLayer = 5 },
        new GodRay { AnchorX = 1600f, Z =  3.3f, Width =  630f, Gradations = 4, Alpha = 0.90f, AfterLayer = 4 },
        new GodRay { AnchorX = 1000f, Z =  3.3f, Width =  930f, Gradations = 5, Alpha = 0.95f, AfterLayer = 4 },
        new GodRay { AnchorX = 1000f, Z =  0.3f, Width =  930f, Gradations = 5, Alpha = 0.95f, AfterLayer = 4 },
    };

    // ---- Sunrise mode (Style = SunStyle.Sunrise) --------------------------------
    // Instead of slanted shafts from off-screen, a visible sun sits ON the vanishing
    // horizon and rays radiate outward from its center by angle. The disc and the
    // sunrise rays slot into the depth order by their own Z (always Z-based, even
    // with SortByZ off) and reuse the same multiply+screen light passes.
    public enum SunStyle { Slanted, Sunrise }
    public SunStyle Style = SunStyle.Slanted;

    // Sunrise ray: pinned to the sun's center, aimed by angle. 0° = right,
    // -90° = straight up (screen Y-down), ±180° = left.
    public struct SunriseRay
    {
        public float AngleDeg;  // direction the ray radiates from the sun center
        public float Z;         // depth: draw slot + width scale, like GodRay.Z
        public float Width;     // outer shaft width in WORLD px at the far end
        public int Gradations;  // stacked brightness steps
        public float Alpha;     // total core brightness (summed over gradations)
    }

    // Sun placement: screen-x fraction, center vertically ON the vanishing horizon
    // (plus SunLift px, +down) — a half-risen sun wants a small positive lift.
    public float SunriseXFrac = 0.5f;
    public float SunLift = 20f;
    // Depth of the sun itself: far ⇒ behind every tree plane, near-zero parallax.
    public float SunriseZ = 40f;
    // Radial-gradient disc: a tight bright core inside a wide soft halo, both
    // screen-blended so they stay luminous over the sky.
    public float SunCoreRadius = 70f;   // screen px
    public float SunHaloRadius = 260f;
    public Color SunCoreColor = new Color(255, 240, 200);
    public Color SunHaloColor = new Color(255, 176, 96);
    public float SunCoreAlpha = 0.95f;
    public float SunHaloAlpha = 0.55f;

    // Glare bleed: the sun's LIGHT punches through the canopy while the trees stay
    // fully opaque to each other — after each tree plane in front of the sun, a
    // shrunken glare copy is screen-blended over it (screen can only brighten, so
    // it adds radiance without ever revealing the scene behind). Per plane crossed
    // the skirt shrinks faster than the core: deep canopy narrows the burn-through
    // hole, but the hot center survives many layers — brightness, not translucency.
    // SunBleed is the overall fraction that penetrates at all (0 = hard occlusion);
    // SunBleedTransmit is the per-plane transmittance (~0.5-0.7).
    public float SunBleed         = 0.85f;
    public float SunBleedTransmit = 0.85f;
    private const float MinBleed  = 0.02f; // below this a copy isn't worth a batch

    // The radiating fan, mostly skyward with a couple of low grazers. Angles are
    // fixed (the sun is the pivot); widths/alphas alternate so it reads hand-drawn.
    private readonly SunriseRay[] _sunriseRays =
    {
        new SunriseRay { AngleDeg = -165f, Z = 4.5f, Width = 1900f, Gradations = 4, Alpha = 0.60f },
        new SunriseRay { AngleDeg = -140f, Z = 4f, Width = 3200f, Gradations = 5, Alpha = 0.80f },
        new SunriseRay { AngleDeg = -118f, Z = 2f, Width = 1600f, Gradations = 3, Alpha = 0.55f },
        new SunriseRay { AngleDeg =  -97f, Z = 2.5f, Width = 2800f, Gradations = 5, Alpha = 0.75f },
        new SunriseRay { AngleDeg =  -74f, Z = 5f, Width = 1500f, Gradations = 3, Alpha = 0.55f },
        new SunriseRay { AngleDeg =  -52f, Z = 5.3f, Width = 3000f, Gradations = 5, Alpha = 0.80f },
        new SunriseRay { AngleDeg =  -28f, Z = 4.1f, Width = 1700f, Gradations = 4, Alpha = 0.60f },
        new SunriseRay { AngleDeg =  -12f, Z =  3.3f, Width = 2200f, Gradations = 4, Alpha = 0.65f },
        new SunriseRay { AngleDeg = -170f, Z =  3.6f, Width = 2000f, Gradations = 4, Alpha = 0.65f },
    };
    // -----------------------------------------------------------------------------

    // Draw-order mode. When true, layers render sorted by descending Z (table order
    // no longer matters) and each ray interleaves by its OWN Z — in front of every
    // plane deeper than it, behind every nearer one — with AfterLayer ignored.
    // When false, table order rules and AfterLayer picks ray slots by table index.
    public bool SortByZ = true;

    // Layer indices in draw order; built lazily (layer tables are fixed at runtime).
    private int[] _order;

    private int[] BuildOrder()
    {
        var order = new int[_layers.Length];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        if (SortByZ)
            for (int i = 1; i < order.Length; i++)  // stable insertion sort, desc Z
                for (int j = i; j > 0 && _layers[order[j]].Z > _layers[order[j - 1]].Z; j--)
                    (order[j], order[j - 1]) = (order[j - 1], order[j]);
        return order;
    }

    // Draw-order slot for a light element at depth z: after the last plane at least
    // as deep (ties: plane occludes light); -1 = behind all planes.
    private int SlotOfZ(float z)
    {
        int slot = -1;
        for (int k = 0; k < _order.Length && _layers[_order[k]].Z >= z; k++) slot = k;
        return slot;
    }

    // The draw-order slot a slanted ray renders after. In table mode, AfterLayer is
    // clamped so values past the end mean "in front of everything" instead of
    // silently never matching a slot.
    private int RaySlot(in GodRay r) =>
        SortByZ ? SlotOfZ(r.Z) : Math.Min(r.AfterLayer, _layers.Length - 1);

    // Does any light element (per the active Style) draw in this slot?
    private bool HasLight(int slot)
    {
        if (Style == SunStyle.Slanted)
        {
            foreach (var r in _rays) if (RaySlot(r) == slot) return true;
            return false;
        }
        int sunSlot = SlotOfZ(SunriseZ);
        if (sunSlot == slot) return true;
        // Slots in front of the sun still glow while the glare bleed carries.
        if (slot > sunSlot && SunBleed * MathF.Pow(SunBleedTransmit, slot - sunSlot) >= MinBleed)
            return true;
        foreach (var r in _sunriseRays) if (SlotOfZ(r.Z) == slot) return true;
        return false;
    }

    // One light pass for the given slot, dispatched by Style.
    private void DrawLight(SpriteBatch sb, int slot, float camX, int screenW, int screenH, float horizonBase)
    {
        if (Style == SunStyle.Slanted) DrawRays(sb, slot, camX, screenW, screenH);
        else DrawSunrise(sb, slot, camX, screenW, screenH, horizonBase);
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

        // Per-ray geometry, cached once for both passes. Anchors wrap every period
        // of scroll-space, but visibility is decided by the SHAFT's on-screen
        // x-span, not the anchor: a shaft anchored well off-screen bottom-left
        // (for an upper-right sun) still slants across the view, so wrapping on
        // the anchor alone pops rays out while their body is visible. Each ray
        // tests its wrapped copy and the neighbors one period to each side, and
        // draws every copy that crosses the screen (near the seam that can be two).
        Span<(int ray, float rot, float len)> vis =
            stackalloc (int, float, float)[_rays.Length * 3];
        int n = 0;
        for (int i = 0; i < _rays.Length; i++)
        {
            ref readonly var ray = ref _rays[i];
            if (RaySlot(ray) != afterLayer) continue;

            // Parallax-scrolled anchor (rate = Proj(Z), same as a tree plane at
            // this depth), wrapped into [0, period).
            float ax0 = ray.AnchorX - camX * Proj(ray.Z);
            ax0 = ((ax0 % period) + period) % period;
            // Conservative half-width: the filter pass draws the full cone width.
            float halfW = ray.Width * Proj(ray.Z) * 1.3f * 0.5f;

            for (int k = -1; k <= 1; k++)
            {
                float ax = ax0 + k * period;
                // The trig: aim from the off-screen sun at the anchor point. Every
                // ray shares the same origin, so the fan converges on the sun.
                var d = new Vector2(ax, screenH) - sun;
                if (d.Y <= 0f) continue; // sun at/below the anchor row: degenerate
                // Shaft x where it enters the top of the screen; on-screen the
                // shaft spans between that and the anchor x (sun sits above y=0).
                float xTop = sun.X - d.X * sun.Y / d.Y;
                float lo = MathF.Min(xTop, ax) - halfW;
                float hi = MathF.Max(xTop, ax) + halfW;
                if (hi < 0f || lo > screenW) continue;
                vis[n++] = (i, MathF.Atan2(d.Y, d.X),
                            d.Length() * 1.3f); // overshoot past the anchor
            }
        }

        // Pass 1 — transmission: multiply the shaft interior toward RayTransmission,
        // full width only (gradations belong to the glow, not the filter).
        if (RayFilter > 0f)
        {
            var filter = Color.Lerp(Color.White, RayTransmission, RayFilter);
            sb.Begin(blendState: MultiplyBlend);
            for (int j = 0; j < n; j++)
            {
                ref readonly var ray = ref _rays[vis[j].ray];
                sb.Draw(_wedge, sun, null, filter, vis[j].rot,
                        new Vector2(0f, WedgeH / 2f),
                        new Vector2(vis[j].len / WedgeW, ray.Width * Proj(ray.Z) * 1.3f / WedgeH),
                        SpriteEffects.None, 0f);
            }
            sb.End();
        }

        // Pass 2 — glow: screen-blend the gradation stack.
        sb.Begin(blendState: ScreenBlend);
        for (int j = 0; j < n; j++)
        {
            ref readonly var ray = ref _rays[vis[j].ray];
            float rs = Proj(ray.Z);

            for (int g = 0; g < ray.Gradations; g++)
            {
                // Inner gradations narrow only subtly (GradationTaper), not to zero.
                float w = ray.Width * rs * (1f - GradationTaper * g / (float)ray.Gradations);
                float a = ray.Alpha / ray.Gradations;
                // The wedge's apex sits at the sun; Width is the cone's width at the
                // anchor distance (len overshoots by 1.3x, so scale w to match).
                sb.Draw(_wedge, sun, null, RayColor * a, vis[j].rot,
                        new Vector2(0f, WedgeH / 2f),
                        new Vector2(vis[j].len / WedgeW, w * 1.3f / WedgeH), SpriteEffects.None, 0f);
            }
        }
        sb.End();
    }

    // Sunrise: sun disc + rays radiating from its center by angle. Same two light
    // passes as DrawRays (multiply transmission, then screen glow); the disc draws
    // in its own Z slot, before any rays sharing that slot.
    private void DrawSunrise(SpriteBatch sb, int slot, float camX, int screenW, int screenH, float horizonBase)
    {
        _wedge ??= BuildWedge();
        _disc ??= BuildDisc();
        // The sun sits on the vanishing horizon (which doesn't parallax) with only
        // the tiny drift its great depth allows.
        var sun = new Vector2(SunriseXFrac * screenW - camX * Proj(SunriseZ),
                              horizonBase + SunLift);
        float len = screenW + screenH; // crosses the screen from any sun position
        var discCenter = new Vector2(DiscSize / 2f, DiscSize / 2f);

        // Glare bleed carried into this slot: energy left after crossing the tree
        // planes between the sun and here (0 when this IS the sun's slot).
        int sunSlot = SlotOfZ(SunriseZ);
        float bleed = slot > sunSlot
            ? SunBleed * MathF.Pow(SunBleedTransmit, slot - sunSlot) : 0f;
        if (bleed < MinBleed) bleed = 0f;
        // Skirt shrinks with the remaining energy; the hot core narrows much
        // slower and keeps most of its brightness — the glare reads as intensity
        // (overexposed foliage) rather than translucency (a ghost disc).
        // Radius ~ sqrt-ish of remaining energy (area tracks energy), so the skirt
        // tightens noticeably per plane while staying visible through several.
        float skirtR = SunHaloRadius * MathF.Pow(bleed, 0.7f);
        float coreR  = SunCoreRadius * MathF.Sqrt(bleed);

        // Transmission pass: this slot's rays, plus the glare veil — multiply the
        // canopy around the sun toward the warm transmission color so its local
        // contrast collapses into the light (the "glare eats the tree" half; the
        // screen pass below adds the radiance itself).
        if (RayFilter > 0f)
        {
            var filter = Color.Lerp(Color.White, RayTransmission, RayFilter);
            sb.Begin(blendState: MultiplyBlend);
            if (bleed > 0f)
                sb.Draw(_disc, sun, null,
                        Color.Lerp(Color.White, RayTransmission, MathF.Min(1f, bleed)), 0f,
                        discCenter, skirtR * 2f / DiscSize, SpriteEffects.None, 0f);
            foreach (var ray in _sunriseRays)
            {
                if (SlotOfZ(ray.Z) != slot) continue;
                sb.Draw(_wedge, sun, null, filter, MathHelper.ToRadians(ray.AngleDeg),
                        new Vector2(0f, WedgeH / 2f),
                        new Vector2(len / WedgeW, ray.Width * Proj(ray.Z) / WedgeH),
                        SpriteEffects.None, 0f);
            }
            sb.End();
        }

        // Glow pass: disc first (so rays lay over it), then the gradation stacks.
        sb.Begin(blendState: ScreenBlend);
        if (sunSlot == slot)
        {
            sb.Draw(_disc, sun, null, SunHaloColor * SunHaloAlpha, 0f,
                    discCenter, SunHaloRadius * 2f / DiscSize, SpriteEffects.None, 0f);
            sb.Draw(_disc, sun, null, SunCoreColor * SunCoreAlpha, 0f,
                    discCenter, SunCoreRadius * 2f / DiscSize, SpriteEffects.None, 0f);
        }
        else if (bleed > 0f)
        {
            sb.Draw(_disc, sun, null, SunHaloColor * (SunHaloAlpha * bleed), 0f,
                    discCenter, skirtR * 2f / DiscSize, SpriteEffects.None, 0f);
            sb.Draw(_disc, sun, null, SunCoreColor * (SunCoreAlpha * MathF.Pow(bleed, 0.3f)), 0f,
                    discCenter, coreR * 2f / DiscSize, SpriteEffects.None, 0f);
        }
        foreach (var ray in _sunriseRays)
        {
            if (SlotOfZ(ray.Z) != slot) continue;
            float rs = Proj(ray.Z);
            for (int g = 0; g < ray.Gradations; g++)
            {
                float w = ray.Width * rs * (1f - GradationTaper * g / (float)ray.Gradations);
                float a = ray.Alpha / ray.Gradations;
                sb.Draw(_wedge, sun, null, RayColor * a, MathHelper.ToRadians(ray.AngleDeg),
                        new Vector2(0f, WedgeH / 2f),
                        new Vector2(len / WedgeW, w / WedgeH), SpriteEffects.None, 0f);
            }
        }
        sb.End();
    }

    // ---- Mist planes ------------------------------------------------------------
    // Fog with structure: translucent noise bands hanging IN the depth stack as
    // their own planes. Each one softens everything behind it (trees, bands, sky)
    // and leaves nearer planes crisp, scrolling at its own Proj(Z) parallax. This is
    // the spatial component of fog; the uniform contrast collapse on the tree
    // strips themselves (FogLerpBlend at bake) sits underneath it.
    public struct MistPlane
    {
        public float Z;        // depth: draw slot + parallax, like everything else
        public float Height;   // band height in WORLD px above its ground line
        public float Scale;    // world px per noise repeat (bigger = broader wisps)
        public float Strength; // peak opacity of the band
    }

    private readonly MistPlane[] _mists =
    {
        new MistPlane { Z = 3.8f,  Height = 2400f, Scale = 5200f, Strength = 0.01f },
        // new MistPlane { Z = 2.6f,  Height = 1700f, Scale = 4400f, Strength = 0.40f },
        // new MistPlane { Z = 1.5f,  Height = 1100f, Scale = 3600f, Strength = 0.30f },
        // new MistPlane { Z = 0.55f, Height =  700f, Scale = 1000f, Strength = 0.20f },
    };

    // Horizontally-seamless value noise (4 octaves, lattice wraps in x), with a
    // vertical envelope: transparent at the band top, densest at the ground line —
    // mist pools low. Baked once; premultiplied white, tinted Fog at draw.
    private const int MistW = 512, MistH = 256;
    private Texture2D _mist;

    private static float Smooth(float t) => t * t * (3f - 2f * t);

    private Texture2D BuildMistTexture()
    {
        var tex = new Texture2D(_device, MistW, MistH);
        var data = new Color[MistW * MistH];
        var noise = new float[MistW * MistH];
        var rng = new Random(9157);
        float total = 0f;
        for (int o = 0; o < 4; o++)
        {
            int gw = 6 << o, gh = 3 << o;
            // Flat-ish falloff: octave sums of noise concentrate hard around the
            // mean, which reads as a uniform sheet; keeping the high octaves strong
            // preserves granular variation for the threshold below to bite on.
            float amp = 1f / (1f + o);
            total += amp;
            var lat = new float[gw * (gh + 1)];
            for (int i = 0; i < lat.Length; i++) lat[i] = (float)rng.NextDouble();
            for (int y = 0; y < MistH; y++)
            {
                float fy = y / (float)MistH * gh;
                int gy = (int)fy;
                float ty = Smooth(fy - gy);
                for (int x = 0; x < MistW; x++)
                {
                    float fx = x / (float)MistW * gw;
                    int gx = (int)fx;
                    float tx = Smooth(fx - gx);
                    int gx1 = (gx + 1) % gw; // x wrap = seamless horizontal tiling
                    float a = lat[gy * gw + gx], b = lat[gy * gw + gx1];
                    float c = lat[(gy + 1) * gw + gx], d = lat[(gy + 1) * gw + gx1];
                    noise[y * MistW + x] += amp *
                        MathHelper.Lerp(MathHelper.Lerp(a, b, tx), MathHelper.Lerp(c, d, tx), ty);
                }
            }
        }
        for (int y = 0; y < MistH; y++)
        {
            float env = Smooth(y / (float)(MistH - 1));
            for (int x = 0; x < MistW; x++)
            {
                // Steep threshold around the mean: only noise PEAKS survive, so the
                // band becomes distinct cloud patches with true holes between them
                // instead of a translucent film (octave-summed noise clusters near
                // 0.5 — a gentle ramp maps it all to similar alpha).
                float n = noise[y * MistW + x] / total;
                float alpha = Math.Clamp((n - 0.52f) * 5f, 0f, 1f) * env;
                data[y * MistW + x] = Color.White * alpha; // premultiplied
            }
        }
        tex.SetData(data);
        return tex;
    }

    // Draws the mist planes assigned to this depth slot. Runs inside the main
    // alpha-blend batch (the texture is premultiplied); manual tiling like the
    // strips, since Reach forbids Wrap on NPOT textures.
    private void DrawMistSlot(SpriteBatch sb, int slot, float camX, float camYTrack,
                              int screenW, int screenH, float horizonBase)
    {
        foreach (var m in _mists)
        {
            if (SlotOfZ(m.Z) != slot) continue;
            float s = Proj(m.Z);
            float groundY = horizonBase + (GroundDrop - camYTrack) * s;
            int bandH = Math.Max(1, (int)(m.Height * s));
            int top = (int)groundY - bandH;
            if (top >= screenH || top + bandH <= 0) continue;

            float tileW = MathF.Max(64f, m.Scale * s);
            float offset = (camX * s) % tileW;
            if (offset < 0) offset += tileW;
            // Brighter than Fog on purpose: the scene behind is already lerped
            // toward Fog (layer FogAmount), so Fog-colored mist has no contrast
            // against it — push toward white so the patches actually read.
            var tint = Color.Lerp(Fog, Color.White, 0.75f) * m.Strength;
            int w = (int)MathF.Ceiling(tileW);
            for (float x = -offset; x < screenW; x += tileW)
                sb.Draw(_mist, new Rectangle((int)x, top, w + 1, bandH), tint);
        }
    }
    // -----------------------------------------------------------------------------

    // A horizontal wedge: apex at the left edge (the sun), linearly widening to the
    // full texture height at the right, with a ~1.5px antialiased edge. Drawn scaled
    // per gradation, so every ray is a true cone radiating from the sun point.
    private const int WedgeW = 256, WedgeH = 128;
    private Texture2D _wedge;

    // Radial-gradient disc for the sun: alpha (1-r)^2 from center to rim — a soft
    // falloff that reads as glow rather than a hard circle. Drawn twice (halo+core).
    private const int DiscSize = 256;
    private Texture2D _disc;

    private Texture2D BuildDisc()
    {
        var tex = new Texture2D(_device, DiscSize, DiscSize);
        var data = new Color[DiscSize * DiscSize];
        float half = DiscSize / 2f;
        for (int y = 0; y < DiscSize; y++)
            for (int x = 0; x < DiscSize; x++)
            {
                float dx = (x + 0.5f - half) / half, dy = (y + 0.5f - half) / half;
                float a = Math.Clamp(1f - MathF.Sqrt(dx * dx + dy * dy), 0f, 1f);
                data[y * DiscSize + x] = Color.White * (a * a); // premultiplied
            }
        tex.SetData(data);
        return tex;
    }

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

    // Largest height multiplier a tree can get — must cover the top of the
    // height-jitter range in the slot loop (currently 0.7 + 0.10).
    private static float MaxScale(in Layer layer) => 0.8f;

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
            var tex = _trees[Math.Clamp((int)layer.Tree, 0, _trees.Length - 1)];
            float texAspect = tex.Width / (float)tex.Height;
            // Project world-space sizes to screen px at this plane's depth.
            float s = Proj(layer.Z*Z_scale);
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

            // Depth shade only — fog is no longer part of the sprite tint (a tint
            // multiplies, so it can't lift dark texels); it's applied as a true
            // lerp by the FogLerpBlend overlay after the trees are drawn.
            var tint = Shade(Color.White, layer.Z*Z_scale);
            // One seeded stream per layer, consumed at bake time only. Every slot
            // draws the SAME number of values — skipped slots included — so a
            // tree's look never depends on how many slots were skipped before it.
            var rng = new Random(layer.Seed);
            for (int i = 0; i < slots; i++)
            {
                bool skip     = rng.Next(100) < layer.SkipPct;
                float jitterT = (float)rng.NextDouble();
                float heightT = (float)rng.NextDouble();
                float aspectT = (float)rng.NextDouble();
                bool flip     = rng.Next(2) == 0;
                if (skip) continue;
                
                float jitter = (jitterT - 0.5f) * spacing * 0.8f;
                float x = i * spacing + jitter;

                float height = baseH * (0.7f + heightT * 0.10f);

                float aspect = 0.9f + aspectT * 0.2f;
                float width = height * texAspect * aspect;
                var fx = flip ? SpriteEffects.FlipHorizontally : SpriteEffects.None;

                // Bottom-aligned to the strip's bottom edge; the ground-line sink is
                // applied when the strip is placed. A tree straddling the strip edge
                // is drawn again shifted by one period so the wrap seam is sd eamless.
                var dest = new Rectangle((int)(x - width / 2f), (int) (stripH - height),
                                         (int)width, (int)height);
                sb.Draw(tex, dest, null, tint, 0f, Vector2.Zero, fx, 0f);
                if (dest.Left < 0)
                    sb.Draw(tex, new Rectangle(dest.X + stripW, dest.Y, dest.Width, dest.Height),
                            null, tint, 0f, Vector2.Zero, fx, 0f);
                if (dest.Right > stripW)
                    sb.Draw(tex, new Rectangle(dest.X - stripW, dest.Y, dest.Width, dest.Height),
                            null, tint, 0f, Vector2.Zero, fx, 0f);
            }

            sb.End();

            // Fog pass: lerp every baked tree pixel toward Fog by FogAmount —
            // lands only where the strip has alpha, and lifts darks properly.
            // FogSlope tilts the amount linearly with height: the strip bottom
            // (ground line) fogs at FogAmount, rising toward FogAmount + FogSlope
            // at the top, so treetops melt into the sky before the trunks do.
            if (layer.FogAmount > 0f || FogSlope > 0f)
            {
                sb.Begin(blendState: FogLerpBlend);
                if (FogSlope == 0f)
                {
                    sb.Draw(_pixel, new Rectangle(0, 0, stripW, stripH), Fog * layer.FogAmount);
                }
                else
                {
                    const int step = 2; // px per gradient band (bake-time only)
                    for (int y = 0; y < stripH; y += step)
                    {
                        float t = 1f - y / (float)stripH; // 1 at top, 0 at bottom
                        float f = Math.Clamp(layer.FogAmount + FogSlope * t, 0f, 1f);
                        sb.Draw(_pixel, new Rectangle(0, y, stripW, Math.Min(step, stripH - y)),
                                Fog * f);
                    }
                }
                sb.End();
            }
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
        // Vertical tracking term: the world pass moves at -camY * Zoom screen px,
        // so scaling the backdrop's camY by the live zoom makes ParallaxY = 1 mean
        // "a Z=0 plane rides the terrain exactly" instead of drifting at 1/Zoom.
        float camYTrack = camera.Position.Y * ParallaxY * camera.Zoom;
        Span<float> groundYs = stackalloc float[_layers.Length];
        float minGroundY = float.MaxValue;
        for (int li = 0; li < _layers.Length; li++)
        {
            groundYs[li] = horizonBase
                           + (GroundDrop - camYTrack) * Proj(_layers[li].Z);
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

        _order ??= BuildOrder();
        _mist ??= BuildMistTexture();

        // Mist, then light, behind every tree plane — rays shine over the mist.
        DrawMistSlot(sb, -1, camera.Position.X, camYTrack, screenW, screenH, horizonBase);
        if (HasLight(-1))
        {
            sb.End();
            DrawLight(sb, -1, camera.Position.X, screenW, screenH, horizonBase);
            sb.Begin(samplerState: SamplerState.LinearClamp);
        }

        for (int k = 0; k < _order.Length; k++)
        {
            int li = _order[k];
            ref readonly var layer = ref _layers[li];
            float groundY = groundYs[li];

            // This layer's terrain band first, so its trees stand on it and it hides
            // the feet of the layer behind.
            var bandColor = Shade(Color.Lerp(Grass, Fog, layer.FogAmount), layer.Z*Z_scale);
            int bandTop = (int)groundY;
            if (bandTop < screenH)
                sb.Draw(_pixel, new Rectangle(0, Math.Max(0, bandTop), screenW,
                        screenH - Math.Max(0, bandTop)), bandColor);

            // Scroll the baked strip at 1:1 pixels — no per-frame rescaling.
            var rt = _strips[li];
            const int SinkPx = 3; // bury the art's flat bottom edge in the band
            int srcX = (int)(camera.Position.X * Proj(layer.Z*Z_scale)) % rt.Width;
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

            // Mist, then light, slotted in front of this plane (light needs its
            // own batch; mist rides the main one).
            DrawMistSlot(sb, k, camera.Position.X, camYTrack, screenW, screenH, horizonBase);
            if (HasLight(k))
            {
                sb.End();
                DrawLight(sb, k, camera.Position.X, screenW, screenH, horizonBase);
                sb.Begin(samplerState: SamplerState.LinearClamp);
            }
        }

        // Underground: dirt just below the nearest (front-most) layer's ground line,
        // deep rock further down — riding the same plane as the nearest trees.
        float nearGroundY = groundYs[_order[_order.Length - 1]];
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
        _disc?.Dispose();
        _disc = null;
        _mist?.Dispose();
        _mist = null;
        if (_strips == null) return;
        foreach (var rt in _strips) rt?.Dispose();
        _strips = null;
    }

}
