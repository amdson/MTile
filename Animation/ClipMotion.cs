using System;
using Microsoft.Xna.Framework;

namespace MTile;

// THE SHARED MOTION QUERY (Plans/ANIMATION_SCENE_AUTHORING_PLAN.md "Proposed document
// additions"; workplan chunk 3): one resolver for "where is the body's anchor in the clip's
// scene at phase t", used by the editor's placement, path dots, ghosts and the probe, so they
// cannot disagree. Resolves the clip's motion SOURCE once (explicit AnimationDocument.Motion,
// else the legacy precedence: a named ReferenceArc → the body_path track → stationary) and
// samples it:
//
//     p(t)   = BodyAt(t)               scene position of the com anchor, rig units
//     D      = CycleDisplacement       p(1) − p(0), for loop extension (BodyPath)
//     pExt(n + φ) = n·D + p(φ)         ExtendedBodyAt — a walking cycle repeating its pose
//                                      while advancing (one-shots clamp instead)
//
// Missing intent stays distinguishable from an explicitly stationary clip (HasIntent), and a
// requested arc that cannot be found is surfaced (ArcMissing) rather than silently replaced
// by a stale track. Derivatives, when needed, are per normalized phase (Duration converts).
public sealed class ClipMotion
{
    public MotionSource       Source     { get; private set; }
    public bool               Explicit   { get; private set; }   // AnimationDocument.Motion was set
    public bool               HasIntent  => Explicit || Source != MotionSource.InPlace;
    public HermiteClipDocument Arc       { get; private set; }   // ReferenceArc source only
    public bool               ArcMissing { get; private set; }   // source says arc, none resolvable
    public AnimationDocument  Doc        { get; private set; }

    // `arcProvider` maps a ReferenceArc name to its document (file or baked default); null
    // when the caller has no arcs at all.
    public static ClipMotion Resolve(AnimationDocument doc, Func<string, HermiteClipDocument> arcProvider)
    {
        var m = new ClipMotion { Doc = doc };
        if (doc == null) { m.Source = MotionSource.InPlace; return m; }
        bool hasArcName = !string.IsNullOrWhiteSpace(doc.ReferenceArc);
        m.Explicit = doc.Motion.HasValue;
        m.Source = doc.Motion ?? (hasArcName ? MotionSource.ReferenceArc
                               : BodyPath.TrySample(doc, 0f, out _) ? MotionSource.Track
                               : MotionSource.InPlace);
        if (m.Source == MotionSource.ReferenceArc)
        {
            m.Arc = hasArcName ? arcProvider?.Invoke(doc.ReferenceArc) : null;
            m.ArcMissing = m.Arc == null;
        }
        return m;
    }

    // Scene position of the com anchor at normalized phase t (clamped to [0,1] — the final
    // endpoint is sampled explicitly; callers wanting loop extension use ExtendedBodyAt).
    public Vector2 BodyAt(float t)
    {
        t = MathHelper.Clamp(t, 0f, 1f);
        switch (Source)
        {
            case MotionSource.Track:
                return BodyPath.TrySample(Doc, t, out var p) ? p : Vector2.Zero;
            case MotionSource.ReferenceArc:
                return Arc == null ? Vector2.Zero : ArcOffsetAt(ArcProgress(t));
            default:
                return Vector2.Zero;
        }
    }

    public Vector2 Delta(float from, float to) => BodyAt(to) - BodyAt(from);

    // Per-cycle displacement D = p(1) − p(0). Zero for a stationary or missing source.
    public Vector2 CycleDisplacement => BodyAt(1f) - BodyAt(0f);

    // Loop extension for an unwrapped phase: n whole cycles of D plus the fractional cycle.
    // A one-shot (Loop false) clamps to its final endpoint instead.
    public Vector2 ExtendedBodyAt(float unwrapped)
    {
        if (Doc == null || !Doc.Loop) return BodyAt(unwrapped);
        float n = MathF.Floor(unwrapped);
        return n * CycleDisplacement + BodyAt(unwrapped - n);
    }

    // The rig root's scene offset at t: the anchor minus the pose-local com (BodyPath's
    // contract, root = anchor − c). comAnchored is false when the clip authors no com — the
    // root then sits at the anchor itself (legacy fixed-root placement).
    public Vector2 RootAt(float t, out bool comAnchored)
    {
        Vector2 c = Vector2.Zero;
        comAnchored = Doc != null && BodyPath.TrySampleAnchor(Doc, t, out c, out _);
        return comAnchored ? BodyAt(t) - c : BodyAt(t);
    }

    // ── Reference-arc mapping (shared with the editor's former private helpers) ─────────
    // Clip time → arc parameter. The two have INDEPENDENT durations, so a clip only rides
    // its arc 1:1 when they match: a 0.4s clip on a 0.3s arc is at the gate by τ=0.75 and
    // overshoots after. Capped at 2 so a mistuned pair can't fling the body off the far end
    // of the linear extrapolation.
    public float ArcProgress(float t)
    {
        if (Arc == null) return 0f;
        float arcDur = Arc.Duration <= 1e-4f ? 1f : Arc.Duration;
        float clipDur = Doc == null || Doc.Duration <= 1e-4f ? 1f : Doc.Duration;
        return MathHelper.Clamp(MathHelper.Clamp(t, 0f, 1f) * (clipDur / arcDur), 0f, 2f);
    }

    // Scene offset at a point along the ARC's own parameter (not clip time), rig units. The
    // arc is authored in game pixels against its own anchors, so its size and direction come
    // straight from the file — only px → rig units is converted.
    public Vector2 ArcOffsetAt(float u)
    {
        if (Arc == null) return Vector2.Zero;
        Vector2 gate = Arc.Span / Game1.SkeletonScale;
        return new ReferenceFrame(Arc, Vector2.Zero, gate).Map(Arc.Eval(u));
    }
}
