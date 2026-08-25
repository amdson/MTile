using System;
using Microsoft.Xna.Framework;

namespace MTile;

// How a channel's per-tick variable enters the dynamics (BALLISTIC_CORRECTOR_PLAN §4).
// VelocityUpdate: z is a velocity delta applied before tick k's position update —
//   it moves position at row tick T by (T − k + 1)·dt along z.
// Force: z is an acceleration held for tick k — Δv = z·dt, moving position at
//   row tick T by (T − k + 1)·dt².
// PositionOffset: z is the path's position deviation AT tick k (px) — it moves
//   position at row tick T by z iff k == T, else nothing. Rows become diagonal
//   in the variables (no cross-tick coupling — the conditioning fix for
//   opposed-row schedules); dynamic honesty comes from ChannelDef.SlewCap
//   bounding adjacent-offset differences (= the deviation's implied velocity).
public enum LeverKind { VelocityUpdate, Force, PositionOffset }

// A solver channel: per-tick convex admissible set with a closed-form projection,
// a lever kind, and a quadratic cost weight. The solver is one loop over channels
// and knows no physics — anything not expressible as this triple is not a channel.
public struct ChannelDef
{
    // Bookkeeping identity (CorrectorLedger): which physical actuator this
    // channel is. Never read by the solve — attribution only.
    public CorrectionChannel Id;
    public LeverKind Lever;
    public float     Weight;       // quadratic cost on ‖z_k‖² (Redirect: the ε regularizer)
    public float     Cap;          // Force: ‖z_k‖ ≤ Cap. Redirect: ‖z_k‖ ≤ Cap·dt (a
                                   // plant is a bounded push — 0 = the bare disc)
    public bool      Redirect;     // admissible set = Thales disc of the coast velocity v̂ₖ
                                   // (v′·(v̂ₖ−v′) ≥ 0 ⇔ ‖z + v̂ₖ/2‖ ≤ ‖v̂ₖ‖/2): the exact
                                   // reachable set of composed frictionless projections.
    public int       ActiveFrom;   // ticks [ActiveFrom, ActiveTo) may act; z = 0 outside
    public int       ActiveTo;
    // Restricted-channel extensions (the stand-fold channel stack):
    public Vector2   Axis;         // unit direction; used when AxisOnly
    public bool      AxisOnly;     // z = λ·Axis; λ ∈ [0,cap] (Unilateral) or [−cap,cap]
    public bool      Unilateral;
    public float[]   CapPerTick;   // per-tick cap override (velocity-conditioned sets,
                                   // frozen from the last rollout); null = use Cap
    public bool[]    ActiveMask;   // per-tick activation predicate; null = [ActiveFrom, ActiveTo)
    // Redirect forward bound (maneuvers): the disc never ADDS speed, but it
    // can CONVERT vertical into forward. When ForwardCap > 0, the projection
    // clamps the post-deflection forward component (along ForwardAxis) at
    // max(coast forward, ForwardCap) — a maneuver may keep the forward speed
    // it arrived with but the deflector won't grow it past the cap. 0 = off.
    public Vector2   ForwardAxis;
    public float     ForwardCap;
    // Rise bound — ForwardCap's vertical twin, and the SAME ceiling the leg
    // servo's own cap fades out at (FoldLegPushFadeSpeed). A channel may not
    // leave the body rising faster than max(coast rise, RiseCap). The legs
    // obey this by construction (their cap tapers to zero at the ceiling), so
    // without it a corner-served channel outranks the launch it rides on: the
    // disc adds no energy, but it CONVERTS forward speed into rise, and an
    // unbounded conversion at a corner sent a jump up faster than any jump
    // should go. 0 = off.
    public float     RiseCap;
    // May serve PlantOnly rows (ambient wall faces near a convex corner) —
    // true only on the corner-plant redirect; every other channel takes no
    // hinge gradient from them (walls recruit nothing but a hand-plant).
    public bool      PlantServes;
    // PositionOffset channels only: bound on the implied deviation VELOCITY,
    // |z_k − z_{k−1}| ≤ SlewCap·dt, anchored at z_{ActiveFrom−1} ≡ 0 (the path
    // starts AT the body — deformation builds over ticks, never teleports).
    // 0 = off. Enforced as a projection against the previous iterate's
    // neighbor (approximate mid-chain, exact at the k = ActiveFrom seam,
    // which is the tick the servo actually tracks).
    public float     SlewCap;
    // Row-class compatibility: a SkipSoftHorizontal channel takes no hinge
    // gradient from soft rows (HingeScale < 1) whose normal is horizontal —
    // the x-progress reference. A free deflector serving a soft x row is an
    // air-brake (autopilot braking along held intent); soft VERTICAL rows
    // (hover/envelope tracking — ducks, catches) are legitimate recruiters
    // and never oppose horizontal intent. The channel's displacement still
    // counts toward EVERY row's slack — physics moves the body regardless of
    // which demand motivated the move; only the response is restricted.
    public bool      SkipSoftHorizontal;
}

