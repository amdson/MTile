using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;

namespace MTile.Tests;

// Cadence acceleration. Since the timing stage (Plans/ANIMATION_TIMING_STAGE.md, chunk 5)
// the phase advances by body travel over the clip's authored stride, so a steady run has a
// steady rate by construction; the joint solve's soft acceleration prior and opt-in hard box
// no longer act on Δφ (locked at 0 there) and their tests were retired with them (T5 deletes
// the rows). What remains: the phase acceleration must be small and frame-rate invariant,
// and a clip change must not restart the cadence from zero. Δφ is read back as the per-frame
// change of State.Phase.
public class PhaseAccelTests
{
    const float Dt = 1f / 60f;

    // The soft row is dt-INVARIANT: it acts on the acceleration in cycles/s², so under the
    // default λ the run's largest acceleration — measured in cycles/s² — lands in the same
    // band at 30 and 60 fps (the raw-phase-unit PhaseStepPrior it replaced was 4× weaker at
    // half the frame rate). The band itself is what the row leaves through: the clip's
    // authored re-contact hop (~50–140 cycles/s²), well under the ~850 of a quarter-cycle
    // skip (the retired seed search could produce one; travel timing cannot).
    [Fact]
    public void SoftPrior_IsFrameRateInvariant()
    {
        float a60 = MaxHop(Trace(90f, 180, 1f / 60f), 60) * 3600f;
        float a30 = MaxHop(Trace(90f, 90,  1f / 30f), 30) * 900f;
        Assert.True(a60 < 300f && a30 < 300f, $"max phase acceleration 60fps={a60:0} 30fps={a30:0} cycles/s² — a skip got through");
        float ratio = MathF.Max(a60, a30) / MathF.Max(1f, MathF.Min(a60, a30));
        Assert.True(ratio < 4f, $"not dt-invariant: max phase acceleration 60fps={a60:0} vs 30fps={a30:0} cycles/s²");
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
