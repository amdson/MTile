using System;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// The timing stage (Plans/ANIMATION_TIMING_STAGE.md, T1): the phase advances by actual body
// travel over the clip's authored cycle distance, headless.
public class AnimGaitTimingTests
{
    private readonly ITestOutputHelper _o;
    public AnimGaitTimingTests(ITestOutputHelper o) => _o = o;
    private const float Scale = 0.6f;

    // Every shipped locomotion cycle yields a plausible authored stride from its contact
    // labels (any source) — the data path the legacy 100 px constant is replaced by.
    [Theory]
    [InlineData("biped", "run")]
    [InlineData("biped", "walk")]
    [InlineData("biped", "crouchwalk")]
    [InlineData("biped_rabbit", "run")]
    [InlineData("biped_rabbit", "walk")]
    public void CycleDistance_FromContactLabels_IsPlausible(string rig, string clipName)
    {
        var (clip, skel) = Load(rig, clipName);
        Assert.True(ClipStrideTrack.TryCompile(clip, skel, out var gait, out string err, anySource: true), err);
        Assert.True(gait.Feet.Length >= 2, "both feet labeled");
        float d = GaitTiming.CycleDistance(clip, gait, Scale, out string source);
        _o.WriteLine($"{rig}/{clipName}: cycle distance {d:0.0} px ({source}); duration {clip.Duration}s");
        foreach (var f in gait.Feet)
            foreach (var st in f.Stances)
                _o.WriteLine($"  {f.Node,-8} td {st.Touchdown:0.00} lo {st.Liftoff:0.00} span {st.Liftoff - st.Touchdown:0.00}  " +
                             $"off {st.TdOffset.X:0.0}→{st.LoOffset.X:0.0} rig  travel {(st.TdOffset.X - st.LoOffset.X) * Scale:0.0} px");
        Assert.Equal("gait_track", source);
        // Forward cycles travel forward. (The stride is whatever the labels say — biped walk
        // is ~20 px/cycle at scale 0.6, run ~43 px.)
        Assert.InRange(d, 5f, 300f);
    }

    [Fact]
    public void Advance_TravelOverCycleDistance_AndClampsBackwardTravel()
    {
        var (clip, skel) = Load("biped", "run");
        ClipStrideTrack.TryCompile(clip, skel, out var gait, out _, anySource: true);
        float cycle = GaitTiming.CycleDistance(clip, gait, Scale, out _);

        // One full cycle of travel is exactly one cycle of phase, from any entry phase.
        var fwd = GaitTiming.Advance(new TimingInputs
        {
            Clip = clip, Gait = gait, Phase = 0.3f, Dt = 1f / 60f, MaxStep = 2f,
            PrevPos = new Vector2(100f, 0f), Pos = new Vector2(100f + cycle, 0f), Facing = +1, Scale = Scale,
        });
        Assert.Equal(1f, fwd.DeltaPhase, 3);
        Assert.Equal(60f, fwd.Rate, 2);
        // A tenth of a cycle of travel is a tenth of a cycle of phase (one constant rate).
        float Step(float frac) => GaitTiming.Advance(new TimingInputs
        {
            Clip = clip, Gait = gait, Phase = 0.3f, Dt = 1f / 60f, MaxStep = 2f,
            PrevPos = new Vector2(100f, 0f), Pos = new Vector2(100f + frac * cycle, 0f), Facing = +1, Scale = Scale,
        }).DeltaPhase;
        Assert.Equal(0.1f, Step(0.1f), 4);

        // Same travel, facing the other way: the body moved against the authored direction.
        var back = GaitTiming.Advance(new TimingInputs
        {
            Clip = clip, Gait = gait, Phase = 0.3f, Dt = 1f / 60f,
            PrevPos = new Vector2(100f, 0f), Pos = new Vector2(100f + 0.1f * cycle, 0f), Facing = -1, Scale = Scale,
        });
        Assert.Equal(0f, back.DeltaPhase);

        // Facing −1 and moving −x is forward travel for that facing.
        var mirrored = GaitTiming.Advance(new TimingInputs
        {
            Clip = clip, Gait = gait, Phase = 0.3f, Dt = 1f / 60f, MaxStep = 2f,
            PrevPos = new Vector2(100f, 0f), Pos = new Vector2(100f - cycle, 0f), Facing = -1, Scale = Scale,
        });
        Assert.Equal(1f, mirrored.DeltaPhase, 3);
    }

