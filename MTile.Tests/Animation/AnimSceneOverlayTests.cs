using System.IO;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;

namespace MTile.Tests;

// PER-CLIP DISPLAY OVERLAYS (ClipScene.Overlays, Animation/SceneReferences.cs). The three
// properties the design rests on:
//   1. they round-trip with the clip, and a clip without one keeps writing no overlay field;
//   2. they carry NO geometry — the hover height is derived from the game's own constants, so
//      it cannot go stale when hover is retuned;
//   3. the bake/check pipeline never sees them, so toggling a visual cannot move a baked path.
public class AnimSceneOverlayTests
{
    [Fact]
    public void Overlay_RoundTripsWithTheClip_AndIsAbsentWhenUnused()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mtile_overlay_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var doc = new AnimationDocument { Name = "ovl", Type = "Misc", Skeleton = "biped" };
            doc.Keyframes.Add(new AnimationKeyframe { Time = 0f });
            doc.Scene = new ClipScene();
            SceneGuideOps.AddGround(doc.Scene, 40f);
            SceneGuideOps.AddOverlay(doc.Scene, SceneOverlayKind.HoverLine);

            var bare = new AnimationDocument { Name = "bare", Type = "Misc", Skeleton = "biped" };
            bare.Keyframes.Add(new AnimationKeyframe { Time = 0f });
            bare.Scene = new ClipScene();
            SceneGuideOps.AddGround(bare.Scene, 40f);

            AnimationStore.Save(doc, dir);
            AnimationStore.Save(bare, dir);
            var loaded = AnimationStore.LoadAll(dir);

            var back = loaded.Find(d => d.Name == "ovl");
            Assert.NotNull(back?.Scene?.Overlays);
            Assert.Single(back.Scene.Overlays);
            Assert.Equal(SceneOverlayKind.HoverLine, back.Scene.Overlays[0].Kind);
            Assert.Null(back.Scene.Overlays[0].Ref);       // derived kinds name nothing

            // A clip that uses none keeps the field absent (not an empty list), so existing
            // clip files are untouched until an overlay is actually added.
            var none = loaded.Find(d => d.Name == "bare");
            Assert.Null(none?.Scene?.Overlays);
            Assert.DoesNotContain("Overlays", File.ReadAllText(Path.Combine(dir, "bare.json")));

