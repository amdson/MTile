using System;
using Microsoft.Xna.Framework;

namespace MTile;

// The least-squares solve's residual/Jacobian machinery (Plans/ANIMATION_SOLVER_PLAN §11),
// split out of CharacterAnimator for legibility: the solve-variable layout, the reusable
// point-Jacobian primitive, the rotation lever arm, and the constraint library
// (`ISolveConstraint` + the blocks the composite objective is assembled from). These are
// `partial` members of CharacterAnimator, so they keep full private access to its FK state
// (_scratch's world buffer, _baseBlend, _angVel, _solveRoot, _contacts, the solver constants).
// The solve ORCHESTRATION (BuildSolvePose, CadenceResiduals/CadenceJacobian, SolvePhaseStepLm)
// stays in CharacterAnimator.cs — this file is purely the constraint + Jacobian math.
public sealed partial class CharacterAnimator
{
    // Variable layout of x (§11.1). δ ≡ d.y; d.x deferred.
    private const int IdxPhi = 0, IdxDy = 1, IdxTheta0 = 2;

    // The one reusable gradient primitive (§11.2): the sensitivity of a world point `p` on
    // bone `b` to the solve variables, written into colX[v]/colY[v] (x/y component of ∂p/∂x_v).
    // Every geometric constraint's Jacobian is (∂r/∂p)·this. Covers the FK-driven channels:
    //   ∂p/∂Δθ_j = Lever(j, p)                       for each ancestor j of b
    //   ∂p/∂Δφ   = Σ_j baseBlend_j · ω_j · Lever(j, p)
    // Δθ is applied to the COMPOSED pose (BuildSolvePose), so its lever is UNATTENUATED — an
    // overlay-owned bone (a vault hand) still bends under a pin. Δφ moves the BASE clip, which the
    // overlay attenuates per bone, so it keeps the baseBlend_j = Π(1−w) factor. (No overlay ⇒
    // baseBlend_j = 1 ⇒ the two channels coincide, so locomotion is unchanged.)
    // δ (IdxDy) does NOT move p in the current model — it is a residual-side vertical shift
    // (tip.Y + δ), so colX/Y[IdxDy] stay 0 and the constraints that use δ add that column
    // themselves. Requires _scratch's world buffer + _angVel current (BuildSolvePose +
    // SampleAngularVelocity, done once by CadenceJacobian). Caller supplies the scratch spans.
    private void PointJacobianColumns(int bone, Vector2 p, Span<float> colX, Span<float> colY)
    {
        colX.Clear(); colY.Clear();
        float dphiX = 0f, dphiY = 0f;
        for (int j = bone; j >= 0; j = _skeleton.Bones[j].Parent)
        {
            int par = _skeleton.Bones[j].Parent;
            // R·T·S: θ_j is the OUTERMOST factor of L_j (world[j] = world[par]·R(θ_j)·T·S), so it
            // pivots about world[par]'s origin (the PARENT's joint) and acts in the parent's
            // linear frame A_p.
            Affine2 wp = par < 0 ? _solveRoot : _scratch.WorldOf(par);   // A_p: parent linear frame
            Vector2 lev = Lever(wp, wp.Translation, p);                   // ∂p/∂θ_j (exact; facing flip + scale)
            float blend = _baseBlend[j];   // Π(1−w) over the active overlay slots masking j
            colX[IdxTheta0 + j] = lev.X;                                  // ∂p/∂Δθ_j (post-compose → unattenuated)
            colY[IdxTheta0 + j] = lev.Y;
            dphiX += blend * _angVel[j] * lev.X;                          // ∂p/∂Δφ (base clip, overlay-attenuated)
            dphiY += blend * _angVel[j] * lev.Y;
        }
        colX[IdxPhi] = dphiX; colY[IdxPhi] = dphiY;
    }

    // A residual block: emits its rows into `r`/`jac` (starting at row0) and returns the count.
    // Both methods run AFTER the shared forward pass (BuildSolvePose / SampleAngularVelocity),
    // so they read _scratch's world buffer + _angVel directly. The count MUST be stable across a
    // single Minimize call (the LM core's fixed-row contract) — it is, because the contact set is
    // frozen by RefreshContacts before the solve.
    private interface ISolveConstraint
    {
        int Residuals(ReadOnlySpan<float> x, Span<float> r);
        int Jacobian(ReadOnlySpan<float> x, Span<float> jac, int stride, int row0);
    }

