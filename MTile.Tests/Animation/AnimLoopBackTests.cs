using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests;

// ClipLoopBack (Animation/ClipLoopBack.cs) and the animator's loop-back jump: a cadence clip
// whose tail matches an earlier point better than its authored seam loops THERE, with no
// annotation on the clip. Two fixtures on the real biped rig: a leg swing over ONE AND A HALF
// sine cycles (a bad seam, a perfect repeat two-thirds in) and the same over exactly one
// cycle (a perfect seam, which must be left alone).
public class AnimLoopBackTests
{
    private const float PathSpeed = 30f;   // body_path rig units per authored timeline

    private static AnimationKeyframe Key(float t, float cycles) => new()
    {
        Time = t,
        Bones = new List<PoseBoneEntry>
        {
            new() { Bone = "leg_l_upper", Rotation =  0.6f * MathF.Sin(MathHelper.TwoPi * cycles * t) },
            new() { Bone = "leg_r_upper", Rotation = -0.6f * MathF.Sin(MathHelper.TwoPi * cycles * t) },
        },
        Additions = new List<AnimAddition>
        {
            new() { Name = "com",       Kind = AnimAdditionKind.Point, Px = 0f,           Py = -40f },
            new() { Name = "body_path", Kind = AnimAdditionKind.Point, Px = PathSpeed * t, Py = 0f },
        },
    };

    private static AnimationDocument Swing(string type, float cycles)
    {
        var keys = new List<AnimationKeyframe>();
        const int K = 12;
        for (int k = 0; k <= K; k++) keys.Add(Key(k / (float)K, cycles));
        return new AnimationDocument { Name = type.ToLowerInvariant(), Type = type, Skeleton = "biped", Loop = true, Duration = 1f, Keyframes = keys };
    }

    private static Skeleton Rig() => SkeletonExamples.Load("biped");

    [Fact]
    public void BadSeam_PlansAJump_ToTheRepeat()
    {
        var plan = ClipLoopBack.Plan(Swing("Walk", 1.5f), Rig(), region: 0.25f, minLoop: 0.5f);
        Assert.True(plan.HasCandidate);
        Assert.True(plan.Jumps, $"cost {plan.Cost} vs seam {plan.SeamCost}");
        Assert.True(plan.Exit >= 0.75f && plan.Exit < 1f, $"exit {plan.Exit}");
        Assert.True(plan.Entry < 0.75f, $"entry {plan.Entry}");
        Assert.Equal(1f / 1.5f, plan.Exit - plan.Entry, 1);   // one sine period
        Assert.True(plan.Cost < plan.SeamCost * 0.25f, $"cost {plan.Cost} vs seam {plan.SeamCost}");
    }

    [Fact]
    public void PerfectSeam_IsLeftAlone()
    {
        var plan = ClipLoopBack.Plan(Swing("Walk", 1f), Rig(), region: 0.25f, minLoop: 0.5f);
        Assert.True(plan.SeamCost < 1e-3f, $"seam {plan.SeamCost}");
        Assert.False(plan.Jumps);
    }

    [Fact]
    public void AnnotatedSeam_UsesWrappedContactsAtTheEndPose()
    {
        var clip = Swing("Walk", 1f);
        clip.Contacts = new()
        {
            new() { Point = "support_l", Start = 0f, End = 0.5f },
            new() { Point = "support_r", Start = 0.5f, End = 1f },
        };
        var graph = ClipTransitionGraph.Build(clip, clip, Rig(), new TransitionOptions
        {
            Window = 0, IncludeEndPhase = true, TreatAsNonLooping = true,
            AngleWeight = 0, FootPositionWeight = 0, FootVelocityWeight = 0,
        });
        Assert.Equal(0f, graph.CostAt(1f, 0f));
        Assert.False(ClipLoopBack.Plan(clip, Rig(), 0.25f, 0.3f).Jumps);
    }