    [Fact]
    public void Advance_NoAuthoredStride_FallsBackToTheNominal_DirectionAgnostic()
    {
        var (clip, skel) = Load("biped", "walkback");   // no contact labels, no body path
        var gait = ClipStrideTrack.TryCompile(clip, skel, out var g, out _, anySource: true) ? g : null;
        var r = GaitTiming.Advance(new TimingInputs
        {
            Clip = clip, Gait = gait, Phase = 0f, Dt = 1f / 60f,
            PrevPos = new Vector2(50f, 0f), Pos = new Vector2(40f, 0f), Facing = +1, Scale = Scale,
        });
        Assert.Equal("nominal", r.Source);
        Assert.Equal(10f / GaitTiming.NominalCycleDistance, r.DeltaPhase, 5);
    }

    [Fact]
    public void Advance_StationaryCycle_PlaysAtAuthoredRate()
    {
        // A synthetic in-place shuffle: the travel curve is flat (no authored body travel).
        var (walk, skel) = Load("biped", "walk");
        foreach (var k in walk.Keyframes) k.Additions = null;   // drop any authored path
        var gait = new ClipStrideTrack { Feet = Array.Empty<FootStrideTrack>(), CycleTravel = 0f, HasTravel = true };
        var r = GaitTiming.Advance(new TimingInputs
        {
            Clip = walk, Gait = gait, Phase = 0f, Dt = 1f / 60f,
            PrevPos = Vector2.Zero, Pos = new Vector2(5f, 0f), Facing = +1, Scale = Scale,
        });
        Assert.Equal((1f / 60f) / walk.Duration, r.DeltaPhase, 5);
    }

    // The live animator advances a run by travel, and the solved pose is still the drawn one.
    [Fact]
    public void Animator_RunAdvancesByTravel_PhaseTracksBodyDistance()
    {
        var clips = AnimationStore.LoadAll(Path.Combine(FindDir("SkeletonStates"), "biped"));
        var anim = new CharacterAnimator(SkeletonExamples.Load("biped"), Scale, clips);
        const float dt = 1f / 60f, vx = 90f;
        float x = 0f, cycles = 0f, prevPhase = 0f;
        for (int i = 0; i < 120; i++)
        {
            x += vx * dt;
            anim.Update(new CharacterAnimSample(new Vector2(x, 0f), new Vector2(vx, 0f), +1, true, "WalkState", "", dt));
            if (i == 0) { prevPhase = anim.State.Phase; continue; }
            float d = anim.State.Phase - prevPhase; if (d < 0f) d += 1f;
            cycles += d; prevPhase = anim.State.Phase;
        }
        float cycleDist = anim.LastTiming.CycleDistance;
        _o.WriteLine($"cycle distance {cycleDist:0.0} px ({anim.LastTiming.Source}); {cycles:0.00} cycles over {vx * dt * 119:0.0} px");
        Assert.Equal("gait_track", anim.LastTiming.Source);
        Assert.Equal(vx * dt * 119 / cycleDist, cycles, 2);
    }

    private static (AnimationDocument, Skeleton) Load(string rig, string clipName)
    {
        var clip = AnimationStore.LoadAll(Path.Combine(FindDir("SkeletonStates"), rig)).Find(d => d.Name == clipName);
        Assert.True(clip != null, $"{rig}/{clipName}.json not found");
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