// One frozen convex subproblem of the sequential-convexification scheme: coast
// velocities (the redirect discs), clearance rows, and channel defs are all fixed;
// Solve runs the fixed-count projected-gradient iterations. The outer passes
// (re-rollout with corrections, rebuild discs/rows, re-solve) live at the
// integration layer where the predictor and constraint builder exist — do NOT
// rebuild sets between individual gradient steps (no descent guarantee there;
// failure would be silent under fixed iteration counts).
//
// Cost: Σ_c w_c Σ_k ‖z‖² + wΔ Σ_c Σ_k ‖z_k − z_{k−1}‖² (anchored at PrevApplied,
// last tick's APPLIED correction) + wH Σ_j max(0, m_j − Σ lever·(z·n̂_j))².
// The hinge gives least-violation best-effort output when infeasible; the
// returned residual is the LINEAR-model violation — the SHIPPED residual must be
// re-measured on a true corrected rollout by the caller (never the surrogate).
//
// Determinism: fixed iteration counts and orderings, cold start at z = 0 (which
// IS warm-starting from the ballistic trajectory — variables are corrections),
// no statics, no allocation (caller owns every array).
public sealed class CorrectionProblem
{
    public int   H;                 // horizon ticks
    public float Dt;
    public Vector2[] CoastVel;      // v̂ₖ, k ∈ [0,H) — redirect disc geometry
    public ClearanceRow[] Rows;
    public int   RowCount;
    public ChannelDef[] Channels;
    public int   ChannelCount;
    public Vector2[] PrevApplied;   // z₋₁ anchor per channel
    public float DeltaWeight;       // wΔ
    public float HingeWeight;       // stiff, fixed — a stiffness constant, not a feel knob
    // Fixed PG iteration count for THIS problem (determinism requires it be fixed
    // per call site, not adaptive). The per-tick budget is DefaultInnerIterations;
    // an entry-feasibility solve over a full arc may afford more (opposed-row
    // schedules need the extra sweeps to unzigzag).
    public int   InnerIterations = CorrectionSolver.DefaultInnerIterations;
    // Optional per-row attribution (length ≥ RowCount, caller-owned): each row's
    // accumulated hinge push into the APPLIED tick-0 variable, summed across
    // iterations/channels — "how hard did this contact shove the correction, and
    // which way". Uniform δv units regardless of lever kind (a Force channel's
    // contribution is scaled by dt), so force-on-the-body = RowPush/dt;
    // projections mean the entries need not sum exactly to z₀. Write-only —
    // null (the default) skips all work, and nothing in the solve reads it back.
    // Feeds the debug overlay's contact arrows and CorrectorLedger's per-contact
    // reaction bookkeeping.
    public Vector2[] RowPush;

    // Deep copy for Diagnostics/QpBench: the live problem is pooled scratch that the next
    // tick overwrites, so a benchmark that wants to replay one frozen subproblem has to own
    // its arrays. Bench-only — the sim never calls this.
    public CorrectionProblem Clone()
    {
        var q = new CorrectionProblem
        {
            H = H, Dt = Dt, RowCount = RowCount, ChannelCount = ChannelCount,
            DeltaWeight = DeltaWeight, HingeWeight = HingeWeight, InnerIterations = InnerIterations,
            CoastVel = (Vector2[])CoastVel.Clone(),
            Rows = (ClearanceRow[])Rows.Clone(),
            PrevApplied = (Vector2[])PrevApplied.Clone(),
            Channels = (ChannelDef[])Channels.Clone(),
            RowPush = RowPush != null ? new Vector2[RowPush.Length] : null,
        };
        for (int c = 0; c < q.ChannelCount; c++)
        {
            if (q.Channels[c].CapPerTick != null) q.Channels[c].CapPerTick = (float[])q.Channels[c].CapPerTick.Clone();
            if (q.Channels[c].ActiveMask != null) q.Channels[c].ActiveMask = (bool[])q.Channels[c].ActiveMask.Clone();
        }
        return q;
    }
}

