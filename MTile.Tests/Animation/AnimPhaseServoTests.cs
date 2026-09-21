using System;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// The foot-synchronized phase servo (GaitTiming T6): the planted feet report the phase, the
// timing stage tracks it as a bounded rate change, and only an outright disagreement
// re-enters the phase.
public class AnimPhaseServoTests(ITestOutputHelper output)
{
    private const float Scale = 0.6f, Dt = 1f / 60f;

    private static (AnimationDocument, Skeleton, ClipStrideTrack) LoadRun()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "SkeletonStates"))) dir = dir.Parent;
        var clip = AnimationStore.LoadAll(Path.Combine(dir!.FullName, "SkeletonStates", "biped")).Single(c => c.Name == "run");
        var skel = SkeletonExamples.Load("biped");
        Assert.True(ClipStrideTrack.TryCompile(clip, skel, out var gait, out string err, anySource: true), err);
        return (clip, skel, gait);
    }

    private static TimingInputs Steady(AnimationDocument clip, ClipStrideTrack gait, float phase, float prevRate,
                                       float travelPx, bool observed, float observedPhase, float slew = 0f)
        => new()
        {
            Clip = clip, Gait = gait, Phase = phase, PrevRate = prevRate, Dt = Dt, MaxStep = 2f,
            PrevPos = new Vector2(100f, 0f), Pos = new Vector2(100f + travelPx, 0f), Facing = +1, Scale = Scale,
            Speed = travelPx / Dt, PrevSpeed = travelPx / Dt, Grounded = true,
            SettleSpeed = 20f, SettleExitSpeed = 30f, IdleSpeed = 40f, SettleTime = 0.15f,
            HasObserved = observed, ObservedPhase = observedPhase,
            ServoGain = 25f, ServoMaxRate = 0.5f, RateSlew = slew, ReentryError = 0.2f,
        };

    [Fact]
    public void NoObservation_IsTheLegacyAdvance()
    {
        var (clip, _, gait) = LoadRun();
        float cycle = GaitTiming.CycleDistance(clip, gait, Scale, out _);
        var r = GaitTiming.Advance(Steady(clip, gait, 0.3f, 0f, 0.1f * cycle, observed: false, 0f));
        Assert.Equal(0.1f, r.DeltaPhase, 4);
        Assert.Equal(0f, r.ServoRate);
        Assert.False(r.Reentered);
    }

    [Fact]
    public void SmallError_DiesWithinAStance_AsABoundedRateChange()
    {
        var (clip, _, gait) = LoadRun();
        float cycle = GaitTiming.CycleDistance(clip, gait, Scale, out _);
        float travel = 0.04f * cycle;                 // 2.4 cycles/s feedforward
        float ff = 0.04f / Dt;
        // The feet run 0.04 cycles AHEAD of the phase (the body outran the com path).
        float phase = 0.30f, truth = 0.34f, prevRate = ff;
        int frames = 0; float maxRate = 0f;
        while (Math.Abs(GaitTiming.WrapHalf(truth - phase)) > 0.004f && frames < 60)
        {
            var r = GaitTiming.Advance(Steady(clip, gait, phase, prevRate, travel, observed: true, truth));
            Assert.False(r.Reentered);
            Assert.True(r.DeltaPhase > 0f, "the phase never runs backward");
            maxRate = MathF.Max(maxRate, r.Rate);
            phase += r.DeltaPhase; phase -= MathF.Floor(phase);
            truth += 0.04f; truth -= MathF.Floor(truth);
            prevRate = r.Rate; frames++;
        }
        output.WriteLine($"converged in {frames} frames, peak rate {maxRate:0.00} cycles/s (feedforward {ff:0.00})");
        Assert.InRange(frames, 2, 15);                      // ~40 ms time constant: gone inside a run stance
        Assert.True(maxRate <= ff * 1.5f + 1e-3f, $"rate {maxRate} exceeded the 1.5x clamp");
    }

    [Fact]
    public void Slew_BoundsTheRateChangePerFrame_FeedforwardIncluded()
    {
        var (clip, _, gait) = LoadRun();
        float cycle = GaitTiming.CycleDistance(clip, gait, Scale, out _);
        // A hop: the feedforward wants to jump from 1.5 to 3 cycles/s in one frame.
        var r = GaitTiming.Advance(Steady(clip, gait, 0.3f, 1.5f, 0.05f * cycle, observed: false, 0f, slew: 40f));
        Assert.Equal(1.5f + 40f * Dt, r.Rate, 3);
        // And with an observation pulling the other way the rate still moves at most slew·dt.
        var r2 = GaitTiming.Advance(Steady(clip, gait, 0.3f, 1.5f, 0.05f * cycle, observed: true, 0.25f, slew: 40f));
        Assert.InRange(r2.Rate, 1.5f - 40f * Dt - 1e-3f, 1.5f + 40f * Dt + 1e-3f);
    }

    [Fact]
    public void LargeError_Reenters_AtTheObservation()
    {
        var (clip, _, gait) = LoadRun();
        float cycle = GaitTiming.CycleDistance(clip, gait, Scale, out _);
        var r = GaitTiming.Advance(Steady(clip, gait, 0.30f, 2f, 0.04f * cycle, observed: true, 0.65f));
        Assert.True(r.Reentered);
        Assert.Equal(0.35f, r.DeltaPhase, 4);
        Assert.Equal(0.04f / Dt, r.Rate, 3);          // the feedforward, for the next frame's slew
        // Backward too: the feet say the phase is behind.
        var b = GaitTiming.Advance(Steady(clip, gait, 0.30f, 2f, 0.04f * cycle, observed: true, 0.05f));
        Assert.True(b.Reentered);
        Assert.Equal(-0.25f, b.DeltaPhase, 4);
    }

    [Fact]
    public void Servo_IsOff_WhileSettling()
    {
        var (clip, _, gait) = LoadRun();
        var inp = Steady(clip, gait, 0.30f, 1f, 0.2f, observed: true, 0.40f);
        inp.State = TimingState.Settling; inp.SettleRemaining = 0.1f; inp.SettleTimeLeft = 0.15f;
        inp.Speed = inp.PrevSpeed = 10f;
        var r = GaitTiming.Advance(inp);
        Assert.Equal(0f, r.ServoRate);
        Assert.False(r.Reentered);
    }

    // Observe: a foot planted at the authored offset for progress u reads back that phase.
    [Theory]
    [InlineData(+1, 0.0f)]
    [InlineData(+1, 0.5f)]
    [InlineData(-1, 0.5f)]
    [InlineData(+1, 1.0f)]
    public void Observe_ReadsTheStancePhase_FromThePlantedFootOffset(int facing, float u)
    {
        var (clip, skel, _) = LoadRun();
        Assert.True(ClipStrideTrack.TryCompile(clip, skel, out var track, out string err, anySource: true), err);
        var ft = track.Feet[0];
        var st = ft.Stances[0];
        float expected = st.Touchdown + u * (st.Liftoff - st.Touchdown); expected -= MathF.Floor(expected);
        Vector2 off = st.TdOffset + u * (st.LoOffset - st.TdOffset);
        var body = new Vector2(300f, 200f);
        var plans = new FootPlan[StepPlanner.MaxFeet];
        plans[0] = new FootPlan { Bone = ft.Bone, State = FootPlanState.Stance, HasSupport = true, Weight = 1f,
                                  Target = body + new Vector2(facing * Scale * off.X, Scale * off.Y) };
        // Ask from a phase a little off: the reading is absolute, the vote is a wrapped difference.
        float from = expected + 0.03f; from -= MathF.Floor(from);
        Assert.True(GaitTiming.Observe(track, plans, track.Feet.Length, body, facing, Scale, from, out float observed, out float residual));
        Assert.Equal(expected, observed, 3);
        Assert.Equal(0f, residual, 3);
        // No planted foot → no reading.
        plans[0].State = FootPlanState.Swing;
        Assert.False(GaitTiming.Observe(track, plans, track.Feet.Length, body, facing, Scale, from, out _, out _));
    }
}
