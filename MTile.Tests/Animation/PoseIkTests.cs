using System;
using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests;

// PoseIk — the offline authoring IK behind `MTile.Probe ik`. Rig-local units, +y DOWN.
public class PoseIkTests
{
    private static Vector2 Tip(Skeleton rig, SkeletonPose pose, string bone)
        => pose.ComputeWorld(Affine2.FromTRS(Vector2.Zero, 0f, Vector2.One))[rig.IndexOf(bone)].Translation;

    [Fact]
    public void DefaultChain_FootIsItsOwnLimb()
    {
        var rig = SkeletonExamples.Biped();
        var chain = PoseIk.DefaultChain(rig, rig.IndexOf("foot_l"));
        Assert.Equal(new[] { "leg_l_upper", "leg_l_lower", "foot_l" },
                     Array.ConvertAll(chain, i => rig.Bones[i].Name));

        var hand = PoseIk.DefaultChain(rig, rig.IndexOf("arm_r_lower"));
        Assert.Equal(new[] { "arm_r_upper", "arm_r_lower" },
                     Array.ConvertAll(hand, i => rig.Bones[i].Name));
    }

    [Fact]
    public void ReachableNudge_HitsTarget_AndOnlyMovesTheChain()
    {
        var rig = SkeletonExamples.Biped();
        var pose = rig.CreatePose(); pose.SetToDefault();
        int tip = rig.IndexOf("foot_l");
        var before = pose.CloneLocal();

        Vector2 target = Tip(rig, pose, "foot_l") + new Vector2(3f, -2f);   // forward + up a touch
        var r = PoseIk.Solve(rig, pose, tip, target, PoseIk.DefaultChain(rig, tip));

        Assert.True(r.Miss < 0.1f, $"miss {r.Miss}");
        Assert.True((Tip(rig, pose, "foot_l") - target).Length() < 0.1f);
        // Non-chain bones untouched (minimal-change property).
        foreach (var name in new[] { "hip", "chest", "head", "arm_l_upper", "leg_r_upper", "foot_r" })
        {
            int i = rig.IndexOf(name);
            Assert.Equal(before[i].Rotation, pose.Local[i].Rotation);
        }
    }

    [Fact]
    public void UnreachableTarget_ReturnsClosestPoint_WithHonestMiss()
    {
        var rig = SkeletonExamples.Biped();
        var pose = rig.CreatePose(); pose.SetToDefault();
        int tip = rig.IndexOf("arm_r_lower");   // hand

        // Way beyond arm reach: far forward of the shoulder.
        Vector2 target = Tip(rig, pose, "chest") + new Vector2(200f, 0f);
        var r = PoseIk.Solve(rig, pose, tip, target, PoseIk.DefaultChain(rig, tip));

        Assert.True(r.Miss > 100f, $"claimed miss {r.Miss} for an impossible target");
        // Achieved must agree with the pose the solve left behind.
        Assert.True((Tip(rig, pose, "arm_r_lower") - r.Achieved).Length() < 1e-3f);
        // And the arm should have stretched TOWARD the target (near-max forward reach):
        // hand well in front of the shoulder.
        Assert.True(r.Achieved.X > Tip(rig, pose, "chest").X + 5f,
                    $"hand at {r.Achieved} did not extend toward the target");
    }
}
