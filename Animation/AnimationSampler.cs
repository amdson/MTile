using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Stateless sampling of an AnimationDocument into a SkeletonPose. Shared by the
// editor (scrubbing/playback) and the in-game CharacterAnimator. Caller supplies
// two scratch poses so sampling allocates nothing per frame.
public static class AnimationSampler
{
    // Map elapsed seconds to a normalized timeline position [0,1], honoring the
    // doc's Duration and Loop (clamp at the end when not looping).
    public static float NormalizedTime(AnimationDocument doc, float elapsedSeconds)
    {
        float dur = doc.Duration <= 1e-4f ? 1f : doc.Duration;
        float u = elapsedSeconds / dur;
        return doc.Loop ? u - MathF.Floor(u) : MathHelper.Clamp(u, 0f, 1f);
    }

    // Sample at normalized time t (in [0,1] timeline space) into `dest`.
    public static void SampleNormalized(AnimationDocument doc, float t,
                                        SkeletonPose a, SkeletonPose b, SkeletonPose dest)
    {
        var ks = doc?.Keyframes;
        if (ks == null || ks.Count == 0) { dest.SetToDefault(); return; }
        if (ks.Count == 1) { PoseData.Apply(ks[0].Bones, dest); return; }
        if (IsCyclic(doc) && HasOpenTail(doc) && (t < ks[0].Time || t > ks[ks.Count - 1].Time))
        {
            // Open-tail loop: interpolate the wrap segment [lastTime, firstTime+1] (period 1).
            float last = ks[ks.Count - 1].Time;
            float h = ks[0].Time + 1f - last;
            float dt = t >= last ? t - last : t + 1f - last;
            float uw = h <= 1e-6f ? 0f : MathHelper.Clamp(dt / h, 0f, 1f);
            PoseData.Apply(ks[ks.Count - 1].Bones, a);
            PoseData.Apply(ks[0].Bones,            b);
            SkeletonPose.Lerp(a, b, uw, dest);
            return;
        }
        if (t <= ks[0].Time)            { PoseData.Apply(ks[0].Bones, dest); return; }
        if (t >= ks[ks.Count - 1].Time) { PoseData.Apply(ks[ks.Count - 1].Bones, dest); return; }

        int i = 0;
        while (i < ks.Count - 1 && ks[i + 1].Time < t) i++;
        float span = ks[i + 1].Time - ks[i].Time;
        float u = span <= 1e-6f ? 0f : (t - ks[i].Time) / span;
        PoseData.Apply(ks[i].Bones,     a);
        PoseData.Apply(ks[i + 1].Bones, b);
        SkeletonPose.Lerp(a, b, u, dest);
    }

    // Convenience: elapsed seconds -> pose in one call.
    public static void SampleAtTime(AnimationDocument doc, float elapsedSeconds,
                                    SkeletonPose a, SkeletonPose b, SkeletonPose dest)
        => SampleNormalized(doc, NormalizedTime(doc, elapsedSeconds), a, b, dest);

    // --- C1 (Catmull-Rom) sampling -------------------------------------------
    //
    // Linear keyframe interpolation is only C0, so the per-bone angular velocity is a step
    // function that jumps at every keyframe (the §3.5 "keyframe kink") — a problem for the
    // analytic cadence Jacobian and for cadence smoothness across the loop seam. These two
    // methods interpolate the rotation channel (the only authored one) with a cubic Hermite
    // spline whose tangent at each keyframe is the AVERAGE of the adjacent secant slopes
    // (the non-uniform Catmull-Rom tangent). The pose (SampleSmooth) and its exact derivative
    // (SampleAngularVelocity) come from the SAME spline, so ω is continuous and the analytic
    // Jacobian matches finite differences everywhere. Tangents at the boundary: a CYCLIC clip
    // (IsCyclic — a seamless loop) wraps them across the seam so velocity is continuous
    // through φ:1→0; a one-shot zeros the end tangents (ease in/out, continuous with the
    // held clamp). Four scratch poses (the keyframe quad iL,i0,i1,iR), as the bracket needs
    // a neighbor on each side.

    // Resolved cubic bracket for normalized time t: the interval [i0,i1], the sub-position u
    // and span h, and the outer neighbors iL / iR (with their spans) — wrapped for a cyclic
    // clip, or flagged absent (haveL/haveR=false → zero end tangent) for a one-shot.
    private struct Bracket
    {
        public int   i0, i1, iL, iR;
        public float u, h, hL, hR;
        public bool  haveL, haveR;
    }

