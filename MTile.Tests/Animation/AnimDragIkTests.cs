using System;
using System.IO;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;

namespace MTile.Tests;

// The editor's IK drag mode (workplan chunk 3.5, PoseIk.DragSession): a limb chain follows a
// reachable target, an unreachable one yields the closest pose plus a miss, error does not
// accumulate across a drag that returns home (prior A anchors the drag-start pose), the lower
// bone's bend keeps its drag-start side, and a keyframe's stretch is untouched.
public class AnimDragIkTests
{
    private static (Skeleton rig, SkeletonPose pose, int tip) ArmL()
    {
        var rig = SkeletonExamples.Load("biped");
        var pose = rig.CreatePose();
        int upper = rig.IndexOf("arm_l_upper"), lower = rig.IndexOf("arm_l_lower");
        pose.Local[upper].Rotation = 0.6f;
        pose.Local[lower].Rotation = 0.8f;   // a real bend, positive side
        return (rig, pose, lower);
    }

    private static Vector2 Tip(Skeleton rig, SkeletonPose pose, int b)
        => pose.ComputeWorld(Affine2.FromTRS(Vector2.Zero, 0f, Vector2.One))[b].Translation;

    [Fact]
    public void Drag_ReachesAReachableTarget_AndReportsTheMissOnAnUnreachableOne()
    {
        var (rig, pose, tip) = ArmL();
        var s = new PoseIk.DragSession(rig, pose, tip);
        Assert.Equal(new[] { "arm_l_upper", "arm_l_lower" }, Array.ConvertAll(s.Chain, i => rig.Bones[i].Name));
        // A target inside the reach disc: the shoulder-to-hand vector shortened and swung a little.
        Vector2 shoulder = Tip(rig, pose, rig.IndexOf("chest"));
        Vector2 arm = Tip(rig, pose, tip) - shoulder;
        Vector2 target = shoulder + Vector2.Transform(arm * 0.9f, Matrix.CreateRotationZ(0.3f));
        PoseIk.Result r = default;
        for (int i = 0; i < 4; i++) r = s.Step(pose, target);       // a few frames of holding still
        Assert.True(r.Miss < 0.05f, $"reachable target missed by {r.Miss}");
        Assert.True((Tip(rig, pose, tip) - target).Length() < 0.05f);

        var far = s.Step(pose, target + new Vector2(200f, 0f));    // way beyond the arm's reach
        Assert.True(far.Miss > 150f);
        Assert.False(float.IsNaN(pose.Local[tip].Rotation));
    }

    [Fact]
    public void Drag_ThatReturnsHome_LeavesThePoseWhereItStarted()
    {
        var (rig, pose, tip) = ArmL();
        int upper = rig.IndexOf("arm_l_upper");
        float u0 = pose.Local[upper].Rotation, l0 = pose.Local[tip].Rotation;
        Vector2 home = Tip(rig, pose, tip);
        var s = new PoseIk.DragSession(rig, pose, tip);
        // Wander around, then come back to the exact start point.
        foreach (var d in new[] { new Vector2(4f, 0f), new Vector2(4f, 4f), new Vector2(-3f, 5f), new Vector2(0f, -3f), Vector2.Zero })
            for (int i = 0; i < 3; i++) s.Step(pose, home + d);
        Assert.True(MathF.Abs(pose.Local[upper].Rotation - u0) < 0.03f, $"upper drifted {pose.Local[upper].Rotation - u0}");
        Assert.True(MathF.Abs(pose.Local[tip].Rotation - l0) < 0.03f, $"lower drifted {pose.Local[tip].Rotation - l0}");
        // Escape restores exactly.
        s.Step(pose, home + new Vector2(5f, 5f));
        s.Restore(pose);
        Assert.Equal(u0, pose.Local[upper].Rotation);
        Assert.Equal(l0, pose.Local[tip].Rotation);
    }

    [Fact]
    public void Drag_KeepsTheLowerBoneOnItsBendSide_AndPreservesStretch()
    {
        var (rig, pose, tip) = ArmL();
        pose.Local[tip].Translation = Vector2.UnitX * rig.Bones[tip].Length * 0.7f;   // a foreshortened keyframe
        var s = new PoseIk.DragSession(rig, pose, tip);
        Vector2 home = Tip(rig, pose, tip);
        // Pull the hand to points that a backward-bending elbow could reach more easily.
        foreach (var d in new[] { new Vector2(-6f, -6f), new Vector2(-9f, -2f), new Vector2(-4f, 6f) })
            for (int i = 0; i < 3; i++) s.Step(pose, home + d);
        Assert.True(pose.Local[tip].Rotation >= 0f, $"elbow bent backwards: {pose.Local[tip].Rotation}");
        Assert.Equal(rig.Bones[tip].Length * 0.7f, pose.Local[tip].Translation.X, 4);
    }
}
