using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MTile;

public class MovementConfig
{
    // General Air Movement
    public float AirAccel { get; set; } = 1500f;
    public float MaxAirSpeed { get; set; } = 150f;
    public float AirDrag { get; set; } = 500f;

    // Jumping
    public float JumpVelocity { get; set; } = -100f;
    public float JumpHoldForce { get; set; } = -1500f;
    public float JumpInitForce { get; set; } = 0f;
    public float MaxJumpHoldTime { get; set; } = 0.12f;
    // Wider-than-Standing probe slack for the jump-source FSD. Jumping states
    // keep an FSD pointing at the surface the player launched from so the jump
    // velocity is defined *relative* to that surface (essential when entering
    // from Parkour with ramp-redirected vy). The window has to outlast the
    // held-jump ascent (~20 px at default tuning) or CheckConditions trips
    // mid-jump and the hold force cuts out.
    public float JumpSourceProbeSlack { get; set; } = 60f;

    // Wall Sliding
    public float SlideTerminalSpeed { get; set; } = 40f;
    public float SlideDrag { get; set; } = 300f;

    // Wall Jumping
    public float WallJumpInitialVelX { get; set; } = 250f;
    public float WallJumpInitialVelY { get; set; } = -150f;
    public float WallJumpAirAccel { get; set; } = 1500f;
    public float WallJumpMaxAirSpeed { get; set; } = 150f;
    public float WallJumpAirDrag { get; set; } = 500f;
    public float WallJumpHoldForce { get; set; } = -1000f;
    public float WallJumpMaxHoldTime { get; set; } = 0.12f;

    // Ground Movement
    public float SpringK { get; set; } = 300f;
    public float SpringDamping { get; set; } = 35f;
    public float SpringMaxRiseSpeed { get; set; } = 80f;
    // Max relative normal velocity at which the standing-FSD is allowed to
    // engage. Body approaching the surface faster than this defers to the
    // swept-tile-collision path (so ImpactDamage.BounceRestitution fires);
    // body moving away faster (just bounced / jumped) isn't spring-caught.
    // Default float.MaxValue ⇒ off — the spring catches every landing as
    // before. Bodies that want to bounce off non-breaking tiles (e.g. the
    // player off stone) lower this so the swept-impact path sees the hit.
    public float MaxGroundEngageVnRel { get; set; } = 300f;
    public float WalkAccel { get; set; } = 3000f;
    public float MaxWalkSpeed { get; set; } = 100f;
    // Legacy: used to be StandingState/CrouchedState's brake force when no input.
    // Now the equivalent role is played by SurfaceContact.Friction (set on floor
    // contacts at collision time, value below). Kept for movement_config.json
    // backward compatibility but unused by code.
    public float BrakingForce { get; set; } = 3000f;
    // Floor-contact friction coefficient (px/s²), applied by the physics solver as
    // a cap on relative tangential velocity change per frame. Matches the old
    // BrakingForce in magnitude so braking from a walk still stops the body in
    // one frame on static ground; on a moving surface it pulls the body's
    // tangential velocity toward the surface's (the tangential carry).
    public float GroundFriction { get; set; } = 3000f;
    
    // Crouching
    public float CrouchMaxWalkSpeed { get; set; } = 50f;
    public float CrouchWalkAccel { get; set; } = 1500f;
    
    // Fast Falling / Sliding
    public float FastFallForce { get; set; } = 1000f;
    public float FastSlideTerminalSpeed { get; set; } = 200f;

    // Running Jump
    public float RunJumpVelocity { get; set; } = -120f;
    public float RunJumpHoldForce { get; set; } = -1200f;
    public float RunJumpMinSpeed { get; set; } = 80f;

    // Duck Under
    public float DuckAutoFireSpeed { get; set; } = 40f;
    public float DuckForce         { get; set; } = 4000f;
    public float DuckPushForce     { get; set; } = 500f;
    public float MaxDuckTime       { get; set; } = 0.5f;

    // Ledge Grab / Pull
    public float GrabGravityCancel { get; set; } = 600f;
    public float GrabSpringK       { get; set; } = 300f;
    public float GrabDamping       { get; set; } = 100f;

    // Ledge Jump (launch at the top of a ledge pull — see LedgeJumpState).
    // The launch is a saturated velocity servo toward TargetVy relative to the
    // ledge: it pushes a slow body toward the target and brakes the pull's surplus
    // vy down to it, so the jump can never exit faster than the target. ServoAccel
    // caps the per-step push (3000 ⇒ ~100 px/s gained per frame at 30 fps);
    // GravityCancel keeps the servo authoritative over vy while powered.
    public float LedgeJumpTargetVy      { get; set; } = -210f;
    public float LedgeJumpServoAccel    { get; set; } = 3000f;
    public float LedgeJumpGravityCancel { get; set; } = 600f;