    private static bool TryBracket(AnimationDocument doc, float t, out Bracket br)
    {
        br = default;
        var ks = doc.Keyframes;
        int N = ks.Count;
        bool cyc  = IsCyclic(doc);
        bool open = cyc && HasOpenTail(doc);

        // Open-tail loop, t in the wrap gap: the segment IS [last, first+1] (period 1).
        if (open && (t < ks[0].Time || t > ks[N - 1].Time))
        {
            float wrapH = ks[0].Time + 1f - ks[N - 1].Time;
            float dt = t >= ks[N - 1].Time ? t - ks[N - 1].Time : t + 1f - ks[N - 1].Time;
            br.i0 = N - 1; br.i1 = 0; br.h = wrapH;
            br.u  = wrapH <= 1e-6f ? 0f : MathHelper.Clamp(dt / wrapH, 0f, 1f);
            br.iL = N - 2; br.hL = ks[N - 1].Time - ks[N - 2].Time; br.haveL = true;
            br.iR = 1;     br.hR = ks[1].Time - ks[0].Time;         br.haveR = true;
            return true;
        }

        int i = 0;
        while (i < N - 1 && ks[i + 1].Time < t) i++;
        if (i > N - 2) i = N - 2;
        float h = ks[i + 1].Time - ks[i].Time;
        br.i0 = i; br.i1 = i + 1; br.h = h;
        br.u = h <= 1e-6f ? 0f : MathHelper.Clamp((t - ks[i].Time) / h, 0f, 1f);

        // Cyclic neighbor across the seam: an open-tail loop's neighbor is the wrap segment
        // itself (last↔first, span to the NEXT cycle); a duplicate-seam loop skips the
        // duplicated endpoint keyframe (period = last−first).
        float seamH  = open ? ks[0].Time + 1f - ks[N - 1].Time : 0f;
        float period = ks[N - 1].Time - ks[0].Time;
        if (i - 1 >= 0)      { br.iL = i - 1; br.hL = ks[i].Time - ks[i - 1].Time;            br.haveL = true; }
        else if (open)       { br.iL = N - 1; br.hL = seamH;                                  br.haveL = true; }
        else if (cyc)        { br.iL = N - 2; br.hL = ks[0].Time - ks[N - 2].Time + period;   br.haveL = true; }
        else                 { br.iL = i;     br.hL = 1f;                                     br.haveL = false; }
        if (i + 2 <= N - 1)  { br.iR = i + 2; br.hR = ks[i + 2].Time - ks[i + 1].Time;        br.haveR = true; }
        else if (open)       { br.iR = 0;     br.hR = seamH;                                  br.haveR = true; }
        else if (cyc)        { br.iR = 1;     br.hR = ks[1].Time - ks[0].Time;                br.haveR = true; }
        else                 { br.iR = i + 1; br.hR = 1f;                                     br.haveR = false; }
        return true;
    }

    // Per-bone tangent slopes (dθ/dt) at the two interval endpoints, plus the interval's own
    // secant delta — the shared core of the pose and velocity evaluation. b/c hold the
    // bracketing keyframes i0/i1; a/d the outer neighbors iL/iR (only read when haveL/haveR).
    private static void Tangents(in Bracket br, SkeletonPose a, SkeletonPose b, SkeletonPose c,
                                 SkeletonPose d, int k, out float dCurr, out float si, out float si1)
    {
        float t1 = b.Local[k].Rotation, t2 = c.Local[k].Rotation;
        dCurr = MathHelper.WrapAngle(t2 - t1);
        // Zero-width intervals (two keyframes sampled at the same editor playhead) must
        // degrade to a step, not divide to NaN — a NaN here poisons the pose for every
        // frame whose C1 bracket touches the duplicate (seen as the rig vanishing for
        // ~2 frames per cycle in run.json). Guard every interval width the same way.
        float slopeCurr = br.h  <= 1e-6f ? 0f : dCurr / br.h;
        float slopeL    = br.hL <= 1e-6f ? 0f : MathHelper.WrapAngle(t1 - a.Local[k].Rotation) / br.hL;
        float slopeR    = br.hR <= 1e-6f ? 0f : MathHelper.WrapAngle(d.Local[k].Rotation - t2) / br.hR;
        si  = br.haveL ? 0.5f * (slopeL + slopeCurr) : 0f;
        si1 = br.haveR ? 0.5f * (slopeCurr + slopeR) : 0f;
    }

