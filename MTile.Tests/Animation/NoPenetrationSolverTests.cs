using System;
using System.IO;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Phase 3 (ANIMATION_SOLVER_PLAN §11.6): the no-penetration HALF-PLANE constraint plus the
// off-locomotion solve trigger that lets it (and external pins) engage on a non-locomotion clip.
// A wall-slide pose is run against a wall plane positioned to cut the outward limbs; the solve
// must (a) run at all off the cadence path, (b) bend the limbs back out of the solid, and
// (c) carry an analytic Jacobian on the new block that matches finite differences.
public class NoPenetrationSolverTests
{
    private readonly ITestOutputHelper _o;
    public NoPenetrationSolverTests(ITestOutputHelper o) => _o = o;

    [Fact]
    public void Solver_NoPenetration_PushesLimbsOutOffLocomotion()
    {
        var clips = AnimationStore.LoadAll(StatesDir());
        var skel  = SkeletonExamples.Biped();
        var anim  = new CharacterAnimator(skel, 0.6f, clips);
        int hand  = anim.Skeleton.IndexOf("arm_l_lower");
        int up    = anim.Skeleton.IndexOf("arm_l_upper");

        const float dt = 1f / 30f;
        var pos = new Vector2(0f, 0f);
        const int facing = +1;

        // Warm up the wall-slide pose with NO surface so it settles (eased pose → target).
        for (int i = 0; i < 20; i++)
            anim.Update(new CharacterAnimSample(pos, Vector2.Zero, facing, false, "WallSlidingState", "", dt, tag: AnimTag.WallSlide));

        // Reconstruct the animator's internal solve root (Position + com baseline) to read where
        // the hand naturally sits, then place the wall plane 1.5px INTO the solid past it so the
        // hand (and anything further out) penetrates. Solid is on +X (the wall the body faces);
        // outward normal points back toward −X (open space).
        anim.TryComReference(out var comL);
        var root = Affine2.FromTRS(new Vector2(pos.X, pos.Y - comL.Y * 0.6f), 0f, new Vector2(0.6f, 0.6f));
        float natHandX = anim.Pose.ComputeWorld(root)[hand].Translation.X;
        const float margin = 0.5f;
        float wallX = natHandX - 1.5f;
        var surf = new SolverSurface(new Vector2(wallX, 0f), new Vector2(-1f, 0f), margin);
        _o.WriteLine($"natHandX={natHandX:0.00} wallX={wallX:0.00} (solid X>{wallX:0.00})");

        float maxArm = 0f, maxJacErr = 0f, lastSolvedHandX = natHandX; int solves = 0;
        for (int i = 0; i < 30; i++)
        {
            anim.Update(new CharacterAnimSample(pos, Vector2.Zero, facing, false, "WallSlidingState", "", dt, tag: AnimTag.WallSlide,
                surfaces: new[] { surf }));
            string rep = anim.SolveScaleReport();
            if (rep == "(no solve)") continue;
            solves++;
            maxArm = MathF.Max(maxArm, MathF.Max(MathF.Abs(anim.AngleCorrection(hand)), MathF.Abs(anim.AngleCorrection(up))));
            maxJacErr = MathF.Max(maxJacErr, anim.MaxJacobianError());
            lastSolvedHandX = anim.SolvedBoneTipWorld(hand).X;
            if (i % 6 == 0) _o.WriteLine($"f{i,2}: handX={lastSolvedHandX:0.00} armΔθ={maxArm:0.000} jacErr={anim.MaxJacobianError():0.0000}  {rep}");
        }

        _o.WriteLine($"=> solves={solves} natHandX={natHandX:0.00} solvedHandX={lastSolvedHandX:0.00} pushBack={natHandX - lastSolvedHandX:0.00}px maxArmΔθ={maxArm:0.000} maxJacErr={maxJacErr:0.0000}");

        // (a) the static solve ran off the locomotion+contact path — the trigger broadening works.
        Assert.True(solves > 0, "no off-locomotion solve ran — the trigger didn't broaden");
        // (b) the limb was bent back out of the solid (penetration reduced, didn't grow).
        Assert.True(natHandX - lastSolvedHandX > 0.3f, $"hand not pushed out of the wall (pushBack {natHandX - lastSolvedHandX:0.00}px)");
        // ...via a real, NONZERO arm correction (Δθ is load-bearing on the no-pen block too).
        Assert.True(maxArm > 0.02f, $"arm Δθ never engaged ({maxArm:0.000}) — surface isn't driving IK");
        // (c) the new block's analytic Jacobian matches finite differences (the headline test).
        Assert.True(maxJacErr < 0.05f, $"no-penetration Jacobian disagrees with FD (relErr {maxJacErr:0.0000})");
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
