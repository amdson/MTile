using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MTile;

// Per-tile-type tunables loaded from material_strengths.json. Today this is
// just MaxHP, but the class is set up to grow (bounce factors, friction
// multipliers, etc.) without churning callers. Lives alongside TileDamage so
// per-material data has one home.
public sealed class MaterialStrength
{
    public float MaxHP { get; set; } = 1f;
    // Bounciness of collision-mode attack recoil off this material (HIT_MOMENTUM_PLAN):
    // attacker bounce speed = (1 + Restitution) · closing speed · move's RecoilScale.
    // 0 = dead thud (approach fully absorbed), 1 = full elastic ring. Materials a
    // json entry doesn't specify fall back to this 0.5 class default.
    public float Restitution { get; set; } = 0.5f;
}

public static class MaterialStrengths
{
    private static Dictionary<TileType, MaterialStrength> _current = Defaults();

    // Defaults match the legacy MaxHPFor switch in TileDamage. Behavior on
    // a missing material_strengths.json is identical to before.
    private static Dictionary<TileType, MaterialStrength> Defaults() => new()
    {
        // Restitution gives each material a tactile identity under attack recoil:
        // stone rings, dirt thuds, sand/foam barely push back (and usually never
        // get here — the stab's hardness/break gates skip them first).
        [TileType.Stone] = new() { MaxHP = 2.0f, Restitution = 0.70f },
        [TileType.Dirt]  = new() { MaxHP = 1.0f, Restitution = 0.35f },
        [TileType.Sand]  = new() { MaxHP = 0.5f, Restitution = 0.05f },
        [TileType.Foam]  = new() { MaxHP = 0.5f, Restitution = 0.15f },
    };

    public static float MaxHPFor(TileType type)
        => _current.TryGetValue(type, out var m) ? m.MaxHP : TileDamage.TileMaxHP;

    public static float RestitutionFor(TileType type)
        => _current.TryGetValue(type, out var m) ? m.Restitution : 0.5f;

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
            // JSON keys are TileType enum names ("Stone", "Dirt", …). Parse
            // them into the enum and skip any unknown ones so a stale config
            // (referencing a removed tile type) doesn't crash.
            var raw = JsonSerializer.Deserialize<Dictionary<string, MaterialStrength>>(stream, opts);
            if (raw == null) return;
            var merged = Defaults();
            foreach (var (name, mat) in raw)
            {
                if (mat == null) continue;
                if (Enum.TryParse<TileType>(name, ignoreCase: true, out var type))
                    merged[type] = mat;
                else
                    Console.WriteLine($"[MaterialStrengths] Unknown TileType '{name}' in JSON, ignored.");
            }
            _current = merged;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MaterialStrengths] Load failed: {ex.Message}");
        }
    }
}
