using System;
using System.IO;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Aimed actions (STAB_AIM_PLAN): the stab re-aims the authored horizontal overlay along the input
// direction. The ActionAimConstraint rotates the L→R-hand vector onto û* (the reference aim rotated
// by the stab's deviation) via the overlay-owned arm's Δθ. This runs on a STANDING stab — Idle base
// + stab overlay, no contacts/pins — so it also exercises the aim branch of the off-locomotion
// solve trigger.
public class ActionAimSolverTests
{
    private readonly ITestOutputHelper _o;
    public ActionAimSolverTests(ITestOutputHelper o) => _o = o;

    [Theory]
    [InlineData(+1,  35f)]   // facing right, aim 35° up-forward
    [InlineData(+1, -35f)]   // facing right, aim 35° down-forward
    [InlineData(-1,  35f)]   // facing left, aim up-forward
    public void Solver_ActionAim_ReAimsStabAlongInput(int facing, float aimDeg)
    {
        var clips = AnimationStore.LoadAll(StatesDir());
        var skel  = SkeletonExamples.Biped();
        var anim  = new CharacterAnimator(skel, 0.6f, clips, useSolver: true);
        int hand  = anim.Skeleton.IndexOf("arm_r_lower");
        int up    = anim.Skeleton.IndexOf("arm_r_upper");
        int chest = anim.Skeleton.IndexOf("chest");

        const float dt = 1f / 30f;
        var pos = new Vector2(0f, 0f);
        // Aim direction in world: rotate horizontal-forward (facing,0) by aimDeg (screen-down +y).
        float a = aimDeg * MathF.PI / 180f;
        var aimDir = new Vector2(facing * MathF.Cos(a), MathF.Sin(a));

        // A standing stab: Idle base + StabAction overlay, mid-thrust, with the aim active.
        CharacterAnimSample Frame() => new(pos, Vector2.Zero, facing, true, "StandingState", "StabAction",
            dt, actionTime: 0.25f, actionDuration: 0.6f, hasAim: true, aimDir: aimDir);

        float maxArm = 0f, maxChest = 0f, maxJacErr = 0f, lastErrDeg = 999f; int solves = 0;
        for (int i = 0; i < 40; i++)
        {
            anim.Update(Frame());
            string rep = anim.SolveScaleReport();
            if (rep == "(no solve)") continue;
            solves++;
            float errDeg = anim.AimAngleError() * 180f / MathF.PI;
            if (i >= 20)   // after the overlay eases in + the aim settles
            {
                lastErrDeg = MathF.Abs(errDeg);
                maxArm   = MathF.Max(maxArm, MathF.Max(MathF.Abs(anim.AngleCorrection(hand)), MathF.Abs(anim.AngleCorrection(up))));
                maxChest = MathF.Max(maxChest, MathF.Abs(anim.AngleCorrection(chest)));
                maxJacErr = MathF.Max(maxJacErr, anim.MaxJacobianError());
            }
            if (i % 8 == 0) _o.WriteLine($"f{i,2}: aimErr={errDeg,6:0.0}° armΔθ={maxArm:0.000} chestΔθ={maxChest:0.000} jacErr={anim.MaxJacobianError():0.0000}  {rep}");
        }

        _o.WriteLine($"=> facing={facing} aim={aimDeg}° solves={solves} finalErr={lastErrDeg:0.0}° maxArmΔθ={maxArm:0.000} maxChestΔθ={maxChest:0.000} maxJacErr={maxJacErr:0.0000}");

        // The solve ran on a standing stab (the aim branch of the off-locomotion trigger fired).
        Assert.True(solves > 0, "no aim solve ran on a standing stab");
        // The L→R aim vector reached the input direction (the re-aim worked) within a few degrees.
        Assert.True(lastErrDeg < 6f, $"stab didn't re-aim: {lastErrDeg:0.0}° off target");
        // ...via a real arm correction, bounded inside the box (not slammed).
        Assert.True(maxArm > 0.02f, $"arm Δθ never engaged ({maxArm:0.000})");
        Assert.True(maxArm < AnimSolverConfig.Current.AngleCorrLimit - 0.01f, $"arm Δθ slammed the box ({maxArm:0.000})");
        // The analytic Jacobian of the aim row matches finite differences (the headline test).
        Assert.True(maxJacErr < 0.05f, $"aim Jacobian disagrees with FD (relErr {maxJacErr:0.0000})");
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
