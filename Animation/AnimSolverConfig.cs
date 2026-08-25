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
    public float PhaseStepPrior { get; set; } = 8f;   // λ on (Δφ − Δφ_prev) — cadence momentum / playback continuity
    // λ on the one-sided phase-rate floor max(0, 1 − Δφ/floor): a hinge that keeps a solved
    // step from collapsing toward 0 when a weak-weight contact (feather fade, fresh capture)
    // stops driving Δφ — the collapsed value would then be replayed by the flight coast for a
    // whole no-contact window (the "locked mid-flight" bug). The floor itself is speed-derived
    // (CharacterAnimator, 0.5 × speed·dt·PhasePerPixel — well below any legitimate cadence),
    // so the row is inert in steady locomotion and at a stop. 0 disables.
    public float PhaseFloorPrior { get; set; } = 60f;

    // How the Δφ rate floor is enforced. This row is the single largest source of
    // ill-conditioning in the cadence solve: in RELATIVE mode its Jacobian is √λ/floor, and
    // with floor ≈ one frame's phase step (~0.008) that is ~930, giving a JᵀJ diagonal of
    // ~8.6e5 against delta_y's 0.83 — the whole 1e6 diagonal ratio, from one row.
    //   0 = relative: √λ·(1 − Δφ/floor).   The historical behaviour.
    //   1 = absolute: √λ·(floor − Δφ).     Jacobian √λ. Reparametrization only — see below.
    //   2 = box:      row inert; Δφ's lower bound is raised to the floor instead.
    //
    // NOTE on mode 1: it is NOT a free win. Matching mode 0's push requires λ_abs = λ_rel/floor²,
    // which puts the Jacobian back at √λ_rel/floor exactly. Mode 1 at a moderate λ is a
    // genuinely WEAKER constraint, not the same one better conditioned. Mode 2 is the only
    // option that keeps the constraint strict while removing the column entirely.
    public int PhaseFloorMode { get; set; }
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
    public float FeatherWidth    { get; set; } = 0.12f; // phase span of the planted-foot crossover
    // Once a contact's feather RELEASE has begun, its weight also fades by time over at most
    // this many seconds (min of the two) — so a low-speed cadence stall can't hold the old
    // foot's grip forever (the foot-swap deadlock; see CharacterAnimator.RefreshContacts).
    // Also bounds the visible cadence pause at a slow-walk foot swap (~3 frames at 0.1s —
    // reads as a weight shift). At healthy cadence the phase feather completes faster anyway.
    public float ContactReleaseTime { get; set; } = 0.1f;

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
        PhaseStepPrior          = src.PhaseStepPrior;
        PhaseFloorPrior         = src.PhaseFloorPrior;
        PhaseFloorMode          = src.PhaseFloorMode;
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
        FeatherWidth            = src.FeatherWidth;
        ContactReleaseTime      = src.ContactReleaseTime;
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
