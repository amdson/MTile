using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;

namespace MTile.Tests;

// The 2.5D authoring layer: a bone's true length (Bone.TrueLength) as what Stretch measures,
// the per-key DepthSide, IK that respects (and in depth mode solves) Stretch, and the depth
// view's turn (PoseDepth) agreeing with a real 3D rotation.
public class PoseDepthTests
{
    // hip (root, 0) → chest (up, 16) → arm_upper (10) → arm_lower (10), the arm pointing
    // straight right. DefaultChain(arm_lower) = [arm_upper, arm_lower].
    private static Skeleton Arm(float? upperTrue = null)
    {
        var b = new SkeletonBuilder("arm");
        int hip   = b.AddRoot("hip", 0f, 0f);
        int chest = b.Add("chest", hip, -MathF.PI / 2f, 16f);
        int up    = b.Add("arm_upper", chest, MathF.PI / 2f, 10f, trueLength: upperTrue);
        b.Add("arm_lower", up, 0f, 10f);
        return b.Build();
    }

    private static Vector2 Tip(SkeletonPose pose, int bone) => pose.ComputeWorld(Affine2.Identity)[bone].Translation;

    [Fact]
    public void TrueLength_IsWhatStretchMeasures_AndNullStretchIsTheDrawnLength()
    {
        var rig = Arm(upperTrue: 12.5f);
        int up = rig.IndexOf("arm_upper");
        var pose = rig.CreatePose();

        PoseData.Apply(new List<PoseBoneEntry> { new() { Bone = "arm_upper", Rotation = MathF.PI / 2f } }, pose);
        Assert.Equal(10f, pose.Local[up].Translation.X, 4);            // unstretched = drawn

        PoseData.Apply(new List<PoseBoneEntry> { new() { Bone = "arm_upper", Rotation = MathF.PI / 2f, Stretch = 0.6f } }, pose);
        Assert.Equal(7.5f, pose.Local[up].Translation.X, 4);           // 0.6 × true 12.5

        var back = PoseData.Capture(pose).Find(e => e.Bone == "arm_upper");
        Assert.Equal(0.6f, back.Stretch.Value, 4);

        pose.Local[up].Translation = Vector2.UnitX * 10f;              // back at the drawn length
        Assert.Null(PoseData.Capture(pose).Find(e => e.Bone == "arm_upper").Stretch);
    }

