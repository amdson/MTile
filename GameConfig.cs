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

    // Desktop-only dev affordance: watch movement_config.json and hot-reload tuning
    // edits mid-session. This mutates a sim-affecting static (MovementConfig.Current)
    // at an arbitrary wall-clock moment, which would desync rollback netcode — so
    // multiplayer MUST run with this false (both peers share fixed, identical config).
    public bool HotReloadMovementConfig { get; set; } = true;

    // Manual-test affordance: spawn a second PlayerCharacter at SecondPlayerOffset
    // (world-space, relative to the stage's player spawn). The secondary's Controller
    // is never wired to hardware — real input still controls the primary only — but
    // the body is a full IHittable so the primary can slash it and observe hitstun /
    // stun / knockback dynamics. SimRunner has the headless equivalent for tests.
    public bool    SpawnSecondPlayer    { get; set; } = false;
    public float   SecondPlayerOffsetX  { get; set; } = 64f;
    public float   SecondPlayerOffsetY  { get; set; } = 0f;

    // Initial player-selected block type for both drag-build and BlockEruption.
    // Switched at runtime via the 1/2/3/4 number keys (Game1.HandleInput). Stored
    // as a string in the config for readability; parsed once at load.
    public string StartingBlockType { get; set; } = "Dirt";

    public static GameConfig Load(string path)
    {
        try
        {
            using var stream = TitleContent.TryOpenRead(path);
            if (stream == null) return new GameConfig();
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<GameConfig>(stream, opts) ?? new GameConfig();
        }
        catch (Exception)
        {
            // Malformed config shouldn't crash the game — fall back to defaults
            // and let the user notice via the default start stage loading.
            return new GameConfig();
        }
    }
}
