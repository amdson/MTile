using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// The clips this first-draft animator can play. Selection is derived purely from
// the observed CharacterAnimSample — never pushed by the sim.
// Walk vs WalkBack distinguishes moving with vs against the facing direction
// (forward stride vs backpedal). Run is forward locomotion above a speed threshold
// (a longer-stride clip — same cadence machinery). Air is split into Jump (rising)
// and Fall. Vault covers the guided ParkourState traversal.
public enum AnimClip { Idle, Walk, WalkBack, Crouch, Jump, Fall, Vault, Run, WallSlide }

// The animation-side state, deliberately separate from any character/sim state.
// The animator owns and evolves this; it is the "previous state" the animator is
// allowed to remember between frames (alongside the previous sample).
public struct CharacterAnimState
{
    public AnimClip Clip;         // currently-selected clip
    public float    ClipTime;     // seconds spent in the current clip
    public float    Phase;        // locomotion cycle phase, wrapped to [0,1)
    public float    LandSquash;   // 1 on touchdown, decays to 0 — drives a landing squash
    public float    ActionWeight; // eased 0..1 blend of the action overlay layer
}

// Drives a skeleton from a character's observed motion. Pure pull model and
// render-only: Update() reads a CharacterAnimSample, evolves the animation state,
// builds a target pose, and eases the live pose toward it. It NEVER writes back to
// the character — movement/action stay agnostic to animation entirely.
// The least-squares solver's residual/Jacobian machinery — the constraint library
// (ISolveConstraint + the blocks), the shared point-Jacobian primitive, and the rotation
// lever arm — lives in the partial CharacterAnimator.Constraints.cs.
public sealed partial class CharacterAnimator
{
    // --- tuning (first-draft constants; no real velocity matching yet) ---
    private const float WalkSpeedThreshold = 12f;    // px/s before Idle -> Walk
    private const float RunSpeedThreshold  = 40f;    // px/s before Walk -> Run (MaxWalkSpeed is 100)
    private const float PhasePerPixel       = 0.010f; // legacy fallback: cycles/sec per px/s
    private const float IdleBobHz           = 0.30f;  // breathing cycles/sec
    private const float Stiffness           = 20f;    // pose-follow rate (1/sec)
    // Upper body (chest subtree: arms + knife) eases far faster *while an action
    // overlay is active*, ramped in by ActionWeight. A slash is ~0.14s with sub-20ms
    // swing segments; the base 20/s (50ms τ) low-passes ~70% of that authored range
    // away. ~90/s (≈11ms τ) passes ~90% so the rendered hand — and the knife glow
    // welded to it — tracks the real attack. Gated by ActionWeight so locomotion's
    // softer arm follow is untouched; only attacks snap.
    private const float UpperBodyStiffness  = 90f;
    private const float WalkLean            = 0.25f;  // torso lean at full walk speed
    private const float WalkLeanRefSpeed    = 160f;   // px/s at which lean reaches max

    // --- cadence / IK solver ---
    // All solver weights + box limits live in AnimSolverConfig (hot-reloadable; the solve is
    // render-only so there's no determinism risk). Read as AnimSolverConfig.Current.X. See the
    // weight TIERS / per-region pose-prior rationale documented there and in §11.4.

    // --- action overlay ---
    // Asymmetric on purpose: GroundSlash1 lasts ~0.14s, so the overlay must register
    // almost immediately; the slow ease-out keeps the arm raised across the brief
    // ReadyAction gaps between combo hits instead of dipping back to the walk pose.
    private const float ActionEaseIn  = 25f;  // 1/s — overlay weight rise rate
    private const float ActionEaseOut = 8f;   // 1/s — overlay weight release rate

    // --- plant-foot debug marker ---
    private const  float PlantFootMarkerRadius = 1.2f;
    private static readonly Color PlantFootMarkerColor = Color.Lime;

    private readonly Skeleton     _skeleton;
    private readonly float        _scale;   // rig→world scale; the solve needs it, not just Draw
    private readonly SkeletonPose _pose;    // live output, eased each frame
    private readonly SkeletonPose _target;  // target assembled this frame
    private readonly SkeletonPose _kfA, _kfB, _kfC, _kfD;   // scratch for the C1 keyframe quad (iL,i0,i1,iR)
    private readonly SkeletonPose _scratch;     // scratch for the cadence loss evaluation

    // A planted contact the cadence solver pins this frame: a bone whose tip should
    // stay at Target (world). Captured when the label appears, held until it drops.
    private struct ActiveContact
    {
        public int           Bone;
        public Vector2       Target;
        public float         Weight;
        public ContactSource Source;
    }
    private readonly List<ActiveContact> _contacts = new();
    // External fixed-point pins resolved from this frame's sample (bone index + world target).
    // Held at the HARD tier by FixedPointConstraint; frozen for the duration of one solve.
    private readonly List<(int bone, Vector2 target)> _pins = new();
    private const int MaxPins = 4;   // sizes the residual scratch; excess pins are dropped
    // No-penetration half-planes resolved from this frame's sample. Frozen for one solve; each
    // emits one row per rig bone (NoPenetrationConstraint) — the limbs the solver pushes out.
    private readonly List<SolverSurface> _surfaces = new();
    private const int MaxSurfaces = 4;   // sizes the residual scratch; excess surfaces are dropped
    private readonly List<(int bone, float weight)> _weightBuf = new();   // scratch for feathered weights
    private float _prevPhaseStep;   // Δφ_prev for the momentum prior

    // Authored clips keyed by category, matched from the loaded animations' Type.
    // When a clip has an authored animation it plays that; otherwise the procedural
    // builder below is the fallback.
    private readonly Dictionary<AnimClip, AnimationDocument> _clips = new();

    // Action overlay clips, keyed by exact action class name (AnimationDocument.Type
    // that fails the AnimClip parse, e.g. "GroundSlash1"). Fixed-rate overlays carrying
    // no contact labels of their OWN — but they ARE composed into the pose the cadence
    // solve optimizes (post-blend, Phase 4.5), so the feet the solver pins are the feet
    // of the blended skeleton, not the bare locomotion clip.
    private readonly Dictionary<string, AnimationDocument> _actionClips = new(StringComparer.Ordinal);
    private readonly bool[][]     _regionMasks;   // per-AnimRegion bone masks, resolved once
    private readonly bool[]       _upperMask;     // chest subtree — bones that snap during attacks
    private readonly float[]      _blend;         // scratch: per-bone ease factor each frame

    // Overlay motion layers composed onto the base pose (Phase 4), generalized from a single
    // Action overlay into an ordered STACK so a movement-sourced overlay (e.g. parkour hands)
    // can co-exist with an Action overlay (e.g. a slash). Slot 0 is PRIVILEGED — the Action-FSM
    // overlay that drives ActionWeight / OverlayActive / the knife pose; slots 1+ are sourced
    // from the MovementState by ResolveMovementOverlays. They paint in order (ComposeOverlays);
    // disjoint masks compose trivially, a shared bone resolves by paint order × weight. The base
    // layer's surviving coefficient is the per-bone product Π(1−w) over the slots masking it —
    // cached in _baseBlend, which the analytic Jacobian scales each base-driven column by.
    private sealed class OverlaySlot
    {
        public string             Key;        // source id (action name / movement clip name); null = idle
        public AnimationDocument  Clip;       // target clip this frame; null = idle / fading out
        public bool[]             Mask;       // Region bone mask (held through the fade-out)
        public float              OffWeight;  // Clip.OffRegionWeight: weight for bones outside Mask (0 = hard mask)
        public float              Weight;     // eased opacity 0..1
        public bool               Active;     // composed this frame (Weight>eps && Mask!=null)
        public readonly SkeletonPose Pose;    // last-sampled overlay pose (persisted for the fade-out)
        public readonly float[]   BoneWeight; // per-bone opacity ((Mask?1:OffWeight)*Weight) — compose scratch
        public OverlaySlot(Skeleton rig) { Pose = rig.CreatePose(); BoneWeight = new float[rig.Count]; }
    }
    private const int   OverlaySlotCount = 3;       // Action (0) + two movement overlays
    private const float OverlayWeightEps = 1e-3f;
    private readonly OverlaySlot[] _slots;
    private OverlaySlot ActionSlot => _slots[0];
    private readonly float[] _baseBlend;            // per-bone Π(1−w) of the active slots masking the bone
    private bool _anyOverlay;                        // any slot composed this frame
    private readonly List<(string key, AnimationDocument clip, float tau)> _ovBuf = new();  // resolver scratch
    private readonly bool[] _ovClaimed = new bool[OverlaySlotCount];                          // request→slot assignment

