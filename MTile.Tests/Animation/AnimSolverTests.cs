using System;
using System.IO;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;

namespace MTile.Tests;

public class AnimSolverTests
{
    // --- the least-squares core, in isolation -------------------------------

    // Unconstrained: minimize (x-3)² + (y+1)² → (3, -1). Proves the LM loop, normal
    // equations, and the Cholesky solve on a 2-var / 2-residual problem.
    [Fact]
    public void LeastSquares_FindsUnconstrainedMinimum()
    {
        var solver = new LeastSquaresSolver(maxVars: 4, maxRes: 4);
        int Resid(ReadOnlySpan<float> x, Span<float> r)
        {
            r[0] = x[0] - 3f;
            r[1] = x[1] + 1f;
            return 2;
        }
        var x  = new float[] { 0f, 0f };
        var lo = new float[] { -10f, -10f };
        var hi = new float[] {  10f,  10f };
        float cost = solver.Minimize(Resid, x.AsSpan(0, 2), lo.AsSpan(0, 2), hi.AsSpan(0, 2));

        Assert.Equal(3f, x[0], 3);
        Assert.Equal(-1f, x[1], 3);
        Assert.True(cost < 1e-5f, $"cost {cost}");
    }

    // The box bound must hold the optimum at the wall when the unconstrained min lies
    // outside it: minimize (x-5)² with x ∈ [0,1] → x = 1.
    [Fact]
    public void LeastSquares_RespectsBox()
    {
        var solver = new LeastSquaresSolver(maxVars: 2, maxRes: 2);
        int Resid(ReadOnlySpan<float> x, Span<float> r) { r[0] = x[0] - 5f; return 1; }
        var x  = new float[] { 0f };
        float _ = solver.Minimize(Resid, x.AsSpan(0, 1),
                                  new float[] { 0f }, new float[] { 1f });
        Assert.Equal(1f, x[0], 3);
    }

    // --- Phase 2: angle corrections + pose prior -----------------------------

    // The solver now carries a per-bone angle correction Δθ regularized by the Tikhonov
    // PosePrior. The well-posedness / no-op proof has two parts:
    //
    //  • A bone with NO contact in its subtree (chest, head, arms) has zero slip-coupling,
    //    so its only residual is the prior row √λ·Δθ. The normal equations then DECOUPLE
    //    that variable (JᵀJ diagonal = λ, off-diagonals 0) and drive it to exactly 0 from
    //    the 0 start — λ-independently. If it drifted, the system would be ill-posed.
    //  • Leg/foot bones (in the active contact chain) may carry a SMALL IK correction that
    //    trims the residual no-slip, but it must stay well inside the box (no blow-up, no
    //    pinning to the AngleCorrLimit wall) — the pose stays on the authored blend.
    [Theory]
    [InlineData("walk", 25f, +1)]
    [InlineData("walk", 25f, -1)]
    [InlineData("run",  90f, +1)]
    [InlineData("run",  90f, -1)]
    public void Solver_AngleCorrections_StayNegligible_InSteadyLocomotion(string clipName, float speed, int facing)
    {
        var clip = AnimationStore.LoadAll(StatesDir()).Find(d => d.Name == clipName);
        Assert.True(clip != null, $"{clipName}.json not found");
        var skel = SkeletonExamples.Biped();
        var anim = new CharacterAnimator(skel, 0.6f, new[] { clip }, useSolver: true);

        // Bones with no foot contact below them — their Δθ is driven purely by the prior.
        var upperBody = new[] { "chest", "head", "arm_l_upper", "arm_l_lower", "arm_r_upper", "arm_r_lower" };
        int[] upper = Array.ConvertAll(upperBody, n => anim.Skeleton.IndexOf(n));

        float dt = 1f / 30f, vx = speed * facing, x = 0f, maxUpper = 0f, maxAll = 0f;
        float prev = anim.State.Phase, totalPhase = 0f;
        for (int i = 0; i < 60; i++)
        {
            x += vx * dt;
            anim.Update(new CharacterAnimSample(
                new Vector2(x, 0f), new Vector2(vx, 0f), facing, true, "WalkState", "", dt));
            foreach (int b in upper) maxUpper = MathF.Max(maxUpper, MathF.Abs(anim.AngleCorrection(b)));
            for (int b = 0; b < anim.Skeleton.Count; b++)
                maxAll = MathF.Max(maxAll, MathF.Abs(anim.AngleCorrection(b)));
            float p = anim.State.Phase, d = p - prev; if (d < -0.5f) d += 1f;
            totalPhase += d; prev = p;
        }

        // The cadence path actually ran (phase advanced — not a vacuous all-flight pass)...
        Assert.True(totalPhase > 0.2f, $"cadence didn't advance ({totalPhase:0.000})");
        // ...unconstrained bones collapse to exactly 0 (the structural no-op)...
        Assert.True(maxUpper < 1e-4f, $"upper-body |Δθ| = {maxUpper:0.000000} rad — should be ~0");
        // ...and even the IK-trimmed leg/foot corrections stay a small fraction of the box.
        Assert.True(maxAll < 0.25f, $"max |Δθ| = {maxAll:0.0000} rad — corrections not minimal");
    }

