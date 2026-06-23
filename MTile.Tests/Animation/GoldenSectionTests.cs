using System;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;

namespace MTile.Tests;

public class GoldenSectionTests
{
    // ---- minimizer algorithm ------------------------------------------------

    [Fact]
    public void Minimize_FindsInteriorMinimum()
    {
        float x = GoldenSection.Minimize(t => (t - 0.3f) * (t - 0.3f), 0f, 1f);
        Assert.InRange(x, 0.29f, 0.31f);
    }

    [Fact]
    public void Minimize_ClampsToLowerEdgeWhenMonotoneIncreasing()
    {
        // (t + 1)^2 is increasing on [0,1] → minimum at the bracket's lower edge.
        float x = GoldenSection.Minimize(t => (t + 1f) * (t + 1f), 0f, 1f);
        Assert.InRange(x, 0f, 0.02f);
    }

    [Fact]
    public void Minimize_ClampsToUpperEdgeWhenMonotoneDecreasing()
    {
        // (t - 2)^2 is decreasing on [0,1] → minimum at the bracket's upper edge.
        float x = GoldenSection.Minimize(t => (t - 2f) * (t - 2f), 0f, 1f);
        Assert.InRange(x, 0.98f, 1f);
    }

    // ---- no-slip cadence (the application) ----------------------------------

    // A planted contact whose horizontal travel comes from rotating the stance leg:
    // the solver should pick Δφ each frame so the foot tip stays put in world space
    // as the body translates forward. This is the 1-DOF cadence case the design
    // targets. Since pose translations now live in the shared skeleton, the foot
    // sweep is driven by rotating `leg_l_upper` — that produces an arc, so we
    // tolerate a small vertical residual (the arc curvature) that IK would later
    // remove.
    [Fact]
    public void Cadence_KeepsPlantedFootStationary_AsBodyTranslates()
    {
        var skel = SkeletonExamples.Biped();
        int footIdx = skel.IndexOf("foot_l");
        float footLen = skel.Bones[footIdx].Length;

        // 2-keyframe clip: rotate leg_l_upper from -0.25 to +0.25 rad so the foot
        // tip sweeps backward (a positive `stride` per the test's convention) as
        // phase advances 0 → 1. Y-down ⇒ +rotation is CW ⇒ +R swings the foot to
        // the left (backward when facing +X). Small angle keeps the arc near-linear
        // (curvature ≈ leg_len * (1−cos θ)).
        var clip = new AnimationDocument { Name = "t", Type = "Walk", Duration = 1f, Loop = true };
        clip.Keyframes.Add(LegRotKeyframe(skel, 0f, -0.25f));
        clip.Keyframes.Add(LegRotKeyframe(skel, 1f,  0.25f));

        var a = skel.CreatePose();
        var b = skel.CreatePose();
        var dest = skel.CreatePose();

        Vector2 FootWorld(float phase, float bodyX)
        {
            AnimationSampler.SampleNormalized(clip, phase, a, b, dest);
            dest.ComputeWorld(Affine2.FromTRS(new Vector2(bodyX, 0f), 0f, Vector2.One));
            return dest.WorldOf(footIdx).TransformPoint(new Vector2(footLen, 0f));
        }

        // Stride = how far the foot tip travels (root frame) over the full cycle.
        float stride = FootWorld(0f, 0f).X - FootWorld(1f, 0f).X;
        Assert.True(stride > 0f, $"expected a forward stride, got {stride}");

        // Capture the plant target at phase 0; then march the body one stride forward
        // and let the solver pick the phase advance each frame.
        Vector2 target = FootWorld(0f, 0f);
        float phi = 0f;
        float maxDrift = 0f;
        const int frames = 24;
        for (int k = 1; k <= frames; k++)
        {
            float bodyX = stride * k / frames;
            float dphi = GoldenSection.Minimize(
                d => (FootWorld(phi + d, bodyX) - target).LengthSquared(),
                0f, 0.2f);
            phi += dphi;
            maxDrift = MathF.Max(maxDrift, (FootWorld(phi, bodyX) - target).Length());
        }

        Assert.True(phi > 0.85f, $"phase should sweep the stance, ended at {phi}");
        // Arc-residual budget for a ±0.25 rad sweep on a ~30-unit leg. Cadence
        // cancels the horizontal motion; the vertical residual is the arc bow,
        // bounded by ~half the stride amplitude.
        Assert.True(maxDrift < 5f, $"planted foot drifted {maxDrift}px (expected near-no-slip; arc residual only)");
    }

    // A keyframe at `time` with leg_l_upper rotated by `rot` from bind (sweeps the
    // whole leg about the hip joint).
    private static AnimationKeyframe LegRotKeyframe(Skeleton skel, float time, float rot)
    {
        var p = skel.CreatePose();
        int li = skel.IndexOf("leg_l_upper");
        var t = p.Local[li];
        t.Rotation = skel.Bones[li].Bind.Rotation + rot;   // swing around the leg's rest (down) orientation
        p.SetLocal(li, t);
        return new AnimationKeyframe { Time = time, Bones = PoseData.Capture(p) };
    }
}
