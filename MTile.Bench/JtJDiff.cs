using System;

namespace MTile.Bench;

// Is the vectorized JᵀJ merely REORDERED, or is it WRONG?
//
// "Reordering the sum changed the solver's behaviour" is also exactly what you would say if
// you had an indexing bug, so this measures the actual disagreement between the two
// accumulations on identical input at the real problem dimensions. Rounding-only should land
// near float epsilon (~1e-7 relative). Anything larger is a bug, not noise.
internal static class JtJDiff
{
    public static void Run()
    {
        Console.WriteLine();
        Console.WriteLine("JᵀJ scalar vs vectorized, same J (rounding check):");
        foreach (var (m, n, maxVars) in new[] { (58, 20, 22), (116, 23, 26), (116, 20, 22) })
            One(m, n, maxVars);
    }

    private static void One(int m, int n, int maxVars)
    {
        var jac = new float[m * maxVars];
        // Deterministic, no Random: a spread of magnitudes so cancellation is in play.
        for (int i = 0; i < m; i++)
            for (int j = 0; j < n; j++)
                jac[i * maxVars + j] = MathF.Sin(i * 0.37f + j * 1.11f) * MathF.Exp((j % 5) - 2);

        var jt = new float[n * m];
        for (int a = 0; a < n; a++)
            for (int i = 0; i < m; i++) jt[a * m + i] = jac[i * maxVars + a];

        double worstRel = 0, worstAbs = 0;
        int w = System.Numerics.Vector<float>.Count;
        for (int a = 0; a < n; a++)
            for (int b = a; b < n; b++)
            {
                float s0 = 0f;
                for (int i = 0; i < m; i++) s0 += jac[i * maxVars + a] * jac[i * maxVars + b];

                var acc = System.Numerics.Vector<float>.Zero;
                int k = 0;
                for (; k <= m - w; k += w)
                    acc += new System.Numerics.Vector<float>(jt, a * m + k)
                         * new System.Numerics.Vector<float>(jt, b * m + k);
                float s1 = System.Numerics.Vector.Dot(acc, System.Numerics.Vector<float>.One);
                for (; k < m; k++) s1 += jt[a * m + k] * jt[b * m + k];

                double abs = Math.Abs(s0 - s1);
                worstAbs = Math.Max(worstAbs, abs);
                if (Math.Abs(s0) > 1e-6) worstRel = Math.Max(worstRel, abs / Math.Abs(s0));
            }

        Console.WriteLine($"  m={m,-4} n={n,-3} lanes={w}   worst abs {worstAbs,10:E2}   worst rel {worstRel,10:E2}");
    }
}
