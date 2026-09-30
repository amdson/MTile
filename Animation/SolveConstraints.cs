using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using static MTile.SolveProblem;

namespace MTile;

// The constraint library (Plans/ANIMATION_SOLVER_PLAN §11; workplan chunk 1.5): the blocks the
// composite objective is assembled from, each a stateless pair of pure functions of the frozen
// SolveProblem and the forward pass's PoseEval — residual rows and their hand-derived analytic
// Jacobian, machine-checked against finite differences by CharacterAnimator.MaxJacobianError
// (per block). Nothing here reads animator state; everything a row needs is in `p` or `e`.
// The solve ORCHESTRATION (freezing the problem, the LM call, the phase seed search) stays in
// CharacterAnimator.cs; the forward pass and the point-Jacobian primitive are in SolveObjective.cs.

// Two rows per planted contact: √w·(tipX + d.x − targetX) horizontal no-slip (the cadence
// pin, drives Δφ, with d.x as the escape at the foot's horizontal turning point) then
// √w·(tipY + δ − targetY) vertical ground hold (drives δ, body bobs). The
// tips are read from the FINAL composed, Δθ-corrected pose (the design invariant — see
// CharacterAnimator's _scratch declaration), so the Jacobian is the full point primitive scaled
// by √w, plus √w on the V row's δ column. Δθ CAN trade against contact slip here (a small
// stance-leg trim that plants the drawn foot exactly is a feature); the weights keep it minimal.
// The weight is FROZEN per solve (c.Weight, from RefreshContacts) — deliberately NOT the
// live feathered w(φ+Δφ): a Δφ-dependent weight lets the solver DELETE its own constraint
// by advancing into a no-contact window (a run free-ran at constant Δφ with zero grip when
// this was tried). Release-under-stall is handled time-side in RefreshContacts instead.
public sealed class PlantedContactsConstraint : ISolveConstraint
{
    public string Name => "PlantedContactsConstraint";

    public int Residuals(SolveProblem p, PoseEval e, ReadOnlySpan<float> x, Span<float> r)
    {
        float dy = x[IdxDy], dx = x[IdxDx];
        int n = 0;
        foreach (var c in p.Contacts)
        {
            Vector2 tip = e.Pose.WorldOf(c.Bone).Translation;   // bone's far end = contact tip
            float sw = MathF.Sqrt(p.Cfg.TierContact * c.Weight) * p.InvCharLen;
            r[n++] = sw * (tip.X + dx - c.Target.X);     // horizontal no-slip (drives Δφ + d.x sway)
            r[n++] = sw * (tip.Y + dy - c.Target.Y);     // vertical ground hold (drives δ)
        }
        return n;
    }

    public int Jacobian(SolveProblem p, PoseEval e, ReadOnlySpan<float> x, Span<float> jac, int stride, int row0)
    {
        int nv = p.Vars;
        var colX = e.ColX.AsSpan(0, nv);
        var colY = e.ColY.AsSpan(0, nv);
        int row = row0;
        foreach (var c in p.Contacts)
        {
            Vector2 tip = e.Pose.WorldOf(c.Bone).Translation;
            float sw = MathF.Sqrt(p.Cfg.TierContact * c.Weight) * p.InvCharLen;
            SolveObjective.PointJacobianColumns(p, e, c.Bone, tip, colX, colY);
            int hRow = row, vRow = row + 1;
            for (int v = 0; v < nv; v++)
            {
                jac[hRow * stride + v] = sw * colX[v];   // ∂H/∂x_v from the x component
                jac[vRow * stride + v] = sw * colY[v];   // ∂V/∂x_v from the y component
            }
            jac[hRow * stride + IdxDx] += sw;            // ∂H/∂d.x (colX[IdxDx]==0, so this is √w)
            jac[vRow * stride + IdxDy] += sw;            // ∂V/∂δ (colY[IdxDy]==0, so this is √w)
            row += 2;
        }
        return row - row0;
    }
}

