using System;
using Microsoft.Xna.Framework;

namespace MTile;

// How a channel's per-tick variable enters the dynamics (BALLISTIC_CORRECTOR_PLAN §4).
// VelocityUpdate: z is a velocity delta applied before tick k's position update —
//   it moves position at row tick T by (T − k + 1)·dt along z.
// Force: z is an acceleration held for tick k — Δv = z·dt, moving position at
//   row tick T by (T − k + 1)·dt².
public enum LeverKind { VelocityUpdate, Force }

// A solver channel: per-tick convex admissible set with a closed-form projection,
// a lever kind, and a quadratic cost weight. The solver is one loop over channels
// and knows no physics — anything not expressible as this triple is not a channel.
public struct ChannelDef
{
    public LeverKind Lever;
    public float     Weight;       // quadratic cost on ‖z_k‖² (Redirect: the ε regularizer)
    public float     Cap;          // Force: ‖z_k‖ ≤ Cap. Ignored for Redirect.
    public bool      Redirect;     // admissible set = Thales disc of the coast velocity v̂ₖ
                                   // (v′·(v̂ₖ−v′) ≥ 0 ⇔ ‖z + v̂ₖ/2‖ ≤ ‖v̂ₖ‖/2): the exact
                                   // reachable set of composed frictionless projections.
    public int       ActiveFrom;   // ticks [ActiveFrom, ActiveTo) may act; z = 0 outside
    public int       ActiveTo;
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
}

public static class CorrectionSolver
{
    public const int DefaultInnerIterations = 4;

    // Solves the frozen subproblem into z (layout: z[c*H + k]); zScratch is a
    // same-size buffer for the synchronous gradient step. Returns the linear-model
    // residual: max over rows of the remaining violation (0 = all rows cleared).
    public static float Solve(CorrectionProblem p, Vector2[] z, Vector2[] zScratch)
    {
        int H = p.H, C = p.ChannelCount;

        // Cold start at z = 0 every tick — the coast is the origin.
        for (int i = 0; i < C * H; i++) z[i] = Vector2.Zero;

        // Fixed step η = 1/L, L a Gershgorin-style Lipschitz bound of the
        // quadratic+hinge surrogate: per-variable weight curvature + Δ-chain
        // curvature + hinge curvature 2wH‖a_j‖² summed over rows.
        float maxW = 0f;
        for (int c = 0; c < C; c++) maxW = MathF.Max(maxW, p.Channels[c].Weight);
        float hingeCurv = 0f;
        for (int j = 0; j < p.RowCount; j++)
        {
            float leverSq = 0f;
            for (int c = 0; c < C; c++)
            {
                var ch = p.Channels[c];
                int kMax = Math.Min(p.Rows[j].Tick, ch.ActiveTo - 1);
                for (int k = ch.ActiveFrom; k <= kMax; k++)
                {
                    float lever = Lever(ch.Lever, p.Rows[j].Tick, k, p.Dt);
                    leverSq += lever * lever;
                }
            }
            hingeCurv += 2f * p.HingeWeight * leverSq;
        }
        float L = 2f * maxW + 8f * p.DeltaWeight + hingeCurv;
        if (L <= 0f) return ComputeResidual(p, z);
        float eta = 1f / L;

        for (int it = 0; it < p.InnerIterations; it++)
        {
            // Row slacks s_j = m_j − Σ lever·(z·n̂) from the CURRENT iterate.
            Span<float> slack = stackalloc float[ClearanceConstraintBuilder.MaxEvents];
            for (int j = 0; j < p.RowCount; j++)
                slack[j] = RowSlack(p, z, j);

            // Synchronous gradient step into zScratch, then project per channel.
            for (int c = 0; c < C; c++)
            {
                var ch = p.Channels[c];
                for (int k = 0; k < H; k++)
                {
                    int i = c * H + k;
                    if (k < ch.ActiveFrom || k >= ch.ActiveTo) { zScratch[i] = Vector2.Zero; continue; }

                    var g = 2f * ch.Weight * z[i];

                    if (p.DeltaWeight > 0f)
                    {
                        var prev = k == ch.ActiveFrom ? p.PrevApplied[c] : z[i - 1];
                        g += 2f * p.DeltaWeight * (z[i] - prev);
                        if (k + 1 < ch.ActiveTo)
                            g -= 2f * p.DeltaWeight * (z[i + 1] - z[i]);
                    }

                    for (int j = 0; j < p.RowCount; j++)
                    {
                        if (slack[j] <= 0f || k > p.Rows[j].Tick) continue;
                        float lever = Lever(ch.Lever, p.Rows[j].Tick, k, p.Dt);
                        g -= 2f * p.HingeWeight * slack[j] * lever * p.Rows[j].Normal;
                    }

                    zScratch[i] = Project(ch, p.CoastVel[k], z[i] - eta * g);
                }
            }

            // Swap-free commit: copy the projected step back (arrays are tiny).
            for (int i = 0; i < C * H; i++) z[i] = zScratch[i];
        }

        return ComputeResidual(p, z);
    }

    // Remaining violation of row j under the linear dynamics map.
    public static float RowSlack(CorrectionProblem p, Vector2[] z, int j)
    {
        var row = p.Rows[j];
        float achieved = 0f;
        for (int c = 0; c < p.ChannelCount; c++)
        {
            var ch = p.Channels[c];
            int kMax = Math.Min(row.Tick, ch.ActiveTo - 1);
            for (int k = ch.ActiveFrom; k <= kMax; k++)
                achieved += Lever(ch.Lever, row.Tick, k, p.Dt)
                          * Vector2.Dot(z[c * p.H + k], row.Normal);
        }
        return row.Depth - achieved;
    }

    public static float ComputeResidual(CorrectionProblem p, Vector2[] z)
    {
        float r = 0f;
        for (int j = 0; j < p.RowCount; j++)
            r = MathF.Max(r, MathF.Max(0f, RowSlack(p, z, j)));
        return r;
    }

    private static float Lever(LeverKind kind, int rowTick, int k, float dt)
    {
        float steps = (rowTick - k + 1) * dt;
        return kind == LeverKind.VelocityUpdate ? steps : steps * dt;
    }

    // Closed-form projection onto the channel's admissible set.
    private static Vector2 Project(in ChannelDef ch, Vector2 coastVel, Vector2 v)
    {
        if (ch.Redirect)
        {
            // Thales disc: ‖z + v̂/2‖ ≤ ‖v̂‖/2 — encodes speed-never-added,
            // positive-dot-product, ≤90°/tick, all for free.
            var center = -coastVel * 0.5f;
            float radius = coastVel.Length() * 0.5f;
            var d = v - center;
            float len = d.Length();
            if (len <= radius) return v;
            return len > 0f ? center + d * (radius / len) : center;
        }
        else
        {
            float len = v.Length();
            if (len <= ch.Cap) return v;
            return len > 0f ? v * (ch.Cap / len) : v;
        }
    }
}
