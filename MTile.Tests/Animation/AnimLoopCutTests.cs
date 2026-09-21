using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests;

// ClipLoopCut (Animation/ClipLoopCut.cs): deriving a runtime loop from a clip's best
// self-transition. The fixture is a leg swinging through ONE AND A HALF sine cycles over the
// authored timeline — the clip does not loop, but any two-thirds of it does — with a
// body_path ramp and a contact span, so the cut's displacement and contacts are checkable.
public class AnimLoopCutTests
{
    private static Skeleton Rig()
    {
        var b = new SkeletonBuilder("tiny");
        int hip = b.AddRoot("hip", 0f, 4f);
        b.Add("leg", hip, MathHelper.PiOver2, 10f);
        b.AddPoint(new NamedPoint { Id = "support", Bone = "leg", Role = "support" });
        return b.Build();
    }

    private const float Cycles = 1.5f;           // sine cycles across the authored timeline
    private const float PathSpeed = 30f;         // body_path rig units per authored timeline

    private static AnimationKeyframe Key(float t) => new()
    {
        Time = t,
        Bones = new List<PoseBoneEntry> { new() { Bone = "leg", Rotation = 0.6f * MathF.Sin(MathHelper.TwoPi * Cycles * t) } },
        Additions = new List<AnimAddition>
        {
            new() { Name = "com",       Kind = AnimAdditionKind.Point, Px = 0f,           Py = -12f },
            new() { Name = "body_path", Kind = AnimAdditionKind.Point, Px = PathSpeed * t, Py = 0f },
        },
    };

    private static AnimationDocument Clip(ContactSpan[] contacts = null)
    {
        var keys = new List<AnimationKeyframe>();
        const int K = 12;
        for (int k = 0; k <= K; k++) keys.Add(Key(k / (float)K));
        return new AnimationDocument
        {
            Name = "swing", Type = "StepUp", Skeleton = "tiny", Loop = false, Duration = 1.2f,
            Keyframes = keys,
            Contacts = contacts == null ? null : new List<ContactSpan>(contacts),
        };
    }

    private static void SampleAt(AnimationDocument doc, Skeleton rig, float t, SkeletonPose dst)
    {
        var a = rig.CreatePose(); var b = rig.CreatePose(); var c = rig.CreatePose(); var d = rig.CreatePose();
        AnimationSampler.SampleSmooth(doc, t, a, b, c, d, dst);
    }

    [Fact]
    public void FindsAWholeSineCycle_AsTheLoop()
    {
        var rig = Rig();
        Assert.True(ClipLoopCut.TryFind(Clip(), rig, null, out var cut, out string err), err);
        float len = cut.FromPhase - cut.ToPhase;
        Assert.Equal(1f / Cycles, len, 1);   // one period: 2/3 of the clip, to sample resolution
        Assert.True(cut.Cost < 0.05f, $"cost {cut.Cost}");
    }

    [Fact]
    public void Cut_IsCyclic_ContinuousAcrossTheSeam_AndCarriesTheDisplacement()
    {
        var rig = Rig();
        Assert.True(ClipLoopCut.TryApply(Clip(), rig, null, out var loop, out var cut, out string err), err);
        float len = cut.FromPhase - cut.ToPhase;

        Assert.True(loop.Loop);
        Assert.True(loop.Keyframes.Count >= 3);
        Assert.Equal(0f, loop.Keyframes[0].Time);
        Assert.Equal(1f, loop.Keyframes[^1].Time);
        Assert.Equal(1.2f * len, loop.Duration, 4);

        // First and last keys share a pose — what makes the sampler treat it as cyclic.
        var first = loop.Keyframes[0].Bones; var last = loop.Keyframes[^1].Bones;
        Assert.Equal(first.Count, last.Count);
        for (int i = 0; i < first.Count; i++) Assert.Equal(first[i].Rotation, last[i].Rotation, 5);

        // The seam samples continuously.
        var p0 = rig.CreatePose(); var p1 = rig.CreatePose();
        SampleAt(loop, rig, 0.002f, p0); SampleAt(loop, rig, 0.998f, p1);
        int leg = rig.IndexOf("leg");
        Assert.True(MathF.Abs(MathHelper.WrapAngle(p0.Local[leg].Rotation - p1.Local[leg].Rotation)) < 0.05f,
                    $"seam gap {p0.Local[leg].Rotation - p1.Local[leg].Rotation}");

        // body_path: p(1) − p(0) is the authored travel over the loop; com is seam-continuous.
        Assert.True(BodyPath.TryCycleDisplacement(loop, out var disp));
        Assert.Equal(PathSpeed * len, disp.X, 2);
        Assert.True(BodyPath.TrySampleAnchor(loop, 0f, out var c0, out _));
        Assert.True(BodyPath.TrySampleAnchor(loop, 1f, out var c1, out _));
        Assert.Equal(c0.Y, c1.Y, 4);
    }

    [Fact]
    public void Contacts_AreCroppedAndRetimed_AndTheLoopKeepsAStancePerPoint()
    {
        var rig = Rig();
        // Planted over the sine's negative half in the middle of the clip.
        var doc = Clip(new[] { new ContactSpan { Point = "support", Start = 1f / 3f, End = 2f / 3f } });
        Assert.True(ClipLoopCut.TryApply(doc, rig, null, out var loop, out var cut, out string err), err);
        var span = Assert.Single(loop.Contacts);
        Assert.True(span.Start >= 0f && span.End <= 1f + 1e-4f, $"span [{span.Start}, {span.End})");
        float len = cut.FromPhase - cut.ToPhase;
        Assert.Equal((1f / 3f) / len, span.End - span.Start, 2);
        Assert.True(ClipStrideTrack.TryCompile(loop, rig, out var track, out string serr), serr);
        Assert.Single(track.Feet);
        Assert.Single(track.Feet[0].Stances);
    }

    [Fact]
    public void Cut_MergesASpanAcrossTheSeam()
    {
        var rig = Rig();
        var doc = Clip(new[]
        {
            new ContactSpan { Point = "support", Start = 0.20f, End = 0.40f },
            new ContactSpan { Point = "support", Start = 0.60f, End = 0.90f },
        });
        // Loop [0.25, 0.75): the first span survives as [0, 0.3), the second as [0.7, 1) —
        // one stance straddling the seam, so they merge into [0.7, 1.3).
        var loop = ClipLoopCut.Cut(doc, rig, 0.25f, 0.75f);
        var span = Assert.Single(loop.Contacts);
        Assert.Equal(0.7f, span.Start, 3);
        Assert.Equal(1.3f, span.End, 3);
    }

    [Fact]
    public void MinLoop_LongerThanAnyMatch_Fails_WithAReason()
    {
        var rig = Rig();
        var ok = ClipLoopCut.TryFind(Clip(), rig, new LoopCutOptions { MinLoop = 0.95f, MaxCost = 0.01f },
                                     out _, out string err);
        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(err));
    }
}
