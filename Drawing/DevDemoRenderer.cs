using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Dev previews for the in-progress rendering layers (RENDERING_UPGRADE_PLAN):
// PrimitiveBatch gradients/curves/surfaces, the DensityField glow, the segment
// metaball shaders, and the glow-trail effect. Each is gated by a GameConfig
// DebugDraw*Demo flag at the call site and anchored in world space. Self-contained
// — owns its own scratch state so none of it lives in Game1.
public sealed class DevDemoRenderer
{
    private readonly PrimitiveBatch            _prims;
    private readonly DensityField             _density;
    private readonly SkeletonMetaballRenderer _metaballs;
    private readonly GlowRenderer             _glow;

    public DevDemoRenderer(PrimitiveBatch prims, DensityField density,
                           SkeletonMetaballRenderer metaballs, GlowRenderer glow)
    {
        _prims     = prims;
        _density   = density;
        _metaballs = metaballs;
        _glow      = glow;
    }

    // Dev preview of the PrimitiveBatch layer: a gradient quad, a stroked cubic Bezier
    // with a width+color taper, and a parametric surface (wavy grid colored by uv). All
    // anchored at `anchor` in world space. Toggled by GameConfig.DebugDrawPrimitiveDemo.
    public void DrawPrimitiveDemo(Matrix transform, Vector2 anchor)
    {
        _prims.Begin(transform);

        // Gradient quad — four corner colors interpolate across the fill.
        var q = anchor + new Vector2(-90f, 0f);
        _prims.Quad(q, q + new Vector2(60f, 0f), q + new Vector2(60f, 50f), q + new Vector2(0f, 50f),
                    Color.Red, Color.Yellow, Color.Lime, Color.Cyan);

        // Stroked cubic Bezier, tapering white->magenta along its length.
        Primitives.StrokeBezier(_prims,
            anchor + new Vector2(-10f, 50f), anchor + new Vector2(20f, -40f),
            anchor + new Vector2(60f,  60f), anchor + new Vector2(95f,  0f),
            width: 6f, Color.White, Color.Magenta);

        // Parametric surface: a sine-warped grid, hue ramped across u and v.
        Vector2 sbase = anchor + new Vector2(20f, 0f);
        Primitives.Surface(_prims,
            (u, v) => sbase + new Vector2(u * 70f, v * 50f + MathF.Sin(u * MathF.PI * 2f) * 8f),
            (u, v) => new Color(u, v, 1f - u * v), ucount: 20, vcount: 14);

        _prims.End();
    }

    // Dev preview of the DensityField glow layer: a cluster of overlapping colored
    // kernels splatted into the field RT and composited additively, so the additive sum
    // (the "sum of kernels around particles") reads as merging soft glow. Toggled by
    // GameConfig.DebugDrawDensityDemo.
    public void DrawDensityDemo(Matrix transform, Vector2 anchor)
    {
        _density.Begin(transform);
        // A ring of colored blobs plus a bright core — overlaps merge additively.
        _density.Splat(anchor,                              34f, new Color(40, 120, 255));
        _density.Splat(anchor + new Vector2( 26f,  4f),     30f, new Color(255, 60, 160));
        _density.Splat(anchor + new Vector2(-24f,  8f),     28f, new Color(80, 255, 140));
        _density.Splat(anchor + new Vector2(  6f, -22f),    26f, new Color(255, 200, 40));
        _density.Splat(anchor + new Vector2( 40f, -16f),    18f, new Color(180, 120, 255));
        _density.End();
        _density.Composite();
    }

    // Dev preview of the segment-metaball shaders: a synthetic stick figure (torso, two
    // arms, two legs) built as bone segments and rendered as one merged gooey blob. Tests
    // the CapsuleSplat + MetaballComposite path before it's wired to the real skeleton.
    private readonly List<MetaballBone> _metaballDemoBones = new();
    public void DrawMetaballDemo(Matrix transform, Vector2 anchor)
    {
        var blob = new Color(120, 200, 255);
        Vector2 hip = anchor, neck = anchor + new Vector2(0f, -34f), head = neck + new Vector2(0f, -10f);
        _metaballDemoBones.Clear();
        _metaballDemoBones.Add(new MetaballBone(hip, neck, blob));                              // torso
        _metaballDemoBones.Add(new MetaballBone(neck, head, blob));                             // neck->head
        _metaballDemoBones.Add(new MetaballBone(neck, neck + new Vector2(-22f, 18f), blob));    // left arm
        _metaballDemoBones.Add(new MetaballBone(neck, neck + new Vector2( 22f, 18f), blob));    // right arm
        _metaballDemoBones.Add(new MetaballBone(hip,  hip  + new Vector2(-14f, 34f), blob));    // left leg
        _metaballDemoBones.Add(new MetaballBone(hip,  hip  + new Vector2( 14f, 34f), blob));    // right leg

        var style = MetaballStyle.Default;
        style.Radius = 18f;
        style.Iso    = 0.35f;
        style.Edge   = 0.05f;
        style.Inner  = new Color(120, 230, 255);
        style.Rim    = new Color(20, 70, 200);
        _metaballs.Render(transform, _metaballDemoBones, style);
    }

    // Dev preview of the glow effect: a glowing triangle riding a curved trail, built from
    // a synthetic Trail so the streak is visible without a live slash. GameConfig.DebugDrawGlowDemo.
    private Trail _glowDemoTrail;
    private float _glowDemoT;
    public void DrawGlowDemo(Matrix cam, Vector2 anchor)
    {
        _glowDemoTrail ??= new Trail(16, 0.4f);
        // Sweep a point along a lissajous curve, pushing one sample per frame.
        _glowDemoT += 0.08f;
        var p = anchor + new Vector2(MathF.Sin(_glowDemoT) * 70f, MathF.Cos(_glowDemoT * 1.7f) * 30f);
        _glowDemoTrail.Tick(1f / 30f);
        _glowDemoTrail.Push(p);
        _glow.DrawTrailGlow(cam, _glowDemoTrail, new Color(120, 200, 255), auraRadius: 20f, coreSize: 9f);
    }
}
