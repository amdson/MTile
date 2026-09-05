using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests;

// ClipStrideTrack compiler (step planner P1 — Plans/ANIMATION_STEP_PLANNER_IMPL.md):
// stance runs from PlannedSupport labels, loop-seam unwrapping, persistent stance,
// structural validation, and the placement convention's agreement with the live
// solve-root (com frame, rig units, facing/scale applied at runtime).
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

    private static AnimationKeyframe Key(float t, float hipRot, params ContactLabel[] contacts) => new()
    {
        Time = t,
        Bones = new List<PoseBoneEntry> { new() { Bone = "hip", Rotation = hipRot } },
        Contacts = contacts.Length == 0 ? null : new List<ContactLabel>(contacts),
    };

    private static ContactLabel Planned(string node) =>
        new() { Node = node, Weight = 1f, Source = ContactSource.PlannedSupport };

    private static AnimationDocument Doc(bool loop, params AnimationKeyframe[] keys) => new()
    {
        Name = "test", Type = "Misc", Skeleton = "tiny", Loop = loop, Duration = 1f,
        Keyframes = new List<AnimationKeyframe>(keys),
    };

    [Fact]
    public void SingleStance_TouchdownAndLiftoff_FromRunBoundaries()
    {
        // foot planned over keys 0.1 and 0.3; free at 0.6 and 0.8. Stance [0.1, 0.6).
        var doc = Doc(loop: true,
            Key(0.1f, 0.0f, Planned("foot")),
            Key(0.3f, 0.2f, Planned("foot")),
            Key(0.6f, 0.5f),
            Key(0.8f, 0.1f));
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
    public void LoopSeam_RunWrapsAndUnwraps()
    {
        // Planned on the LAST ring key (0.75) and the first (0.0): one stance wrapping
        // the seam, touchdown 0.75, liftoff unwrapped to 1.25 (= key 0.25 + 1).
        var doc = Doc(loop: true,
            Key(0.00f, 0.0f, Planned("foot")),
            Key(0.25f, 0.3f),
            Key(0.50f, 0.5f),
            Key(0.75f, 0.2f, Planned("foot")));
        Assert.True(ClipStrideTrack.TryCompile(doc, TinyRig(), out var track, out string err), err);
        var f = Assert.Single(track.Feet);
        var st = Assert.Single(f.Stances);
        Assert.Equal(0.75f, st.Touchdown, 3);
        Assert.Equal(1.25f, st.Liftoff, 3);
    }

    [Fact]
    public void ClosingDuplicateKey_IsDroppedFromTheRing()
    {
        // Walk-shaped: closing key at t=1 duplicates key 0's label. Must not create a
        // second stance — one stance [0, 0.5).
        var doc = Doc(loop: true,
            Key(0.00f, 0.0f, Planned("foot")),
            Key(0.25f, 0.2f, Planned("foot")),
            Key(0.50f, 0.4f),
            Key(0.75f, 0.2f),
            Key(1.00f, 0.0f, Planned("foot")));
        Assert.True(ClipStrideTrack.TryCompile(doc, TinyRig(), out var track, out string err), err);
        var f = Assert.Single(track.Feet);
        var st = Assert.Single(f.Stances);
        Assert.Equal(0.0f, st.Touchdown, 3);
        Assert.Equal(0.5f, st.Liftoff, 3);
    }

    [Fact]
    public void PersistentStance_WholeCycle_NoSwings()
    {
        var doc = Doc(loop: true,
            Key(0.0f, 0.0f, Planned("foot")),
            Key(0.5f, 0.1f, Planned("foot")));
        Assert.True(ClipStrideTrack.TryCompile(doc, TinyRig(), out var track, out string err), err);
        var f = Assert.Single(track.Feet);
        var st = Assert.Single(f.Stances);
        Assert.True(st.Persistent);
        Assert.Empty(f.Swings);
    }

    [Fact]
    public void NonLoop_TailStance_HoldsToClipEnd()
    {
        var doc = Doc(loop: false,
            Key(0.0f, 0.0f),
            Key(0.4f, 0.2f, Planned("foot")),
            Key(0.8f, 0.3f, Planned("foot")));
        Assert.True(ClipStrideTrack.TryCompile(doc, TinyRig(), out var track, out string err), err);
        var f = Assert.Single(track.Feet);
        var st = Assert.Single(f.Stances);
        Assert.Equal(0.4f, st.Touchdown, 3);
        Assert.Equal(1.0f, st.Liftoff, 3);
        Assert.Empty(f.Swings);   // nothing follows a non-loop tail stance
    }

    [Fact]
    public void MixedSources_OnOneNode_IsRefused()
    {
        var doc = Doc(loop: true,
            Key(0.0f, 0f, Planned("foot")),
            Key(0.5f, 0f, new ContactLabel { Node = "foot", Source = ContactSource.SelfPlant }));
        Assert.False(ClipStrideTrack.TryCompile(doc, TinyRig(), out _, out string err));
        Assert.Contains("mixes", err);
    }

    [Fact]
    public void UnknownBone_IsRefused()
    {
        var doc = Doc(loop: true, Key(0.0f, 0f, Planned("flipper")), Key(0.5f, 0f));
        Assert.False(ClipStrideTrack.TryCompile(doc, TinyRig(), out _, out string err));
        Assert.Contains("flipper", err);
    }

    [Fact]
    public void NoOptIn_CompilesEmpty_NotAnError()
    {
        var doc = Doc(loop: true,
            Key(0.0f, 0f, new ContactLabel { Node = "foot", Source = ContactSource.SelfPlant }),
            Key(0.5f, 0f));
        Assert.True(ClipStrideTrack.TryCompile(doc, TinyRig(), out var track, out string err), err);
        Assert.Empty(track.Feet);
    }

    // The placement convention: offsets are rig-unit, facing +1, com.Y-shifted, so
    //     world = bodyPos + (dir·scale·off.X, scale·off.Y)
    // must equal FK under the live solve-root T(bodyX, bodyY − com.Y·scale)·S(dir·scale, scale)
    // (CharacterAnimator.Update step 2). Verified on the REAL walk clip + biped rig,
    // labels flipped to PlannedSupport in memory (no file writes).
    [Theory]
    [InlineData(1, 2.0f)]
    [InlineData(-1, 1.5f)]
    public void RealWalkClip_OffsetConvention_MatchesLiveSolveRoot(int dir, float scale)
    {
        var rig = SkeletonStore.Load(FindDir("Skeletons"), "biped");
        var walk = AnimationStore.LoadAll(Path.Combine(FindDir("SkeletonStates"), "biped"))
                                 .First(a => a.Type == "Walk");
        foreach (var k in walk.Keyframes)
            if (k.Contacts != null)
                foreach (var l in k.Contacts) l.Source = ContactSource.PlannedSupport;

        Assert.True(ClipStrideTrack.TryCompile(walk, rig, out var track, out string err), err);
        Assert.Equal(2, track.Feet.Length);   // foot_l + foot_r
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
            float comY = 0f;
            if (AnimAdditionSampler.SamplePoint(walk, st.Touchdown, "com", out var com)) comY = com.Y;
            var root = Affine2.FromTRS(new Vector2(body.X, body.Y - comY * scale), 0f,
                                       new Vector2(dir * scale, scale));
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