public static class CorrectionSolver
{
    public const int DefaultInnerIterations = 4;
    public const int MaxChannels = 8;

    // Write-only profiling counters, off by default. Same contract as
    // PlayerCharacter.CorrectorDebug's trajectory capture: nothing in the sim ever READS
    // these, so they cannot influence a step, a replay, or a rollback checksum — the
    // no-sim-affecting-statics rule is about state that feeds back, and this never does.
    // Enabled by MTile.Bench to attribute the corrector's share of a sim tick.
    public static bool Profile;
    public static long Ticks;   // Stopwatch ticks accumulated inside Solve
    public static int  Calls;   // Solve invocations
    public static void ResetProfile() { Ticks = 0; Calls = 0; SumH = SumC = SumR = SumIters = 0; }

    // Per-sweep step magnitude was measured here once (see Plans/PERF_AUDIT.md): the fold
    // path's 16 sweeps decay only ~5x end to end, so there is no converged tail to skip.
    public static long SumH, SumC, SumR, SumIters;   // dimension totals over profiled calls

    // Capture hook for Diagnostics/QpBench (bench-only, never armed by the sim): while
    // armed, each FOLD subproblem is deep-copied out so it can be replayed under one clock
    // pair. Write-only from the sim's point of view — nothing in a step reads it back.
    public static bool ArmCapture;
    public static CorrectionProblem Captured;

    private static bool Active(in ChannelDef ch, int k)
        => ch.ActiveMask != null ? ch.ActiveMask[k] : k >= ch.ActiveFrom && k < ch.ActiveTo;

    private static float CapAt(in ChannelDef ch, int k)
        => ch.CapPerTick != null ? ch.CapPerTick[k] : ch.Cap;

    private static bool Skips(in ChannelDef ch, in ClearanceRow row)
        => (row.PlantOnly && !ch.PlantServes)
           || (ch.SkipSoftHorizontal && row.HingeScale < 1f && MathF.Abs(row.Normal.X) > 0.7f);

    // Solves the frozen subproblem into z (layout: z[c*H + k]); zScratch is a
    // same-size buffer for the synchronous gradient step. Returns the linear-model
    // residual: max over rows of the remaining violation (0 = all rows cleared).
    public static float Solve(CorrectionProblem p, Vector2[] z, Vector2[] zScratch)
    {
        if (ArmCapture && p.InnerIterations > DefaultInnerIterations) Captured = p.Clone();
        if (Profile) { Calls++; long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                       float r = SolveCore(p, z, zScratch);
                       Ticks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; return r; }
        return SolveCore(p, z, zScratch);
    }

