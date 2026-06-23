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
        if (ks == null || ks.Count == 0) { dest.SetToBind(); return; }
        if (ks.Count == 1 || t <= ks[0].Time)    { PoseData.Apply(ks[0].Bones, dest); return; }
        if (t >= ks[ks.Count - 1].Time)           { PoseData.Apply(ks[ks.Count - 1].Bones, dest); return; }

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
        int i = 0;
        while (i < N - 1 && ks[i + 1].Time < t) i++;
        if (i > N - 2) i = N - 2;
        float h = ks[i + 1].Time - ks[i].Time;
        br.i0 = i; br.i1 = i + 1; br.h = h;
        br.u = h <= 1e-6f ? 0f : MathHelper.Clamp((t - ks[i].Time) / h, 0f, 1f);

        bool cyc = IsCyclic(doc);
        float period = ks[N - 1].Time - ks[0].Time;
        if (i - 1 >= 0)      { br.iL = i - 1; br.hL = ks[i].Time - ks[i - 1].Time;            br.haveL = true; }
        else if (cyc)        { br.iL = N - 2; br.hL = ks[0].Time - ks[N - 2].Time + period;   br.haveL = true; }
        else                 { br.iL = i;     br.hL = 1f;                                     br.haveL = false; }
        if (i + 2 <= N - 1)  { br.iR = i + 2; br.hR = ks[i + 2].Time - ks[i + 1].Time;        br.haveR = true; }
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
        float slopeCurr = dCurr / br.h;
        si  = br.haveL ? 0.5f * (MathHelper.WrapAngle(t1 - a.Local[k].Rotation) / br.hL + slopeCurr) : 0f;
        si1 = br.haveR ? 0.5f * (slopeCurr + MathHelper.WrapAngle(d.Local[k].Rotation - t2) / br.hR) : 0f;
    }

    // C1 sample at normalized time t into `dest` (rotation = Hermite spline; translation/scale
    // = rig bind, as authored). a/b/c/d are scratch poses for the keyframe quad. Drop-in for
    // SampleNormalized where smooth interpolation is wanted.
    public static void SampleSmooth(AnimationDocument doc, float t,
                                    SkeletonPose a, SkeletonPose b, SkeletonPose c, SkeletonPose d,
                                    SkeletonPose dest)
    {
        var ks = doc?.Keyframes;
        if (ks == null || ks.Count == 0) { dest.SetToBind(); return; }
        if (ks.Count == 1 || t <= ks[0].Time)        { PoseData.Apply(ks[0].Bones, dest); return; }
        if (t >= ks[ks.Count - 1].Time)              { PoseData.Apply(ks[ks.Count - 1].Bones, dest); return; }

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

    // Whether a clip is a seamless cycle (Loop set AND first/last keyframe poses match —
    // locomotion duplicates the seam pose; action clips default Loop=true but start≠end, so
    // they read as one-shots). Cached on the doc. Decides the C1 boundary-tangent policy.
    internal static bool IsCyclic(AnimationDocument doc)
    {
        if (doc.CyclicCache.HasValue) return doc.CyclicCache.Value;
        var ks = doc.Keyframes;
        bool cyc = doc.Loop && ks != null && ks.Count >= 3 && SamePose(ks[0], ks[ks.Count - 1]);
        doc.CyclicCache = cyc;
        return cyc;
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