// Two rows per SWINGING planner-owned foot (step planner P3): √(TierContact·SwingShare)
// · (tip − swingTarget), both axes. A soft "follow the authored swing toward the planned
// landing" pull, evaluated at the rendered point including the root correction. The lower
// weight and root priors limit its influence; omitting the root here fits the wrong point.
// No SkipPair collision exemption and no no-slip cadence semantics.
// Δθ bends the leg along the path; the Δφ column (via the point primitive) gives the
// bounded timing correction the plan allows, boxed by the momentum prior as usual.
// Targets are frozen per solve (p.Swings — StepPlanner runs once, before the solve).
public sealed class SwingTargetConstraint : ISolveConstraint
{
    private const float SwingShare = 0.35f;   // structural: well under a planted contact's 1.0
    public string Name => "SwingTargetConstraint";

    public int Residuals(SolveProblem p, PoseEval e, ReadOnlySpan<float> x, Span<float> r)
    {
        int n = 0;
        foreach (var (bone, target) in p.Swings)
        {
            Vector2 tip = e.Pose.WorldOf(bone).Translation;
            float sw = MathF.Sqrt(p.Cfg.TierContact * SwingShare) * p.InvCharLen;
            r[n++] = sw * (tip.X + x[IdxDx] - target.X);
            r[n++] = sw * (tip.Y + x[IdxDy] - target.Y);
        }
        return n;
    }

    public int Jacobian(SolveProblem p, PoseEval e, ReadOnlySpan<float> x, Span<float> jac, int stride, int row0)
    {
        int nv = p.Vars;
        var colX = e.ColX.AsSpan(0, nv);
        var colY = e.ColY.AsSpan(0, nv);
        int row = row0;
        foreach (var (bone, _) in p.Swings)
        {
            Vector2 tip = e.Pose.WorldOf(bone).Translation;
            float sw = MathF.Sqrt(p.Cfg.TierContact * SwingShare) * p.InvCharLen;
            SolveObjective.PointJacobianColumns(p, e, bone, tip, colX, colY);
            for (int v = 0; v < nv; v++)
            {
                jac[row * stride + v]       = sw * colX[v];
                jac[(row + 1) * stride + v] = sw * colY[v];
            }
            jac[row * stride + IdxDx] += sw;
            jac[(row + 1) * stride + IdxDy] += sw;
            row += 2;
        }
        return row - row0;
    }
}

// Two rows per external pin: √TierHard·(tipX − targetX) and √TierHard·(tipY + δ − targetY) —
// a both-axis HARD pin holding a bone's far tip at a fixed world point. This is the first
// constraint that genuinely drives Δθ (IK): the arm/leg bends so the pinned tip reaches the
// target. The Y row rides the body bob δ (the tip moves with the rig), same as a contact's V
// row. Structurally a contact at the hard tier with an EXTERNAL (fixed) target. §11.5/§4.3.
public sealed class FixedPointConstraint : ISolveConstraint
{
    public string Name => "FixedPointConstraint";

    public int Residuals(SolveProblem p, PoseEval e, ReadOnlySpan<float> x, Span<float> r)
    {
        float dy = x[IdxDy], dx = x[IdxDx];
        float sw = MathF.Sqrt(p.Cfg.TierHard) * p.InvCharLen;
        int n = 0;
        foreach (var (bone, target) in p.Pins)
        {
            Vector2 tip = e.Pose.WorldOf(bone).Translation;
            r[n++] = sw * (tip.X + dx - target.X);       // pin X (rides the body sway d.x)
            r[n++] = sw * (tip.Y + dy - target.Y);       // pin Y (rides the body bob δ)
        }
        return n;
    }

    public int Jacobian(SolveProblem p, PoseEval e, ReadOnlySpan<float> x, Span<float> jac, int stride, int row0)
    {
        int nv = p.Vars;
        var colX = e.ColX.AsSpan(0, nv);
        var colY = e.ColY.AsSpan(0, nv);
        float sw = MathF.Sqrt(p.Cfg.TierHard) * p.InvCharLen;
        int row = row0;
        foreach (var (bone, _) in p.Pins)
        {
            Vector2 tip = e.Pose.WorldOf(bone).Translation;
            SolveObjective.PointJacobianColumns(p, e, bone, tip, colX, colY);
            int xRow = row, yRow = row + 1;
            for (int v = 0; v < nv; v++)
            {
                jac[xRow * stride + v] = sw * colX[v];   // ∂(pinX)/∂x_v
                jac[yRow * stride + v] = sw * colY[v];   // ∂(pinY)/∂x_v
            }
            jac[xRow * stride + IdxDx] += sw;            // ∂(pinX)/∂d.x
            jac[yRow * stride + IdxDy] += sw;            // ∂(pinY)/∂δ
            row += 2;
        }
        return row - row0;
    }
}