    // Duck Under (stable height)
    public float DuckDamping       { get; set; } = 80f;

    // Ledge Vault
    public float VaultAutoFireSpeed { get; set; } = 90f;
    public float VaultLiftForce  { get; set; } = 2000f;
    public float VaultPushForce  { get; set; } = 500f;
    public float MaxVaultTime    { get; set; } = 0.5f;

    // Guided States (Parkour, LedgePull, LedgeDrop, CoveredJump — path-followed via PD control)
    // Stability condition for 30fps Euler integration: K·dt² + D·dt < 2
    // At dt=1/30: K·0.001 + D·0.033 < 2 → with K=200, D=40: 0.20+1.32=1.52 ✓
    public float GuidedSpringK        { get; set; } = 200f;
    public float GuidedDamping        { get; set; } = 40f;
    public float GuidedMaxForce       { get; set; } = 10000f;
    public float GuidedLookahead      { get; set; } = 0.05f;  // fraction of path to look ahead
    public float GuidedGravityCancel  { get; set; } = 600f;
    public float GuidedMinDuration    { get; set; } = 0.15f;
    public float GuidedMaxDuration    { get; set; } = 0.6f;
    public float GuidedRefSpeed       { get; set; } = 80f;    // fallback for duration estimate

    // Vault kick — instantaneous velocity bump applied at ParkourState entry.
    // Forward component is scaled by wallDir; upward is negative Y.
    public float VaultKickForward     { get; set; } = 0f;
    public float VaultKickUp          { get; set; } = -40f;

    // Corridor probe (shared local-geometry sensing for the maneuver layer —
    // see Plans/CORRIDOR_MANEUVER_PLAN.md and Character/CorridorProbe.cs).
    // Columns of tiles scanned ahead of the body's leading face (capped at
    // Corridor.MaxColumns). Deliberately short: ~2 blocks reads as character
    // reflexes; more reads as autopilot.
    public int   CorridorHorizonColumns { get; set; } = 4;
    // Vertical search window around the standing base (px). Up must cover the
    // tallest measurable rise (CorridorMaxRise) plus headroom above it; down
    // bounds how deep a drop still counts as "floor" rather than NoFloor.
    public float CorridorWindowUp   { get; set; } = 64f;
    public float CorridorWindowDown { get; set; } = 40f;
    // Minimum floor-to-ceiling gap (px) a column must offer to be traversable.
    // Between 1 tile (16 — impassable) and 2 tiles (32 — tight standing fit).
    public float CorridorMinGap { get; set; } = 24f;
    // Largest single-column floor rise (px) that is still maneuver territory
    // (mantle/arc-jump envelope, ~2.6 tiles). Above this the column is a wall.
    public float CorridorMaxRise { get; set; } = 42f;

    // Mantle (deliberate flush-step climb — see MantleState, Plans/CORRIDOR_MANEUVER_PLAN.md).
    // Rise band in px the mantle accepts (the 1-block vault band: 0.5..1.2 tiles, matching
    // ExposedUpperCornerChecker's). Steeper is arc-jump territory; shallower is a walk-over.
    public float MantleMinRise { get; set; } = 8f;
    public float MantleMaxRise { get; set; } = 20f;
    // Body face must be within this many px of the step lip — the mantle is the flush/slow
    // fallback for the case the ramps' steep-angle taper refuses, not a running maneuver.
    public float MantleFlushDistance { get; set; } = 6f;
    // |vx| gate: above this the body is mid-run and the reflex ramps own the vault; the
    // mantle only claims stalled/deliberate entries. Below MaxWalkSpeed by a wide margin.
    public float MantleMaxEntrySpeed { get; set; } = 60f;

    // Arc jump (automatic ballistic arc over a 1-block rise hit AT SPEED — see ArcJumpState).
    // The running companion to the mantle: same rise band (MantleMinRise..MantleMaxRise), split
    // by entry speed at MantleMaxEntrySpeed (a single shared threshold so the bands always
    // tile) — above it the body pops an honest hop and arcs over, reproducing the old
    // ramp-vault feel with real ballistics; at or below it the flush/slow approach belongs
    // to MantleState.
    // Body face may be up to this many px from the lip when the hop fires — unlike the
    // mantle's flush gate, the arc wants a little run-up room so entry speed carries over.
    public float ArcJumpTriggerDistance { get; set; } = 20f;
    // Extra apex height (px) above the landing gate the entry hop budgets for, so the
    // ballistic rollout crosses the lip with a small clearance instead of grazing it.
    public float ArcJumpApexMargin { get; set; } = 4f;

