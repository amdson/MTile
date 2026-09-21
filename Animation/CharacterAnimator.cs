using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// The clips this first-draft animator can play. Selection is derived purely from
// the observed CharacterAnimSample — never pushed by the sim.
// Walk vs WalkBack distinguishes moving with vs against the facing direction
// (forward stride vs backpedal). Run is forward locomotion above a speed threshold
// (a longer-stride clip — same cadence machinery). Air is split into Jump (rising)
// and Fall. Parkour/Mantle/ArcJump/LedgePull are the four guided lip maneuvers — one clip each
// (they were a single shared "Vault" clip until 2026-08-04; see Plans/ANIMATION_BINDING_MAP.md).
// EVERY value here must have a clip file whose Type matches, or binding throws at construction.
public enum AnimClip { Idle, Walk, WalkBack, Crouch, CrouchWalk, 
DuckUnder, Jump, Fall, Parkour, Run, WallSlide, Hang, Hitstun, 
Tumble, WallJumpKick, DoubleJumpFlip, RunTurn, Land, LedgeJump, 
Dropdown, Mantle, ArcJump, LedgePull, StepUp, Stairs }

// The animation-side state, deliberately separate from any character/sim state.
// The animator owns and evolves this; it is the "previous state" the animator is
// allowed to remember between frames (alongside the previous sample).
public struct CharacterAnimState
{
    public AnimClip Clip;         // currently-selected clip
    public float    ClipTime;     // seconds spent in the current clip
    public float    Phase;        // locomotion cycle phase, wrapped to [0,1)
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
    // (The Walk/Run speed thresholds live in GroundLocomotionDriver — clip selection policy
    //  moved into the move drivers; see Animation/MoveDriver.cs.)
    private const float PhasePerPixel       = 0.010f; // legacy fallback: cycles/sec per px/s
    private const float IdleBobHz           = 0.30f;  // breathing cycles/sec
    // Pose-follow rate (1/sec). No longer a BlendToward ease — the smoothing lives INSIDE the
    // solve (polish item 1): each frame these rates become the per-bone smoothness weights
    // λs_i = λp_i·(1−b_i)/b_i with b_i = 1−exp(−k_i·dt) (_lambdaSmooth/_easeB), chosen so an
    // UNCONSTRAINED bone follows its blend target with exactly the old exponential ease while
    // a constrained bone (pin/contact/no-pen) satisfies its constraint on the RENDERED pose.
    private const float Stiffness           = 20f;
    // Upper body (chest subtree: arms + knife) smooths far faster *while an action
    // overlay is active*, ramped in by ActionWeight. A slash is ~0.14s with sub-20ms
    // swing segments; the base 20/s (50ms τ) low-passes ~70% of that authored range
    // away. ~90/s (≈11ms τ) passes ~90% so the rendered hand — and the knife glow
    // welded to it — tracks the real attack. Gated by ActionWeight so locomotion's
    // softer arm follow is untouched; only attacks snap.
    private const float UpperBodyStiffness  = 90f;

    // --- cadence / IK solver ---
    // All solver weights + box limits live in AnimSolverConfig (hot-reloadable; the solve is
    // render-only so there's no determinism risk). Read as _frame.Solver.X — the PER-FRAME
    // effective copy of AnimSolverConfig.Current (refreshed at the top of Update, overridable
    // by the move driver in Contribute) — never as Current directly. See the weight TIERS /
    // per-region pose-prior rationale documented there and in §11.4.

    // --- action overlay ---
    // (The overlay slot machinery — binding, easing, request→slot matching, compositing —
    //  lives in OverlayStack; the ease rates moved there with it.)

    // --- plant-foot debug marker ---
    private const  float PlantFootMarkerRadius = 1.2f;
    private static readonly Color PlantFootMarkerColor = Color.Lime;

    private readonly Skeleton     _skeleton;
    private readonly float        _scale;   // rig→world scale; the solve needs it, not just Draw
    // 1 / characteristic length (the rig's REACH: longest root→tip chain × scale, world px).
    // Every PIXEL residual (contacts, pins, no-penetration, the com ties) is multiplied by
    // this, making it DIMENSIONLESS — "fraction of a body-reach of error" — so the config
    // tiers are commensurable with the radian-scale rows (aim, pose priors): through the
    // lever arms, 1 rad of joint error ≈ 1 reach of tip error, so weight numbers now compare
    // honestly across both kinds of row (§11.4). The px tiers in AnimSolverConfig carry the
    // matching ×reach² rescale, so effective behavior is unchanged.
    private readonly float        _invCharLen;
    private readonly SkeletonPose _pose;    // live output, eased each frame
    private readonly SkeletonPose _target;  // target assembled this frame
    private readonly SkeletonPose _kfA, _kfB, _kfC, _kfD;   // scratch for the C1 keyframe quad (iL,i0,i1,iR)
    private readonly SkeletonPose _scratch;     // pose scratch: contact capture, pose matching, the static
                                                // solve's dormancy check (the solve's own pose is _eval.Pose)
    // DESIGN INVARIANT (decision 2026-07-14): every constraint evaluates the FINAL composed,
    // Δθ-corrected pose — the one that gets drawn. No constraint reads an intermediate pose,
    // and there is exactly ONE solve per frame over the full objective; conflicts between
    // constraints (a pin bending a planted leg, no-pen pushing a foot) are resolved by their
    // WEIGHTS, not by structure. Two structural alternatives were tried and rejected: contacts
    // on a Δθ-free pose (the drawn foot then slips by the Δθ contribution the constraint never
    // sees) and a two-stage cadence/IK split (hides objective misspecification instead of
    // surfacing it as a weight problem). The foot-swap stall that motivated them is fixed at
    // its actual root — contact RELEASE bookkeeping (see RefreshContacts' time fade).

    // The planted contacts the cadence solver pins this frame (ActiveContact — SolveProblem.cs):
    // captured when a label appears, held until it drops (RefreshContacts owns the lifecycle);
    // the solve reads a FROZEN copy (FreezeProblem).
    private readonly List<ActiveContact> _contacts = new();
    // External fixed-point pins resolved from this frame's sample (bone index + world target).
    // Held at the HARD tier by FixedPointConstraint; frozen for the duration of one solve.
    private readonly List<(int bone, Vector2 target)> _pins = new();
    private const int MaxPins = SolveProblem.MaxPins;
    // No-penetration half-planes resolved from this frame's sample. Frozen for one solve; each
    // emits one row per rig bone (NoPenetrationConstraint) — the limbs the solver pushes out.
    private readonly List<SolverSurface> _surfaces = new();
    private const int MaxSurfaces = SolveProblem.MaxSurfaces;
    // Support band shared with NoPenetrationConstraint.SkipPair — documented on SolveProblem.
    private const float ContactSupportBand = SolveProblem.ContactSupportBand;
    // Variable layout of x (documented on SolveProblem).
    private const int IdxPhi = SolveProblem.IdxPhi, IdxDy = SolveProblem.IdxDy,
                      IdxDx = SolveProblem.IdxDx, IdxTheta0 = SolveProblem.IdxTheta0;
    // Whether any surface can plausibly engage this frame (sample.SurfacesNear) — gates the
    // off-locomotion static solve so dormant terrain planes don't defeat the fast path.
    private bool _surfacesNear;
    // Scratch for feathered contact weights: (bone, w, dw/dφ) at some phase. Filled by
    // WeightedContactsAtPhase — at the entry phase for RefreshContacts' capture/release
    // bookkeeping (the solve itself reads the FROZEN per-contact weight — see
    // PlantedContactsConstraint).
    private readonly List<(int bone, float weight, float dweight)> _weightBuf = new();
    private float _prevPhaseStep;   // last frame's phase advance Δφ (cycles/frame)
    // The timing stage's state (GaitTiming — Plans/ANIMATION_TIMING_STAGE.md): last frame's
    // rate (cycles/s) and the full result, for the planner's pace estimate and diagnostics.
    private float        _rate;
    private TimingResult _lastTiming;   // carries the stopping policy's state frame to frame
    public  TimingResult LastTiming => _lastTiming;
    // Worst perpendicular miss of the observed foot offset from its authored stance sweep
    // (rig units) on the last frame an observation was taken — diagnostics.
    private float _lastObservedResidual;
    public  float LastObservedResidual => _lastObservedResidual;
    // The cadence clip family the stopping policy may hold through a settle (step 1).
    private static bool IsCadenceClip(AnimClip c)
        => c is AnimClip.Walk or AnimClip.WalkBack or AnimClip.Run or AnimClip.CrouchWalk or AnimClip.StepUp or AnimClip.Stairs;
    // Any-source stride tracks for the timing stage, cached per document like _strideCache.
    private readonly Dictionary<AnimationDocument, ClipStrideTrack> _gaitCache = new();
    private ClipStrideTrack GaitTrackFor(AnimationDocument doc)
    {
        if (doc == null) return null;
        if (!_gaitCache.TryGetValue(doc, out var t))
        {
            if (!ClipStrideTrack.TryCompile(doc, _skeleton, out t, out _, anySource: true)) t = null;
            _gaitCache[doc] = t;
        }
        return t;
    }

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
    // In-solve smoothing state (polish item 1 — replaces the BlendToward ease):
    //   _thetaEmitted — each bone's FINAL local rotation actually emitted last frame, captured
    //     pre-lean/squash (lean captured in would feed back: the solver would learn the lean
    //     into Δθ and it would be applied twice). PERSISTS across clip switches — that's what
    //     bridges them. The smoothness target for both the LM solve and the fast path.
    //   _lambdaSmooth — per-bone λs_i = λp_i·(1−b_i)/b_i, derived each frame from Stiffness/dt.
    //   _easeB — per-bone b_i = 1−exp(−k_i·dt), the closed-form fast-path blend factor.
    private readonly float[]      _thetaEmitted;
    private readonly float[]      _lambdaSmooth;
    private readonly float[]      _easeB;
    //   (the per-solve smoothness targets t_i live in _problem.SmoothTarget — FillSmoothTargets)
    private bool                  _haveEmitted;   // false until the first frame has been drawn

