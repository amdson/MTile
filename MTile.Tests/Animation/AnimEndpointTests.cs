using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;

namespace MTile.Tests;

// The endpoint resolver (Plans/ANIMATION_SCENE_AUTHORING_PLAN.md "Endpoint data contract";
// workplan chunk 4): legacy node labels keep resolving to the exact bone tip, named points
// on the rig and the clip resolve by id with clip precedence, conflicting records are
// rejected, endpoint positions agree with the rendered chain, rig points persist, and a
// point-labeled contact drives the solver exactly like the node-labeled one it replaces.
public class AnimEndpointTests
{
    private static Skeleton TinyRig(params NamedPoint[] points)
    {
        var b = new SkeletonBuilder("tiny");
        int hip = b.AddRoot("hip", 0f, 0f);
        int leg = b.Add("leg_l_lower", hip, MathHelper.PiOver2, 10f);
        b.Add("foot_l", leg, -1.3f, 0.25f);
        foreach (var p in points) b.AddPoint(p);
        return b.Build();
    }

    [Fact]
    public void LegacyNode_ResolvesToTheBoneTip_AndPointsTakePrecedenceFromTheClip()
    {
        var rig = TinyRig(new NamedPoint { Id = "support_l", Bone = "leg_l_lower", Role = "support" });
        Assert.True(EndpointResolver.TryResolve(rig, null, new ContactLabel { Node = "foot_l" }, out var legacy));
        Assert.Equal(rig.IndexOf("foot_l"), legacy.Bone); Assert.True(legacy.IsExactTip);

        Assert.True(EndpointResolver.TryResolve(rig, null, new ContactLabel { Point = "support_l" }, out var rp));
        Assert.Equal(rig.IndexOf("leg_l_lower"), rp.Bone); Assert.True(rp.IsExactTip);

        // A clip point shadows the rig's by id; a bare bone name resolves as its End.
        var clip = new AnimationDocument { Points = new List<NamedPoint> { new() { Id = "support_l", Bone = "foot_l" } } };
        Assert.True(EndpointResolver.TryResolvePoint(rig, clip, "support_l", out var cp));
        Assert.Equal(rig.IndexOf("foot_l"), cp.Bone);
        Assert.True(EndpointResolver.TryResolvePoint(rig, null, "leg_l_lower", out var bare));
        Assert.Equal(BoneEnd.End, bare.End);

        // Both fields, agreeing: fine. Disagreeing: rejected. Unknown: rejected.
        Assert.True(EndpointResolver.TryResolve(rig, null, new ContactLabel { Node = "leg_l_lower", Point = "support_l" }, out _));
        Assert.False(EndpointResolver.TryResolve(rig, null, new ContactLabel { Node = "foot_l", Point = "support_l" }, out _));
        Assert.False(EndpointResolver.TryResolve(rig, null, new ContactLabel { Node = "flipper" }, out _));
        Assert.Equal(-1, EndpointResolver.BoneOf(rig, null, new ContactLabel { Point = "nope" }));
    }

    [Fact]
    public void World_EndStartAndOffset_AgreeWithTheRenderedChain()
    {
        var rig = TinyRig();
        var pose = rig.CreatePose();
        var root = Affine2.FromTRS(new Vector2(100f, 50f), 0f, new Vector2(-2f, 2f));   // facing left, scale 2
        var world = pose.ComputeWorld(root);
        int leg = rig.IndexOf("leg_l_lower");
        var end = new ResolvedPoint("e", leg, BoneEnd.End, Vector2.Zero);
        var start = new ResolvedPoint("s", leg, BoneEnd.Start, Vector2.Zero);
        Assert.Equal(world[leg].Translation, EndpointResolver.World(world, root, rig, end));
        Assert.Equal(world[rig.IndexOf("hip")].Translation, EndpointResolver.World(world, root, rig, start));
        // The root bone's Start is the root origin.
        var hipStart = new ResolvedPoint("h", rig.IndexOf("hip"), BoneEnd.Start, Vector2.Zero);
        Assert.Equal(new Vector2(100f, 50f), EndpointResolver.World(world, root, rig, hipStart));
        // An offset rides the bone's frame: +X along the bone, scaled and facing-flipped.
        var off = new ResolvedPoint("o", leg, BoneEnd.End, new Vector2(1f, 0f));
        Vector2 along = world[leg].TransformPoint(new Vector2(1f, 0f));
        Assert.Equal(along, EndpointResolver.World(world, root, rig, off));
        Assert.Equal(2f, Vector2.Distance(along, world[leg].Translation), 3);
    }

