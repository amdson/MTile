using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests;

// ClipStrideTrack compiler (step planner P1 — Plans/ANIMATION_STEP_PLANNER_IMPL.md).
//
// Contacts are AUTHORED SPANS now, so the stance boundaries are no longer derived: what used
// to be tested here (run detection across a keyframe ring, "liftoff = the key after the run",
// seam unwrapping, dropping a duplicate closing key) has no subject — a span states its own
// interval. What still has to hold is everything DOWNSTREAM of that: swings between stances,
// chord-relative residuals, the persistent case, structural refusals, and the placement
// convention's agreement with the live solve-root.
public class AnimStrideTrackTests
{
    // Tiny rig: hip (root, rotates) -> foot (length 10). Rotating the hip swings the
    // foot tip, so keyframed hip rotations give real motion for offset/residual checks.
    private static Skeleton TinyRig()
    {
        var b = new SkeletonBuilder("tiny");
        int hip = b.AddRoot("hip", 0f, 4f);
        b.Add("foot", hip, MathHelper.PiOver2, 10f);   // points down (+y) at rest
        return b.Build();
    }

    private static AnimationKeyframe Key(float t, float hipRot) => new()
    {
        Time = t,
        Bones = new List<PoseBoneEntry> { new() { Bone = "hip", Rotation = hipRot } },
    };

    private static ContactSpan Planned(string point, float start, float end) => new()
    { Point = point, Start = start, End = end };

    private static AnimationDocument Doc(bool loop, ContactSpan[] contacts, params AnimationKeyframe[] keys) => new()
    {
        Name = "test", Type = "Misc", Skeleton = "tiny", Loop = loop, Duration = 1f,
        Keyframes = new List<AnimationKeyframe>(keys),
        Contacts = contacts == null ? null : new List<ContactSpan>(contacts),
    };

    [Fact]
    public void Stance_IsTheAuthoredSpan_AndTheSwingFollowsIt()
    {
        var doc = Doc(loop: true, new[] { Planned("foot", 0.1f, 0.6f) },
            Key(0.1f, 0.0f), Key(0.3f, 0.2f), Key(0.6f, 0.5f), Key(0.8f, 0.1f));
        Assert.True(ClipStrideTrack.TryCompile(doc, TinyRig(), out var track, out string err), err);
        var f = Assert.Single(track.Feet);
        var st = Assert.Single(f.Stances);
        Assert.Equal(0.1f, st.Touchdown, 3);
        Assert.Equal(0.6f, st.Liftoff, 3);
        Assert.False(st.Persistent);
        // One swing back to the same stance (cyclic), from 0.6 unwrapped to 1.1.
        var sw = Assert.Single(f.Swings);
        Assert.Equal(0.6f, sw.Start, 3);
        Assert.Equal(1.1f, sw.End, 3);
        // Residuals are chord-relative: exact zero at both endpoints by construction.
        Assert.Equal(Vector2.Zero, sw.Residuals[0]);
        Assert.Equal(Vector2.Zero, sw.Residuals[^1]);
    }

    [Fact]
    public void SpanCrossingTheSeam_StaysOneStance()
    {
        // The span says 0.75 → 1.25 outright; there is no ring to walk and nothing to unwrap.
        var doc = Doc(loop: true, new[] { Planned("foot", 0.75f, 1.25f) },
            Key(0.00f, 0.0f), Key(0.25f, 0.3f), Key(0.50f, 0.5f), Key(0.75f, 0.2f));
        Assert.True(ClipStrideTrack.TryCompile(doc, TinyRig(), out var track, out string err), err);
        var f = Assert.Single(track.Feet);
        var st = Assert.Single(f.Stances);
        Assert.Equal(0.75f, st.Touchdown, 3);
        Assert.Equal(1.25f, st.Liftoff, 3);
        // Its two endpoints are the same phase modulo 1, so the offsets agree.
        var doc2 = Doc(loop: true, new[] { Planned("foot", 0.75f, 1.75f) }, Key(0f, 0f), Key(0.5f, 0.4f));
        Assert.True(ClipStrideTrack.TryCompile(doc2, TinyRig(), out var t2, out err), err);
        Assert.True(t2.Feet[0].Stances[0].Persistent, "a span covering a whole cycle is persistent");
    }

    [Fact]
    public void TwoStancesOnOneFoot_GiveTwoSwings()
    {
        var doc = Doc(loop: true, new[] { Planned("foot", 0.0f, 0.3f), Planned("foot", 0.5f, 0.8f) },
            Key(0.0f, 0.0f), Key(0.3f, 0.3f), Key(0.5f, 0.1f), Key(0.8f, 0.4f));
        Assert.True(ClipStrideTrack.TryCompile(doc, TinyRig(), out var track, out string err), err);
        var f = Assert.Single(track.Feet);
        Assert.Equal(2, f.Stances.Length);
        Assert.Equal(2, f.Swings.Length);
        Assert.Equal(0.3f, f.Swings[0].Start, 3);
        Assert.Equal(0.5f, f.Swings[0].End, 3);
        // The cyclic swing wraps: 0.8 → 1.0 (the next stance's touchdown, unwrapped).
        Assert.Equal(0.8f, f.Swings[1].Start, 3);
        Assert.Equal(1.0f, f.Swings[1].End, 3);
    }

    [Fact]
    public void PersistentStance_WholeCycle_NoSwings()
    {
        var doc = Doc(loop: true, new[] { Planned("foot", 0f, 1f) }, Key(0.0f, 0.0f), Key(0.5f, 0.1f));
        Assert.True(ClipStrideTrack.TryCompile(doc, TinyRig(), out var track, out string err), err);
        var f = Assert.Single(track.Feet);
        Assert.True(Assert.Single(f.Stances).Persistent);
        Assert.Empty(f.Swings);
    }

