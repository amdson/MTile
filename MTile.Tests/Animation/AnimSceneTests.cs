using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;

namespace MTile.Tests;

// Scene guides + motion intent (Plans/ANIMATION_SCENE_AUTHORING_PLAN.md; workplan chunk 3):
// the document additions round-trip and stay optional, the shared motion query resolves the
// documented precedence, loop extension samples the endpoint explicitly, and the guide edit
// operations behave (hit tests, resize floors, snapping, duplication).
public class AnimSceneTests
{
    // The shared query reproduces the editor's former private placement formulas on the
    // shipped clips: root = p(t) − com(t) with p from the arc (mapped px → rig units through
    // the arc's own anchors at ArcProgress) or the body_path track, else zero.
    [Theory]
    [InlineData("biped", "parkour")]          // ReferenceArc + com
    [InlineData("biped_rabbit", "crouchwalk")] // body_path track + com
    [InlineData("biped", "walk")]             // com only (stationary, legacy)
    public void RootAt_MatchesTheLegacyEditorPlacement(string rig, string clipName)
    {
        var clip = AnimationStore.LoadAll(Path.Combine(FindDir("SkeletonStates"), rig)).Find(d => d.Name == clipName);
        Assert.True(clip != null, $"{rig}/{clipName}.json not found");
        HermiteClipDocument arc = null;
        if (clip.ReferenceArc != null)
        {
            string path = Path.Combine(Path.GetDirectoryName(FindDir("SkeletonStates"))!, "ReferenceClips", clip.ReferenceArc + ".json");
            arc = (File.Exists(path) ? HermiteClipDocument.Load(path) : null) ?? ReferenceClipRegistry.Get(clip.ReferenceArc);
            Assert.NotNull(arc);
        }
        var m = ClipMotion.Resolve(clip, _ => arc);
        for (int i = 0; i <= 20; i++)
        {
            float t = i / 20f;
            // The legacy formulas, inline.
            Vector2 refOff;
            if (arc != null)
            {
                float arcDur = arc.Duration <= 1e-4f ? 1f : arc.Duration, clipDur = clip.Duration <= 1e-4f ? 1f : clip.Duration;
                float u = MathHelper.Clamp(MathHelper.Clamp(t, 0f, 1f) * (clipDur / arcDur), 0f, 2f);
                refOff = new ReferenceFrame(arc, Vector2.Zero, arc.Span / Game1.SkeletonScale).Map(arc.Eval(u));
            }
            else refOff = AnimAdditionSampler.SamplePoint(clip, t, BodyPath.ChannelName, out var r) ? r : Vector2.Zero;
            bool comAnchored = AnimAdditionSampler.SamplePoint(clip, t, "com", out var com);
            Vector2 legacyRoot = comAnchored ? refOff - com : refOff;

            Vector2 root = m.RootAt(t, out bool anchored);
            Assert.Equal(comAnchored, anchored);
            Assert.True((root - legacyRoot).Length() < 1e-4f, $"{clipName} t={t:0.00}: {root} vs legacy {legacyRoot}");
        }
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

    private static AnimationDocument Doc(bool loop = true) => new()
    {
        Name = "t", Type = "Misc", Skeleton = "biped", Loop = loop, Duration = 1f,
        Keyframes = new List<AnimationKeyframe>
        {
            new() { Time = 0f, Bones = new List<PoseBoneEntry>() },
            new() { Time = 1f, Bones = new List<PoseBoneEntry>() },
        },
    };

    private static void SetPath(AnimationDocument d, int key, float x, float y)
    {
        var k = d.Keyframes[key];
        k.Additions ??= new List<AnimAddition>();
        k.Additions.Add(new AnimAddition { Name = BodyPath.ChannelName, Kind = AnimAdditionKind.Point, Px = x, Py = y });
    }

    [Fact]
    public void SceneAndMotion_RoundTrip_AndStayOptional()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mtile_scene_" + Guid.NewGuid().ToString("N"));
        try
        {
            var d = Doc();
            d.Motion = MotionSource.InPlace;
            d.Scene = new ClipScene();
            SceneGuideOps.AddGround(d.Scene, 40f).Label = "floor";
            var b = SceneGuideOps.AddBlock(d.Scene, 18f, 22f, 18f, 18f); b.Locked = true;
            AnimationStore.Save(d, dir);
            var back = AnimationStore.LoadAll(dir)[0];
            Assert.Equal(MotionSource.InPlace, back.Motion);
            Assert.NotNull(back.Scene);
            Assert.Equal(2, back.Scene.Guides.Count);
            Assert.Equal(SceneGuideKind.Ground, back.Scene.Guides[0].Kind);
            Assert.Equal("floor", back.Scene.Guides[0].Label);
            Assert.True(back.Scene.Guides[1].Locked);
            Assert.Equal(b.Id, back.Scene.Guides[1].Id);

            // An explicitly empty scene survives as empty (≠ missing).
            var e = Doc(); e.Name = "empty"; e.Scene = new ClipScene();
            AnimationStore.Save(e, dir);
            var backE = AnimationStore.LoadAll(dir).Find(x => x.Name == "empty");
            Assert.NotNull(backE.Scene); Assert.Empty(backE.Scene.Guides);
            Assert.Null(backE.Motion);

            // A legacy clip (no fields) loads with both absent and saves without them.
            var l = Doc(); l.Name = "legacy";
            AnimationStore.Save(l, dir);
            string json = File.ReadAllText(l.FilePath);
            Assert.DoesNotContain("Scene", json); Assert.DoesNotContain("Motion", json);
            Assert.Null(AnimationStore.LoadAll(dir).Find(x => x.Name == "legacy").Scene);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Motion_LegacyPrecedence_ArcThenTrackThenStationary()
    {
        var plain = ClipMotion.Resolve(Doc(), _ => null);
        Assert.Equal(MotionSource.InPlace, plain.Source);
        Assert.False(plain.HasIntent);
        Assert.Equal(Vector2.Zero, plain.BodyAt(0.5f));

        var tracked = Doc(); SetPath(tracked, 0, 0f, 0f); SetPath(tracked, 1, 30f, -4f);
        var m = ClipMotion.Resolve(tracked, _ => null);
        Assert.Equal(MotionSource.Track, m.Source);
        Assert.True(m.HasIntent);
        Assert.Equal(new Vector2(30f, -4f), m.CycleDisplacement);

        var arc = new HermiteClipDocument { Duration = 1f };
        arc.Keys.Add(new HermiteClipKey { T = 0f, X = 0f, Y = 0f, TX = 10f, TY = 0f });
        arc.Keys.Add(new HermiteClipKey { T = 1f, X = 10f, Y = -5f, TX = 10f, TY = 0f });
        var arced = Doc(); arced.ReferenceArc = "test"; SetPath(arced, 1, 30f, -4f);   // arc wins over the track
        var a = ClipMotion.Resolve(arced, n => n == "test" ? arc : null);
        Assert.Equal(MotionSource.ReferenceArc, a.Source);
        Assert.False(a.ArcMissing);
        Assert.NotEqual(Vector2.Zero, a.BodyAt(1f));

        // A named arc nobody can resolve is reported, not silently replaced by the track.
        var missing = ClipMotion.Resolve(arced, _ => null);
        Assert.True(missing.ArcMissing);
        Assert.Equal(Vector2.Zero, missing.BodyAt(1f));
    }

    [Fact]
    public void Motion_ExplicitDeclaration_OverridesTheLegacyPrecedence()
    {
        var d = Doc(); SetPath(d, 0, 0f, 0f); SetPath(d, 1, 30f, 0f); d.ReferenceArc = "x";
        d.Motion = MotionSource.InPlace;
        var m = ClipMotion.Resolve(d, _ => null);
        Assert.Equal(MotionSource.InPlace, m.Source);
        Assert.True(m.HasIntent);                       // stationary ON PURPOSE ≠ missing intent
        Assert.Equal(Vector2.Zero, m.CycleDisplacement);
        d.Motion = MotionSource.Track;
        Assert.Equal(new Vector2(30f, 0f), ClipMotion.Resolve(d, _ => null).CycleDisplacement);
    }

    [Fact]
    public void LoopExtension_AddsWholeCycles_OneShotClamps()
    {
        var d = Doc(); SetPath(d, 0, 0f, 0f); SetPath(d, 1, 20f, 0f);
        var m = ClipMotion.Resolve(d, _ => null);
        Assert.Equal(m.BodyAt(0.25f) + new Vector2(40f, 0f), m.ExtendedBodyAt(2.25f));
        Assert.Equal(new Vector2(20f, 0f), m.BodyAt(1f));   // the endpoint is sampled, not wrapped to 0
        var one = Doc(loop: false); SetPath(one, 0, 0f, 0f); SetPath(one, 1, 20f, 0f);
        Assert.Equal(new Vector2(20f, 0f), ClipMotion.Resolve(one, _ => null).ExtendedBodyAt(2.25f));
    }

    [Fact]
    public void Guides_HitTest_Drag_Snap_Duplicate()
    {
        var s = SceneGuideOps.Legacy(groundY: 40f, withBlock: true, blockOffset: Vector2.Zero, tileRig: 18f);
        var floor = s.Guides[0]; var block = s.Guides[1];
        Assert.Equal(new Vector2(18f, 22f), new Vector2(block.X, block.Y));

        Assert.Equal((floor, GuidePart.GroundLine), SceneGuideOps.HitTest(s, new Vector2(-5f, 41f), 2f));
        Assert.Equal((block, GuidePart.Body),       SceneGuideOps.HitTest(s, new Vector2(27f, 31f), 2f));
        Assert.Equal((block, GuidePart.TopRight),   SceneGuideOps.HitTest(s, new Vector2(35.5f, 22.5f), 2f));
        Assert.Equal((block, GuidePart.Left),       SceneGuideOps.HitTest(s, new Vector2(18.5f, 30f), 2f));
        Assert.Null(SceneGuideOps.HitTest(s, new Vector2(80f, 10f), 2f).guide);

        // A hidden guide is not hit; a locked one is hit but ignores drags.
        block.Hidden = true;
        Assert.Null(SceneGuideOps.HitTest(s, new Vector2(27f, 31f), 2f).guide);
        block.Hidden = false; block.Locked = true;
        SceneGuideOps.Drag(block, GuidePart.Body, new Vector2(5f, 0f));
        Assert.Equal(18f, block.X);
        block.Locked = false;

        // Resizing never inverts: dragging the left edge past the right stops at the min size.
        SceneGuideOps.Drag(block, GuidePart.Left, new Vector2(100f, 0f));
        Assert.Equal(SceneGuideOps.MinBlockSize, block.W, 3);
        Assert.Equal(18f + 18f - SceneGuideOps.MinBlockSize, block.X, 3);
        SceneGuideOps.Drag(block, GuidePart.BottomRight, new Vector2(3f, 4f));
        Assert.Equal(SceneGuideOps.MinBlockSize + 3f, block.W, 3);
        Assert.Equal(22f, block.H, 3);

        // Snapping lands on the tile grid anchored at the ground line.
        block.X = 20.4f; block.W = 17f; block.Y = 21.1f; block.H = 19f;
        SceneGuideOps.SnapToGrid(block, 18f, 40f);
        Assert.Equal(18f, block.X); Assert.Equal(18f, block.W); Assert.Equal(22f, block.Y); Assert.Equal(18f, block.H);

        var dup = SceneGuideOps.Duplicate(s, block, new Vector2(18f, 0f));
        Assert.NotEqual(block.Id, dup.Id);
        Assert.Equal(36f, dup.X);
        Assert.Equal(3, s.Guides.Count);
        Assert.True(SceneGuideOps.Remove(s, dup));
        Assert.Equal(2, s.Guides.Count);
    }
}
