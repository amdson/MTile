using System;
using Xunit;
using MTile;

namespace MTile.Tests;

// The QR step solver must agree with the Cholesky/normal-equations one wherever the problem
// is well conditioned — that is what makes it a swap rather than a different algorithm.
// (Where it DISAGREES, on ill-conditioned problems, QR is the one to believe: forming JᵀJ
// squares the condition number. See Plans/PERF_AUDIT.md Finding 1b.)
public class QrStepTests
{
    // r(x) = A·x − b for a fixed well-conditioned A: a linear least-squares problem, so both
    // paths should land on the same minimizer.
    private static LeastSquaresSolver.ResidualFn Linear(float[] A, float[] b, int m, int n)
        => (x, r) =>
        {
            for (int i = 0; i < m; i++)
            {
                float s = -b[i];
                for (int j = 0; j < n; j++) s += A[i * n + j] * x[j];
                r[i] = s;
            }
            return m;
        };

    [Fact]
    public void QrAndCholesky_AgreeOnAWellConditionedProblem()
    {
        const int m = 24, n = 6;
        var A = new float[m * n];
        var b = new float[m];
        for (int i = 0; i < m; i++)
        {
            for (int j = 0; j < n; j++) A[i * n + j] = MathF.Sin(i * 0.7f + j * 1.3f) + (i == j ? 2f : 0f);
            b[i] = MathF.Cos(i * 0.41f);
        }

        var lo = new float[n]; var hi = new float[n];
        for (int j = 0; j < n; j++) { lo[j] = -100f; hi[j] = 100f; }

        var solver = new LeastSquaresSolver(n, m);
        var xChol = new float[n];
        float cChol = solver.Minimize(Linear(A, b, m, n), null, xChol, lo, hi, iters: 40, qr: false);

        var solver2 = new LeastSquaresSolver(n, m);
        var xQr = new float[n];
        float cQr = solver2.Minimize(Linear(A, b, m, n), null, xQr, lo, hi, iters: 40, qr: true);

        // Same minimum, and the same place — a loose tolerance because these are two
        // different numerical routes to it, not the same arithmetic.
        Assert.True(MathF.Abs(cQr - cChol) <= 1e-3f * MathF.Max(1f, cChol),
                    $"cost mismatch: chol={cChol} qr={cQr}");
        for (int j = 0; j < n; j++)
            Assert.True(MathF.Abs(xQr[j] - xChol[j]) <= 1e-2f,
                        $"x[{j}] mismatch: chol={xChol[j]} qr={xQr[j]}");
    }

    [Fact]
    public void Qr_RespectsBoxBounds()
    {
        const int m = 8, n = 3;
        var A = new float[m * n];
        var b = new float[m];
        for (int i = 0; i < m; i++)
        {
            for (int j = 0; j < n; j++) A[i * n + j] = (i == j % m) ? 1f : 0.25f;
            b[i] = 50f;                       // pulls the solution well outside the box
        }
        var lo = new float[n]; var hi = new float[n];
        for (int j = 0; j < n; j++) { lo[j] = -1f; hi[j] = 1f; }

        var solver = new LeastSquaresSolver(n, m);
        var x = new float[n];
        solver.Minimize(Linear(A, b, m, n), null, x, lo, hi, iters: 20, qr: true);

        for (int j = 0; j < n; j++)
            Assert.InRange(x[j], lo[j], hi[j]);
    }
}