    [Fact]
    public void NonLoop_TailStance_HasNoSwing()
    {
        var doc = Doc(loop: false, new[] { Planned("foot", 0.4f, 1.0f) }, Key(0.0f, 0.0f), Key(0.8f, 0.3f));
        Assert.True(ClipStrideTrack.TryCompile(doc, TinyRig(), out var track, out string err), err);
        var f = Assert.Single(track.Feet);
        var st = Assert.Single(f.Stances);
        Assert.Equal(0.4f, st.Touchdown, 3);
        Assert.Equal(1.0f, st.Liftoff, 3);
        Assert.Empty(f.Swings);   // nothing follows a non-loop tail stance
    }

    // A span's Source is not an ownership label: every span on a point is a stance of that
    // point's track, whatever it says.
    [Fact]
    public void AnySource_OnOnePoint_CompilesAsOneTrack()
    {
        var doc = Doc(loop: true,
            new[] { Planned("foot", 0f, 0.4f),
                    new ContactSpan { Point = "foot", Start = 0.5f, End = 0.9f, Source = ContactSource.External } },
            Key(0.0f, 0f), Key(0.5f, 0f));
        Assert.True(ClipStrideTrack.TryCompile(doc, TinyRig(), out var track, out string err), err);
        var f = Assert.Single(track.Feet);
        Assert.Equal(2, f.Stances.Length);
    }

    [Fact]
    public void UnknownPoint_IsRefused()
    {
        var doc = Doc(loop: true, new[] { Planned("flipper", 0f, 0.5f) }, Key(0.0f, 0f), Key(0.5f, 0f));
        Assert.False(ClipStrideTrack.TryCompile(doc, TinyRig(), out _, out string err));
        Assert.Contains("flipper", err);
    }

    [Fact]
    public void EmptySpan_IsRefused()
    {
        var doc = Doc(loop: true, new[] { Planned("foot", 0.4f, 0.4f) }, Key(0.0f, 0f), Key(0.5f, 0f));
        Assert.False(ClipStrideTrack.TryCompile(doc, TinyRig(), out _, out string err));
        Assert.Contains("empty span", err);
    }

    [Fact]
    public void NoSpans_CompilesEmpty_NotAnError()
    {
        Assert.True(ClipStrideTrack.TryCompile(Doc(true, null, Key(0f, 0f), Key(0.5f, 0f)),
                                               TinyRig(), out var track, out string err), err);
        Assert.Empty(track.Feet);
        Assert.True(ClipStrideTrack.TryCompile(Doc(true, System.Array.Empty<ContactSpan>(), Key(0f, 0f), Key(0.5f, 0f)),
                                               TinyRig(), out var t2, out err), err);
        Assert.Empty(t2.Feet);
    }

    // The placement convention: offsets are rig-unit, facing +1, com-shifted (both axes), so
    //     world = bodyPos + (dir·scale·off.X, scale·off.Y)
    // must equal FK under the live solve-root T(body + BodyPath.RootOffset(c))·S(dir·scale, scale)
    // (CharacterAnimator.SolveRootAt). Verified against the REAL biped rig and walk clip, with
    // the spans authored HERE rather than read from the file — the convention is a property of
    // the compiler, and pinning it to whatever contacts a clip happens to carry made this test
    // hostage to clip content.
    [Theory]
    [InlineData(1, 2.0f)]
    [InlineData(-1, 1.5f)]
    public void RealWalkClip_OffsetConvention_MatchesLiveSolveRoot(int dir, float scale)
    {
        var rig = SkeletonStore.Load(FindDir("Skeletons"), "biped");
        var walk = AnimationStore.LoadAll(Path.Combine(FindDir("SkeletonStates"), "biped"))
                                 .First(a => a.Type == "Walk");
        walk.Contacts = new List<ContactSpan>
        {
            Planned("support_l", 0.00f, 0.50f),
            Planned("support_r", 0.50f, 1.00f),
        };

        Assert.True(ClipStrideTrack.TryCompile(walk, rig, out var track, out string err), err);
        Assert.Equal(2, track.Feet.Length);   // support_l + support_r
        foreach (var f in track.Feet)
        {
            var st = Assert.Single(f.Stances);
            Assert.False(st.Persistent);
            Assert.True(st.Liftoff - st.Touchdown > 0.2f, "walk stance spans about half the cycle");

            // Reconstruct the touchdown world position both ways.
            var body = new Vector2(320f, 145f);
            Vector2 viaTrack = body + new Vector2(dir * scale * st.TdOffset.X, scale * st.TdOffset.Y);

            var a = rig.CreatePose(); var b = rig.CreatePose(); var c = rig.CreatePose();
            var d = rig.CreatePose(); var dst = rig.CreatePose();
            AnimationSampler.SampleSmooth(walk, st.Touchdown, a, b, c, d, dst);
            Vector2 rootPos = body;
            if (BodyPath.TrySampleAnchor(walk, st.Touchdown, out var com, out _))
                rootPos += BodyPath.RootOffset(com, dir, scale);
            var root = Affine2.FromTRS(rootPos, 0f, new Vector2(dir * scale, scale));
            Vector2 viaLiveRoot = dst.ComputeWorld(root)[f.Bone].Translation;

            Assert.True((viaTrack - viaLiveRoot).Length() < 1e-3f,
                $"{f.Node}: track {viaTrack} vs live-root {viaLiveRoot}");
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
}