    private static float SolveCore(CorrectionProblem p, Vector2[] z, Vector2[] zScratch)
    {
        int H = p.H, C = p.ChannelCount, R = p.RowCount;
        if (Profile) { SumH += H; SumC += C; SumR += R; SumIters += p.InnerIterations; }

        // Cold start at z = 0 every tick — the coast is the origin.
        for (int i = 0; i < C * H; i++) z[i] = Vector2.Zero;

        if (p.RowPush != null)
            for (int j = 0; j < R; j++) p.RowPush[j] = Vector2.Zero;

        // ---- Iteration-invariant precompute -------------------------------
        // Everything in this block depends only on the FROZEN subproblem
        // (channels, rows, dt, H) and not on the iterate z, but it used to be
        // rebuilt inside every one of the InnerIterations sweeps — 16 of them on
        // the fold path (MovementConfig.FoldIterations), which is the path that
        // fires on nearly every tick in tight terrain. Hoisting is
        // arithmetically identical: same expressions, same accumulation order,
        // so the solve is bit-for-bit what it was.

        // Lever table. Lever(kind, T, k, dt) depends on the kind and the TICK
        // GAP T − k alone, so 2·H entries cover every (row, tick) pair and
        // replace an O(C·H·R) recompute per sweep. PositionOffset is the gap-0
        // indicator and needs no table.
        Span<float> levV = stackalloc float[BallisticPredictor.MaxHorizon];
        Span<float> levF = stackalloc float[BallisticPredictor.MaxHorizon];
        for (int d = 0; d < BallisticPredictor.MaxHorizon; d++) { levV[d] = (d + 1) * p.Dt; levF[d] = levV[d] * p.Dt; }

        // Row-class compatibility per (channel, row) as a bitmask — R is capped
        // at ClearanceConstraintBuilder.MaxEvents = 32, so one uint per channel.
        // Bit set = this channel takes no hinge gradient from that row.
        Span<uint> skip = stackalloc uint[MaxChannels];
        for (int c = 0; c < C; c++)
        {
            uint m = 0;
            for (int j = 0; j < R; j++) if (Skips(p.Channels[c], p.Rows[j])) m |= 1u << j;
            skip[c] = m;
        }

        // Activation and lever kind flattened out of ChannelDef. The sweeps used to
        // re-read the (large, array-carrying) struct and dereference ActiveMask on every
        // (row, channel, tick) triple; H is capped at BallisticPredictor.MaxHorizon = 48,
        // so one ulong per channel holds the whole activation predicate as a bit test.
        Span<ulong> act  = stackalloc ulong[MaxChannels];
        Span<int>   kind = stackalloc int[MaxChannels];
        for (int c = 0; c < C; c++)
        {
            ulong m = 0;
            for (int k = 0; k < H; k++) if (Active(p.Channels[c], k)) m |= 1ul << k;
            act[c] = m;
            kind[c] = (int)p.Channels[c].Lever;
        }

        // Row constants folded to the same grouping the gradient uses:
        // ((2*wH*hs) * slack) * lever * n̂ is left-associative exactly as before.
        Span<float> hs2 = stackalloc float[ClearanceConstraintBuilder.MaxEvents];
        for (int j = 0; j < R; j++) hs2[j] = 2f * p.HingeWeight * p.Rows[j].HingeScale;

        // PER-VARIABLE step sizes eta_ck = 1/L_ck (diagonal preconditioning with
        // a Gershgorin row-sum bound). A single global step is set by the
        // stiffest variable — and the fold mixes VelocityUpdate levers (~dt)
        // with Force levers (~dt^2) and hard rows (HingeScale 1) with soft
        // references (0.02): the curvature disparity is ~10^3–10^4, so a shared
        // step starves the force channels and soft demands to numerical silence
        // under the fixed iteration budget. Per-variable: L_ck = 2w_c + 8wD
        // (delta-chain row sum) + sum_j 2*wH*hs_j*lever_ckj*S_j, where S_j is
        // row j's TOTAL compatible lever mass — the hinge Hessian's (c,k) row
        // sum, so the preconditioned gradient step is a descent step on the
        // quadratic surrogate. Compatibility (HardRowsOnly) shapes both gradient
        // and bound identically.
        // Axis-only channels enter row j's Hessian through |n̂_j · axis| — a
        // vertical row cannot move a horizontal channel — so the bound carries
        // that factor (exact row-sum entries; free 2D channels keep 1). Without
        // it, hard rows a channel cannot serve still shrank its step: measured
        // as the lattice tracker's Drive at 34 of a 3000 cap (2026-08-24).
        Span<float> rowS = stackalloc float[ClearanceConstraintBuilder.MaxEvents];
        for (int j = 0; j < R; j++)
        {
            float sum = 0f;
            int kMax = Math.Min(p.Rows[j].Tick, H - 1);
            for (int c = 0; c < C; c++)
            {
                if ((skip[c] & (1u << j)) != 0) continue;
                var ch = p.Channels[c];
                int kd = kind[c];
                float ax = AxisFactor(ch, p.Rows[j].Normal);
                for (int k = 0; k <= kMax; k++)
                {
                    if (!Active(ch, k)) continue;
                    sum += Lev(kd, p.Rows[j].Tick, k, levV, levF) * ax;
                }
            }
            rowS[j] = sum;
        }

        // Curvature bound over the SAME (row, k) pairs the gradient uses — the
        // Hessian row sum of the terms this variable actually feels. The bound
        // uses the full hinge set (not just currently-violated rows):
        // conservative when a hinge is inactive, never optimistic. Fixed for the
        // whole solve, so it is built once here rather than per sweep.
        Span<float> Lt = stackalloc float[MaxChannels * BallisticPredictor.MaxHorizon];
        for (int c = 0; c < C; c++)
        {
            var ch = p.Channels[c];
            uint sk = skip[c];
            int kd = kind[c];
            for (int k = 0; k < H; k++)
            {
                if (!Active(ch, k)) continue;
                float L = 2f * ch.Weight + 8f * p.DeltaWeight;
                for (int j = 0; j < R; j++)
                {
                    if (k > p.Rows[j].Tick) continue;
                    if ((sk & (1u << j)) != 0) continue;
                    float lever = Lev(kd, p.Rows[j].Tick, k, levV, levF) * AxisFactor(ch, p.Rows[j].Normal);
                    L += 2f * p.HingeWeight * p.Rows[j].HingeScale * lever * rowS[j];
                }
                Lt[c * H + k] = L;
            }
        }
        // -------------------------------------------------------------------

        Span<float> slack = stackalloc float[ClearanceConstraintBuilder.MaxEvents];
        for (int it = 0; it < p.InnerIterations; it++)
        {
            // Row slacks s_j = m_j − Σ lever·(z·n̂) from the CURRENT iterate.
            // Inlined RowSlack so it can use the lever table; same order of
            // accumulation as the shared helper, which stays the API for
            // callers outside the solve.
            for (int j = 0; j < R; j++)
            {
                var row = p.Rows[j];
                int kMax = Math.Min(row.Tick, H - 1);
                float achieved = 0f;
                for (int c = 0; c < C; c++)
                {
                    ulong a = act[c];
                    int kd = kind[c];
                    for (int k = 0; k <= kMax; k++)
                    {
                        if ((a >> k & 1) == 0) continue;
                        achieved += Lev(kd, row.Tick, k, levV, levF)
                                  * Vector2.Dot(z[c * H + k], row.Normal);
                    }
                }
                slack[j] = row.Depth - achieved;
            }

            // Synchronous gradient step into zScratch, then project per channel.
            for (int c = 0; c < C; c++)
            {
                var ch = p.Channels[c];
                uint sk = skip[c];
                int kd = kind[c];
                ulong a = act[c];
                for (int k = 0; k < H; k++)
                {
                    int i = c * H + k;
                    if ((a >> k & 1) == 0) { zScratch[i] = Vector2.Zero; continue; }

                    var g = 2f * ch.Weight * z[i];
                    float L = Lt[i];

                    if (p.DeltaWeight > 0f)
                    {
                        var prev = k == ch.ActiveFrom ? p.PrevApplied[c] : z[i - 1];
                        g += 2f * p.DeltaWeight * (z[i] - prev);
                        if (k + 1 < ch.ActiveTo)
                            g -= 2f * p.DeltaWeight * (z[i + 1] - z[i]);
                    }

                    if (L <= 0f) { zScratch[i] = z[i]; continue; }

                    for (int j = 0; j < R; j++)
                    {
                        if (slack[j] <= 0f) continue;
                        var row = p.Rows[j];
                        if (k > row.Tick) continue;
                        if ((sk & (1u << j)) != 0) continue;
                        float lever = Lev(kd, row.Tick, k, levV, levF);
                        var push = hs2[j] * slack[j] * lever * row.Normal;
                        g -= push;
                        if (p.RowPush != null && k == 0)
                            p.RowPush[j] += (ch.Lever switch
                            {
                                LeverKind.Force => p.Dt,            // accel → δv
                                LeverKind.PositionOffset => 1f / p.Dt,   // px → δv
                                _ => 1f,
                            }) * push / L;
                    }

                    zScratch[i] = Project(ch, k, p.CoastVel[k], z[i] - g / L, p.Dt);

                    // Slew clamp (PositionOffset dynamics honesty): pull the
                    // offset into reach of its predecessor. Mid-chain uses the
                    // PREVIOUS iterate's neighbor (synchronous sweep) so pairs
                    // of new values can transiently exceed the bound by one
                    // sweep's movement; the seam anchor (0) is exact.
                    if (ch.SlewCap > 0f)
                    {
                        float slew = ch.SlewCap * p.Dt;
                        var anchor = k == ch.ActiveFrom ? Vector2.Zero : z[i - 1];
                        var dd = zScratch[i] - anchor;
                        float dl = dd.Length();
                        if (dl > slew) zScratch[i] = anchor + dd * (slew / dl);
                    }
                }
            }

            // Swap-free commit: copy the projected step back (arrays are tiny).
            for (int i = 0; i < C * H; i++) z[i] = zScratch[i];
        }

        return ComputeResidual(p, z);
    }