            // Removing the last one drops the field again.
            SceneGuideOps.RemoveOverlay(back.Scene, back.Scene.Overlays[0]);
            Assert.Null(back.Scene.Overlays);
        }
        finally { Directory.Delete(dir, true); }
    }

    // The hover line is a VIEW of the tuning, never a copy of it: its height is exactly the
    // fold's hover gap plus the body polygon's own bottom, read live.
    [Fact]
    public void HoverLine_IsDerivedFromTheMovementConfig()
    {
        float expected = (MovementConfig.Current.FoldHoverOffset + SceneReferences.BodyBottomPx) / Game1.SkeletonScale;
        Assert.Equal(expected, SceneReferences.StandingComRig, 4);
        Assert.Equal(PlayerCharacter.Radius * (2f * PlayerCharacter.BodyHeightScale - 1f), SceneReferences.BodyBottomPx, 4);
        // Crouched rides lower than standing (its hover offset is the smaller one).
        Assert.True(SceneReferences.CrouchComRig <= SceneReferences.StandingComRig);
        // And the overlay kinds map onto those two heights.
        Assert.Equal(SceneReferences.StandingComRig, SceneReferences.ComRig(SceneOverlayKind.HoverLine), 4);
        Assert.Equal(SceneReferences.CrouchComRig, SceneReferences.ComRig(SceneOverlayKind.CrouchLine), 4);
    }

    // Bake and check are a pure function of the clip's guides and contacts. Adding an overlay
    // must not move the baked path, the guides it writes, or the drift/penetration report.
    [Fact]
    public void Bake_AndCheck_IgnoreOverlays()
    {
        var rig = SkeletonExamples.Load("biped");
        string dir = Path.Combine(FindDir("SkeletonStates"), "biped");
        // Two independent parses of the same file — the bake mutates the document it is given.
        var plain = AnimationStore.LoadAll(dir).Find(d => d.Name == "stepup");
        var withOverlay = AnimationStore.LoadAll(dir).Find(d => d.Name == "stepup");
        Assert.NotNull(plain);
        Assert.NotNull(withOverlay);
        withOverlay.Scene ??= new ClipScene();
        SceneGuideOps.AddOverlay(withOverlay.Scene, SceneOverlayKind.HoverLine);

        Assert.True(ClipSceneBake.TryBake(plain, rig, out var a, out string errA), errA);
        Assert.True(ClipSceneBake.TryBake(withOverlay, rig, out var b, out string errB), errB);
        Assert.Equal(a.CycleDisplacement.X, b.CycleDisplacement.X, 4);
        Assert.Equal(a.CycleDisplacement.Y, b.CycleDisplacement.Y, 4);
        Assert.Equal(a.Keys, b.Keys);

        Assert.True(ClipSceneBake.TryCheck(plain, rig, out var ca, out _));
        Assert.True(ClipSceneBake.TryCheck(withOverlay, rig, out var cb, out _));
        Assert.Equal(ca.MaxDrift, cb.MaxDrift, 4);
        Assert.Equal(ca.MaxPenetration, cb.MaxPenetration, 4);
        // The overlay survived the bake untouched, and the bake wrote no overlay of its own.
        Assert.Single(withOverlay.Scene.Overlays);
        Assert.Null(plain.Scene.Overlays);
    }

    // A trajectory overlay names a document and keeps that name across a save; several can
    // coexist, and the same (kind, ref) pair is not added twice.
    [Fact]
    public void TrajectoryOverlays_RoundTrip_AndDedupePerReference()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mtile_traj_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var doc = new AnimationDocument { Name = "traj", Type = "Misc", Skeleton = "biped" };
            doc.Keyframes.Add(new AnimationKeyframe { Time = 0f });
            doc.Scene = new ClipScene();
            SceneGuideOps.AddOverlay(doc.Scene, SceneOverlayKind.Arc, "parkour");
            SceneGuideOps.AddOverlay(doc.Scene, SceneOverlayKind.ClipPath, "walk");
            SceneGuideOps.AddOverlay(doc.Scene, SceneOverlayKind.Arc, "parkour");   // same pair again
            Assert.Equal(2, doc.Scene.Overlays.Count);
            SceneGuideOps.AddOverlay(doc.Scene, SceneOverlayKind.Arc, "mantle");    // different ref
            Assert.Equal(3, doc.Scene.Overlays.Count);

            AnimationStore.Save(doc, dir);
            var back = AnimationStore.LoadAll(dir).Find(d => d.Name == "traj");
            Assert.NotNull(back?.Scene?.Overlays);
            Assert.Equal(3, back.Scene.Overlays.Count);
            var arc = back.Scene.Overlays.Find(o => o.Kind == SceneOverlayKind.Arc && o.Ref == "parkour");
            Assert.NotNull(arc);
            Assert.True(SceneReferences.IsTrajectory(arc.Kind));
            Assert.False(SceneReferences.IsHoverLine(arc.Kind));
        }
        finally { Directory.Delete(dir, true); }
    }

    // The curve you SEE and the path you GET must be the same: mapping writes exactly the
    // points the arc overlay draws, through one shared mapping (ClipMotion.ArcOffset).
    [Fact]
    public void MappedPath_MatchesWhatTheArcOverlayDraws()
    {
        string name = null;
        foreach (var n in ReferenceClipRegistry.KnownNames) { name = n; break; }
        Assert.NotNull(name);
        var arc = ReferenceClipRegistry.Get(name);
        Assert.NotNull(arc);

        var doc = new AnimationDocument { Name = "rider", Type = "Misc", Skeleton = "biped", Duration = 0.5f };
        for (int i = 0; i <= 4; i++) doc.Keyframes.Add(new AnimationKeyframe { Time = i / 4f });

        Assert.True(ClipArcMap.TryMap(doc, arc, stretch: false, out var r, out string err), err);
        Assert.Equal(doc.Keyframes.Count, r.Keys);
        Assert.Equal(MotionSource.Track, ClipMotion.Resolve(doc).Source);

        // What the overlay DRAWS and what the map WROTE are the same curve: every keyframe's
        // body_path point is the arc position the overlay would put there.
        foreach (var kf in doc.Keyframes)
        {
            Vector2 drawn = ClipMotion.ArcOffset(arc, ClipArcMap.ArcParam(doc, arc, kf.Time, stretch: false));
            Assert.True(BodyPath.TrySample(doc, kf.Time, out var written));
            Assert.Equal(drawn.X, written.X, 4);
            Assert.Equal(drawn.Y, written.Y, 4);
        }

        // Stretched instead: t = 1 lands on the arc's own end whatever the durations say.
        var stretched = new AnimationDocument { Name = "fit", Type = "Misc", Skeleton = "biped", Duration = 5f };
        for (int i = 0; i <= 4; i++) stretched.Keyframes.Add(new AnimationKeyframe { Time = i / 4f });
        Assert.True(ClipArcMap.TryMap(stretched, arc, stretch: true, out _, out _));
        Assert.True(BodyPath.TrySample(stretched, 1f, out var endPoint));
        Vector2 arcEnd = ClipMotion.ArcOffset(arc, 1f);
        Assert.Equal(arcEnd.X, endPoint.X, 4);
        Assert.Equal(arcEnd.Y, endPoint.Y, 4);
    }

    // An arc overlay is a REFERENCE: attaching one draws a curve and nothing else. Only the
    // map writes the clip's path.
    [Fact]
    public void ArcOverlay_AloneDoesNotMoveTheClip()
    {
        string name = null;
        foreach (var n in ReferenceClipRegistry.KnownNames) { name = n; break; }
        Assert.NotNull(name);

        var doc = new AnimationDocument { Name = "still", Type = "Misc", Skeleton = "biped" };
        doc.Keyframes.Add(new AnimationKeyframe { Time = 0f });
        doc.Keyframes.Add(new AnimationKeyframe { Time = 1f });
        doc.Scene = new ClipScene();
        SceneGuideOps.AddOverlay(doc.Scene, SceneOverlayKind.Arc, name);

        var m = ClipMotion.Resolve(doc);
        Assert.Equal(MotionSource.InPlace, m.Source);       // still stationary
        Assert.Equal(Vector2.Zero, m.BodyAt(0.5f));
        Assert.False(BodyPath.TrySample(doc, 0f, out _));   // no path was written

        // Mapping is the step that changes it, and the overlay survives unchanged.
        var arc = ReferenceClipRegistry.Get(name);
        Assert.NotNull(arc);
        Assert.True(ClipArcMap.TryMap(doc, arc, stretch: false, out _, out _));
        Assert.Equal(MotionSource.Track, ClipMotion.Resolve(doc).Source);
        Assert.Single(doc.Scene.Overlays);
        Assert.Equal(SceneOverlayKind.Arc, doc.Scene.Overlays[0].Kind);
    }

    private static string FindDir(string name)
    {
        var d = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (d != null)
        {
            string c = Path.Combine(d.FullName, name);
            if (Directory.Exists(c)) return c;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException(name);
    }
}
