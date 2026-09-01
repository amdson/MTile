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
    // Tolerance for the "unmasked bones didn't move" comparison — see
    // LowerBody_Untouched_ByUpperBodyOverlay for why this isn't exact equality.
    // Absolute difference, not xUnit's `precision` overload: that one rounds to N
    // decimals before comparing, so two values a hair apart still fail when they
    // straddle a rounding boundary (0.03045 vs 0.03044 at precision 4).
    private const float UntouchedTolerance = 1e-3f;

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

    // The overlay must not touch unmasked bones: lower body locals track a control
    // animator fed the same samples with no action.
    //
    // Compared to a tolerance rather than bit-identically. The two animators do not
    // execute an identical instruction sequence — the slashing one runs a blend over
    // every bone with the lower body's weight at zero — so the unmasked bones come
    // back a float rounding apart even though nothing moved them, and asserting exact
    // equality made this fail on FP noise.
    //
    // Measured worst case across all 7 bones x 5 components: 2.0e-5 rad, at
    // leg_l_upper.rot. The 1e-3 tolerance is 50x that and still 0.057 degrees — a
    // bone the overlay actually drove would move by a visible fraction of a radian,
    // three orders of magnitude clear of this. Re-measure by setting the tolerance to
    // 0: the failure prints the worst offender.
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

        float worst = 0f; string worstAt = "none";
        void Track(string bone, string what, float expect, float actual)
        {
            float d = MathF.Abs(expect - actual);
            if (d > worst) { worst = d; worstAt = $"{bone}.{what} ({expect} vs {actual})"; }
        }

        foreach (var bone in new[] { "hip", "leg_l_upper", "leg_l_lower", "foot_l",
                                     "leg_r_upper", "leg_r_lower", "foot_r" })
        {
            int b = skel.IndexOf(bone);
            var expect = control.Pose.Local[b];
            var actual = slashing.Pose.Local[b];
            Track(bone, "rot",   expect.Rotation,      actual.Rotation);
            Track(bone, "posX",  expect.Translation.X, actual.Translation.X);
            Track(bone, "posY",  expect.Translation.Y, actual.Translation.Y);
            Track(bone, "sclX",  expect.Scale.X,       actual.Scale.X);
            Track(bone, "sclY",  expect.Scale.Y,       actual.Scale.Y);
        }

        Assert.True(worst < UntouchedTolerance,
            $"lower body moved under an UpperBody overlay: worst delta {worst:G4} at {worstAt} "
            + $"(tolerance {UntouchedTolerance:G4})");
    }

    // A graded overlay (OffRegionWeight > 0) drives its OFF-region bones a fraction of the
    // way toward the overlay pose, while its in-region bones still go to full weight. End-to-
    // end check of the per-bone graded blend (EaseSlot fill + PaintMotionLayer lerp).
    [Fact]
    public void GradedOverlay_PartiallyDrives_OffRegionBones()
    {
        var skel = SkeletonExamples.Biped();
        int armR = skel.IndexOf("arm_r_upper");
        int legL = skel.IndexOf("leg_l_upper");
        const float LegBase = 1.0f, LegOverlay = 0.0f, Off = 0.3f;

        // Static FullBody base holds the leg at LegBase (no contacts -> no cadence to perturb
        // it); the UpperBody cast sets the same leg to LegOverlay with a 0.3 off-region weight.
        var anim = new CharacterAnimator(skel, 0.6f, new[]
        {
            StaticBase(skel, "Walk", legRot: LegBase),
            GradedCast(skel, "GradedCast", arm: SlashArm, legRot: LegOverlay, off: Off),
        });

        var st = new DriveState();
        Drive(anim, skel, ref st, frames: 60, action: "GradedCast");   // saturate the ease-in
        Assert.True(anim.State.ActionWeight > 0.98f, $"weight should saturate; got {anim.State.ActionWeight}");

        // In-region arm: full weight -> reaches the overlay pose.
        Assert.True(MathF.Abs(anim.Pose.Local[armR].Rotation - SlashArm) < 0.1f,
            $"in-region arm should reach the overlay pose; got {anim.Pose.Local[armR].Rotation}");
        // Off-region leg: blended ~Off of the way from base to overlay.
        float expectLeg = MathHelper.Lerp(LegBase, LegOverlay, Off);   // lerp(1,0,0.3) = 0.7
        Assert.True(MathF.Abs(anim.Pose.Local[legL].Rotation - expectLeg) < 0.03f,
            $"off-region leg should sit at lerp(base,overlay,{Off})={expectLeg}; got {anim.Pose.Local[legL].Rotation}");
    }

    // OffRegionWeight defaults to 0 -> a hard mask: off-region bones stay bit-identical to a
    // no-overlay control. Regression guard for the legacy binary-mask behavior.
    [Fact]
    public void GradedOverlay_ZeroOff_IsHardMask()
    {
        var skel = SkeletonExamples.Biped();
        int legL = skel.IndexOf("leg_l_upper");
        AnimationDocument[] Clips() => new[]
        {
            StaticBase(skel, "Walk", legRot: 1.0f),
            GradedCast(skel, "HardCast", arm: SlashArm, legRot: 0.0f, off: 0f),
        };
        var graded  = new CharacterAnimator(skel, 0.6f, Clips());
        var control = new CharacterAnimator(skel, 0.6f, Clips());

        var stA = new DriveState();
        var stB = new DriveState();
        for (int i = 0; i < 30; i++)
        {
            Drive(graded,  skel, ref stA, frames: 1, action: "HardCast");
            Drive(control, skel, ref stB, frames: 1, action: "");
        }
        Assert.Equal(control.Pose.Local[legL].Rotation, graded.Pose.Local[legL].Rotation);
    }

    // A graded overlay over a cadence-driven walk: the off-region legs are partly pulled by the
    // overlay yet keep cycling. The graded per-bone weight feeds the cadence Jacobian's base-
    // blend product, so the solver must still advance rather than freeze/NaN.
    [Fact]
    public void GradedOverlay_OverWalk_CadenceStillAdvances()
    {
        var skel = SkeletonExamples.Biped();
        int legL = skel.IndexOf("leg_l_upper");
        var anim = new CharacterAnimator(skel, 0.6f, new[]
        {
            BuildWalkClip(skel),                                       // contacts -> cadence runs
            GradedCast(skel, "GradedCast", arm: SlashArm, legRot: 0.0f, off: 0.3f),
        });

        var st = new DriveState();
        Drive(anim, skel, ref st, frames: 20, action: "GradedCast");
        float a = anim.Pose.Local[legL].Rotation;
        Assert.False(float.IsNaN(a), "graded overlay produced NaN legs under cadence");
        Drive(anim, skel, ref st, frames: 5, action: "GradedCast");
        Assert.True(MathF.Abs(a - anim.Pose.Local[legL].Rotation) > 1e-4f,
            "legs froze under a graded overlay — cadence should keep cycling");
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
        Drive(anim, skel, ref st, frames: 10, action: "");                 // settle into the walk pose
        float walkArm = anim.Pose.Local[armR].Rotation;                    // the rest target it eases back to
        Drive(anim, skel, ref st, frames: 15, action: "GroundSlash1");
        Assert.True(anim.State.ActionWeight > 0.9f);

        float prevW = anim.State.ActionWeight;
        float slashArm = anim.Pose.Local[armR].Rotation;                   // arm at the slash peak
        for (int i = 0; i < 30; i++)
        {
            Drive(anim, skel, ref st, frames: 1, action: "RecoveryAction");
            float w = anim.State.ActionWeight;
            Assert.True(w <= prevW + 1e-6f, $"weight rose during fade-out ({prevW} -> {w})");
            Assert.False(float.IsNaN(anim.Pose.Local[armR].Rotation));
            prevW = w;
        }
        Assert.Equal(0f, anim.State.ActionWeight);
        // Eased back toward the walk pose: the arm is closer to the walk rotation than the slash
        // peak was. (The joint-chain rig gives the rest arm a non-zero bind rotation, so measure
        // relative to the walk pose, not |rotation|.)
        float dEnd   = MathF.Abs(MathHelper.WrapAngle(anim.Pose.Local[armR].Rotation - walkArm));
        float dSlash = MathF.Abs(MathHelper.WrapAngle(slashArm - walkArm));
        Assert.True(dEnd < dSlash, $"arm should ease back toward the walk pose (dEnd {dEnd:0.###} !< dSlash {dSlash:0.###})");
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

    // When the action REPORTS its progress, the clip's whole [0,1] timeline is remapped onto
    // it — so a clip held at progress 0.5 sits at its MIDPOINT no matter how much sim time
    // has elapsed, instead of clamping to its end (as the clip's own 0.14s clock would).
    // This is the contract that lets a variable-length action (Grab's early throw, Beam
    // outliving its clip) stay in sync; ActionProgress < 0 opts back out.
    [Fact]
    public void Overlay_TimeRemapsToReportedProgress()
    {
        var skel = SkeletonExamples.Biped();
        int armR = skel.IndexOf("arm_r_upper");
        float Mid = 0.5f * (0.5f + SlashArm);   // RampSlash midpoint: lerp(0.5, 2.0, 0.5)

        // Reported progress 0.5 with sim time far past the clip's own end -> midpoint.
        var remap = NewAnimator(skel);
        var stR = new DriveState { ActionTime = 0.25f, FreezeActionTime = true, ActionProgress = 0.5f };
        Drive(remap, skel, ref stR, frames: 40, action: "RampSlash");
        Assert.True(MathF.Abs(remap.Pose.Local[armR].Rotation - Mid) < 0.1f,
            $"clip should sit at its midpoint ({Mid}); got {remap.Pose.Local[armR].Rotation}");

        // Declined (negative): keyed to the clip's own 0.14s, so 0.25s is well past the end
        // and the arm has clamped to the final pose.
        var noRemap = NewAnimator(skel);
        var stN = new DriveState { ActionTime = 0.25f, FreezeActionTime = true, ActionProgress = null };
        Drive(noRemap, skel, ref stN, frames: 40, action: "RampSlash");
        Assert.True(MathF.Abs(noRemap.Pose.Local[armR].Rotation - SlashArm) < 0.1f,
            $"declined clip should hold its end ({SlashArm}); got {noRemap.Pose.Local[armR].Rotation}");
    }

    // A clip with a SettleShare splits its timeline: the action's reported progress sweeps
    // only the swing, and the tail plays down the recovery countdown AFTER the action has
    // exited — while the FSM sits in RecoveryAction, which binds nothing itself. SettleSlash
    // ramps 0.5 → SlashArm with SettleShare 0.5, so the swing ends at the midpoint and the
    // settle walks the rest; without the settle the overlay would simply fade out from there.
    [Fact]
    public void Overlay_SettlePlaysThroughRecoveryCountdown()
    {
        var skel = SkeletonExamples.Biped();
        int armR = skel.IndexOf("arm_r_upper");
        float Mid = 0.5f * (0.5f + SlashArm);

        // Swing at full reported progress: the clip sits at the END OF THE SWING (τ = 0.5).
        var anim = NewAnimator(skel);
        var st = new DriveState { ActionTime = 0.14f, FreezeActionTime = true, ActionProgress = 1f };
        Drive(anim, skel, ref st, frames: 40, action: "SettleSlash");
        Assert.True(MathF.Abs(anim.Pose.Local[armR].Rotation - Mid) < 0.1f,
            $"swing should end at the clip midpoint ({Mid}); got {anim.Pose.Local[armR].Rotation}");

        // Recovery: 20 frames counting down. Held near the end, the settle has walked the
        // tail to the clip's final pose — and the overlay stayed bound, not faded.
        st.RecoveryFramesLeft = 20;
        for (int i = 0; i < 19; i++) { Drive(anim, skel, ref st, frames: 1, action: "RecoveryAction"); st.RecoveryFramesLeft--; }
        Drive(anim, skel, ref st, frames: 30, action: "RecoveryAction");   // hold at 1 frame left
        Assert.True(anim.OverlayActive, "the settle should keep the overlay bound through recovery");
        Assert.True(MathF.Abs(anim.Pose.Local[armR].Rotation - SlashArm) < 0.15f,
            $"settle should reach the clip's end pose ({SlashArm}); got {anim.Pose.Local[armR].Rotation}");

        // Countdown over → the overlay releases and fades.
        st.RecoveryFramesLeft = 0;
        Drive(anim, skel, ref st, frames: 60, action: "RecoveryAction");
        Assert.False(anim.OverlayActive, "with no countdown left the overlay should fade out");

        // Control: the same recovery frames after a clip with NO settle just fade from the
        // swing's end — the arm never reaches SlashArm.
        var plain = NewAnimator(skel);
        var stP = new DriveState { ActionTime = 0.14f, FreezeActionTime = true, ActionProgress = 1f };
        Drive(plain, skel, ref stP, frames: 40, action: "RampSlash");
        stP.RecoveryFramesLeft = 20;
        Drive(plain, skel, ref stP, frames: 20, action: "RecoveryAction");
        Assert.True(plain.State.ActionWeight < 0.5f,
            $"a clip without a settle should be fading through recovery; weight {plain.State.ActionWeight}");
    }

    // --- harness ------------------------------------------------------------

    private struct DriveState
    {
        public float X;
        public float ActionTime;
        public bool  FreezeActionTime;
        // null = the action declines to report progress (the animator uses the clip's own
        // Duration). Nullable rather than a -1 field so `new DriveState()` means "declined".
        public float? ActionProgress;
        // Frames left on the self-recovery countdown fed to the sample (0 = none).
        public int   RecoveryFramesLeft;
    }

    private static CharacterAnimator NewAnimator(Skeleton skel)
        => new(skel, 0.6f, new[]
        {
            BuildWalkClip(skel),
            BuildSlashClip(skel, "slash1", "GroundSlash1", armStart: SlashArm, armEnd: SlashArm),
            BuildSlashClip(skel, "slash3", "GroundSlash3", armStart: -1.5f,   armEnd: -1.5f),
            BuildSlashClip(skel, "ramp",   "RampSlash",    armStart: 0.5f,    armEnd: SlashArm),
            BuildSlashClip(skel, "settle", "SettleSlash",  armStart: 0.5f,    armEnd: SlashArm, settleShare: 0.5f),
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
                "WalkState", action, Dt, st.ActionTime, st.ActionProgress ?? -1f,
                recoveryFramesLeft: st.RecoveryFramesLeft));
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

    // A static FullBody base that just holds leg_l_upper at legRot (two identical keyframes,
    // no contacts) — a fixed off-region target for the graded-overlay tests.
    private static AnimationDocument StaticBase(Skeleton skel, string type, float legRot)
    {
        var clip = new AnimationDocument { Name = type.ToLowerInvariant(), Type = type, Duration = 0.8f, Loop = true };
        clip.Keyframes.Add(LegKf(skel, 0f, legRot));
        clip.Keyframes.Add(LegKf(skel, 1f, legRot));
        return clip;
    }

    // An UpperBody overlay that poses both an in-region bone (arm_r_upper) and an off-region
    // bone (leg_l_upper), with a graded OffRegionWeight so the leg blends partially.
    private static AnimationDocument GradedCast(Skeleton skel, string type, float arm, float legRot, float off)
    {
        var clip = new AnimationDocument
        {
            Name = type.ToLowerInvariant(), Type = type, Duration = 0.14f, Loop = false,
            Region = AnimRegion.UpperBody, OffRegionWeight = off,
        };
        clip.Keyframes.Add(CastKf(skel, 0f, arm, legRot));
        clip.Keyframes.Add(CastKf(skel, 1f, arm, legRot));
        return clip;
    }

    private static AnimationKeyframe LegKf(Skeleton skel, float t, float legRot)
    {
        var p = skel.CreatePose();
        SetRot(skel, p, "leg_l_upper", legRot);
        return new AnimationKeyframe { Time = t, Bones = PoseData.Capture(p) };
    }

    private static AnimationKeyframe CastKf(Skeleton skel, float t, float arm, float legRot)
    {
        var p = skel.CreatePose();
        SetRot(skel, p, "arm_r_upper", arm);
        SetRot(skel, p, "leg_l_upper", legRot);
        return new AnimationKeyframe { Time = t, Bones = PoseData.Capture(p) };
    }

    // UpperBody overlay clip ramping arm_r_upper from armStart to armEnd. No contacts —
    // action overlays are constraint-free by design.
    private static AnimationDocument BuildSlashClip(Skeleton skel, string name, string type,
                                                    float armStart, float armEnd, float settleShare = 0f)
    {
        var clip = new AnimationDocument
        {
            Name = name, Type = type, Duration = 0.14f, Loop = false,
            Region = AnimRegion.UpperBody, SettleShare = settleShare,
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

    // BIND-RELATIVE leg swings (matching CharacterAnimatorTests.SetRot): under the joint-chain
    // rig the legs' bind rotation points them DOWN (~π/2), so a swing must add it — an absolute
    // ±1 leaves the legs near-horizontal, where the "planted" foot sweeps FORWARD at stance
    // start and the forward-only cadence correctly freezes (the degenerate wrong-stance-foot
    // case). The golden-section path's basin-jumping bug used to mask this; the LM solver
    // doesn't. The other helpers in this file stay absolute on purpose (their assertions
    // compare local rotations to the authored values directly).
    private static AnimationKeyframe LocoKf(Skeleton skel, float t, float legL, float legR, string plant)
    {
        var p = skel.CreatePose();
        int li = skel.IndexOf("leg_l_upper"), ri = skel.IndexOf("leg_r_upper");
        SetRot(skel, p, "leg_l_upper", skel.Bones[li].Rotation + legL);
        SetRot(skel, p, "leg_r_upper", skel.Bones[ri].Rotation + legR);
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
