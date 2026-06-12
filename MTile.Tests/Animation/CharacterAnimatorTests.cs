using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;

namespace MTile.Tests;

public class CharacterAnimatorTests
{
    // Exercises the ACTUAL authored SkeletonStates/walk.json (not a synthetic clip),
    // so a mislabeled/mis-facing real file is caught. Walking forward must advance the
    // cadence; a frozen total means the labeled stance foot sweeps the wrong way.
    [Fact]
    public void RealWalkJson_AdvancesPhase_WhenWalkingForward()
    {
        string dir = FindStatesDir();
        var walk = AnimationStore.LoadAll(dir).Find(d => d.Name == "walk");
        Assert.True(walk != null, $"walk.json not found under {dir}");

        var skel = SkeletonExamples.Biped();
        // 25 px/s sits in the walk band (12 < v < 40) so SelectClip picks the authored
        // walk clip rather than escalating to Run.
        float right = RunRealClip(skel, walk, +1, 25f);
        float left  = RunRealClip(skel, walk, -1, 25f);

        // Threshold targets the "froze" failure mode (Δφ ≈ 0 every frame because the
        // stance foot label is on the wrong foot). Not gating on amplitude — that's
        // a function of authored Tx tuning that drifts as the clip is iterated on.
        Assert.True(right > 0.15f, $"walking RIGHT froze / barely advanced (total phase {right:0.000})");
        Assert.True(left  > 0.15f, $"walking LEFT froze / barely advanced (total phase {left:0.000})");
    }

    // The authored SkeletonStates/run.json must also advance the cadence both ways at a
    // run-band speed (so SelectClip escalates Walk -> Run).
    [Fact]
    public void RealRunJson_AdvancesPhase_BothDirections()
    {
        string dir = FindStatesDir();
        var run = AnimationStore.LoadAll(dir).Find(d => d.Name == "run");
        Assert.True(run != null, $"run.json not found under {dir}");

        var skel = SkeletonExamples.Biped();
        Assert.True(RunRealClip(skel, run, +1, 90f) > 0.5f, "run froze walking right");
        Assert.True(RunRealClip(skel, run, -1, 90f) > 0.5f, "run froze walking left");
    }

    // Simulate moving in `facing` direction at `speed` px/s for 40 frames; return the
    // unwrapped phase total. The clip is bound by its Type, so SelectClip must resolve
    // to that clip for the speed used.
    private static float RunRealClip(Skeleton skel, AnimationDocument clip, int facing, float speed)
    {
        var anim = new CharacterAnimator(skel, 0.6f, new[] { clip });
        float dt = 1f / 30f, vx = speed * facing, x = 0f, prev = anim.State.Phase, total = 0f;
        for (int i = 0; i < 40; i++)
        {
            x += vx * dt;
            anim.Update(new CharacterAnimSample(
                new Vector2(x, 0f), new Vector2(vx, 0f), facing, true, "WalkState", "", dt));
            float p = anim.State.Phase, d = p - prev;
            if (d < -0.5f) d += 1f;
            total += d;
            prev = p;
        }
        return total;
    }

