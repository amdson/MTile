using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MTile;

// A named bundle of ImpactDamage tuning parameters, loaded from
// impact_profiles.json at startup. Each PhysicsBody's Impact is built from a
// profile by name (see ImpactProfiles.Build), so tuning lives next to the
// other top-level config (movement_config.json / game_config.json) rather
// than scattered across hardcoded constructor blocks.
public sealed class ImpactProfile
{
    public float Mass                   { get; set; } = 1f;
    public float ImpulseThreshold       { get; set; } = 200f;
    public float DamagePerUnitImpulse   { get; set; } = 0.01f;
    public float BreakThreshold         { get; set; } = 1100f;
    public float NormalRetainOnBreak    { get; set; } = 0.6f;
    public float BounceRestitution      { get; set; } = 0f;
    public float BounceImpulseThreshold { get; set; } = 200f;

    // Materialize a fresh ImpactDamage for a body. Each call yields a NEW
    // instance so per-body / per-test mutations don't leak back into the
    // profile dictionary.
    public ImpactDamage ToImpactDamage() => new()
    {
        Mass                   = Mass,
        ImpulseThreshold       = ImpulseThreshold,
        DamagePerUnitImpulse   = DamagePerUnitImpulse,
        BreakThreshold         = BreakThreshold,
        NormalRetainOnBreak    = NormalRetainOnBreak,
        BounceRestitution      = BounceRestitution,
        BounceImpulseThreshold = BounceImpulseThreshold,
    };
}

// Static registry of named ImpactProfiles. Mirrors MovementConfig's lifecycle:
// hardcoded defaults are in effect from process start (so tests + headless
// sim work without any JSON load); Game1.Initialize calls Load() to overlay
// values from impact_profiles.json. Missing file / missing keys / parse
// errors all leave defaults in place — never crashes the game.
public static class ImpactProfiles
{
    // Profile name constants. Keep in sync with impact_profiles.json keys.
    // Using consts (rather than enums) so JSON keys and code references
    // stay aligned without a parallel enum to maintain.
    public const string Player     = "player";
    public const string Ball       = "ball";
    public const string EnergyBall = "energy_ball";

    private static Dictionary<string, ImpactProfile> _current = Defaults();

    // Defaults match the historical hardcoded ImpactDamage initializers at
    // PlayerCharacter.cs, EntityFactory.MakeBall, and EnergyBallProjectile.
    // Behavior on a missing impact_profiles.json is identical to before.
    private static Dictionary<string, ImpactProfile> Defaults() => new()
    {
        [Player] = new()
        {
            // See impact_profiles.json for the per-field rationale + the
            // PlayerImpactByVelocityTests spec these values are tuned to.
            Mass                   = 2.5f,
            ImpulseThreshold       = 100f,   // > Mass·gravity·dt (50) so sitting on a tile doesn't chip via the post-step contact walk
            DamagePerUnitImpulse   = 0.0008f,
            BreakThreshold         = 800f,
            BounceRestitution      = 0.35f,
            BounceImpulseThreshold = 300f,
        },
        [Ball] = new()
        {
            Mass                 = 2.5f,
            ImpulseThreshold     = 200f,
            DamagePerUnitImpulse = 0.1f,
        },
        [EnergyBall] = new()
        {
            Mass                 = 1.2f,
            ImpulseThreshold     = 50f,
            DamagePerUnitImpulse = 0.04f,
            BreakThreshold       = 80f,
            NormalRetainOnBreak  = 0.55f,
        },
    };

    // Return a NEW ImpactDamage instance for `name`. Unknown names log and
    // fall back to ImpactDamage's class defaults — caller probably typo'd
    // a constant; surfaces the issue without crashing.
    public static ImpactDamage Build(string name)
    {
        if (_current.TryGetValue(name, out var profile))
            return profile.ToImpactDamage();
        Console.WriteLine($"[ImpactProfiles] Unknown profile '{name}', falling back to default ImpactDamage.");
        return new ImpactDamage();
    }

    public static void Load(string path)
    {
        try
        {
            using var stream = TitleContent.TryOpenRead(path);
            if (stream == null) return;
            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            var raw = JsonSerializer.Deserialize<Dictionary<string, ImpactProfile>>(stream, opts);
            if (raw == null) return;
            // Merge JSON entries over defaults — a partial file overrides only
            // the profiles it names, leaving unmentioned ones at the hardcoded
            // values. Lets the JSON be incremental.
            var merged = Defaults();
            foreach (var (name, prof) in raw)
                if (prof != null) merged[name] = prof;
            _current = merged;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ImpactProfiles] Load failed: {ex.Message}");
        }
    }
}