    // C1 sample at normalized time t into `dest` (rotation = Hermite spline; translation/scale
    // = rig bind, as authored). a/b/c/d are scratch poses for the keyframe quad. Drop-in for
    // SampleNormalized where smooth interpolation is wanted.
    public static void SampleSmooth(AnimationDocument doc, float t,
                                    SkeletonPose a, SkeletonPose b, SkeletonPose c, SkeletonPose d,
                                    SkeletonPose dest)
    {
        var ks = doc?.Keyframes;
        if (ks == null || ks.Count == 0) { dest.SetToDefault(); return; }
        if (ks.Count == 1) { PoseData.Apply(ks[0].Bones, dest); return; }
        // An open-tail loop has no held clamp region: times outside [first,last] are the
        // wrap segment, which TryBracket resolves. Only clamp for everything else.
        if (!(IsCyclic(doc) && HasOpenTail(doc)))
        {
            if (t <= ks[0].Time)            { PoseData.Apply(ks[0].Bones, dest); return; }
            if (t >= ks[ks.Count - 1].Time) { PoseData.Apply(ks[ks.Count - 1].Bones, dest); return; }
        }

        TryBracket(doc, t, out var br);
        PoseData.Apply(ks[br.iL].Bones, a);
        PoseData.Apply(ks[br.i0].Bones, b);
        PoseData.Apply(ks[br.i1].Bones, c);
        PoseData.Apply(ks[br.iR].Bones, d);

        float u = br.u, u2 = u * u, u3 = u2 * u;
        float h01 = -2f * u3 + 3f * u2, h10 = u3 - 2f * u2 + u, h11 = u3 - u2;
        int count = dest.Count;
        for (int k = 0; k < count; k++)
        {
            Tangents(br, a, b, c, d, k, out float dCurr, out float si, out float si1);
            float rot = b.Local[k].Rotation + h01 * dCurr + br.h * (h10 * si + h11 * si1);
            dest.Local[k] = new BoneTransform(b.Local[k].Translation, rot, b.Local[k].Scale);
        }
    }

    // Per-bone angular velocity dθ/dt of the C1 spline at normalized time t — the exact
    // derivative of SampleSmooth, hence the ∂(clip pose)/∂φ the analytic cadence Jacobian
    // chains through FK. Continuous everywhere (including the loop seam for a cyclic clip);
    // zero in the clamped end regions (the pose is held). a/b/c/d scratch as in SampleSmooth.
    public static void SampleAngularVelocity(AnimationDocument doc, float t,
                                             SkeletonPose a, SkeletonPose b, SkeletonPose c,
                                             SkeletonPose d, Span<float> vel)
    {
        vel.Clear();
        var ks = doc?.Keyframes;
        if (ks == null || ks.Count < 2) return;
        // A one-shot HOLDS outside [first,last] → zero velocity there. A cyclic clip has no
        // held region: t≈0/1 is the loop SEAM, interior to the cycle, where the velocity is
        // the (wrapped) seam tangent — so bracket it instead of zeroing.
        if (!IsCyclic(doc) && (t <= ks[0].Time || t >= ks[ks.Count - 1].Time)) return;

        TryBracket(doc, t, out var br);
        PoseData.Apply(ks[br.iL].Bones, a);
        PoseData.Apply(ks[br.i0].Bones, b);
        PoseData.Apply(ks[br.i1].Bones, c);
        PoseData.Apply(ks[br.iR].Bones, d);

        float u = br.u;
        float h01p = -6f * u * u + 6f * u, h10p = 3f * u * u - 4f * u + 1f, h11p = 3f * u * u - 2f * u;
        float invH = 1f / br.h;
        int count = Math.Min(b.Count, vel.Length);
        for (int k = 0; k < count; k++)
        {
            Tangents(br, a, b, c, d, k, out float dCurr, out float si, out float si1);
            vel[k] = h01p * dCurr * invH + h10p * si + h11p * si1;
        }
    }

