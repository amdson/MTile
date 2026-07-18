using System;
using Microsoft.Xna.Framework;

namespace MTile;

// DIAGNOSTICS — test hooks and tuning instrumentation only; nothing here runs on the
// game's per-frame path. Split out of CharacterAnimator.cs for legibility (the partial
// keeps full private access to the solve state):
//   MaxJacobianError   — the FD-vs-analytic Jacobian oracle (the headline solver test)
//   SolveScaleReport   — gradient/column magnitudes the weight tiers are tuned from
//   SolvedBoneTipWorld — where the solver actually placed a bone tip (incl. the d offset)
//   AimAngleError      — how far off the aim re-target landed
public sealed partial class CharacterAnimator
{
    // TEST HOOK: the largest absolute discrepancy between the analytic cadence Jacobian
    // (CadenceJacobian) and a central finite difference of CadenceResiduals, evaluated at
    // the live solve point of the last frame. ~0 confirms every analytic column. Returns
    // -1 when no cadence solve ran this frame (flight / non-locomotion, no pins).
    internal float MaxJacobianError()
    {
        if (!_haveCorr || _ls == null) return -1f;
        int bones = _skeleton.Count;
        int n = IdxTheta0 + bones;
        // 2/contact + 2/pin + bones/surface (no-penetration) + 1 aim + continuity + com(2) + bones×2 priors.
        int m = (_contacts.Count + _pins.Count) * 2 + _surfaces.Count * bones + (_aimActive ? 1 : 0) + 3 + 2 * bones;

        // The body may be far from the world origin (it has walked many units), so a residual
        // tip.x − target.x is a tiny difference of large coordinates and a float32 finite
        // difference of tip.x loses it to catastrophic cancellation. The Jacobian is
        // translation-invariant, so shift the whole solve to the origin (zero the root
        // translation; subtract it from each captured target) for the oracle, then restore.
        float ox = _solveRoot.Tx, oy = _solveRoot.Ty;
        var savedRoot = _solveRoot;
        _solveRoot = new Affine2(_solveRoot.M11, _solveRoot.M12, _solveRoot.M21, _solveRoot.M22, 0f, 0f);
        for (int i = 0; i < _contacts.Count; i++)
        { var c = _contacts[i]; c.Target = new Vector2(c.Target.X - ox, c.Target.Y - oy); _contacts[i] = c; }
        for (int i = 0; i < _pins.Count; i++)
        { var p = _pins[i]; _pins[i] = (p.bone, new Vector2(p.target.X - ox, p.target.Y - oy)); }
        for (int i = 0; i < _surfaces.Count; i++)
        { var sf = _surfaces[i]; _surfaces[i] = new SolverSurface(new Vector2(sf.Point.X - ox, sf.Point.Y - oy), sf.Normal, sf.Margin); }

        var x = new float[n];
        Array.Copy(_solveVars, x, n);
        var anal = new float[m * n];
        CadenceJacobian(x, anal, n);             // dense fill (we cleared by fresh alloc)

        var rp = new float[m];
        var rm = new float[m];

        // The no-penetration rows carry a one-sided max(0, margin − gap): smooth where active,
        // but with a KNEE at gap == margin. A central difference that straddles that knee (the
        // tip sits within ~one FD step of the boundary) sees only half the slope and is NOT a
        // valid oracle there — exactly the keyframe-boundary situation for Δφ. So mark every
        // no-pen row whose penetration is within a band of 0 and skip it below; the analytic
        // value is still exact, we just can't judge it by FD at the corner. Rows comfortably
        // active or inactive (|pen| ≥ band) stay valid (a single-column ±h step can't flip them).
        var skipRow = new bool[m];
        int npStart = (_contacts.Count + _pins.Count) * 2;
        int b0 = _skeleton.Count;
        const float kneeBand = 0.5f;   // > any single-column FD tip displacement (lever × h)
        int npRow = npStart;
        foreach (var s in _surfaces)
            for (int b = 0; b < b0; b++, npRow++)
            {
                Vector2 tip = _scratch.WorldOf(b).Translation;   // _scratch left at x by CadenceJacobian
                float gap = s.Normal.X * (tip.X + x[IdxDx] - s.Point.X)
                          + s.Normal.Y * (tip.Y + x[IdxDy] - s.Point.Y);
                if (MathF.Abs(s.Margin - gap) < kneeBand) skipRow[npRow] = true;
            }

        float worst = 0f;
        for (int j = 0; j < n; j++)
        {
            // Per-column FD step: Δφ (col 0) runs through the Hermite sample, whose cubic has
            // high curvature inside the short keyframe intervals (f‴ ~ 1/width³), so its step
            // must SCALE WITH the bracketing interval — a fixed step's O(h²·f‴) truncation
            // blows past the oracle tolerance in legitimately-authored ~0.01-phase intervals.
            // δ/Δθ are low-curvature but their residuals are differences of large world
            // coordinates, so they need a LARGER h to clear the float32 cancellation floor.
            float h = j == 0 ? PhiFdStep(_solveClip, Wrap01(_solvePhi + x[0])) : 1e-2f;
            // The C1 spline makes ∂/∂φ CONTINUOUS (the analytic column is exact everywhere),
            // but acceleration still jumps at a keyframe boundary (C1, not C2), so a central
            // difference that STRADDLES one carries O(h) error and can't serve as the oracle
            // there. Validate the Δφ column only when the ±h step stays inside one interval
            // (incl. the loop seam, which the wrap maps to a different interval) — by
            // continuity the analytic value is then correct at the boundaries too.
            if (j == 0)
            {
                int i0 = IntervalAt(_solveClip, Wrap01(_solvePhi + x[0]));
                if (IntervalAt(_solveClip, Wrap01(_solvePhi + x[0] + h)) != i0 ||
                    IntervalAt(_solveClip, Wrap01(_solvePhi + x[0] - h)) != i0) continue;
            }
            float save = x[j];
            x[j] = save + h; CadenceResiduals(x, rp);
            x[j] = save - h; CadenceResiduals(x, rm);
            x[j] = save;
            for (int i = 0; i < m; i++)
            {
                if (skipRow[i]) continue;   // no-pen row at its activation knee — FD oracle is mute here
                float a  = anal[i * n + j];
                float fd = (rp[i] - rm[i]) / (2f * h);
                // Relative metric: the float32 oracle carries ~0.1% truncation/cancellation
                // noise on large entries, while any STRUCTURAL Jacobian error (sign, wrong
                // lever, missing term) is O(10–100%). Normalizing by the magnitude separates
                // the two cleanly. (+1 keeps small-entry columns from blowing up on noise.)
                float e = MathF.Abs(fd - a) / (1f + MathF.Abs(a));
                if (e > worst) { worst = e; DbgWorstCol = j; DbgWorstRow = i; DbgFd = fd; DbgAnal = a; }
            }
        }

        _solveRoot = savedRoot;            // restore the shifted solve context
        for (int i = 0; i < _contacts.Count; i++)
        { var c = _contacts[i]; c.Target = new Vector2(c.Target.X + ox, c.Target.Y + oy); _contacts[i] = c; }
        for (int i = 0; i < _pins.Count; i++)
        { var p = _pins[i]; _pins[i] = (p.bone, new Vector2(p.target.X + ox, p.target.Y + oy)); }
        for (int i = 0; i < _surfaces.Count; i++)
        { var sf = _surfaces[i]; _surfaces[i] = new SolverSurface(new Vector2(sf.Point.X + ox, sf.Point.Y + oy), sf.Normal, sf.Margin); }
        return worst;
    }
    internal int DbgWorstCol, DbgWorstRow; internal float DbgFd, DbgAnal;