    // --- Phase 3: solved vertical offset δ (ComOffset + vertical ground) -----

    // δ is the body's vertical bob: a hard per-contact ground row holds the planted foot
    // at its plant height (δ ≠ 0), and a soft com row pulls δ → 0 when no foot pins it.
    // Run has both stance and no-contact (flight) windows, so over a cycle δ must BOTH
    // engage (stance) and release to exactly 0 (flight → body at the com baseline, both
    // feet free to leave the ground), and never pin to the box.
    [Theory]
    [InlineData("run", 90f, +1)]
    [InlineData("run", 90f, -1)]
    public void Solver_VerticalOffset_EngagesInStance_ReleasesInFlight(string clipName, float speed, int facing)
    {
        var clip = AnimationStore.LoadAll(StatesDir()).Find(d => d.Name == clipName);
        Assert.True(clip != null, $"{clipName}.json not found");
        var skel = SkeletonExamples.Biped();
        var anim = new CharacterAnimator(skel, 0.6f, new[] { clip }, useSolver: true);

        float dt = 1f / 30f, vx = speed * facing, x = 0f, maxAbs = 0f;
        int flightFrames = 0, stanceBobFrames = 0;
        for (int i = 0; i < 60; i++)
        {
            x += vx * dt;
            anim.Update(new CharacterAnimSample(
                new Vector2(x, 0f), new Vector2(vx, 0f), facing, true, "WalkState", "", dt));
            float d = anim.VerticalOffset;
            maxAbs = MathF.Max(maxAbs, MathF.Abs(d));
            if (d == 0f) flightFrames++;                       // no solve this frame → flight, baseline
            else if (MathF.Abs(d) > 0.05f) stanceBobFrames++;  // foot pinned → body bobbed off baseline
        }

        Assert.True(flightFrames > 0, "δ never released to 0 — no flight frame (body always pinned)");
        Assert.True(stanceBobFrames > 0, "δ never engaged — the vertical ground hold did nothing");
        Assert.True(maxAbs < 23f, $"δ pinned near the box wall ({maxAbs:0.0} px) — over-correcting");
    }

    // --- Phase 5: analytic Jacobian (replaces finite differences) ------------

    // The cadence solve now drives LM with the closed-form §3.3 Jacobian instead of finite
    // differences. The two must agree wherever the residual is differentiable: this runs the
    // solver path over a stride and, each frame a solve ran, compares the analytic Jacobian
    // to a central finite difference of the same residual (the Δφ column is skipped only at a
    // keyframe boundary, where ∂/∂φ genuinely jumps — the §3.5 kink). Sign of the facing-flip
    // lever arm is covered by running both directions. A wrong column shows up as O(1) error.
    [Theory]
    [InlineData("walk", 25f, +1)]
    [InlineData("walk", 25f, -1)]
    [InlineData("run",  90f, +1)]
    [InlineData("run",  90f, -1)]
    public void Solver_AnalyticJacobian_MatchesFiniteDifference(string clipName, float speed, int facing)
    {
        var clip = AnimationStore.LoadAll(StatesDir()).Find(d => d.Name == clipName);
        Assert.True(clip != null, $"{clipName}.json not found");
        var skel = SkeletonExamples.Biped();
        var anim = new CharacterAnimator(skel, 0.6f, new[] { clip }, useSolver: true);

        float dt = 1f / 30f, vx = speed * facing, x = 0f, worst = 0f;
        int checks = 0, wc = -1, wr = -1; float wfd = 0f, wan = 0f;
        for (int i = 0; i < 60; i++)
        {
            x += vx * dt;
            anim.Update(new CharacterAnimSample(
                new Vector2(x, 0f), new Vector2(vx, 0f), facing, true, "WalkState", "", dt));
            float e = anim.MaxJacobianError();
            if (e >= 0f) { checks++; if (e > worst) { worst = e; wc = anim.DbgWorstCol; wr = anim.DbgWorstRow; wfd = anim.DbgFd; wan = anim.DbgAnal; } }
        }

        Assert.True(checks > 0, "no cadence solve ran — nothing validated");
        // Relative agreement: ~0.1% is the float32 oracle's noise floor; a real structural
        // error in any column would be orders of magnitude larger.
        Assert.True(worst < 5e-3f, $"analytic Jacobian disagrees with finite differences by {worst:0.000000} (rel) at col {wc} row {wr} (fd {wfd:0.0000} vs anal {wan:0.0000})");
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
