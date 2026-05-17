using System;
using System.IO;
using System.Text.Json;

namespace MTile;

// Top-level game configuration. Lives next to the executable as game_config.json
// (copied from the repo root). Loaded once in Game1's constructor, before the
// GraphicsDeviceManager is finalized, so window prefs apply on first frame.
public sealed class GameConfig
{
    public string Stage        { get; set; } = "start";
    public int    WindowWidth  { get; set; } = 1600;
    public int    WindowHeight { get; set; } = 1200;
    public bool   Fullscreen   { get; set; } = false;

    // Debug-overlay toggles. Defaults match the historical inline values in Game1
    // so a missing/old config behaves identically to before.
    public bool DebugDrawHitboxes          { get; set; } = true;
    public bool DebugDrawHurtboxes         { get; set; } = false;
    public bool DebugDrawPlayerOrientation { get; set; } = true;
    public bool DebugDrawBodies            { get; set; } = false;
    public bool DebugDrawConstraints       { get; set; } = true;
    public bool DebugDrawGuidedPath        { get; set; } = true;
    public bool DebugDrawHealthBars        { get; set; } = true;
    public bool DebugDrawMassBall          { get; set; } = false;

    // Cosmetic: tiny particle trail under the cursor so it's easier to spot
    // against busy terrain. Set false to disable; toggle independently of debug overlays.
    public bool MouseTrail { get; set; } = true;

    public static GameConfig Load(string path)
    {
        if (!File.Exists(path)) return new GameConfig();
        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<GameConfig>(File.ReadAllText(path), opts)
                ?? new GameConfig();
        }
        catch (Exception)
        {
            // Malformed config shouldn't crash the game — fall back to defaults
            // and let the user notice via the default start stage loading.
            return new GameConfig();
        }
    }
}
