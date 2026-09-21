using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MTile;

// Tunable weights / limits for the per-frame animation least-squares solve (the cadence Δφ,
// the vertical body offset δ, and the per-bone IK corrections Δθ). Mirrors MovementConfig:
// a static `Current` swapped by Load(), so edits to anim_solver_config.json hot-reload live.
//
// These are EMPIRICAL — read the gradient magnitudes off CharacterAnimator.SolveScaleReport and
// tweak by feel (Plans/ANIMATION_SOLVER_PLAN §11.4). The solver is RENDER-ONLY (never feeds the
// sim), so hot-reloading it carries no determinism risk — unlike movement_config.json.
//
// UNITS (2026-07-14): every residual is DIMENSIONLESS. Pixel rows (contacts, pins,
// no-penetration, the com ties) are divided by the rig's REACH (longest root→tip chain ×
// scale — CharacterAnimator._invCharLen), so their residual is "fraction of a body-reach of
// error"; angle rows are already radians ~ O(1). Through the lever arms 1 rad of joint error
// ≈ 1 reach of tip error, so the numbers below compare HONESTLY across both kinds of row —
// the tier spread you see (limb prior 4 … hard pin 4700) is the true effective priority
// spread; the old similar-magnitude numbers (TierHard 10 vs CorePosePrior 60) concealed it
// via mismatched units. Behavior is unchanged: px tiers carry the matching ×reach² rescale
// (reach ≈ 21.6px for the biped at the game's 0.6 scale, reach² ≈ 467).
//
// Weight TIERS: HARD (pins, no-pen) ≫ CONTACT (no-slip/ground) ≫ AIM/priors. The pose priors
// are PER-REGION: the torso is stiff (it shouldn't swing to satisfy a limb pin) and the limbs
// are loose (they do the IK).
public class AnimSolverConfig
{
    // --- constraint weight tiers (dimensionless rows — see the units note above) ---
    public float TierHard      { get; set; } = 4700f; // FixedPoint external pins (both axes)
    public float TierNoPen     { get; set; } = 4700f; // active no-penetration half-plane push-out (hard tier, like a pin)
    public float TierAim       { get; set; } = 60f;   // action aim: rotate the overlay's L→R-hand vector onto the input dir
    public float TierContact   { get; set; } = 470f;  // planted-foot no-slip (Δφ) + ground hold (δ), × feathered label weight
    public float CorePosePrior { get; set; } = 60f;   // λ_θ on hip/chest/head — stiff torso
    public float LimbPosePrior { get; set; } = 4f;    // λ_θ on arms/legs/feet — loose, they bend for IK
    // NOTE: there is no ThetaSmooth knob anymore — the temporal smoothness λs_i is DERIVED
    // per frame from the pose-follow stiffness + dt (CharacterAnimator._lambdaSmooth), so the
    // in-solve smoothing reproduces the retired BlendToward ease exactly on unconstrained
    // bones. Tune the FEEL via CharacterAnimator.Stiffness / UpperBodyStiffness.
    public float ComWeightY    { get; set; } = 23f;   // soft λ pulling δ → com baseline (so flight frames release)
    // λ pulling the horizontal body sway d.x → 0. Deliberately STIFFER than ComWeightY: d.x
    // exists to soak the no-slip residual at a planted foot's horizontal turning point
    // (∂slipX/∂Δφ = 0 there — cadence alone can't track the body), and the absolute pull-to-0
    // is what stops it absorbing sustained travel and stalling the leg cycle (§11.1's trap).
    public float ComWeightX    { get; set; } = 230f;
    // EXPERIMENT (2026-08-25): the ComWeightY a RUN uses while a solid ceiling sits right
    // over the body (CharacterAnimSample.LowCeiling — a 2-high/32px corridor, which Standing
    // threads upright at fold hover with ~1px of head-room). The run cycle is authored for
    // a full-height stance; pressed under a roof the body's com rides low and the legs get
    // mashed into the floor. A looser com tie lets the ground-hold rows lift the rig root
    // off the com baseline instead of fighting it. Applied per frame by
    // GroundLocomotionDriver.Contribute through FrameInputs.Solver (the per-frame effective
    // config) — nothing else reads it. Set equal to ComWeightY to disable the experiment.
    public float LowCeilingRunComWeightY { get; set; } = 4f;