    // Exact coordinate sweeps over ticks [0, ticks) — at least the tick the
    // caller applies — after the gradient sweeps. The projected-gradient step bound is a Gershgorin row
    // sum over every hard row a variable touches, inactive ones included, so
    // in a handful of sweeps a channel that a row simply asks for at its cap
    // delivers a few percent of it (from rest, the drive gave 179 of 3000
    // px/s² in 16 sweeps; the converged optimum is the cap, 1024 sweeps).
    // With the other ticks held, each axis channel's objective in its one
    // scalar λ is a convex piecewise quadratic — Σ w_j·max(0, b_j − a_j λ)²
    // over the rows it serves, plus Weight·λ² — whose derivative is monotone,
    // so its minimizer on [lo, cap] is found exactly by bracketing. Sweeping
    // the whole horizon (ticks = H) converges to the optimum's tick 0; tick 0
    // alone makes it do the later ticks' work (from rest the legs fired at
    // 2471 px/s² against the optimum's 1165 — a visible hop on every walk
    // start). Free 2D channels keep the gradient sweeps' value. No
    // Δ-smoothing term: callers with DeltaWeight > 0 are not served (the
    // lattice tracker runs with 0).
    public static void ExactSweeps(CorrectionProblem p, Vector2[] z, int sweeps, int ticks)
    {
        if (p.DeltaWeight > 0f || p.H <= 0) return;
        int H = p.H, C = p.ChannelCount, R = p.RowCount;
        ticks = Math.Min(ticks, H);
        Span<float> a = stackalloc float[ClearanceConstraintBuilder.MaxEvents];
        Span<float> b = stackalloc float[ClearanceConstraintBuilder.MaxEvents];
        Span<float> w = stackalloc float[ClearanceConstraintBuilder.MaxEvents];
        Span<int>   rj = stackalloc int[ClearanceConstraintBuilder.MaxEvents];
        // Row slacks, maintained incrementally: a coordinate's move Δλ shifts
        // every row it physically touches by −coef·Δλ (skipped rows too —
        // Skips only withholds a row's gradient, not the channel's effect).
        Span<float> slack = stackalloc float[ClearanceConstraintBuilder.MaxEvents];
        for (int j = 0; j < R; j++) slack[j] = RowSlack(p, z, j);
        for (int sw = 0; sw < sweeps; sw++)
        for (int k = 0; k < ticks; k++)
        for (int c = 0; c < C; c++)
        {
            var ch = p.Channels[c];
            if (!ch.AxisOnly || ch.Lever == LeverKind.PositionOffset || !Active(ch, k)) continue;
            int i = c * H + k;
            float cap = CapAt(ch, k);
            // The rise ceiling is part of the admissible set, not a
            // post-projection touch-up: this sweep REPLACES whatever Project
            // produced for an axis channel, so a bound only enforced there
            // would be silently undone (the leg servo kept pushing a jump past
            // MaxAssistRiseSpeed exactly this way).
            cap = MathF.Min(cap, RiseHeadroom(ch, p.CoastVel[k], p.Dt));
            if (cap <= 0f) { z[i] = Vector2.Zero; continue; }
            float lo = ch.Unilateral ? 0f : -cap, hi = cap;
            float lamOld = Vector2.Dot(z[i], ch.Axis);
            // slack_j(λ) = b_j − a_j·λ for the rows this channel serves at tick k
            int n = 0;
            for (int j = 0; j < R; j++)
            {
                var row = p.Rows[j];
                if (row.Tick < k) continue;
                float coef = Lever(ch.Lever, row.Tick, k, p.Dt) * Vector2.Dot(ch.Axis, row.Normal);
                if (coef == 0f) continue;
                a[n] = coef; b[n] = slack[j] + coef * lamOld; rj[n] = j;
                w[n] = Skips(ch, row) ? 0f : p.HingeWeight * row.HingeScale;   // touched, but no gradient
                n++;
            }
            float lam;
            if (Deriv(lo, ch.Weight, a, b, w, n) >= 0f) lam = lo;
            else if (Deriv(hi, ch.Weight, a, b, w, n) <= 0f) lam = hi;
            else
            {
                float l = lo, h = hi;
                for (int it = 0; it < 40; it++)
                {
                    float m = 0.5f * (l + h);
                    if (Deriv(m, ch.Weight, a, b, w, n) > 0f) h = m; else l = m;
                }
                lam = 0.5f * (l + h);
            }
            z[i] = lam * ch.Axis;
            for (int q = 0; q < n; q++) slack[rj[q]] = b[q] - a[q] * lam;
        }
    }

