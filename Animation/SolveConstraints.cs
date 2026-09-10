using System;
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
// landing" pull — deliberately NOT a planted contact: no d.x/δ columns (the root must not
// chase a swinging foot), no SkipPair collision exemption, no no-slip cadence semantics.
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
            r[n++] = sw * (tip.X - target.X);
            r[n++] = sw * (tip.Y - target.Y);
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

// One row per (surface × sampled bone tip): the one-sided HALF-PLANE no-penetration residual
// √w·max(0, margin − n·(q − p0)), pushing a limb point q out of a solid surface the movement
// layer already resolved (wall-slide wall, ground line — §11.5/§4.5 v1). q = each bone's far
// tip; every joint of the chain is some bone's tip, so sampling all tips covers the limbs.
// INACTIVE rows (the point is already clear) emit 0 residual AND 0 Jacobian, so the row COUNT
// is stable across one Minimize (the LM fixed-row contract) without a separate active-set
// pass — only WHICH rows are nonzero changes. The active residual is smooth (affine in q), so
// its analytic Jacobian −√w·n·PointJacobian(b, q) matches finite differences everywhere except
// the activation knee (the max()'s corner, like the keyframe kink, is where the FD oracle is
// mute). The Y component rides the body bob δ (q.Y + δ), same as a contact/pin's vertical row.
public sealed class NoPenetrationConstraint : ISolveConstraint
{
    public string Name => "NoPenetrationConstraint";

    // A (surface, bone) pair that never emits a live row — its row slot stays a
    // permanent zero (residual AND Jacobian), so the fixed surfaces×bones layout is
    // preserved. Two reasons:
    //  - BoneMask: terrain planes constrain only the tips they were extracted for
    //    (-1 = all bones, the wall-slide plane).
    //  - Planted-foot exemption: the cadence solve sweeps a PLANTED foot along its
    //    support plane at gap ≈ 0, exactly on the one-sided knee — the contact's
    //    V-row already owns "foot sits on ground", so its own upward support plane
    //    must not flicker against it.
    private static bool SkipPair(SolveProblem p, in SolverSurface s, int b)
    {
        if (((s.BoneMask >> b) & 1) == 0) return true;
        if (s.Normal.Y < -0.7f)                        // upward-facing plane (y-down)
            foreach (var c in p.Contacts)
                if (c.Bone == b &&
                    MathF.Abs(s.Normal.X * (c.Target.X - s.Point.X)
                            + s.Normal.Y * (c.Target.Y - s.Point.Y)) < ContactSupportBand)
                    return true;                       // this plane supports the plant
        return false;
    }

    public int Residuals(SolveProblem p, PoseEval e, ReadOnlySpan<float> x, Span<float> r)
    {
        float dy = x[IdxDy], dx = x[IdxDx];
        float sw = MathF.Sqrt(p.Cfg.TierNoPen) * p.InvCharLen;
        int bones = p.Skeleton.Count, n = 0;
        foreach (var s in p.Surfaces)
            for (int b = 0; b < bones; b++)
            {
                if (SkipPair(p, in s, b)) { r[n++] = 0f; continue; }
                Vector2 tip = e.Pose.WorldOf(b).Translation;
                float gap = s.Normal.X * (tip.X + dx - s.Point.X) + s.Normal.Y * (tip.Y + dy - s.Point.Y);
                float pen = s.Margin - gap;                  // >0 ⇒ inside the margin (penetrating)
                r[n++] = pen > 0f ? sw * pen : 0f;
            }
        return n;
    }