    // Overlay motion layers composed onto the base pose (Phase 4): the compositor lives in
    // OverlayStack (an ordered stack of crossfading slots — slot 0 is the privileged Action-FSM
    // overlay; slots 1+ serve driver requests). _baseBlend ALIASES the stack's Π(1−w) array —
    // same object — so the analytic Jacobian's Δφ attenuation reads it with no copy.
    private readonly OverlayStack _overlays;
    private readonly float[] _baseBlend;            // alias of _overlays.BaseBlend (per-bone Π(1−w))

    // The settle: the last attack overlay seen, held so its SettleShare tail can play down
    // the recovery countdown after the action itself has exited (the FSM is in
    // RecoveryAction then, which binds nothing of its own). _settleTotal is the longest
    // countdown observed since the swing — the sample carries frames LEFT, not the stamp's
    // length, so progress is measured against the first (largest) reading. Render-only
    // memory, like the rest of the animator; an eviction into recovery jump-cuts to the
    // settle's start, the same rule Grab's throw segment documents.
    private string            _settleKey;
    private AnimationDocument _settleClip;
    private int               _settleTotal;
    private Vector2           _settleAim;      // the swing's aim, kept so the settle doesn't un-aim
    private bool              _settleHasAim;

    // The move-driver registry (Animation/MoveDriver.cs): per-situation animation policy —
    // clip selection, time mode, entry, and per-frame contributions (overlays, pins, future
    // constraint blocks). First Matches() in order wins; _frame is its contribution scratch,
    // cleared and refilled each Update.
    private readonly IMoveDriver[] _drivers;
    private readonly FrameInputs   _frame = new();
    private ClipTimeMode           _timeMode;       // how the current clip's sample time is produced

    private CharacterAnimState  _state;
    // What the core remembers about earlier frames, handed to every driver (Animation/
    // MoveDriver.cs). Refreshed at the top of Update; the retained sample is replaced at
    // the very bottom, so `_history.Prev` is last frame's throughout the body.
    private AnimHistory _history;

    // The clip doc sampled this frame and the normalized time it was sampled at —
    // remembered so the host can pull labeled additions (e.g. the "com" reference
    // point) for the exact pose being drawn, after Update returns.
    private AnimationDocument _curDoc;
    private float             _curComT;

    // Generalized cadence solver (Plans/ANIMATION_SOLVER_PLAN.md). The phase advance Δφ,
    // the vertical body offset δ, and the per-bone IK corrections Δθ all come from a
    // Levenberg–Marquardt least-squares solve over the composite constraint objective
    // (horizontal foot no-slip, ground hold, pins, no-penetration, aim, priors). This is
    // THE animator path (the legacy 1-D golden-section cadence was retired after the LM
    // path was shown to be the better minimizer of the same objective — see
    // ANIMATION_SOLVER_PLAN §7 Phase 1 follow-up). Render-only.
    private readonly LeastSquaresSolver  _ls;
    private readonly float[]             _solveVars, _solveLo, _solveHi;
    private readonly LeastSquaresSolver.ResidualFn _cadenceResiduals;
    private readonly LeastSquaresSolver.JacobianFn _cadenceJacobian;   // analytic J (replaces FD)
    private readonly bool[]              _isCore;        // bone is torso (hip/chest/head) → stiff Tikhonov λ_θ
    // The composite objective, assembled per frame into _problem.Blocks: the geometric core
    // head (contacts, pins, no-pen, aim), then any driver-contributed blocks (FrameInputs.
    // Constraints — still inside the geometric band), then the prior tail (continuity, rate
    // floor, com, Tikhonov, smoothness). List order IS the residual/Jacobian row order the LM
    // core assumes; it's frozen for the frame once assembled (step 1.8). Diagnostics derive
    // block offsets by walking this list (no more triple-maintained row arithmetic).
    private readonly ISolveConstraint[]      _coreGeom;
    private readonly ISolveConstraint[]      _corePriors;
    private readonly int                     _maxResiduals;   // LM core's row capacity (diag scratch sizing)
    // THE SOLVE CORE (workplan chunk 1.5 — SolveProblem.cs / SolveObjective.cs / SolveConstraints.cs):
    // every solve FREEZES its inputs into _problem (FreezeProblem), and every residual/Jacobian
    // evaluation is then a pure function of (_problem, x) writing its forward pass into _eval.
    // Both are preallocated once; the LM core is handed closures over them (_cadenceResiduals /
    // _cadenceJacobian). Between solves _problem keeps the LAST solve's inputs, which is what
    // the diagnostics (SolvedBoneTipWorld, MaxJacobianError, …) re-evaluate.
    private readonly SolveProblem _problem;
    private readonly PoseEval     _eval;
    // The root offset d = (δ, d.x) as EMITTED (drawn) last frame — the temporal anchor for the
    // com block's smoothness rows and the value the host reads (VerticalOffset /
    // HorizontalOffset). On a solve frame it is the solved d; on a no-solve frame (flight,
    // non-locomotion) it EASES toward 0 by the same base ease factor the bones use, instead
    // of snapping to the baseline — the one-frame root pop at contact release/capture.
    private float             _dyEmitted, _dxEmitted;
    private float             _easeBase;          // this frame's base ease factor b = 1 − exp(−Stiffness·dt)
    private bool              _haveCorr;          // a Δθ-correction solve ran this frame

    // Action-aim state (the stab re-aim, §STAB_AIM_PLAN), resolved each frame in step 1.7 and
    // frozen for the solve. û* is captured once at solve start from the Δθ=0 pose into
    // _problem.AimTarget (CaptureAimTarget).
    private bool    _aimActive;
    private Vector2 _aimDir;       // world input aim direction (unit) this frame
    private int     _aimFacing;    // facing the reference rotation is measured from
    private readonly int _aimBoneL, _aimBoneR;   // the L→R hand pair whose vector encodes the aim

    // Cached bone indices (resolved once).
    private readonly int _hip, _chest;

    public Skeleton           Skeleton => _skeleton;
    public SkeletonPose       Pose     => _pose;
    public CharacterAnimState State    => _state;

    // The step planner (Plans/ANIMATION_STEP_PLANNER_IMPL.md). Runs for CadencePhase
    // clips that opted in via PlannedSupport labels; P2 scope — its plans feed the
    // debug overlay and diagnostics only, the solve does not consume them yet.
    public readonly StepPlanner Planner = new();
    // Stride tracks cached per DOCUMENT REFERENCE — an editor-reloaded doc is a new
    // reference and recompiles; a failed compile caches null (legacy path, no respam).
    private readonly Dictionary<AnimationDocument, ClipStrideTrack> _strideCache = new();

    private ClipStrideTrack StrideTrackFor(AnimationDocument doc)
    {
        if (doc == null) return null;
        if (!_strideCache.TryGetValue(doc, out var t))
        {
            if (!ClipStrideTrack.TryCompile(doc, _skeleton, out t, out _)) t = null;
            _strideCache[doc] = t;
        }
        return t;
    }

    // Non-null only on frames the planner ran for the active clip: those feet are
    // PLANNER-OWNED — RefreshContacts skips its SelfPlant capture/release lifecycle for
    // them and mirrors the planner's stance plans into _contacts instead (P3 handover).
    // Null (planner off / clip not opted in / no terrain) = the legacy path, bit-for-bit.
    private ClipStrideTrack _curPlanTrack;
    private bool PlannerOwned(int bone) => _curPlanTrack?.ForBone(bone) != null;
    // The solver config the LAST Update actually solved with — Current plus whatever the
    // move driver overrode that frame (FrameInputs.Solver). Diagnostics / tests.
    public AnimSolverConfig   SolverConfig => _frame.Solver;
    // The cadence's current per-frame phase rate Δφ (last solved / coasted step; the
    // legacy velocity-derived rate right after a clip change). Diagnostics / tests.
    public float              PhaseStep    => _prevPhaseStep;

    // Planner-owned contact count — diagnostics / tests only (proves the P3 handover
    // actually mirrors stance plans into the solve's contact list; the total count is
    // the Diagnostics partial's ContactCount).
    public int PlannedContactCount
    {
        get { int n = 0; foreach (var c in _contacts) if (c.Source == ContactSource.PlannedSupport) n++; return n; }
    }

    // Per-bone angle correction Δθ (radians) the solver applied this frame, by bone
    // index — the IK channel on top of the authored blend. Zero on frames with no
    // solve (flight / non-locomotion without pins). Diagnostic + tests.
    public float AngleCorrection(int bone)
        => _haveCorr && bone >= 0 && bone < _skeleton.Count ? _solveVars[IdxTheta0 + bone] : 0f;

