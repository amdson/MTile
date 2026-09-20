using System.IO;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;

namespace MTile.Tests;

// CLIP-LOCAL REFERENCE ARCS (AnimationDocument.Arcs / ClipArcs). Editing an arc from inside
// the clip editor forks it into that clip, so the promise under test is isolation: the fork
// is a deep copy, it round-trips with the clip, the shared pool is never touched, and the
// overlay's Local flag decides which of two same-named arcs you get.
public class AnimClipArcTests
{
    private static HermiteClipDocument SharedArc(string name = "shared")
    {
        var arc = HermiteClipDocument.NewDefault(name);
        arc.Duration = 0.5f;
        return arc;
    }

    private static AnimationDocument Clip(string name = "c")
    {
        var d = new AnimationDocument { Name = name, Type = "Misc", Skeleton = "biped", Duration = 0.5f };
        for (int i = 0; i <= 2; i++) d.Keyframes.Add(new AnimationKeyframe { Time = i / 2f });
        return d;
    }

    [Fact]
    public void Fork_IsADeepCopy_AndLeavesTheSharedArcAlone()
    {
        var shared = SharedArc("parkour");
        var clip = Clip();

        var local = ClipArcs.Fork(clip, shared);
        Assert.NotNull(local);
        Assert.Equal("parkour", local.Name);        // same name, different pool
        Assert.Equal("parkour", local.FromShared);  // provenance recorded
        Assert.Same(local, ClipArcs.FindLocal(clip, "parkour"));

        // Editing the fork cannot reach the shared document: not the list, not a key object.
        int sharedKeys = shared.Keys.Count;
        Vector2 sharedFirst = shared.Keys[0].Pos;
        local.Keys[0].Pos = sharedFirst + new Vector2(11f, -7f);
        local.Duration = 2.5f;
        ArcEditOpsAddKey(local);

        Assert.Equal(sharedKeys, shared.Keys.Count);
        Assert.Equal(sharedFirst, shared.Keys[0].Pos);
        Assert.Equal(0.5f, shared.Duration);
        Assert.Null(shared.FromShared);
    }

    // Adds a key without referencing the Demo project (ArcEditOps lives there): the same
    // insert the editor performs, reduced to what this test needs.
    private static void ArcEditOpsAddKey(HermiteClipDocument arc)
    {
        arc.Keys.Insert(1, new HermiteClipKey { Pos = arc.Eval(0.5f), Tan = arc.EvalTangent(0.5f) });
        arc.RederiveT();
    }

    [Fact]
    public void LocalArc_RoundTripsWithTheClip_AndIsAbsentWhenUnused()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mtile_arcs_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var clip = Clip("withArc");
            var local = ClipArcs.Fork(clip, SharedArc("mantle"));
            local.Duration = 1.25f;
            local.Keys[0].Pos = new Vector2(3f, -4f);

            var bare = Clip("bare");

            AnimationStore.Save(clip, dir);
            AnimationStore.Save(bare, dir);
            var loaded = AnimationStore.LoadAll(dir);

            var back = loaded.Find(d => d.Name == "withArc");
            Assert.NotNull(back?.Arcs);
            Assert.Single(back.Arcs);
            Assert.Equal("mantle", back.Arcs[0].Name);
            Assert.Equal("mantle", back.Arcs[0].FromShared);
            Assert.Equal(1.25f, back.Arcs[0].Duration, 3);
            Assert.Equal(new Vector2(3f, -4f), back.Arcs[0].Keys[0].Pos);

            // A clip with no local arcs writes no field at all.
            Assert.Null(loaded.Find(d => d.Name == "bare").Arcs);
            Assert.DoesNotContain("\"Arcs\"", File.ReadAllText(Path.Combine(dir, "bare.json")));

