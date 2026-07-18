using System.Collections.Generic;
using MTile;
using Xunit;

namespace MTile.Tests;

// Guards the loop-seam guard (AnimationSampler.SeamMismatch): a looping phase-driven clip
// whose first/last keyframe poses drift apart degrades to one-shot seam sampling — a
// per-stride pose pop + cadence stall (the run.json incident). The detector must catch
// the drift, ignore intentional one-shot action clips, and go quiet once the seam is fixed.
public class SeamGuardTests
{
    private static AnimationDocument Clip(string type, bool loop, float lastLegRot)
    {
        List<PoseBoneEntry> Pose(float legRot) => new()
        {
            new PoseBoneEntry { Bone = "leg_l_upper", Rotation = legRot },
            new PoseBoneEntry { Bone = "chest",       Rotation = -1.57f },
        };
        return new AnimationDocument
        {
            Name = "test", Type = type, Loop = loop, Duration = 0.7f,
            Keyframes = new List<AnimationKeyframe>
            {
                new() { Time = 0f,   Bones = Pose(1.0f) },
                new() { Time = 0.5f, Bones = Pose(1.8f) },
                new() { Time = 1f,   Bones = Pose(lastLegRot) },
            },
        };
    }

    [Fact]
    public void DriftedSeamOnLoopingRun_IsFlagged()
    {
        Assert.True(AnimationSampler.SeamMismatch(Clip("Run", true, 1.45f), out string bone, out float d));
        Assert.Equal("leg_l_upper", bone);
        Assert.Equal(0.45f, d, 2);
    }

    [Fact]
    public void MatchedSeam_IsClean()
        => Assert.False(AnimationSampler.SeamMismatch(Clip("Run", true, 1.0f), out _, out _));

    [Fact]
    public void ActionClipWithLoopFlag_IsIgnored()
        // Action overlays declare Loop=true with start != end ON PURPOSE (they play once
        // over the action window) — the guard must not nag about them.
        => Assert.False(AnimationSampler.SeamMismatch(Clip("GroundSlash1", true, 9.9f), out _, out _));

    [Fact]
    public void NonLoopingClip_IsIgnored()
        => Assert.False(AnimationSampler.SeamMismatch(Clip("Run", false, 9.9f), out _, out _));

    [Fact]
    public void ShippedLocomotionClips_HaveCleanSeams()
    {
        foreach (var doc in AnimationStore.LoadAll(StatesDir()))
            Assert.False(AnimationSampler.SeamMismatch(doc, out string bone, out float d),
                $"clip '{doc.Name}': seam mismatch on {bone} ({d:0.000} rad) — copy the first keyframe pose onto the last");
    }

    private static string StatesDir()
    {
        var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (d != null)
        {
            string c = System.IO.Path.Combine(d.FullName, "SkeletonStates");
            if (System.IO.Directory.Exists(c)) return c;
            d = d.Parent;
        }
        return "SkeletonStates";
    }
}
