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
// Weight TIERS (rough ordering, kept a MODEST spread so JᵀJ stays well-conditioned in the
// Cholesky): hard pins ≳ contacts ≳ priors. A little reach slip beats a stiff, ill-conditioned
// solve. The pose priors are PER-REGION: the torso is stiff (it shouldn't swing to satisfy a
// limb pin) and the limbs are loose (they do the IK).
public class AnimSolverConfig
{
    // --- constraint weight tiers ---
    public float TierHard      { get; set; } = 10f;   // FixedPoint external pins (both axes)
    public float TierNoPen     { get; set; } = 10f;   // active no-penetration half-plane push-out (hard tier, like a pin)
    public float TierContact   { get; set; } = 1f;    // planted-foot no-slip (Δφ) + ground hold (δ), × feathered label weight
    public float CorePosePrior { get; set; } = 60f;   // λ_θ on hip/chest/head — stiff torso
    public float LimbPosePrior { get; set; } = 4f;    // λ_θ on arms/legs/feet — loose, they bend for IK
    public float ThetaSmooth   { get; set; } = 40f;   // λ pulling each Δθ toward last frame (temporal smoothness — damps flat-DOF jitter)
    public float PhaseStepPrior { get; set; } = 8f;   // λ on (Δφ − Δφ_prev) — cadence momentum / playback continuity
    public float ComWeightY    { get; set; } = 0.05f; // soft λ pulling δ → com baseline (so flight frames release)

    // --- box limits (clamps, not weights) ---
    public float AngleCorrLimit  { get; set; } = 0.6f;  // |Δθ| cap per bone (rad)
    public float VertOffsetLimit { get; set; } = 24f;   // |δ| cap (world px)
    public float MaxPhaseStep    { get; set; } = 0.25f; // max Δφ advanced per frame (< one stance window)
    public float FeatherWidth    { get; set; } = 0.12f; // phase span of the planted-foot crossover

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