    // Emitted vertical root offset δ (world px) to add on top of the host's baseline
    // placement (RigRoot) — the body's bob that keeps the planted foot grounded. The solved
    // value on a solve frame; on flight / non-locomotion frames with no solve it EASES toward
    // 0 (the com anchor) at the base ease rate rather than snapping there — see _dyEmitted.
    public float VerticalOffset => _dyEmitted;

    // Emitted horizontal root offset d.x (world px) — the body's slight fore-aft sway that
    // soaks the no-slip residual at a planted foot's horizontal turning point (where cadence
    // alone can't track the body). Added by the host beside VerticalOffset; eases like δ.
    public float HorizontalOffset => _dxEmitted;

    // Did a solve (cadence or static) run this frame? False on flight / plain non-locomotion
    // frames, where the offsets above are easing and AngleCorrection reads 0. Tests.
    public bool SolvedThisFrame => _haveCorr;

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

    public CharacterAnimator(Skeleton skeleton, float scale, IEnumerable<AnimationDocument> animations = null)
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
        _thetaEmitted = new float[rig.Count];
        _lambdaSmooth = new float[rig.Count];
        _easeB        = new float[rig.Count];
        _overlays  = new OverlayStack(rig, _regionMasks);
        _baseBlend = _overlays.BaseBlend;   // alias — the Jacobian reads the stack's array directly

        int I(string n) => rig.IndexOf(n);
        _hip = I("hip"); _chest = I("chest");
        _aimBoneL = I("arm_l_lower"); _aimBoneR = I("arm_r_lower");   // the stab-aim hand pair

        // Variables: Δφ + the root offset d = (δ, d.x) + per-bone Δθ (rig.Count), with a
        // little headroom. Residuals: two rows per contact (H no-slip + V ground) + two per
        // external pin + continuity + com + one prior per bone. Sized to the rig once.
        int nv = IdxTheta0 + rig.Count + 2;
        // 2/contact + 2/pin + (MaxSurfaces × bones) no-penetration + 1 aim + continuity
        // + phase-rate floor + com(δ, d.x: absolute tie + temporal smoothness = 4)
        // + bones Tikhonov + bones Δθ-smoothness
        // + headroom for driver-contributed constraint blocks (FrameInputs.Constraints).
        const int MaxContributedRows = 16;
        int nr = 2 * 4 + 2 * MaxPins + MaxSurfaces * rig.Count + 1 + 4 + 2 * rig.Count + 4
               + MaxContributedRows;
        _maxResiduals = nr;
        _ls = new LeastSquaresSolver(maxVars: nv, maxRes: nr);
        _solveVars = new float[nv];
        _solveLo   = new float[nv];
        _solveHi   = new float[nv];
        _problem   = new SolveProblem(rig);
        _eval      = new PoseEval(rig, nv);
        // The rig's REACH: longest root→tip cumulative bone length, in world px. The unit the
        // pixel residuals are expressed in (see _invCharLen). Computed once; topological bone
        // order (parents precede children) makes this a single pass.
        {
            var cum = new float[rig.Count];
            float reach = 0f;
            for (int i = 0; i < rig.Count; i++)
            {
                int par = rig.Bones[i].Parent;
                cum[i] = (par < 0 ? 0f : cum[par]) + rig.Bones[i].Length;
                reach = MathF.Max(reach, cum[i]);
            }
            _invCharLen = 1f / MathF.Max(reach * MathF.Abs(scale), 1e-3f);
        }
        // Which bones are torso (stiff Tikhonov λ_θ from config) vs limb (loose). Structural —
        // the WEIGHTS live in AnimSolverConfig so they hot-reload; this just tags the bones.
        _isCore = new bool[rig.Count];
        for (int i = 0; i < rig.Count; i++)
        {
            string nm = rig.Bones[i].Name;
            _isCore[i] = nm == "hip" || nm == "chest" || nm == "head";
        }
        // The problem's per-animator constants (everything else is frozen per solve).
        _problem.Overlays  = _overlays;  _problem.BaseBlend    = _baseBlend;
        _problem.Scale     = scale;      _problem.InvCharLen   = _invCharLen;
        _problem.IsCore    = _isCore;    _problem.LambdaSmooth = _lambdaSmooth;
        _problem.AimBoneL  = _aimBoneL;  _problem.AimBoneR     = _aimBoneR;
        _cadenceResiduals = (x, r)           => SolveObjective.Residuals(_problem, _eval, x, r);
        _cadenceJacobian  = (x, jac, stride) => SolveObjective.Jacobian(_problem, _eval, x, jac, stride);
        // The composite objective's core blocks (§11), assembled with any driver contributions
        // into _problem.Blocks each frame (step 1.8). Order is load-bearing: it IS the
        // residual/Jacobian row order the LM core and the FD-vs-analytic oracle assume.
        // Stateless blocks, preallocated once → zero per-frame allocation.
        _coreGeom = new ISolveConstraint[]
        {
            new PlantedContactsConstraint(),   // 2 rows/contact: H no-slip (Δφ) + V ground hold (δ)
            new SwingTargetConstraint(),       // 2 rows/planned swing foot: soft follow toward the landing
            new FixedPointConstraint(),        // 2 rows/pin: both-axis hard external pin (Δθ IK)
            new NoPenetrationConstraint(),     // 1 row/(surface×bone): half-plane limb push-out (Δθ/δ)
            new ActionAimConstraint(),         // 1 row: re-aim the action overlay along the input dir (Δθ)
        };
        _corePriors = new ISolveConstraint[]
        {
            new ComOffsetConstraint(),         // 4 rows: soft com pulls δ, d.x → baseline + their smoothness
            new PosePriorConstraint(),         // N rows: Tikhonov on each Δθ (toward 0)
            new ThetaSmoothnessConstraint(),   // N rows: final angle toward last EMITTED (the in-solve ease)
        };

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

