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
    // Column-major copy of J, rebuilt each iteration so the JᵀJ dot products walk
    // contiguous memory and can be vectorized. [maxVars*maxRes] (a*maxRes + i).
    private readonly float[] _jacT;
    private readonly float[] _g;               // [maxVars]  gradient  Jᵀr
    private readonly float[] _jtj, _A;         // [maxVars*maxVars]  JᵀJ and the damped copy
    private readonly double[] _jtjD, _AD;      // the same, when DoubleAccum is on
    private readonly double[] _gD, _stepD;
    private readonly float[] _step, _xTrial;   // [maxVars]
    // QR path scratch (see QrStep): column-major J copy + rhs, the stacked 2n×n system, and
    // the column norms that form the damping metric.
    private readonly float[] _qrA, _qrB, _qrS, _qrSb, _qrColSq;
    private readonly float[] _dInv, _gScaled;  // [maxVars] Jacobi column scaling
    private readonly bool[]  _fixed;           // [maxVars] active-set mask: variable held this iteration

    // Work counters for the last Minimize call. The wall-clock cost of a solve is almost
    // entirely (residual evals + Jacobian evals) × the cost of one pose rebuild, because
    // the caller's ResidualFn re-samples the clip, recomposes the overlay stack and
    // re-runs forward kinematics over the whole rig on every single call. The iteration
    // count alone doesn't tell you that: each of the `iters` outer steps can burn up to 8
    // more residual evals in the trust-region retry loop below, so the true multiplier
    // ranges over almost an order of magnitude depending on how hard the step is to
    // accept. Diagnostics only — nothing reads these to make a decision.
    public int LastIterations { get; private set; }
    public int LastResidualEvals { get; private set; }
    public int LastJacobianEvals { get; private set; }
    // Row count m of the last solve. The core's own cost is O(iters · n² · m) in the normal
    // equations, so m is the lever on everything the callbacks don't explain.
    public int LastRows { get; private set; }

    // Final sum-of-squares cost of the last solve. The quality check when a change alters
    // the LM's numerical path (e.g. vectorized normal equations): comparing DRAWN POSES is
    // misleading on the cadence path, where a hair of difference in Δφ shifts the phase and
    // shows up as a huge per-bone angle delta that a viewer would read as identical motion.
    // Cost is the objective the solver is actually minimizing, so cost parity means the
    // solve is as good, whatever route it took.
    public float LastCost { get; private set; }
    // Cost after each iteration of the last solve (index 0 = starting cost). Lets a caller
    // see whether the iterations are BUYING anything: this solver has no relative-improvement
    // (ftol), step-size (xtol) or gradient (gtol) stopping test — only the iteration cap and
    // an absolute cost floor — so it keeps stepping while any downhill move exists, however
    // microscopic. Diagnostics only.
    public readonly float[] CostTrace = new float[16];
    public int CostTraceCount { get; private set; }
    // Stopwatch ticks spent in the caller's callbacks vs. in this class's own linear
    // algebra (normal equations + Cholesky + the trust loop). Splitting these is the whole
    // point: "the solve is slow" is actionable only once you know whether the fix is fewer
    // rows, cheaper callbacks, or fewer iterations. Gated so the timing calls themselves
    // stay out of the shipping path.
    public static bool ProfileCallbacks;
    public long LastResidualTicks { get; private set; }
    public long LastJacobianTicks { get; private set; }
    public long LastAlgebraTicks  { get; private set; }

    public LeastSquaresSolver(int maxVars, int maxRes)
    {
        _maxVars = maxVars; _maxRes = maxRes;
        _r = new float[maxRes]; _rTrial = new float[maxRes];
        _jac  = new float[maxRes * maxVars];
        _jacT = new float[maxVars * maxRes];
        _qrA  = new float[maxVars * maxRes];
        _qrB  = new float[maxRes];
        _qrS  = new float[maxVars * 2 * maxVars];
        _qrSb = new float[2 * maxVars];
        _qrColSq = new float[maxVars];
        _dInv    = new float[maxVars];
        _fixed   = new bool[maxVars];
        _gScaled = new float[maxVars];
        _g = new float[maxVars];
        _jtj = new float[maxVars * maxVars];
        _A   = new float[maxVars * maxVars];
        _jtjD = new double[maxVars * maxVars];
        _AD   = new double[maxVars * maxVars];
        _gD   = new double[maxVars];
        _stepD = new double[maxVars];
        _step = new float[maxVars]; _xTrial = new float[maxVars];
    }

    // Minimize in place from the starting x (finite-difference Jacobian). Returns the
    // final sum-of-squares cost.
    public float Minimize(ResidualFn fn, Span<float> x, ReadOnlySpan<float> lo,
                          ReadOnlySpan<float> hi, int iters = 12)
        => Minimize(fn, null, x, lo, hi, iters);

    // Relative-cost-reduction stopping test (MINPACK's `ftol`, Ceres' function_tolerance):
    // stop once an iteration improves the cost by less than this fraction. Every serious LM
    // implementation has one; this class historically had none, so it ran to the iteration
    // cap whenever it could find ANY downhill step, however microscopic.
    //
    // DEFAULT 0 = disabled = the historical behaviour, bit-for-bit. Turning it on changes
    // the solved pose slightly (it stops earlier), so it is opt-in until someone looks at
    // the result on screen — see Plans/PERF_AUDIT.md Finding 1c for what it is worth.
    public static float Ftol;

    // Vectorized normal equations (transposed J + Vector<float> lanes). On by default;
    // the scalar path is kept so the two can be compared pose-for-pose, since the lane
    // split changes the summation order and therefore the LM's exact trajectory.
    public static bool VectorizeNormalEquations = true;

    // Accumulate JᵀJ, the gradient, and the Cholesky in double rather than float.
    //
    // The loss weights span roughly 5..5000, so the SQUARED weights entering each dot
    // product of JᵀJ span ~1e6. Terms contributed by the low-weight priors therefore land
    // below the float32 epsilon of the terms contributed by the hard constraints, and are
    // dropped outright — cancellation from dynamic range WITHIN a single sum. No column
    // scaling can reach that: a per-column constant factors straight out of the dot
    // product, leaving the relative rounding identical. A double accumulator carries ~9
    // more digits than the spread needs and removes it.
    //
    // This is a DIFFERENT defect from the squared condition number, which is a property of
    // the factorization and is what the QR path addresses. Separable, hence two switches.
    public static bool DoubleAccum;

    // With DoubleAccum on, round JᵀJ back to float32 and factor in float. Splits the two
    // halves of the win: what the wide ACCUMULATION buys, vs what the wide FACTORIZATION
    // buys. Diagnostic only.
    public static bool DoubleAccumRoundToFloat;

    // Use the QR path instead of the normal equations (see QrStep). Off by default while it
    // is being evaluated; per-call override via the `qr` parameter.
    public static bool UseQr;

    // Jacobi column scaling on the normal-equations path (see the damping loop). OFF by
    // default. It works — it drops the conditioning of the matrix Cholesky factors from
    // ~1e6 to ~9 — but it does NOT improve the answer, because the precision is lost when
    // JᵀJ is FORMED, not when it is factored. Kept because it is exact and may matter if the
    // formation is ever done in higher precision; not enabled, because it changed biped/run's
    // result with no benefit to show for it. See Plans/PERF_AUDIT.md Finding 1b.
    public static bool JacobiScale;
    // Active-set treatment of the box bounds. Historically the box was enforced by PROJECTION
    // only: solve the free Gauss–Newton step, then clamp the trial point. That is fine while
    // the bounds are slack, but a variable pressed against a wall poisons every other
    // variable's step — the clamped component was computed jointly with theirs, so once it
    // is cut off the rest of the step no longer fits, the trial cost rises, and the trust
    // loop answers by inflating µ for ALL variables. With Δφ boxed to a ramp (MaxPhaseAccel)
    // this showed up as a hard-tier hand pin missed by 2px and the terrain no-pen by 1.4px:
    // the cadence wall was starving the IK. The rule here (projected Newton, Bertsekas):
    // a variable AT a bound whose gradient points OUT of the box — or whose box is a point,
    // like the static solve's locked Δφ — is held for this iteration: its Jacobian column
    // is zeroed and it gets a unit diagonal, so its step is exactly 0 and the others solve
    // as if it were a constant. Off = projection only (historical behaviour, bit-for-bit).
    public static bool ActiveSetBounds = true;

    // The active-set pass (see ActiveSetBounds). Runs on the freshly built Jacobian, before
    // either factorization consumes it. g = Jᵀr is the gradient of ½‖r‖²; the descent
    // direction is −g, so a variable at its LOWER bound wants to leave the box when g > 0,
    // at its UPPER bound when g < 0. The mask is consumed by the normal-equation
    // accumulation (unit diagonal) and QrFactor (unit column scale).
    private void ApplyActiveSet(ReadOnlySpan<float> x, ReadOnlySpan<float> lo, ReadOnlySpan<float> hi, int m, int n)
    {
        for (int j = 0; j < n; j++)
        {
            float span = hi[j] - lo[j];
            bool hold;
            if (span <= 1e-9f) hold = true;                    // point box: the variable is locked
            else
            {
                float tol = 1e-6f * span;
                bool atLo = x[j] - lo[j] <= tol, atHi = hi[j] - x[j] <= tol;
                if (!atLo && !atHi) hold = false;
                else
                {
                    float g = 0f;
                    for (int i = 0; i < m; i++) g += _jac[i * _maxVars + j] * _r[i];
                    hold = (atLo && g > 0f) || (atHi && g < 0f);
                }
            }
            _fixed[j] = hold;
            if (hold)
                for (int i = 0; i < m; i++) _jac[i * _maxVars + j] = 0f;
        }
    }

    // Minimize in place using an ANALYTIC Jacobian `jac` (null → finite differences).
    // Returns the final sum-of-squares cost.
    public float Minimize(ResidualFn fn, JacobianFn jac, Span<float> x, ReadOnlySpan<float> lo,
                          ReadOnlySpan<float> hi, int iters = 12, float ftol = -1f,
                          bool? vectorize = null, bool? qr = null)
    {
        int n = x.Length;
        if (ftol < 0f) ftol = Ftol;   // negative = "use the process-wide default"
        LastIterations = 0; LastResidualEvals = 1; LastJacobianEvals = 0;
        LastResidualTicks = LastJacobianTicks = LastAlgebraTicks = 0;
        long _t0 = ProfileCallbacks ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        long _tAll = _t0;
        int m = fn(x, _r);
        if (ProfileCallbacks) { LastResidualTicks += System.Diagnostics.Stopwatch.GetTimestamp() - _t0; }
        LastRows = m;
        float cost = SumSq(_r, m);
        CostTraceCount = 0;
        if (ProfileCallbacks && CostTraceCount < CostTrace.Length) CostTrace[CostTraceCount++] = cost;
        float mu = 1e-3f;

        for (int it = 0; it < iters && cost > 1e-12f; it++, LastIterations++)
        {
            // --- Jacobian  J[i,j] = ∂r_i/∂x_j : analytic if supplied, else finite diff ---
            if (jac != null)
            {
                for (int i = 0; i < m; i++)
                    for (int j = 0; j < n; j++) _jac[i * _maxVars + j] = 0f;
                if (ProfileCallbacks) _t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                jac(x, _jac, _maxVars);
                if (ProfileCallbacks) LastJacobianTicks += System.Diagnostics.Stopwatch.GetTimestamp() - _t0;
                LastJacobianEvals++;
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

            if (ActiveSetBounds) ApplyActiveSet(x, lo, hi, m, n);
            else Array.Clear(_fixed, 0, n);

            bool useQr = qr ?? UseQr;

            // QR path skips the JᵀJ build entirely — it factors J once, here, and then only
            // re-solves a small n×n system per damping value.
            if (useQr) QrFactor(m, n);
            else
            {
            // --- normal equations: g = Jᵀr,  JtJ = JᵀJ ---
            //
            // This is the solver's hot loop: O(n²·m/2) multiply-adds, ~80% of the LM's
            // algebra time (Plans/PERF_AUDIT.md Finding 1a). Two things were wrong with
            // doing it straight off the row-major J:
            //
            //   1. Each column walk strides by _maxVars, so a column of J is scattered.
            //   2. `s` is ONE serial accumulator — every add waits on the previous one's
            //      ~4-cycle latency, so the loop runs at latency speed, not throughput.
            //
            // Transposing first makes each column contiguous, which then lets
            // Vector<float> carry Count independent accumulators and break the chain.
            // Transposing costs O(n·m) against the O(n²·m/2) it feeds — for a 23-variable
            // rig that is ~1/6 of the work it saves. Measured 2.5–3.4× on the desktop;
            // wasm has 4 lanes rather than AVX2's 8, so expect proportionally less there.
            //
            // The lane split changes the summation ORDER, so JᵀJ is no longer bit-identical
            // to the scalar version. That is acceptable HERE and nowhere near the sim: the
            // animation solver is render-only and never feeds Simulation.Step, so it cannot
            // move a rollback checksum. Do not copy this reasoning into Character/.
            for (int a = 0; a < n; a++)
                for (int i = 0; i < m; i++) _jacT[a * _maxRes + i] = _jac[i * _maxVars + a];

            if (DoubleAccum)
            {
                // Same sums, same order as the scalar path — only the accumulator widens.
                // Deliberately NOT vectorized: the question here is what the accumulator
                // width is worth, and lane-splitting would confound it with reordering.
                for (int a = 0; a < n; a++)
                {
                    int oa = a * _maxRes;
                    double ga = 0.0;
                    for (int i = 0; i < m; i++) ga += (double)_jacT[oa + i] * _r[i];
                    _gD[a] = ga;
                    _g[a] = (float)ga;
                    for (int b = a; b < n; b++)
                    {
                        int ob = b * _maxRes;
                        double s = 0.0;
                        for (int i = 0; i < m; i++) s += (double)_jacT[oa + i] * _jacT[ob + i];
                        _jtjD[a * n + b] = s;
                        _jtjD[b * n + a] = s;
                        _jtj[a * n + b] = (float)s;    // kept in sync for the diagnostics below
                        _jtj[b * n + a] = (float)s;
                    }
                }
                goto accumulated;
            }

            int w = (vectorize ?? VectorizeNormalEquations) ? System.Numerics.Vector<float>.Count : int.MaxValue;
            // w = int.MaxValue makes the lane loop unreachable, leaving the scalar tail to
            // do all m elements — the historical accumulation, bit-for-bit.
            for (int a = 0; a < n; a++)
            {
                int oa = a * _maxRes;
                float ga = 0f;
                for (int i = 0; i < m; i++) ga += _jacT[oa + i] * _r[i];
                _g[a] = ga;
                for (int b = a; b < n; b++)
                {
                    int ob = b * _maxRes;
                    var acc = System.Numerics.Vector<float>.Zero;
                    int i = 0;
                    for (; i <= m - w; i += w)
                        acc += new System.Numerics.Vector<float>(_jacT, oa + i)
                             * new System.Numerics.Vector<float>(_jacT, ob + i);
                    float s = System.Numerics.Vector.Dot(acc, System.Numerics.Vector<float>.One);
                    for (; i < m; i++) s += _jacT[oa + i] * _jacT[ob + i];   // tail
                    _jtj[a * n + b] = s;
                    _jtj[b * n + a] = s;
                }
            }
            accumulated: ;
            // Held variables (ActiveSetBounds): their column is zero, so their JᵀJ row/col and
            // gradient are 0 — give them a unit diagonal so every damped system stays PD and
            // solves their step as exactly 0 (g_j = 0 ⇒ step_j = 0/(1+µ)).
            for (int a = 0; a < n; a++)
                if (_fixed[a]) { _jtj[a * n + a] = 1f; if (DoubleAccum) _jtjD[a * n + a] = 1.0; }
            }

            if (!useQr)   // JᵀJ only exists on the normal-equations path
            {
                float dMin = float.MaxValue, dMax = 0f;
                for (int a = 0; a < n; a++)
                { float d = _jtj[a * n + a]; if (d > dMax) dMax = d; if (d < dMin) dMin = d; }
                LastDiagRatio = dMin > 0f ? dMax / dMin : float.PositiveInfinity;
                // Column scale factors, fixed across the damping retries (JᵀJ does not change).
                for (int a = 0; a < n; a++)
                    _dInv[a] = 1f / MathF.Sqrt(MathF.Max(_jtj[a * n + a], 1e-6f));
            }

            // --- damped step, accept/reject by cost (the LM trust loop) ---
            float reduction = 0f;
            bool improved = false;
            for (int tries = 0; tries < 8; tries++)
            {
                if (useQr)
                {
                    if (!QrStep(m, n, mu)) { mu *= 4f; continue; }
                }
                else if (JacobiScale)
                {
                    // Jacobi (column) scaling — MINPACK's `diag`, Ceres' jacobi_scaling.
                    // With D = diag(‖J col a‖), the damped system factors exactly as
                    //     JᵀJ + µD² = D · (D⁻¹JᵀJD⁻¹ + µI) · D
                    // so solving the bracketed matrix and rescaling the result is the SAME
                    // step, computed in coordinates where the JᵀJ part has unit diagonal.
                    // Nothing here changes the answer in exact arithmetic; it changes how
                    // much of the answer survives float32. Note the damping was already
                    // scale-invariant (µ·JᵀJ_aa) — only the factorization was not.
                    for (int a = 0; a < n; a++)
                        for (int b = 0; b < n; b++)
                            _A[a * n + b] = _jtj[a * n + b] * _dInv[a] * _dInv[b]
                                          + (a == b ? mu : 0f);
                    for (int a = 0; a < n; a++) _gScaled[a] = _g[a] * _dInv[a];

                    if (!CholSolve(_A, _gScaled, _step, n)) { mu *= 4f; continue; }
                    for (int a = 0; a < n; a++) _step[a] *= _dInv[a];
                }
                else if (DoubleAccum && !DoubleAccumRoundToFloat)
                {
                    // Same damped system and same damping metric as the float branch below
                    // — only the arithmetic width differs, so the two are comparable
                    // step-for-step (and comparable to QR, whose D matches this one).
                    for (int a = 0; a < n; a++)
                        for (int b = 0; b < n; b++)
                            _AD[a * n + b] = _jtjD[a * n + b]
                                           + (a == b ? mu * Math.Max(_jtjD[a * n + a], 1e-6) : 0.0);

                    if (!CholSolveD(_AD, _gD, _stepD, n)) { mu *= 4f; continue; }
                    for (int a = 0; a < n; a++) _step[a] = (float)_stepD[a];
                }
                else
                {
                    for (int a = 0; a < n; a++)
                        for (int b = 0; b < n; b++)
                            _A[a * n + b] = _jtj[a * n + b]
                                          + (a == b ? mu * MathF.Max(_jtj[a * n + a], 1e-6f) : 0f);

                    if (!CholSolve(_A, _g, _step, n)) { mu *= 4f; continue; }   // step = A⁻¹g
                }

                for (int a = 0; a < n; a++)
                    _xTrial[a] = MathHelper.Clamp(x[a] - _step[a], lo[a], hi[a]);   // Gauss–Newton: x − A⁻¹g

                if (ProfileCallbacks) _t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                int m2 = fn(new ReadOnlySpan<float>(_xTrial, 0, n), _rTrial);
                if (ProfileCallbacks) LastResidualTicks += System.Diagnostics.Stopwatch.GetTimestamp() - _t0;
                LastResidualEvals++;
                float trial = SumSq(_rTrial, m2);
                if (trial < cost)
                {
                    for (int a = 0; a < n; a++) x[a] = _xTrial[a];
                    Array.Copy(_rTrial, _r, m);
                    // Relative reduction this iteration bought, measured BEFORE cost is
                    // overwritten — the ftol test below reads it.
                    reduction = cost > 0f ? (cost - trial) / cost : 0f;
                    cost = trial;
                    mu = MathF.Max(mu * 0.3f, 1e-7f);
                    improved = true;
                    break;
                }
                mu *= 4f;
            }
            if (ProfileCallbacks && CostTraceCount < CostTrace.Length) CostTrace[CostTraceCount++] = cost;
            if (!improved) break;   // damping couldn't find a downhill step → converged / stuck
            if (ftol > 0f && reduction < ftol) break;   // converged: further iterations buy nothing
        }
        if (ProfileCallbacks)
            LastAlgebraTicks = (System.Diagnostics.Stopwatch.GetTimestamp() - _tAll)
                             - LastResidualTicks - LastJacobianTicks;   // incl. the J zeroing loop
        LastCost = cost;
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
    // Worst pivot ratio seen in the last CholSolve — a cheap condition-number proxy for JᵀJ.

    // ---- QR path (MINPACK lmder/lmpar structure) --------------------------------
    //
    // The normal-equations path forms JᵀJ, which SQUARES the condition number: a Jacobian
    // with cond(J) ≈ 1e3 — ordinary for this rig — gives cond(JᵀJ) ≈ 1e6, close enough to
    // the float32 edge that a legal reordering of one sum moves the answer (Finding 1b).
    // Working on J directly through a QR factorization keeps cond(J), buying back ~3 digits.
    //
    // Structure matters for cost. Re-factoring the full augmented [J; √µ D] on every damping
    // try would be ~7× the algebra of the Cholesky path. Instead, as MINPACK does:
    //
    //   once per ITERATION:  Householder QR of J (m×n) → R (n×n upper), and Qᵀ(−r) → qtb
    //   once per DAMPING µ:  QR of the small stacked [R; √µ D] (2n×n) → back-substitute
    //
    // The per-µ work is then O(n³) on a 2n×n block rather than O(mn²), and the whole JᵀJ
    // build disappears — so this costs roughly 3× the algebra of normal equations, not 7×.
    //
    // D is the same damping metric the Cholesky path uses (√(µ · max(‖J col‖², 1e-6))), so
    // this solves the SAME damped system, just more stably: it is an apples-to-apples swap,
    // not a different algorithm with different tuning.

    // Householder QR of the m×n column-major matrix `a` (stride `lda`), applying the same
    // reflections to `b` (length m). On return the upper triangle of `a` holds R.
    private static void HouseholderQr(float[] a, int lda, float[] b, int m, int n)
    {
        for (int k = 0; k < n && k < m; k++)
        {
            // Reflector for column k below row k.
            float norm = 0f;
            for (int i = k; i < m; i++) { float v = a[k * lda + i]; norm += v * v; }
            norm = MathF.Sqrt(norm);
            if (norm <= 1e-20f) continue;
            float akk = a[k * lda + k];
            float alpha = akk >= 0f ? -norm : norm;      // sign choice avoids cancellation
            float vk = akk - alpha;
            float vnorm2 = norm * norm - akk * akk + vk * vk;
            if (vnorm2 <= 1e-20f) continue;

            a[k * lda + k] = vk;                          // v stored in place, v[k]=vk
            for (int j = k + 1; j < n; j++)               // apply to the trailing columns
            {
                float dot = 0f;
                for (int i = k; i < m; i++) dot += a[k * lda + i] * a[j * lda + i];
                float f = 2f * dot / vnorm2;
                for (int i = k; i < m; i++) a[j * lda + i] -= f * a[k * lda + i];
            }
            {                                             // ...and to the right-hand side
                float dot = 0f;
                for (int i = k; i < m; i++) dot += a[k * lda + i] * b[i];
                float f = 2f * dot / vnorm2;
                for (int i = k; i < m; i++) b[i] -= f * a[k * lda + i];
            }
            a[k * lda + k] = alpha;                       // R's diagonal entry
        }
    }

    // Factor J ONCE per iteration: column-major copy, column norms (the damping metric),
    // rhs = -r, then Householder QR in place. R lands in the upper triangle of _qrA and
    // Qᵀ(-r) in _qrB. Hoisting this out of the damping loop is the whole point of the
    // MINPACK structure — doing it per µ costs 3-6× and buys nothing.
    private void QrFactor(int m, int n)
    {
        for (int a = 0; a < n; a++)
            for (int i = 0; i < m; i++) _qrA[a * _maxRes + i] = _jac[i * _maxVars + a];
        for (int a = 0; a < n; a++)
        {
            float s = 0f;
            for (int i = 0; i < m; i++) { float v = _qrA[a * _maxRes + i]; s += v * v; }
            // A held variable's column is zero; a unit scale keeps its damping row healthy
            // (its LS solution is 0 either way — the column is decoupled — this just avoids
            // the √(µ·1e-6) floor doing the job).
            _qrColSq[a] = _fixed[a] ? 1f : s;
        }
        for (int i = 0; i < m; i++) _qrB[i] = -_r[i];
        HouseholderQr(_qrA, _maxRes, _qrB, m, n);
    }

    // Per damping value: solve the small stacked system using the factorization above.
    // Equivalent to (JᵀJ + µ·diag)·step = Jᵀr, but never forming JᵀJ.
    private bool QrStep(int m, int n, float mu)
    {
        float[] colSq = _qrColSq;

        // Stacked small system [R; √µ D] δ ≈ [qtb; 0], 2n×n.
        int two = 2 * n;
        Array.Clear(_qrS, 0, n * two);
        for (int a = 0; a < n; a++)
        {
            for (int i = 0; i <= a && i < n; i++) _qrS[a * two + i] = _qrA[a * _maxRes + i];  // R
            _qrS[a * two + n + a] = MathF.Sqrt(mu * MathF.Max(colSq[a], 1e-6f));              // √µ D
        }
        for (int i = 0; i < n; i++) _qrSb[i] = i < m ? _qrB[i] : 0f;
        for (int i = n; i < two; i++) _qrSb[i] = 0f;

        HouseholderQr(_qrS, two, _qrSb, two, n);

        for (int i = n - 1; i >= 0; i--)                  // back-substitute R2 δ = rhs
        {
            float s = _qrSb[i];
            for (int k = i + 1; k < n; k++) s -= _qrS[k * two + i] * _step[k];
            float d = _qrS[i * two + i];
            if (MathF.Abs(d) <= 1e-20f) return false;
            _step[i] = s / d;
        }
        for (int i = 0; i < n; i++) _step[i] = -_step[i];  // δ → step (caller does x − step)
        return true;
    }

    // CholSolve in double. Same algorithm line for line; reports the pivot ratio into the
    // same diagnostic so the two paths stay comparable.
    private static bool CholSolveD(double[] A, double[] b, double[] step, int n)
    {
        double pMin = double.MaxValue, pMax = 0.0;
        for (int j = 0; j < n; j++)
        {
            double d = A[j * n + j];
            for (int k = 0; k < j; k++) d -= A[j * n + k] * A[j * n + k];
            if (d <= 1e-12) { LastPivotRatio = float.PositiveInfinity; return false; }
            pMin = Math.Min(pMin, d); pMax = Math.Max(pMax, d);
            double ljj = Math.Sqrt(d);
            A[j * n + j] = ljj;
            for (int i = j + 1; i < n; i++)
            {
                double s = A[i * n + j];
                for (int k = 0; k < j; k++) s -= A[i * n + k] * A[j * n + k];
                A[i * n + j] = s / ljj;
            }
        }
        LastPivotRatio = pMin > 0.0 ? (float)(pMax / pMin) : float.PositiveInfinity;
        for (int i = 0; i < n; i++)                       // forward:  L y = b
        {
            double s = b[i];
            for (int k = 0; k < i; k++) s -= A[i * n + k] * step[k];
            step[i] = s / A[i * n + i];
        }
        for (int i = n - 1; i >= 0; i--)                  // back:     Lᵀ x = y
        {
            double s = step[i];
            for (int k = i + 1; k < n; k++) s -= A[k * n + i] * step[k];
            step[i] = s / A[i * n + i];
        }
        return true;
    }

    // Forming the normal equations SQUARES cond(J), so this is the number that says whether a
    // last-bit change in JᵀJ can move the computed step by an O(1) amount.
    // NOTE: this is measured inside CholSolve, which runs on the DAMPED matrix
    // A = JᵀJ + µ·diag — so it reports cond(A), not cond(JᵀJ), and it moves with µ. A solve
    // that converges faster keeps µ small, leaves A less regularized, and reports a WORSE
    // number for an unchanged problem. Use LastDiagRatio below to talk about the underlying
    // conditioning; use this only to describe the system actually being factored.
    public static float LastPivotRatio;

    // Ratio of largest to smallest DIAGONAL of the UNDAMPED JᵀJ, i.e.
    // (max column norm / min column norm)². This is the number that tests whether the
    // ill-conditioning is mere SCALING — loss-weight tiers and mismatched variable units
    // (Δφ per frame vs δ in px vs Δθ in rad) — which a diagonal rescale would fix. If the
    // pivot ratio is far worse than this, the columns are genuinely near-dependent instead,
    // and no rescale helps.
    public static float LastDiagRatio;

    private static bool CholSolve(float[] A, float[] b, float[] step, int n)
    {
        float pMin = float.MaxValue, pMax = 0f;
        for (int j = 0; j < n; j++)
        {
            float d = A[j * n + j];
            for (int k = 0; k < j; k++) d -= A[j * n + k] * A[j * n + k];
            if (d <= 1e-12f) { LastPivotRatio = float.PositiveInfinity; return false; }
            pMin = MathF.Min(pMin, d); pMax = MathF.Max(pMax, d);
            float ljj = MathF.Sqrt(d);
            A[j * n + j] = ljj;
            for (int i = j + 1; i < n; i++)
            {
                float s = A[i * n + j];
                for (int k = 0; k < j; k++) s -= A[i * n + k] * A[j * n + k];
                A[i * n + j] = s / ljj;
            }
        }
        LastPivotRatio = pMin > 0f ? pMax / pMin : float.PositiveInfinity;
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
