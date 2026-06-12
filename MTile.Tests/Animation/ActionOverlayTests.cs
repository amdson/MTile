using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;

namespace MTile.Tests;

// Headless tests of the action overlay layer: an action clip (Type = action class
// name, Region-masked) lerps over the movement base pose, driven by deterministic
// sim time (sample.ActionTime) and gated by the eased ActionWeight.
public class ActionOverlayTests
{
    private const float Dt = 1f / 30f;
    private const float WalkVx = 25f;          // walk band (12 < v < 40)
    private const float SlashArm = 2.0f;       // distinctive arm_r_upper rotation in the slash clip

    // While walking and slashing: the masked arm converges to the slash pose, the
    // legs keep cycling (cadence alive), and the overlay weight saturates.
    [Fact]
    public void WalkPlusSlash_ArmsOverridden_LegsKeepCycling()
    {
        var skel = SkeletonExamples.Biped();
        var anim = NewAnimator(skel);
        int armR = skel.IndexOf("arm_r_upper");
        int legL = skel.IndexOf("leg_l_upper");

        var st = new DriveState();
        Drive(anim, skel, ref st, frames: 10, action: "");          // settle into the walk
        float legBefore = anim.Pose.Local[legL].Rotation;
        Drive(anim, skel, ref st, frames: 15, action: "GroundSlash1");

        Assert.True(MathF.Abs(anim.Pose.Local[armR].Rotation - SlashArm) < 0.2f,
            $"arm should converge to the slash pose; got {anim.Pose.Local[armR].Rotation}");
        Assert.True(anim.State.ActionWeight > 0.9f, $"weight should saturate; got {anim.State.ActionWeight}");
        float legNow = anim.Pose.Local[legL].Rotation;
        Drive(anim, skel, ref st, frames: 3, action: "GroundSlash1");
        Assert.True(MathF.Abs(anim.Pose.Local[legL].Rotation - legNow) > 1e-4f
                 || MathF.Abs(legNow - legBefore) > 1e-4f,
            "legs froze while slashing — cadence should keep cycling under an UpperBody overlay");
    }

    // The overlay must not touch unmasked bones at all: lower body locals stay
    // bit-identical to a control animator fed the same samples with no action.
    [Fact]
    public void LowerBody_Untouched_ByUpperBodyOverlay()
    {
        var skel = SkeletonExamples.Biped();
        var slashing = NewAnimator(skel);
        var control  = NewAnimator(skel);

        var stA = new DriveState();
        var stB = new DriveState();
        for (int i = 0; i < 25; i++)
        {
            Drive(slashing, skel, ref stA, frames: 1, action: "GroundSlash1");
            Drive(control,  skel, ref stB, frames: 1, action: "");
        }

        foreach (var bone in new[] { "hip", "leg_l_upper", "leg_l_lower", "foot_l",
                                     "leg_r_upper", "leg_r_lower", "foot_r" })
        {
            int b = skel.IndexOf(bone);
            Assert.Equal(control.Pose.Local[b].Rotation,    slashing.Pose.Local[b].Rotation);
            Assert.Equal(control.Pose.Local[b].Translation, slashing.Pose.Local[b].Translation);
            Assert.Equal(control.Pose.Local[b].Scale,       slashing.Pose.Local[b].Scale);
        }
    }

    // NullAction/ReadyAction/RecoveryAction/None read as "no action": weight stays 0
    // even with a bound slash clip.
    [Theory]
    [InlineData("None")]
    [InlineData("NullAction")]
    [InlineData("ReadyAction")]
    [InlineData("RecoveryAction")]
    public void InactiveActions_NoOverlay(string action)
    {
        var skel = SkeletonExamples.Biped();
        var anim = NewAnimator(skel);
        var st = new DriveState();
        Drive(anim, skel, ref st, frames: 20, action: action);
        Assert.Equal(0f, anim.State.ActionWeight);
    }

    // An action with no authored clip gets no overlay (exact-name mapping, no fallback).
    [Fact]
    public void MissingClip_NoOverlay()
    {
        var skel = SkeletonExamples.Biped();
        var anim = NewAnimator(skel);
        var st = new DriveState();
        Drive(anim, skel, ref st, frames: 20, action: "StabAction");
        Assert.Equal(0f, anim.State.ActionWeight);
    }

    // When the action ends the overlay fades out monotonically from the last sampled
    // overlay pose — no jump, no NaN, arm returns toward the walk pose.
    [Fact]
    public void ActionEnd_FadesOut_FromLastPose()
    {
        var skel = SkeletonExamples.Biped();
        var anim = NewAnimator(skel);
        int armR = skel.IndexOf("arm_r_upper");

        var st = new DriveState();
        Drive(anim, skel, ref st, frames: 15, action: "GroundSlash1");
        Assert.True(anim.State.ActionWeight > 0.9f);

        float prevW = anim.State.ActionWeight;
        float prevArm = anim.Pose.Local[armR].Rotation;
        for (int i = 0; i < 30; i++)
        {
            Drive(anim, skel, ref st, frames: 1, action: "RecoveryAction");
            float w = anim.State.ActionWeight;
            Assert.True(w <= prevW + 1e-6f, $"weight rose during fade-out ({prevW} -> {w})");
            Assert.False(float.IsNaN(anim.Pose.Local[armR].Rotation));
            prevW = w;
        }
        Assert.Equal(0f, anim.State.ActionWeight);
        Assert.True(MathF.Abs(anim.Pose.Local[armR].Rotation) < MathF.Abs(prevArm),
            "arm should ease back toward the walk pose after the action ends");
    }

