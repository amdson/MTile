using System;
using System.IO;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// The step-up authoring pilot (Plans/ANIMATION_SCENE_AUTHORING_PLAN.md "Pilot one step-up
// scene"; workplan chunk 7): the shipped stepup clips carry explicit motion intent — a baked
// scene path that keeps every planted foot fixed in scene space and rises as it runs — and
// their swing toe paths clear the authored step guides. The bake itself reproduces the stance
// sweeps' displacement, so the path is a pure function of the clip's own labels.
public class AnimStepUpSceneTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("biped")]
    [InlineData("biped_rabbit")]
    public void ShippedStepUp_HasMotionIntent_KeepsStanceFeetFixed_AndClearsItsSteps(string rig)
    {
        var (clip, skel) = Load(rig, "stepup");
        Assert.Equal(MotionSource.Track, clip.Motion);
        Assert.NotNull(clip.Scene);
        Assert.True(clip.Scene.Guides.Count >= 2, "a ground line and at least one step block");
        Assert.True(ClipSceneBake.TryCheck(clip, skel, out var c, out string err), err);
        output.WriteLine(ClipSceneBake.Describe(c));
        Assert.True(c.CycleDisplacement.X > 5f, "the cycle runs forward");
        Assert.True(c.CycleDisplacement.Y < -5f, "the cycle climbs");
        Assert.True(c.MaxDrift < 2.5f, $"a planted foot drifts {c.MaxDrift:0.00} rig in scene space");
        Assert.True(c.MaxPenetration < 0.5f, $"a swing toe dips {c.MaxPenetration:0.00} rig into a step guide");
        // The timing stage reads the same displacement from the path.
        Assert.True(ClipStrideTrack.TryCompile(clip, skel, out var gait, out _));
        var d = GaitTiming.CycleDisplacement(clip, gait, 1f, out string source);
        Assert.Equal("body_path", source);
        Assert.Equal(c.CycleDisplacement.X, d.X, 2);
        Assert.Equal(c.CycleDisplacement.Y, d.Y, 2);
    }

    // A level gait bakes flat: its run matches the stance sweeps (to grid resolution), its Y
    // is dropped (the walk's sweeps "sink" ~9 rig per cycle — leg geometry the runtime's
    // vertical offset absorbs), and the scene is a ground line only.
    [Fact]
    public void Bake_ReproducesTheStanceSweeps_OnACopy()
    {
        var (clip, skel) = Load("biped", "walk");
        foreach (var k in clip.Keyframes)
            k.Additions?.RemoveAll(a => a.Name == BodyPath.ChannelName);
        clip.Motion = null; clip.Scene = null;
        Assert.True(ClipStrideTrack.TryCompile(clip, skel, out var gait, out _));
        Assert.True(ClipSceneBake.TryBake(clip, skel, out var r, out string err, flat: true), err);
        output.WriteLine($"walk: D = {r.CycleDisplacement}, sweeps {gait.CycleDisplacement}, {r.Guides} guides");
        Assert.InRange(r.CycleDisplacement.X, gait.CycleDisplacement.X * 0.95f, gait.CycleDisplacement.X * 1.05f);   // grid resolution
        Assert.Equal(0f, r.CycleDisplacement.Y);
        Assert.Equal(MotionSource.Track, clip.Motion);
        Assert.Equal(1, r.Guides);   // a flat walk: the ground line only
        Assert.True(ClipSceneBake.TryCheck(clip, skel, out var c, out _));
        Assert.True(c.MaxPenetration < 3f, $"toe dips {c.MaxPenetration} rig under the ground line");   // the walk drags its toe a little
        // The timing stage reads the flat run from the path.
        Assert.Equal("body_path", GaitTiming.CycleDisplacement(clip, gait, 1f, out string source) is var d ? source : source);
        Assert.Equal(r.CycleDisplacement.X, d.X, 3);
    }

    private static (AnimationDocument, Skeleton) Load(string rig, string name)
    {
        var clip = AnimationStore.LoadAll(Path.Combine(FindDir("SkeletonStates"), rig)).Find(d => d.Name == name);
        Assert.NotNull(clip);
        return (clip, SkeletonExamples.Load(rig));
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