    // d/dλ of Σ w_j·max(0, b_j − a_j λ)² + weight·λ² — monotone increasing.
    private static float Deriv(float lam, float weight, ReadOnlySpan<float> a, ReadOnlySpan<float> b,
                               ReadOnlySpan<float> w, int n)
    {
        float d = 2f * weight * lam;
        for (int q = 0; q < n; q++)
        {
            float s = b[q] - a[q] * lam;
            if (s > 0f) d -= 2f * w[q] * a[q] * s;
        }
        return d;
    }

    // |n̂ · axis| for axis-only channels (the row's true coupling to the
    // variable); 1 for free 2D channels (Redirect).
    private static float AxisFactor(in ChannelDef ch, Vector2 normal)
        => ch.AxisOnly ? MathF.Abs(Vector2.Dot(ch.Axis, normal)) : 1f;

    // Table-backed Lever for the solve's hot loops. Identical arithmetic to
    // Lever(): (T − k + 1)·dt is precomputed per tick gap instead of per
    // (channel, tick, row) per sweep.
    private static float Lev(int kind, int rowTick, int k,
                             ReadOnlySpan<float> levV, ReadOnlySpan<float> levF)
        => kind switch
        {
            (int)LeverKind.PositionOffset => k == rowTick ? 1f : 0f,
            (int)LeverKind.VelocityUpdate => levV[rowTick - k],
            _                        => levF[rowTick - k],
        };

