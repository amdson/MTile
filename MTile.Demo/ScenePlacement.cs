using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile;

namespace MTileDemo;

// SCENE PLACEMENT (Plans/ANIMATION_SCENE_AUTHORING_PLAN.md "Refactor boundaries" 1): where the
// clip's scene origin, the com anchor and the rig root land on screen at the playhead. The
// math is the shared ClipMotion query (the same sampler the probe and the runtime read), so the
// editor's ghosts, path dots and rendered pose cannot disagree; this class only adds the VIEW:
// the working-area center, the arrow-key pan, the wheel zoom, an optional follow camera, and
// the continuous-loop preview. Recomputed every frame.
//
// Frames: SCENE space (rig units, origin = the scene anchor) ↔ SCREEN via SceneAnchor +
// p·Scale. The rig root hangs off the com anchor by BodyPath's contract (root = anchor −
// com·Scale) — com-anchored clips draw exactly the way the game places them.
//
// SCALE: `Scale` (px per rig unit) is RigScale × Zoom. Everything that converts between screen
// and rig units reads it — or reads it off the frame transform it was handed — so the whole
// view zooms as one: rig, guides, grid, path, ghosts, and every drag's conversion.
internal sealed class ScenePlacement
{
    public const float RigScale = 5f;    // screen px per rig unit at 100% zoom
    public const float MinZoom = 0.25f, MaxZoom = 8f;
    // The in-game ground sits 2·Radius under the body position (the addcom stamp convention:
    // com.Y = sole − 2R/scale ⇒ a planted sole rests on the line).
    public const float GroundBelowComRig = 2f * PlayerCharacter.Radius / Game1.SkeletonScale;

    public Vector2 Pan;                  // arrow keys / middle-drag nudge, Home resets, Frame sets
    public bool    FollowView;           // camera tracks the body instead of the fixed scene
    public bool    ContinuousLoop;       // playback accumulates the cycle displacement (loops)

    public float Zoom { get; private set; } = 1f;
    public float Scale => RigScale * Zoom;

    public ClipMotion Motion      { get; private set; }
    public Vector2    SceneAnchor { get; private set; }   // screen point of scene (0,0)
    public Vector2    Anchor      { get; private set; }   // screen point of the com anchor at the playhead
    public Affine2    Root        { get; private set; }   // the rig root transform

    // `t` is the playhead; `unwrapped` (continuous-loop playback) lets it exceed 1 so whole
    // cycles of displacement accumulate.
    public void Update(AnimationDocument doc, float t, bool unwrapped, Vector2 center)
    {
        Motion = ClipMotion.Resolve(doc);
        float wrapped = t - MathF.Floor(t);
        Vector2 body = unwrapped && ContinuousLoop ? Motion.ExtendedBodyAt(t) : Motion.BodyAt(wrapped);
        Vector2 bodyPx = body * Scale;

        SceneAnchor = center + Pan;
        if (FollowView) SceneAnchor -= bodyPx;   // the body stays put; the scene slides under it
        Anchor = SceneAnchor + bodyPx;

        // Every clip authors a com, so the root always hangs off the anchor by BodyPath's
        // contract. A doc that somehow samples none leaves com at zero, which lands the root
        // on the anchor — the same place, with no branch to keep in step.
        Vector2 com = Vector2.Zero;
        if (doc != null) BodyPath.TrySampleAnchor(doc, wrapped, out com, out _);
        Root = Affine2.FromTRS(Anchor - com * Scale, 0f, new Vector2(Scale, Scale));
    }

    public Vector2 ToScreen(Vector2 scene) => SceneAnchor + scene * Scale;
    public Vector2 ToScene(Vector2 screen) => (screen - SceneAnchor) / Scale;

    // Wheel zoom ABOUT A SCREEN POINT: the scene position under the cursor stays under it, so
    // zooming reads as pulling the view toward what you are pointing at rather than drifting
    // off centre. Solving SceneAnchor' + Zoom'·p = screen for the same p gives the pan delta.
    public void ZoomBy(float factor, Vector2 screen)
    {
        float old = Zoom;
        Zoom = MathHelper.Clamp(Zoom * factor, MinZoom, MaxZoom);
        float k = Zoom / old;
        if (MathF.Abs(k - 1f) < 1e-6f) return;
        Pan += (screen - SceneAnchor) * (1f - k);
    }

    public void ResetZoom() => Zoom = 1f;

    // The rig root at any phase (ghosts): the same contract as Root, at `t`.
    public Affine2 RootAt(float t)
    {
        Vector2 r = Motion.RootAt(t, out _);
        return Affine2.FromTRS(SceneAnchor + r * Scale, 0f, new Vector2(Scale, Scale));
    }

    // The frame GROUND-RELATIVE references live in: scene space (SceneAnchor, Scale), with the
    // nominal ground line GroundBelowComRig under the anchor. A clip's own Ground guides are
    // authored in this frame and may sit anywhere; this is only where the grid and a new
    // guide's snap origin start from.
    public (Affine2 frame, float groundY) GuideFrame()
        => (Affine2.FromTRS(SceneAnchor, 0f, new Vector2(Scale, Scale)), GroundBelowComRig);

    // "Frame scene/path": FIT the path (with the riding body's extent) and every visible guide
    // in the working area — zoom to cover the content's extent with a margin, then pan it
    // centred. `viewSize` is the canvas in px; pass Vector2.Zero to keep the current zoom and
    // only re-centre.
    public void FrameScene(AnimationDocument doc, IEnumerable<SceneGuide> guides, Vector2 center, Vector2 viewSize = default)
    {
        Vector2 min = new(float.MaxValue), max = new(float.MinValue);
        void Include(Vector2 p) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
        for (int i = 0; i <= 16; i++)
        {
            Vector2 p = Motion.BodyAt(i / 16f);
            Include(p + new Vector2(0f, GroundBelowComRig)); Include(p - new Vector2(0f, GroundBelowComRig * 0.5f));
        }
        if (guides != null)
            foreach (var g in guides)
            {
                if (g.Hidden) continue;
                if (g.Kind == SceneGuideKind.Ground) Include(new Vector2(0f, g.Y));
                else { Include(new Vector2(g.X, g.Y)); Include(new Vector2(g.X + g.W, g.Y + g.H)); }
            }
        if (min.X > max.X) { Pan = Vector2.Zero; return; }

        if (viewSize.X > 1f && viewSize.Y > 1f)
        {
            Vector2 span = Vector2.Max(max - min, new Vector2(1f));
            float fit = MathF.Min(viewSize.X * 0.8f / span.X, viewSize.Y * 0.8f / span.Y) / RigScale;
            Zoom = MathHelper.Clamp(fit, MinZoom, MaxZoom);
        }
        Pan = -(min + max) * 0.5f * Scale;
    }
}