// One row per rig bone: a UNIFIED no-penetration potential over every face masked to that
// bone, pushing its far tip q back out of the solid toward the body.
//
// The face list is pre-filtered by the host to faces that FACE THE BODY (TerrainSurfaces:
// the physics body's centre lies on the face's free side), so every face here is a way
// back to where the body is. Per face s (segment, span [−H, H] along the tangent):
//   d_s    = depth of q BEHIND the face's line (margin-shifted); only d_s > 0 faces take part
//   dist_s = distance from q to the face SEGMENT — d_s inside the span, the distance to the
//            nearer end past it (so a tip under a convex corner exits diagonally, at the corner)
// q is INVALID when it is behind some face within its span (extended past concave ends). That
// covers a tip inside a block AND a tip poked clean through a thin wall (the far face faces
// away and was dropped, so the tip is still behind the near one: it is pulled back through
// the side it went in, never shoved out the far side).
//   P = −τ·ln Σ w_s·exp(−dist_s/τ)   a soft MIN over the exits: the shallowest body-facing
// exit dominates, near-tied exits (a corner) blend instead of flipping. w_s = 1 in span; past
// a span end it ramps in with d_s (smoothstep over MemberEps) so a face joining the set never
// jumps P. The residual is √w·shape(P): P up to NoPenAllowance, then NoPenSteepGain× steeper.
// P → 0 as q reaches a face, so the invalid→valid switch is continuous.
// INACTIVE rows emit 0 residual AND 0 Jacobian, so the row COUNT is fixed at one per bone
// (the LM fixed-row contract). The Jacobian holds each face's normal/end fixed — exact away
// from the kinks (activation, the allowance knee, span ends), where the FD oracle is mute.
// The Y component rides the body bob δ and X the sway d.x, same as a contact/pin row.
public sealed class NoPenetrationConstraint : ISolveConstraint
{
    public string Name => "NoPenetrationConstraint";

    public const float Tau       = 1f;   // soft-min temperature (px): corner exits blend over ~τ
    public const float MemberEps = 1f;   // off-span faces ramp into the soft-min over this depth (px)

    // A (surface, bone) pair that never takes part. Two reasons:
    //  - BoneMask: terrain faces constrain only the tips they were extracted for
    //    (-1 = all bones, the wall-slide plane).
    //  - Planted-foot exemption: the cadence solve sweeps a PLANTED foot along its
    //    support face at gap ≈ 0 — the contact's V-row already owns "foot sits on
    //    ground", so its own upward support face must not flicker against it.
    private static bool SkipPair(List<ActiveContact> contacts, in SolverSurface s, int b)
    {
        if (((s.BoneMask >> b) & 1) == 0) return true;
        if (contacts != null && s.Normal.Y < -0.7f)   // upward-facing (y-down)
            foreach (var c in contacts)
                if (c.Bone == b &&
                    MathF.Abs(s.Normal.X * (c.Target.X - s.Point.X)
                            + s.Normal.Y * (c.Target.Y - s.Point.Y)) < ContactSupportBand)
                    return true;                      // this face supports the plant
        return false;
    }

