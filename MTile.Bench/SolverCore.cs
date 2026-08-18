using System;
using System.Diagnostics;
using MTile;

namespace MTile.Bench;

// Isolates LeastSquaresSolver's OWN cost at the animation solve's real dimensions.
//
// The --diag attribution says ~80% of an animator solve is spent inside the solver rather
// than in the animator's callbacks, which for a 58×20 problem is an order of magnitude more
// than the flop count predicts. Before concluding anything about the linear algebra — and
// well before reaching for a matrix library — that attribution needs a second opinion from
// a setup where the callbacks provably cost nothing.
//
// So: same m, n, and maxVars, but a residual function that only writes m floats and a
// Jacobian that only writes the m×n block. Whatever time remains is the core's, measured
// directly instead of by subtraction.
internal static class SolverCore
{
    public static void Run()
    {
        Console.WriteLine();
        Console.WriteLine("solver core in isolation (callbacks stubbed to near-zero):");
        Console.WriteLine($"{"m x n (maxVars)",-22} {"us/Minimize",12} {"iters",7} {"rEvals",8} {"resid",8} {"jacob",8} {"algebra",9}");

        // The animation solve's real shape (biped_rabbit: 17 bones → n = 3+17, maxVars =
        // 3+17+2, m ≈ 58), plus neighbours to show how the cost scales.
        Bench(58, 20, 22);
        Bench(58, 20, 20);    // stride == n: is the padded stride hurting?
        Bench(116, 20, 22);   // 2x rows
        Bench(58, 40, 42);    // 2x vars
    }

    private static void Bench(int m, int n, int maxVars)
    {
        var solver = new LeastSquaresSolver(maxVars: maxVars, maxRes: 256);
        var x  = new float[n];
        var lo = new float[n];
        var hi = new float[n];
        for (int i = 0; i < n; i++) { lo[i] = -1f; hi[i] = 1f; }

        // A residual/Jacobian pair with a genuine minimum, so the trust loop behaves like
        // the real one (accepts steps, converges) rather than thrashing on a degenerate
        // problem. Deterministic, allocation-free, and a few flops per row — the point is
        // for the callbacks to be negligible, not absent.
        LeastSquaresSolver.ResidualFn fn = (xs, r) =>
        {
            for (int i = 0; i < m; i++)
            {
                float s = 0.01f * (i + 1);
                for (int j = 0; j < n; j++) s += ((i + j) % 7 - 3) * 0.1f * xs[j];
                r[i] = s;
            }
            return m;
        };
        LeastSquaresSolver.JacobianFn jf = (xs, jac, stride) =>
        {
            for (int i = 0; i < m; i++)
                for (int j = 0; j < n; j++)
                    jac[i * stride + j] = ((i + j) % 7 - 3) * 0.1f;
        };

        LeastSquaresSolver.ProfileCallbacks = true;
        for (int w = 0; w < 200; w++) { Array.Clear(x); solver.Minimize(fn, jf, x, lo, hi); }

        const int N = 2000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < N; i++) { Array.Clear(x); solver.Minimize(fn, jf, x, lo, hi); }
        sw.Stop();

        double toUs = 1e6 / Stopwatch.Frequency;
        Console.WriteLine($"{$"{m} x {n} ({maxVars})",-22} {sw.Elapsed.TotalMilliseconds * 1000.0 / N,12:F1} " +
                          $"{solver.LastIterations,7} {solver.LastResidualEvals,8} " +
                          $"{solver.LastResidualTicks * toUs,8:F1} {solver.LastJacobianTicks * toUs,8:F1} " +
                          $"{solver.LastAlgebraTicks * toUs,9:F1}");
    }
}

// Isolates the single hottest loop in the solver — the JᵀJ accumulation — and compares
// the current memory layout against the transposed one.
//
// Today J is stored ROW-major (`_jac[i * maxVars + a]`), but the JᵀJ loop's inner
// iteration runs over ROWS i for a fixed pair of columns (a, b). So both operands are read
// with a stride of maxVars floats: the worst possible access pattern for that loop, and one
// no auto-vectorizer can do anything with. Storing Jᵀ instead (`jt[a * maxRes + i]`) makes
// both operands contiguous in i, which is both cache-friendly and trivially vectorizable —
// with System.Numerics.Vector<float>, which is BCL (no package) and does emit real wasm
// SIMD under AOT.
//
// This measures what that layout change alone is worth, before any dependency is considered.
internal static class JtJLayout
{
    public static void Run()
    {
        Console.WriteLine();
        Console.WriteLine("JtJ accumulation, current vs transposed layout (m=58, n=20, maxVars=22):");
        Bench(58, 20, 22);
        Bench(116, 20, 22);
    }

