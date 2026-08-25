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
public enum AnimClip { Idle, Walk, WalkBack, Crouch, CrouchWalk, DuckUnder, Jump, Fall, Parkour, Run, WallSlide, Hang, Hitstun, Tumble, WallJumpKick, DoubleJumpFlip, RunTurn, Land, LedgeJump, Dropdown, Mantle, ArcJump, LedgePull }

// The animation-side state, deliberately separate from any character/sim state.
// The animator owns and evolves this; it is the "previous state" the animator is
// allowed to remember between frames (alongside the previous sample).
public struct CharacterAnimState
{
    public AnimClip Clip;         // currently-selected clip
    public float    ClipTime;     // seconds spent in the current clip
    public float    Phase;        // locomotion cycle phase, wrapped to [0,1)
    public float    LandTime;     // counts down from LandClipTime after a touchdown — while
                                  // positive, a near-idle landing plays the authored Land
                                  // one-shot (replaced the old procedural hip squash)
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
    private const float LandClipTime       = 0.25f;  // s the Land one-shot owns Idle after touchdown
                                                     // (keep == land.json Duration)
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
    // private const float WalkLean            = 0.25f;  // torso lean at full walk speed
    // private const float WalkLeanRefSpeed    = 160f;   // px/s at which lean reaches max

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
    private readonly SkeletonPose _scratch;     // solve scratch: the composed, Δθ-corrected pose
    // DESIGN INVARIANT (decision 2026-07-14): every constraint evaluates the FINAL composed,
    // Δθ-corrected pose — the one that gets drawn. No constraint reads an intermediate pose,
    // and there is exactly ONE solve per frame over the full objective; conflicts between
    // constraints (a pin bending a planted leg, no-pen pushing a foot) are resolved by their
    // WEIGHTS, not by structure. Two structural alternatives were tried and rejected: contacts
    // on a Δθ-free pose (the drawn foot then slips by the Δθ contribution the constraint never
    // sees) and a two-stage cadence/IK split (hides objective misspecification instead of
    // surfacing it as a weight problem). The foot-swap stall that motivated them is fixed at
    // its actual root — contact RELEASE bookkeeping (see RefreshContacts' time fade).

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
    private const int MaxSurfaces = 8;   // sizes the residual scratch; excess surfaces are dropped
                                         // (terrain extraction emits a handful + the wall plane)
    // How near an upward-facing face must be to a toe to count as SUPPORTING its plant. One
    // meaning, two users that must agree: SnapToSupport captures the target onto such a face,
    // and NoPenetrationConstraint.SkipPair mutes its own row against the same face because the
    // contact now owns it. If they disagreed, a plant could be snapped to a face that still
    // fires a no-pen row against it, or held off a face that stays muted.
    private const float ContactSupportBand = 8f;
    // Whether any surface can plausibly engage this frame (sample.SurfacesNear) — gates the
    // off-locomotion static solve so dormant terrain planes don't defeat the fast path.
    private bool _surfacesNear;
    // Scratch for feathered contact weights: (bone, w, dw/dφ) at some phase. Filled by
    // WeightedContactsAtPhase — at the entry phase for RefreshContacts' capture/release
    // bookkeeping, then at each candidate φ+Δφ inside the solve (BuildSolvePose) so the
    // contact rows read a LIVE weight (§4.2).
    private readonly List<(int bone, float weight, float dweight)> _weightBuf = new();
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
    // Per-solve smoothness targets t_i = wrapAngle(emitted_i − composedEntry_i): last frame's
    // deviation from THIS frame's composed base at the entry phase. Filled at each solve start
    // (FillSmoothTargets, after BuildSolvePose at x = 0); constant for the whole Minimize.
    private readonly float[]      _smoothTarget;
    private bool                  _haveEmitted;   // false until the first frame has been drawn
    // private float                 _leanEase;      // eased locomotion lean (post-solve additive)

    // Overlay motion layers composed onto the base pose (Phase 4): the compositor lives in
    // OverlayStack (an ordered stack of crossfading slots — slot 0 is the privileged Action-FSM
    // overlay; slots 1+ serve driver requests). _baseBlend ALIASES the stack's Π(1−w) array —
    // same object — so the analytic Jacobian's Δφ attenuation reads it with no copy.
    private readonly OverlayStack _overlays;
    private readonly float[] _baseBlend;            // alias of _overlays.BaseBlend (per-bone Π(1−w))

