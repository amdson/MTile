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
public enum AnimClip { Idle, Walk, WalkBack, Crouch, Jump, Fall, Vault, Run }

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
public sealed class CharacterAnimator
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

    // --- cadence solver ---
    private const float MaxPhaseStep   = 0.25f;  // max Δφ/frame; < one stance window (§5.2)
    private const float PhaseStepPrior = 8f;     // λ — momentum prior weight (tune by eye, §12.2)
    private const float FeatherWidth   = 0.12f;  // phase span of the foot-swap crossover (§5.2)

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
    private readonly SkeletonPose _kfA, _kfB;   // scratch for animation sampling
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
    private readonly List<(int bone, float weight)> _weightBuf = new();   // scratch for feathered weights
    private float _prevPhaseStep;   // Δφ_prev for the momentum prior

    // Authored clips keyed by category, matched from the loaded animations' Type.
    // When a clip has an authored animation it plays that; otherwise the procedural
    // builder below is the fallback.
    private readonly Dictionary<AnimClip, AnimationDocument> _clips = new();

    // Action overlay clips, keyed by exact action class name (AnimationDocument.Type
    // that fails the AnimClip parse, e.g. "GroundSlash1"). Constraint-free fixed-rate
    // overlays: no contact labels, never enter the cadence φ-solve.
    private readonly Dictionary<string, AnimationDocument> _actionClips = new(StringComparer.Ordinal);
    private readonly bool[][]     _regionMasks;   // per-AnimRegion bone masks, resolved once
    private readonly bool[]       _upperMask;     // chest subtree — bones that snap during attacks
    private readonly float[]      _blend;         // scratch: per-bone ease factor each frame
    private readonly SkeletonPose _actionPose;    // last-sampled overlay pose (persisted so the fade-out blends from it)
    private string            _boundAction;       // action name the overlay is bound to
    private AnimationDocument _boundActionClip;   // null = no overlay for the bound action
    private bool[]            _actionMask;        // mask of the bound clip's Region (kept through fade-out)

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
    // Per-solve context the residual closure reads (set just before each Minimize call).
    private AnimationDocument _solveClip;
    private float             _solvePhi;
    private Affine2           _solveRoot;

    // Cached bone indices (resolved once).
    private readonly int _hip, _chest;

    public Skeleton           Skeleton => _skeleton;
    public SkeletonPose       Pose     => _pose;
    public CharacterAnimState State    => _state;

    // Lowest point (max local Y; Y is down) of the *current* eased pose, in skeleton-
    // local units — the live "sole" line. A host places the rig so this rests on the
    // ground each frame (rootY = groundY - CurrentSoleY()*scale) so a swinging/arcing
    // foot never punches through the floor. Recomputes the live pose's world buffer
    // under identity; the subsequent Draw recomputes it under the real root.
    public float CurrentSoleY()
    {
        var w = _pose.ComputeWorld(Affine2.Identity);
        float sole = 0f;
        for (int i = 0; i < _skeleton.Count; i++)
        {
            sole = MathF.Max(sole, w[i].Translation.Y);
            sole = MathF.Max(sole, w[i].TransformPoint(new Vector2(_skeleton.Bones[i].Length, 0f)).Y);
        }
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
        _scratch  = rig.CreatePose();
        _actionPose = rig.CreatePose();

        _regionMasks = new bool[3][];
        foreach (AnimRegion r in Enum.GetValues<AnimRegion>())
            _regionMasks[(int)r] = BoneMask.Resolve(rig, r);
        _upperMask = _regionMasks[(int)AnimRegion.UpperBody];
        _blend     = new float[rig.Count];

        int I(string n) => rig.IndexOf(n);
        _hip = I("hip"); _chest = I("chest");

        // Sized for Phase 1 (one Δφ variable) with headroom for later phases (joint
        // corrections, a 2-D CoM offset → ~16 variables; a handful of contact residuals
        // + regularizers → ~16 rows). Allocated only when the solver path is active.
        _useSolver = useSolver;
        if (_useSolver)
        {
            _ls = new LeastSquaresSolver(maxVars: 16, maxRes: 16);
            _solveVars = new float[16];
            _solveLo   = new float[16];
            _solveHi   = new float[16];
            _cadenceResiduals = CadenceResiduals;
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
        }
        else _state.ClipTime += dt;

        float speed   = MathF.Abs(s.Velocity.X);
        bool hasClip  = _clips.TryGetValue(clip, out var anim);
        bool locomotion = clip == AnimClip.Walk || clip == AnimClip.WalkBack || clip == AnimClip.Run;

        // 2. Advance the locomotion phase. A Walk/WalkBack clip with contact labels is
        //    cadence-driven: the solver picks Δφ so the planted foot doesn't slip
        //    against the body's real motion. Everything else keeps the old rate.
        if (locomotion && hasClip && HasContacts(anim))
        {
            int dir = s.Facing == 0 ? 1 : s.Facing;
            // Solve-root: hip at the body center. The constant ground offset Draw adds
            // is irrelevant here (it cancels in foot − target); scale and facing must
            // match Draw so foot travel and body motion share world units.
            var root = Affine2.FromTRS(s.Position, 0f, new Vector2(dir * _scale, _scale));
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
            AnimationSampler.SampleNormalized(anim, _state.Phase, _kfA, _kfB, _target);
        else
            AnimationSampler.SampleAtTime(anim, _state.ClipTime, _kfA, _kfB, _target);

        // 3.5 Action overlay: lerp the action clip over the base on the clip's region
        //     mask, gated by the eased ActionWeight. Sampled at deterministic sim time
        //     (s.ActionTime) so the pose tracks the hitbox windows. Runs BEFORE lean/
        //     squash — those are additive deltas and must stay continuous in the weight
        //     (run-slash keeps its lean; landing mid-air-slash still squashes).
        if (!string.Equals(s.Action, _boundAction, StringComparison.Ordinal))
        {
            _boundAction = s.Action;
            _boundActionClip = IsOverlayAction(s.Action)
                && _actionClips.TryGetValue(s.Action, out var ac) ? ac : null;
            // When the clip goes null, keep the previous mask and _actionPose: the
            // fade-out must blend away from the last sampled overlay, not garbage.
            if (_boundActionClip != null)
                _actionMask = _regionMasks[(int)_boundActionClip.Region];
        }
        bool overlayActive = _boundActionClip != null;
        if (overlayActive)
        {
            // Time-remap: when the action declares a length, stretch/compress the clip's
            // whole [0,1] timeline onto the action's [0, ActionDuration] so it sweeps
            // exactly once over the swing (ignoring the clip's own Duration/Loop). Clamp
            // past the end so it holds the final pose through recovery. No declared
            // length → fall back to the clip's authored seconds.
            if (s.ActionDuration > 1e-4f)
                AnimationSampler.SampleNormalized(
                    _boundActionClip, MathHelper.Clamp(s.ActionTime / s.ActionDuration, 0f, 1f),
                    _kfA, _kfB, _actionPose);
            else
                AnimationSampler.SampleAtTime(_boundActionClip, s.ActionTime, _kfA, _kfB, _actionPose);
        }
        float wRate = overlayActive ? ActionEaseIn : ActionEaseOut;
        _state.ActionWeight += ((overlayActive ? 1f : 0f) - _state.ActionWeight)
                             * (1f - MathF.Exp(-wRate * dt));
        if (_state.ActionWeight > 1e-3f && _actionMask != null)
        {
            for (int i = 0; i < _skeleton.Count; i++)
                if (_actionMask[i])
                    _target.Local[i] = BoneTransform.Lerp(
                        _target.Local[i], _actionPose.Local[i], _state.ActionWeight);
        }
        else if (!overlayActive) _state.ActionWeight = 0f;   // snap the fade tail

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
                Vector2 tip = _pose.WorldOf(c.Bone)
                                   .TransformPoint(new Vector2(_skeleton.Bones[c.Bone].Length, 0f));
                ctx.Disc(tip, PlantFootMarkerRadius, PlantFootMarkerColor);
            }
    }

    // Whether an action overlay clip is currently bound and playing (vs faded out).
    public bool OverlayActive => _boundActionClip != null;

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
        var pose = (fromOverlay && _boundActionClip != null) ? _actionPose : _pose;
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

        float featherStart = ks[j].Time - FeatherWidth;
        float u = (j != i && phase > featherStart)
                ? MathHelper.Clamp((phase - featherStart) / FeatherWidth, 0f, 1f) : 0f;

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
            AnimationSampler.SampleNormalized(clip, phase, _kfA, _kfB, _scratch);
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
                Vector2 tip = _scratch.WorldOf(bone).TransformPoint(new Vector2(_skeleton.Bones[bone].Length, 0f));
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
            AnimationSampler.SampleNormalized(clip, Wrap01(phi + dphi), _kfA, _kfB, _scratch);
            _scratch.ComputeWorld(r);
            float e = 0f;
            foreach (var c in _contacts)
            {
                Vector2 tip = _scratch.WorldOf(c.Bone).TransformPoint(new Vector2(_skeleton.Bones[c.Bone].Length, 0f));
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
            return e + PhaseStepPrior * d * d;
        }
        return GoldenSection.Minimize(Loss, 0f, MaxPhaseStep);
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
        _solveLo[0] = 0f; _solveHi[0] = MaxPhaseStep;

        // The cadence objective is NON-CONVEX in Δφ: a planted foot's horizontal track
        // is non-monotonic over a stance arc (it can drift forward before sweeping back),
        // so the gradient at Δφ=0 may point into the Δφ<0 wall while the true minimum
        // sits further inside the bracket. A purely local descent stalls there. Globalize
        // with a cheap coarse seed search (1-D only), keeping the momentum warm-start
        // (Δφ_prev) as a candidate so steady-state locomotion stays smooth, then let LM
        // refine. The full multi-DOF solver can't grid-search — it leans on temporal
        // warm-starting from the previous frame instead (ANIMATION_SOLVER_PLAN §3.5).
        float best     = MathHelper.Clamp(_prevPhaseStep, 0f, MaxPhaseStep);
        float bestCost = CadenceCostAt(best);
        const int seeds = 9;
        for (int k = 0; k <= seeds; k++)
        {
            float s = MaxPhaseStep * k / seeds;
            float c = CadenceCostAt(s);
            if (c < bestCost) { bestCost = c; best = s; }
        }

        _solveVars[0] = best;
        _ls.Minimize(_cadenceResiduals,
                     _solveVars.AsSpan(0, 1), _solveLo.AsSpan(0, 1), _solveHi.AsSpan(0, 1));
        return _solveVars[0];
    }

    private float CadenceCostAt(float dphi)
    {
        Span<float> s = stackalloc float[1];
        s[0] = dphi;
        return _ls.Cost(_cadenceResiduals, s);
    }

    // r(Δφ): one row per planted contact (√weight · horizontal slip of its tip at phase
    // φ+Δφ) plus one playback-continuity row (√PhaseStepPrior · (Δφ − Δφ_prev)). The sum
    // of squares is exactly the golden-section Loss, so the two paths share a minimum.
    private int CadenceResiduals(ReadOnlySpan<float> x, Span<float> r)
    {
        float dphi = x[0];
        AnimationSampler.SampleNormalized(_solveClip, Wrap01(_solvePhi + dphi), _kfA, _kfB, _scratch);
        _scratch.ComputeWorld(_solveRoot);

        int n = 0;
        foreach (var c in _contacts)
        {
            Vector2 tip = _scratch.WorldOf(c.Bone).TransformPoint(new Vector2(_skeleton.Bones[c.Bone].Length, 0f));
            r[n++] = MathF.Sqrt(c.Weight) * (tip.X - c.Target.X);          // horizontal no-slip
        }
        r[n++] = MathF.Sqrt(PhaseStepPrior) * (dphi - _prevPhaseStep);     // playback continuity
        return n;
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

    private void Rot(int bone, float delta)       { if (bone >= 0) _target.Local[bone].Rotation    += delta; }
    private void Translate(int bone, Vector2 d)    { if (bone >= 0) _target.Local[bone].Translation += d;     }
    private void Scale(int bone, Vector2 d)        { if (bone >= 0) _target.Local[bone].Scale       += d;     }

    private static float Wrap01(float x) => x - MathF.Floor(x);
}