    // Whether a clip is a seamless cycle. Two authoring styles qualify:
    //  - ENDPOINT SEAM: the last keyframe sits at t≈1 and duplicates the first pose
    //    (the legacy convention; SeamMismatch guards the duplicate).
    //  - OPEN TAIL: a looping phase-driven clip whose last keyframe ends BEFORE t=1.
    //    The cycle period is exactly 1 and the sampler interpolates the wrap segment
    //    [lastTime, firstTime+1] back to the first keyframe — no duplicate needed.
    // Action clips default Loop=true but start≠end at the endpoint, so they still read as
    // one-shots (the open-tail rule is gated to locomotion types to keep it that way).
    // Cached on the doc. Decides the C1 boundary-tangent policy.
    internal static bool IsCyclic(AnimationDocument doc)
    {
        if (doc.CyclicCache.HasValue) return doc.CyclicCache.Value;
        var ks = doc.Keyframes;
        bool cyc = doc.Loop && ks != null && ks.Count >= 3 &&
                   (SamePose(ks[0], ks[ks.Count - 1]) ||
                    (HasOpenTail(doc) && IsPhaseLoopType(doc)));
        doc.CyclicCache = cyc;
        return cyc;
    }

    // The last keyframe ends before the 1-unit cycle endpoint, leaving a real wrap segment.
    internal static bool HasOpenTail(AnimationDocument doc)
    {
        var ks = doc?.Keyframes;
        return ks != null && ks.Count > 0 && ks[ks.Count - 1].Time < 1f - EndpointEps;
    }

    // The clip categories that loop through the phase seam (same gate as SeamMismatch).
    private static bool IsPhaseLoopType(AnimationDocument doc)
        => Enum.TryParse<AnimClip>(doc.Type, ignoreCase: true, out var c) &&
           (c == AnimClip.Walk || c == AnimClip.WalkBack || c == AnimClip.Run || c == AnimClip.Idle
            || c == AnimClip.CrouchWalk);

    private const float EndpointEps = 1e-3f;

    // AUTHORING GUARD: a looping phase-driven clip (Walk/WalkBack/Run/Idle) whose last
    // keyframe sits AT the cycle endpoint (t≈1) must make it a duplicate of the FIRST —
    // otherwise the clip silently degrades to one-shot seam semantics (held pose + zero ω
    // at the seam) ⇒ a per-stride pose pop and a cadence stall/hop (this exact failure
    // shipped in run.json once). A clip whose last keyframe ends BEFORE t=1 is an OPEN-TAIL
    // loop instead — the sampler interpolates the wrap segment back to the first keyframe,
    // no duplicate required — so it is exempt. Returns true on an endpoint drift and reports
    // the worst bone + wrapped delta. Recomputes fresh (ignores CyclicCache) so the animation
    // editor can warn live while the clip is being edited; also drops the stale cache so
    // sampling picks edits up.
    public static bool SeamMismatch(AnimationDocument doc, out string bone, out float deltaRad)
    {
        bone = null; deltaRad = 0f;
        var ks = doc?.Keyframes;
        if (ks == null || ks.Count < 3 || !doc.Loop) return false;
        // Only phase-driven categories loop through the seam; action clips declare Loop=true
        // with start≠end on purpose (they play once over the action window).
        if (!IsPhaseLoopType(doc)) return false;
        // Open-tail loop: the wrap segment supplies the seam — nothing to match.
        if (HasOpenTail(doc)) { doc.CyclicCache = null; return false; }

        var first = ks[0].Bones; var last = ks[ks.Count - 1].Bones;
        if (first == null || last == null) return false;
        foreach (var e in first)
        {
            if (e.Bone == null) continue;
            bool found = false;
            foreach (var f in last)
                if (f.Bone == e.Bone)
                {
                    float d = MathF.Abs(MathHelper.WrapAngle(f.Rotation - e.Rotation));
                    if (d > deltaRad) { deltaRad = d; bone = e.Bone; }
                    found = true; break;
                }
            if (!found && deltaRad < MathF.PI) { deltaRad = MathF.PI; bone = e.Bone + " (missing)"; }
        }
        doc.CyclicCache = null;   // edits may have changed cyclicity — let sampling re-derive
        return deltaRad > 1e-3f;
    }

    private static bool SamePose(AnimationKeyframe ka, AnimationKeyframe kb)
    {
        var ba = ka.Bones; var bb = kb.Bones;
        if (ba == null || bb == null || ba.Count != bb.Count) return false;
        foreach (var e in ba)
        {
            bool found = false;
            foreach (var f in bb)
                if (f.Bone == e.Bone) { if (MathF.Abs(MathHelper.WrapAngle(f.Rotation - e.Rotation)) > 1e-3f) return false; found = true; break; }
            if (!found) return false;
        }
        return true;
    }
}