    // DIAGNOSTIC (tests/tuning only): at the last frame's solve point, report the magnitudes
    // that set the weight tiers (§11.4) — the relative gradient/column scales the priors must
    // balance, plus the solved values and the pin reach error. The weights are chosen from
    // THIS, not from first principles. Returns "(no solve)" on a frame where no LM solve ran.
    //
    // For each solve variable v, ‖J[:,v]‖ over the GEOMETRIC rows (contacts + pins) measures how
    // strongly v moves the pinned points — i.e. its natural gradient scale. Δθ is reported as the
    // max over bones (and which bone). Also: the cost gradient g=Jᵀr per block, the solved
    // Δφ/δ/max|Δθ|, and ‖tip − target‖ for each pin (did the hard pin actually reach?).
    internal string SolveScaleReport()
    {
        if (!_haveCorr || _ls == null) return "(no solve)";
        int bones = _skeleton.Count, n = IdxTheta0 + bones;
        int geom = (_contacts.Count + _pins.Count) * 2 + _surfaces.Count * bones + (_aimActive ? 1 : 0);
        int m = geom + 3 + 2 * bones;   // + continuity + com(δ, d.x) + bones×2 priors

        var x = new float[n];
        Array.Copy(_solveVars, x, n);
        var jac = new float[m * n];          // zeroed by fresh alloc; CadenceJacobian fills it
        CadenceJacobian(x, jac, n);          // also leaves _scratch world at the solved x
        var r = new float[m];
        CadenceResiduals(x, r);

        // Column L2 norms over the geometric rows only (the FK-coupled sensitivity).
        float ColNorm(int v) { float s = 0f; for (int i = 0; i < geom; i++) { float e = jac[i * n + v]; s += e * e; } return MathF.Sqrt(s); }
        float gradPhi = 0f, gradDy = 0f, gradTh = 0f;
        for (int i = 0; i < m; i++) { gradPhi += jac[i * n + IdxPhi] * r[i]; gradDy += jac[i * n + IdxDy] * r[i]; }
        float colTheMax = 0f; int colTheBone = -1;
        for (int b = 0; b < bones; b++) { float c = ColNorm(IdxTheta0 + b); if (c > colTheMax) { colTheMax = c; colTheBone = b; } float g = 0f; for (int i = 0; i < m; i++) g += jac[i * n + IdxTheta0 + b] * r[i]; gradTh = MathF.Max(gradTh, MathF.Abs(g)); }

        float maxTheta = 0f; int maxThetaBone = -1;
        for (int b = 0; b < bones; b++) { float a = MathF.Abs(_solveVars[IdxTheta0 + b]); if (a > maxTheta) { maxTheta = a; maxThetaBone = b; } }

        var sb = new System.Text.StringBuilder();
        sb.Append($"contacts={_contacts.Count} pins={_pins.Count} surfaces={_surfaces.Count}  |  ");
        sb.Append($"colNorm[Δφ]={ColNorm(IdxPhi),7:0.000} δ={ColNorm(IdxDy),6:0.000} Δθmax={colTheMax,6:0.000}({Name(colTheBone)})  |  ");
        sb.Append($"grad[Δφ]={gradPhi,8:0.000} δ={gradDy,7:0.000} Δθ={gradTh,7:0.000}  |  ");
        sb.Append($"solved Δφ={_solveVars[IdxPhi],6:0.000} δ={_solveVars[IdxDy],6:0.00} dx={_solveVars[IdxDx],5:0.00} |Δθ|max={maxTheta,5:0.000}({Name(maxThetaBone)})");
        for (int i = 0; i < _pins.Count; i++)
        {
            var (bone, target) = _pins[i];
            Vector2 tip = _scratch.WorldOf(bone).Translation;
            tip.X += _solveVars[IdxDx]; tip.Y += _solveVars[IdxDy];
            sb.Append($"  | pin[{Name(bone)}] reach={(tip - target).Length(),5:0.00}px");
        }
        if (_surfaces.Count > 0)
        {
            // Largest residual remaining over the no-penetration rows (= worst √w·penetration
            // the solve couldn't push out). The rows sit right after contacts+pins in `r`.
            float maxPen = 0f;
            int s0 = (_contacts.Count + _pins.Count) * 2, sN = s0 + _surfaces.Count * bones;
            for (int i = s0; i < sN && i < m; i++) maxPen = MathF.Max(maxPen, MathF.Abs(r[i]));
            sb.Append($"  | nopen maxResid={maxPen,5:0.00}");
        }
        if (_aimActive)
        {
            // The solved aim vector vs the target û* — the angle error left after the solve.
            Vector2 v = _scratch.WorldOf(_aimBoneR).Translation - _scratch.WorldOf(_aimBoneL).Translation;
            float ang = MathF.Atan2(v.X * _aimTarget.Y - v.Y * _aimTarget.X, v.X * _aimTarget.X + v.Y * _aimTarget.Y);
            sb.Append($"  | aim errDeg={ang * 180f / MathF.PI,6:0.0}");
        }
        return sb.ToString();

        string Name(int b) => b >= 0 && b < bones ? _skeleton.Bones[b].Name : "-";
    }