    // Remaining violation of row j under the linear dynamics map.
    public static float RowSlack(CorrectionProblem p, Vector2[] z, int j)
    {
        var row = p.Rows[j];
        float achieved = 0f;
        for (int c = 0; c < p.ChannelCount; c++)
        {
            var ch = p.Channels[c];
            int kMax = Math.Min(row.Tick, p.H - 1);
            for (int k = 0; k <= kMax; k++)
            {
                if (!Active(ch, k)) continue;
                achieved += Lever(ch.Lever, row.Tick, k, p.Dt)
                          * Vector2.Dot(z[c * p.H + k], row.Normal);
            }
        }
        return row.Depth - achieved;
    }

    // Refusal residual counts HARD rows only (HingeScale ≥ 1): soft support rows
    // are intentionally violated during ducks and must not trip the gate.
    public static float ComputeResidual(CorrectionProblem p, Vector2[] z)
    {
        float r = 0f;
        for (int j = 0; j < p.RowCount; j++)
        {
            if (p.Rows[j].HingeScale < 1f) continue;
            r = MathF.Max(r, MathF.Max(0f, RowSlack(p, z, j)));
        }
        return r;
    }

    private static float Lever(LeverKind kind, int rowTick, int k, float dt)
    {
        if (kind == LeverKind.PositionOffset) return k == rowTick ? 1f : 0f;
        float steps = (rowTick - k + 1) * dt;
        return kind == LeverKind.VelocityUpdate ? steps : steps * dt;
    }