    // The unified potential P at world point q for `bone`, and ∂P/∂q. Returns false (P = 0,
    // grad = 0) when q is valid. Shared with the animator's dormancy pre-check and the FD
    // oracle's kink guard so all three read one definition. `contacts` null = no exemption.
    public static bool Potential(List<SolverSurface> surfaces, List<ActiveContact> contacts,
                                 int bone, Vector2 q, out float pen, out Vector2 grad)
    {
        pen = 0f; grad = Vector2.Zero;
        bool invalid = false;
        float minDist = float.MaxValue;
        foreach (var s in surfaces)
        {
            if (SkipPair(contacts, in s, bone)) continue;
            if (!Measure(in s, q, out float d, out float lat, out float dist, out _)) continue;
            if (InSpan(in s, lat)) invalid = true;
            minDist = MathF.Min(minDist, dist);
        }
        if (!invalid) return false;

        // Soft-min, shifted by the hard min so exp never underflows.
        float sum = 0f; Vector2 dSum = Vector2.Zero;
        foreach (var s in surfaces)
        {
            if (SkipPair(contacts, in s, bone)) continue;
            if (!Measure(in s, q, out float d, out float lat, out float dist, out Vector2 dDist)) continue;
            float w = 1f, dw = 0f;                     // dw = ∂w/∂d
            if (!InSpan(in s, lat))
            {
                float u = MathF.Min(d / MemberEps, 1f);
                w  = u * u * (3f - 2f * u);
                dw = d < MemberEps ? 6f * u * (1f - u) / MemberEps : 0f;
            }
            if (w <= 0f) continue;
            float e = MathF.Exp(-(dist - minDist) / Tau);
            sum  += w * e;
            // ∂(w·e)/∂q = e·dw·∂d/∂q + w·e·(−1/τ)·∂dist/∂q, with ∂d/∂q = −n.
            dSum += e * dw * -s.Normal - (w * e / Tau) * dDist;
        }
        if (sum <= 0f) return false;
        pen  = minDist - Tau * MathF.Log(sum);
        grad = -Tau * dSum / sum;
        return true;
    }

    // Depth d behind face s's margin-shifted line (false when q is on the free side), the
    // tangent coordinate lat, and the distance to the segment with its gradient.
    private static bool Measure(in SolverSurface s, Vector2 q, out float d, out float lat,
                                out float dist, out Vector2 dDist)
    {
        Vector2 n = s.Normal, t = s.Tangent;
        Vector2 u = q - (s.Point + n * s.Margin);
        d   = -(n.X * u.X + n.Y * u.Y);
        lat = t.X * u.X + t.Y * u.Y;
        dist = 0f; dDist = Vector2.Zero;
        if (d <= 0f) return false;
        float tc = Math.Clamp(lat, -s.HalfLength, s.HalfLength);
        Vector2 v = u - t * tc;                        // closest segment point → q
        dist  = v.Length();
        dDist = dist > 1e-6f ? v / dist : -n;
        return true;
    }

    private static bool InSpan(in SolverSurface s, float lat)
        => lat >= -s.HalfLength - s.ExtLo && lat <= s.HalfLength + s.ExtHi;

    // The residual shape over P and its slope.
    private static float Shape(AnimSolverConfig c, float pen, out float slope)
    {
        if (pen <= 0f) { slope = 0f; return 0f; }
        float over = pen - c.NoPenAllowance;
        if (over <= 0f) { slope = 1f; return pen; }
        slope = c.NoPenSteepGain;
        return pen + (c.NoPenSteepGain - 1f) * over;
    }

    public int Residuals(SolveProblem p, PoseEval e, ReadOnlySpan<float> x, Span<float> r)
    {
        float dy = x[IdxDy], dx = x[IdxDx];
        float sw = MathF.Sqrt(p.Cfg.TierNoPen) * p.InvCharLen;
        int bones = p.Skeleton.Count;
        for (int b = 0; b < bones; b++)
        {
            r[b] = 0f;
            if (p.Surfaces.Count == 0) continue;
            Vector2 tip = e.Pose.WorldOf(b).Translation;
            if (Potential(p.Surfaces, p.Contacts, b, new Vector2(tip.X + dx, tip.Y + dy), out float pen, out _))
                r[b] = sw * Shape(p.Cfg, pen, out _);
        }
        return bones;
    }