            // Removing the last local arc drops the field again.
            ClipArcs.Remove(back, back.Arcs[0]);
            Assert.Null(back.Arcs);
        }
        finally { Directory.Delete(dir, true); }
    }

    // The overlay's Local flag is what picks the pool, so a clip can show the shared arc and
    // its own fork of the same name at once.
    [Fact]
    public void OverlayLocalFlag_SelectsBetweenSameNamedArcs()
    {
        var clip = Clip();
        clip.Scene = new ClipScene();
        var sharedRow = SceneGuideOps.AddOverlay(clip.Scene, SceneOverlayKind.Arc, "parkour");
        var localRow  = SceneGuideOps.AddOverlay(clip.Scene, SceneOverlayKind.Arc, "parkour", local: true);
        Assert.NotSame(sharedRow, localRow);             // two rows, not a dedupe
        Assert.Equal(2, clip.Scene.Overlays.Count);
        Assert.False(sharedRow.Local);
        Assert.True(localRow.Local);

        // Only the local one resolves inside the clip; the shared row needs the shared pool.
        var local = ClipArcs.Fork(clip, SharedArc("parkour"));
        Assert.Same(local, ClipArcs.FindLocal(clip, localRow.Ref));
        Assert.Same(local, ClipArcs.FindLocal(clip, sharedRow.Ref));   // same NAME…
        Assert.True(localRow.Local && !sharedRow.Local);               // …the flag is the difference

        // The flag survives a clone of the scene.
        var copy = clip.Scene.Clone();
        Assert.True(copy.Overlays.Find(o => o.Local) != null);
        Assert.True(copy.Overlays.Find(o => !o.Local) != null);
    }

    [Fact]
    public void LocalNames_AreUniqueWithinTheClip()
    {
        var clip = Clip();
        ClipArcs.Fork(clip, SharedArc("arc"));
        var second = ClipArcs.Fork(clip, SharedArc("arc"));
        Assert.Equal("arc2", second.Name);
        Assert.Equal(2, clip.Arcs.Count);
        Assert.Equal("arc3", ClipArcs.UniqueName(clip, "arc"));
    }

    // Seeding from the clip's own path: the quickest way to turn an authored body_path into an
    // editable curve. Arcs are authored in game pixels, so the trace converts from rig units.
    [Fact]
    public void NewLocal_SeededFromBodyPath_TracesItInPixels()
    {
        var clip = Clip();
        for (int i = 0; i < clip.Keyframes.Count; i++)
        {
            clip.Keyframes[i].Additions = new System.Collections.Generic.List<AnimAddition>
            {
                new() { Name = BodyPath.ChannelName, Kind = AnimAdditionKind.Point, Px = i * 10f, Py = -i * 4f },
            };
        }

        var arc = ClipArcs.NewLocal(clip, "traced", seedFromPath: true);
        Assert.NotNull(arc);
        Assert.Equal(clip.Keyframes.Count, arc.Keys.Count);
        Assert.Equal(clip.Duration, arc.Duration, 3);
        // First/last keys sit on the path's ends, converted rig → px, and the anchors match.
        Assert.Equal(0f, arc.Keys[0].X, 3);
        Assert.Equal(20f * Game1.SkeletonScale, arc.Keys[^1].X, 3);
        Assert.Equal(-8f * Game1.SkeletonScale, arc.Keys[^1].Y, 3);
        Assert.Equal(arc.Keys[0].Pos, arc.Entry);
        Assert.Equal(arc.Keys[^1].Pos, arc.Gate);

        // An empty clip has nothing to trace, and falls back to the default arc.
        var plain = ClipArcs.NewLocal(Clip(), "empty", seedFromPath: true);
        Assert.NotNull(plain);
        Assert.True(plain.Keys.Count >= 2);
    }

    // A fresh fork maps identically to the arc it came from — the copy is the same curve until
    // it is edited.
    [Fact]
    public void MappingFromAFork_MatchesTheSharedArc_UntilEdited()
    {
        var shared = SharedArc("m");
        var a = Clip("a");
        var b = Clip("b");
        var fork = ClipArcs.Fork(b, shared);

        Assert.True(ClipArcMap.TryMap(a, shared, stretch: false, out _, out _));
        Assert.True(ClipArcMap.TryMap(b, fork, stretch: false, out _, out _));
        foreach (var kf in a.Keyframes)
        {
            Assert.True(BodyPath.TrySample(a, kf.Time, out var pa));
            Assert.True(BodyPath.TrySample(b, kf.Time, out var pb));
            Assert.Equal(pa.X, pb.X, 4);
            Assert.Equal(pa.Y, pb.Y, 4);
        }

        // Edit the fork, remap, and only b moves.
        fork.Keys[^1].Pos += new Vector2(25f, -25f);
        Assert.True(ClipArcMap.TryMap(b, fork, stretch: false, out _, out _));
        Assert.True(BodyPath.TrySample(a, 1f, out var endA));
        Assert.True(BodyPath.TrySample(b, 1f, out var endB));
        Assert.NotEqual(endA.X, endB.X, 3);
    }
}