    // DIAGNOSTIC (tests/tuning): the world far-tip of `bone` at the accepted solve point of the
    // last frame, including the body offset d = (d.x, δ) — i.e. where the solver actually placed
    // that tip. Rebuilds the pose at _solveVars under the last solve root. Vector2.Zero if no solve ran.
    internal Vector2 SolvedBoneTipWorld(int bone)
    {
        if (!_haveCorr) return Vector2.Zero;
        BuildSolvePose(_solveVars.AsSpan(0, IdxTheta0 + _skeleton.Count));
        Vector2 tip = _scratch.WorldOf(bone).Translation;
        tip.X += _solveVars[IdxDx];
        tip.Y += _solveVars[IdxDy];
        return tip;
    }

    // TEST/DIAGNOSTIC: the signed angle (radians) between the solved aim vector (right hand − left
    // hand at the accepted solve point) and the frozen target û* — i.e. how far off the re-aim
    // landed. ~0 means the aim constraint reached its target. NaN when no aim solve ran this frame.
    internal float AimAngleError()
    {
        if (!_haveCorr || !_aimActive) return float.NaN;
        Vector2 v = SolvedBoneTipWorld(_aimBoneR) - SolvedBoneTipWorld(_aimBoneL);   // δ cancels in the difference
        Vector2 u = _aimTarget;
        return MathF.Atan2(v.X * u.Y - v.Y * u.X, v.X * u.X + v.Y * u.Y);
    }