    public int Jacobian(SolveProblem p, PoseEval e, ReadOnlySpan<float> x, Span<float> jac, int stride, int row0)
    {
        float dy = x[IdxDy], dx = x[IdxDx];
        float sw = MathF.Sqrt(p.Cfg.TierNoPen) * p.InvCharLen;
        int nv = p.Vars, bones = p.Skeleton.Count;
        var colX = e.ColX.AsSpan(0, nv);
        var colY = e.ColY.AsSpan(0, nv);
        for (int b = 0; b < bones; b++)
        {
            if (p.Surfaces.Count == 0) continue;
            Vector2 tip = e.Pose.WorldOf(b).Translation;
            if (!Potential(p.Surfaces, p.Contacts, b, new Vector2(tip.X + dx, tip.Y + dy), out float pen, out Vector2 g))
                continue;                                     // inactive → zero row (solver pre-zeroes)
            Shape(p.Cfg, pen, out float slope);
            if (slope == 0f) continue;
            float kx = sw * slope * g.X, ky = sw * slope * g.Y;
            SolveObjective.PointJacobianColumns(p, e, b, tip, colX, colY);   // ∂(world tip)/∂x (d added below)
            int row = row0 + b;
            for (int v = 0; v < nv; v++)
                jac[row * stride + v] = kx * colX[v] + ky * colY[v];
            jac[row * stride + IdxDx] += kx;                  // q.X rides d.x
            jac[row * stride + IdxDy] += ky;                  // q.Y rides δ
        }
        return bones;
    }

    // FD-oracle guard: is q within `band` px of a kink of the potential (activation, the
    // allowance knee, a face's line or span end)? A central difference straddling one is not
    // a valid oracle there.
    public static bool NearKink(SolveProblem p, int bone, Vector2 q, float band)
    {
        Potential(p.Surfaces, p.Contacts, bone, q, out float pen, out _);
        if (MathF.Abs(pen) < band || MathF.Abs(pen - p.Cfg.NoPenAllowance) < band) return true;
        foreach (var s in p.Surfaces)
        {
            if (SkipPair(p.Contacts, in s, bone)) continue;
            Vector2 u = q - (s.Point + s.Normal * s.Margin);
            float d = -(s.Normal.X * u.X + s.Normal.Y * u.Y);
            float lat = s.Tangent.X * u.X + s.Tangent.Y * u.Y;
            if (MathF.Abs(d) < band || MathF.Abs(d - MemberEps) < band) return true;
            float lo = -s.HalfLength, hi = s.HalfLength;
            if (MathF.Abs(lat - lo) < band || MathF.Abs(lat - hi) < band
                || MathF.Abs(lat - (lo - s.ExtLo)) < band || MathF.Abs(lat - (hi + s.ExtHi)) < band) return true;
        }
        return false;
    }
}

// One row: aim an input-parametrized action (a stab) along its direction. The residual is the
// SIGNED ANGLE √w·atan2(v × û*, v · û*) between the live aim vector v = (right hand − left hand)
// and the frozen target unit direction û*. The angle (not the bare cross) is used so the cost
// angle² has its ONLY minimum at v ∥ û* (parallel) — antiparallel is a *maximum*, not a second
// zero, so the solve can't fall into the wrong basin (the bare cross v×û* zeroes at both). û* is
// the authored (Δθ=0) reference aim ROTATED by the stab's deviation from horizontal-forward
// (captured once per frame, §2 of STAB_AIM_PLAN), so it preserves the clip's windup→thrust
// dynamics while turning the whole aim onto the input direction. The solver bends the
// (overlay-owned, post-compose) arm via Δθ. d = (d.x, δ) cancels (shifts both hands equally →
// drops out of pR − pL), so the aim row has no d columns.
public sealed class ActionAimConstraint : ISolveConstraint
{
    public string Name => "ActionAimConstraint";

    public int Residuals(SolveProblem p, PoseEval e, ReadOnlySpan<float> x, Span<float> r)
    {
        if (!p.AimActive) return 0;
        Vector2 pL = e.Pose.WorldOf(p.AimBoneL).Translation;
        Vector2 pR = e.Pose.WorldOf(p.AimBoneR).Translation;
        Vector2 v = pR - pL, u = p.AimTarget;
        float c = v.X * u.Y - v.Y * u.X;   // cross
        float d = v.X * u.X + v.Y * u.Y;   // dot
        float sw = MathF.Sqrt(p.Cfg.TierAim);
        r[0] = sw * MathF.Atan2(c, d);     // signed angle(v, û*); 0 ⇔ parallel, ±π ⇔ antiparallel (a max)
        return 1;
    }

