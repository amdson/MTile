using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile;

namespace MTileDemo;

// SCENE PLACEMENT (Plans/ANIMATION_SCENE_AUTHORING_PLAN.md "Refactor boundaries" 1): where the
// clip's scene origin, the com anchor and the rig root land on screen at the playhead. The
// math is the shared ClipMotion query (the same sampler the probe and the runtime read), so the
// editor's ghosts, path dots and rendered pose cannot disagree; this class only adds the VIEW:
// the working-area center, the arrow-key pan, the reference-arc centering bias, an optional
// follow camera, and the continuous-loop preview. Recomputed every frame.
//
// Frames: SCENE space (rig units, origin = the scene anchor) ↔ SCREEN via SceneAnchor +
// p·RigScale. The rig root hangs off the com anchor by BodyPath's contract (root = anchor −
// com·RigScale) — com-anchored clips draw exactly the way the game places them.
internal sealed class ScenePlacement
{
    public const float RigScale = 5f;   // screen px per rig unit
    // The in-game ground sits 2·Radius under the body position (the addcom stamp convention:
    // com.Y = sole − 2R/scale ⇒ a planted sole rests on the line).
    public const float GroundBelowComRig = 2f * PlayerCharacter.Radius / Game1.SkeletonScale;

    public Vector2 Pan;                  // arrow keys nudge, Home resets, Frame sets
    public bool    FollowView;           // camera tracks the body instead of the fixed scene
    public bool    ContinuousLoop;       // playback accumulates the cycle displacement (loops)

    public ClipMotion Motion      { get; private set; }
    public Vector2    SceneAnchor { get; private set; }   // screen point of scene (0,0)
    public Vector2    Anchor      { get; private set; }   // screen point of the com anchor at the playhead
    public Affine2    Root        { get; private set; }   // the rig root transform
    public bool       ComAnchored { get; private set; }   // the clip authors a com (else legacy fixed root)

    // `t` is the playhead; `unwrapped` (continuous-loop playback) lets it exceed 1 so whole
    // cycles of displacement accumulate. `arc` is the loaded ReferenceArc (or null).
    public void Update(AnimationDocument doc, HermiteClipDocument arc, float t, bool unwrapped, Vector2 center)
    {
        Motion = ClipMotion.Resolve(doc, _ => arc);
        float wrapped = t - MathF.Floor(t);
        Vector2 body = unwrapped && ContinuousLoop ? Motion.ExtendedBodyAt(t) : Motion.BodyAt(wrapped);
        Vector2 bodyPx = body * RigScale;

        SceneAnchor = center + Pan;
        // An arc is authored at true game scale, so a tall one would run off the working area
        // from a centered anchor: bias the scene by the arc's own midpoint (view only).
        if (!FollowView && Motion.Source == MotionSource.ReferenceArc && Motion.Arc != null)
            SceneAnchor -= ArcCenter() * RigScale;
        if (FollowView) SceneAnchor -= bodyPx;   // the body stays put; the scene slides under it
        Anchor = SceneAnchor + bodyPx;

        Vector2 com = Vector2.Zero;
        ComAnchored = doc != null && BodyPath.TrySampleAnchor(doc, wrapped, out com, out _);
        Root = Affine2.FromTRS(ComAnchored ? Anchor - com * RigScale : Anchor, 0f, new Vector2(RigScale, RigScale));
    }

    public Vector2 ToScreen(Vector2 scene) => SceneAnchor + scene * RigScale;
    public Vector2 ToScene(Vector2 screen) => (screen - SceneAnchor) / RigScale;

    // The rig root at any phase (ghosts): the same contract as Root, at `t`.
    public Affine2 RootAt(float t)
    {
        Vector2 r = Motion.RootAt(t, out _);
        return Affine2.FromTRS(SceneAnchor + r * RigScale, 0f, new Vector2(RigScale, RigScale));
    }

    // The frame GROUND-RELATIVE references live in. An explicit Scene's guides are in scene
    // space (SceneAnchor, RigScale). Legacy previews: fixed at the anchor when com-anchored
    // (GroundBelowComRig under it), else the rig root itself with the bind-pose sole height.
    public (Affine2 frame, float groundY) GuideFrame(AnimationDocument doc, float legacyFloorLocalY)
    {
        if (doc?.Scene != null || ComAnchored)
            return (Affine2.FromTRS(SceneAnchor, 0f, new Vector2(RigScale, RigScale)), GroundBelowComRig);
        return (Root, legacyFloorLocalY);
    }

    // Midpoint of the drawn arc's bounding box, in rig units — the view bias that keeps a
    // full-scale path on screen. The box covers what the RIDING BODY sweeps: the arc's points
    // are com positions, and the rig hangs GroundBelowComRig below its com with the head some
    // way above, so only that asymmetry moves the midpoint.
    public Vector2 ArcCenter()
    {
        float end = MathF.Max(Motion.ArcProgress(1f), 1f);
        Vector2 min = new(float.MaxValue), max = new(float.MinValue);
        for (int i = 0; i <= 16; i++)
        {
            Vector2 p = Motion.ArcOffsetAt(end * i / 16f);
            min = Vector2.Min(min, p); max = Vector2.Max(max, p);
        }
        max.Y += GroundBelowComRig;
        min.Y -= GroundBelowComRig * 0.5f;
        return (min + max) * 0.5f;
    }

    // "Frame scene/path": pan so the path (with the riding body's extent) and every visible
    // guide sit centered in the working area.
    public void FrameScene(AnimationDocument doc, IEnumerable<SceneGuide> guides, Vector2 center)
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
        Vector2 c = (min + max) * 0.5f;
        Vector2 bias = !FollowView && Motion.Source == MotionSource.ReferenceArc && Motion.Arc != null ? ArcCenter() : Vector2.Zero;
        Pan = (bias - c) * RigScale;
    }
}