    // Ballistic corrector (Plans/BALLISTIC_CORRECTOR_PLAN.md). CorrectorVaultEnabled
    // gates ParkourCorrectorState (the corrector-driven running vault) for A/B against
    // the ramp stack; the rest tune the predict → rows → solve loop. Iteration counts
    // and the hinge/ε weights are deliberately NOT here — fixed constants, not knobs.
    public bool  CorrectorVaultEnabled          { get; set; } = true;
    public int   CorrectorHorizon               { get; set; } = 18;
    public float CorrectorMargin                { get; set; } = 2f;
    public float CorrectorDeltaWeight           { get; set; } = 5f;
    // Body face within this many px of the rise lip before the vault fires — larger
    // than ArcJumpTriggerDistance because the corrector plans the whole arc (2 tiles,
    // the corridor plan's reflex-vs-autopilot boundary).
    public float CorrectorVaultTriggerDistance  { get; set; } = 32f;
    // Ambient corrector mode (plan step 7 — replaces the ambient reflex ramps).
    // Runs during free movement under a held direction: free-coast predict over a
    // short horizon, passable-feature rows only, redirect-only solve, applied iff
    // fully feasible (anti-autopilot: infeasible plans are discarded whole).
    // Dev/test capability-probing harness (NOT a shipping config): which channel
    // set the stand-fold solve uses — "full", "redirect-traction", "legs-only".
    // Hot-reloadable: edit movement_config.json while the game runs to switch
    // live. Anything but "full" deliberately cripples locomotion.
    public string CorrectorScenario             { get; set; } = "full";
    public bool  AmbientCorrectorEnabled        { get; set; } = true;
    public int   AmbientHorizon                 { get; set; } = 10;
    // Max unresolved clearance residual (px) an ambient plan may carry and still
    // be applied. Small: ambient assists are grazes, not commitments.
    public float AmbientRefusalResidual         { get; set; } = 1f;

    // Trigger-by-feasibility gate tolerance (px): the final corrected rollout must
    // reach within this of the landing gate line, past the lip, before any
    // unavoided impact — otherwise the maneuver refuses. Per-maneuver knob by
    // design (plan §1: refusal calibrates against the maneuver's NORMAL tracking
    // level, not ≈0 — the spring settles a couple px shy of the exact gate).
    public float CorrectorRefusalResidual       { get; set; } = 4f;

    // Covered Jump (jump initiated while partially under an overhang — see CoveredJumpState)
    // Hard cap on the slide-out phase so a degenerate "can't actually get clear" position bails to
    // Falling instead of hanging. The slide itself reuses WalkAccel / MaxWalkSpeed.
    public float MaxCoveredSlideTime { get; set; } = 0.4f;

    // Dropdown (hold Down on a platform edge — see DropdownState). Same role as MaxCoveredSlideTime:
    // hard cap so a stuck position falls through to Falling instead of hanging.
    public float MaxDropdownTime { get; set; } = 0.4f;
    // Horizontal velocity is scaled by this factor at the moment the body goes airborne off the
    // platform edge, so the drop trajectory lands close to the wall rather than flinging the body
    // forward at the full slide speed.
    public float DropdownExitVelMult { get; set; } = 0.4f;

    // Tile sprout — duration of the grow animation when a new tile is placed.
    // Sprout translates from its parent tile's center to its target cell center
    // over this window, then commits as a regular solid tile.
    public float SproutLifetime { get; set; } = 0.1f;

    // Double Jump
    public float DoubleJumpVelocity { get; set; } = -100f;
    public float DoubleJumpHoldForce { get; set; } = -1500f;
    public float DoubleJumpInitForce { get; set; } = 0f;
    public float DoubleJumpMaxHoldTime { get; set; } = 0.12f;

    private static MovementConfig _current = new MovementConfig();
    
    [JsonIgnore]
    public static MovementConfig Current => _current;

    public static void Load(string path)
    {
        try
        {
            using var stream = TitleContent.TryOpenRead(path);
            if (stream == null)
            {
                // On desktop, seed an editable copy next to the binary so dev hot-reload
                // has something to watch. On web (TitleContainer-only), Save will no-op
                // via its own try/catch; defaults stay in effect.
                Save(path);
                return;
            }
            var options = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            _current = JsonSerializer.Deserialize<MovementConfig>(stream, options) ?? new MovementConfig();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MovementConfig] Load failed: {ex.Message}");
        }
    }

    public static void Save(string path)
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(_current, options);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MovementConfig] Save failed: {ex.Message}");
        }
    }
}