    public int Jacobian(SolveProblem p, PoseEval e, ReadOnlySpan<float> x, Span<float> jac, int stride, int row0)
    {
        if (!p.AimActive) return 0;
        int nv = p.Vars;
        var cxR = e.ColX.AsSpan(0, nv);  var cyR = e.ColY.AsSpan(0, nv);
        var cxL = e.ColX2.AsSpan(0, nv); var cyL = e.ColY2.AsSpan(0, nv);
        Vector2 pR = e.Pose.WorldOf(p.AimBoneR).Translation;
        Vector2 pL = e.Pose.WorldOf(p.AimBoneL).Translation;
        SolveObjective.PointJacobianColumns(p, e, p.AimBoneR, pR, cxR, cyR);   // ∂pR/∂x
        SolveObjective.PointJacobianColumns(p, e, p.AimBoneL, pL, cxL, cyL);   // ∂pL/∂x
        Vector2 v = pR - pL, u = p.AimTarget;
        float c = v.X * u.Y - v.Y * u.X, d = v.X * u.X + v.Y * u.Y;
        float denom = c * c + d * d;       // = |v|² (û* unit); the d(atan2) normalizer
        if (denom < 1e-9f) return 1;        // hands coincident — leave the row at 0
        float sw = MathF.Sqrt(p.Cfg.TierAim) / denom;
        // θ = atan2(c, d) ⇒ ∂θ/∂x_k = (d·∂c − c·∂d)/(c²+d²), with ∂v = ∂pR − ∂pL.
        for (int k = 0; k < nv; k++)
        {
            float dvx = cxR[k] - cxL[k], dvy = cyR[k] - cyL[k];
            float dc = dvx * u.Y - dvy * u.X;   // ∂(cross)
            float dd = dvx * u.X + dvy * u.Y;   // ∂(dot)
            jac[row0 * stride + k] = sw * (d * dc - c * dd);
        }
        return 1;
    }
}

// Two rows: √ComWeightY · δ and √ComWeightX · d.x — the soft com ties pulling the root
// offset d → baseline. The Y row lets a no-contact flight frame settle to the com anchor
// (both feet free to leave the ground). The X row is the ABSOLUTE anti-absorption guard on
// the body sway: pulling toward 0 (not toward last frame) charges sustained travel
// absorption quadratically, so d.x can soak the turning-point singularity's residual but
// can never carry the cadence. ComWeightX ≫ ComWeightY on purpose.
//
// Rows 2–3 are the TEMPORAL smoothness twins: √λs·(δ − δ_prev) and √λs·(d.x − d.x_prev),
// anchored on last frame's EMITTED offsets (p.DyEmitted / p.DxEmitted). λs = λ·(1−b)/b with
// b the base ease factor — the same derivation as the per-bone Δθ smoothness (step 1.6),
// so with nothing else in the objective δ eases toward 0 by exactly the factor the
// no-solve frames apply. Without these rows δ was memoryless: it jumped to whatever the
// new contact's ground-hold asked on the capture frame and snapped to 0 on release — the
// 4px one-frame root drop/pop at every run stride's flight ↔ stance edge.
public sealed class ComOffsetConstraint : ISolveConstraint
{
    public string Name => "ComOffsetConstraint";
    private static void Weights(SolveProblem p, out float wy, out float wx, out float sy, out float sx)
    {
        var cfg = p.Cfg;
        float b = p.EaseBase, k = (1f - b) / b;
        wy = MathF.Sqrt(cfg.ComWeightY) * p.InvCharLen;
        wx = MathF.Sqrt(cfg.ComWeightX) * p.InvCharLen;
        sy = MathF.Sqrt(cfg.ComWeightY * k) * p.InvCharLen;
        sx = MathF.Sqrt(cfg.ComWeightX * k) * p.InvCharLen;
    }
    public int Residuals(SolveProblem p, PoseEval e, ReadOnlySpan<float> x, Span<float> r)
    {
        Weights(p, out float wy, out float wx, out float sy, out float sx);
        r[0] = wy * x[IdxDy];
        r[1] = wx * x[IdxDx];
        r[2] = sy * (x[IdxDy] - p.DyEmitted);
        r[3] = sx * (x[IdxDx] - p.DxEmitted);
        return 4;
    }
    public int Jacobian(SolveProblem p, PoseEval e, ReadOnlySpan<float> x, Span<float> jac, int stride, int row0)
    {
        Weights(p, out float wy, out float wx, out float sy, out float sx);
        jac[row0 * stride + IdxDy]       = wy;
        jac[(row0 + 1) * stride + IdxDx] = wx;
        jac[(row0 + 2) * stride + IdxDy] = sy;
        jac[(row0 + 3) * stride + IdxDx] = sx;
        return 4;
    }
}

