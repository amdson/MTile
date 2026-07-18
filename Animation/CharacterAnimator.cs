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
public enum AnimClip { Idle, Walk, WalkBack, Crouch, CrouchWalk, DuckUnder, Jump, Fall, Vault, Run, WallSlide, Hang, Hitstun, Tumble, WallJumpKick, DoubleJumpFlip, RunTurn }

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
    private float                 _leanEase;      // eased locomotion lean (post-solve additive)
    private float                 _squashEase;    // eased landing-squash amount (post-solve additive)

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
        _baseBlend = new float[rig.Count];
        _slots = new OverlaySlot[OverlaySlotCount];
        for (int i = 0; i < _slots.Length; i++) _slots[i] = new OverlaySlot(rig);

        int I(string n) => rig.IndexOf(n);
        _hip = I("hip"); _chest = I("chest");
        _aimBoneL = I("arm_l_lower"); _aimBoneR = I("arm_r_lower");   // the stab-aim hand pair

        // Variables: Δφ + the root offset d = (δ, d.x) + per-bone Δθ (rig.Count), with a
        // little headroom. Residuals: two rows per contact (H no-slip + V ground) + two per
        // external pin + continuity + com + one prior per bone. Sized to the rig once.
        int nv = IdxTheta0 + rig.Count + 2;
        // 2/contact + 2/pin + (MaxSurfaces × bones) no-penetration + 1 aim + continuity
        // + com(δ, d.x) + bones Tikhonov + bones Δθ-smoothness.
        int nr = 2 * 4 + 2 * MaxPins + MaxSurfaces * rig.Count + 1 + 3 + 2 * rig.Count + 2;
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
            // _thetaEmitted deliberately PERSISTS across the switch — the smoothness prior
            // measures the final angle against it, which is exactly what crossfades the pose
            // gap between the old and new clip (the retired ease's snap-then-follow, in-solve).
        }
        else _state.ClipTime += dt;

        float speed   = MathF.Abs(s.Velocity.X);
        bool hasClip  = _clips.TryGetValue(clip, out var anim);
        bool locomotion = clip == AnimClip.Walk || clip == AnimClip.WalkBack || clip == AnimClip.Run
                       || clip == AnimClip.CrouchWalk;

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

        // 1.6 Per-bone smoothing weights for THIS frame (the in-solve ease — polish item 1).
        //     b_i = 1−exp(−k_i·dt) is the old framerate-independent ease factor; the upper-body
        //     rate ramps with ActionWeight so attacks snap, exactly as the retired BlendToward
        //     did. λs_i = λp_i·(1−b_i)/b_i makes the UNCONSTRAINED optimum of (Tikhonov +
        //     smoothness) equal that ease exactly — the per-region λp cancels, so torso and
        //     limbs follow at the same rate unless constrained. Computed before the solves so
        //     the LM path and the fast path share identical smoothing this frame.
        {
            var cfg0 = AnimSolverConfig.Current;
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
        {
            // The sample's Surfaces may be an oversized reused scratch — SurfaceCount is the
            // logical count (-1 = whole array, the hand-built/test path).
            int srfCount = s.SurfaceCount < 0 ? s.Surfaces.Length : s.SurfaceCount;
            for (int i = 0; i < srfCount && _surfaces.Count < MaxSurfaces; i++)
                _surfaces.Add(s.Surfaces[i]);
        }
        _surfacesNear = s.SurfacesNear && _surfaces.Count > 0;
        ResolveActionAim(in s);

        // 2. Advance the locomotion phase. A Walk/WalkBack clip with contact labels is
        //    cadence-driven: the solver picks Δφ so the planted foot doesn't slip
        //    against the body's real motion. Everything else keeps the old rate.
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
        float leanTarget = 0f;
        if (clip == AnimClip.Walk || clip == AnimClip.WalkBack || clip == AnimClip.Run
            || clip == AnimClip.CrouchWalk)
            leanTarget = (clip == AnimClip.WalkBack ? -1f : 1f)
                       * WalkLean * MathHelper.Clamp(speed / WalkLeanRefSpeed, 0f, 1f);
        _leanEase += (leanTarget - _leanEase) * (1f - MathF.Exp(-Stiffness * dt));
        if (MathF.Abs(_leanEase) > 1e-4f) Rot(_chest, _leanEase);

        // 3c. Landing squash on top of any clip: flatten + sink briefly on touchdown. The
        //     applied amount is eased (the global pose ease used to soften the touchdown snap
        //     to 1; now the envelope carries its own attack ramp).
        _squashEase += (_state.LandSquash - _squashEase) * (1f - MathF.Exp(-Stiffness * dt));
        if (_squashEase > 1e-4f)
        {
            float k = _squashEase;
            Scale(_hip, new Vector2(0.35f * k, -0.35f * k));
            Translate(_hip, new Vector2(0f, 3f * k));
        }

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
        // Guided traversal wins over the generic ground/air clips while active. Keyed on the
        // state's declared AnimTag (MovementState.AnimationTag) — never on its class name.
        if (s.Tag == AnimTag.Parkour) return AnimClip.Vault;
        // Crouch splits on speed like the standing clips: a moving crouch plays the
        // CrouchWalk shuffle cycle (same cadence machinery as Walk), a still one holds
        // the static Crouch pose.
        if (s.Tag == AnimTag.Crouch)
        {
            if (MathF.Abs(s.Velocity.X) > WalkSpeedThreshold) return AnimClip.CrouchWalk;
            // Still crouch under a solid ceiling right overhead (squeezing through a low gap):
            // the DuckUnder variant tucks the head harder, flattens the torso, and raises the
            // free hand to brace the ceiling. Without a low ceiling a still crouch is the
            // generic Crouch. LowCeiling comes from the SAME CeilingChecker query the CrouchedState
            // uses to stay crouched without Down held — read-only, never fed back into the sim.
            return s.LowCeiling ? AnimClip.DuckUnder : AnimClip.Crouch;
        }
        // Wall cling/slide: airborne but pinned to a wall, so it must win over the generic
        // Vy → Jump/Fall below. WallSlidingState pins Facing = wallDir for the whole slide,
        // so the rig reliably faces the wall (+X) — and the no-penetration half-plane
        // (CharacterAnimSample.From) sits on that same +X side.
        if (s.Tag == AnimTag.WallSlide) return AnimClip.WallSlide;
        // LedgeGrab pins the body with a spring + damper; the resulting Vy oscillates
        // sign every frame as it rings down (e.g. −30, 0, −20, 0, ...). The generic
        // `Vy < 0 ? Jump : Fall` heuristic below would flip the clip every frame and
        // produce a per-frame Jump/Fall animation flicker even though the movement
        // state is stable. Map ledge holds to a stable clip instead — LedgePull plays
        // Vault (it's the same "guided pull" shape as ParkourState), LedgeGrab plays
        // Hang (authored to grip the corner; doesn't depend on Vy sign).
        if (s.Tag == AnimTag.LedgePull) return AnimClip.Vault;
        if (s.Tag == AnimTag.LedgeGrab) return AnimClip.Hang;
        // Grounded hitstun: a recoil flinch that must win over the Idle/Walk speed checks
        // below, since knockback leaves the body sliding at walk speed while stunned and
        // the generic picker would play a walk cycle through the flinch. StunnedState is
        // grounded-only (an airborne heavy hit goes to TumbleState), so this can't collide
        // with the air branch.
        if (s.Tag == AnimTag.Stunned) return AnimClip.Hitstun;
        // Airborne heavy-hit launch: must be checked BEFORE the generic `!s.Grounded`
        // branch below, not after — TumbleState is itself airborne (CheckPreConditions
        // requires no ground), so the generic Jump/Fall fallback would also match and,
        // being an earlier `return` in this if-chain, would win first and this branch
        // would never be reached. Unlike Stunned (grounded, so it can't collide with the
        // air branch at all), Tumble's condition is a STRICT SUBSET of `!s.Grounded`, so
        // ordering here is load-bearing, not just documentation.
        if (s.Tag == AnimTag.Tumble) return AnimClip.Tumble;
        // Wall-jump kickoff / double-jump flip: like Tumble, both states are strictly
        // airborne, so these must precede the generic !Grounded return to be reachable.
        // Each state is brief; when it hands off, the generic Jump/Fall resumes.
        if (s.Tag == AnimTag.WallJump) return AnimClip.WallJumpKick;
        if (s.Tag == AnimTag.DoubleJump) return AnimClip.DoubleJumpFlip;
        if (!s.Grounded) return s.Velocity.Y < 0f ? AnimClip.Jump : AnimClip.Fall;
        float speed = MathF.Abs(s.Velocity.X);
        if (speed > WalkSpeedThreshold)
        {
            // Moving against facing = backpedal at walk speeds, but ABOVE run speed it's a
            // direction-reversal skid (facing mirrors instantly on input; momentum still
            // carries the old way): play the RunTurn one-shot until velocity crosses zero
            // (→ Idle band) or realigns with facing (→ Run). Below run speed it's a
            // deliberate backpedal, which keeps WalkBack.
            if (Math.Sign(s.Velocity.X) != s.Facing)
                return speed > RunSpeedThreshold ? AnimClip.RunTurn : AnimClip.WalkBack;
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
        => clip == AnimClip.Walk || clip == AnimClip.WalkBack || clip == AnimClip.Run || clip == AnimClip.Idle
        || clip == AnimClip.CrouchWalk;

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

        float feather = AnimSolverConfig.Current.FeatherWidth;
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
        float timeFade = dt / MathF.Max(1e-3f, AnimSolverConfig.Current.ContactReleaseTime);
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
            ComposeOverlays(_scratch);   // capture the target on the COMPOSED pose (= what we measure)
            _scratch.ComputeWorld(root);
        }

        foreach (var (bone, w, _) in _weightBuf)
        {
            if (w <= 1e-3f || ActiveIndex(bone) >= 0) continue;   // held ones updated above
            Vector2 tip = _scratch.WorldOf(bone).Translation;     // bone's far end = contact tip
            _contacts.Add(new ActiveContact { Bone = bone, Target = tip, Weight = w, Source = ContactSource.SelfPlant });
        }
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
        var cfg = AnimSolverConfig.Current;
        int n = IdxTheta0 + _skeleton.Count;  // x = [Δφ, δ, d.x, Δθ_0…]

        _solveLo[IdxPhi] = 0f;                    _solveHi[IdxPhi] = cfg.MaxPhaseStep;
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
        // Δθ starts at 0 (not warm-started): the θ-smoothness prior supplies the temporal
        // continuity from the COST side (its target is last frame's EMITTED pose), and a
        // box-clamped warm seed would stick the solution at the wall.
        _ls.Minimize(_cadenceResiduals, _cadenceJacobian,
                     _solveVars.AsSpan(0, n), _solveLo.AsSpan(0, n), _solveHi.AsSpan(0, n));
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
        int n = IdxTheta0 + _skeleton.Count;
        _solveLo[IdxPhi] = 0f;                    _solveHi[IdxPhi] = 0f;   // Δφ locked — no cadence here
        _solveLo[IdxDy]  = -cfg.VertOffsetLimit;  _solveHi[IdxDy]  = cfg.VertOffsetLimit;
        _solveLo[IdxDx]  = -cfg.HorizOffsetLimit; _solveHi[IdxDx]  = cfg.HorizOffsetLimit;
        for (int i = IdxTheta0; i < n; i++) { _solveLo[i] = -cfg.AngleCorrLimit; _solveHi[i] = cfg.AngleCorrLimit; }
        Array.Clear(_solveVars, 0, n);
        FillSmoothTargets(n);                 // freeze t_i (emitted deviation) before any residual eval
        CaptureAimTarget(n);                  // freeze û* from the reference pose before any residual eval

        _ls.Minimize(_cadenceResiduals, _cadenceJacobian,
                     _solveVars.AsSpan(0, n), _solveLo.AsSpan(0, n), _solveHi.AsSpan(0, n));
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
        ComposeOverlays(_scratch);                                                // overlay first (linear)
        int bones = _skeleton.Count;
        // Δθ is applied onto the COMPOSED pose, not the base — so the IK correction survives an
        // overlay that fully owns a bone (a vault hand owned by the VaultHands overlay). Pre-compose
        // Δθ would be overwritten by PaintMotionLayer's lerp and the pin couldn't bend that limb.
        for (int i = 0; i < bones; i++) _scratch.Local[i].Rotation += x[IdxTheta0 + i];   // post-compose IK
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

    // dw/dφ companion of WeightOf — the sign tells RefreshContacts whether a contact is on
    // the FADING side of a feather crossover (release has begun).
    private float DWeightOf(int bone)
    {
        foreach (var e in _weightBuf) if (e.bone == bone) return e.dweight;
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
        if (s.Tag == AnimTag.Parkour && _actionClips.TryGetValue("VaultHands", out var hands))
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
        if (_pins.Count < MaxPins && s.HasGrip && s.Tag == AnimTag.Parkour
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