        // The move-driver registry — after clip binding so drivers can resolve their
        // auxiliary clips (e.g. ParkourDriver's ClimbHands) from _actionClips.
        _drivers = MoveDrivers.CreateDefault(rig, _actionClips);
    }

    public void Update(in CharacterAnimSample s)
    {
        float dt = s.Dt;
        _haveCorr = false;   // cleared until a cadence solve produces Δθ this frame
        // This frame's EFFECTIVE solver config: a fresh copy of the (hot-reloaded) global,
        // which the move driver may override in step 1.4 before any solve reads it. Every
        // config read below and in the constraint rows goes through _frame.Solver.
        _frame.Solver.CopyFrom(AnimSolverConfig.Current);

        // 0. Fold this frame into the observed history the drivers select on (how long the
        //    body has been down, last frame's sample). Measurement only — no policy.
        _history.BeginFrame(in s, dt);

        // 1. Select the active MOVE DRIVER — the first registry entry whose situation matches —
        //    and ask it which clip to play, how its time is produced, and (optionally) where to
        //    start it. All per-move selection policy lives in the drivers (Animation/
        //    MoveDriver.cs); this loop is the entire selector. GroundLocomotionDriver matches
        //    unconditionally, so `driver` is never null.
        IMoveDriver driver = null;
        for (int di = 0; di < _drivers.Length; di++)
            if (_drivers[di].Matches(in s, in _state, in _history)) { driver = _drivers[di]; break; }
        ClipChoice choice = driver.Select(in s, in _state, in _history);
        AnimClip clip = choice.Clip;
        ClipTimeMode mode = choice.Time;
        // (The landing one-shot used to be overridden onto the driver's Idle choice here. It
        //  is LandingDriver now — the airborne→grounded edge it needs is in AnimHistory, so it
        //  is ordinary registry policy rather than a core special case.)
        // SETTLING (timing stage T3) — the one override left on the driver's choice: while the
        // stopping policy is finishing the last step, a cadence clip is held through the
        // driver's Idle choice so the landing completes on the locomotion clip; Idle takes
        // over once the phase holds (SupportedIdle) and the contacts release through the
        // ordinary handoff.
        //     The entry test is evaluated HERE on this frame's speed (GaitTiming.WantsSettle,
        //     the same predicate the stage applies): an abrupt stop drops below the idle band
        //     in the frame the driver first picks Idle, before any Settling state exists.
        if (clip == AnimClip.Idle && IsCadenceClip(_state.Clip) && _clips.ContainsKey(_state.Clip))
        {
            float spd = MathF.Abs(s.Velocity.X);
            float prevSpeed = _history.HasPrev ? MathF.Abs(_history.Prev.Velocity.X) : spd;
            bool settling = _lastTiming.State == TimingState.Settling
                || (_lastTiming.State == TimingState.Traveling
                    && GaitTiming.WantsSettle(s.Grounded, spd, prevSpeed, _frame.Solver.SettleSpeed));
            if (settling) { clip = _state.Clip; mode = ClipTimeMode.CadencePhase; }
        }

        float speed   = MathF.Abs(s.Velocity.X);
        bool hasClip  = _clips.TryGetValue(clip, out var anim);
        bool clipSwitched = clip != _state.Clip;

        if (clipSwitched)
        {
            _state.Clip = clip;
            _state.ClipTime = 0f;
            // (The rate needs no seed across the switch: the timing stage recomputes it from
            //  travel every frame, and the planner's pace estimate reads that.)
            // Entry override: a driver may place the new clip's start — MatchPose scans the
            // cycle for the phase closest to the pose already on screen (fall → run's flight
            // arc), StartT places it explicitly (future run→vault footing). StartT < 0 keeps
            // the default convention — ClipTime restarts, the locomotion phase PERSISTS.
            bool phaseMode = mode is ClipTimeMode.CadencePhase or ClipTimeMode.IdleBob
                                  or ClipTimeMode.Hold;
            if (choice.MatchPose && hasClip && phaseMode)
                _state.Phase = BestMatchingPhase(anim);
            else if (choice.StartT >= 0f)
            {
                if (phaseMode)
                    _state.Phase = Wrap01(choice.StartT);
                else if (hasClip)
                    _state.ClipTime = choice.StartT * anim.Duration;
            }
            // _thetaEmitted deliberately PERSISTS across the switch — the smoothness prior
            // measures the final angle against it, which is exactly what crossfades the pose
            // gap between the old and new clip (the retired ease's snap-then-follow, in-solve).
            // Contacts: transfer the stance support the incoming clip can carry at the entry
            // phase; release the rest (ANIMATION_OWNERSHIP_CONTRACT.md §4).
            TransferContacts(hasClip && mode == ClipTimeMode.CadencePhase ? anim : null, _state.Phase, in s);
        }
        else _state.ClipTime += dt;
        _timeMode = mode;
        _problem.WrapPhase = mode is ClipTimeMode.CadencePhase or ClipTimeMode.IdleBob or ClipTimeMode.Hold;
        bool locomotion = mode == ClipTimeMode.CadencePhase;   // the cadence-solvable clip family

        // 1.4 The driver's contributions to this frame's solve inputs — overlay requests,
        //     fixed-point pins, and (future) whole constraint blocks. FROZEN here, like every
        //     other solve input.
        _frame.Clear();
        driver.Contribute(in s, hasClip ? SampleT(anim, in s) : 0f, _frame);

        // 1.5 Resolve the overlay compositor NOW, before the cadence solve, so the solve
        //     optimizes the POST-BLEND skeleton — the feet of the composed pose, not the bare
        //     locomotion clip. Overlay poses are sampled ONCE here at their pinned τ; both the
        //     poses and the per-bone opacities are CONSTANT w.r.t. the solve vars, so the
        //     residual just re-applies the same linear blend (_overlays.Compose) before FK,
        //     and the Jacobian scales each base-layer column by the cached Π(1−w). The same
        //     frozen stack feeds the draw in step 3.5, so the skeleton the solver optimized is
        //     bit-identical to the one rendered.
        //     Slot 0 is the Action-FSM overlay (orthogonal to movement, resolved here); its τ
        //     is whatever progress the action REPORTS (ActionState.AnimationProgress — sweeps
        //     once over the activation however long it lasts), falling back to the clip's own
        //     seconds when the action declines to say. Slots 1+ serve the driver's requests.
        //     A clip with a SettleShare splits that timeline: the action's progress sweeps only
        //     the swing [0, 1−share]; the settle tail plays afterwards, down the recovery
        //     countdown (ResolveSettle), so the follow-through IS the end-lag.
        string actKey = IsOverlayAction(s.Action) ? s.Action : null;
        AnimationDocument actClip =
            actKey != null && _actionClips.TryGetValue(actKey, out var ac) ? ac : null;
        float actTau = 0f;
        if (actClip != null)
        {
            actTau = s.ActionProgress >= 0f ? MathHelper.Clamp(s.ActionProgress, 0f, 1f)
                   : AnimationSampler.NormalizedTime(actClip, s.ActionTime);
            if (s.ActionProgress >= 0f) actTau *= 1f - SettleShareOf(actClip);
            _settleKey = actKey; _settleClip = actClip; _settleTotal = 0;   // arm the settle
        }
        else if (!ResolveSettle(in s, out actKey, out actClip, out actTau))
            _settleClip = null;
        _overlays.Update(actKey, actClip, actTau, _frame.Overlays, dt);
        _state.ActionWeight = _overlays.ActionWeight;   // upper-body stiffness ramp + tests

        // 1.6 Per-bone smoothing weights for THIS frame (the in-solve ease — polish item 1).
        //     b_i = 1−exp(−k_i·dt) is the old framerate-independent ease factor; the upper-body
        //     rate ramps with ActionWeight so attacks snap, exactly as the retired BlendToward
        //     did. λs_i = λp_i·(1−b_i)/b_i makes the UNCONSTRAINED optimum of (Tikhonov +
        //     smoothness) equal that ease exactly — the per-region λp cancels, so torso and
        //     limbs follow at the same rate unless constrained. Computed before the solves so
        //     the LM path and the fast path share identical smoothing this frame.
        {
            var cfg0 = _frame.Solver;
            float bBase  = 1f - MathF.Exp(-Stiffness * dt);
            _easeBase = MathF.Max(bBase, 1e-4f);   // the root offset d eases at the base rate (see _dyEmitted)
            float upperK = Stiffness + (UpperBodyStiffness - Stiffness) * _state.ActionWeight;
            float bUpper = 1f - MathF.Exp(-upperK * dt);
            for (int i = 0; i < _skeleton.Count; i++)
            {
                float b  = MathF.Max(_upperMask[i] ? bUpper : bBase, 1e-4f);   // dt→0 guard
                float lp = _isCore[i] ? cfg0.CorePosePrior : cfg0.LimbPosePrior;
                _easeB[i]        = b;
                _lambdaSmooth[i] = lp * (1f - b) / b;
            }
        }

        // 1.7 Resolve this frame's external pins (sample → bone-index targets, then the
        //     driver's contributed pins) so the solve's FixedPointConstraint can read them.
        //     Frozen here for the whole solve. Unknown bone names and excess pins (> MaxPins)
        //     are dropped rather than reallocating the scratch.
        _pins.Clear();
        if (s.Pins != null)
            foreach (var pin in s.Pins)
            {
                if (_pins.Count >= MaxPins) break;
                int b = _skeleton.IndexOf(pin.Bone);
                if (b >= 0) _pins.Add((b, pin.Target));
            }
        foreach (var (bone, target) in _frame.Pins)
            if (_pins.Count < MaxPins) _pins.Add((bone, target));
        _surfaces.Clear();
        if (s.Surfaces != null)
        {
            // The sample's Surfaces may be an oversized reused scratch — SurfaceCount is the
            // logical count (-1 = whole array, the hand-built/test path).
            int srfCount = s.SurfaceCount < 0 ? s.Surfaces.Length : s.SurfaceCount;
            for (int i = 0; i < srfCount && _surfaces.Count < MaxSurfaces; i++)
                _surfaces.Add(s.Surfaces[i]);
        }
        _surfacesNear = s.SurfacesNear && _surfaces.Count > 0;
        ResolveActionAim(in s);

        // 1.8 Assemble this frame's composite objective: the geometric core head, then any
        //     driver-contributed blocks (still inside the geometric band, before the priors),
        //     then the prior tail. The list is FROZEN for the frame — both solves and the
        //     diagnostics walk it, and its order is the LM core's row order.
        _problem.Blocks.Clear();
        foreach (var c in _coreGeom)           _problem.Blocks.Add(c);
        foreach (var c in _frame.Constraints)  _problem.Blocks.Add(c);
        foreach (var c in _corePriors)         _problem.Blocks.Add(c);

        // 2. TIMING — advance the locomotion phase FIRST (Plans/ANIMATION_TIMING_STAGE.md;
        //    the timing stage is the phase's one owner, ANIMATION_OWNERSHIP_CONTRACT.md Rule
        //    A): this frame's actual body travel over the clip's authored cycle distance.
        //    Every CadencePhase clip advances this way, labeled or not; the planner, the
        //    contact refresh and the pose solve below all evaluate at the RESULTING phase.
        float phiEntry = _state.Phase;   // the smoothness rows measure deviation from THIS phase's base
        if (locomotion && hasClip)
        {
            var cfgT = _frame.Solver;
            // (T6) What the planted feet say the phase is, read from LAST frame's stance plans
            // (their support points are fixed in the world) against THIS frame's body. Not
            // across a clip switch: the plans belong to the old clip's track.
            bool observed = false; float observedPhase = 0f;
            _lastObservedResidual = 0f;
            if (cfgT.PhaseServoEnabled && !clipSwitched && _curPlanTrack != null)
                observed = GaitTiming.Observe(_curPlanTrack, Planner.Plans, Planner.FeetCount,
                                              s.Position, s.Facing, _scale, _state.Phase,
                                              out observedPhase, out _lastObservedResidual);
            _lastTiming = GaitTiming.Advance(new TimingInputs
            {
                Clip = anim, Gait = GaitTrackFor(anim),
                Phase = _state.Phase, PrevRate = _rate, Dt = dt,
                HasObserved = observed, ObservedPhase = observedPhase,
                ServoGain = cfgT.PhaseServoGain, ServoMaxRate = cfgT.PhaseServoMaxRate,
                RateSlew = cfgT.PhaseRateSlew, ReentryError = cfgT.PhaseReentryError,
                Pos = s.Position, PrevPos = _history.HasPrev ? _history.Prev.Position : s.Position,
                Facing = s.Facing, Scale = _scale, MaxStep = cfgT.MaxPhaseStep,
                State = _lastTiming.State, SettleRemaining = _lastTiming.SettleRemaining,
                SettleTimeLeft = _lastTiming.SettleTimeLeft,
                Speed = speed, Grounded = s.Grounded,
                PrevSpeed = _history.HasPrev ? MathF.Abs(_history.Prev.Velocity.X) : speed,
                SettleSpeed = cfgT.SettleSpeed, SettleExitSpeed = cfgT.SettleExitSpeed,
                IdleSpeed = GroundLocomotionDriver.WalkSpeedThreshold, SettleTime = cfgT.SettleTime,
            });
            _state.Phase   = Wrap01(_state.Phase + _lastTiming.DeltaPhase);
            _prevPhaseStep = _lastTiming.DeltaPhase;
            _rate          = _lastTiming.Rate;
        }
        else
        {
            if (_timeMode == ClipTimeMode.IdleBob) _state.Phase = Wrap01(_state.Phase + dt * IdleBobHz);
            _lastTiming.State = TimingState.Traveling;   // off the cadence family the policy is idle
        }

        // Step planner: runs for opted-in CadencePhase clips with terrain in the sample, at
        // the phase the timing stage produced, paced by its rate. Its stance plans are
        // mirrored into the contact list by RefreshContacts (P3 ownership handover).
        var planTrack = locomotion && hasClip && s.Chunks != null
                        && AnimSolverConfig.Current.PlannerEnabled
            ? StrideTrackFor(anim) : null;
        if (planTrack != null && planTrack.Feet.Length > 0)
        {
            Planner.Update(new PlannerInputs
            {
                BodyPos = s.Position, BodyVel = s.Velocity,
                Facing = s.Facing, Scale = _scale,
                Phase = _state.Phase,
                NominalRate = _rate, Dt = dt,
                Track = planTrack, Chunks = s.Chunks, PredictAt = s.PredictAt,
            });
            _curPlanTrack = planTrack;   // RefreshContacts' ownership predicate this frame
        }
        else
        {
            if (Planner.FeetCount > 0) Planner.Reset();
            _curPlanTrack = null;        // opted-in clips degrade to SelfPlant semantics
        }

        // 2.2 Contacts at the resulting phase, then the POSE solve (root offset + limbs; Δφ
        //     locked). Solve-root: the draw's placement (BodyPath.RootOffset — body minus the
        //     pose anchor c, both axes, facing/scale applied), so contact targets are captured
        //     in the SAME frame the pose is drawn and the solved d perturbs about it.
        if (locomotion && hasClip && HasContacts(anim))
        {
            _problem.Clip = anim; _problem.Body = s.Position; _problem.Dir = s.Facing == 0 ? 1 : s.Facing;
            var root = SolveForward.RootAt(_problem, _state.Phase);
            RefreshContacts(anim, _state.Phase, dt, root);
            if (_contacts.Count > 0) SolvePoseLm(anim, _state.Phase, phiEntry);
            // Flight (no planted contact): nothing to fit; the phase already advanced by travel.
        }
        else _contacts.Clear();

        // 2.5 Off-locomotion solve (Phase 3): the cadence path above only runs the LM solve
        //     for a locomotion clip with planted contacts (pins/surfaces/aim ride that SAME
        //     single solve there — one objective over the final pose, conflicts resolved by
        //     weights). But those external constraints must also engage on clips with no
        //     cadence to drive (wall slide, vault, an aimed stab from idle), so when no solve
        //     ran this frame and there IS something external to satisfy, run a STATIC solve —
        //     Δφ locked (no cadence here), only δ + the per-bone Δθ move.
        //     Gated on _surfacesNear, not raw surface presence: terrain planes exist near-
        //     permanently at margin 0 and are dormant until something is within the engage
        //     band — idle/flight frames keep the closed-form fast path.
        if (!_haveCorr && hasClip && (_pins.Count > 0 || _surfacesNear || _aimActive))
            SolveStaticPose(anim, in s);

        // 3. Build the target pose, sampled at the time the clip's TimeMode produces (phase
        //    for cadence/idle, normalized ClipTime for one-shots, MovementProgress for
        //    progress-driven maneuvers). Every clip category must have an authored file
        //    bound — no procedural fallback.
        if (!hasClip)
            throw new InvalidOperationException(
                $"No authored animation bound for clip '{clip}'. Add a SkeletonStates/*.json " +
                $"with Type=\"{clip}\" (loaded into CharacterAnimator).");

        _curDoc  = anim;
        _curComT = SampleT(anim, in s);
        AnimationSampler.SampleSmooth(anim, _curComT, _kfA, _kfB, _kfC, _kfD, _target);

        // 3.5 Paint the resolved overlay stack onto the base pose (Phase 4 motion layer).
        //     The stack was frozen in step 1.5 and composed identically inside the cadence
        //     solve, so the skeleton the solver optimized is the one drawn. Runs BEFORE
        //     lean/squash so those additive deltas stay continuous in the weight (run-slash
        //     keeps its lean; landing mid-air-slash still squashes).
        _overlays.Compose(_target);

        // 3.6 The smoothing/correction channel — ONE of two mutually exclusive paths, both
        //     minimizing the same objective (polish item 1):
        //     · An LM solve ran (_haveCorr): its Δθ already balances the geometric rows against
        //       the smoothness prior; apply it onto the COMPOSED pose (matching SolveForward.Run's
        //       order, so the drawn skeleton is the one the solver optimized; post-compose means
        //       a pin can bend an overlay-owned bone — the vault hand).
        //     · No geometric rows this frame: the objective is diagonal per bone and its optimum
        //       is closed-form — exactly the old exponential ease of the blend target from the
        //       last EMITTED pose: θ = emitted + b·wrap(target − emitted). This is the fast path
        //       (idle, flight, plain one-shots); no LM needed.
        if (_haveCorr)
            for (int i = 0; i < _skeleton.Count; i++)
                _target.Local[i].Rotation += _solveVars[IdxTheta0 + i];
        else if (_haveEmitted)
            for (int i = 0; i < _skeleton.Count; i++)
            {
                float g = MathHelper.WrapAngle(_target.Local[i].Rotation - _thetaEmitted[i]);
                _target.Local[i].Rotation = _thetaEmitted[i] + _easeB[i] * g;
            }

        // Capture the EMITTED angles — the smoothness target for next frame's solve/fast path.
        // Captured BEFORE lean/squash: those are post-solve additive layers, and folding them
        // into the target would feed back (the solver would learn the lean into Δθ and the lean
        // would then be applied twice). Persists across clip switches (that's the crossfade).
        for (int i = 0; i < _skeleton.Count; i++) _thetaEmitted[i] = _target.Local[i].Rotation;
        _haveEmitted = true;
        // The root offset d follows the same rule: a solve frame emits the solved d (which the
        // com block's smoothness rows already pulled toward last frame's); a no-solve frame
        // eases it toward the baseline at the base rate — the closed-form of the same prior
        // with nothing else in the objective — so flight ↔ stance edges are continuous.
        if (_haveCorr) { _dyEmitted = _solveVars[IdxDy]; _dxEmitted = _solveVars[IdxDx]; }
        else           { _dyEmitted *= 1f - _easeBase;   _dxEmitted *= 1f - _easeBase; }

        // (3b retired: the procedural locomotion lean is authored into the walk/run clips.)

        // (3c retired: the procedural landing squash is replaced by the authored Land
        //  one-shot selected in step 1 — pose-driven, no post-solve scale/translate hack.)

        // 4. Emit. The target IS the pose now — smoothing already happened inside the solve /
        //    fast path (the retired BlendToward is the closed form of that objective), so a
        //    constrained tip (pin, planted foot) is satisfied on the RENDERED skeleton.
        _pose.CopyFrom(_target);

        _history.EndFrame(in s);
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
    public bool OverlayActive => _overlays.ActionBound;

    // The same clip clocks and final pose used by the skeleton, including settle.
    // Unbound overlays contribute no attachments while their pose eases away.
    public void SampleAttachments(List<AttachmentSample> output)
    {
        output.Clear();
        AttachmentSampling.Append(_curDoc, _curComT, 1f, output, _skeleton);
        _overlays.AppendAttachments(output);
    }

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
        var pose = (fromOverlay && _overlays.ActionBound) ? _overlays.ActionPose : _pose;
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
        => BodyPath.TrySampleAnchor(_curDoc, _curComT, out comLocal, out _);

    // --- clip selection ------------------------------------------------------
    // (Clip selection policy lives in the move drivers — Animation/MoveDriver.cs. The old
    //  SelectClip if-chain became the driver registry's order; per-branch rationale moved
    //  onto the drivers themselves.)

    // Whether an action name should drive the overlay layer. NullAction/ReadyAction/
    // RecoveryAction read as "no action" — the overlay fades out through them, which
    // is also what bridges the gaps inside a slash combo. This string policy lives
    // here (not in the sample) for the same reason the move drivers own clip policy:
    // the sample stays a dumb snapshot.
    private static bool IsOverlayAction(string action)
        => !string.IsNullOrEmpty(action)
           && action != "None" && action != "NullAction"
           && action != "ReadyAction" && action != "RecoveryAction";

    private static float SettleShareOf(AnimationDocument clip)
        => MathHelper.Clamp(clip.SettleShare, 0f, 0.95f);

    // Is this frame a settle frame: the FSM sits in RecoveryAction with the player's own
    // countdown still running, and the last swing's clip authored a settle tail? Distinct
    // from a settle BEING DRAWN — the ease can still be fading a swing that authored none.
    private bool SettleWanted(in CharacterAnimSample s)
        => s.Action == "RecoveryAction" && s.RecoveryFramesLeft > 0
           && _settleClip != null && SettleShareOf(_settleClip) > 0f;

    // Keep the last swing's overlay bound through the recovery countdown, its τ walking the
    // settle tail [1−share, 1] as the frames run out. False when there's nothing to settle
    // (no countdown, no tail authored, or the FSM moved on) — the caller then unbinds and
    // the ordinary fade-out takes over.
    private bool ResolveSettle(in CharacterAnimSample s, out string key,
                               out AnimationDocument clip, out float tau)
    {
        key = null; clip = null; tau = 0f;
        if (!SettleWanted(in s)) return false;
        _settleTotal = Math.Max(_settleTotal, s.RecoveryFramesLeft);
        float progress = 1f - (float)s.RecoveryFramesLeft / _settleTotal;
        float share    = SettleShareOf(_settleClip);
        key  = _settleKey;
        clip = _settleClip;
        tau  = (1f - share) + share * progress;
        return true;
    }

    // The current clip's normalized sample time under its TimeMode: cadence/idle clips play
    // off the wrapped phase; one-shots off normalized ClipTime (held at the end); progress-
    // driven maneuvers off the movement's spatial progress. Shared by step 3's sampling,
    // _curComT, and the static solve, so they all address the same pose.
    private float SampleT(AnimationDocument anim, in CharacterAnimSample s) => _timeMode switch
    {
        ClipTimeMode.CadencePhase or ClipTimeMode.IdleBob or ClipTimeMode.Hold => _state.Phase,
        ClipTimeMode.Progress => MathHelper.Clamp(s.MovementProgress, 0f, 1f),
        _ => AnimationSampler.NormalizedTime(anim, _state.ClipTime),
    };

    // The phase of `anim`'s cycle whose pose is closest to the pose we last EMITTED — the
    // MatchPose entry (ClipChoice.MatchPose): entering a cycle clip from an arbitrary pose
    // (a fall settling into the run) starts it where the visual discontinuity is smallest,
    // and the smoothness prior then bridges the small remaining gap. Distance is the summed
    // squared wrapped angle difference over all bones (uniform weights — the torso/limb
    // distinction matters little for a coarse 32-way scan). Uses the _kf quad + _scratch;
    // runs only on a clip-change frame, before any solve, so the scratch reuse is safe.
    private float BestMatchingPhase(AnimationDocument anim)
    {
        if (!_haveEmitted) return 0f;
        const int K = 32;
        float best = 0f, bestD = float.MaxValue;
        for (int k = 0; k < K; k++)
        {
            float t = k / (float)K;
            AnimationSampler.SampleSmooth(anim, t, _kfA, _kfB, _kfC, _kfD, _scratch);
            float d = 0f;
            for (int i = 0; i < _skeleton.Count; i++)
            {
                float e = MathHelper.WrapAngle(_scratch.Local[i].Rotation - _thetaEmitted[i]);
                d += e * e;
            }
            if (d < bestD) { bestD = d; best = t; }
        }
        return best;
    }

    // --- cadence solver ------------------------------------------------------

    private static bool HasContacts(AnimationDocument clip) => clip?.Contacts is { Count: > 0 };

    // Contact weights at `phase`, written into _weightBuf as (bone, weight, dweight) merged by
    // bone (§5.2), where dweight = dw/dφ. Each span evaluates its own curve; a foot swap is a
    // smooth crossover because the two spans OVERLAP with eased ends, not because a fixed-width
    // feather is applied on top. The derivative's SIGN tells RefreshContacts which side of a
    // crossover a contact is on (dw/dφ < 0 = release has begun → the time-fade floor engages;
    // see RefreshContacts / the foot-swap deadlock).
    //
    // The keyframe ring is gone from this: spans carry their own interval and their own wrap
    // (ContactSpan.Covers retries at φ+1), so there is no open-tail special case to keep in
    // step with AnimationSampler, and dw/dφ is analytic inside a span rather than a step
    // function with corners at the feather clamps.
    private void WeightedContactsAtPhase(AnimationDocument clip, float phase)
    {
        _weightBuf.Clear();
        var spans = clip.Contacts;
        if (spans == null) return;
        foreach (var c in spans)
        {
            if (!c.Covers(phase, out _)) continue;
            float w = c.WeightAt(phase);
            if (w <= 0f) continue;
            // Spans resolve through the endpoint resolver, which THROWS on one this solve
            // cannot honor (unresolvable, or not an exact bone tip) rather than dropping it.
            int b = EndpointResolver.BoneOf(_skeleton, clip, c.Point);
            float dw = c.SlopeAt(phase);
            int at = -1;
            for (int k = 0; k < _weightBuf.Count; k++) if (_weightBuf[k].bone == b) { at = k; break; }
            if (at >= 0) _weightBuf[at] = (b, _weightBuf[at].weight + w, _weightBuf[at].dweight + dw);
            else         _weightBuf.Add((b, w, dw));
        }
    }

    // Refresh active contacts from the feathered weights: drop those that faded to ~0,
    // update held ones' weights, and lazily capture newly-appearing ones (world tip at
    // the current phase, while their weight is still small — §5.2). SelfPlant only for
    // now (External = Phase 5).
    private void RefreshContacts(AnimationDocument clip, float phase, float dt, in Affine2 root)
    {
        WeightedContactsAtPhase(clip, phase);

        // Held contacts: weight = the phase-feathered value — except once RELEASE has begun
        // (the contact sits on the FADING side of a crossover, dw/dφ < 0), the fade also
        // advances by TIME, taking the smaller of the two. This breaks the FOOT-SWAP
        // DEADLOCK: at low walk speed the solve can park mid-feather (advancing φ is locally
        // uphill against the old foot's slip, and the momentum prior pins Δφ=0), and since
        // the weight only faded with φ, the old contact then held its grip forever — legs
        // frozen. Time continues the release the feather already started, the old foot lets
        // go within ContactReleaseTime, and the new foot's no-slip pulls the cycle forward
        // again. At healthy cadence the phase fade is faster and the time floor never bites.
        // (The weight must stay FROZEN inside the solve itself — see PlantedContactsConstraint.)
        float timeFade = dt / MathF.Max(1e-3f, _frame.Solver.ContactReleaseTime);
        // ENGAGE mirror of the release floor: a contact's weight may RISE by at most this much
        // per frame, from a capture at (at most) this much. The phase feather alone is too
        // short in TIME at run cadence — a 0.12-phase crossover at Δφ ≈ 0.07/frame is under
        // two frames, so a re-contact went 0 → 0.76 → 1.0 and the ground-hold row yanked the
        // root to the new foot in one frame (the landing jerk). Ramping over ContactEngageTime
        // lets δ and the leg share the landing across several frames at any cadence; at a slow
        // walk the phase feather is the slower of the two and this floor never bites.
        float timeRamp = dt / MathF.Max(1e-3f, _frame.Solver.ContactEngageTime);
        for (int i = _contacts.Count - 1; i >= 0; i--)
        {
            var c = _contacts[i];
            // Planner-owned bones: the SelfPlant lifecycle below never touches them.
            // Their contacts are maintained by the plan mirror at the end (stance =
            // upsert, anything else = prompt removal — the documented toe-off rule).
            if (PlannerOwned(c.Bone))
            {
                if (c.Source != ContactSource.PlannedSupport) _contacts.RemoveAt(i);   // handover
                continue;
            }
            float w = WeightOf(c.Bone);
            w = MathF.Min(w, c.Weight + timeRamp);
            if (DWeightOf(c.Bone) < 0f) w = MathF.Min(w, c.Weight - timeFade);
            // Deliberately NO slew floor on release (tried 2026-08-26): a fast cadence steps
            // clean over the release ramp (Δφ 0.078 vs a ~0.12-phase crossover) and a full-weight
            // contact drops in one frame — but holding it for a time ramp instead dragged the
            // old foot's ground-hold through toe-off (δ → +6px) and stalled the cadence on
            // its no-slip row (Δφ → 0.01), the foot-swap deadlock all over again. Toe-off
            // must let go promptly; the root's continuity across it is the emitted offsets'
            // ease (_dyEmitted), not a lingering contact.
            if (w <= 1e-3f) { _contacts.RemoveAt(i); continue; }
            c.Weight = w;
            _contacts[i] = c;
        }

        bool needWorld = false;
        foreach (var (bone, w, _) in _weightBuf)
            if (w > 1e-3f && ActiveIndex(bone) < 0 && !PlannerOwned(bone)) { needWorld = true; break; }
        if (needWorld)
        {
            AnimationSampler.SampleSmooth(clip, phase, _kfA, _kfB, _kfC, _kfD, _scratch);
            _overlays.Compose(_scratch);   // capture the target on the COMPOSED pose (= what we measure)
            _scratch.ComputeWorld(root);
        }

        foreach (var (bone, w, _) in _weightBuf)
        {
            if (w <= 1e-3f || ActiveIndex(bone) >= 0 || PlannerOwned(bone)) continue;   // held ones updated above
            Vector2 tip = _scratch.WorldOf(bone).Translation;     // bone's far end = contact tip
            _contacts.Add(new ActiveContact { Bone = bone, Target = SnapToSupport(bone, tip),
                                              Weight = MathF.Min(w, timeRamp),   // capture SMALL, ramp in
                                              Source = ContactSource.SelfPlant });
        }

        // ── Plan mirror (planner-owned feet only) ────────────────────────────────
        // Stance with support ⇒ one PlannedSupport contact at the plan's fixed point,
        // weight = the planner's engage ramp (same config constants as the legacy path).
        // Everything else ⇒ no contact: toe-off lets go promptly, per the release-slew
        // decision above — the swing is the SwingTargetConstraint's job, never a plant.
        if (_curPlanTrack != null)
            for (int i = 0; i < Planner.FeetCount; i++)
            {
                ref readonly var p = ref Planner.Plans[i];
                int at = ActiveIndex(p.Bone);
                bool want = p.State == FootPlanState.Stance && p.HasSupport && p.Weight > 1e-3f;
                if (!want)
                {
                    if (at >= 0) _contacts.RemoveAt(at);
                    continue;
                }
                var mirrored = new ActiveContact { Bone = p.Bone, Target = p.Target,
                                                   Weight = p.Weight,
                                                   Source = ContactSource.PlannedSupport };
                if (at >= 0) _contacts[at] = mirrored;
                else _contacts.Add(mirrored);
            }
    }

    // A CLIP SWITCH's contact handoff (ANIMATION_OWNERSHIP_CONTRACT.md §4; timing-stage T4).
    // A stance contact TRANSFERS to `incoming` iff the same bone is a support owner there (a
    // label of any source), the entry phase falls inside that bone's stance in the incoming
    // clip (never into an incoming swing just because the name matches), and the target is
    // within the incoming gait's reach of the body. Ownership follows the incoming clip's
    // label source (a self-plant foot becomes planner-owned across run → walk, and back), and
    // the planner adopts the transferred support point so it does not re-select and pop.
    // Everything else releases promptly — the emitted-offset ease and the Δθ smoothness prior
    // carry the visual continuity, as at any toe-off. `incoming` null = no cadence clip.
    private void TransferContacts(AnimationDocument incoming, float phase, in CharacterAnimSample s)
    {
        var gait = incoming != null ? GaitTrackFor(incoming) : null;
        var planTrack = incoming != null && s.Chunks != null && AnimSolverConfig.Current.PlannerEnabled
            ? StrideTrackFor(incoming) : null;
        Span<(int Bone, Vector2 Target, float Weight)> adopted = stackalloc (int, Vector2, float)[StepPlanner.MaxFeet];
        int nAdopted = 0;
        for (int i = _contacts.Count - 1; i >= 0; i--)
        {
            var c = _contacts[i];
            var ft = gait?.ForBone(c.Bone);
            bool keep = ft != null && ft.StanceAt(phase, out _) >= 0
                        && (c.Target - s.Position).Length() <= ft.MaxReachRig * _scale;
            if (!keep) { _contacts.RemoveAt(i); continue; }
            bool planned = planTrack?.ForBone(c.Bone) != null;
            c.Source = planned ? ContactSource.PlannedSupport : ContactSource.SelfPlant;
            _contacts[i] = c;
            if (planned && nAdopted < adopted.Length) adopted[nAdopted++] = (c.Bone, c.Target, c.Weight);
        }
        Planner.Rebind(planTrack, adopted.Slice(0, nAdopted), s.Chunks);
        // A transferred planner-owned foot whose point found no tread under it is dropped by
        // the mirror on this frame's RefreshContacts (Rebind left it Unplanned) — the
        // documented toe-off rule, not a special case.
    }

    // Snap a freshly captured plant onto the terrain face that supports it.
    //
    // Without this a SelfPlant target is purely SELF-referential: it holds the foot at
    // whatever height the com anchor happened to place the rig, and NOTHING else can pull it
    // down. NoPenetration is one-sided at margin 0 (a foot at gap 0 is exactly inactive), and
    // SkipPair additionally mutes it under a plant on the premise that this contact's V-row
    // owns "foot sits on ground" — which it could not, having no ground in it. So a clip whose
    // com.Y missed the addcom identity (com.Y = soleLocal − 2·Radius/scale) hovered permanently,
    // invisible to the objective because the target moved with the rig. Snapping makes the
    // V-row genuinely own ground contact, which in turn makes SkipPair's premise true.
    //
    // The move is VERTICAL, not along the normal, so the horizontal no-slip anchor stays
    // exactly where the rig put it — that row drives the cadence and must not be perturbed by
    // the ground. Falls through unchanged when no face supports the toe (no terrain extracted,
    // or the plant is genuinely mid-air), so the com anchor remains the fallback.
    private Vector2 SnapToSupport(int bone, Vector2 tip)
    {
        float best = ContactSupportBand, drop = 0f;
        foreach (var s in _surfaces)
        {
            if (((s.BoneMask >> bone) & 1) == 0) continue;
            if (s.Normal.Y > -0.7f) continue;                     // upward-facing only (y-down)
            float gap = s.Normal.X * (tip.X - s.Point.X) + s.Normal.Y * (tip.Y - s.Point.Y);
            float d = MathF.Abs(gap);
            if (d >= best) continue;                              // outside the band, or a nearer face won
            best = d;
            drop = -gap / s.Normal.Y;                             // vertical move onto the plane
        }
        return best < ContactSupportBand ? new Vector2(tip.X, tip.Y + drop) : tip;
    }

    // The locomotion POSE solve: the full composite objective (planted-foot no-slip + ground
    // hold, swing targets, pins, surfaces, aim, priors) minimized over the root offset d and
    // the per-bone Δθ by the general LM core, at the phase the timing stage already resolved
    // (Δφ locked to 0 — the timing stage owns it; the column is removed in chunk 8's diet).
    // NOTE (historical): only the HORIZONTAL component of a planted contact ever drove the
    // cadence. The foot's vertical arc (lift over the stance) is intrinsic to the cycle and is
    // reconciled by the ground-hold row + δ (see PlantedContactsConstraint).
    private void SolvePoseLm(AnimationDocument clip, float phi, float phiEntry)
    {
        // (Clip/Body/Dir were placed by the caller, step 2.2, before RefreshContacts captured.)
        var cfg = _frame.Solver;
        int n = IdxTheta0 + _skeleton.Count;  // x = [Δφ, δ, d.x, Δθ_0…]

        _solveLo[IdxPhi] = 0f;                    _solveHi[IdxPhi] = 0f;   // Δφ locked — timing owns it
        _solveLo[IdxDy]  = -cfg.VertOffsetLimit;  _solveHi[IdxDy]  = cfg.VertOffsetLimit;
        _solveLo[IdxDx]  = -cfg.HorizOffsetLimit; _solveHi[IdxDx]  = cfg.HorizOffsetLimit;
        for (int i = IdxTheta0; i < n; i++) { _solveLo[i] = -cfg.AngleCorrLimit; _solveHi[i] = cfg.AngleCorrLimit; }
        Array.Clear(_solveVars, 0, n);        // d, Δθ start at 0 (baseline pose)
        FreezeProblem(clip, phi, phiEntry, n);   // every input the rows read, incl. t_i (entry base) and û*

        // Δθ starts at 0 (not warm-started): the θ-smoothness prior supplies the temporal
        // continuity from the COST side (its target is last frame's EMITTED pose), and a
        // box-clamped warm seed would stick the solution at the wall.
        _ls.Minimize(_cadenceResiduals, _cadenceJacobian,
                     _solveVars.AsSpan(0, n), _solveLo.AsSpan(0, n), _solveHi.AsSpan(0, n),
                     vectorize: _frame.Solver.CadenceVectorize);
        CaptureBreakdown(n);
        _haveCorr = true;
    }

    // Off-locomotion solve (Phase 3): satisfy this frame's external pins + no-penetration
    // surfaces on a clip with no cadence to drive. Δφ is LOCKED (box [0,0]) — there is no
    // planted-foot no-slip here — so only δ (the body bob) and the per-bone Δθ (the IK that
    // bends limbs off a wall / onto a pin) move. The base pose is sampled at the SAME phase /
    // clip-time step 3 draws at, so the solved Δθ line up when applied there. Mirrors
    // SolvePoseLm's root construction (com baseline so capture/solve/draw share a frame).
    // Dormancy slack for the static solve's pre-check: a masked tip must press past its
    // plane by more than this (px) before the LM solve engages. Sub-pixel sink for one
    // frame is invisible; the solve, once it runs, still resolves to the full margin.
    private const float StaticSolveSlack = 0.75f;

    private void SolveStaticPose(AnimationDocument anim, in CharacterAnimSample s)
    {
        var cfg = _frame.Solver;
        float phi = SampleT(anim, in s);

        _contacts.Clear();                  // no planted contacts on this path
        _problem.Clip = anim; _problem.Body = s.Position; _problem.Dir = s.Facing == 0 ? 1 : s.Facing;
        var root = SolveForward.RootAt(_problem, _problem.TimeAt(phi));   // the dormancy pre-check below reads it directly
        int n = IdxTheta0 + _skeleton.Count;
        _solveLo[IdxPhi] = 0f;                    _solveHi[IdxPhi] = 0f;   // Δφ locked — no cadence here
        _solveLo[IdxDy]  = -cfg.VertOffsetLimit;  _solveHi[IdxDy]  = cfg.VertOffsetLimit;
        _solveLo[IdxDx]  = -cfg.HorizOffsetLimit; _solveHi[IdxDx]  = cfg.HorizOffsetLimit;
        for (int i = IdxTheta0; i < n; i++) { _solveLo[i] = -cfg.AngleCorrLimit; _solveHi[i] = cfg.AngleCorrLimit; }
        Array.Clear(_solveVars, 0, n);
        // Dormancy gate (perf). With no pins and no aim, the only geometric rows are the
        // no-pen half-planes — and those are margin-0/inactive on almost every frame the
        // body merely stands NEAR terrain (feet resting at gap ≈ 0 keep the engage band
        // lit permanently). Running the LM solve then just re-derives the closed-form
        // ease at ~40× the cost, every grounded frame — the per-frame hitch that made
        // mass terrain destruction lag (a fresh crater puts every limb "near" a face).
        // One forward pass decides: solve only when some masked tip actually presses
        // past its plane; otherwise leave _haveCorr false → step 3.6's fast path.
        if (_pins.Count == 0 && !_aimActive)
        {
            // Evaluate at the pose the FAST PATH would draw (sample → compose → ease
            // toward emitted, step 3.6's else-branch) — not the raw x = 0 sample: the
            // clip may plant tips slightly past a plane before the smoothness ease pulls
            // them back to last frame's (already-solved, clear) emitted pose. Checking
            // the drawn candidate makes skip ⇒ the drawn pose really is clear.
            AnimationSampler.SampleSmooth(anim, _problem.TimeAt(phi), _kfA, _kfB, _kfC, _kfD, _scratch);
            _overlays.Compose(_scratch);
            if (_haveEmitted)
                for (int i = 0; i < _skeleton.Count; i++)
                {
                    float g = MathHelper.WrapAngle(_scratch.Local[i].Rotation - _thetaEmitted[i]);
                    _scratch.Local[i].Rotation = _thetaEmitted[i] + _easeB[i] * g;
                }
            _scratch.ComputeWorld(root);
            float worst = float.MinValue;
            foreach (var srf in _surfaces)
                for (int b = 0; b < _skeleton.Count; b++)
                {
                    if (((srf.BoneMask >> b) & 1) == 0) continue;
                    Vector2 tip = _scratch.WorldOf(b).Translation;
                    float gap = srf.Normal.X * (tip.X - srf.Point.X)
                              + srf.Normal.Y * (tip.Y - srf.Point.Y);
                    worst = MathF.Max(worst, srf.Margin - gap);
                }
            if (worst <= StaticSolveSlack) return;   // all rows dormant — nothing to solve
        }
        FreezeProblem(anim, phi, phi, n);

        _ls.Minimize(_cadenceResiduals, _cadenceJacobian,
                     _solveVars.AsSpan(0, n), _solveLo.AsSpan(0, n), _solveHi.AsSpan(0, n),
                     ftol: cfg.StaticFtol, vectorize: cfg.StaticVectorize);
        CaptureBreakdown(n);
        _haveCorr = true;
    }

    // FREEZE this solve's problem (SolveProblem.cs): copy every input a residual or Jacobian may
    // read — the geometric rows (contacts, planner swing targets, pins, surfaces, the aim), the
    // config snapshot, the prior anchors — then capture the two reference-pose targets (t_i, û*)
    // off the x = 0 forward pass. The placement (Clip/Body/Dir) is the caller's: it was set
    // before RefreshContacts captured its targets in that frame. `n` = the variable count, and
    // _solveVars must be all-zero (the reference-pose captures evaluate it). After this nothing
    // writes _problem until the solve ends; the forward pass and every block are pure in it.
    // `phiEntry` is the phase the frame ENTERED with (before the timing stage advanced it):
    // the smoothness targets t_i are measured against the base pose there, so this frame's
    // clip playback (base(φ) − base(φ_entry)) stays free of the smoothing rows — charging it
    // was the absolute-pose smoothing that dragged the cadence (ThetaSmoothnessConstraint).
    private void FreezeProblem(AnimationDocument clip, float phi, float phiEntry, int n)
    {
        var p = _problem;
        p.Clip = clip; p.Phi = phi;
        p.Contacts.Clear(); p.Contacts.AddRange(_contacts);
        p.Pins.Clear();     p.Pins.AddRange(_pins);
        p.Surfaces.Clear(); p.Surfaces.AddRange(_surfaces);
        p.Swings.Clear();
        for (int i = 0; i < Planner.FeetCount; i++)
        {
            ref readonly var pl = ref Planner.Plans[i];
            if (pl.State == FootPlanState.Swing && pl.HasSupport) p.Swings.Add((pl.Bone, pl.Target));
        }
        p.AimActive      = _aimActive;
        p.Cfg            = _frame.Solver;
        p.EaseBase       = _easeBase;
        p.DyEmitted      = _dyEmitted;
        p.DxEmitted      = _dxEmitted;
        p.Phi = phiEntry;
        FillSmoothTargets(n);                 // freeze t_i (emitted deviation from the ENTRY base) before any residual eval
        p.Phi = phi;
        CaptureAimTarget(n);                  // freeze û* from the reference pose before any residual eval
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

    // dw/dφ companion of WeightOf — the sign tells RefreshContacts whether a contact is on
    // the FADING side of a feather crossover (release has begun).
    private float DWeightOf(int bone)
    {
        foreach (var e in _weightBuf) if (e.bone == bone) return e.dweight;
        return 0f;
    }

    // --- helpers -------------------------------------------------------------

    // (The overlay slot machinery — paint/bind/ease/claim — lives in OverlayStack; the
    //  vault's movement overlay + grip-pin policy lives in ParkourDriver.)

    // Resolve this frame's action aim (§STAB_AIM_PLAN). The sample carries the world aim direction
    // (a stab's StabDir); the animator owns which bones encode the aim (the L→R hand pair) and so
    // freezes _aimActive/_aimDir/_aimFacing for the solve. The target û* is captured at solve start
    // (CaptureAimTarget) once the reference pose is built. HasAim is only set for aimed actions.
    private void ResolveActionAim(in CharacterAnimSample s)
    {
        _aimActive = false;
        if (_aimBoneL < 0 || _aimBoneR < 0) return;
        // Through the settle the FSM is in RecoveryAction, which has no aim of its own — the
        // stab's arm would snap back to horizontal for its follow-through. Hold the swing's
        // aim until the settle ends.
        bool hasAim = s.HasAim;
        var  aim    = s.AimDir;
        if (!hasAim && SettleWanted(in s) && _settleHasAim) { hasAim = true; aim = _settleAim; }
        else if (!SettleWanted(in s)) { _settleHasAim = hasAim; _settleAim = aim; }
        if (!hasAim) return;
        if (aim.LengthSquared() < 1e-6f) return;
        _aimDir    = Vector2.Normalize(aim);
        _aimFacing = s.Facing == 0 ? 1 : s.Facing;
        _aimActive = true;
    }

    // Freeze this solve's smoothness targets t_i = wrapAngle(emitted_i − composedEntry_i): the
    // deviation of last frame's EMITTED pose from THIS frame's composed base at the entry phase
    // (Δφ = Δθ = 0 — SolveForward.Run at the zeroed vars leaves that base in _eval.Pose.Local).
    // The ThetaSmoothnessConstraint pulls each Δθ_i toward t_i, which is exactly the retired
    // ease's "follow from where you were" — measured in DEVIATION space so clip playback is
    // free (see the constraint's comment). Before the first drawn frame the targets are 0
    // (rows degrade to an extra Tikhonov — harmless for one solve). Must run while _solveVars
    // is all-zero, before the Δφ seed search evaluates any residual.
    private void FillSmoothTargets(int n)
    {
        var t = _problem.SmoothTarget;
        if (!_haveEmitted) { Array.Clear(t, 0, _skeleton.Count); return; }
        SolveForward.Run(_problem, _solveVars.AsSpan(0, n), _eval);   // all-zero ⇒ composed base at the entry phase
        for (int i = 0; i < _skeleton.Count; i++)
            t[i] = MathHelper.WrapAngle(_thetaEmitted[i] - _eval.Pose.Local[i].Rotation);
    }

    // Freeze the aim target û* for this frame's solve: the authored reference aim (the L→R hand
    // vector of the Δθ=0 composed pose) ROTATED by the stab's deviation from horizontal-forward
    // f=(facing,0). Rotating the reference (rather than aiming a fixed vector) preserves the clip's
    // windup→thrust dynamics. Called at solve start, when _solveVars is all-zero (the reference).
    private void CaptureAimTarget(int n)
    {
        if (!_aimActive) return;
        SolveForward.Run(_problem, _solveVars.AsSpan(0, n), _eval);   // _solveVars == 0 here ⇒ Δθ=0, Δφ=0 reference pose
        Vector2 pL = _eval.Pose.WorldOf(_aimBoneL).Translation;
        Vector2 pR = _eval.Pose.WorldOf(_aimBoneR).Translation;
        Vector2 aRef = pR - pL;
        // R takes f=(facing,0) → _aimDir: cosθ = f·d = facing·d.x, sinθ = f×d = facing·d.y (both unit).
        float c = _aimFacing * _aimDir.X, sgn = _aimFacing * _aimDir.Y;
        Vector2 rot = new Vector2(aRef.X * c - aRef.Y * sgn, aRef.X * sgn + aRef.Y * c);
        float len = rot.Length();
        _problem.AimTarget = len > 1e-6f ? rot / len : new Vector2(_aimFacing, 0f);
    }

    private void Rot(int bone, float delta)       { if (bone >= 0) _target.Local[bone].Rotation    += delta; }
    private void Translate(int bone, Vector2 d)    { if (bone >= 0) _target.Local[bone].Translation += d;     }
    private void Scale(int bone, Vector2 d)        { if (bone >= 0) _target.Local[bone].Scale       += d;     }

    private static float Wrap01(float x) => SolveProblem.Wrap01(x);
}