    private CharacterAnimState  _state;
    private CharacterAnimSample _prev;      // previous frame's sample
    private bool _hasPrev;

    // The clip doc sampled this frame and the normalized time it was sampled at —
    // remembered so the host can pull labeled additions (e.g. the "com" reference
    // point) for the exact pose being drawn, after Update returns.
    private AnimationDocument _curDoc;
    private float             _curComT;

    // Generalized cadence solver (Plans/ANIMATION_SOLVER_PLAN.md, Phase 1). When on,
    // the phase advance Δφ comes from a Levenberg–Marquardt least-squares solve over
    // the SAME objective the golden-section path minimizes (horizontal foot no-slip +
    // playback continuity), proving the general machinery at parity before later phases
    // add joint corrections, a CoM offset, and more constraints. Off → the legacy 1-D
    // golden-section solve. Render-only either way.
    private readonly bool                _useSolver;
    private readonly LeastSquaresSolver  _ls;
    private readonly float[]             _solveVars, _solveLo, _solveHi;
    private readonly LeastSquaresSolver.ResidualFn _cadenceResiduals;
    private readonly LeastSquaresSolver.JacobianFn _cadenceJacobian;   // analytic J (replaces FD)
    private readonly float[]             _angVel;   // per-bone clip dθ/dt scratch for the Jacobian
    private readonly float[]             _colX, _colY;   // ∂p/∂x column scratch for the point Jacobian
    private readonly float[]             _colX2, _colY2;  // second point-Jacobian scratch (the aim's other hand)
    private readonly bool[]              _isCore;        // bone is torso (hip/chest/head) → stiff Tikhonov λ_θ
    private readonly float[]             _prevTheta;     // last frame's solved Δθ (temporal smoothness target)
    private readonly ISolveConstraint[]  _constraints;   // the composite objective, assembled in order
    // Per-solve context the residual closure reads (set just before each Minimize call).
    private AnimationDocument _solveClip;
    private float             _solvePhi;
    private Affine2           _solveRoot;
    private bool              _haveCorr;          // a Δθ-correction solve ran this frame

    // Action-aim state (the stab re-aim, §STAB_AIM_PLAN), resolved each frame in step 1.7 and
    // frozen for the solve. _aimTarget (û*) is captured once at solve start from the Δθ=0 pose.
    private bool    _aimActive;
    private Vector2 _aimDir;       // world input aim direction (unit) this frame
    private Vector2 _aimTarget;    // frozen target unit vector û* the live aim vector is driven onto
    private int     _aimFacing;    // facing the reference rotation is measured from
    private readonly int _aimBoneL, _aimBoneR;   // the L→R hand pair whose vector encodes the aim

    // Cached bone indices (resolved once).
    private readonly int _hip, _chest;

    public Skeleton           Skeleton => _skeleton;
    public SkeletonPose       Pose     => _pose;
    public CharacterAnimState State    => _state;

    // Per-bone angle correction Δθ (radians) the solver applied this frame, by bone
    // index — the IK channel on top of the authored blend. Zero on the golden path and
    // on frames with no cadence solve (flight / non-locomotion). Diagnostic + tests.
    public float AngleCorrection(int bone)
        => _haveCorr && bone >= 0 && bone < _skeleton.Count ? _solveVars[2 + bone] : 0f;

    // Solved vertical root offset δ (world px) to add on top of the host's baseline
    // placement (RigRoot) — the body's bob that keeps the planted foot grounded. Zero on
    // the golden path and on flight / non-locomotion frames (→ host baseline = com anchor).
    public float VerticalOffset => _haveCorr ? _solveVars[1] : 0f;

    // TEST HOOK: the largest absolute discrepancy between the analytic cadence Jacobian
    // (CadenceJacobian) and a central finite difference of CadenceResiduals, evaluated at
    // the live solve point of the last frame. ~0 confirms every analytic column. Returns
    // -1 when no cadence solve ran this frame (flight / non-locomotion / golden path).
    internal float MaxJacobianError()
    {
        if (!_haveCorr || _ls == null) return -1f;
        int bones = _skeleton.Count;
        int n = 2 + bones;
        // 2/contact + 2/pin + bones/surface (no-penetration) + 1 aim + continuity + com + bones×2 priors.
        int m = (_contacts.Count + _pins.Count) * 2 + _surfaces.Count * bones + (_aimActive ? 1 : 0) + 2 + 2 * bones;

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
                float gap = s.Normal.X * (tip.X - s.Point.X) + s.Normal.Y * (tip.Y + x[IdxDy] - s.Point.Y);
                if (MathF.Abs(s.Margin - gap) < kneeBand) skipRow[npRow] = true;
            }

