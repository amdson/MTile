using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// In-solve smoothing (ANIMATION_POLISH_PLAN item 1): the BlendToward ease was retired and its
// math moved inside the solve objective (ThetaSmoothnessConstraint) / the closed-form fast
// path. These tests pin the two behaviors that replacement must preserve:
//  · EASE PARITY — an unconstrained bone follows its blend target with the same exponential
//    ease the old system had: gap shrinks by exactly (1−b) per frame, b = 1−exp(−Stiffness·dt).
//  · CLIP-SWITCH CONTINUITY — a clip switch does not snap: the smoothness target (last EMITTED
//    pose) persists across the switch, so the pose crossfades the gap instead of popping.
public class SmoothingTests
{
    private const float Dt = 1f / 30f;
    // Must mirror CharacterAnimator.Stiffness (private const). The parity assertion is what
    // notices if these drift apart.
    private const float Stiffness = 20f;

    private readonly ITestOutputHelper _o;
    public SmoothingTests(ITestOutputHelper o) => _o = o;

    // Two static full-body clips a leg-bone apart: Idle holds the leg at rest+0.8, Crouch at
    // rest−0.4. Idle → converge, switch to Crouch, and check the per-frame decay of the gap.
    [Fact]
    public void UnconstrainedBone_FollowsTarget_WithTheRetiredEaseRate()
    {
        var skel = SkeletonExamples.Biped();
        int leg = skel.IndexOf("leg_l_upper");
        float rest = skel.Bones[leg].Rotation;
        var anim = new CharacterAnimator(skel, 0.6f, new[]
        {
            StaticClip(skel, "Idle",   leg, rest + 0.8f),
            StaticClip(skel, "Crouch", leg, rest - 0.4f),
        });

        // Converge on Idle (standing, zero velocity).
        for (int i = 0; i < 60; i++)
            anim.Update(new CharacterAnimSample(Vector2.Zero, Vector2.Zero, +1, true, "StandingState", "", Dt));
        Assert.True(MathF.Abs(anim.Pose.Local[leg].Rotation - (rest + 0.8f)) < 1e-3f,
            $"didn't converge on Idle ({anim.Pose.Local[leg].Rotation} vs {rest + 0.8f})");

        // Switch to Crouch; the gap to the new target must decay by (1−b) per frame.
        float target = rest - 0.4f;
        float expectedRatio = MathF.Exp(-Stiffness * Dt);   // 1−b ≈ 0.5134 at 30fps
        float prevGap = MathF.Abs(anim.Pose.Local[leg].Rotation - target);
        for (int i = 0; i < 8; i++)
        {
            anim.Update(new CharacterAnimSample(Vector2.Zero, Vector2.Zero, +1, true, "CrouchedState", "", Dt));
            float gap = MathF.Abs(anim.Pose.Local[leg].Rotation - target);
            float ratio = gap / MathF.Max(prevGap, 1e-6f);
            _o.WriteLine($"f{i}: gap={gap:0.0000} ratio={ratio:0.0000} (expected {expectedRatio:0.0000})");
            // CONTINUITY: no snap — the pose covers at most b of the gap in one frame...
            Assert.True(gap > 0.25f * prevGap - 1e-4f, $"pose snapped at frame {i} (gap {prevGap:0.000} → {gap:0.000})");
            // ...PARITY: and the decay matches the retired ease's rate.
            if (prevGap > 0.01f)   // ratio is noise-dominated once the gap is tiny
                Assert.True(MathF.Abs(ratio - expectedRatio) < 0.06f,
                    $"decay ratio {ratio:0.000} ≠ ease rate {expectedRatio:0.000} at frame {i}");
            prevGap = gap;
        }
        Assert.True(prevGap < 0.01f, $"gap should be nearly closed after 8 frames, is {prevGap:0.000}");
    }

