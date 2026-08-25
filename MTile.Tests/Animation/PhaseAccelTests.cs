using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;

namespace MTile.Tests;

// The cadence acceleration bound (AnimSolverConfig.MaxPhaseAccel): the per-frame phase rate
// Δφ may only change by at most MaxPhaseAccel from one frame to the next — the rate RAMPS,
// it can't hop. Enforced as a box on Δφ in the cadence solve and the same cap on the flight
// coast. Δφ is read back as the per-frame change of State.Phase (equal to the solved /
// coasted step on the locomotion path).
public class PhaseAccelTests
{
    const float Dt = 1f / 60f;

    // Cold start (Idle → cycle) then steady locomotion, at walk and run speed: every
    // consecutive Δφ change stays inside the bound, and the cycle still reaches a healthy
    // cadence — the box shapes the rate, it doesn't stall it.
    [Theory]
    [InlineData(25f)]
    [InlineData(90f)]
    public void Bounded_RateChangePerFrame_NeverExceedsMaxPhaseAccel_AndCadenceStaysHealthy(float vx)
    {
        float aMax = PerFrameBox();
        Assert.True(aMax > 0f, "MaxPhaseAccel is off by default — this test assumes the box is armed");

        var steps = Trace(vx, frames: 150);
        float worst = 0f; int worstAt = -1;
        for (int i = 1; i < steps.Count; i++)
        {
            float a = MathF.Abs(steps[i] - steps[i - 1]);
            if (a > worst) { worst = a; worstAt = i; }
        }
        Assert.True(worst <= aMax + 1e-5f,
                    $"Δφ hopped by {worst:0.0000} at frame {worstAt} (bound {aMax:0.0000}) — vx={vx}");

        float late = 0f;
        for (int i = 90; i < steps.Count; i++) late += steps[i];
        Assert.True(late > 0.8f, $"cadence unhealthy under the box: {late:0.000} cycles over the last 60 frames at vx={vx}");
    }

    // The scenario is real: with the box OFF the re-contact hop after a flight window (run)
    // / the foot-swap hop (walk) exceeds the bound by a wide margin — so the test above is
    // proving the box does something, not that the solve was already smooth.
    [Theory]
    [InlineData(25f)]
    [InlineData(90f)]
    public void Control_BoxOff_RateHopsExist(float vx)
    {
        var cfg = AnimSolverConfig.Current;
        float saved = cfg.MaxPhaseAccel, box = PerFrameBox();
        try
        {
            cfg.MaxPhaseAccel = 0f;
            var steps = Trace(vx, frames: 150);
            float worst = 0f;
            for (int i = 1; i < steps.Count; i++) worst = MathF.Max(worst, MathF.Abs(steps[i] - steps[i - 1]));
            Assert.True(worst > 1.5f * box, $"no hop to bound: worst Δ(Δφ) {worst:0.0000} with the box off, per-frame bound {box:0.0000}");
        }
        finally { cfg.MaxPhaseAccel = saved; }
    }

    // A clip change seeds Δφ_prev with the velocity-derived legacy rate, not 0: the first
    // solved step after Idle → Run starts from that seed and ramps up from there, and a
    // Walk → Run switch mid-locomotion keeps the legs moving (no restart from zero).
    [Fact]
    public void ClipChange_SeedsRateFromVelocity_NoRestartFromZero()
    {
        float aMax = PerFrameBox();
        var anim = RealAnimator();
        var pos = Vector2.Zero;
        for (int i = 0; i < 30; i++) anim.Update(Sample(pos, 0f));
        Assert.Equal(AnimClip.Idle, anim.State.Clip);

        // Idle → Run: the seed is |vx|·dt·PhasePerPixel = 90/60·0.01 = 0.015.
        float before = anim.State.Phase;
        pos.X += 90f * Dt;
        anim.Update(Sample(pos, 90f));
        Assert.Equal(AnimClip.Run, anim.State.Clip);
        float first = Wrap(anim.State.Phase - before);
        Assert.InRange(first, 0.015f - aMax - 1e-5f, 0.015f + aMax + 1e-5f);

        // Walk (25) for a while, then Run (90): the step right after the switch is within
        // one acceleration of the walk's last step's neighbourhood — never near 0.
        anim = RealAnimator();
        pos = Vector2.Zero;
        for (int i = 0; i < 60; i++) { pos.X += 25f * Dt; anim.Update(Sample(pos, 25f)); }
        Assert.Equal(AnimClip.Walk, anim.State.Clip);
        before = anim.State.Phase;
        pos.X += 90f * Dt;
        anim.Update(Sample(pos, 90f));
        Assert.Equal(AnimClip.Run, anim.State.Clip);
        float switchStep = Wrap(anim.State.Phase - before);
        Assert.True(switchStep >= 0.015f - aMax - 1e-5f, $"cadence restarted from zero at Walk→Run: Δφ={switchStep:0.0000}");
    }

    // --- helpers ------------------------------------------------------------------

    private static List<float> Trace(float vx, int frames)
    {
        var anim = RealAnimator();
        var pos = Vector2.Zero;
        var steps = new List<float>(frames);
        float prev = anim.State.Phase;
        for (int i = 0; i < frames; i++)
        {
            pos.X += vx * Dt;
            anim.Update(Sample(pos, vx));
            steps.Add(Wrap(anim.State.Phase - prev));
            prev = anim.State.Phase;
        }
        return steps;
    }

    private static float Wrap(float d) => d < -0.5f ? d + 1f : d;

    // The knob is in cycles/s²; the per-frame |Δφ − Δφ_prev| box at this test's dt is a·dt².
    private static float PerFrameBox() => AnimSolverConfig.Current.MaxPhaseAccel * Dt * Dt;

    private static CharacterAnimSample Sample(Vector2 pos, float vx)
        => new(pos, new Vector2(vx, 0f), 1, true, "Standing", "", Dt);

    private static CharacterAnimator RealAnimator()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "SkeletonStates", "biped"))) d = d.Parent;
        Assert.NotNull(d);
        var clips = AnimationStore.LoadAll(Path.Combine(d.FullName, "SkeletonStates", "biped"));
        Assert.NotEmpty(clips);
        return new CharacterAnimator(SkeletonExamples.Biped(), 0.6f, clips);
    }
}