    private static string FindStatesDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            string c = Path.Combine(d.FullName, "SkeletonStates");
            if (Directory.Exists(c)) return c;
            d = d.Parent;
        }
        return "SkeletonStates";
    }

    // Walking forward with a contact-labeled walk clip should advance the phase via
    // the cadence solver. Regression guard for the "frozen animation" bug: if the
    // planted foot is the one sweeping *forward* (wrong stance foot), the forward-only
    // solver can't cancel the body's motion and returns Δφ = 0 every frame.
    [Fact]
    public void Cadence_AdvancesPhase_WhenWalkingForward()
    {
        var skel = SkeletonExamples.Biped();
        // vx is in the run band, so bind a Run clip too (escalation has no procedural fallback).
        var anim = new CharacterAnimator(skel, 0.6f, new[] { BuildWalkClip(skel), BuildRunClip(skel) });

        float dt = 1f / 30f, vx = 120f, x = 0f, prev = anim.State.Phase, total = 0f;
        for (int i = 0; i < 30; i++)
        {
            x += vx * dt;
            anim.Update(new CharacterAnimSample(
                new Vector2(x, 0f), new Vector2(vx, 0f), +1, true, "WalkState", "", dt));

            float p = anim.State.Phase;
            float d = p - prev;
            if (d < -0.5f) d += 1f;   // unwrap the cycle boundary
            total += d;
            prev = p;
        }

        Assert.True(total > 1f, $"phase should sweep multiple cycles while walking; advanced only {total}");
    }

    // Cadence is distance-driven: faster movement advances more phase over the same
    // time ("the pace of running matches the player's movement"). The scaling is
    // sub-linear here because the authored stance foot traces an arc (its Y changes),
    // and a 1-DOF phase knob can't cancel both axes — that residual is what IK
    // (Phase 5) removes. So we assert clear responsiveness, not exact linearity.
    [Fact]
    public void Cadence_RateScalesWithSpeed()
    {
        var skel = SkeletonExamples.Biped();
        // Both speeds stay in the walk band (< RunSpeedThreshold) so SelectClip keeps
        // the synthetic Walk clip rather than escalating to Run.
        float slow = WalkPhase(skel, 20f, 30);
        float fast = WalkPhase(skel, 35f, 30);

        Assert.True(slow > 0.2f, $"slow walk barely advanced ({slow})");
        Assert.InRange(fast / slow, 1.25f, 2.3f);
    }

    // Across many foot swaps the phase should keep advancing within the per-frame
    // bound and never stall for more than the initial capture frame — the feathered
    // crossover should not reintroduce the swap freeze.
    [Fact]
    public void Cadence_AdvancesSmoothly_AcrossSwaps()
    {
        var skel = SkeletonExamples.Biped();
        // vx is in the run band, so bind a Run clip too (escalation has no procedural fallback).
        var anim = new CharacterAnimator(skel, 0.6f, new[] { BuildWalkClip(skel), BuildRunClip(skel) });

        float dt = 1f / 30f, vx = 140f, x = 0f, prev = anim.State.Phase, maxStep = 0f;
        int stalls = 0;
        for (int i = 0; i < 60; i++)
        {
            x += vx * dt;
            anim.Update(new CharacterAnimSample(
                new Vector2(x, 0f), new Vector2(vx, 0f), +1, true, "WalkState", "", dt));
            float p = anim.State.Phase, d = p - prev;
            if (d < -0.5f) d += 1f;
            maxStep = MathF.Max(maxStep, d);
            if (d < 1e-4f) stalls++;
            prev = p;
        }

        Assert.True(maxStep <= 0.25f + 1e-3f, $"phase step exceeded the bound: {maxStep}");
        Assert.True(stalls <= 2, $"phase stalled on {stalls} frames (swap freeze regression?)");
    }

    // Regression: in LedgeGrabState the body's velocity is governed by a spring +
    // damper around the hang Y. Vy oscillates between negative and zero each frame
    // as the ring-down settles (e.g. −30, 0, −20, 0, ...). The clip-selector used
    // to pick `Vy < 0 ? Jump : Fall` for any airborne state, so the clip flipped
    // every frame and produced a visible Jump/Fall flicker. The animator must now
    // recognise LedgeGrab and lock to a stable clip independent of Vy sign.
    [Fact]
    public void LedgeGrabState_OscillatingVy_DoesNotFlipClipPerFrame()
    {
        var skel = SkeletonExamples.Biped();
        var anim = new CharacterAnimator(skel, 0.6f, new[] {
            BuildWalkClip(skel),
            BuildDummyClip(skel, "fall", "Fall"),
            BuildDummyClip(skel, "jump", "Jump"),
            BuildDummyClip(skel, "vault", "Vault"),
        });

        // 12 frames of the trace from a real grab-settle, alternating Vy < 0 and Vy = 0.
        float[] vyTrace = { -30f, 0f, -23f, 0f, -16f, 0f, -10f, 0f, -7f, 0f, -4f, 0f };
        float dt = 1f / 30f;
        var clips = new System.Collections.Generic.List<AnimClip>();
        foreach (var vy in vyTrace)
        {
            anim.Update(new CharacterAnimSample(
                position: new Vector2(135f, 27.5f),
                velocity: new Vector2(0f, vy),
                facing: +1,
                grounded: false,
                movementState: "LedgeGrabState",
                action: "",
                dt: dt));
            clips.Add(anim.State.Clip);
        }

        // The clip must be constant across the whole ring-down — no per-frame flip.
        Assert.True(clips.TrueForAll(c => c == clips[0]),
            $"LedgeGrabState clip flipped during Vy ring-down: {string.Join(",", clips)}");
        // And it must not be the raw airborne Jump/Fall decision (that's the bug we
        // just fixed). Fall is the current placeholder for "hanging on a ledge".
        Assert.Equal(AnimClip.Fall, clips[0]);
    }

    // Same regression for the pull-up: LedgePull is the active pull-up after grab and
    // should map to a stable guided-traversal clip (Vault), not flip on Vy sign.
    [Fact]
    public void LedgePullState_OscillatingVy_LocksToVaultClip()
    {
        var skel = SkeletonExamples.Biped();
        var anim = new CharacterAnimator(skel, 0.6f, new[] {
            BuildWalkClip(skel),
            BuildDummyClip(skel, "fall", "Fall"),
            BuildDummyClip(skel, "jump", "Jump"),
            BuildDummyClip(skel, "vault", "Vault"),
        });

        float[] vyTrace = { -100f, 0f, -50f, 0f, -25f, 0f };
        float dt = 1f / 30f;
        var clips = new System.Collections.Generic.List<AnimClip>();
        foreach (var vy in vyTrace)
        {
            anim.Update(new CharacterAnimSample(
                position: new Vector2(135f, 27.5f),
                velocity: new Vector2(0f, vy),
                facing: +1,
                grounded: false,
                movementState: "LedgePullState",
                action: "",
                dt: dt));
            clips.Add(anim.State.Clip);
        }

        Assert.True(clips.TrueForAll(c => c == AnimClip.Vault),
            $"LedgePullState should lock to Vault clip; got {string.Join(",", clips)}");
    }

    // Total (unwrapped) phase advanced after walking forward at `vx` for `frames`.
    private static float WalkPhase(Skeleton skel, float vx, int frames)
    {
        var anim = new CharacterAnimator(skel, 0.6f, new[] { BuildWalkClip(skel) });
        float dt = 1f / 30f, x = 0f, prev = anim.State.Phase, total = 0f;
        for (int i = 0; i < frames; i++)
        {
            x += vx * dt;
            anim.Update(new CharacterAnimSample(
                new Vector2(x, 0f), new Vector2(vx, 0f), +1, true, "WalkState", "", dt));
            float p = anim.State.Phase, d = p - prev;
            if (d < -0.5f) d += 1f;
            total += d;
            prev = p;
        }
        return total;
    }

    // Mirrors SkeletonStates/walk.json: legs scissor ±1.0; foot_r is the stance foot
    // over [0, 0.5] (it sweeps backward there), foot_l over [0.5, 1].
    private static AnimationDocument BuildWalkClip(Skeleton skel) => BuildLocoClip(skel, "walk", "Walk");
    private static AnimationDocument BuildRunClip(Skeleton skel)  => BuildLocoClip(skel, "run",  "Run");

    // Single-keyframe stub clip — just needs to exist so CharacterAnimator can bind
    // SelectClip's choice without throwing. Pose is the rest pose.
    private static AnimationDocument BuildDummyClip(Skeleton skel, string name, string type)
    {
        var clip = new AnimationDocument { Name = name, Type = type, Duration = 1f, Loop = true };
        var pose = skel.CreatePose();
        clip.Keyframes.Add(new AnimationKeyframe
        {
            Time = 0f,
            Bones = PoseData.Capture(pose),
            Contacts = new List<ContactLabel>(),
        });
        return clip;
    }

    private static AnimationDocument BuildLocoClip(Skeleton skel, string name, string type)
    {
        var clip = new AnimationDocument { Name = name, Type = type, Duration = 0.8f, Loop = true };
        clip.Keyframes.Add(Kf(skel, 0f,   legL:  1.0f, legR: -1.0f, plant: "foot_r"));
        clip.Keyframes.Add(Kf(skel, 0.5f, legL: -1.0f, legR:  1.0f, plant: "foot_l"));
        clip.Keyframes.Add(Kf(skel, 1f,   legL:  1.0f, legR: -1.0f, plant: "foot_r"));
        return clip;
    }

    private static AnimationKeyframe Kf(Skeleton skel, float t, float legL, float legR, string plant)
    {
        var p = skel.CreatePose();
        SetRot(skel, p, "leg_l_upper", legL);
        SetRot(skel, p, "leg_r_upper", legR);
        return new AnimationKeyframe
        {
            Time = t,
            Bones = PoseData.Capture(p),
            Contacts = new List<ContactLabel> { new() { Node = plant, Weight = 1f } },
        };
    }

    private static void SetRot(Skeleton skel, SkeletonPose p, string bone, float rot)
    {
        int i = skel.IndexOf(bone);
        var t = p.Local[i];
        t.Rotation = rot;
        p.SetLocal(i, t);
    }
}
