using System;
using System.IO;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Phase 2 (ANIMATION_SOLVER_PLAN §11.6): the first load-bearing constraint — a hard external
// FixedPoint pin that drives the Δθ IK channel — plus the gradient-magnitude logging that the
// weight TIERS are chosen from (the weights are empirical, not derived from first principles).
public class FixedPointSolverTests
{
    private readonly ITestOutputHelper _o;
    public FixedPointSolverTests(ITestOutputHelper o) => _o = o;

    // The pin target is BODY-RELATIVE, captured once from the animator's OWN natural hand
    // after warm-up (a "carried lantern": the hand holds a fixed point relative to the body
    // while the walk swings underneath). Self-contained on purpose — an earlier version used a
    // phase-locked SHADOW animator as the oracle, but the pin legitimately shifts the knife-edge
    // foot-swap hop by a frame, the two walks desync, and the shadow-derived target then fights
    // the main walk's own swing in a feedback loop that manufactures the very arm spike the test
    // meant to bound. A body-relative target has no second timeline to desync from.
    [Theory]
    [InlineData("walk", 25f, +1)]
    [InlineData("walk", 25f, -1)]
    public void Solver_FixedPoint_ReachesViaBoundedAngleCorrection(string clipName, float speed, int facing)
    {
        var clip = AnimationStore.LoadAll(StatesDir()).Find(d => d.Name == clipName);
        Assert.True(clip != null, $"{clipName}.json not found");
        var skel = SkeletonExamples.Biped();
        var main  = new CharacterAnimator(skel, 0.6f, new[] { clip });
        int hand  = main.Skeleton.IndexOf("arm_l_lower");
        int up    = main.Skeleton.IndexOf("arm_l_upper");
        int chest = main.Skeleton.IndexOf("chest");

        float dt = 1f / 30f, vx = speed * facing, x = 0f;
        float maxReach = 0f, maxArm = 0f, maxChest = 0f; int solves = 0;
        // Warm-up (frames 0..39, unpinned): track the natural hand's min/max relative to the
        // body across the arm swing, then pin at the swing MIDPOINT (+ a touch of lift) — so
        // the required correction is ~half the swing amplitude each way, comfortably inside
        // the box, rather than a full amplitude off one extreme.
        const int PinFrom = 40, AssertFrom = 52, Frames = 90;
        Vector2 relMin = new(float.MaxValue, float.MaxValue), relMax = new(float.MinValue, float.MinValue);
        Vector2 rel = default;
        for (int i = 0; i < Frames; i++)
        {
            x += vx * dt;
            var body = new Vector2(x, 0f);
            ExternalPin[] pins = i >= PinFrom ? new[] { new ExternalPin("arm_l_lower", body + rel) } : null;
            main.Update(new CharacterAnimSample(body, new Vector2(vx, 0f), facing, true, "WalkState", "", dt, pins: pins));
            if (i < PinFrom)
            {
                Vector2 natural = main.SolvedBoneTipWorld(hand);
                if (natural != Vector2.Zero)
                {
                    Vector2 r0 = natural - body;
                    relMin = Vector2.Min(relMin, r0); relMax = Vector2.Max(relMax, r0);
                }
                if (i == PinFrom - 1) rel = (relMin + relMax) * 0.5f + new Vector2(0f, -1.5f);
                continue;
            }

            string rep = main.SolveScaleReport();
            if (rep == "(no solve)") continue;
            solves++;
            if (i >= AssertFrom)   // after the pin ease-in, in steady state
            {
                Vector2 tip = main.SolvedBoneTipWorld(hand);
                maxReach = MathF.Max(maxReach, (tip - (body + rel)).Length());
                maxArm   = MathF.Max(maxArm, MathF.Max(MathF.Abs(main.AngleCorrection(hand)),
                                                       MathF.Abs(main.AngleCorrection(up))));
                maxChest = MathF.Max(maxChest, MathF.Abs(main.AngleCorrection(chest)));
                if (i % 2 == 0) _o.WriteLine($"f{i,2}: armΔθ up={main.AngleCorrection(up):0.000} lo={main.AngleCorrection(hand):0.000} chest={main.AngleCorrection(chest):0.000} reach={(main.SolvedBoneTipWorld(hand)-(body+rel)).Length():0.00} ph={main.State.Phase:0.000}");
            }
        }

        _o.WriteLine($"=> maxReach={maxReach:0.00}px  maxArmΔθ={maxArm:0.000}  maxChestΔθ={maxChest:0.000}  (box={AnimSolverConfig.Current.AngleCorrLimit})");
        Assert.True(solves > 0, "no solve ran");
        // The hard pin is reached (the IK works)...
        Assert.True(maxReach < 1.5f, $"pin not reached: {maxReach:0.00}px");
        // ...via a real, NONZERO arm correction (Δθ became load-bearing — the Phase 2 point)...
        Assert.True(maxArm > 0.02f, $"arm Δθ never engaged ({maxArm:0.000}) — pin isn't driving IK");
        // ...the arm does the work, NOT the torso (stiff-core prior keeps the body from swinging
        // to satisfy a hand pin — the key conditioning win)...
        Assert.True(maxChest < 0.2f, $"torso swung to reach a hand pin (chest Δθ {maxChest:0.000}) — core not stiff enough");
        // ...and the arm correction stays sane — holding a body-relative point against the walk's
        // full arm swing legitimately needs ~half the swing amplitude at the extremes (≈0.75 rad
        // since smoothing moved in-solve and stopped double-damping the follow), but it must stay
        // well inside the (now wide, clip-switch-sized) box rather than drift to it.
        Assert.True(maxArm < 1.0f, $"arm Δθ unexpectedly large ({maxArm:0.000}) — priors too weak?");
    }

    private static string StatesDir()
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
}
