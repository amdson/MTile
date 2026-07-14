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
    public float ComWeightY    { get; set; } = 23f;   // soft λ pulling δ → com baseline (so flight frames release)
    // λ pulling the horizontal body sway d.x → 0. Deliberately STIFFER than ComWeightY: d.x
    // exists to soak the no-slip residual at a planted foot's horizontal turning point
    // (∂slipX/∂Δφ = 0 there — cadence alone can't track the body), and the absolute pull-to-0
    // is what stops it absorbing sustained travel and stalling the leg cycle (§11.1's trap).
    public float ComWeightX    { get; set; } = 230f;

    // --- box limits (clamps, not weights) ---
    // |Δθ| cap per bone (rad). Widened from 0.6 when smoothing moved in-solve: Δθ now also
    // BRIDGES clip switches (spanning the pose gap, then decaying), and Idle↔Walk gaps can
    // exceed 1 rad — a tight box would clamp the bridge and pop. Sanity backstop only; the
    // priors do the real bounding. Proper per-joint bounds = JointLimits (future phase).
    public float AngleCorrLimit  { get; set; } = 3.2f;
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
