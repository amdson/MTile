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

    // --- cadence parity: solver path vs golden-section path ------------------

    // The two cadence paths minimize the same objective, so over the same input they
    // should advance the locomotion phase by nearly the same total. Run real clips at
    // in-band speeds, both directions, and compare.
    [Theory]
    [InlineData("walk", 25f, +1)]
    [InlineData("walk", 25f, -1)]
    [InlineData("run",  90f, +1)]
    [InlineData("run",  90f, -1)]
    public void SolverPath_MatchesGoldenSection_OnRealClips(string clipName, float speed, int facing)
    {
        var clip = AnimationStore.LoadAll(StatesDir()).Find(d => d.Name == clipName);
        Assert.True(clip != null, $"{clipName}.json not found");
        var skel = SkeletonExamples.Biped();

        float golden = TotalPhase(new CharacterAnimator(skel, 0.6f, new[] { clip }, useSolver: false), speed, facing);
        float solver = TotalPhase(new CharacterAnimator(skel, 0.6f, new[] { clip }, useSolver: true ), speed, facing);

        // Both must actually advance (not the frozen failure mode)...
        Assert.True(golden > 0.2f, $"golden froze ({golden:0.000})");
        Assert.True(solver > 0.2f, $"solver froze ({solver:0.000})");
        // ...and land close to each other (same minimum, different minimizer).
        Assert.InRange(solver / golden, 0.85f, 1.15f);
    }

    private static float TotalPhase(CharacterAnimator anim, float speed, int facing)
    {
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