    public int Jacobian(SolveProblem p, PoseEval e, ReadOnlySpan<float> x, Span<float> jac, int stride, int row0)
    {
        float dy = x[IdxDy], dx = x[IdxDx];
        float sw = MathF.Sqrt(p.Cfg.TierNoPen) * p.InvCharLen;
        int nv = p.Vars, bones = p.Skeleton.Count;
        var colX = e.ColX.AsSpan(0, nv);
        var colY = e.ColY.AsSpan(0, nv);
        int row = row0;
        foreach (var s in p.Surfaces)
            for (int b = 0; b < bones; b++, row++)
            {
                if (SkipPair(p, in s, b)) continue;          // permanent zero row
                Vector2 tip = e.Pose.WorldOf(b).Translation;
                float gap = s.Normal.X * (tip.X + dx - s.Point.X) + s.Normal.Y * (tip.Y + dy - s.Point.Y);
                if (s.Margin - gap <= 0f) continue;          // inactive → zero row (solver pre-zeroes)
                SolveObjective.PointJacobianColumns(p, e, b, tip, colX, colY);   // ∂(world tip)/∂x (d added below)
                // r = √w·(margin − n·q) ⇒ ∂r/∂x = −√w · n·(∂q/∂x)
                for (int v = 0; v < nv; v++)
                    jac[row * stride + v] = -sw * (s.Normal.X * colX[v] + s.Normal.Y * colY[v]);
                jac[row * stride + IdxDx] += -sw * s.Normal.X;   // q.X rides d.x ⇒ ∂(n·q)/∂d.x = n.X
                jac[row * stride + IdxDy] += -sw * s.Normal.Y;   // q.Y rides δ ⇒ ∂(n·q)/∂δ = n.Y
            }
        return row - row0;
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

// One row: √PhaseAccelPrior · (Δφ − Δφ_prev) / (dt² · PhaseAccelRef) — the cadence
// ACCELERATION penalty (playback continuity / momentum). Δφ − Δφ_prev is the phase
// acceleration in cycles/frame²; dividing by dt² makes it cycles/s² (so the row means the
// same thing at 30 and 60 fps) and by PhaseAccelRef (100 cycles/s² ≈ the run's re-contact
// hop at 60 fps) makes it O(1) like every other dimensionless row — λ = 1 charges one full
// hop about what one pixel of planted-foot slip costs (√TierContact/reach ≈ 1). The old
// PhaseStepPrior was this row without the normalization: in raw phase/frame units a 0.03
// hop cost 8·0.03² ≈ 0.007 against 1.0 for 1px of slip, so at 8 it never did anything.
public sealed class PlaybackContinuityConstraint : ISolveConstraint
{
    public string Name => "PlaybackContinuityConstraint";
    public int Residuals(SolveProblem p, PoseEval e, ReadOnlySpan<float> x, Span<float> r)
    { r[0] = MathF.Sqrt(p.Cfg.PhaseAccelPrior) * p.PhaseAccelNorm * (x[IdxPhi] - p.PrevPhaseStep); return 1; }
    public int Jacobian(SolveProblem p, PoseEval e, ReadOnlySpan<float> x, Span<float> jac, int stride, int row0)
    { jac[row0 * stride + IdxPhi] = MathF.Sqrt(p.Cfg.PhaseAccelPrior) * p.PhaseAccelNorm; return 1; }
}

// One row: √PhaseFloorPrior · max(0, 1 − Δφ/floor) — the one-sided phase-rate floor.
// A hinge (NoPen-style knee: inactive ⇒ 0 residual AND 0 Jacobian, count stays 1) that
// props the solved step up toward the speed-derived floor when nothing else drives it —
// a weak-weight contact (feather fade / fresh zero-residual capture) otherwise lets Δφ
// collapse, and the flight coast then replays that collapsed value for the whole
// no-contact window. Deficit is normalized by the floor so the row is O(1) like the
// other dimensionless rows. floor ≤ ~1e-5 (standstill / static solve) disables the row.
public sealed class PhaseRateFloorConstraint : ISolveConstraint
{
    public string Name => "PhaseRateFloorConstraint";
    public int Residuals(SolveProblem p, PoseEval e, ReadOnlySpan<float> x, Span<float> r)
    {
        var cfg = p.Cfg;
        float fl = p.PhaseFloor;
        if (cfg.PhaseFloorMode == 2 || fl <= 1e-5f) { r[0] = 0f; return 1; }  // box mode: the bound does it
        float def = cfg.PhaseFloorMode == 1 ? fl - x[IdxPhi] : 1f - x[IdxPhi] / fl;
        r[0] = def > 0f ? MathF.Sqrt(cfg.PhaseFloorPrior) * def : 0f;
        return 1;
    }
    public int Jacobian(SolveProblem p, PoseEval e, ReadOnlySpan<float> x, Span<float> jac, int stride, int row0)
    {
        var cfg = p.Cfg;
        float fl = p.PhaseFloor;
        if (cfg.PhaseFloorMode == 2 || fl <= 1e-5f) return 1;
        if (x[IdxPhi] < fl)
            jac[row0 * stride + IdxPhi] = -MathF.Sqrt(cfg.PhaseFloorPrior)
                                        * (cfg.PhaseFloorMode == 1 ? 1f : 1f / fl);
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