    // --- box limits (clamps, not weights) ---
    // |Δθ| cap per bone (rad). Widened from 0.6 when smoothing moved in-solve: Δθ now also
    // BRIDGES clip switches (spanning the pose gap, then decaying), and Idle↔Walk gaps can
    // exceed 1 rad — a tight box would clamp the bridge and pop. Sanity backstop only; the
    // priors do the real bounding. Proper per-joint bounds = JointLimits (future phase).
    public float AngleCorrLimit  { get; set; } = 3.2f;
    // Relative-cost-reduction stopping test for the STATIC solve (MINPACK ftol, Ceres
    // function_tolerance). The static path had none, so it spent its whole 12-iteration
    // budget regardless: the cost trace shows idle reaching 0.001 of its starting cost
    // after ONE iteration and then flatlining for nine more (Plans/PERF_AUDIT.md 1c).
    // Not applied to the CADENCE solve — Δφ persists frame to frame, so a solve that stops
    // earlier shifts the phase rather than just the pose, and that needs eyes on it.
    // 0 disables (historical behaviour, bit-for-bit).
    public float StaticFtol { get; set; } = 1e-3f;
    // Vectorize the normal equations on each solve path. Measured (MTile.Bench --ftol):
    // on the STATIC path the vectorized reduction reaches an identical final cost while
    // running 1.3–1.6× faster, so it is free. On the CADENCE path it is not — and the reason
    // is conditioning, not delicate arithmetic. Forming the normal equations SQUARES cond(J):
    // biped/run reaches cond(JᵀJ) ~ 1e6 on its worst frames, so the ~5e-7 relative difference
    // between the two summation orders becomes an O(1) change in the computed STEP, and the
    // solve walks off to a ~25% worse cost. Idle sits at ~1e3 and is unaffected. The fix is
    // QR of J instead of normal equations (Plans/PERF_AUDIT.md Finding 1b), which works with
    // cond(J) rather than its square; until then the cadence path stays scalar.
    public bool StaticVectorize  { get; set; } = true;
    public bool CadenceVectorize { get; set; } = false;
    public float VertOffsetLimit { get; set; } = 24f;   // |δ| cap (world px)
    public float HorizOffsetLimit { get; set; } = 4f;   // |d.x| cap (world px) — small sway, and the hard backstop on travel absorption
    public float MaxPhaseStep    { get; set; } = 0.25f; // max Δφ advanced per frame (< one stance window)

    // ── Step planner (Plans/ANIMATION_STEP_PLANNER_IMPL.md — knob budget: these 3) ──
    // Master A/B for the whole planner path; off = every clip on the legacy SelfPlant
    // lifecycle even when opted in. Render-only, so live-toggling is always safe.
    public bool  PlannerEnabled      { get; set; } = true;
    // Landing selection: score bonus (px) for keeping the previously selected tread.
    public float PlannerHysteresis   { get; set; } = 4f;
    // Swing progress after which a still-valid landing target is frozen.
    public float PlannerLateSwingLock { get; set; } = 0.8f;
    // A committed landing is reconsidered only when the predicted landing wish has moved
    // more than this (px) from the wish it was committed at (StepPlanner, runtime §6).
    public float PlannerReplanDistance { get; set; } = 4f;

    // ── Loop-back (Animation/ClipLoopBack.cs) ──
    // A cadence clip may leave its authored seam alone and instead jump from a point in its
    // TAIL back to an earlier point, when that pair matches better than the seam does. Off =
    // every clip wraps at phase 1 exactly as authored. Render-only; safe to toggle live.
    public bool  LoopBackEnabled     { get; set; } = true;
    // The tail: exits are considered over the last this-fraction of the clip.
    public float LoopBackRegion      { get; set; } = 0.25f;
    // The shortest loop a jump may leave, as a fraction of the clip (keeps runs long).
    public float LoopBackMinLoop     { get; set; } = 0.5f;
    // Once a contact's feather RELEASE has begun, its weight also fades by time over at most
    // this many seconds (min of the two) — so a low-speed cadence stall can't hold the old
    // foot's grip forever (the foot-swap deadlock; see CharacterAnimator.RefreshContacts).
    // Also bounds the visible cadence pause at a slow-walk foot swap (~3 frames at 0.1s —
    // reads as a weight shift). At healthy cadence the phase feather completes faster anyway.
    public float ContactReleaseTime { get; set; } = 0.1f;
    // ENGAGE mirror of ContactReleaseTime: a planted contact's weight rises over at least this
    // many seconds (captured at ≤ one frame's worth, then +dt/ContactEngageTime per frame,
    // min of that and the phase feather). The phase feather alone is under two frames at run
    // cadence, so a re-contact engaged at ~full weight in one frame and the ground-hold row
    // yanked the root down to the new foot (the landing jerk). ~5 frames at 60 fps.
    public float ContactEngageTime { get; set; } = 0.08f;

    // ── Timing stage — the stopping policy (GaitTiming, Plans/ANIMATION_TIMING_STAGE.md T3) ──
    // Grounded and slowing below SettleSpeed (px/s) enters Settling: the nearest landing is
    // finished over at most SettleTime seconds, then the phase holds (SupportedIdle) once the
    // body is inside the driver's idle band. Speed above SettleExitSpeed restarts travel-driven
    // timing from wherever the phase is (hysteresis: exit > enter).
    public float SettleSpeed     { get; set; } = 20f;
    public float SettleExitSpeed { get; set; } = 30f;
    public float SettleTime      { get; set; } = 0.15f;