    // Two rows per planted contact: √w·(tipX − targetX) horizontal no-slip (the cadence pin,
    // drives Δφ) then √w·(tipY + δ − targetY) vertical ground hold (drives δ, body bobs). The
    // Jacobian is the point primitive scaled by √w, plus √w on the V row's δ column.
    private sealed class PlantedContactsConstraint : ISolveConstraint
    {
        private readonly CharacterAnimator _a;
        public PlantedContactsConstraint(CharacterAnimator a) => _a = a;

        public int Residuals(ReadOnlySpan<float> x, Span<float> r)
        {
            float dy = x[IdxDy];
            int n = 0;
            foreach (var c in _a._contacts)
            {
                Vector2 tip = _a._scratch.WorldOf(c.Bone).Translation;   // bone's far end = contact tip
                float sw = MathF.Sqrt(AnimSolverConfig.Current.TierContact * c.Weight);
                r[n++] = sw * (tip.X - c.Target.X);          // horizontal no-slip (drives Δφ)
                r[n++] = sw * (tip.Y + dy - c.Target.Y);     // vertical ground hold (drives δ)
            }
            return n;
        }

        public int Jacobian(ReadOnlySpan<float> x, Span<float> jac, int stride, int row0)
        {
            int nv = IdxTheta0 + _a._skeleton.Count;
            var colX = _a._colX.AsSpan(0, nv);
            var colY = _a._colY.AsSpan(0, nv);
            int row = row0;
            foreach (var c in _a._contacts)
            {
                Vector2 tip = _a._scratch.WorldOf(c.Bone).Translation;
                float sw = MathF.Sqrt(AnimSolverConfig.Current.TierContact * c.Weight);
                _a.PointJacobianColumns(c.Bone, tip, colX, colY);
                int hRow = row, vRow = row + 1;
                for (int v = 0; v < nv; v++)
                {
                    jac[hRow * stride + v] = sw * colX[v];   // ∂H/∂x_v from the x component
                    jac[vRow * stride + v] = sw * colY[v];   // ∂V/∂x_v from the y component
                }
                jac[vRow * stride + IdxDy] += sw;            // ∂V/∂δ (colY[IdxDy]==0, so this is √w)
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
    private sealed class FixedPointConstraint : ISolveConstraint
    {
        private readonly CharacterAnimator _a;
        public FixedPointConstraint(CharacterAnimator a) => _a = a;

        public int Residuals(ReadOnlySpan<float> x, Span<float> r)
        {
            float dy = x[IdxDy];
            float sw = MathF.Sqrt(AnimSolverConfig.Current.TierHard);
            int n = 0;
            foreach (var (bone, target) in _a._pins)
            {
                Vector2 tip = _a._scratch.WorldOf(bone).Translation;
                r[n++] = sw * (tip.X - target.X);            // pin X
                r[n++] = sw * (tip.Y + dy - target.Y);       // pin Y (rides the body bob δ)
            }
            return n;
        }

        public int Jacobian(ReadOnlySpan<float> x, Span<float> jac, int stride, int row0)
        {
            int nv = IdxTheta0 + _a._skeleton.Count;
            var colX = _a._colX.AsSpan(0, nv);
            var colY = _a._colY.AsSpan(0, nv);
            float sw = MathF.Sqrt(AnimSolverConfig.Current.TierHard);
            int row = row0;
            foreach (var (bone, _) in _a._pins)
            {
                Vector2 tip = _a._scratch.WorldOf(bone).Translation;
                _a.PointJacobianColumns(bone, tip, colX, colY);
                int xRow = row, yRow = row + 1;
                for (int v = 0; v < nv; v++)
                {
                    jac[xRow * stride + v] = sw * colX[v];   // ∂(pinX)/∂x_v
                    jac[yRow * stride + v] = sw * colY[v];   // ∂(pinY)/∂x_v
                }
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
    private sealed class NoPenetrationConstraint : ISolveConstraint
    {
        private readonly CharacterAnimator _a;
        public NoPenetrationConstraint(CharacterAnimator a) => _a = a;

        public int Residuals(ReadOnlySpan<float> x, Span<float> r)
        {
            float dy = x[IdxDy];
            float sw = MathF.Sqrt(AnimSolverConfig.Current.TierNoPen);
            int bones = _a._skeleton.Count, n = 0;
            foreach (var s in _a._surfaces)
                for (int b = 0; b < bones; b++)
                {
                    Vector2 tip = _a._scratch.WorldOf(b).Translation;
                    float gap = s.Normal.X * (tip.X - s.Point.X) + s.Normal.Y * (tip.Y + dy - s.Point.Y);
                    float pen = s.Margin - gap;                  // >0 ⇒ inside the margin (penetrating)
                    r[n++] = pen > 0f ? sw * pen : 0f;
                }
            return n;
        }

        public int Jacobian(ReadOnlySpan<float> x, Span<float> jac, int stride, int row0)
        {
            float dy = x[IdxDy];
            float sw = MathF.Sqrt(AnimSolverConfig.Current.TierNoPen);
            int nv = IdxTheta0 + _a._skeleton.Count, bones = _a._skeleton.Count;
            var colX = _a._colX.AsSpan(0, nv);
            var colY = _a._colY.AsSpan(0, nv);
            int row = row0;
            foreach (var s in _a._surfaces)
                for (int b = 0; b < bones; b++, row++)
                {
                    Vector2 tip = _a._scratch.WorldOf(b).Translation;
                    float gap = s.Normal.X * (tip.X - s.Point.X) + s.Normal.Y * (tip.Y + dy - s.Point.Y);
                    if (s.Margin - gap <= 0f) continue;          // inactive → zero row (solver pre-zeroes)
                    _a.PointJacobianColumns(b, tip, colX, colY); // ∂(world tip)/∂x (δ added below)
                    // r = √w·(margin − n·q) ⇒ ∂r/∂x = −√w · n·(∂q/∂x)
                    for (int v = 0; v < nv; v++)
                        jac[row * stride + v] = -sw * (s.Normal.X * colX[v] + s.Normal.Y * colY[v]);
                    jac[row * stride + IdxDy] += -sw * s.Normal.Y;   // q.Y rides δ ⇒ ∂(n·q)/∂δ = n.Y
                }
            return row - row0;
        }
    }

    // One row: √PhaseStepPrior · (Δφ − Δφ_prev) — the playback-continuity / momentum prior.
    private sealed class PlaybackContinuityConstraint : ISolveConstraint
    {
        private readonly CharacterAnimator _a;
        public PlaybackContinuityConstraint(CharacterAnimator a) => _a = a;
        public int Residuals(ReadOnlySpan<float> x, Span<float> r)
        { r[0] = MathF.Sqrt(AnimSolverConfig.Current.PhaseStepPrior) * (x[IdxPhi] - _a._prevPhaseStep); return 1; }
        public int Jacobian(ReadOnlySpan<float> x, Span<float> jac, int stride, int row0)
        { jac[row0 * stride + IdxPhi] = MathF.Sqrt(AnimSolverConfig.Current.PhaseStepPrior); return 1; }
    }

    // One row: √ComWeightY · δ — the soft com tie pulling δ→baseline so a no-contact flight
    // frame settles to the com anchor (both feet free to leave the ground).
    private sealed class ComOffsetConstraint : ISolveConstraint
    {
        private readonly CharacterAnimator _a;
        public ComOffsetConstraint(CharacterAnimator a) => _a = a;
        public int Residuals(ReadOnlySpan<float> x, Span<float> r)
        { r[0] = MathF.Sqrt(AnimSolverConfig.Current.ComWeightY) * x[IdxDy]; return 1; }
        public int Jacobian(ReadOnlySpan<float> x, Span<float> jac, int stride, int row0)
        { jac[row0 * stride + IdxDy] = MathF.Sqrt(AnimSolverConfig.Current.ComWeightY); return 1; }
    }

    // N rows: √λ_θ(i) · Δθ_i — the per-bone Tikhonov prior (_posePrior: stiff torso, loose
    // limbs) keeping corrections minimal and JᵀJ non-singular where constraints under-determine
    // the pose. The per-bone weight is what stops a redundant proximal joint drifting to the box.
    private sealed class PosePriorConstraint : ISolveConstraint
    {
        private readonly CharacterAnimator _a;
        public PosePriorConstraint(CharacterAnimator a) => _a = a;
        public int Residuals(ReadOnlySpan<float> x, Span<float> r)
        {
            var cfg = AnimSolverConfig.Current;
            int bones = _a._skeleton.Count;
            for (int i = 0; i < bones; i++)
                r[i] = MathF.Sqrt(_a._isCore[i] ? cfg.CorePosePrior : cfg.LimbPosePrior) * x[IdxTheta0 + i];
            return bones;
        }
        public int Jacobian(ReadOnlySpan<float> x, Span<float> jac, int stride, int row0)
        {
            var cfg = AnimSolverConfig.Current;
            int bones = _a._skeleton.Count;
            for (int i = 0; i < bones; i++)
                jac[(row0 + i) * stride + (IdxTheta0 + i)] = MathF.Sqrt(_a._isCore[i] ? cfg.CorePosePrior : cfg.LimbPosePrior);
            return bones;
        }
    }

    // N rows: √ThetaSmoothPrior · (Δθ_i − Δθ_i,prev) — the TEMPORAL smoothness prior. Unlike the
    // Tikhonov (toward 0), this pulls each correction toward LAST frame's solved value, pinning the
    // near-singular/flat limb DOF that a single 2D pin leaves under-determined — so it stops landing
    // somewhere new each frame (jitter) without biasing the reach (cost → 0 once Δθ settles). The
    // Jacobian is the same constant √λ on the diagonal as the Tikhonov (the −Δθ_prev is a constant).
    private sealed class ThetaSmoothnessConstraint : ISolveConstraint
    {
        private readonly CharacterAnimator _a;
        public ThetaSmoothnessConstraint(CharacterAnimator a) => _a = a;
        public int Residuals(ReadOnlySpan<float> x, Span<float> r)
        {
            float sq = MathF.Sqrt(AnimSolverConfig.Current.ThetaSmooth);
            int bones = _a._skeleton.Count;
            for (int i = 0; i < bones; i++) r[i] = sq * (x[IdxTheta0 + i] - _a._prevTheta[i]);
            return bones;
        }
        public int Jacobian(ReadOnlySpan<float> x, Span<float> jac, int stride, int row0)
        {
            float sq = MathF.Sqrt(AnimSolverConfig.Current.ThetaSmooth);
            int bones = _a._skeleton.Count;
            for (int i = 0; i < bones; i++) jac[(row0 + i) * stride + (IdxTheta0 + i)] = sq;
            return bones;
        }
    }

    // The 2D rotation lever arm ∂p/∂θ for a joint whose rotation acts in the linear frame `wp`
    // (its parent's world transform) and pivots about `pivot` (under R·T·S, the parent's joint =
    // wp.Translation), evaluated at world point `p`. Exactly A·J·A⁻¹·(p − pivot) where A is wp's linear part
    // and J the 90° rotation — correct under the facing-flip reflection and any scale/squash
    // (reduces to the bare perp(p − pivot) when A is a pure rotation). Returns 0 if wp is singular.
    private static Vector2 Lever(in Affine2 wp, Vector2 pivot, Vector2 p)
    {
        float dx = p.X - pivot.X, dy = p.Y - pivot.Y;
        float det = wp.M11 * wp.M22 - wp.M12 * wp.M21;
        if (MathF.Abs(det) < 1e-12f) return Vector2.Zero;
        float inv = 1f / det;
        float wx = ( wp.M22 * dx - wp.M12 * dy) * inv;     // A⁻¹·(p − o)
        float wy = (-wp.M21 * dx + wp.M11 * dy) * inv;
        float jx = -wy, jy = wx;                            // J·(…)
        return new Vector2(wp.M11 * jx + wp.M12 * jy, wp.M21 * jx + wp.M22 * jy);   // A·(…)
    }
}
