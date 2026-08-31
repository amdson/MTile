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

    // The speed-cap principle (movement_todo #3): no AUTOMATIC move — vault
    // hops, corrector channels, any assist — may push the body upward faster
    // than the fastest deliberate ground jump ACHIEVES. That's the launch
    // impulse plus the hold force sustained over the full hold window (a bare
    // 120 impulse only reaches 12px — jumps get their height from the hold),
    // ≈208 at default tuning. Derived, not authored: retuning the jump
    // family moves the assist ceiling with it. The combat band (tech bounce
    // -260, knockback) is ImpactDamage's domain, outside this principle;
    // the invariant is pinned by SpeedInvariantTests.
    public float MaxAssistRiseSpeed
    {
        get
        {
            float g = Simulation.WorldGravityY;
            float jump = -JumpVelocity
                + MathF.Max(0f, (-JumpHoldForce - g) * MaxJumpHoldTime);
            float run = -RunJumpVelocity
                + MathF.Max(0f, (-RunJumpHoldForce - g) * MaxJumpHoldTime);
            return MathF.Max(jump, MathF.Max(run, -DoubleJumpVelocity));
        }
    }
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

    // The 2-block arc (ArcJumpState) only fires when genuinely
    // running in — |vx along dir| at or above this — AND Up is held
    // (movement_todo #6). Half of MaxWalkSpeed: moving with intent, without
    // demanding a full-speed runway.
    public float ArcJumpRunSpeed { get; set; } = 50f;

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

    // Guided lip maneuvers (ledge pull, parkour, mantle, arc jump)
    public float LipLiftForce  { get; set; } = 2000f;
    public float LipPushForce  { get; set; } = 500f;
    public float MaxLipManeuverTime    { get; set; } = 0.5f;

    // Guided States (reference-clip tracking — ReferencePath.TrackForce PD servo,
    // consumed by LedgePull/Dropdown when UseReferenceClips is on)
    // Stability condition for 30fps Euler integration: K·dt² + D·dt < 2
    // At dt=1/30: K·0.001 + D·0.033 < 2 → with K=200, D=40: 0.20+1.32=1.52 ✓
    public float GuidedSpringK        { get; set; } = 200f;
    public float GuidedDamping        { get; set; } = 40f;
    public float GuidedMaxForce       { get; set; } = 10000f;
    public float GuidedLookahead      { get; set; } = 0.05f;  // fraction of path to look ahead
    public float GuidedGravityCancel  { get; set; } = 600f;

    // Reference clips (movement_todo 7+8): LedgePull/Dropdown track authored
    // Hermite clips (ReferenceClips/<name>.json, baked defaults in
    // ReferenceClipRegistry) instead of their legacy bespoke force logic.
    // Hot-reloadable A/B toggle; durations are the nominal clip playback times
    // (must stay under MaxLipManeuverTime / MaxDropdownTime respectively — the state
    // timeouts still govern).
    public bool  UseReferenceClips    { get; set; } = true;
    // 0.60 matches the bespoke pull's measured pace: the corner crossing (which
    // is both the completion trigger and the end of the mid-pull re-grab window)
    // sits at clip progress ≈ 0.72 → ~13 of the 15 frames MaxLipManeuverTime allows at
    // 1/30 — the same last-2-frames crest the legacy pull had.
    public float LedgePullRefDuration { get; set; } = 0.60f;
    public float DropdownRefDuration  { get; set; } = 0.30f;
    public float GuidedMinDuration    { get; set; } = 0.15f;
    public float GuidedMaxDuration    { get; set; } = 0.6f;
    public float GuidedRefSpeed       { get; set; } = 80f;    // fallback for duration estimate

    // Parkour kick — instantaneous velocity bump applied at ParkourState entry.
    // Forward component is scaled by wallDir; upward is negative Y.
    public float ParkourKickForward     { get; set; } = 0f;
    public float ParkourKickUp          { get; set; } = -40f;

    // Corridor probe (shared local-geometry sensing for the maneuver layer —
    // see Plans/CORRIDOR_MANEUVER_PLAN.md and Character/CorridorProbe.cs).
    // Columns of tiles scanned ahead of the body's leading face (capped at
    // Corridor.MaxColumns = 6, so this is the maximum). Deliberately short:
    // ~6 columns / ~66 px reads as character reflexes; more reads as autopilot.
    public int   CorridorHorizonColumns { get; set; } = 6;
    // Vertical search window around the standing base (px). Up must cover the
    // tallest measurable rise (CorridorMaxRise) plus headroom above it; down
    // bounds how deep a drop still counts as "floor" rather than NoFloor.
    // Body-relative px, deliberately independent of Chunk.TileSize.
    public float CorridorWindowUp   { get; set; } = 64f;
    public float CorridorWindowDown { get; set; } = 40f;
    // Minimum floor-to-ceiling gap a column must offer to be traversable — a
    // tight standing body fit. Body-relative px, independent of Chunk.TileSize.
    public float CorridorMinGap { get; set; } = 24f;
    // Largest single-column floor rise that is still maneuver territory (the
    // mantle/arc-jump envelope). Above this the column is a wall.
    // Body-relative px, independent of Chunk.TileSize.
    public float CorridorMaxRise { get; set; } = 42f;

    // Mantle (deliberate flush-step climb — see MantleState, Plans/CORRIDOR_MANEUVER_PLAN.md).
    // Rise band in px the mantle accepts (the leg-reach vault band, matching
    // ExposedUpperCornerChecker's). Steeper is arc-jump territory; shallower is a walk-over.
    // Body-relative px, deliberately independent of Chunk.TileSize.
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
    // Body-relative px, deliberately independent of Chunk.TileSize.
    public float ArcJumpTriggerDistance { get; set; } = 20f;
    // Extra apex height (px) above the landing gate the entry hop budgets for, so the
    // ballistic rollout crosses the lip with a small clearance instead of grazing it.
    public float ArcJumpApexMargin { get; set; } = 4f;

    // Ballistic corrector (Plans/BALLISTIC_CORRECTOR_PLAN.md). CorrectorClimbEnabled
    // gates ParkourState (the corrector-driven running vault) for A/B against
    // the ramp stack; the rest tune the predict → rows → solve loop. Iteration counts
    // and the hinge/ε weights are deliberately NOT here — fixed constants, not knobs.
    public bool  CorrectorClimbEnabled          { get; set; } = true;
    public int   CorrectorHorizon               { get; set; } = 18;
    public float CorrectorMargin                { get; set; } = 2f;
    public float CorrectorDeltaWeight           { get; set; } = 5f;
    // Body face within this many px of the rise lip before the vault fires — larger
    // than ArcJumpTriggerDistance because the corrector plans the whole arc.
    // Body-relative px, deliberately independent of Chunk.TileSize.
    public float CorrectorClimbTriggerDistance  { get; set; } = 32f;
    // Ambient corrector mode (plan step 7 — replaces the ambient reflex ramps).
    // Runs during free movement under a held direction: free-coast predict over a
    // short horizon, passable-feature rows only, redirect-only solve, applied iff
    // fully feasible (anti-autopilot: infeasible plans are discarded whole).
    // Dev/test capability-probing harness (NOT a shipping config): which channel
    // set the stand-fold solve uses — "full", "redirect-traction", "legs-only".
    // Hot-reloadable: edit movement_config.json while the game runs to switch
    // live. Anything but "full" deliberately cripples locomotion.
    public string CorrectorScenario             { get; set; } = "full";
    // Ambient corner-plant redirect (cave-mouth duck-in): airborne descending
    // ticks near a convex corner may hand-plant it, and wall-face rows near
    // those ticks enter the solve as PlantOnly recruiters. DEFAULT OFF: the
    // bumpy corridor is structurally a chain of cave mouths, and micro-plants
    // on every ceiling bump shed ~6% corridor speed (76.5 → 72 px/s) while
    // buying only ~0.1s on a real cave entry (the envelope trim + hand-catch
    // already threads it). Flip on to playtest the trade — CaveMouthTests
    // pins the enabled behavior either way. Maneuver plants are always on
    // (their face rows were never filtered).
    public bool FoldCornerPlantEnabled          { get; set; } = false;
    // Ablation switch for the qp fold's plant-and-deflect disc (channel
    // slot 3) — hot-reloadable A/B for measuring what the redirect actually
    // buys on a course. Off masks the channel everywhere, including the
    // feature-anchored (cave-mouth) plant ticks.
    public bool FoldRedirectEnabled             { get; set; } = true;
    // Bound on the redirect's per-tick deflection, as an acceleration
    // (‖Δv‖ ≤ FoldRedirectForce·dt). The bare Thales disc lets one tick take
    // the body to rest — a plant against the ground is a push of bounded
    // strength, so a walk into a face slows over several ticks instead of
    // halting. 0 = unbounded (the bare disc).
    public float FoldRedirectForce              { get; set; } = 1500f;
    public bool  AmbientCorrectorEnabled        { get; set; } = true;
    public int   AmbientHorizon                 { get; set; } = 10;
    // ── Fold tuning surface (CONSOLIDATION_PLAN §6) — hot-reloadable feel knobs.
    // Structural constants (HingeWeight, hinge scales, anchor leak, SupportReach)
    // stay in code: they are stability/semantics, not feel.
    // Hover clearance above the C-obstacle top surface for the standing fold
    // (≈ the old spring equilibrium) and for the crouch (0 = resting on it).
    public float FoldHoverOffset                { get; set; } = 10f;
    public float CrouchHoverOffset              { get; set; } = 0f;
    // Climb band: how far above the support anchor the envelope may bind a
    // floor — a leg reach, so it is body-relative px and deliberately
    // independent of Chunk.TileSize. Low ledges must bind for Standing; a
    // crouch covers only surface roughness. movement_config.json also carries
    // this key at the same value (20); no TileSize caveat applies any more.
    public float FoldClimbReachUp               { get; set; } = 20f;
    public float CrouchClimbReachUp             { get; set; } = 4f;
    // Lattice engine (FoldProfile.RiseCost): the state's price per px CLIMBED
    // on the planned path, traded against LatticeProgressWeight at the goal;
    // drops are free (gravity delivers them). The binding case is the body
    // PRESSED AGAINST the obstacle, where standing still earns nothing and
    // the window's whole progress (7 × 56 = 392) is the climb's reward:
    // Standing 16 buys a climb ceiling of 392/16 ≈ 24.5 px — it mounts a
    // 2-tile (22 px) step (352 < 392) and refuses a 3-tile (33 px) wall
    // (528 > 392); Crouch 30 refuses the 2-tile step too (660 > 392) — a
    // crouch does not mount ledges. The ceiling is in PX and unchanged by
    // Chunk.TileSize; only the tile counts it corresponds to moved.
    public float FoldRiseCost                   { get; set; } = 16f;
    public float CrouchRiseCost                 { get; set; } = 30f;
    // Duck budget — FoldClimbReachUp's downward mirror for the ref engine's
    // wall classification: a frontal obstacle whose duck-under needs at most
    // this much descent is entered by ducking; anything deeper is a give-up
    // (raw carry into an honest bonk). Body-relative px, deliberately
    // independent of Chunk.TileSize; movement_config.json carries the same
    // value (16), so no sync caveat applies any more.
    public float FoldDuckReach                  { get; set; } = 16f;
    // Fixed inner iteration budget of the per-tick fold solve (determinism
    // requires it fixed; raise for convergence, costs linearly).
    public int   FoldIterations                 { get; set; } = 16;
    // Fold channel authority caps (px/s²) and the leg-servo push fade-out
    // speed (px/s) — see CorrectorChannels for each channel's role.
    public float FoldLegForce                   { get; set; } = 6000f;
    public float FoldDriveForce                 { get; set; } = 3000f;
    public float FoldCornerForce                { get; set; } = 1500f;
    public float FoldTuckForce                  { get; set; } = 3600f;
    public float FoldLegPushFadeSpeed           { get; set; } = 400f;
    // Flight authority (px/s²): lateral air steering along intent, and the
    // deliberately TINY two-sided vertical nudge — in flight there is nothing
    // to push against (no redirect, no legs), only air control.
    public float FoldAirLateralForce            { get; set; } = 1500f;
    public float FoldAirVerticalForce           { get; set; } = 300f;
    // TEMP EXPERIMENT: free 2D force at convex-corner plant ticks (see
    // CorrectorChannels' CornerPlant channel). 0 (default) disables the
    // channel; opt in via movement_config.json while playtesting.
    public float FoldCornerPlantForce           { get; set; } = 0f;
    // Stand-fold engine: "qp" = the linearized channel solve (the default —
    // what the test suite pins); "ref" = the reference-rollout engine
    // (FoldReference: terrain-generated carry reference + PathDeform + servo;
    // non-anchored regimes fall back to qp); "lm" = the nonlinear trajectory
    // engine (TrajectoryLm — kept as the offline oracle, too heavy for the
    // rollback loop); "lattice" = the ref engine's tail riding the lattice
    // path DP's reference (FoldLattice / LatticePathPlanner; the Lattice*
    // knobs above tune it). Hot-reloadable A/B while playtesting.
    public string FoldEngine                    { get; set; } = "qp";
    // TrajectoryLm tuning: fixed LM iteration budget (fixed for determinism,
    // like FoldIterations), residual weights, and the cap on the applied
    // tick-0 correction (px/s² — the lone actuation bound the LM path has).
    public int   FoldLmIterations               { get; set; } = 12;
    public float FoldLmPenWeight                { get; set; } = 10f;
    public float FoldLmHoverWeight              { get; set; } = 0.5f;
    public float FoldLmProgressWeight           { get; set; } = 0.2f;
    public float FoldLmEffortWeight             { get; set; } = 0.02f;
    public float FoldLmMaxForce                 { get; set; } = 8000f;
    // Lattice path planner (Plans/LATTICE_PATH_PLANNER.md) — the FoldEngine
    // "lattice" reference generator, also the freeze-frame oracle. Window =
    // the cone's footprint from the seed: LookaheadPx along u, ±L·tanθ
    // across (§2.1). ConeCos > 0 is structural (the DAG condition, §3.3) and
    // is set to "90° − ε" so every forward offset is an edge — climbing is
    // priced by RiseWeight, never filtered (with the ±3 offset table any
    // value ≤ 0.316 admits the same edges). Sim-affecting under "lattice" —
    // hot-reload is gated like every movement knob.
    // Planning lookahead along u — a physical distance, so body-relative px
    // and deliberately independent of Chunk.TileSize.
    public float LatticeLookaheadPx             { get; set; } = 56f;
    // DP grid cell = Chunk.TileSize / this ⇒ ~3.7 px per cell. Tuned for the
    // physical cell size, not the tile count: raising it past the intended
    // resolution inflates the node count quadratically. Clamped to [2,8].
    public int   LatticeCellsPerTile            { get; set; } = 3;
    public float LatticeConeCos                 { get; set; } = 0.05f;
    // Progress worth per px along u, traded against the edge costs at the
    // goal (argmax of w·progress − cost over every reachable node). Bounded by
    // "mount a 1-high block, refuse a 2-high wall" at SteepWeight 30:
    // roughly (5, 9). The §4.1 taste number.
    public float LatticeProgressWeight          { get; set; } = 7f;
    public float LatticeHoverWeight             { get; set; } = 3f;     // per px (linear, like RiseCost)
    public float LatticeSeedWeight              { get; set; } = 0f;
    // Seed run (§3.5): the path's first SeedRunPx are FORCED along the body's
    // current velocity direction (quantized to the nearest admitted offset)
    // when it moves at ≥ SeedRunMinSpeed and that direction lies inside the
    // cone; a blocked run is forced as far as it fits. Otherwise only the
    // soft SeedWeight bias applies. 0 disables the run — the default since
    // 2026-08-24: with a tracker that re-plans from the body every tick, the
    // run feeds the current velocity back into the target (measured three
    // times: a landing that never rose, a drop caught at leg reach, a jump
    // arc straightened into a line off a tunnel roof). Kept as a knob for
    // the freeze-frame oracle only.
    public float LatticeSeedRunPx               { get; set; } = 0f;
    public float LatticeSeedRunMinSpeed         { get; set; } = 20f;
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

    // ── Block peel (Shift+LMB terrain grab rework — see BlockGrabAction) ─────────
    // OFF restores the legacy one-frame drag-rip. All forces here are in abstract
    // "peel units": the spring force and the glue values live on the same scale, so
    // break-out is a direct comparison. Hot-reloads like everything else in this file.
    public bool  BlockPeelEnabled { get; set; } = true;
    // Gaussian paint kernel around the cursor: tether deposited per second at the
    // kernel center; falls off exp(-r²/2σ²). A cell is ADMITTED to the group (up to
    // PeelMemberBuffer.Capacity) when the kernel weight over it reaches
    // PeelJoinThreshold — i.e. the threshold sets the admission radius as a fraction
    // of the kernel peak; skirt grazes deposit nothing on non-members.
    public float PeelKernelSigma   { get; set; } = 0.7f * Chunk.TileSize;
    public float PeelTetherRate    { get; set; } = 3.0f;
    public float PeelJoinThreshold { get; set; } = 0.25f;
    // Player→group spring: F = Coeff * (|mouse − group COM| / 16px)^Power. The
    // divisor is a FIXED px scale, not Chunk.TileSize — see PullPointEntity.
    // Exceeding Max snaps the spring and cancels the whole attempt instantly.
    public float PeelSpringCoeff { get; set; } = 4f;
    public float PeelSpringPower { get; set; } = 1.5f;
    public float PeelSpringMax   { get; set; } = 60f;
    // Wear rates, per (force·second): group→block tethers erode with each block's
    // share of the spring force (dropping the block from the group at zero), and
    // the block→world glue erodes the same way — that's the "peeling" itself.
    public float PeelTetherWear { get; set; } = 0.05f;
    public float PeelGlueWear   { get; set; } = 0.4f;
    // Glue never wears below this fraction of its live base value — the residual
    // stickiness that makes oversized groups permanently unliftable.
    public float PeelGlueFloor { get; set; } = 0.15f;
    // Per-block base glue = weight(material) * (Core + Σ outward-edge factors),
    // where an outward edge is a solid neighbor NOT in the group: 1.0 same
    // material, PeelCrossMaterialEdge if different. Core is the per-block term
    // (its own weight/inertia) that exists even for a free-hanging block.
    public float PeelGlueCore          { get; set; } = 0.5f;
    public float PeelCrossMaterialEdge { get; set; } = 0.2f;
    // Material weights, descending by toughness (rough energy-to-break).
    public float PeelWeightStone { get; set; } = 3.0f;
    public float PeelWeightDirt  { get; set; } = 1.5f;
    public float PeelWeightSand  { get; set; } = 0.8f;
    public float PeelWeightFoam  { get; set; } = 0.4f;
    // Hardened rock is refused admission to the group outright (TileTypes.IsGrabbable),
    // so this is unreachable today — it's here so that if that gate is ever relaxed the
    // material lands at a glue no spring under PeelSpringMax can wear through, rather
    // than silently inheriting the old `_ => Foam` default and peeling like scaffolding.
    public float PeelWeightHardened { get; set; } = 1000f;

    // Carry budget: the harvested orb bleeds linearly to nothing over this long, then the
    // grab ends empty (Plans/BLOCK_THROW_PLAN.md T1). Longer = the clod can be carried
    // further before it crumbles away.
    public float GrabDissipateSeconds { get; set; } = 2.0f;
    // ── Block throw (Plans/BLOCK_THROW_PLAN.md §5) ───────────────────────────────
    // Held rest position: the clod orbits the body at GrabHandDistance in the cursor's
    // direction and leans outward by at most GrabHandLean (tanh-saturated) as the cursor
    // moves away. Shapes only where the ball SITS — the throw is the cursor's velocity.
    public float GrabHandDistance    { get; set; } = 1.4f * PlayerCharacter.Radius;
    public float GrabHandLean        { get; set; } = 20f;
    // The ball's critically damped tracker toward the pulling point: lag time constant
    // while the point is in hand, tighter once it has been released (so detach comes
    // fast), and the cap on the ball's speed in both — which is THE throw-speed cap.
    public float GrabBallSmoothTime  { get; set; } = 0.08f;
    public float GrabChaseSmoothTime { get; set; } = 0.05f;
    public float GrabBallMaxSpeed    { get; set; } = 800f;
    // Detach: the ball lets go of the released point once its velocity has converged to
    // the point's (|Δv| below GrabCatchSpeed) or after GrabChaseMaxSeconds (the point
    // outran the speed cap ⇒ the ball flies at the cap).
    public float GrabCatchSpeed      { get; set; } = 40f;
    public float GrabChaseMaxSeconds { get; set; } = 0.25f;
    // Cursor-velocity EMA blend per frame (~4 frames of memory at 0.35): the point's
    // hand-off velocity is this, measured on the raw cursor in world space.
    public float GrabSwipeSmoothing  { get; set; } = 0.35f;
    // Lifetime of a released point that still has a peel contest to finish.
    public float GrabPointMaxSeconds { get; set; } = 0.25f;

    public float PeelWeight(TileType t) => t switch
    {
        TileType.Stone => PeelWeightStone,
        TileType.Dirt  => PeelWeightDirt,
        TileType.Sand  => PeelWeightSand,
        TileType.Hardened => PeelWeightHardened,
        _              => PeelWeightFoam,
    };

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
                // Say so. This config is sim-affecting, so a peer that silently falls
                // back to defaults desyncs from one that loaded the file — immediately
                // and permanently, since even the resting Y differs. That failure used
                // to be completely quiet, which made it near-impossible to diagnose from
                // the symptom (see the notes on Simulation.Checksum's desync guard).
                Console.WriteLine($"[MovementConfig] {path} not found — running on DEFAULT " +
                                  "movement tuning. In multiplayer this WILL desync unless " +
                                  "every peer is missing it too.");
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