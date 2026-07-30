using System;
using System.IO;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;

namespace MTile.Tests;

// The PRE-CONTACT run window (GroundLocomotionDriver.PreContactGap): the movement FSM's
// StandingState precondition is permissive (engages with the floor up to ProbeSlack away),
// so the sample can say Grounded while the body is still physically descending
// (sample.GroundGap > 0). The animator must NOT cycle the legs in air during that window —
// it holds the run clip at a frozen, pose-matched phase (ClipTimeMode.Hold) and resumes
// the cadence when the gap closes.
public class PreRunAirborneTests
{
    const float Dt = 1f / 60f;

    [Fact]
    public void GroundedRunWithAirGap_HoldsPhase_ThenResumesOnContact()
    {
        var anim = RealAnimator();

        // Fall toward the ground: airborne sample → Fall clip.
        var pos = Vector2.Zero;
        for (int i = 0; i < 10; i++)
        {
            pos.Y += 200f * Dt;
            anim.Update(new CharacterAnimSample(pos, new Vector2(90f, 200f), 1, false, "Falling", "", Dt));
        }
        Assert.Equal(AnimClip.Fall, anim.State.Clip);

        // FSM flips to grounded-run while the feet are still 12px above rest: the driver
        // must pick Run but HOLD the phase (no in-air leg cycling).
        anim.Update(new CharacterAnimSample(pos, new Vector2(90f, 120f), 1, true, "Standing", "", Dt,
                                            groundGap: 12f));
        Assert.Equal(AnimClip.Run, anim.State.Clip);
        float held = anim.State.Phase;
        for (int i = 0; i < 8; i++)
        {
            pos.X += 90f * Dt; pos.Y += 60f * Dt;
            anim.Update(new CharacterAnimSample(pos, new Vector2(90f, 60f), 1, true, "Standing", "", Dt,
                                                groundGap: MathF.Max(0.5f, 12f - i * 1.5f) + 2f));
            Assert.Equal(AnimClip.Run, anim.State.Clip);
            Assert.Equal(held, anim.State.Phase);   // frozen — no cycling before contact
        }

        // Contact (gap ≈ 0): the cadence resumes from the held phase — same clip, so no
        // entry reset — and the cycle advances again.
        float total = 0f, prev = anim.State.Phase;
        for (int i = 0; i < 40; i++)
        {
            pos.X += 90f * Dt;
            anim.Update(new CharacterAnimSample(pos, new Vector2(90f, 0f), 1, true, "Standing", "", Dt));
            float phase = anim.State.Phase;
            float d = phase - prev; if (d < -0.5f) d += 1f;
            total += d; prev = phase;
            Assert.Equal(AnimClip.Run, anim.State.Clip);
        }
        Assert.True(total > 0.3f, $"cadence failed to resume after contact (total phase {total:0.000})");
    }

    // The MatchPose entry: entering the held run from Fall must start the cycle at the
    // phase whose pose best matches what was on screen — i.e. exactly what
    // BestMatchingPhase computes, not phase 0 / stale phase by accident. Pinned by
    // running the same scan here against the emitted pose captured just before entry.
    [Fact]
    public void EntryFromFall_PosePicksClosestPhase()
    {
        var anim = RealAnimator();
        var pos = Vector2.Zero;
        for (int i = 0; i < 30; i++)   // long fall so the fall pose fully settles
        {
            pos.Y += 200f * Dt;
            anim.Update(new CharacterAnimSample(pos, new Vector2(90f, 200f), 1, false, "Falling", "", Dt));
        }
        // Capture the emitted pose (the live pose) at the moment of entry.
        var emitted = new float[anim.Skeleton.Count];
        for (int i = 0; i < emitted.Length; i++) emitted[i] = anim.Pose.Local[i].Rotation;

        anim.Update(new CharacterAnimSample(pos, new Vector2(90f, 120f), 1, true, "Standing", "", Dt,
                                            groundGap: 12f));
        Assert.Equal(AnimClip.Run, anim.State.Clip);

        // Reproduce the 32-way scan against the run clip and assert the animator picked it.
        string dir = FindStatesDir();
        var run = AnimationStore.LoadAll(dir).Find(d => d.Name == "run");
        Assert.NotNull(run);
        var skel = anim.Skeleton;
        var a = skel.CreatePose(); var b = skel.CreatePose();
        var c = skel.CreatePose(); var d4 = skel.CreatePose(); var outP = skel.CreatePose();
        float best = 0f, bestD = float.MaxValue;
        for (int k = 0; k < 32; k++)
        {
            float t = k / 32f;
            AnimationSampler.SampleSmooth(run, t, a, b, c, d4, outP);
            float dist = 0f;
            for (int i = 0; i < skel.Count; i++)
            {
                float e = MathHelper.WrapAngle(outP.Local[i].Rotation - emitted[i]);
                dist += e * e;
            }
            if (dist < bestD) { bestD = dist; best = t; }
        }
        Assert.Equal(best, anim.State.Phase, 3);
    }

    private static CharacterAnimator RealAnimator()
    {
        string dir = FindStatesDir();
        var clips = AnimationStore.LoadAll(dir);
        Assert.NotEmpty(clips);
        return new CharacterAnimator(SkeletonExamples.Biped(), 0.6f, clips);
    }

    private static string FindStatesDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            string c = Path.Combine(d.FullName, "SkeletonStates", "biped");
            if (Directory.Exists(c)) return c;
            d = d.Parent;
        }
        return "SkeletonStates/biped";
    }
}