    // ── Timing stage — the foot-synchronized servo (GaitTiming T6) ──
    // The phase is servoed toward what the planner's planted feet imply (GaitTiming.Observe)
    // as a bounded RATE change: Gain (1/s) sets how fast the error dies (a 25/s gain is a
    // 40 ms time constant, inside any stance at run cadence); MaxRate caps the correction at
    // this fraction of the larger of the feedforward and the authored rate, so playback never
    // deviates more than that; RateSlew (cycles/s²) caps how fast the whole rate may change
    // per frame — the anti-jerk term (0 = off); ReentryError (cycles): past this the clip and
    // the feet disagree outright and the phase re-enters at the observation instead of
    // chasing it (the smoothness prior bridges the pose, as on a clip switch).
    public bool  PhaseServoEnabled { get; set; } = true;
    public float PhaseServoGain    { get; set; } = 25f;
    public float PhaseServoMaxRate { get; set; } = 0.5f;
    public float PhaseRateSlew     { get; set; } = 40f;
    public float PhaseReentryError { get; set; } = 0.2f;

    private static AnimSolverConfig _current = new AnimSolverConfig();

    [JsonIgnore]
    public static AnimSolverConfig Current => _current;

    // Overwrite every knob on this instance from `src`. This is how the animator builds its
    // PER-FRAME EFFECTIVE config (FrameInputs.Solver): a copy of Current taken at the top of
    // each Update, which a move driver may then override programmatically for that frame
    // (IMoveDriver.Contribute) — e.g. a softer ComWeightY for a run under a low ceiling.
    // Every solve-side reader goes through the effective copy, never Current directly, so
    // any knob in this class is overridable the same way. Allocation-free by design (one
    // long-lived instance per animator, refreshed in place, ~30 field copies a frame).
    //
    // KEEP IN SYNC with the property list: a knob added above but not copied here would be
    // silently read at its DEFAULT on the effective config — the hot-reloaded json value
    // would never reach the solver. AnimSolverOverrideTests.CopyFrom_CoversEveryKnob walks
    // the public properties by reflection and fails the build's test run if one is missed.
    public void CopyFrom(AnimSolverConfig src)
    {
        TierHard                = src.TierHard;
        TierNoPen               = src.TierNoPen;
        TierAim                 = src.TierAim;
        TierContact             = src.TierContact;
        CorePosePrior           = src.CorePosePrior;
        LimbPosePrior           = src.LimbPosePrior;
        ComWeightY              = src.ComWeightY;
        ComWeightX              = src.ComWeightX;
        LowCeilingRunComWeightY = src.LowCeilingRunComWeightY;
        AngleCorrLimit          = src.AngleCorrLimit;
        StaticFtol              = src.StaticFtol;
        StaticVectorize         = src.StaticVectorize;
        CadenceVectorize        = src.CadenceVectorize;
        VertOffsetLimit         = src.VertOffsetLimit;
        HorizOffsetLimit        = src.HorizOffsetLimit;
        MaxPhaseStep            = src.MaxPhaseStep;
        ContactReleaseTime      = src.ContactReleaseTime;
        ContactEngageTime       = src.ContactEngageTime;
        PlannerEnabled          = src.PlannerEnabled;
        PlannerHysteresis       = src.PlannerHysteresis;
        PlannerLateSwingLock    = src.PlannerLateSwingLock;
        PlannerReplanDistance   = src.PlannerReplanDistance;
        LoopBackEnabled         = src.LoopBackEnabled;
        LoopBackRegion          = src.LoopBackRegion;
        LoopBackMinLoop         = src.LoopBackMinLoop;
        SettleSpeed             = src.SettleSpeed;
        SettleExitSpeed         = src.SettleExitSpeed;
        SettleTime              = src.SettleTime;
        PhaseServoEnabled       = src.PhaseServoEnabled;
        PhaseServoGain          = src.PhaseServoGain;
        PhaseServoMaxRate       = src.PhaseServoMaxRate;
        PhaseRateSlew           = src.PhaseRateSlew;
        PhaseReentryError       = src.PhaseReentryError;
    }

    public static void Load(string path)
    {
        try
        {
            using var stream = TitleContent.TryOpenRead(path);
            if (stream == null) { Save(path); return; }   // seed an editable copy if missing (desktop)
            var options = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            _current = JsonSerializer.Deserialize<AnimSolverConfig>(stream, options) ?? new AnimSolverConfig();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AnimSolverConfig] Load failed: {ex.Message}");
        }
    }

    public static void Save(string path)
    {
        try
        {
            var json = JsonSerializer.Serialize(_current, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AnimSolverConfig] Save failed: {ex.Message}");
        }
    }
}