    // The move-driver registry (Animation/MoveDriver.cs): per-situation animation policy —
    // clip selection, time mode, entry, and per-frame contributions (overlays, pins, future
    // constraint blocks). First Matches() in order wins; _frame is its contribution scratch,
    // cleared and refilled each Update.
    private readonly IMoveDriver[] _drivers;
    private readonly FrameInputs   _frame = new();
    private ClipTimeMode           _timeMode;       // how the current clip's sample time is produced

    private CharacterAnimState  _state;
    private CharacterAnimSample _prev;      // previous frame's sample
    private bool _hasPrev;

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
    private readonly float[]             _angVel;   // per-bone clip dθ/dt scratch for the Jacobian
    private readonly float[]             _colX, _colY;   // ∂p/∂x column scratch for the point Jacobian
    private readonly float[]             _colX2, _colY2;  // second point-Jacobian scratch (the aim's other hand)
    private readonly bool[]              _isCore;        // bone is torso (hip/chest/head) → stiff Tikhonov λ_θ
    // The composite objective, assembled per frame into _frameComposite: the geometric core
    // head (contacts, pins, no-pen, aim), then any driver-contributed blocks (FrameInputs.
    // Constraints — still inside the geometric band), then the prior tail (continuity, rate
    // floor, com, Tikhonov, smoothness). List order IS the residual/Jacobian row order the LM
    // core assumes; it's frozen for the frame once assembled (step 1.8). Diagnostics derive
    // block offsets by walking this list (no more triple-maintained row arithmetic).
    private readonly ISolveConstraint[]      _coreGeom;
    private readonly ISolveConstraint[]      _corePriors;
    private readonly List<ISolveConstraint>  _frameComposite = new();
    private readonly int                     _maxResiduals;   // LM core's row capacity (diag scratch sizing)
    // Per-solve context the residual closure reads (set just before each Minimize call).
    private AnimationDocument _solveClip;
    private float             _solvePhi;
    private Affine2           _solveRoot;
    private float             _phaseFloor;        // speed-derived Δφ floor for PhaseRateFloorConstraint (0 = inert)
    private float             _phaseAccelBox;     // this frame's |Δφ − Δφ_prev| bound = MaxPhaseAccel·dt² (0 = no box)
    private float             _phaseAccelNorm;    // 1/(dt²·PhaseAccelRef): (Δφ − Δφ_prev)·norm = acceleration in PhaseAccelRef units
    // Reference phase acceleration (cycles/s²) the soft acceleration row is normalized by —
    // about one re-contact hop of the run at 60 fps (0.03/frame · 3600). Makes PhaseAccelPrior
    // O(1): λ = 1 ⇒ one such hop costs ≈ 1px of planted-foot slip.
    private const float       PhaseAccelRef = 100f;
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
    // The solver config the LAST Update actually solved with — Current plus whatever the
    // move driver overrode that frame (FrameInputs.Solver). Diagnostics / tests.
    public AnimSolverConfig   SolverConfig => _frame.Solver;
    // The cadence's current per-frame phase rate Δφ (last solved / coasted step; the
    // legacy velocity-derived rate right after a clip change). Diagnostics / tests.
    public float              PhaseStep    => _prevPhaseStep;

    // Per-bone angle correction Δθ (radians) the solver applied this frame, by bone
    // index — the IK channel on top of the authored blend. Zero on frames with no
    // solve (flight / non-locomotion without pins). Diagnostic + tests.
    public float AngleCorrection(int bone)
        => _haveCorr && bone >= 0 && bone < _skeleton.Count ? _solveVars[IdxTheta0 + bone] : 0f;

    // Solved vertical root offset δ (world px) to add on top of the host's baseline
    // placement (RigRoot) — the body's bob that keeps the planted foot grounded. Zero on
    // flight / non-locomotion frames with no solve (→ host baseline = com anchor).
    public float VerticalOffset => _haveCorr ? _solveVars[IdxDy] : 0f;

