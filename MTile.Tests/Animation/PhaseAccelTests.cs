using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;

namespace MTile.Tests;

// Cadence acceleration handling. Two mechanisms:
//  · PhaseAccelPrior — the SOFT row √λ·(Δφ − Δφ_prev)/(dt²·100): a dt-invariant penalty on
//    phase acceleration, calibrated so λ = 1 charges one re-contact hop about what 1px of
//    planted-foot slip costs. The shipped mechanism (on by default).
//  · MaxPhaseAccel — the optional HARD box |Δφ − Δφ_prev| ≤ MaxPhaseAccel·dt² on the solve
//    and the flight coast. Off by default; tested here as an opt-in.
// Δφ is read back as the per-frame change of State.Phase (equal to the solved / coasted
// step on the locomotion path).
public class PhaseAccelTests
{
    const float Dt = 1f / 60f;

    // The soft penalty does what it says: with λ at its default the run's largest
    // frame-to-frame Δφ change is materially smaller than with λ = 0, and the cadence is
    // still healthy (the row trades hop for a little slip; it must not stall the cycle).
    [Fact]
    public void SoftPrior_ReducesTheLargestHop_AndCadenceStaysHealthy()
    {
        var cfg = AnimSolverConfig.Current;
        float saved = cfg.PhaseAccelPrior;
        Assert.True(saved > 0f, "PhaseAccelPrior is off by default — this test assumes the soft row is armed");
        Assert.Equal(0f, cfg.MaxPhaseAccel);   // the box is an opt-in; the prior must carry this alone
        try
        {
            cfg.PhaseAccelPrior = 0f;
            var free = Trace(90f, frames: 180);
            cfg.PhaseAccelPrior = saved;
            var soft = Trace(90f, frames: 180);

            float hopFree = MaxHop(free, from: 60), hopSoft = MaxHop(soft, from: 60);
            Assert.True(hopSoft < 0.7f * hopFree,
                        $"soft prior barely moved the largest hop: {hopFree:0.0000} → {hopSoft:0.0000}");
            float late = 0f;
            for (int i = 120; i < soft.Count; i++) late += soft[i];
            Assert.True(late > 0.8f, $"cadence unhealthy under the soft prior: {late:0.000} cycles over 60 frames");
        }
        finally { cfg.PhaseAccelPrior = saved; }
    }

    // The soft row is dt-INVARIANT: it acts on the acceleration in cycles/s², so under the
    // default λ the run's largest acceleration — measured in cycles/s² — lands in the same
    // band at 30 and 60 fps (the raw-phase-unit PhaseStepPrior it replaced was 4× weaker at
    // half the frame rate). The band itself is what the row leaves through: the clip's
    // authored re-contact hop (~50–140 cycles/s²), well under the ~850 of a quarter-cycle
    // skip, which the row is there to remove (see AnimSolverConfig.PhaseAccelPrior).
    [Fact]
    public void SoftPrior_IsFrameRateInvariant()
    {
        float a60 = MaxHop(Trace(90f, 180, 1f / 60f), 60) * 3600f;
        float a30 = MaxHop(Trace(90f, 90,  1f / 30f), 30) * 900f;
        Assert.True(a60 < 300f && a30 < 300f, $"max phase acceleration 60fps={a60:0} 30fps={a30:0} cycles/s² — a skip got through");
        float ratio = MathF.Max(a60, a30) / MathF.Max(1f, MathF.Min(a60, a30));
        Assert.True(ratio < 4f, $"not dt-invariant: max phase acceleration 60fps={a60:0} vs 30fps={a30:0} cycles/s²");
    }

    // OPT-IN hard box: with MaxPhaseAccel set, every consecutive Δφ change stays inside
    // MaxPhaseAccel·dt² at walk and run speed, and the cycle still reaches a healthy cadence.
    [Theory]
    [InlineData(25f)]
    [InlineData(90f)]
    public void HardBox_OptIn_RateChangePerFrameNeverExceedsBound_AndCadenceStaysHealthy(float vx)
    {
        var cfg = AnimSolverConfig.Current;
        float saved = cfg.MaxPhaseAccel;
        try
        {
            cfg.MaxPhaseAccel = 40f;
            float aMax = 40f * Dt * Dt;
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
        finally { cfg.MaxPhaseAccel = saved; }
    }

    // A clip change seeds Δφ_prev with the velocity-derived legacy rate, not 0 — a Walk → Run
    // switch mid-locomotion keeps the legs moving (no restart from a standstill). Read
    // directly off the animator's PhaseStep on the switch frame.
    [Fact]
    public void ClipChange_SeedsRateFromVelocity_NoRestartFromZero()
    {
        var anim = RealAnimator();
        var pos = Vector2.Zero;
        for (int i = 0; i < 60; i++) { pos.X += 25f * Dt; anim.Update(Sample(pos, 25f)); }
        Assert.Equal(AnimClip.Walk, anim.State.Clip);
        pos.X += 90f * Dt;
        anim.Update(Sample(pos, 90f));
        Assert.Equal(AnimClip.Run, anim.State.Clip);
        // The seed is |vx|·dt·PhasePerPixel = 90/60·0.01 = 0.015; the first solved step sits
        // near it (the soft prior pulls toward the seed), never near 0.
        Assert.True(anim.PhaseStep > 0.008f, $"cadence restarted from zero at Walk→Run: Δφ={anim.PhaseStep:0.0000}");
    }

    // --- helpers ------------------------------------------------------------------

    private static float MaxHop(List<float> steps, int from)
    {
        float worst = 0f;
        for (int i = Math.Max(1, from); i < steps.Count; i++) worst = MathF.Max(worst, MathF.Abs(steps[i] - steps[i - 1]));
        return worst;
    }

    private static List<float> Trace(float vx, int frames, float dt = Dt)
    {
        var anim = RealAnimator();
        var pos = Vector2.Zero;
        var steps = new List<float>(frames);
        float prev = anim.State.Phase;
        for (int i = 0; i < frames; i++)
        {
            pos.X += vx * dt;
            anim.Update(new CharacterAnimSample(pos, new Vector2(vx, 0f), 1, true, "Standing", "", dt));
            steps.Add(Wrap(anim.State.Phase - prev));
            prev = anim.State.Phase;
        }
        return steps;
    }

    private static float Wrap(float d) => d < -0.5f ? d + 1f : d;

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
