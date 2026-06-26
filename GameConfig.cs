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
    // Phantom corner-clearance "ramps" at exposed step corners / overhangs. Draws each
    // active SteeringRamp's corner, surface tangent (the trajectory the body skims to),
    // and banned direction (into the solid). Colored by Sense: Over = green, Under = orange.
    public bool DebugDrawSteeringRamps     { get; set; } = true;
    public bool DebugDrawGuidedPath        { get; set; } = true;
    public bool DebugDrawHealthBars        { get; set; } = true;
    // Force fields (hold / grab / throw) — on by default so the otherwise-invisible
    // combat fields read while playtesting (COMBAT_FEEL_PLAN Phases 2/6).
    public bool DebugDrawForceFields       { get; set; } = true;
    public bool DebugDrawMassBall          { get; set; } = false;
    // Procedural skeleton animation overlay on the primary player (render-only,
    // pull-model). On by default while the rig is being built out.
    public bool DebugDrawSkeleton          { get; set; } = true;
    // Skeleton joint node discs. Off by default — the rig reads clearly from its
    // bones, and the nodes clutter the locomotion view. Turn on to inspect the rig.
    public bool DebugDrawSkeletonJoints    { get; set; } = false;
    // Highlight the foot the cadence solver is currently pinning (the plant foot).
    public bool DebugHighlightPlantFoot    { get; set; } = true;
    // The player's vector sprite (the placeholder hexagon body). Turn off to view
    // the skeleton on its own. Independent of DebugDrawBodies (the physics polygon).
    public bool DrawPlayerSprites          { get; set; } = true;

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

    // Route the locomotion cadence (the per-frame phase advance Δφ) through the new
    // generalized least-squares animation solver instead of the legacy 1-D golden-
    // section search. Phase 1 minimizes the SAME objective (horizontal foot no-slip +
    // playback continuity), so it should match the old cadence — it exists to prove the
    // general solver machinery before later phases add joint corrections and a solved
    // CoM offset. Render-only. See Plans/ANIMATION_SOLVER_PLAN.md.
    public bool AnimSolver { get; set; } = false;

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
