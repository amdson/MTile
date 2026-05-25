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
}
