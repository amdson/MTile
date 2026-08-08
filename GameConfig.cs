using System;
using System.Collections.Generic;
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
    public bool DebugDrawConstraints       { get; set; } = false;
    // Corridor probe (Plans/CORRIDOR_MANEUVER_PLAN.md): per-column floor/ceiling gates ahead
    // of the primary player in their facing direction, corner markers, and a red truncation
    // post (stubs: 1 = Pinch, 2 = TallRise, 3 = NoFloor). Render-only scan.
    public bool DebugDrawCorridor          { get; set; } = false;
    // BallisticPredictor coast ahead of the primary player (Plans/BALLISTIC_CORRECTOR_PLAN.md
    // step 1): predicted correction-free trajectory as a polyline, grounded samples marked.
    // Render-only rollout each frame; never feeds back into the sim.
    public bool DebugDrawCoast             { get; set; } = true;
    // Corrector maneuver trajectories, captured from what the ACTIVE maneuver state
    // actually computed this timestep (not a render-side re-simulation):
    //   Reference  — the authored arc planned at maneuver entry (gold, frozen)
    //   Ballistic  — this tick's uncorrected coast prediction (aqua)
    //   Solved     — this tick's coast with the final corrections applied (magenta)
    // Any flag on enables capture in the sim (write-only diagnostics; never read back).
    public bool DebugDrawCorrectorReference { get; set; } = true;
    public bool DebugDrawCorrectorBallistic { get; set; } = true;
    public bool DebugDrawCorrectorSolved    { get; set; } = true;
    //   Contacts   — per-clearance-row push arrows: the δv each contact shoved
    //                into the correction applied this frame (orange-red)
    public bool DebugDrawCorrectorContacts  { get; set; } = false;
    // C-space obstacle boundary near the player (exposed template facets: axis
    // faces steel blue, corner bevels orange) — what the row builder plans against.
    public bool DebugDrawCObstacles         { get; set; } = false;
    public bool DebugDrawGuidedPath        { get; set; } = true;
    public bool DebugDrawHealthBars        { get; set; } = true;
    // Frame-time probe: worst CPU ms per section over the last 60 frames (sim step
    // loop / cosmetics+animator / draw submit), particle + entity counts, and a
    // CATCH-UP marker whenever MonoGame reports IsRunningSlowly (fixed-timestep
    // debt — the classic cause of visible lag spirals).
    public bool DebugFrameTimings          { get; set; } = true;
    // Master switch for the skeletal animation solver (LM cadence solve + terrain
    // surface extraction, CosmeticUpdateSystem). Off = animators never tick: the
    // rig/sprite-skin/attack-glow read a frozen pose. Perf escape hatch — cosmetic
    // only, the sim never reads the animator.
    public bool RunAnimationSolver         { get; set; } = true;
    // Ctrl+M stage capture (StageSaver): chunks within this Chebyshev radius (in
    // chunks) of the player's chunk are saved. 2 → a 5×5 chunk neighborhood.
    public int StageSaveChunkRadius        { get; set; } = 2;
    // Force fields (hold / grab / throw) — on by default so the otherwise-invisible
    // combat fields read while playtesting (COMBAT_FEEL_PLAN Phases 2/6).
    public bool DebugDrawForceFields       { get; set; } = true;
    // Procedural skeleton animation overlay on the primary player (render-only,
    // pull-model). On by default while the rig is being built out.
    public bool DebugDrawSkeleton          { get; set; } = true;
    // Skeleton joint node discs. Off by default — the rig reads clearly from its
    // bones, and the nodes clutter the locomotion view. Turn on to inspect the rig.
    public bool DebugDrawSkeletonJoints    { get; set; } = false;
    // Highlight the foot the cadence solver is currently pinning (the plant foot).
    public bool DebugHighlightPlantFoot    { get; set; } = true;
    // Parallax backdrop. No effect if the art file is missing (e.g. WASM).
    public bool DrawBackground             { get; set; } = true;
    // "trees" = procedural forest from Assets/tree.png; "mountain" = the tiling
    // mountain strip. Trees fall back to the mountain if tree.png is absent.
    public string BackgroundStyle          { get; set; } = "trees";
    // The player's vector sprite (the placeholder hexagon body). Turn off to view
    // the skeleton on its own. Independent of DebugDrawBodies (the physics polygon).
    public bool DrawPlayerSprites          { get; set; } = true;
    // MLS-deformed sprite skin over the rig. No effect until the named binding exists;
    // independent of DebugDrawSkeleton (stick figure).
    public bool DrawPlayerSpriteSkin       { get; set; } = true;
    // Base rig the in-game animator runs: Skeletons/<name>.json, with its clip pool at
    // SkeletonStates/<name>/. Sprite skins bake against THIS rig, so a binding authored
    // on another skeleton needs this switched (render-only — the sim never sees it).
    public string AnimationRig             { get; set; } = "biped";
    // Fallback binding to skin players with: SpriteBindings/<name>.json (authored via
    // MTile.Demo bind mode). Empty/missing name → no skin.
    public string PlayerSpriteBinding      { get; set; } = "player";
    // Per-player binding names, indexed by player (0 = primary, 1+ = secondary). A
    // player past the end of the list — or whose named binding fails to load — falls
    // back to PlayerSpriteBinding.
    public List<string> PlayerSpriteBindings { get; set; } = new() { "rabbit", "badger" };
    // World→screen camera zoom (Camera.Zoom). >1 magnifies; the default matches the
    // long-standing hardcoded value.
    public float CameraZoom                { get; set; } = 1.55f;

    // Cosmetic: tiny particle trail under the cursor so it's easier to spot
    // against busy terrain. Set false to disable; toggle independently of debug overlays.
    public bool MouseTrail { get; set; } = true;

    // Dev preview for the new PrimitiveBatch layer (gradients / stroked Bezier curves /
    // parametric surfaces). Draws a demo card in world space above the player. Render-only.
    public bool DebugDrawPrimitiveDemo { get; set; } = false;

    // Dev preview for the DensityField glow layer (additive kernel accumulation in a
    // RenderTarget). Draws a cluster of overlapping colored glow blobs. Render-only.
    public bool DebugDrawDensityDemo { get; set; } = false;

    // Dev preview for the segment-metaball shaders (CapsuleSplat + MetaballComposite):
    // a synthetic stick figure of bone segments rendered as one merged gooey blob.
    public bool DebugDrawMetaballDemo { get; set; } = false;

    // Dev preview for the GlowRenderer: a glowing triangle riding a curved motion trail.
    public bool DebugDrawGlowDemo { get; set; } = false;

    // Route the primary player's attack glow through GlowTrailField — the persistent
    // reaction–diffusion accumulation buffer (decay + reproject + blur + stamp) — so
    // the slash/stab leaves a soft world-anchored streak. Off (default) → the spline
    // glow ribbon that follows the knife path (GlowRenderer.DrawTrailRibbon). Render-only.
    public bool GlowTrailField { get; set; } = false;

    // Debug/view time scale for the OFFLINE sim. 1 = normal; 0.2 runs the whole
    // simulation (and thus the animations it drives) at a fifth speed for inspecting
    // motion; 0 pauses; >1 fast-forwards. Ignored in networked play (would desync).
    public float TimeScale { get; set; } = 1f;

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

    // TEMP EXPERIMENT: freeze-frame corrector inspector. When true, after the
    // stage loads the player is teleported to FreezeFramePos (null → stage
    // spawn) with FreezeFrameVel, the sim steps exactly ONCE with the held
    // input below, and TimeScale is forced to 0 — the game renders that single
    // frame's captured corrector trajectories (ballistic aqua, solved magenta,
    // reference gold if a maneuver entered). Lives in scenario configs under
    // Testing/ (passed as a CLI arg), not in game_config.json; F5 re-reads the
    // active config file and re-freezes, so edit → F5 iterates.
    public bool   FreezeFrame       { get; set; } = false;
    public float? FreezeFramePosX   { get; set; }
    public float? FreezeFramePosY   { get; set; }
    public float  FreezeFrameVelX   { get; set; } = 0f;
    public float  FreezeFrameVelY   { get; set; } = 0f;
    public int    FreezeFrameInputX { get; set; } = 1;   // -1/0/+1 held direction
    public bool   FreezeFrameDown   { get; set; } = false;

    public static GameConfig Load(string path)
    {
        try
        {
            using var stream = TitleContent.TryOpenRead(path);
            if (stream == null) return new GameConfig();
            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
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
