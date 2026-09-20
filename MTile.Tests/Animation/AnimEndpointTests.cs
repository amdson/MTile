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
    public void ContactPoints_ResolveThroughTheClipThenTheRigThenTheBoneName()
    {
        var rig = TinyRig(new NamedPoint { Id = "support_l", Bone = "leg_l_lower", Role = "support" });
        Assert.True(EndpointResolver.TryResolvePoint(rig, null, "support_l", out var rp));
        Assert.Equal(rig.IndexOf("leg_l_lower"), rp.Bone); Assert.True(rp.IsExactTip);

        // A bare bone name resolves as that bone's End — the ONE remaining spelling of what
        // ContactLabel.Node used to mean.
        Assert.True(EndpointResolver.TryResolvePoint(rig, null, "foot_l", out var byBone));
        Assert.Equal(rig.IndexOf("foot_l"), byBone.Bone); Assert.True(byBone.IsExactTip);

        // A clip point shadows the rig's by id.
        var clip = new AnimationDocument { Points = new List<NamedPoint> { new() { Id = "support_l", Bone = "foot_l" } } };
        Assert.True(EndpointResolver.TryResolvePoint(rig, clip, "support_l", out var cp));
        Assert.Equal(rig.IndexOf("foot_l"), cp.Bone);
        Assert.True(EndpointResolver.TryResolvePoint(rig, null, "leg_l_lower", out var bare));
        Assert.Equal(BoneEnd.End, bare.End);

        Assert.False(EndpointResolver.TryResolvePoint(rig, null, "flipper", out _));
    }

    // A contact a consumer cannot honor is an ERROR, never a dropped one: the solver used to
    // skip both cases silently, so a typo'd id or an offset point just stopped planting a foot.
    [Fact]
    public void BoneOf_Throws_OnAnUnresolvableId_AndOnANonTipPoint()
    {
        var rig = TinyRig(
            new NamedPoint { Id = "support_l", Bone = "leg_l_lower", Role = "support" },
            new NamedPoint { Id = "ball_l",    Bone = "foot_l", Ox = 0.1f },       // offset ≠ exact tip
            new NamedPoint { Id = "ankle_l",   Bone = "foot_l", End = BoneEnd.Start });

        Assert.Equal(rig.IndexOf("leg_l_lower"), EndpointResolver.BoneOf(rig, null, "support_l"));
        Assert.Throws<InvalidOperationException>(() => EndpointResolver.BoneOf(rig, null, "nope"));
        Assert.Throws<InvalidOperationException>(() => EndpointResolver.BoneOf(rig, null, "ball_l"));
        Assert.Throws<InvalidOperationException>(() => EndpointResolver.BoneOf(rig, null, "ankle_l"));
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

    // The same run, once with its point labels (support_l/support_r) and once with those ids
    // replaced by the bare NAMES of the bones they resolve to: identical solver behavior.
    [Fact]
    public void PointLabeledContacts_DriveTheSolverLikeBoneNamedOnes()
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
            relabeled.Keyframes.Add(new AnimationKeyframe { Time = kf.Time, Bones = kf.Bones, Additions = kf.Additions });

        // Author the spans here rather than reading whatever the clip carries: the property
        // under test is that a POINT ID and the bare BONE NAME it resolves to drive the solver
        // identically, and that holds regardless of which contacts the file happens to have.
        run.Contacts = new List<ContactSpan>
        {
            new() { Point = "support_l", Start = 0.00f, End = 0.50f },
            new() { Point = "support_r", Start = 0.50f, End = 1.00f },
        };
        relabeled.Contacts = new List<ContactSpan>();
        foreach (var cs in run.Contacts)
        {
            Assert.True(EndpointResolver.TryResolvePoint(rig, run, cs.Point, out var rp), $"unresolved {cs.Point}");
            relabeled.Contacts.Add(new ContactSpan
            { Point = rig.Bones[rp.Bone].Name, Start = cs.Start, End = cs.End, Source = cs.Source });
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