    [Fact]
    public void TrueLength_RoundTripsThroughTheRigFile_AndSurvivesResize()
    {
        var rig = Arm(upperTrue: 12.5f);
        string dir = Path.Combine(Path.GetTempPath(), "mtile-posedepth-" + Guid.NewGuid().ToString("N"));
        try
        {
            SkeletonStore.Save(SkeletonStore.Capture("arm", rig), dir);
            var loaded = SkeletonStore.Load(dir, "arm");
            Assert.Equal(12.5f, loaded.Bones[loaded.IndexOf("arm_upper")].TrueLength);
            Assert.Null(loaded.Bones[loaded.IndexOf("arm_lower")].TrueLength);
            Assert.DoesNotContain("TrueLength\": null", File.ReadAllText(Path.Combine(dir, "arm.json")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }

        var resized = rig.Bones[rig.IndexOf("arm_upper")].WithLength(20f);
        Assert.Equal(25f, resized.TrueLength.Value, 4);                 // rest view angle kept
    }

    [Fact]
    public void DepthSide_SurvivesRecapture_AndClone()
    {
        var rig = Arm();
        var pose = rig.CreatePose();
        var key = new List<PoseBoneEntry> { new() { Bone = "arm_upper", Rotation = MathF.PI / 2f, Stretch = 0.5f, DepthSide = -1 } };
        PoseData.Apply(key, pose);
        Assert.Null(PoseData.Capture(pose).Find(e => e.Bone == "arm_upper").DepthSide);
        Assert.Equal(-1, PoseData.Capture(pose, key).Find(e => e.Bone == "arm_upper").DepthSide);
        Assert.Equal(-1, key[0].Clone().DepthSide);
    }

    [Fact]
    public void Solve_KeepsTheKeysStretch()
    {
        var rig = Arm();
        int up = rig.IndexOf("arm_upper"), lo = rig.IndexOf("arm_lower");
        var pose = rig.CreatePose();
        pose.Local[up].Translation = Vector2.UnitX * 6f;               // a foreshortened upper arm
        PoseIk.Solve(rig, pose, lo, Tip(pose, lo) + new Vector2(-2f, 4f), PoseIk.DefaultChain(rig, lo));
        Assert.Equal(6f, pose.Local[up].Translation.X, 4);
    }

    [Fact]
    public void Drag_HoldsANearlyEndOnBone()
    {
        var rig = Arm();
        int up = rig.IndexOf("arm_upper"), lo = rig.IndexOf("arm_lower");
        var pose = rig.CreatePose();
        pose.Local[up].Translation = Vector2.UnitX * 1f;               // s = 0.1: pointing at the camera
        float rot0 = pose.Local[up].Rotation;
        var drag = new PoseIk.DragSession(rig, pose, lo);
        for (int f = 0; f < 10; f++) drag.Step(pose, Tip(pose, lo) + new Vector2(0f, 3f));
        Assert.Equal(rot0, pose.Local[up].Rotation, 5);
    }

    [Fact]
    public void DepthDrag_ForeshortensWherePlainDragBends()
    {
        var rig = Arm();
        int up = rig.IndexOf("arm_upper"), lo = rig.IndexOf("arm_lower");
        Vector2 shoulder = Tip(rig.CreatePose(), rig.IndexOf("chest"));
        Vector2 target = shoulder + new Vector2(14f, 0f);              // pull the hand 6 units in
        // A slight bend to start: a dead-straight arm is singular for a plain reach-in (no
        // first-order way to shorten), which would make the comparison about that instead.
        SkeletonPose Start() { var p = rig.CreatePose(); p.Local[up].Rotation -= 0.1f; p.Local[lo].Rotation = 0.2f; return p; }

        var plain = Start();
        var pd = new PoseIk.DragSession(rig, plain, lo);
        for (int f = 0; f < 20; f++) pd.Step(plain, target);
        Assert.Equal(10f, plain.Local[up].Translation.X, 4);           // plain: lengths untouched...
        Assert.Equal(10f, plain.Local[lo].Translation.X, 4);
        Assert.True(MathF.Abs(plain.Local[lo].Rotation) > 0.5f);       // ...so the elbow folds

        var depth = Start();
        var dd = new PoseIk.DragSession(rig, depth, lo, solveStretch: true);
        for (int f = 0; f < 20; f++) dd.Step(depth, target);
        Assert.True(dd.Last.Miss < 0.1f, $"miss {dd.Last.Miss}");
        float s = (depth.Local[up].Translation.X + depth.Local[lo].Translation.X) / 20f;
        Assert.True(s < 0.8f, $"mean stretch {s}");
        Assert.True(MathF.Abs(depth.Local[lo].Rotation) < 0.5f * MathF.Abs(plain.Local[lo].Rotation),
                    $"depth bend {depth.Local[lo].Rotation} vs plain {plain.Local[lo].Rotation}");
    }

    [Theory]
    [InlineData(0.7f)]
    [InlineData(-0.9f)]
    [InlineData(2.4f)]
    [InlineData(-2.8f)]
    public void Turn_ReadsBackItsTilt(float tilt)
    {
        var rig = Arm();
        int up = rig.IndexOf("arm_upper");
        var pose = rig.CreatePose();
        var sides = new int?[rig.Count];
        PoseDepth.Turn(pose, up, tilt, false, sides);
        Assert.Equal(tilt, PoseDepth.Tilt(pose, up, sides[up]), 4);
        Assert.Equal(MathF.Cos(tilt), PoseDepth.StretchOf(pose, up), 4);
    }

    [Fact]
    public void TurnWithChildren_IsARigid3DRotationOfTheLimb()
    {
        var rig = Arm();
        int up = rig.IndexOf("arm_upper"), lo = rig.IndexOf("arm_lower"), chest = rig.IndexOf("chest");
        var pose = rig.CreatePose();
        var sides = new int?[rig.Count];
        // A bent, already-tilted forearm, so the turn has something nontrivial to carry.
        pose.Local[lo].Rotation = 0.9f;
        pose.Local[lo].Translation = Vector2.UnitX * 7f; sides[lo] = -1;

        var before = PoseDepth.Joints(pose, sides);
        float delta = 0.8f;
        PoseDepth.Turn(pose, up, PoseDepth.Tilt(pose, up, sides[up]) + delta, true, sides);
        var after = PoseDepth.Joints(pose, sides);

        // Expected: rotate each limb joint about the shoulder by delta in the (arm axis, z)
        // plane — the arm points along +x here, so that is a rotation about the y axis.
        Vector3 S = before[chest];
        Vector3 Rot(Vector3 p)
        {
            Vector3 r = p - S;
            float c = MathF.Cos(delta), s = MathF.Sin(delta);
            return S + new Vector3(r.X * c - r.Z * s, r.Y, r.X * s + r.Z * c);
        }
        foreach (int j in new[] { up, lo })
        {
            Vector3 want = Rot(before[j]);
            Assert.True(Vector3.Distance(want, after[j]) < 1e-3f, $"joint {rig.Bones[j].Name}: want {want}, got {after[j]}");
        }
        Assert.Equal(before[chest], after[chest]);
    }

    [Fact]
    public void TurnAlone_LeavesTheChildsOwnForeshortening()
    {
        var rig = Arm();
        int up = rig.IndexOf("arm_upper"), lo = rig.IndexOf("arm_lower");
        var pose = rig.CreatePose();
        var sides = new int?[rig.Count];
        pose.Local[lo].Translation = Vector2.UnitX * 7f; sides[lo] = -1;
        float rot = pose.Local[lo].Rotation;
        PoseDepth.Turn(pose, up, 1.0f, false, sides);
        Assert.Equal(7f, pose.Local[lo].Translation.X, 5);
        Assert.Equal(rot, pose.Local[lo].Rotation, 5);
        Assert.Equal(-1, sides[lo]);
    }
}