    // Closed-form projection onto the channel's admissible set.
    private static Vector2 Project(in ChannelDef ch, int k, Vector2 coastVel, Vector2 v, float dt)
    {
        if (ch.Redirect)
        {
            // The disc may be parametrized as a VelocityUpdate (z = Δv) or as
            // a Force (z = Δv/dt): the same admissible set, projected in Δv
            // space. The force form keeps the channel's lever commensurate
            // with the force channels' — a Δv lever is 1/dt (60×) larger and
            // inflates every variable's Gershgorin step bound wherever the
            // disc is active (the lattice tracker, where it is active on all
            // near ticks, saw the legs and drive starve to ~1% of their caps).
            bool asForce = ch.Lever == LeverKind.Force;
            if (asForce) v *= dt;
            // Thales disc: ‖z + v̂/2‖ ≤ ‖v̂‖/2 — encodes speed-never-added,
            // positive-dot-product, ≤90°/tick, all for free.
            var center = -coastVel * 0.5f;
            float radius = coastVel.Length() * 0.5f;
            var d = v - center;
            float len = d.Length();
            var z = len <= radius ? v : (len > 0f ? center + d * (radius / len) : center);
            // Forward bound: clamp only engages when v′·axis exceeds both the
            // coast forward speed and the cap, so it moves v′ toward the disc
            // center's forward component — the result stays inside the disc
            // (still speed-non-increasing), just no longer forward-growing.
            if (ch.ForwardCap > 0f)
            {
                float fwd = Vector2.Dot(coastVel + z, ch.ForwardAxis);
                float bound = MathF.Max(Vector2.Dot(coastVel, ch.ForwardAxis), ch.ForwardCap);
                if (fwd > bound) z -= (fwd - bound) * ch.ForwardAxis;
            }
            z = ClampRise(ch, z, coastVel, dt: 1f);   // already in Δv space here
            // Bounded plant: ‖z‖ ≤ Cap·dt. The disc is convex and contains 0,
            // so shrinking z toward 0 keeps it admissible — the clipped point
            // is in both sets (not the nearest point of the intersection,
            // which is fine for the projected iteration).
            if (ch.Cap > 0f)
            {
                float zc = ch.Cap * dt, zl = z.Length();
                if (zl > zc) z *= zc / zl;
            }
            return asForce ? z / dt : z;
        }

        float cap = CapAt(ch, k);
        if (ch.AxisOnly)
        {
            float lam = Vector2.Dot(v, ch.Axis);
            lam = ch.Unilateral ? Math.Clamp(lam, 0f, cap) : Math.Clamp(lam, -cap, cap);
            return ClampRise(ch, lam * ch.Axis, coastVel, dt);
        }

        float vlen = v.Length();
        if (vlen > cap) v = vlen > 0f ? v * (cap / vlen) : v;
        return ClampRise(ch, v, coastVel, dt);
    }

    // ChannelDef.RiseCap: trim whatever part of z would leave the body rising
    // past max(coast rise, cap). Applied in Δv space so a force lever and a
    // velocity lever obey one rule — a lift that would push past the ceiling
    // is shortened, a deflection that would rotate speed up past it is rotated
    // back down. Like the forward bound it is an approximate projection (it
    // can leave the disc's boundary); the projected iteration tolerates that.
    // The largest λ an UPWARD-pointing axis channel may spend at this tick
    // before the body would rise past max(coast rise, RiseCap) — the cap form
    // of ClampRise, for the exact coordinate sweep (which sets λ directly and
    // so must see the ceiling as part of its box, not as a later projection).
    // A channel with no upward component is unbounded by it.
    private static float RiseHeadroom(in ChannelDef ch, Vector2 coastVel, float dt)
    {
        if (ch.RiseCap <= 0f) return float.MaxValue;
        float up = -ch.Axis.Y;                      // y-down: up is −y
        if (up <= 0f) return float.MaxValue;
        float scale = ch.Lever == LeverKind.Force ? dt : 1f;
        if (scale <= 0f) return float.MaxValue;
        float coastRise = -coastVel.Y;
        float bound = MathF.Max(coastRise, ch.RiseCap);
        return MathF.Max(0f, (bound - coastRise) / (scale * up));
    }

    private static Vector2 ClampRise(in ChannelDef ch, Vector2 z, Vector2 coastVel, float dt)
    {
        if (ch.RiseCap <= 0f) return z;
        float scale = ch.Lever == LeverKind.Force ? dt : 1f;   // z → Δv
        float rise  = -(coastVel.Y + z.Y * scale);             // y-down: up is −y
        float bound = MathF.Max(-coastVel.Y, ch.RiseCap);
        if (rise > bound && scale > 0f) z.Y += (rise - bound) / scale;
        return z;
    }
}
