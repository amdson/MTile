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

    // A SHADOW animator runs the same walk WITHOUT pins → gives the natural (uncorrected) hand
    // each frame; the main animator pins its hand a small fixed offset off that. So the required
    // correction is small and steady (not fighting the clip's own arm swing): a clean read of
    // whether the hard pin reaches via a MINIMAL, bounded Δθ (the IK working), and the magnitudes
    // the tiers are set from. The hand bone is arm_l_lower (the wrist/hand tip).
    [Theory]
    [InlineData("walk", 25f, +1)]
    [InlineData("walk", 25f, -1)]
    public void Solver_FixedPoint_ReachesViaBoundedAngleCorrection(string clipName, float speed, int facing)
    {
        var clip = AnimationStore.LoadAll(StatesDir()).Find(d => d.Name == clipName);
        Assert.True(clip != null, $"{clipName}.json not found");
        var skel = SkeletonExamples.Biped();
        var main   = new CharacterAnimator(skel, 0.6f, new[] { clip }, useSolver: true);
        var shadow = new CharacterAnimator(skel, 0.6f, new[] { clip }, useSolver: true);
        int hand  = main.Skeleton.IndexOf("arm_l_lower");
        int up    = main.Skeleton.IndexOf("arm_l_upper");
        int chest = main.Skeleton.IndexOf("chest");
        var offset = new Vector2(2f, -1.5f);   // hold the hand a touch forward + up of natural

        float dt = 1f / 30f, vx = speed * facing, x = 0f;
        float maxReach = 0f, maxArm = 0f, maxChest = 0f; int solves = 0;
        for (int i = 0; i < 48; i++)
        {
            x += vx * dt;
            var sample = new CharacterAnimSample(new Vector2(x, 0f), new Vector2(vx, 0f), facing, true, "WalkState", "", dt);
            shadow.Update(sample);
            Vector2 natural = shadow.SolvedBoneTipWorld(hand);
            if (natural == Vector2.Zero) { main.Update(sample); continue; }   // no shadow solve yet

            var pins = new[] { new ExternalPin("arm_l_lower", natural + offset) };
            main.Update(new CharacterAnimSample(new Vector2(x, 0f), new Vector2(vx, 0f), facing, true, "WalkState", "", dt, pins: pins));

            string rep = main.SolveScaleReport();
            if (rep == "(no solve)") continue;
            solves++;
            if (i >= 24)   // after warm-up, in steady state
            {
                Vector2 tip = main.SolvedBoneTipWorld(hand);
                maxReach = MathF.Max(maxReach, (tip - (natural + offset)).Length());
                maxArm   = MathF.Max(maxArm, MathF.Max(MathF.Abs(main.AngleCorrection(hand)),
                                                       MathF.Abs(main.AngleCorrection(main.Skeleton.IndexOf("arm_l_upper")))));
                maxChest = MathF.Max(maxChest, MathF.Abs(main.AngleCorrection(chest)));
                if (i % 4 == 0) _o.WriteLine($"f{i,2}: armΔθ up={main.AngleCorrection(up):0.000} lo={main.AngleCorrection(hand):0.000} chest={main.AngleCorrection(chest):0.000} reach={(main.SolvedBoneTipWorld(hand)-(natural+offset)).Length():0.00}");
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
        // ...and the arm correction stays inside the box (not slammed — smoothness/priors keep it sane).
        Assert.True(maxArm < 0.55f, $"arm Δθ slammed the box ({maxArm:0.000}) — prior/smoothness too weak");
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
