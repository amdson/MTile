using System;
using Microsoft.Xna.Framework;

namespace MTile;

// General small-scale Levenberg–Marquardt least-squares minimizer:
//
//     minimize   Σ_k r_k(x)²     subject to a box   lo ≤ x ≤ hi.
//
// Dense and allocation-free after construction (all scratch is sized to the max
// problem up front and reused), pure MathF → identical on DesktopGL and the
// KNI/Blazor WASM build. This is the reusable core the generalized animation solver
// is built on (see Plans/ANIMATION_SOLVER_PLAN.md): the caller supplies a residual
// function r(x), the box, and a starting point; the solver walks x toward a local
// minimum of the sum of squares.
//
// The residual function is re-evaluated at perturbed x so the Jacobian can be taken
// by FINITE DIFFERENCES — robust and trivial to get right while the framework is
// young. Analytic Jacobians (the 2D lever-arm trick) are a later optimization that
// only pays off once the variable count grows; the plan tracks that.
//
// Render-only: the animator never feeds the sim, so the non-determinism inherent in
// floating-point iteration counts / damping is fine here (unlike sim code).
public sealed class LeastSquaresSolver
{
    // Fill `residuals` with r(x) and return the count actually written. The count must
    // be STABLE for the duration of one Minimize call (the Jacobian assumes fixed rows).
    public delegate int ResidualFn(ReadOnlySpan<float> x, Span<float> residuals);

    // Optional ANALYTIC Jacobian: fill `jac` (row-major, the given `stride` per row — i.e.
    // J[i,j] = jac[i*stride + j] = ∂r_i/∂x_j) for the m residual rows and n variables at x.
    // The solver zeroes the active m×n block before calling, so only the nonzero entries
    // need be written. Rows MUST be in the same order the ResidualFn emits them. When no
    // JacobianFn is supplied the solver falls back to finite differences (below).
    public delegate void JacobianFn(ReadOnlySpan<float> x, Span<float> jac, int stride);

    private readonly int _maxVars, _maxRes;
    private readonly float[] _r, _rTrial;      // [maxRes]   residuals at x / at trial
    private readonly float[] _jac;             // [maxRes*maxVars]  J, row-major  (i*maxVars + j)
    private readonly float[] _g;               // [maxVars]  gradient  Jᵀr
    private readonly float[] _jtj, _A;         // [maxVars*maxVars]  JᵀJ and the damped copy
    private readonly float[] _step, _xTrial;   // [maxVars]

    public LeastSquaresSolver(int maxVars, int maxRes)
    {
        _maxVars = maxVars; _maxRes = maxRes;
        _r = new float[maxRes]; _rTrial = new float[maxRes];
        _jac = new float[maxRes * maxVars];
        _g = new float[maxVars];
        _jtj = new float[maxVars * maxVars];
        _A   = new float[maxVars * maxVars];
        _step = new float[maxVars]; _xTrial = new float[maxVars];
    }

    // Minimize in place from the starting x (finite-difference Jacobian). Returns the
    // final sum-of-squares cost.
    public float Minimize(ResidualFn fn, Span<float> x, ReadOnlySpan<float> lo,
                          ReadOnlySpan<float> hi, int iters = 12)
        => Minimize(fn, null, x, lo, hi, iters);