    // A combo rebind (GroundSlash1 -> GroundSlash3) swaps the clip without the weight
    // dipping; the pose moves toward the new clip's values.
    [Fact]
    public void ComboRebind_NoWeightDip()
    {
        var skel = SkeletonExamples.Biped();
        var anim = NewAnimator(skel);
        int armR = skel.IndexOf("arm_r_upper");

        var st = new DriveState();
        Drive(anim, skel, ref st, frames: 10, action: "GroundSlash1");
        Assert.True(anim.State.ActionWeight > 0.9f);

        st.ActionTime = 0f;   // sim resets TimeInState on the new action's Enter
        for (int i = 0; i < 12; i++)
        {
            Drive(anim, skel, ref st, frames: 1, action: "GroundSlash3");
            Assert.True(anim.State.ActionWeight > 0.9f,
                $"weight dipped to {anim.State.ActionWeight} during combo rebind");
        }
        // BlendToward lerps rotation along the shortest angular path, so the eased
        // value may land at the target plus a full turn — compare modulo 2π.
        float diff = MathF.IEEERemainder(anim.Pose.Local[armR].Rotation - (-1.5f), 2f * MathF.PI);
        Assert.True(MathF.Abs(diff) < 0.2f,
            $"arm should converge to the second clip's pose; got {anim.Pose.Local[armR].Rotation}");
    }

    // ActionTime far past a Loop=false clip's duration holds the final keyframe.
    [Fact]
    public void ClipOutlasted_HoldsFinalPose()
    {
        var skel = SkeletonExamples.Biped();
        var anim = NewAnimator(skel);
        int armR = skel.IndexOf("arm_r_upper");

        // RampSlash ramps 0.5 -> 2.0 over its 0.14s; hold ActionTime at 2x duration.
        var st = new DriveState { ActionTime = 0.28f, FreezeActionTime = true };
        Drive(anim, skel, ref st, frames: 20, action: "RampSlash");
        Assert.True(MathF.Abs(anim.Pose.Local[armR].Rotation - SlashArm) < 0.2f,
            $"pose should hold the final keyframe past the clip end; got {anim.Pose.Local[armR].Rotation}");
    }

    // --- harness ------------------------------------------------------------

    private struct DriveState
    {
        public float X;
        public float ActionTime;
        public bool  FreezeActionTime;
    }

    private static CharacterAnimator NewAnimator(Skeleton skel)
        => new(skel, 0.6f, new[]
        {
            BuildWalkClip(skel),
            BuildSlashClip(skel, "slash1", "GroundSlash1", armStart: SlashArm, armEnd: SlashArm),
            BuildSlashClip(skel, "slash3", "GroundSlash3", armStart: -1.5f,   armEnd: -1.5f),
            BuildSlashClip(skel, "ramp",   "RampSlash",    armStart: 0.5f,    armEnd: SlashArm),
        });

    // Walk forward at WalkVx feeding `action`; ActionTime advances with dt unless frozen.
    private static void Drive(CharacterAnimator anim, Skeleton skel, ref DriveState st,
                              int frames, string action)
    {
        for (int i = 0; i < frames; i++)
        {
            st.X += WalkVx * Dt;
            anim.Update(new CharacterAnimSample(
                new Vector2(st.X, 0f), new Vector2(WalkVx, 0f), +1, true,
                "WalkState", action, Dt, st.ActionTime));
            if (!st.FreezeActionTime) st.ActionTime += Dt;
        }
    }

    // Mirrors CharacterAnimatorTests.BuildLocoClip: legs scissor with planted feet so
    // the cadence solver runs under the overlay.
    private static AnimationDocument BuildWalkClip(Skeleton skel)
    {
        var clip = new AnimationDocument { Name = "walk", Type = "Walk", Duration = 0.8f, Loop = true };
        clip.Keyframes.Add(LocoKf(skel, 0f,   legL:  1.0f, legR: -1.0f, plant: "foot_r"));
        clip.Keyframes.Add(LocoKf(skel, 0.5f, legL: -1.0f, legR:  1.0f, plant: "foot_l"));
        clip.Keyframes.Add(LocoKf(skel, 1f,   legL:  1.0f, legR: -1.0f, plant: "foot_r"));
        return clip;
    }

    // UpperBody overlay clip ramping arm_r_upper from armStart to armEnd. No contacts —
    // action overlays are constraint-free by design.
    private static AnimationDocument BuildSlashClip(Skeleton skel, string name, string type,
                                                    float armStart, float armEnd)
    {
        var clip = new AnimationDocument
        {
            Name = name, Type = type, Duration = 0.14f, Loop = false,
            Region = AnimRegion.UpperBody,
        };
        clip.Keyframes.Add(ArmKf(skel, 0f, armStart));
        clip.Keyframes.Add(ArmKf(skel, 1f, armEnd));
        return clip;
    }

    private static AnimationKeyframe ArmKf(Skeleton skel, float t, float arm)
    {
        var p = skel.CreatePose();
        SetRot(skel, p, "arm_r_upper", arm);
        return new AnimationKeyframe { Time = t, Bones = PoseData.Capture(p) };
    }

    private static AnimationKeyframe LocoKf(Skeleton skel, float t, float legL, float legR, string plant)
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