    [Fact]
    public void ContactTransfer_RejectsBothFootSwapAndWrongPartOfStance()
    {
        var clip = Swing("Walk", 1f);
        clip.Contacts = new()
        {
            new() { Point = "support_l", Start = 0f, End = 0.5f },
            new() { Point = "support_r", Start = 0.5f, End = 1f },
        };
        Assert.True(ClipStrideTrack.TryCompile(clip, Rig(), out var gait, out _));
        Assert.True(ClipTransitionGraph.ContactsCompatible(gait, 0.12f, gait, 0.13f, 0.02f));
        Assert.False(ClipTransitionGraph.ContactsCompatible(gait, 0.12f, gait, 0.62f, 0.02f));
        Assert.False(ClipTransitionGraph.ContactsCompatible(gait, 0.12f, gait, 0.42f, 0.02f));
    }

    [Fact]
    public void AnnotatedRepeat_StillAllowsContactCompatibleLoopBack()
    {
        var clip = Swing("Walk", 2.5f);
        clip.Contacts = new()
        {
            new() { Point = "support_l", Start = 0f, End = .2f },
            new() { Point = "support_r", Start = .2f, End = .4f },
            new() { Point = "support_l", Start = .4f, End = .6f },
            new() { Point = "support_r", Start = .6f, End = .8f },
            new() { Point = "support_l", Start = .8f, End = 1f },
        };
        var plan = ClipLoopBack.Plan(clip, Rig(), 0.3f, 0.3f, new TransitionOptions { Samples = 101 });
        Assert.True(plan.Jumps, $"{plan.Cost} vs seam {plan.SeamCost}");
        Assert.True(ClipStrideTrack.TryCompile(clip, Rig(), out var gait, out _));
        Assert.True(ClipTransitionGraph.ContactsCompatible(gait, plan.Exit, gait, plan.Entry, .01f));
    }

    // Drive the animator: with loop-back on, the phase never crosses the bad seam by wrapping;
    // every backwards step is a counted jump onto the plan's entry. With it off, it wraps.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Animator_LoopsAtThePlannedExit_NotTheSeam(bool enabled)
    {
        bool prev = AnimSolverConfig.Current.LoopBackEnabled;
        AnimSolverConfig.Current.LoopBackEnabled = enabled;
        try
        {
            var rig = Rig();
            var anim = new CharacterAnimator(rig, 0.6f, new[] { Swing("Walk", 1.5f), Swing("Run", 1.5f) });
            float dt = 1f / 30f, vx = 25f, x = 0f;
            float prevPhase = anim.State.Phase;
            int jumps = 0, wraps = 0;
            LoopBackPlan? plan = null;
            for (int i = 0; i < 150; i++)
            {
                x += vx * dt;
                anim.Update(new CharacterAnimSample(new Vector2(x, 0f), new Vector2(vx, 0f), 1, true, "StandingState", "", dt));
                plan ??= anim.LoopBackPlanFor(anim.State.Clip);
                float ph = anim.State.Phase;
                if (ph < prevPhase - 0.05f)
                {
                    if (anim.LoopBackJumps > jumps)
                    {
                        jumps = anim.LoopBackJumps;
                        // Landed at the entry plus at most one step of overshoot.
                        Assert.True(ph >= plan.Value.Entry - 1e-4f && ph <= plan.Value.Entry + 0.3f,
                                    $"jump landed at {ph}, entry {plan.Value.Entry}");
                    }
                    else wraps++;
                }
                prevPhase = ph;
            }
            Assert.NotNull(plan);
            if (enabled) { Assert.True(jumps >= 2, $"jumps {jumps}"); Assert.Equal(0, wraps); }
            else         { Assert.Equal(0, jumps); Assert.True(wraps >= 1, "expected the authored wrap"); }
        }
        finally { AnimSolverConfig.Current.LoopBackEnabled = prev; }
    }
}
