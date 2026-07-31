using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;

namespace MTile.Tests;

// The per-keyframe Stretch channel (pseudo-3D bone-length distortion — pelvis yaw
// foreshortening the hip struts). Pins: Apply scales the local translation only (no
// subtree scale), Capture round-trips it, SampleSmooth interpolates it between keys,
// and the JSON stays clean of the property when unstretched (legacy round-trip).
public class PoseStretchTests
{
    [Fact]
    public void Apply_ScalesTranslation_NotSubtree()
    {
        var skel = SkeletonExamples.Biped();
        int upper = skel.IndexOf("leg_l_upper");
        int lower = skel.IndexOf("leg_l_lower");
        var pose = skel.CreatePose();

        var bones = PoseData.Capture(pose);
        bones.Find(b => b.Bone == "leg_l_upper").Stretch = 0.5f;
        PoseData.Apply(bones, pose);

        Assert.Equal(skel.Bones[upper].Length * 0.5f, pose.Local[upper].Translation.X, 3);
        Assert.Equal(Vector2.One, pose.Local[upper].Scale);
        // The child's own segment keeps full length: its tip sits Length away from
        // its (shifted) joint, i.e. the subtree translates but does not shrink.
        var w = pose.ComputeWorld(Affine2.Identity);
        float seg = (w[lower].Translation - w[upper].Translation).Length();
        Assert.Equal(skel.Bones[lower].Length, seg, 2);
    }

    [Fact]
    public void CaptureApply_RoundTripsStretch_AndOmitsUnit()
    {
        var skel = SkeletonExamples.Biped();
        var pose = skel.CreatePose();
        var bones = PoseData.Capture(pose);

        // Unstretched pose captures null Stretch on every bone → property absent in JSON
        // under the store's null-omitting policy (AnimationStore.Opts).
        Assert.All(bones, b => Assert.Null(b.Stretch));
        var opts = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        Assert.DoesNotContain("Stretch", JsonSerializer.Serialize(bones, opts));

        bones.Find(b => b.Bone == "leg_r_upper").Stretch = -0.2f;
        PoseData.Apply(bones, pose);
        var back = PoseData.Capture(pose);
        Assert.Equal(-0.2f, back.Find(b => b.Bone == "leg_r_upper").Stretch.Value, 3);
        Assert.Null(back.Find(b => b.Bone == "leg_l_upper").Stretch);
    }

    [Fact]
    public void SampleSmooth_InterpolatesStretch()
    {
        var skel = SkeletonExamples.Biped();
        int leg = skel.IndexOf("leg_l_upper");
        float len = skel.Bones[leg].Length;

        var clip = new AnimationDocument { Name = "s", Type = "Misc", Duration = 1f, Loop = false };
        var k0 = PoseData.Capture(skel.CreatePose());
        var k1 = PoseData.Capture(skel.CreatePose());
        k1.Find(b => b.Bone == "leg_l_upper").Stretch = 0.5f;
        clip.Keyframes.Add(new AnimationKeyframe { Time = 0f, Bones = k0 });
        clip.Keyframes.Add(new AnimationKeyframe { Time = 1f, Bones = k1 });

        var a = skel.CreatePose(); var b = skel.CreatePose();
        var c = skel.CreatePose(); var d = skel.CreatePose();
        var dest = skel.CreatePose();

        AnimationSampler.SampleSmooth(clip, 0.5f, a, b, c, d, dest);
        // Smoothstep weight at u=0.5 is exactly 0.5 → stretch 0.75.
        Assert.Equal(len * 0.75f, dest.Local[leg].Translation.X, 2);

        AnimationSampler.SampleSmooth(clip, 1f, a, b, c, d, dest);
        Assert.Equal(len * 0.5f, dest.Local[leg].Translation.X, 2);
    }
}