    // Solved horizontal root offset d.x (world px) — the body's slight fore-aft sway that
    // soaks the no-slip residual at a planted foot's horizontal turning point (where cadence
    // alone can't track the body). Added by the host beside VerticalOffset. Zero with no solve.
    public float HorizontalOffset => _haveCorr ? _solveVars[IdxDx] : 0f;

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
        _smoothTarget = new float[rig.Count];
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
        // + phase-rate floor + com(δ, d.x) + bones Tikhonov + bones Δθ-smoothness
        // + headroom for driver-contributed constraint blocks (FrameInputs.Constraints).
        const int MaxContributedRows = 16;
        int nr = 2 * 4 + 2 * MaxPins + MaxSurfaces * rig.Count + 1 + 4 + 2 * rig.Count + 2
               + MaxContributedRows;
        _maxResiduals = nr;
        _ls = new LeastSquaresSolver(maxVars: nv, maxRes: nr);
        _solveVars = new float[nv];
        _solveLo   = new float[nv];
        _solveHi   = new float[nv];
        _angVel    = new float[rig.Count];
        _colX      = new float[nv];
        _colY      = new float[nv];
        _colX2     = new float[nv];
        _colY2     = new float[nv];
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
        _cadenceResiduals = CadenceResiduals;
        _cadenceJacobian  = CadenceJacobian;
        // The composite objective's core blocks (§11), assembled with any driver contributions
        // into _frameComposite each frame (step 1.8). Order is load-bearing: it IS the
        // residual/Jacobian row order the LM core and the FD-vs-analytic oracle assume.
        // Preallocated once → zero per-frame allocation.
        _coreGeom = new ISolveConstraint[]
        {
            new PlantedContactsConstraint(this),   // 2 rows/contact: H no-slip (Δφ) + V ground hold (δ)
            new FixedPointConstraint(this),        // 2 rows/pin: both-axis hard external pin (Δθ IK)
            new NoPenetrationConstraint(this),     // 1 row/(surface×bone): half-plane limb push-out (Δθ/δ)
            new ActionAimConstraint(this),         // 1 row: re-aim the action overlay along the input dir (Δθ)
        };
        _corePriors = new ISolveConstraint[]
        {
            new PlaybackContinuityConstraint(this),// 1 row: Δφ momentum prior
            new PhaseRateFloorConstraint(this),    // 1 row: one-sided Δφ ≥ speed-derived floor (anti-collapse)
            new ComOffsetConstraint(this),         // 2 rows: soft com pulls δ, d.x → baseline
            new PosePriorConstraint(this),         // N rows: Tikhonov on each Δθ (toward 0)
            new ThetaSmoothnessConstraint(this),   // N rows: final angle toward last EMITTED (the in-solve ease)
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

        // 0. Use the previous frame's state: detect a touchdown (was airborne, now
        //    grounded) and arm the authored Land one-shot window.
        if (_hasPrev && !_prev.Grounded && s.Grounded) _state.LandTime = LandClipTime;
        _state.LandTime = MathF.Max(0f, _state.LandTime - dt);

        // 1. Select the active MOVE DRIVER — the first registry entry whose situation matches —
        //    and ask it which clip to play, how its time is produced, and (optionally) where to
        //    start it. All per-move selection policy lives in the drivers (Animation/
        //    MoveDriver.cs); this loop is the entire selector. GroundLocomotionDriver matches
        //    unconditionally, so `driver` is never null.
        IMoveDriver driver = null;
        for (int di = 0; di < _drivers.Length; di++)
            if (_drivers[di].Matches(in s, in _state)) { driver = _drivers[di]; break; }
        ClipChoice choice = driver.Select(in s, in _state);
        AnimClip clip = choice.Clip;
        ClipTimeMode mode = choice.Time;
        // Landing one-shot (cross-move memory of the airborne→grounded edge, so it stays
        // core-side): a touchdown that settles into the Idle band plays the authored
        // crouch-touch instead. Only Idle is overridden — landing into a run/walk keeps the
        // locomotion cycle, and any tagged state wins outright. Re-evaluated per frame, so
        // speeding up or leaving the ground cancels it naturally.
        if (_state.LandTime > 0f && clip == AnimClip.Idle) { clip = AnimClip.Land; mode = ClipTimeMode.Clock; }

        float speed   = MathF.Abs(s.Velocity.X);
        bool hasClip  = _clips.TryGetValue(clip, out var anim);

        if (clip != _state.Clip)
        {
            _state.Clip = clip;
            _state.ClipTime = 0f;
            _contacts.Clear();        // contacts belong to the clip that just ended
            // Δφ momentum across the switch: the velocity-derived legacy rate (the same
            // estimate the rate floor / fallback advance use — at or below any authored
            // cadence), NOT 0. With the acceleration box (MaxPhaseAccel) a zero seed would
            // freeze the legs for a ramp at every Walk↔Run / Fall→Run switch even though the
            // body never stopped; without the box it's just a better momentum-prior target.
            _prevPhaseStep = speed * dt * PhasePerPixel;
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
        }
        else _state.ClipTime += dt;
        _timeMode = mode;
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
        string actKey = IsOverlayAction(s.Action) ? s.Action : null;
        AnimationDocument actClip =
            actKey != null && _actionClips.TryGetValue(actKey, out var ac) ? ac : null;
        float actTau = actClip == null ? 0f
            : s.ActionProgress >= 0f ? MathHelper.Clamp(s.ActionProgress, 0f, 1f)
            : AnimationSampler.NormalizedTime(actClip, s.ActionTime);
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
        _frameComposite.Clear();
        foreach (var c in _coreGeom)           _frameComposite.Add(c);
        foreach (var c in _frame.Constraints)  _frameComposite.Add(c);
        foreach (var c in _corePriors)         _frameComposite.Add(c);

        // 2. Advance the locomotion phase. A Walk/WalkBack clip with contact labels is
        //    cadence-driven: the solver picks Δφ so the planted foot doesn't slip
        //    against the body's real motion. Everything else keeps the old rate.
        // Speed-derived Δφ floor for this frame's solve (PhaseRateFloorConstraint) and the
        // flight coast below. 0.5 × the legacy distance rate: well under any legitimate
        // cadence (authored sweeps run ≲ 100 px/phase ⇒ solved steps ≥ the full legacy rate),
        // so it only catches collapse — a weak-weight contact frame solving Δφ ≈ 0, which the
        // coast would then replay for a whole no-contact window (the mid-flight phase lock).
        // Scales with |vx|, so stopping/decelerating legitimately drops the floor to 0.
        _phaseFloor = 0.5f * speed * dt * PhasePerPixel;
        // This frame's cadence acceleration box (AnimSolverConfig.MaxPhaseAccel, cycles/s² —
        // the per-frame step may move by at most a·dt²). Read AFTER the driver's Contribute
        // so a per-frame override reaches it; shared by the solve box and the flight coast.
        _phaseAccelBox = _frame.Solver.MaxPhaseAccel > 0f ? _frame.Solver.MaxPhaseAccel * dt * dt : 0f;
        // Normalization for the soft acceleration row (PlaybackContinuityConstraint):
        // (Δφ − Δφ_prev) · _phaseAccelNorm is the phase acceleration in units of PhaseAccelRef
        // cycles/s² — dimensionless and dt-invariant, like every other row.
        _phaseAccelNorm = 1f / (MathF.Max(dt, 1e-4f) * MathF.Max(dt, 1e-4f) * PhaseAccelRef);

        if (locomotion && hasClip && HasContacts(anim))
        {
            int dir = s.Facing == 0 ? 1 : s.Facing;
            // Solve-root: hip placed at the body center, plus the com baseline Draw uses
            // (rootY = BodyY − com.Y·scale), so contact targets are captured at the SAME
            // height the pose is drawn and the solved δ perturbs about it. The horizontal
            // no-slip is unaffected by the Y shift. scale/facing match Draw so foot travel
            // and body motion share world units.
            float comBaseY = 0f;
            if (SampleNamedPoint(anim, _state.Phase, "com", out var comL))
                comBaseY = -comL.Y * _scale;
            var root = Affine2.FromTRS(new Vector2(s.Position.X, s.Position.Y + comBaseY), 0f,
                                       new Vector2(dir * _scale, _scale));
            RefreshContacts(anim, _state.Phase, dt, root);
            if (_contacts.Count > 0)
            {
                float dphi = SolvePhaseStepLm(anim, _state.Phase, root);
                _state.Phase   = Wrap01(_state.Phase + dphi);
                _prevPhaseStep = dphi;
            }
            else
            {
                // Flight: a run's no-contact window has no planted foot to pin against,
                // so there's nothing for the cadence solver to do. Coast the cycle at the
                // last solved step's momentum, floored at the speed-derived rate — the last
                // solved step can be both COLLAPSED (a weak fade/capture frame solved ≈ 0;
                // replaying it locks the phase mid-flight until the next contact's feather,
                // which a crawling phase may never reach) and STALE (speed changed since it
                // was solved; no solve runs in flight to notice). The floor tracks current
                // |vx| each frame and is stored back so the momentum survives the window.
                float coast = MathF.Max(_prevPhaseStep, _phaseFloor);
                // The floor may lift a collapsed rate only as fast as the acceleration box
                // allows — same ramp rule as the solve (0 = no box).
                if (_phaseAccelBox > 0f) coast = MathF.Min(coast, _prevPhaseStep + _phaseAccelBox);
                _state.Phase   = Wrap01(_state.Phase + coast);
                _prevPhaseStep = coast;
            }
        }
        else
        {
            _contacts.Clear();
            if (locomotion)            _state.Phase = Wrap01(_state.Phase + speed * dt * PhasePerPixel);
            else if (_timeMode == ClipTimeMode.IdleBob) _state.Phase = Wrap01(_state.Phase + dt * IdleBobHz);
        }

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
        //       the smoothness prior; apply it onto the COMPOSED pose (matching BuildSolvePose's
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

        // 3b. Directional lean for locomotion — an eased scalar layered OUTSIDE the smoothing
        //     loop (see the capture note above). The ease covers both the speed ramp and the
        //     clip-switch drop (walk→jump used to be smoothed by the global pose ease).
        // float leanTarget = 0f;
        // if (clip == AnimClip.Walk || clip == AnimClip.WalkBack || clip == AnimClip.Run
        //     || clip == AnimClip.CrouchWalk)
        //     leanTarget = (clip == AnimClip.WalkBack ? -1f : 1f)
        //                * WalkLean * MathHelper.Clamp(speed / WalkLeanRefSpeed, 0f, 1f);
        // _leanEase += (leanTarget - _leanEase) * (1f - MathF.Exp(-Stiffness * dt));
        // if (MathF.Abs(_leanEase) > 1e-4f) Rot(_chest, _leanEase);

        // (3c retired: the procedural landing squash is replaced by the authored Land
        //  one-shot selected in step 1 — pose-driven, no post-solve scale/translate hack.)

        // 4. Emit. The target IS the pose now — smoothing already happened inside the solve /
        //    fast path (the retired BlendToward is the closed form of that objective), so a
        //    constrained tip (pin, planted foot) is satisfied on the RENDERED skeleton.
        _pose.CopyFrom(_target);

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
    public bool OverlayActive => _overlays.ActionBound;

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
        => SampleNamedPoint(_curDoc, _curComT, "com", out comLocal);

    // Named root-space Point track at normalized time t — the shared sparse-channel C1
    // sampler (AnimAdditionSampler.SamplePoint): only keyframes that author the point are
    // its keys, so gaps bridge smoothly instead of hold-then-snap, and motion between
    // authored keys is Catmull-Rom like the pose spline. Allocation-free.
    private static bool SampleNamedPoint(AnimationDocument doc, float t, string name, out Vector2 p)
        => AnimAdditionSampler.SamplePoint(doc, t, name, out p);

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

    private static bool HasContacts(AnimationDocument clip)
    {
        var ks = clip?.Keyframes;
        if (ks == null) return false;
        foreach (var k in ks)
            if (k.Contacts != null && k.Contacts.Count > 0) return true;
        return false;
    }

    // Feathered contact weights at `phase`, written into _weightBuf as (bone, weight, dweight)
    // merged by bone (§5.2), where dweight = dw/dφ. The keyframe interval's contacts hold full
    // weight, then crossfade to the next interval's over FeatherWidth before the change — so a
    // foot swap is a smooth crossover instead of a hard switch. The derivative's SIGN tells
    // RefreshContacts which side of a crossover a contact is on (dw/dφ < 0 = release has begun
    // → the time-fade floor engages; see RefreshContacts / the foot-swap deadlock).
    private void WeightedContactsAtPhase(AnimationDocument clip, float phase)
    {
        _weightBuf.Clear();
        var ks = clip.Keyframes;

        int i = 0;
        for (int k = 0; k < ks.Count; k++) { if (ks[k].Time > phase) break; i = k; }
        int j = Math.Min(i + 1, ks.Count - 1);
        float jTime = ks[j].Time;
        // Open-tail loop, phase in the wrap gap: the interval is [last, first+1], so the
        // last keyframe's contacts hold and crossfade into the FIRST keyframe's before the
        // seam — same feathered crossover as any interior keyframe change.
        if (AnimationSampler.IsCyclic(clip) && AnimationSampler.HasOpenTail(clip)
            && (phase >= ks[ks.Count - 1].Time || phase < ks[0].Time))
        {
            i = ks.Count - 1; j = 0;
            jTime = ks[0].Time + 1f;
            if (phase < ks[0].Time) phase += 1f;
        }

        float feather = _frame.Solver.FeatherWidth;
        float featherStart = jTime - feather;
        bool inWindow = j != i && phase > featherStart;
        float u  = inWindow ? MathHelper.Clamp((phase - featherStart) / feather, 0f, 1f) : 0f;
        // du/dφ: 1/feather strictly inside the ramp, 0 outside / at the clamps (a kink the
        // FD-vs-analytic oracle must skip, like the keyframe boundary — see FeatherRegionAt).
        float du = inWindow && u > 0f && u < 1f ? 1f / feather : 0f;

        AddWeighted(ks[i].Contacts, 1f - u, -du);
        if (u > 0f) AddWeighted(ks[j].Contacts, u, du);
    }

    private void AddWeighted(List<ContactLabel> labels, float scale, float dscale)
    {
        if (labels == null || scale <= 0f) return;
        foreach (var l in labels)
        {
            int b = _skeleton.IndexOf(l.Node);
            if (b < 0) continue;
            float w  = l.Weight * scale;
            float dw = l.Weight * dscale;
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
        for (int i = _contacts.Count - 1; i >= 0; i--)
        {
            var c = _contacts[i];
            float w = WeightOf(c.Bone);
            if (DWeightOf(c.Bone) < 0f) w = MathF.Min(w, c.Weight - timeFade);
            if (w <= 1e-3f) { _contacts.RemoveAt(i); continue; }
            c.Weight = w;
            _contacts[i] = c;
        }

        bool needWorld = false;
        foreach (var (bone, w, _) in _weightBuf)
            if (w > 1e-3f && ActiveIndex(bone) < 0) { needWorld = true; break; }
        if (needWorld)
        {
            AnimationSampler.SampleSmooth(clip, phase, _kfA, _kfB, _kfC, _kfD, _scratch);
            _overlays.Compose(_scratch);   // capture the target on the COMPOSED pose (= what we measure)
            _scratch.ComputeWorld(root);
        }

        foreach (var (bone, w, _) in _weightBuf)
        {
            if (w <= 1e-3f || ActiveIndex(bone) >= 0) continue;   // held ones updated above
            Vector2 tip = _scratch.WorldOf(bone).Translation;     // bone's far end = contact tip
            _contacts.Add(new ActiveContact { Bone = bone, Target = SnapToSupport(bone, tip),
                                              Weight = w, Source = ContactSource.SelfPlant });
        }
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

    // The cadence solve: horizontal foot no-slip + a playback-continuity prior (plus the
    // full composite objective — pins, surfaces, aim, priors), minimized over
    // x = [Δφ, δ, Δθ…] by the general LM core. Δφ ∈ [0, MaxPhaseStep].
    // NOTE (historical): only the HORIZONTAL component of a planted contact drives Δφ.
    // The foot's vertical arc (lift over the stance) is intrinsic to the cadence and is
    // reconciled by the ground-hold row + δ — penalizing it in the no-slip term made the
    // arc dominate at walk speed and froze the cadence (see PlantedContactsConstraint).
    private float SolvePhaseStepLm(AnimationDocument clip, float phi, in Affine2 root)
    {
        _solveClip = clip; _solvePhi = phi; _solveRoot = root;
        var cfg = _frame.Solver;
        int n = IdxTheta0 + _skeleton.Count;  // x = [Δφ, δ, d.x, Δθ_0…]

        float phiLo = 0f, phiHi = cfg.MaxPhaseStep;
        // Rate floor as a BOX rather than a penalty row (PhaseFloorMode 2): same anti-collapse
        // guarantee, enforced exactly, and it removes the row whose √λ/floor Jacobian is the
        // whole 1e6 diagonal ratio. Clamped below MaxPhaseStep so the box can never invert.
        if (cfg.PhaseFloorMode == 2 && _phaseFloor > 1e-5f)
            phiLo = MathF.Min(_phaseFloor, cfg.MaxPhaseStep);
        // Acceleration box: |Δφ − Δφ_prev| ≤ MaxPhaseAccel — the cadence RAMPS, it doesn't
        // hop (AnimSolverConfig.MaxPhaseAccel). This is the OUTERMOST bound: the rate floor
        // may raise the lower edge inside it, but never past its ceiling, so a collapsed
        // rate climbs back to the floor at the capped acceleration instead of snapping.
        if (_phaseAccelBox > 0f)
        {
            float aHi = MathF.Min(cfg.MaxPhaseStep, _prevPhaseStep + _phaseAccelBox);
            float aLo = MathF.Max(0f,               _prevPhaseStep - _phaseAccelBox);
            phiHi = aHi;
            phiLo = MathF.Min(MathF.Max(phiLo, aLo), aHi);
        }
        _solveLo[IdxPhi] = phiLo;                 _solveHi[IdxPhi] = phiHi;
        _solveLo[IdxDy]  = -cfg.VertOffsetLimit;  _solveHi[IdxDy]  = cfg.VertOffsetLimit;
        _solveLo[IdxDx]  = -cfg.HorizOffsetLimit; _solveHi[IdxDx]  = cfg.HorizOffsetLimit;
        for (int i = IdxTheta0; i < n; i++) { _solveLo[i] = -cfg.AngleCorrLimit; _solveHi[i] = cfg.AngleCorrLimit; }
        Array.Clear(_solveVars, 0, n);        // d, Δθ start at 0 (baseline pose); Δφ seeded below
        FillSmoothTargets(n);                 // freeze t_i (emitted deviation) before any residual eval
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
        // Seeds are clamped into the Δφ box (the floor raises its bottom, the acceleration
        // box narrows both edges) so the basin pick can't land where the solve can't go.
        float best     = MathHelper.Clamp(_prevPhaseStep, phiLo, phiHi);
        float bestCost = CadenceCostAt(best, n);
        const int seeds = 9;
        for (int k = 0; k <= seeds; k++)
        {
            float s = MathHelper.Clamp(cfg.MaxPhaseStep * k / seeds, phiLo, phiHi);
            float c = CadenceCostAt(s, n);
            if (c < bestCost) { bestCost = c; best = s; }
        }

        _solveVars[0] = best;   // already inside the box
        // Δθ starts at 0 (not warm-started): the θ-smoothness prior supplies the temporal
        // continuity from the COST side (its target is last frame's EMITTED pose), and a
        // box-clamped warm seed would stick the solution at the wall.
        _ls.Minimize(_cadenceResiduals, _cadenceJacobian,
                     _solveVars.AsSpan(0, n), _solveLo.AsSpan(0, n), _solveHi.AsSpan(0, n),
                     vectorize: _frame.Solver.CadenceVectorize);
        CaptureBreakdown(n);
        _haveCorr = true;
        return _solveVars[0];
    }

    // Off-locomotion solve (Phase 3): satisfy this frame's external pins + no-penetration
    // surfaces on a clip with no cadence to drive. Δφ is LOCKED (box [0,0]) — there is no
    // planted-foot no-slip here — so only δ (the body bob) and the per-bone Δθ (the IK that
    // bends limbs off a wall / onto a pin) move. The base pose is sampled at the SAME phase /
    // clip-time step 3 draws at, so the solved Δθ line up when applied there. Mirrors
    // SolvePhaseStepLm's root construction (com baseline so capture/solve/draw share a frame).
    // Dormancy slack for the static solve's pre-check: a masked tip must press past its
    // plane by more than this (px) before the LM solve engages. Sub-pixel sink for one
    // frame is invisible; the solve, once it runs, still resolves to the full margin.
    private const float StaticSolveSlack = 0.75f;

    private void SolveStaticPose(AnimationDocument anim, in CharacterAnimSample s)
    {
        var cfg = _frame.Solver;
        int dir = s.Facing == 0 ? 1 : s.Facing;
        float phi = SampleT(anim, in s);
        float comBaseY = 0f;
        if (SampleNamedPoint(anim, phi, "com", out var comL)) comBaseY = -comL.Y * _scale;
        var root = Affine2.FromTRS(new Vector2(s.Position.X, s.Position.Y + comBaseY), 0f,
                                   new Vector2(dir * _scale, _scale));

        _contacts.Clear();                  // no planted contacts on this path
        _solveClip = anim; _solvePhi = phi; _solveRoot = root;
        _phaseFloor = 0f;                   // Δφ locked below — the rate-floor row must stay inert
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
            AnimationSampler.SampleSmooth(_solveClip, Wrap01(_solvePhi), _kfA, _kfB, _kfC, _kfD, _scratch);
            _overlays.Compose(_scratch);
            if (_haveEmitted)
                for (int i = 0; i < _skeleton.Count; i++)
                {
                    float g = MathHelper.WrapAngle(_scratch.Local[i].Rotation - _thetaEmitted[i]);
                    _scratch.Local[i].Rotation = _thetaEmitted[i] + _easeB[i] * g;
                }
            _scratch.ComputeWorld(_solveRoot);
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
        FillSmoothTargets(n);                 // freeze t_i (emitted deviation) before any residual eval
        CaptureAimTarget(n);                  // freeze û* from the reference pose before any residual eval

        _ls.Minimize(_cadenceResiduals, _cadenceJacobian,
                     _solveVars.AsSpan(0, n), _solveLo.AsSpan(0, n), _solveHi.AsSpan(0, n),
                     ftol: cfg.StaticFtol, vectorize: cfg.StaticVectorize);
        CaptureBreakdown(n);
        _haveCorr = true;
    }

    // Cost at a candidate Δφ with d and the angle corrections held at 0 (Δφ seed search).
    private float CadenceCostAt(float dphi, int n)
    {
        System.Diagnostics.Debug.Assert(n <= 80, "CadenceCostAt scratch undersized for this rig");
        Span<float> s = stackalloc float[80];   // ≥ IdxTheta0 + rig bone count; d/Δθ entries stay 0
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
        _overlays.Compose(_scratch);                                              // overlay first (linear)
        int bones = _skeleton.Count;
        // Δθ is applied onto the COMPOSED pose, not the base — so the IK correction survives an
        // overlay that fully owns a bone (a vault hand owned by the ClimbHands overlay). Pre-compose
        // Δθ would be overwritten by the overlay paint's lerp and the pin couldn't bend that limb.
        for (int i = 0; i < bones; i++) _scratch.Local[i].Rotation += x[IdxTheta0 + i];   // post-compose IK
        _scratch.ComputeWorld(_solveRoot);
    }

    // The composite objective (§11.3), ASSEMBLED per frame into `_frameComposite` (step 1.8:
    // core geometric head + driver contributions + prior tail): one shared forward pass
    // (BuildSolvePose) leaves _scratch's world buffer valid, then each constraint emits its
    // rows. Row order = list order (load-bearing: the LM core and the FD-vs-analytic oracle
    // assume a fixed row order; the list is frozen for the frame).
    private int CadenceResiduals(ReadOnlySpan<float> x, Span<float> r)
    {
        BuildSolvePose(x);            // _scratch world is now the composed, corrected pose at φ+Δφ
        int row = 0;
        foreach (var c in _frameComposite) row += c.Residuals(x, r.Slice(row));
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
        foreach (var c in _frameComposite) row += c.Jacobian(x, jac, stride, row);
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
        if (!s.HasAim || _aimBoneL < 0 || _aimBoneR < 0) return;
        if (s.AimDir.LengthSquared() < 1e-6f) return;
        _aimDir    = Vector2.Normalize(s.AimDir);
        _aimFacing = s.Facing == 0 ? 1 : s.Facing;
        _aimActive = true;
    }

    // Freeze this solve's smoothness targets t_i = wrapAngle(emitted_i − composedEntry_i): the
    // deviation of last frame's EMITTED pose from THIS frame's composed base at the entry phase
    // (Δφ = Δθ = 0 — BuildSolvePose at the zeroed vars leaves that base in _scratch.Local).
    // The ThetaSmoothnessConstraint pulls each Δθ_i toward t_i, which is exactly the retired
    // ease's "follow from where you were" — measured in DEVIATION space so clip playback is
    // free (see the constraint's comment). Before the first drawn frame the targets are 0
    // (rows degrade to an extra Tikhonov — harmless for one solve). Must run while _solveVars
    // is all-zero, before the Δφ seed search evaluates any residual.
    private void FillSmoothTargets(int n)
    {
        if (!_haveEmitted) { Array.Clear(_smoothTarget, 0, _skeleton.Count); return; }
        BuildSolvePose(_solveVars.AsSpan(0, n));   // all-zero ⇒ composed base at the entry phase
        for (int i = 0; i < _skeleton.Count; i++)
            _smoothTarget[i] = MathHelper.WrapAngle(_thetaEmitted[i] - _scratch.Local[i].Rotation);
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
