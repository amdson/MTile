using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests;

// Open-tail loop semantics: a looping phase-driven clip whose LAST keyframe ends before
// t=1 cycles with period exactly 1 — the sampler interpolates the wrap segment
// [lastTime, firstTime+1] back to the first keyframe, so no duplicated seam pose is
// needed. Clips whose last keyframe sits AT t=1 keep the legacy duplicate-seam contract
// (guarded by SeamMismatch); action clips keep reading as one-shots.
public class OpenTailLoopTests
{
    private const string BONE = "leg_l_upper";

    private static AnimationDocument Clip(string type, bool loop, params (float t, float rot)[] kfs)
    {
        var doc = new AnimationDocument { Name = "test", Type = type, Loop = loop, Duration = 0.7f,
                                          Keyframes = new List<AnimationKeyframe>() };
        foreach (var (t, rot) in kfs)
            doc.Keyframes.Add(new AnimationKeyframe
            {
                Time  = t,
                Bones = new List<PoseBoneEntry>
                {
                    new PoseBoneEntry { Bone = BONE,    Rotation = rot },
                    new PoseBoneEntry { Bone = "chest", Rotation = -1.57f },
                },
            });
        return doc;
    }

    // last keyframe at 0.75 — the wrap segment spans [0.75, 1.0] back to rot=1.0 at t=0(+1)
    private static AnimationDocument OpenRun()
        => Clip("Run", true, (0f, 1.0f), (0.3f, 1.8f), (0.75f, 0.4f));

    private static float Rot(AnimationDocument doc, float t)
    {
        var skel = SkeletonExamples.Biped();
        var a = new SkeletonPose(skel); var b = new SkeletonPose(skel);
        var c = new SkeletonPose(skel); var d = new SkeletonPose(skel);
        var dest = new SkeletonPose(skel);
        AnimationSampler.SampleSmooth(doc, t, a, b, c, d, dest);
        return dest.Local[skel.IndexOf(BONE)].Rotation;
    }

    private static float Vel(AnimationDocument doc, float t)
    {
        var skel = SkeletonExamples.Biped();
        var a = new SkeletonPose(skel); var b = new SkeletonPose(skel);
        var c = new SkeletonPose(skel); var d = new SkeletonPose(skel);
        Span<float> vel = stackalloc float[skel.Count];
        AnimationSampler.SampleAngularVelocity(doc, t, a, b, c, d, vel);
        return vel[skel.IndexOf(BONE)];
    }

    [Fact]
    public void OpenTailLocomotion_IsCyclic()
        => Assert.True(AnimationSampler.IsCyclic(OpenRun()));

    [Fact]
    public void ActionClipWithOpenTail_StaysOneShot()
        // Action overlays declare Loop=true with start != end; ending before t=1 must NOT
        // suddenly make them wrap.
        => Assert.False(AnimationSampler.IsCyclic(Clip("GroundSlash1", true, (0f, 1.0f), (0.3f, 1.8f), (0.75f, 0.4f))));

    [Fact]
    public void WrapSegment_InterpolatesLastToFirst()
    {
        var doc = OpenRun();
        // Midpoint of the wrap segment [0.75, 1.0]: rotation should sit strictly between
        // the last keyframe (0.4) and the first (1.0) — not held at 0.4.
        float mid = Rot(doc, 0.875f);
        Assert.InRange(mid, 0.45f, 0.95f);
    }

    [Fact]
    public void PoseIsContinuousAcrossSeam()
    {
        var doc = OpenRun();
        float before = Rot(doc, 0.999f);
        float atZero = Rot(doc, 0f);
        Assert.True(MathF.Abs(MathHelper.WrapAngle(atZero - before)) < 0.02f,
            $"pose pops across the seam: {before:0.###} -> {atZero:0.###}");
    }

    [Fact]
    public void VelocityIsContinuousAcrossSeam()
    {
        var doc = OpenRun();
        float before = Vel(doc, 0.995f);
        float after  = Vel(doc, 0.005f);
        Assert.True(MathF.Abs(after - before) < 0.35f,
            $"omega jumps across the seam: {before:0.###} -> {after:0.###}");
        // And it must actually be moving through the seam (an accidental one-shot would
        // hold the pose: omega == 0 there).
        Assert.True(MathF.Abs(before) > 0.1f, "seam velocity is ~0 — clip degraded to one-shot");
    }

    [Fact]
    public void VelocityMatchesFiniteDifference_InWrapSegment()
    {
        var doc = OpenRun();
        const float e = 1e-3f;
        foreach (float t in new[] { 0.80f, 0.875f, 0.97f })
        {
            float fd = MathHelper.WrapAngle(Rot(doc, t + e) - Rot(doc, t - e)) / (2f * e);
            Assert.True(MathF.Abs(fd - Vel(doc, t)) < 0.05f,
                $"t={t}: analytic {Vel(doc, t):0.####} vs FD {fd:0.####}");
        }
    }

    [Fact]
    public void SeamMismatch_ExemptsOpenTail()
        => Assert.False(AnimationSampler.SeamMismatch(OpenRun(), out _, out _));

    [Fact]
    public void SeamMismatch_StillFlagsEndpointDrift()
        => Assert.True(AnimationSampler.SeamMismatch(
            Clip("Run", true, (0f, 1.0f), (0.5f, 1.8f), (1f, 1.45f)), out string bone, out _)
            && bone == BONE);

    [Fact]
    public void LinearSampler_AlsoWraps()
    {
        var doc = OpenRun();
        var skel = SkeletonExamples.Biped();
        var a = new SkeletonPose(skel); var b = new SkeletonPose(skel);
        var dest = new SkeletonPose(skel);
        AnimationSampler.SampleNormalized(doc, 0.875f, a, b, dest);
        float mid = dest.Local[skel.IndexOf(BONE)].Rotation;
        Assert.Equal(0.7f, mid, 2);   // exact lerp midpoint of 0.4 -> 1.0
    }
}