    private static void Bench(int m, int n, int maxVars)
    {
        var jac = new float[m * maxVars];          // row-major, as the solver stores it
        var jt  = new float[n * m];                // transposed
        for (int i = 0; i < m; i++)
            for (int j = 0; j < n; j++)
            {
                float v = ((i * 7 + j * 13) % 17 - 8) * 0.05f;
                jac[i * maxVars + j] = v;
                jt[j * m + i] = v;
            }
        var jtj = new float[n * n];

        const int N = 200000;
        // --- current: strided row-major ---
        for (int w = 0; w < 2000; w++) Strided(jac, jtj, m, n, maxVars);
        var sw1 = Stopwatch.StartNew();
        for (int i = 0; i < N; i++) Strided(jac, jtj, m, n, maxVars);
        sw1.Stop();

        // --- transposed, scalar ---
        for (int w = 0; w < 2000; w++) Contiguous(jt, jtj, m, n);
        var sw2 = Stopwatch.StartNew();
        for (int i = 0; i < N; i++) Contiguous(jt, jtj, m, n);
        sw2.Stop();

        // --- transposed, vectorized (System.Numerics.Vector<float>, BCL only) ---
        for (int w = 0; w < 2000; w++) Vectorized(jt, jtj, m, n);
        var sw3 = Stopwatch.StartNew();
        for (int i = 0; i < N; i++) Vectorized(jt, jtj, m, n);
        sw3.Stop();

        double u1 = sw1.Elapsed.TotalMilliseconds * 1000.0 / N;
        double u2 = sw2.Elapsed.TotalMilliseconds * 1000.0 / N;
        double u3 = sw3.Elapsed.TotalMilliseconds * 1000.0 / N;
        Console.WriteLine($"  m={m,-4} strided {u1,7:F2} us   transposed {u2,7:F2} us ({u1 / u2,4:F1}x)   " +
                          $"+Vector<float> {u3,7:F2} us ({u1 / u3,4:F1}x)   [Vector<float>.Count={System.Numerics.Vector<float>.Count}]");
    }

    private static void Strided(float[] jac, float[] jtj, int m, int n, int maxVars)
    {
        for (int a = 0; a < n; a++)
            for (int b = a; b < n; b++)
            {
                float s = 0f;
                for (int i = 0; i < m; i++) s += jac[i * maxVars + a] * jac[i * maxVars + b];
                jtj[a * n + b] = s; jtj[b * n + a] = s;
            }
    }

    private static void Contiguous(float[] jt, float[] jtj, int m, int n)
    {
        for (int a = 0; a < n; a++)
            for (int b = a; b < n; b++)
            {
                float s = 0f;
                int oa = a * m, ob = b * m;
                for (int i = 0; i < m; i++) s += jt[oa + i] * jt[ob + i];
                jtj[a * n + b] = s; jtj[b * n + a] = s;
            }
    }

    private static void Vectorized(float[] jt, float[] jtj, int m, int n)
    {
        int w = System.Numerics.Vector<float>.Count;
        for (int a = 0; a < n; a++)
            for (int b = a; b < n; b++)
            {
                int oa = a * m, ob = b * m;
                var acc = System.Numerics.Vector<float>.Zero;
                int i = 0;
                for (; i <= m - w; i += w)
                    acc += new System.Numerics.Vector<float>(jt, oa + i)
                         * new System.Numerics.Vector<float>(jt, ob + i);
                float s = System.Numerics.Vector.Dot(acc, System.Numerics.Vector<float>.One);
                for (; i < m; i++) s += jt[oa + i] * jt[ob + i];
                jtj[a * n + b] = s; jtj[b * n + a] = s;
            }
    }
}