    // A switch between clips must never pop even mid-motion: drive a cadence walk, hard-switch
    // to a static Fall pose (airborne), and bound the largest per-frame change of any bone's
    // emitted local angle. The bound is the ease covering the pose gap: b·gap + the clip's own
    // per-frame motion — generous headroom, but a SNAP (full gap in one frame) fails it.
    [Fact]
    public void ClipSwitch_MidWalk_DoesNotSnap()
    {
        var skel = SkeletonExamples.Biped();
        var anim = new CharacterAnimator(skel, 0.6f, new[]
        {
            WalkClip(skel),
            StaticClip(skel, "Fall", skel.IndexOf("leg_l_upper"), skel.Bones[skel.IndexOf("leg_l_upper")].Rotation - 0.9f),
        });

        float dt = Dt, vx = 30f, x = 0f;
        var prevLocal = new float[skel.Count];
        for (int i = 0; i < 30; i++)
        {
            x += vx * dt;
            anim.Update(new CharacterAnimSample(new Vector2(x, 0f), new Vector2(vx, 0f), +1, true, "WalkState", "", dt));
        }
        for (int b = 0; b < skel.Count; b++) prevLocal[b] = anim.Pose.Local[b].Rotation;

        // Hard switch: airborne fall. The walk→fall leg gap is ~1.7 rad; a snap would show as a
        // ~1.7 rad single-frame jump; the ease covers b≈0.49 of it (~0.83) at most.
        float maxStep = 0f;
        for (int i = 0; i < 10; i++)
        {
            anim.Update(new CharacterAnimSample(new Vector2(x, -10f), new Vector2(vx, 30f), +1, false, "FallingState", "", dt));
            for (int b = 0; b < skel.Count; b++)
            {
                float d = MathF.Abs(MathHelper.WrapAngle(anim.Pose.Local[b].Rotation - prevLocal[b]));
                maxStep = MathF.Max(maxStep, d);
                prevLocal[b] = anim.Pose.Local[b].Rotation;
            }
        }
        _o.WriteLine($"max per-frame bone step across the switch: {maxStep:0.000} rad");
        Assert.True(maxStep < 1.0f, $"clip switch snapped ({maxStep:0.000} rad in one frame) — emitted-pose bridge broken?");
        Assert.True(maxStep > 0.05f, "pose never moved across the switch — switch not exercised?");
    }

    // --- clip builders --------------------------------------------------------

    private static AnimationDocument StaticClip(Skeleton skel, string type, int bone, float rot)
    {
        var clip = new AnimationDocument { Name = type.ToLowerInvariant() + "_s", Type = type, Duration = 1f, Loop = true };
        var p = skel.CreatePose();
        var t = p.Local[bone]; t.Rotation = rot; p.SetLocal(bone, t);
        clip.Keyframes.Add(new AnimationKeyframe { Time = 0f, Bones = PoseData.Capture(p) });
        clip.Keyframes.Add(new AnimationKeyframe { Time = 1f, Bones = PoseData.Capture(p) });
        return clip;
    }

    // Bind-relative scissoring walk with planted feet (cadence runs) — the standard fixture.
    private static AnimationDocument WalkClip(Skeleton skel)
    {
        var clip = new AnimationDocument { Name = "walk", Type = "Walk", Duration = 0.8f, Loop = true };
        clip.Keyframes.Add(Kf(skel, 0f,   legL:  1.0f, legR: -1.0f, plant: "foot_r"));
        clip.Keyframes.Add(Kf(skel, 0.5f, legL: -1.0f, legR:  1.0f, plant: "foot_l"));
        clip.Keyframes.Add(Kf(skel, 1f,   legL:  1.0f, legR: -1.0f, plant: "foot_r"));
        return clip;
    }

    private static AnimationKeyframe Kf(Skeleton skel, float t, float legL, float legR, string plant)
    {
        var p = skel.CreatePose();
        Swing(skel, p, "leg_l_upper", legL);
        Swing(skel, p, "leg_r_upper", legR);
        return new AnimationKeyframe
        {
            Time = t,
            Bones = PoseData.Capture(p),
            Contacts = new List<ContactLabel> { new() { Node = plant, Weight = 1f } },
        };
    }

    private static void Swing(Skeleton skel, SkeletonPose p, string bone, float rot)
    {
        int i = skel.IndexOf(bone);
        var t = p.Local[i];
        t.Rotation = skel.Bones[i].Rotation + rot;
        p.SetLocal(i, t);
    }
}