        float worst = 0f;
        for (int j = 0; j < n; j++)
        {
            // Per-column FD step: Δφ (col 0) runs through the Hermite sample, whose cubic has
            // high curvature inside the short keyframe intervals, so it needs a SMALL h to
            // keep central-difference truncation (O(h²·f‴)) down; δ/Δθ are low-curvature but
            // their residuals are differences of large world coordinates, so they need a
            // LARGER h to clear the float32 cancellation floor.
            float h = j == 0 ? 1e-3f : 1e-2f;
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
        int m = geom + 2 + 2 * bones;

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
        sb.Append($"solved Δφ={_solveVars[IdxPhi],6:0.000} δ={_solveVars[IdxDy],6:0.00} |Δθ|max={maxTheta,5:0.000}({Name(maxThetaBone)})");
        for (int i = 0; i < _pins.Count; i++)
        {
            var (bone, target) = _pins[i];
            Vector2 tip = _scratch.WorldOf(bone).Translation; tip.Y += _solveVars[IdxDy];
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
    // last frame, including the body bob δ — i.e. where the solver actually placed that tip.
    // Rebuilds the pose at _solveVars under the last solve root. Vector2.Zero if no solve ran.
    internal Vector2 SolvedBoneTipWorld(int bone)
    {
        if (!_haveCorr) return Vector2.Zero;
        BuildSolvePose(_solveVars.AsSpan(0, IdxTheta0 + _skeleton.Count));
        Vector2 tip = _scratch.WorldOf(bone).Translation;
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

    // Keyframe interval index of normalized time t (for the FD oracle's boundary guard): the
    // bracketing interval [i, i+1], or -2 in a non-cyclic clamped end region (held pose).
    private static int IntervalAt(AnimationDocument doc, float t)
    {
        var ks = doc?.Keyframes;
        if (ks == null || ks.Count < 2) return -1;
        if (t <= ks[0].Time || t >= ks[ks.Count - 1].Time) return -2;
        int i = 0;
        while (i < ks.Count - 1 && ks[i + 1].Time < t) i++;
        return i;
    }

    // Lowest point (max local Y; Y is down) of the *current* eased pose, in skeleton-
    // local units — the live "sole" line. A host places the rig so this rests on the
    // ground each frame (rootY = groundY - CurrentSoleY()*scale) so a swinging/arcing
    // foot never punches through the floor. Recomputes the live pose's world buffer
    // under identity; the subsequent Draw recomputes it under the real root.
    public float CurrentSoleY()
    {
        var w = _pose.ComputeWorld(Affine2.Identity);
        float sole = 0f;
        // world[i].Translation is each bone's far end (and every joint) under the R·T·S chain,
        // so the sole is simply the lowest of those — no +Length tip term (it overshoots a bone).
        for (int i = 0; i < _skeleton.Count; i++)
            sole = MathF.Max(sole, w[i].Translation.Y);
        return sole;
    }

    public CharacterAnimator(Skeleton skeleton, float scale, IEnumerable<AnimationDocument> animations = null,
                             bool useSolver = false)
    {
        // Materialize once: the list is walked twice (compose the rig, then bind clips).
        var anims = animations == null ? null
                  : animations as IReadOnlyList<AnimationDocument> ?? new List<AnimationDocument>(animations);
        // Layer in clip-local attachment bones (e.g. a slash's knife) so the rig can
        // resolve them; the base Skeletons/*.json stays free of attack-specific bones.
        var rig = SkeletonComposition.WithClipBones(skeleton, anims);

        _skeleton = rig;
        _scale    = scale;
        _pose     = rig.CreatePose();
        _target   = rig.CreatePose();
        _kfA      = rig.CreatePose();
        _kfB      = rig.CreatePose();
        _kfC      = rig.CreatePose();
        _kfD      = rig.CreatePose();
        _scratch  = rig.CreatePose();

        _regionMasks = new bool[3][];
        foreach (AnimRegion r in Enum.GetValues<AnimRegion>())
            _regionMasks[(int)r] = BoneMask.Resolve(rig, r);
        _upperMask = _regionMasks[(int)AnimRegion.UpperBody];
        _blend     = new float[rig.Count];
        _baseBlend = new float[rig.Count];
        _slots = new OverlaySlot[OverlaySlotCount];
        for (int i = 0; i < _slots.Length; i++) _slots[i] = new OverlaySlot(rig);

        int I(string n) => rig.IndexOf(n);
        _hip = I("hip"); _chest = I("chest");
        _aimBoneL = I("arm_l_lower"); _aimBoneR = I("arm_r_lower");   // the stab-aim hand pair

        // Sized for Phase 1 (one Δφ variable) with headroom for later phases (joint
        // corrections, a 2-D CoM offset → ~16 variables; a handful of contact residuals
        // + regularizers → ~16 rows). Allocated only when the solver path is active.
        _useSolver = useSolver;
        if (_useSolver)
        {
            // Variables: Δφ (1) + δ vertical offset (1) + per-bone Δθ (rig.Count), with a
            // little headroom. Residuals: two rows per contact (H no-slip + V ground) + two per
            // external pin + continuity + com + one prior per bone. Sized to the rig once.
            int nv = 2 + rig.Count + 2;
            // 2/contact + 2/pin + (MaxSurfaces × bones) no-penetration + 1 aim + continuity + com
            // + bones Tikhonov + bones Δθ-smoothness.
            int nr = 2 * 4 + 2 * MaxPins + MaxSurfaces * rig.Count + 1 + 2 + 2 * rig.Count + 2;
            _ls = new LeastSquaresSolver(maxVars: nv, maxRes: nr);
            _solveVars = new float[nv];
            _solveLo   = new float[nv];
            _solveHi   = new float[nv];
            _angVel    = new float[rig.Count];
            _colX      = new float[nv];
            _colY      = new float[nv];
            _colX2     = new float[nv];
            _colY2     = new float[nv];
            // Which bones are torso (stiff Tikhonov λ_θ from config) vs limb (loose). Structural —
            // the WEIGHTS live in AnimSolverConfig so they hot-reload; this just tags the bones.
            _isCore = new bool[rig.Count];
            for (int i = 0; i < rig.Count; i++)
            {
                string nm = rig.Bones[i].Name;
                _isCore[i] = nm == "hip" || nm == "chest" || nm == "head";
            }
            _prevTheta = new float[rig.Count];
            _cadenceResiduals = CadenceResiduals;
            _cadenceJacobian  = CadenceJacobian;
            // The composite objective as an ordered list of constraints (§11). The order is
            // load-bearing: it IS the residual/Jacobian row order the LM core and the
            // FD-vs-analytic oracle assume. Preallocated once → zero per-frame allocation.
            _constraints = new ISolveConstraint[]
            {
                new PlantedContactsConstraint(this),   // 2 rows/contact: H no-slip (Δφ) + V ground hold (δ)
                new FixedPointConstraint(this),        // 2 rows/pin: both-axis hard external pin (Δθ IK)
                new NoPenetrationConstraint(this),     // 1 row/(surface×bone): half-plane limb push-out (Δθ/δ)
                new ActionAimConstraint(this),         // 1 row: re-aim the action overlay along the input dir (Δθ)
                new PlaybackContinuityConstraint(this),// 1 row: Δφ momentum prior
                new ComOffsetConstraint(this),         // 1 row: soft com pulls δ→baseline
                new PosePriorConstraint(this),         // N rows: Tikhonov on each Δθ (toward 0)
                new ThetaSmoothnessConstraint(this),   // N rows: pull each Δθ toward last frame (temporal)
            };
        }

        // Bind each clip category to the first authored animation whose Type matches
        // the enum name (case-insensitive) AND whose Skeleton matches this rig.
        // Mismatched-rig clips are dropped silently — a level with multiple character
        // archetypes shares the SkeletonStates/ pool and each animator picks its own.
        // Types that aren't an AnimClip are action overlays, keyed by exact name;
        // stray types ("Misc") land there harmlessly — no action ever looks them up.
        if (anims != null)
            foreach (var anim in anims)
            {
                if (anim.Skeleton != rig.Name) continue;
                if (Enum.TryParse<AnimClip>(anim.Type, ignoreCase: true, out var clip))
                {
                    if (!_clips.ContainsKey(clip)) _clips[clip] = anim;
                }
                else if (anim.Type != null && !_actionClips.ContainsKey(anim.Type))
                    _actionClips[anim.Type] = anim;
            }
    }

    public void Update(in CharacterAnimSample s)
    {
        float dt = s.Dt;
        _haveCorr = false;   // cleared until a cadence solve produces Δθ this frame

        // 0. Use the previous frame's state: detect a touchdown (was airborne, now
        //    grounded) and arm a landing squash that decays over the next frames.
        if (_hasPrev && !_prev.Grounded && s.Grounded) _state.LandSquash = 1f;
        _state.LandSquash = MathF.Max(0f, _state.LandSquash - dt * 4f);

        // 1. Select a clip from observed state only.
        AnimClip clip = SelectClip(in s);
        if (clip != _state.Clip)
        {
            _state.Clip = clip;
            _state.ClipTime = 0f;
            _contacts.Clear();        // contacts belong to the clip that just ended
            _prevPhaseStep = 0f;
            if (_prevTheta != null) Array.Clear(_prevTheta, 0, _prevTheta.Length);   // no Δθ history across a clip switch
        }
        else _state.ClipTime += dt;

        float speed   = MathF.Abs(s.Velocity.X);
        bool hasClip  = _clips.TryGetValue(clip, out var anim);
        bool locomotion = clip == AnimClip.Walk || clip == AnimClip.WalkBack || clip == AnimClip.Run;

        // 1.5 Resolve the action overlay NOW, before the cadence solve, so the solve
        //     optimizes the POST-BLEND skeleton — the feet of the composed pose, not the
        //     bare locomotion clip. An overlay is a second motion sampled at its pinned τ
        //     (= action progress, §9 Q1) with a per-bone opacity = Region mask × eased
        //     ActionWeight. Both are CONSTANT w.r.t. the solve vars, so we sample once here
        //     and the residual just re-applies the same linear blend (ComposeOverlays)
        //     before FK; the Jacobian likewise scales each base-layer column by (1−w). The
        //     same _actionPose/_overlayWeight feed the draw in step 3.5, so the skeleton the
        //     solver optimized is bit-identical to the one rendered.
        // -- slot 0: the Action-FSM overlay. τ = action progress, time-remapped onto the
        //    declared ActionDuration (sweeps once over the swing, holds past the end) or the
        //    clip's own seconds when no length is declared. --
        string actKey = IsOverlayAction(s.Action) ? s.Action : null;
        AnimationDocument actClip =
            actKey != null && _actionClips.TryGetValue(actKey, out var ac) ? ac : null;
        BindSlot(ActionSlot, actKey, actClip);
        float actTau = actClip == null ? 0f
            : s.ActionDuration > 1e-4f ? MathHelper.Clamp(s.ActionTime / s.ActionDuration, 0f, 1f)
            : AnimationSampler.NormalizedTime(actClip, s.ActionTime);
        EaseSlot(ActionSlot, actClip, actTau, dt);

        // -- slots 1+: movement-sourced overlays (parkour hands, …), resolved animation-side
        //    from the MovementState. Matched to slots by key so an in-progress fade stays on
        //    its own slot; a slot with no request fades out holding its last pose/mask. --
        _ovBuf.Clear();
        ResolveMovementOverlays(in s, _ovBuf);
        Array.Clear(_ovClaimed, 0, _ovClaimed.Length);
        for (int si = 1; si < _slots.Length; si++)
        {
            int req = -1;
            // prefer the request already on this slot (key match) to keep the weight continuous
            for (int k = 0; k < _ovBuf.Count && k < _ovClaimed.Length; k++)
                if (!_ovClaimed[k] && string.Equals(_ovBuf[k].key, _slots[si].Key, StringComparison.Ordinal))
                { req = k; break; }
            if (req < 0 && _slots[si].Weight <= OverlayWeightEps)   // else adopt a new request on an idle slot
                for (int k = 0; k < _ovBuf.Count && k < _ovClaimed.Length; k++)
                    if (!_ovClaimed[k]) { req = k; break; }
            if (req >= 0)
            {
                _ovClaimed[req] = true;
                BindSlot(_slots[si], _ovBuf[req].key, _ovBuf[req].clip);
                EaseSlot(_slots[si], _ovBuf[req].clip, _ovBuf[req].tau, dt);
            }
            else
            {
                BindSlot(_slots[si], _slots[si].Key, null);   // fade out (holds mask/pose)
                EaseSlot(_slots[si], null, 0f, dt);
            }
        }

        // The Action slot's eased weight is the public ActionWeight (upper-body stiffness ramp
        // + tests). Then cache the per-bone base-layer survival Π(1−w) and the any-overlay flag
        // that ComposeOverlays / the Jacobian read this frame.
        _state.ActionWeight = ActionSlot.Weight;
        _anyOverlay = false;
        for (int i = 0; i < _skeleton.Count; i++) _baseBlend[i] = 1f;
        foreach (var slot in _slots)
        {
            if (!slot.Active) continue;
            _anyOverlay = true;
            // Per-bone survival of the base layer = Π(1−w) over slots, using each bone's
            // GRADED weight (BoneWeight, freshly filled in EaseSlot above) so a graded
            // off-region overlay correctly tells the cadence Jacobian the legs are only
            // partly overridden. Off-region bones at OffWeight=0 contribute *=1 (no-op),
            // reproducing the old hard-mask product exactly.
            for (int i = 0; i < _skeleton.Count; i++)
                _baseBlend[i] *= 1f - slot.BoneWeight[i];
        }

        // 1.7 Resolve this frame's external pins (sample → bone-index targets) so the solve's
        //     FixedPointConstraint can read them. Frozen here for the whole solve. Unknown bone
        //     names and excess pins (> MaxPins) are dropped rather than reallocating the scratch.
        _pins.Clear();
        if (s.Pins != null)
            foreach (var pin in s.Pins)
            {
                if (_pins.Count >= MaxPins) break;
                int b = _skeleton.IndexOf(pin.Bone);
                if (b >= 0) _pins.Add((b, pin.Target));
            }
        ResolveMovementPins(in s);
        _surfaces.Clear();
        if (s.Surfaces != null)
            foreach (var srf in s.Surfaces)
            {
                if (_surfaces.Count >= MaxSurfaces) break;
                _surfaces.Add(srf);
            }
        ResolveActionAim(in s);

        // 2. Advance the locomotion phase. A Walk/WalkBack clip with contact labels is
        //    cadence-driven: the solver picks Δφ so the planted foot doesn't slip
        //    against the body's real motion. Everything else keeps the old rate.
        if (locomotion && hasClip && HasContacts(anim))
        {
            int dir = s.Facing == 0 ? 1 : s.Facing;
            // Solve-root: hip placed at the body center, plus — on the solver path — the
            // com baseline Draw uses (rootY = BodyY − com.Y·scale), so contact targets are
            // captured at the SAME height the pose is drawn and the solved δ perturbs about
            // it. The horizontal no-slip is unaffected by the Y shift, and the golden path
            // ignores target.Y, so this changes nothing for the legacy path. scale/facing
            // match Draw so foot travel and body motion share world units.
            float comBaseY = 0f;
            if (_useSolver && SampleNamedPoint(anim, _state.Phase, "com", out var comL))
                comBaseY = -comL.Y * _scale;
            var root = Affine2.FromTRS(new Vector2(s.Position.X, s.Position.Y + comBaseY), 0f,
                                       new Vector2(dir * _scale, _scale));
            RefreshContacts(anim, _state.Phase, root);
            if (_contacts.Count > 0)
            {
                float dphi = _useSolver ? SolvePhaseStepLm(anim, _state.Phase, root)
                                        : SolvePhaseStep(anim, _state.Phase, root);
                _state.Phase   = Wrap01(_state.Phase + dphi);
                _prevPhaseStep = dphi;
            }
            else
            {
                // Flight: a run's no-contact window has no planted foot to pin against,
                // so there's nothing for the cadence solver to do. Coast the cycle at the
                // last solved step's momentum (falling back to the distance-based rate on
                // a cold entry) so the swing keeps moving until the next foot plants and
                // the solver re-engages — otherwise the phase would freeze mid-air.
                _state.Phase = Wrap01(_state.Phase +
                    (_prevPhaseStep > 1e-5f ? _prevPhaseStep : speed * dt * PhasePerPixel));
            }
        }
        else
        {
            _contacts.Clear();
            if (locomotion)            _state.Phase = Wrap01(_state.Phase + speed * dt * PhasePerPixel);
            else if (clip == AnimClip.Idle) _state.Phase = Wrap01(_state.Phase + dt * IdleBobHz);
        }

        // 2.5 Off-locomotion solve (Phase 3): the cadence path above only runs the LM solve
        //     for a locomotion clip with planted contacts. But external pins and no-penetration
        //     surfaces (wall-slide grip/limbs) must engage on ANY clip. So when no cadence solve
        //     ran this frame yet there IS something external to satisfy, run a STATIC solve —
        //     Δφ locked (no cadence here), only δ + the per-bone Δθ move, bending the limbs to
        //     respect the pins/surfaces while the body bob settles. Behaviour-preserving for
        //     normal locomotion: there _haveCorr is already true (cadence solved), and during
        //     plain flight/idle there are no pins/surfaces, so this is skipped.
        if (_useSolver && !_haveCorr && hasClip && (_pins.Count > 0 || _surfaces.Count > 0 || _aimActive))
            SolveStaticPose(anim, clip, in s);

        // 3. Build the target pose. Locomotion + idle sample by phase (so cadence
        //    drives the pose); one-shot clips sample by ClipTime. Every clip category
        //    must have an authored file bound — no procedural fallback.
        if (!hasClip)
            throw new InvalidOperationException(
                $"No authored animation bound for clip '{clip}'. Add a SkeletonStates/*.json " +
                $"with Type=\"{clip}\" (loaded into CharacterAnimator).");

        _curDoc  = anim;
        _curComT = IsPhaseDriven(clip) ? _state.Phase
                                       : AnimationSampler.NormalizedTime(anim, _state.ClipTime);

        if (IsPhaseDriven(clip))
            AnimationSampler.SampleSmooth(anim, _state.Phase, _kfA, _kfB, _kfC, _kfD, _target);
        else
            AnimationSampler.SampleSmooth(anim, AnimationSampler.NormalizedTime(anim, _state.ClipTime),
                                          _kfA, _kfB, _kfC, _kfD, _target);

        // 3.5 Paint the resolved action overlay onto the base pose (Phase 4 motion layer).
        //     The overlay was resolved in step 1.5 — bound clip, sampled _actionPose at its
        //     pinned τ, eased ActionWeight, per-bone opacity _overlayWeight — and composed
        //     identically inside the cadence solve (ComposeOverlays), so the skeleton the
        //     solver optimized is the one drawn. Runs BEFORE lean/squash so those additive
        //     deltas stay continuous in the weight (run-slash keeps its lean; landing
        //     mid-air-slash still squashes).
        ComposeOverlays(_target);

        // Apply the solver's per-bone angle corrections onto the COMPOSED pose (matching
        // BuildSolvePose's order, so the drawn skeleton is the one the solver optimized). Δθ
        // post-compose means a pin can bend an overlay-owned bone (the vault hand). While only
        // the Tikhonov prior touches Δθ they're ≈ 0 and this is a no-op.
        if (_haveCorr)
            for (int i = 0; i < _skeleton.Count; i++)
                _target.Local[i].Rotation += _solveVars[2 + i];

        // 3b. Directional lean for locomotion — applied on top of the base pose
        //     (authored OR procedural) so forward/backpedal read distinctly: lean
        //     into travel when walking forward, lean back when backpedaling.
        if (clip == AnimClip.Walk || clip == AnimClip.WalkBack || clip == AnimClip.Run)
        {
            float lean = (clip == AnimClip.WalkBack ? -1f : 1f)
                       * WalkLean * MathHelper.Clamp(speed / WalkLeanRefSpeed, 0f, 1f);
            Rot(_chest, lean);
        }

        // 3c. Landing squash on top of any clip: flatten + sink briefly on touchdown.
        if (_state.LandSquash > 0f)
        {
            float k = _state.LandSquash;
            Scale(_hip, new Vector2(0.35f * k, -0.35f * k));
            Translate(_hip, new Vector2(0f, 3f * k));
        }

        // 4. Ease the live pose toward the target (framerate-independent). Per-bone:
        //    the upper-body subtree stiffens with ActionWeight so an attack's fast swing
        //    isn't low-passed away (and the knife glow, read from this live pose, stays
        //    welded to the rendered hand); everything else keeps the soft locomotion rate.
        float baseB  = 1f - MathF.Exp(-Stiffness * dt);
        float upperK = Stiffness + (UpperBodyStiffness - Stiffness) * _state.ActionWeight;
        float upperB = 1f - MathF.Exp(-upperK * dt);
        for (int i = 0; i < _skeleton.Count; i++)
            _blend[i] = _upperMask[i] ? upperB : baseB;
        _pose.BlendToward(_target, _blend);

        _prev = s;
        _hasPrev = true;
    }

    // Render the eased pose at the character's world position. The rig→world scale is
    // the one the constructor was given (shared with the cadence solve); facing flips X.
    //   drawJoints         — draw the joint node discs (off → bones only).
    //   highlightPlantFoot — mark the foot the cadence solver is currently pinning.
    public void Draw(DrawContext ctx, Vector2 worldPos, int facing,
                     bool drawJoints = true, bool highlightPlantFoot = false)
    {
        int dir = facing == 0 ? 1 : facing;
        var root = Affine2.FromTRS(worldPos, 0f, new Vector2(dir * _scale, _scale));

        var style = SkeletonDrawStyle.Default;
        if (!drawJoints) style.JointRadius = 0f;
        SkeletonRenderer.Draw(ctx, _pose, root, style);   // leaves _pose world valid for `root`

        if (highlightPlantFoot)
            foreach (var c in _contacts)
            {
                Vector2 tip = _pose.WorldOf(c.Bone).Translation;   // bone's far end = contact tip
                ctx.Disc(tip, PlantFootMarkerRadius, PlantFootMarkerColor);
            }
    }

    // Whether an action overlay clip is currently bound and playing (vs faded out).
    public bool OverlayActive => ActionSlot.Clip != null;

    // World position of a named bone's origin under the same root Draw() uses, WITHOUT
    // drawing the rig — lets a host anchor a render effect (e.g. the slash glow) to an
    // animated bone. `fromOverlay` reads the RAW action-overlay pose (the authored
    // attack trajectory at ActionTime, full weight, no pose-smoothing) instead of the
    // eased live pose, so a glow shows the full motion the clip encodes even though the
    // visible rig eases/lags; it falls back to the live pose when no overlay is active.
    // false if the bone is absent. Pure pull / render-only.
    public bool TryBoneOrigin(string name, Vector2 worldPos, int facing,
                              out Vector2 origin, bool fromOverlay = false)
    {
        int b = _skeleton.IndexOf(name);
        if (b < 0) { origin = worldPos; return false; }
        int dir = facing == 0 ? 1 : facing;
        var root = Affine2.FromTRS(worldPos, 0f, new Vector2(dir * _scale, _scale));
        var pose = (fromOverlay && ActionSlot.Clip != null) ? ActionSlot.Pose : _pose;
        origin = pose.ComputeWorld(root)[b].Translation;
        return true;
    }

    // The clip's bundled center-of-mass reference point (the "com" Point addition),
    // in rig-local space, sampled at the pose drawn this frame. This is the anchor a
    // host maps onto the character's physics body (its polygon centroid = the real
    // COM) to place the rig — replacing the ad-hoc "drop until the lowest foot touches
    // the ground" rule, which can't ever let both feet leave the ground (a run's flight
    // phase). Returns false for clips that don't author one (the host then falls back).
    public bool TryComReference(out Vector2 comLocal)
        => SampleNamedPoint(_curDoc, _curComT, "com", out comLocal);

    // Non-allocating lerp of a named root-space Point addition across the keyframes
    // bracketing normalized time t (cf. AnimAdditionSampler.Sample, which allocates a
    // list every call). Holds the value when only one bracketing keyframe defines it.
    private static bool SampleNamedPoint(AnimationDocument doc, float t, string name, out Vector2 p)
    {
        p = default;
        var ks = doc?.Keyframes;
        if (ks == null || ks.Count == 0) return false;
        t = MathHelper.Clamp(t, ks[0].Time, ks[ks.Count - 1].Time);

        int i = 0;
        while (i < ks.Count - 1 && ks[i + 1].Time < t) i++;
        int j = Math.Min(i + 1, ks.Count - 1);

        bool ha = TryPointAt(ks[i], name, out var pa);
        bool hb = TryPointAt(ks[j], name, out var pb);
        if (ha && hb)
        {
            float span = ks[j].Time - ks[i].Time;
            float u = span <= 1e-6f ? 0f : (t - ks[i].Time) / span;
            p = Vector2.Lerp(pa, pb, u);
            return true;
        }
        if (ha) { p = pa; return true; }
        if (hb) { p = pb; return true; }
        return false;
    }

    private static bool TryPointAt(AnimationKeyframe k, string name, out Vector2 p)
    {
        p = default;
        if (k.Additions == null) return false;
        foreach (var a in k.Additions)
            if (a.Kind == AnimAdditionKind.Point && a.Name == name)
            {
                p = new Vector2(a.Px, a.Py);
                return true;
            }
        return false;
    }

    // --- clip selection ------------------------------------------------------

    private static AnimClip SelectClip(in CharacterAnimSample s)
    {
        // Guided traversal wins over the generic ground/air clips while active.
        if (s.MovementState != null && s.MovementState.Contains("Parkour")) return AnimClip.Vault;
        if (s.MovementState != null && s.MovementState.Contains("Crouch")) return AnimClip.Crouch;
        // Wall cling/slide (WallSlidingState): airborne but pinned to a wall, so it must
        // win over the generic Vy → Jump/Fall below. WallSlidingState pins Facing = wallDir for
        // the whole slide, so the rig reliably faces the wall (+X) — and the no-penetration
        // half-plane (CharacterAnimSample.From) sits on that same +X side.
        if (s.MovementState != null && s.MovementState.Contains("WallSlid")) return AnimClip.WallSlide;
        // LedgeGrab pins the body with a spring + damper; the resulting Vy oscillates
        // sign every frame as it rings down (e.g. −30, 0, −20, 0, ...). The generic
        // `Vy < 0 ? Jump : Fall` heuristic below would flip the clip every frame and
        // produce a per-frame Jump/Fall animation flicker even though the movement
        // state is stable. Map ledge holds to a stable clip instead — LedgePull plays
        // Vault (it's the same "guided pull" shape as ParkourState), LedgeGrab plays
        // Fall (a placeholder until a Hang clip exists; doesn't depend on Vy sign).
        if (s.MovementState != null && s.MovementState.Contains("LedgePull")) return AnimClip.Vault;
        if (s.MovementState != null && s.MovementState.Contains("LedgeGrab")) return AnimClip.Fall;
        if (!s.Grounded) return s.Velocity.Y < 0f ? AnimClip.Jump : AnimClip.Fall;
        float speed = MathF.Abs(s.Velocity.X);
        if (speed > WalkSpeedThreshold)
        {
            // Moving against facing = backpedal; with facing = forward, escalating to
            // Run past the run threshold.
            if (Math.Sign(s.Velocity.X) != s.Facing) return AnimClip.WalkBack;
            return speed > RunSpeedThreshold ? AnimClip.Run : AnimClip.Walk;
        }
        return AnimClip.Idle;
    }

    // Whether an action name should drive the overlay layer. NullAction/ReadyAction/
    // RecoveryAction read as "no action" — the overlay fades out through them, which
    // is also what bridges the gaps inside a slash combo. This string policy lives
    // here (not in the sample) for the same reason SelectClip's MovementState
    // matching does: the sample stays a dumb snapshot.
    private static bool IsOverlayAction(string action)
        => !string.IsNullOrEmpty(action)
           && action != "None" && action != "NullAction"
           && action != "ReadyAction" && action != "RecoveryAction";

    // Locomotion + idle play off the wrapped phase; one-shots play off ClipTime.
    private static bool IsPhaseDriven(AnimClip clip)
        => clip == AnimClip.Walk || clip == AnimClip.WalkBack || clip == AnimClip.Run || clip == AnimClip.Idle;

    // --- cadence solver ------------------------------------------------------

    private static bool HasContacts(AnimationDocument clip)
    {
        var ks = clip?.Keyframes;
        if (ks == null) return false;
        foreach (var k in ks)
            if (k.Contacts != null && k.Contacts.Count > 0) return true;
        return false;
    }

    // Feathered contact weights at `phase`, written into _weightBuf as (bone, weight)
    // merged by bone (§5.2). The keyframe interval's contacts hold full weight, then
    // crossfade to the next interval's over FeatherWidth before the change — so a foot
    // swap is a smooth crossover instead of a hard switch.
    private void WeightedContactsAtPhase(AnimationDocument clip, float phase)
    {
        _weightBuf.Clear();
        var ks = clip.Keyframes;

        int i = 0;
        for (int k = 0; k < ks.Count; k++) { if (ks[k].Time > phase) break; i = k; }
        int j = Math.Min(i + 1, ks.Count - 1);

        float feather = AnimSolverConfig.Current.FeatherWidth;
        float featherStart = ks[j].Time - feather;
        float u = (j != i && phase > featherStart)
                ? MathHelper.Clamp((phase - featherStart) / feather, 0f, 1f) : 0f;

        AddWeighted(ks[i].Contacts, 1f - u);
        if (u > 0f) AddWeighted(ks[j].Contacts, u);
    }

    private void AddWeighted(List<ContactLabel> labels, float scale)
    {
        if (labels == null || scale <= 0f) return;
        foreach (var l in labels)
        {
            int b = _skeleton.IndexOf(l.Node);
            if (b < 0) continue;
            float w = l.Weight * scale;
            int at = -1;
            for (int k = 0; k < _weightBuf.Count; k++) if (_weightBuf[k].bone == b) { at = k; break; }
            if (at >= 0) _weightBuf[at] = (b, _weightBuf[at].weight + w);
            else         _weightBuf.Add((b, w));
        }
    }

    // Refresh active contacts from the feathered weights: drop those that faded to ~0,
    // update held ones' weights, and lazily capture newly-appearing ones (world tip at
    // the current phase, while their weight is still small — §5.2). SelfPlant only for
    // now (External = Phase 5).
    private void RefreshContacts(AnimationDocument clip, float phase, in Affine2 root)
    {
        WeightedContactsAtPhase(clip, phase);

        for (int i = _contacts.Count - 1; i >= 0; i--)
            if (WeightOf(_contacts[i].Bone) <= 1e-3f)
                _contacts.RemoveAt(i);

        bool needWorld = false;
        foreach (var (bone, w) in _weightBuf)
            if (w > 1e-3f && ActiveIndex(bone) < 0) { needWorld = true; break; }
        if (needWorld)
        {
            AnimationSampler.SampleSmooth(clip, phase, _kfA, _kfB, _kfC, _kfD, _scratch);
            ComposeOverlays(_scratch);   // capture the target on the COMPOSED pose (= what we measure)
            _scratch.ComputeWorld(root);
        }

        foreach (var (bone, w) in _weightBuf)
        {
            if (w <= 1e-3f) continue;
            int idx = ActiveIndex(bone);
            if (idx >= 0)
            {
                var c = _contacts[idx];
                c.Weight = w;
                _contacts[idx] = c;
            }
            else
            {
                Vector2 tip = _scratch.WorldOf(bone).Translation;   // bone's far end = contact tip
                _contacts.Add(new ActiveContact { Bone = bone, Target = tip, Weight = w, Source = ContactSource.SelfPlant });
            }
        }
    }

    // Pick Δφ ∈ [0, MaxPhaseStep] minimizing planted-contact slip plus a momentum
    // prior, via the derivative-free golden-section search.
    private float SolvePhaseStep(AnimationDocument clip, float phi, in Affine2 root)
    {
        var r = root;   // capture by value for the closure
        float Loss(float dphi)
        {
            AnimationSampler.SampleSmooth(clip, Wrap01(phi + dphi), _kfA, _kfB, _kfC, _kfD, _scratch);
            ComposeOverlays(_scratch);   // measure slip on the post-blend pose, matching capture
            _scratch.ComputeWorld(r);
            float e = 0f;
            foreach (var c in _contacts)
            {
                Vector2 tip = _scratch.WorldOf(c.Bone).Translation;   // bone's far end = contact tip
                // Penalize only HORIZONTAL (along-ground) slip. The planted foot must not
                // slide across the ground, but its vertical arc (lift over the stance) is
                // intrinsic to the cadence and is reconciled by the ground/COM anchor — not
                // something Δφ should fight. Penalizing the Y component made the vertical
                // arc dominate the loss at walk speed (small horizontal drift), pinning Δφ
                // to ~0 every frame: the cadence froze below the run-speed band.
                float dx = tip.X - c.Target.X;
                e += c.Weight * dx * dx;
            }
            float d = dphi - _prevPhaseStep;
            return e + AnimSolverConfig.Current.PhaseStepPrior * d * d;
        }
        return GoldenSection.Minimize(Loss, 0f, AnimSolverConfig.Current.MaxPhaseStep);
    }

    // Solver-path equivalent of SolvePhaseStep: the SAME objective (horizontal foot
    // no-slip + a playback-continuity prior) cast as least-squares residuals and
    // minimized over the single variable Δφ ∈ [0, MaxPhaseStep] by the general LM core.
    // At parity this lands on the same Δφ the golden-section search finds; it exists so
    // the solver framework is exercised end-to-end before later phases add variables
    // (joint corrections, CoM offset) and constraints. See ANIMATION_SOLVER_PLAN Phase 1.
    private float SolvePhaseStepLm(AnimationDocument clip, float phi, in Affine2 root)
    {
        _solveClip = clip; _solvePhi = phi; _solveRoot = root;
        var cfg = AnimSolverConfig.Current;
        int n = 2 + _skeleton.Count;          // x[0]=Δφ; x[1]=δ (vertical); x[2+i]=Δθ_i

        _solveLo[0] = 0f;                   _solveHi[0] = cfg.MaxPhaseStep;
        _solveLo[1] = -cfg.VertOffsetLimit; _solveHi[1] = cfg.VertOffsetLimit;
        for (int i = 2; i < n; i++) { _solveLo[i] = -cfg.AngleCorrLimit; _solveHi[i] = cfg.AngleCorrLimit; }
        Array.Clear(_solveVars, 0, n);        // δ, Δθ start at 0 (baseline pose); Δφ seeded below
        CaptureAimTarget(n);                  // freeze û* from the reference pose before any residual eval

        // The cadence objective is NON-CONVEX in Δφ: a planted foot's horizontal track
        // is non-monotonic over a stance arc (it can drift forward before sweeping back),
        // so the gradient at Δφ=0 may point into the Δφ<0 wall while the true minimum
        // sits further inside the bracket. A purely local descent stalls there. Globalize
        // with a cheap coarse seed search (1-D only), keeping the momentum warm-start
        // (Δφ_prev) as a candidate so steady-state locomotion stays smooth, then let LM
        // refine. δ and the Δθ corrections need no seeding — under their (com / Tikhonov)
        // priors they are convex about 0 — so they ride along at 0 while we pick the Δφ
        // basin, then LM refines the whole vector jointly (ANIMATION_SOLVER_PLAN §3.5).
        float best     = MathHelper.Clamp(_prevPhaseStep, 0f, cfg.MaxPhaseStep);
        float bestCost = CadenceCostAt(best, n);
        const int seeds = 9;
        for (int k = 0; k <= seeds; k++)
        {
            float s = cfg.MaxPhaseStep * k / seeds;
            float c = CadenceCostAt(s, n);
            if (c < bestCost) { bestCost = c; best = s; }
        }

        _solveVars[0] = best;
        // Δθ starts at 0 (not warm-started from _prevTheta): the θ-smoothness prior supplies the
        // temporal continuity from the COST side, and a box-clamped _prevTheta seed would stick
        // the solution at the wall. _prevTheta is only the smoothness TARGET, set after the solve.
        _ls.Minimize(_cadenceResiduals, _cadenceJacobian,
                     _solveVars.AsSpan(0, n), _solveLo.AsSpan(0, n), _solveHi.AsSpan(0, n));
        for (int i = 0; i < _skeleton.Count; i++) _prevTheta[i] = _solveVars[2 + i];
        _haveCorr = true;
        return _solveVars[0];
    }

    // Off-locomotion solve (Phase 3): satisfy this frame's external pins + no-penetration
    // surfaces on a clip with no cadence to drive. Δφ is LOCKED (box [0,0]) — there is no
    // planted-foot no-slip here — so only δ (the body bob) and the per-bone Δθ (the IK that
    // bends limbs off a wall / onto a pin) move. The base pose is sampled at the SAME phase /
    // clip-time step 3 draws at, so the solved Δθ line up when applied there. Mirrors
    // SolvePhaseStepLm's root construction (com baseline so capture/solve/draw share a frame).
    private void SolveStaticPose(AnimationDocument anim, AnimClip clip, in CharacterAnimSample s)
    {
        var cfg = AnimSolverConfig.Current;
        int dir = s.Facing == 0 ? 1 : s.Facing;
        float phi = IsPhaseDriven(clip) ? _state.Phase
                                        : AnimationSampler.NormalizedTime(anim, _state.ClipTime);
        float comBaseY = 0f;
        if (SampleNamedPoint(anim, phi, "com", out var comL)) comBaseY = -comL.Y * _scale;
        var root = Affine2.FromTRS(new Vector2(s.Position.X, s.Position.Y + comBaseY), 0f,
                                   new Vector2(dir * _scale, _scale));

        _contacts.Clear();                  // no planted contacts on this path
        _solveClip = anim; _solvePhi = phi; _solveRoot = root;
        int n = 2 + _skeleton.Count;
        _solveLo[0] = 0f;                   _solveHi[0] = 0f;   // Δφ locked — no cadence here
        _solveLo[1] = -cfg.VertOffsetLimit; _solveHi[1] = cfg.VertOffsetLimit;
        for (int i = 2; i < n; i++) { _solveLo[i] = -cfg.AngleCorrLimit; _solveHi[i] = cfg.AngleCorrLimit; }
        Array.Clear(_solveVars, 0, n);
        CaptureAimTarget(n);                  // freeze û* from the reference pose before any residual eval

        _ls.Minimize(_cadenceResiduals, _cadenceJacobian,
                     _solveVars.AsSpan(0, n), _solveLo.AsSpan(0, n), _solveHi.AsSpan(0, n));
        for (int i = 0; i < _skeleton.Count; i++) _prevTheta[i] = _solveVars[2 + i];
        _haveCorr = true;
    }

    // Cost at a candidate Δφ with δ and the angle corrections held at 0 (Δφ seed search).
    private float CadenceCostAt(float dphi, int n)
    {
        Span<float> s = stackalloc float[80];   // ≥ 2 + rig bone count; δ/Δθ entries stay 0
        s.Clear();
        s[0] = dphi;
        return _ls.Cost(_cadenceResiduals, s.Slice(0, n));
    }

    // The cadence solve's forward pass: build the COMPOSED, corrected world pose for a
    // candidate x = [Δφ, δ, Δθ…] and leave it in _scratch (world buffer valid under
    // _solveRoot). One place so the residual and the (coming) analytic Jacobian evaluate
    // the SAME skeleton. Order mirrors Update's draw exactly: sample the base clip at
    // φ+Δφ, add the per-bone Δθ, then paint the action overlay on top (the linear blend).
    //
    // Jacobian note (next step, ANIMATION_SOLVER_PLAN): the analytic columns read straight
    // off the buffer this leaves behind — for a contact tip p on bone b, ∂p/∂Δθ_j is the
    // 2D lever arm perp(p − origin_j) for each ancestor joint j (0 otherwise), scaled by
    // the blend's (1−_overlayWeight[j]); ∂p/∂Δφ chains the same FK over a d-sample-by-φ
    // companion; δ and the priors are constant columns. So this method is the substrate
    // both paths share — keep the FK/compose/sample ordering here authoritative.
    private void BuildSolvePose(ReadOnlySpan<float> x)
    {
        AnimationSampler.SampleSmooth(_solveClip, Wrap01(_solvePhi + x[0]), _kfA, _kfB, _kfC, _kfD, _scratch);
        ComposeOverlays(_scratch);                                                // overlay first (linear)
        int bones = _skeleton.Count;
        // Δθ is applied onto the COMPOSED pose, not the base — so the IK correction survives an
        // overlay that fully owns a bone (a vault hand owned by the VaultHands overlay). Pre-compose
        // Δθ would be overwritten by PaintMotionLayer's lerp and the pin couldn't bend that limb.
        for (int i = 0; i < bones; i++) _scratch.Local[i].Rotation += x[2 + i];   // post-compose IK
        _scratch.ComputeWorld(_solveRoot);
    }

    // The composite objective (§11.3). The residual/Jacobian are now ASSEMBLED from the
    // ordered `_constraints` list rather than hand-inlined: one shared forward pass
    // (BuildSolvePose) leaves _scratch's world buffer valid, then each constraint emits its
    // rows. Row order = list order (load-bearing: the LM core and the FD-vs-analytic oracle
    // assume a fixed row order). Behaviour is bit-identical to the old monolith — the four
    // blocks below emit exactly the old rows in the old order.
    private int CadenceResiduals(ReadOnlySpan<float> x, Span<float> r)
    {
        BuildSolvePose(x);            // _scratch world is now the composed, corrected pose at φ+Δφ
        int row = 0;
        foreach (var c in _constraints) row += c.Residuals(x, r.Slice(row));
        return row;
    }

    private void CadenceJacobian(ReadOnlySpan<float> x, Span<float> jac, int stride)
    {
        BuildSolvePose(x);                       // _scratch world = composed pose at φ+Δφ
        // ω_j: per-bone angular velocity of the BASE clip at φ+Δφ — the Δφ channel, read by
        // PointJacobianColumns. Sampled once here so every constraint shares it.
        AnimationSampler.SampleAngularVelocity(_solveClip, Wrap01(_solvePhi + x[IdxPhi]),
                                               _kfA, _kfB, _kfC, _kfD, _angVel.AsSpan(0, _skeleton.Count));
        int row = 0;
        foreach (var c in _constraints) row += c.Jacobian(x, jac, stride, row);
    }

    private int ActiveIndex(int bone)
    {
        for (int i = 0; i < _contacts.Count; i++) if (_contacts[i].Bone == bone) return i;
        return -1;
    }

    private float WeightOf(int bone)
    {
        foreach (var e in _weightBuf) if (e.bone == bone) return e.weight;
        return 0f;
    }

    // --- helpers -------------------------------------------------------------

    // Paint a motion layer onto the accumulator pose: per bone, blend toward the layer by
    // its opacity weight (0 keeps the accumulator, 1 takes the layer; shortest-path angle
    // lerp via BoneTransform.Lerp). Ordered application = layer opacity, the general form
    // of the action overlay's "override my region, gated by the eased weight". This is the
    // one composition primitive for all motions (Phase 4); allocation-free.
    private void PaintMotionLayer(SkeletonPose acc, SkeletonPose layer, float[] weight)
    {
        for (int i = 0; i < _skeleton.Count; i++)
        {
            float w = weight[i];
            if (w > 0f) acc.Local[i] = BoneTransform.Lerp(acc.Local[i], layer.Local[i], w);
        }
    }

    // Compose this frame's resolved action overlay onto a pose — the single point the solve
    // and the draw share, so both evaluate the SAME post-blend skeleton. No-op when no
    // overlay is active this frame (resolved in step 1.5). The overlay pose + weights are
    // constant w.r.t. the solve vars, so the blend is a per-bone linear pre-multiply on the
    // base layer: the residual's gradient w.r.t. a base-driven var is just scaled by (1−w),
    // which the (coming) analytic Jacobian applies directly and the FD path captures for free.
    private void ComposeOverlays(SkeletonPose pose)
    {
        if (!_anyOverlay) return;
        // Paint slots foreground-LAST (slot 0, the Action overlay, wins a shared bone over a
        // movement overlay). _baseBlend is a product → order-independent, so the analytic
        // Jacobian stays consistent regardless of this paint order.
        for (int si = _slots.Length - 1; si >= 0; si--)
            if (_slots[si].Active) PaintMotionLayer(pose, _slots[si].Pose, _slots[si].BoneWeight);
    }

    // Point a slot at a target overlay (key + clip). On a KEY change rebind the clip and (when
    // non-null) its Region mask; a null clip keeps the prior mask/pose so a fade-out blends away
    // from the last sample, not garbage. Weight is NOT reset → a combo rebind keeps the arm up.
    private void BindSlot(OverlaySlot slot, string key, AnimationDocument clip)
    {
        if (!string.Equals(key, slot.Key, StringComparison.Ordinal))
        {
            slot.Key = key;
            slot.Clip = clip;
            if (clip != null) { slot.Mask = _regionMasks[(int)clip.Region]; slot.OffWeight = clip.OffRegionWeight; }
        }
        else slot.Clip = clip;
    }

    // Ease a slot's opacity toward 1 (clip bound) or 0 (fading), sample its pose at τ when
    // bound, and refresh its per-bone compose weights / Active flag. A fully faded, unbound slot
    // snaps to 0 and frees its key for reuse. Mirrors the old single-overlay ease exactly.
    private void EaseSlot(OverlaySlot slot, AnimationDocument clip, float tau, float dt)
    {
        bool bound = clip != null;
        float wRate = bound ? ActionEaseIn : ActionEaseOut;
        slot.Weight += ((bound ? 1f : 0f) - slot.Weight) * (1f - MathF.Exp(-wRate * dt));
        if (bound)
            AnimationSampler.SampleSmooth(clip, tau, _kfA, _kfB, _kfC, _kfD, slot.Pose);
        slot.Active = slot.Weight > OverlayWeightEps && slot.Mask != null;
        if (slot.Active)
            for (int i = 0; i < _skeleton.Count; i++)
                slot.BoneWeight[i] = (slot.Mask[i] ? 1f : slot.OffWeight) * slot.Weight;
        else if (!bound) { slot.Weight = 0f; slot.Key = null; }   // snap the fade tail, free the slot
    }

    // Animation-side policy: the overlay layers a MovementState contributes, BEYOND the Action
    // overlay (slot 0). Owns the lookup (name convention against _actionClips, like SelectClip
    // owns base-clip policy) and derives each overlay's τ from MOVEMENT DATA in the sample —
    // movement progress is input-driven (a vault advances by body position vs. the corner, not
    // elapsed time), so τ can't come from a clock. Push at most OverlaySlotCount-1 entries.
    // Empty until the parkour clips exist (Phase B).
    private void ResolveMovementOverlays(in CharacterAnimSample s,
                                         List<(string key, AnimationDocument clip, float tau)> dst)
    {
        // Parkour vault: an UpperBody "hands" overlay (reach for the ledge → push off), bound by
        // name convention and timed by the vault's SPATIAL progress (body vs. corner), not a
        // clock. The legs/body come from the Vault base clip; this owns the upper body while the
        // maneuver runs. Disjoint from the Action slot (a co-occurring slash → the other arm).
        if (s.MovementState != null && s.MovementState.Contains("Parkour")
            && _actionClips.TryGetValue("VaultHands", out var hands))
            dst.Add(("VaultHands", hands, MathHelper.Clamp(s.MovementProgress, 0f, 1f)));
    }

    // Animation-side policy for FIXED-POINT pins a MovementState contributes (the geometric
    // counterpart of ResolveMovementOverlays): the sample carries the grip TARGET (geometry),
    // and this owns which BONE pins to it and WHEN. Adds to _pins, respecting the MaxPins cap.
    private const string VaultGripBone  = "arm_l_lower";   // the lead reaching hand the VaultHands clip drives
    private const float  VaultGripStart = 0.45f;           // engage once the clip hand is near the corner…
    private const float  VaultGripEnd   = 0.85f;           // …release before the push-off over the top
    private void ResolveMovementPins(in CharacterAnimSample s)
    {
        // Vault: pin the lead hand to the ledge corner over the GRAB WINDOW. Before the window the
        // hand swings up via the clip; a both-axis hard pin from progress 0 would snap it to the
        // corner. Gating where the clip already brings the hand near the corner keeps the lock
        // smooth (a small Δθ correction). The pinned hand is owned solely by the VaultHands overlay
        // slot — the plan invariant — so its post-compose Δθ bends it freely onto the corner.
        if (_pins.Count < MaxPins && s.HasGrip
            && s.MovementState != null && s.MovementState.Contains("Parkour")
            && s.MovementProgress >= VaultGripStart && s.MovementProgress <= VaultGripEnd)
        {
            int b = _skeleton.IndexOf(VaultGripBone);
            if (b >= 0) _pins.Add((b, s.GripTarget));
        }
    }

    // Resolve this frame's action aim (§STAB_AIM_PLAN). The sample carries the world aim direction
    // (a stab's StabDir); the animator owns which bones encode the aim (the L→R hand pair) and so
    // freezes _aimActive/_aimDir/_aimFacing for the solve. The target û* is captured at solve start
    // (CaptureAimTarget) once the reference pose is built. HasAim is only set for aimed actions.
    private void ResolveActionAim(in CharacterAnimSample s)
    {
        _aimActive = false;
        if (!_useSolver || !s.HasAim || _aimBoneL < 0 || _aimBoneR < 0) return;
        if (s.AimDir.LengthSquared() < 1e-6f) return;
        _aimDir    = Vector2.Normalize(s.AimDir);
        _aimFacing = s.Facing == 0 ? 1 : s.Facing;
        _aimActive = true;
    }

    // Freeze the aim target û* for this frame's solve: the authored reference aim (the L→R hand
    // vector of the Δθ=0 composed pose) ROTATED by the stab's deviation from horizontal-forward
    // f=(facing,0). Rotating the reference (rather than aiming a fixed vector) preserves the clip's
    // windup→thrust dynamics. Called at solve start, when _solveVars is all-zero (the reference).
    private void CaptureAimTarget(int n)
    {
        if (!_aimActive) return;
        BuildSolvePose(_solveVars.AsSpan(0, n));   // _solveVars == 0 here ⇒ Δθ=0, Δφ=0 reference pose
        Vector2 pL = _scratch.WorldOf(_aimBoneL).Translation;
        Vector2 pR = _scratch.WorldOf(_aimBoneR).Translation;
        Vector2 aRef = pR - pL;
        // R takes f=(facing,0) → _aimDir: cosθ = f·d = facing·d.x, sinθ = f×d = facing·d.y (both unit).
        float c = _aimFacing * _aimDir.X, sgn = _aimFacing * _aimDir.Y;
        Vector2 rot = new Vector2(aRef.X * c - aRef.Y * sgn, aRef.X * sgn + aRef.Y * c);
        float len = rot.Length();
        _aimTarget = len > 1e-6f ? rot / len : new Vector2(_aimFacing, 0f);
    }

    private void Rot(int bone, float delta)       { if (bone >= 0) _target.Local[bone].Rotation    += delta; }
    private void Translate(int bone, Vector2 d)    { if (bone >= 0) _target.Local[bone].Translation += d;     }
    private void Scale(int bone, Vector2 d)        { if (bone >= 0) _target.Local[bone].Scale       += d;     }

    private static float Wrap01(float x) => x - MathF.Floor(x);
}
