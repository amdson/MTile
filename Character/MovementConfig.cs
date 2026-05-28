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

    // Phantom safety ramps near corners during ParkourState. Originally a
    // belt-and-suspenders against the path-tracking PD slipping into the
    // corner geometry. Off by default — the multi-segment path + apex clamp
    // should keep the body clear without needing the constraint.
    public bool  ParkourSafetyRamps   { get; set; } = false;

    // Steering Ramps (ParkourState — see Character/STEERING_RAMP_IMPL.md)
    // Force (px/s²) the vault state applies straight up, scaled by the ramp's weight; should match
    // gravity so the climb is gravity-neutral while the ramp is fully engaged.
    public float RampAntiGravForce { get; set; } = 600f;

    // Hard cap on the magnitude of the upward velocity the parkour redirect may
    // produce (px/s, y-down convention so the cap is on |negative vy|). On top of
    // the existing MaxSpeed cap: a steep redirect on a tall ledge converts
    // horizontal speed into a fast vertical kick that magnitude alone allows. Set
    // slightly above |JumpVelocity| so a one-block vault still feels punchy.
    public float ParkourRampMaxVy { get; set; } = 200f;

    // Per-step impulse magnitude the parkour ramp may deliver (px/s² ⇒ Δv
    // cap = ParkourRampMaxForce · dt). With a finite cap the ramp becomes a
    // soft constraint: it drives velocity toward its target *up to* this
    // strength per step, and stronger external forces (knockback, gravity
    // overrun) keep velocity into the tile so the swept resolver picks up
    // the impact normally.
    //
    // Default matches WalkAccel (3000) so a normal parkour entry reaches
    // target velocity over ~2-3 frames — matches the legacy SoftClampVelocity
    // ramp-up — while typical knockback Δv (~200-400 px/s, ie ~6000-12000
    // px/s² equivalent) overshoots the cap and reaches the underlying tile.
    public float ParkourRampMaxForce { get; set; } = 3500f;

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