    // A per-frame snapshot of the solve for external inspection tools (the offline take
    // viewer draws these as overlays: contact markers scaled by weight, pin targets,
    // no-pen surfaces, the solved offsets). Everything is copied out — safe to hold
    // across frames while the animator keeps running.
    public sealed class AnimFrameDebug
    {
        public struct ContactDbg { public string Bone; public Vector2 Target; public float Weight; }
        public struct PinDbg     { public string Bone; public Vector2 Target; }

        public bool         Solved;          // an LM solve ran this frame (offsets/Δθ valid)
        public string       Clip;            // locomotion clip of the solve (null when none)
        public float        Phase;           // entry phase φ of the solve
        public float        DPhi, Dy, Dx;    // solved Δφ, δ (vertical), d.x (horizontal)
        public float        MaxDTheta;       // largest |Δθ| over bones
        public string       MaxDThetaBone;
        public ContactDbg[] Contacts;        // planted contacts with their FROZEN solve weights
        public PinDbg[]     Pins;            // external fixed-point pins
        public SolverSurface[] Surfaces;     // no-penetration half-planes
        public bool         AimActive;
        public Vector2      AimTarget;       // frozen û* of the aim row
        public float        AimErrDeg;       // solved aim error, degrees (0 when inactive)
    }

    public AnimFrameDebug CaptureDebug()
    {
        var d = new AnimFrameDebug
        {
            Solved   = _haveCorr,
            Clip     = _solveClip?.Name,
            Phase    = _solvePhi,
            Contacts = new AnimFrameDebug.ContactDbg[_contacts.Count],
            Pins     = new AnimFrameDebug.PinDbg[_pins.Count],
            Surfaces = _surfaces.ToArray(),
        };
        for (int i = 0; i < _contacts.Count; i++)
            d.Contacts[i] = new AnimFrameDebug.ContactDbg
            { Bone = BoneName(_contacts[i].Bone), Target = _contacts[i].Target, Weight = _contacts[i].Weight };
        for (int i = 0; i < _pins.Count; i++)
            d.Pins[i] = new AnimFrameDebug.PinDbg
            { Bone = BoneName(_pins[i].bone), Target = _pins[i].target };

        if (_haveCorr)
        {
            d.DPhi = _solveVars[IdxPhi];
            d.Dy   = _solveVars[IdxDy];
            d.Dx   = _solveVars[IdxDx];
            for (int b = 0; b < _skeleton.Count; b++)
            {
                float a = MathF.Abs(_solveVars[IdxTheta0 + b]);
                if (a > d.MaxDTheta) { d.MaxDTheta = a; d.MaxDThetaBone = BoneName(b); }
            }
            d.AimActive = _aimActive;
            if (_aimActive)
            {
                d.AimTarget = _aimTarget;
                float err = AimAngleError();
                d.AimErrDeg = float.IsNaN(err) ? 0f : err * 180f / MathF.PI;
            }
        }
        return d;

        string BoneName(int b) => b >= 0 && b < _skeleton.Count ? _skeleton.Bones[b].Name : "?";
    }

    // Keyframe interval index of normalized time t (for the FD oracle's boundary guard): the
    // bracketing interval [i, i+1], or -2 outside [first,last] — a non-cyclic clamped end
    // region (held pose) or an open-tail loop's wrap segment (one region either way, so the
    // straddle guard still catches probes crossing into/out of it).
    private static int IntervalAt(AnimationDocument doc, float t)
    {
        var ks = doc?.Keyframes;
        if (ks == null || ks.Count < 2) return -1;
        if (t <= ks[0].Time || t >= ks[ks.Count - 1].Time) return -2;
        int i = 0;
        while (i < ks.Count - 1 && ks[i + 1].Time < t) i++;
        return i;
    }

    // Central-difference step for the Δφ column, scaled to the interval bracketing t: 2% of
    // the interval width keeps the cubic's relative truncation ~0.04% while the floor stays
    // above float32 cancellation noise. Held clamp regions (and docs with no interval to
    // measure) keep the legacy 1e-3 (the pose is flat there anyway).
    private static float PhiFdStep(AnimationDocument doc, float t)
    {
        var ks = doc?.Keyframes;
        float w = 1f;
        if (ks != null && ks.Count >= 2)
        {
            int i = IntervalAt(doc, t);
            if (i >= 0) w = ks[i + 1].Time - ks[i].Time;
            else if (i == -2 && AnimationSampler.IsCyclic(doc) && AnimationSampler.HasOpenTail(doc))
                w = ks[0].Time + 1f - ks[ks.Count - 1].Time;   // the wrap segment
        }
        return MathHelper.Clamp(0.02f * w, 1e-4f, 1e-3f);
    }

}