    // Minimize in place using an ANALYTIC Jacobian `jac` (null → finite differences).
    // Returns the final sum-of-squares cost.
    public float Minimize(ResidualFn fn, JacobianFn jac, Span<float> x, ReadOnlySpan<float> lo,
                          ReadOnlySpan<float> hi, int iters = 12)
    {
        int n = x.Length;
        int m = fn(x, _r);
        float cost = SumSq(_r, m);
        float mu = 1e-3f;

        for (int it = 0; it < iters && cost > 1e-12f; it++)
        {
            // --- Jacobian  J[i,j] = ∂r_i/∂x_j : analytic if supplied, else finite diff ---
            if (jac != null)
            {
                for (int i = 0; i < m; i++)
                    for (int j = 0; j < n; j++) _jac[i * _maxVars + j] = 0f;
                jac(x, _jac, _maxVars);
            }
            else for (int j = 0; j < n; j++)
            {
                float xj = x[j];
                // Step relative to the variable's box width, not just its magnitude: a
                // step that's too small reads a locally-flat slope as zero and stalls LM
                // at a starting point where the objective only curves away over a finite
                // span (e.g. a planted foot whose horizontal velocity is ~0 at the instant
                // it lands). A range-relative secant sees that structure; the accept/reject
                // trust loop still refines toward the true minimum.
                float span = hi[j] - lo[j];
                float h    = span > 1e-6f ? 0.02f * span : 1e-3f * MathF.Max(1f, MathF.Abs(xj));
                if (xj + h > hi[j]) h = -h;           // step inward off an upper bound
                x[j] = xj + h;
                fn(x, _rTrial);
                float inv = 1f / h;
                for (int i = 0; i < m; i++) _jac[i * _maxVars + j] = (_rTrial[i] - _r[i]) * inv;
                x[j] = xj;
            }

            // --- normal equations: g = Jᵀr,  JtJ = JᵀJ ---
            for (int a = 0; a < n; a++)
            {
                float ga = 0f;
                for (int i = 0; i < m; i++) ga += _jac[i * _maxVars + a] * _r[i];
                _g[a] = ga;
                for (int b = a; b < n; b++)
                {
                    float s = 0f;
                    for (int i = 0; i < m; i++) s += _jac[i * _maxVars + a] * _jac[i * _maxVars + b];
                    _jtj[a * n + b] = s;
                    _jtj[b * n + a] = s;
                }
            }

            // --- damped step, accept/reject by cost (the LM trust loop) ---
            bool improved = false;
            for (int tries = 0; tries < 8; tries++)
            {
                for (int a = 0; a < n; a++)
                    for (int b = 0; b < n; b++)
                        _A[a * n + b] = _jtj[a * n + b]
                                      + (a == b ? mu * MathF.Max(_jtj[a * n + a], 1e-6f) : 0f);

                if (!CholSolve(_A, _g, _step, n)) { mu *= 4f; continue; }   // step = A⁻¹g

                for (int a = 0; a < n; a++)
                    _xTrial[a] = MathHelper.Clamp(x[a] - _step[a], lo[a], hi[a]);   // Gauss–Newton: x − A⁻¹g

                int m2 = fn(new ReadOnlySpan<float>(_xTrial, 0, n), _rTrial);
                float trial = SumSq(_rTrial, m2);
                if (trial < cost)
                {
                    for (int a = 0; a < n; a++) x[a] = _xTrial[a];
                    Array.Copy(_rTrial, _r, m);
                    cost = trial;
                    mu = MathF.Max(mu * 0.3f, 1e-7f);
                    improved = true;
                    break;
                }
                mu *= 4f;
            }
            if (!improved) break;   // damping couldn't find a downhill step → converged / stuck
        }
        return cost;
    }

    // Sum of squares of r(x) — for picking a seed (e.g. a coarse global search) before
    // a local Minimize. Uses the residual scratch, so don't call mid-Minimize.
    public float Cost(ResidualFn fn, ReadOnlySpan<float> x) => SumSq(_r, fn(x, _r));

    private static float SumSq(float[] v, int n)
    {
        float s = 0f;
        for (int i = 0; i < n; i++) s += v[i] * v[i];
        return s;
    }

    // Solve A·step = b for symmetric positive-definite A (row-major n×n) via Cholesky,
    // factoring in place into A's lower triangle. Returns false if A is not PD (the
    // caller bumps the LM damping and retries). b is read-only; result lands in step.
    private static bool CholSolve(float[] A, float[] b, float[] step, int n)
    {
        for (int j = 0; j < n; j++)
        {
            float d = A[j * n + j];
            for (int k = 0; k < j; k++) d -= A[j * n + k] * A[j * n + k];
            if (d <= 1e-12f) return false;
            float ljj = MathF.Sqrt(d);
            A[j * n + j] = ljj;
            for (int i = j + 1; i < n; i++)
            {
                float s = A[i * n + j];
                for (int k = 0; k < j; k++) s -= A[i * n + k] * A[j * n + k];
                A[i * n + j] = s / ljj;
            }
        }
        for (int i = 0; i < n; i++)                       // forward:  L y = b
        {
            float s = b[i];
            for (int k = 0; k < i; k++) s -= A[i * n + k] * step[k];
            step[i] = s / A[i * n + i];
        }
        for (int i = n - 1; i >= 0; i--)                  // back:     Lᵀ x = y
        {
            float s = step[i];
            for (int k = i + 1; k < n; k++) s -= A[k * n + i] * step[k];
            step[i] = s / A[i * n + i];
        }
        return true;
    }
}