    [Fact]
    public void RigPoints_RoundTripThroughTheSkeletonStore_AndSurviveComposition()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mtile_pts_" + Guid.NewGuid().ToString("N"));
        try
        {
            var rig = TinyRig(new NamedPoint { Id = "support_l", Bone = "leg_l_lower", Role = "support", Label = "left sole" });
            SkeletonStore.Save(SkeletonStore.Capture("tiny", rig), dir);
            var back = SkeletonStore.Load(dir, "tiny");
            Assert.Single(back.Points);
            Assert.Equal("support", back.Points[0].Role);
            Assert.Equal("left sole", back.Points[0].Label);
            var composed = SkeletonComposition.Compose(back, new[] { new SkeletonBoneRecord { Name = "knife", Parent = "hip", Length = 0f } });
            Assert.Single(composed.Points);
            Assert.True(composed.IndexOf("knife") >= 0);
            // A point naming an unknown bone fails the load loudly.
            var bad = SkeletonStore.Capture("bad", rig); bad.Points[0].Bone = "nope";
            SkeletonStore.Save(bad, dir);
            Assert.Null(SkeletonStore.Load(dir, "bad"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // The same run, once with its point labels (support_l/support_r) and once with the labels
    // rewritten to legacy node names on the same bones: identical solver behavior.
    [Fact]
    public void PointLabeledContacts_DriveTheSolverLikeNodeLabels()
    {
        var clips = AnimationStore.LoadAll(Path.Combine(FindDir("SkeletonStates"), "biped"));
        var baseRig = SkeletonExamples.Load("biped");
        var run = clips.Find(c => c.Name == "run");
        Assert.NotNull(run);

        var rig = baseRig;   // its support_l/support_r points sit at the lower legs' ends
        var relabeled = new AnimationDocument
        {
            Name = run.Name, Type = run.Type, Skeleton = run.Skeleton, Duration = run.Duration, Loop = run.Loop,
        };
        foreach (var kf in run.Keyframes)
        {
            var copy = new AnimationKeyframe { Time = kf.Time, Bones = kf.Bones, Additions = kf.Additions };
            if (kf.Contacts != null)
            {
                copy.Contacts = new List<ContactLabel>();
                foreach (var l in kf.Contacts)
                {
                    // The shipped clip carries point labels; the relabeled copy names the bones.
                    Assert.True(EndpointResolver.TryResolve(rig, run, l, out var rp), $"unresolved label {l.Key}");
                    copy.Contacts.Add(new ContactLabel { Node = rig.Bones[rp.Bone].Name, Weight = l.Weight, Source = l.Source });
                }
            }
            relabeled.Keyframes.Add(copy);
        }

        float[] Trace(Skeleton r, AnimationDocument clip)
        {
            var anim = new CharacterAnimator(r, 0.6f, new[] { clip });
            var outp = new List<float>();
            float x = 0f;
            for (int i = 0; i < 90; i++)
            {
                x += 90f / 60f;
                anim.Update(new CharacterAnimSample(new Vector2(x, 0f), new Vector2(90f, 0f), +1, true, "WalkState", "", 1f / 60f));
                outp.Add(anim.State.Phase); outp.Add(anim.VerticalOffset); outp.Add(anim.ContactCount);
                for (int b = 0; b < anim.Skeleton.Count; b++) outp.Add(anim.AngleCorrection(b));
            }
            return outp.ToArray();
        }
        var a = Trace(baseRig, run);
        var b = Trace(rig, relabeled);
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++) Assert.True(a[i] == b[i], $"diverged at sample {i}: {a[i]} vs {b[i]}");
    }

    private static string FindDir(string name)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            string c = Path.Combine(d.FullName, name);
            if (Directory.Exists(c)) return c;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException(name);
    }
}