// N rows: √λ_θ(i) · Δθ_i — the per-bone Tikhonov prior (stiff torso, loose limbs) keeping
// corrections minimal and JᵀJ non-singular where constraints under-determine the pose. The
// per-bone weight is what stops a redundant proximal joint drifting to the box.
public sealed class PosePriorConstraint : ISolveConstraint
{
    public string Name => "PosePriorConstraint";
    public int Residuals(SolveProblem p, PoseEval e, ReadOnlySpan<float> x, Span<float> r)
    {
        var cfg = p.Cfg;
        int bones = p.Skeleton.Count;
        for (int i = 0; i < bones; i++)
            r[i] = MathF.Sqrt(p.IsCore[i] ? cfg.CorePosePrior : cfg.LimbPosePrior) * x[IdxTheta0 + i];
        return bones;
    }
    public int Jacobian(SolveProblem p, PoseEval e, ReadOnlySpan<float> x, Span<float> jac, int stride, int row0)
    {
        var cfg = p.Cfg;
        int bones = p.Skeleton.Count;
        for (int i = 0; i < bones; i++)
            jac[(row0 + i) * stride + (IdxTheta0 + i)] = MathF.Sqrt(p.IsCore[i] ? cfg.CorePosePrior : cfg.LimbPosePrior);
        return bones;
    }
}

// N rows: √λs_i · (Δθ_i − t_i), t_i = wrapAngle(θ_emitted,i − composedEntry_i) — TEMPORAL
// smoothness of the pose's DEVIATION FROM THE BASE CLIP against the deviation actually
// emitted last frame (both measured from this frame's composed base at the ENTRY phase, a
// per-solve constant — p.SmoothTarget, filled when the problem is frozen). This row IS the
// retired BlendToward ease, moved inside the objective (polish item 1): λs_i is derived from
// the Stiffness constants + dt (p.LambdaSmooth) so an UNCONSTRAINED bone's optimum is exactly
// the old exponential ease of its deviation, while a constrained bone trades smoothing
// against pins/contacts in ONE objective (no ease-induced pin lag on the rendered tip).
// Measuring the DEVIATION — not the absolute angle — is load-bearing: an absolute-pose
// smoothness charges clip playback itself (Δφ advancing the walk IS pose change), which
// measurably dragged the run cadence to a crawl when tried. Deviation smoothness makes
// playback free, still bridges clip switches (right after a switch, emitted − newBase is
// the whole pose gap → Δθ spans it and then decays), and is diagonal + Δφ-free.
public sealed class ThetaSmoothnessConstraint : ISolveConstraint
{
    public string Name => "ThetaSmoothnessConstraint";
    public int Residuals(SolveProblem p, PoseEval e, ReadOnlySpan<float> x, Span<float> r)
    {
        int bones = p.Skeleton.Count;
        for (int i = 0; i < bones; i++)
            r[i] = MathF.Sqrt(p.LambdaSmooth[i]) * (x[IdxTheta0 + i] - p.SmoothTarget[i]);
        return bones;
    }
    public int Jacobian(SolveProblem p, PoseEval e, ReadOnlySpan<float> x, Span<float> jac, int stride, int row0)
    {
        int bones = p.Skeleton.Count;
        for (int i = 0; i < bones; i++)
            jac[(row0 + i) * stride + (IdxTheta0 + i)] = MathF.Sqrt(p.LambdaSmooth[i]);
        return bones;
    }
}
