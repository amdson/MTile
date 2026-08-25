using System;
using Microsoft.Xna.Framework;

namespace MTile;

// A limb pinned to a fixed world point over a window — an EXTERNAL contact (a hand on a ledge
// corner, a hand/foot on a wall during a slide). The solver bends the limb (Δθ) at the HARD
// weight tier to hold `Bone`'s far tip at `Target` on both axes. Render-only, like the rest of
// the animation boundary; the host/movement layer supplies these from surfaces it already
// resolved (see Plans/ANIMATION_SOLVER_PLAN §11.5).
public readonly struct ExternalPin
{
    public readonly string  Bone;    // rig bone whose far tip is pinned (e.g. "arm_l_lower" = the hand)
    public readonly Vector2 Target;  // world point to hold that tip at
    public ExternalPin(string bone, Vector2 target) { Bone = bone; Target = target; }
}

// A one-sided no-penetration HALF-PLANE the solver keeps the rig's limbs out of: the solid
// fills the side BEHIND `Point` (against `Normal`); the free space is where `Normal·(q − Point)`
// is positive. The solve pushes any limb sample point that crosses it back out to `Margin` along
// `Normal` (residual √w·max(0, Margin − Normal·(q − Point))). Supplied by the host from a surface
// the movement layer already resolved (the wall-slide wall) or extracted from nearby exposed
// tile faces (TerrainSurfaces). Render-only, `Normal` is unit.
public readonly struct SolverSurface
{
    public readonly Vector2 Point;   // a point on the surface (world)
    public readonly Vector2 Normal;  // unit outward normal — points into the free half-space
    public readonly float   Margin;  // keep limb points at least this far out along Normal (world px)
    // Which rig bones this surface constrains, as a bitmask over skeleton bone indices
    // (bit b = bone b). -1 = all bones (the wall-slide wall). Terrain surfaces carry only
    // the tip bones they were extracted FOR, so a plane near a hand never pushes a foot.
    public readonly int     BoneMask;
    public SolverSurface(Vector2 point, Vector2 normal, float margin, int boneMask = -1)
    { Point = point; Normal = normal; Margin = margin; BoneMask = boneMask; }
}

// The movement-state categories the animation layer keys behavior on — clip selection,
// movement overlays, grip-pin gating, the wall-slide no-pen surface. A MovementState
// declares its tag via the AnimationTag virtual (default None = "nothing special: pick by
// grounded/velocity"). This replaces substring matching on state CLASS NAMES, which was
// fragile to renames and to future states whose names happen to contain a match (e.g. a
// ParkourRoll would have read as a vault). Add a value here + an override when a new state
// needs distinct animation policy.
// Parkour/Mantle/ArcJump are the three CLIMB states (ClimbStates.cs), split by entry speed and
// rise band; they share the hands overlay and grip machinery but each gets its own clip so the
// speed vault, the flush climb and the two-block arc can be authored apart.
public enum AnimTag { None, Parkour, WallSlide, Crouch, LedgeGrab, LedgePull, Stunned, Tumble, WallJump, DoubleJump, LedgeJump, Dropdown, Mantle, ArcJump }

// A read-only snapshot of everything the animation layer is allowed to look at,
// gathered once per render frame. This is the *one-way* boundary between the sim
// and animation: the animator reads this; movement/action code never produces it
// and is completely unaware it exists. Add fields here as animations need more
// signal (action vars, combat flags, surface normals, …) — always read-only.
public readonly struct CharacterAnimSample
{
    public readonly Vector2 Position;       // body world position
    public readonly Vector2 Velocity;       // body velocity (px/s)
    public readonly int     Facing;         // -1 / +1
    public readonly bool    Grounded;
    public readonly string  MovementState;  // PlayerCharacter.CurrentStateName — DEBUG/display only
    // The state's animation category (MovementState.AnimationTag) — what the animator actually
    // keys on (clip selection, overlays, grip pins, the wall surface). Never string-matched.
    public readonly AnimTag Tag;
    public readonly string  Action;         // PlayerCharacter.CurrentActionName
    public readonly float   Dt;             // render delta time (NOT the sim fixed dt)
    // Seconds since the current action entered (ActionVars.TimeInState). Deterministic
    // sim time — drives the action overlay clip so the slash pose stays frame-synced
    // with the hitbox windows and survives rollback.
    public readonly float   ActionTime;
    // How far through its activation the current action reports itself to be
    // (ActionState.AnimationProgress), normalized [0,1]. The overlay clip is remapped
    // onto it, so the authored pose sweeps once over the action no matter how long the
    // action actually runs or how long the clip's own timeline is.
    // **NEGATIVE = the action has no opinion** → the animator falls back to playing the
    // clip at its own authored seconds (right for held, open-ended actions like Guard,
    // whose clip loops).
    public readonly float   ActionProgress;
    // Normalized progress [0,1] of a guided maneuver (CurrentState.AnimationProgress) — drives a
    // movement overlay whose clip time is SPATIAL, not a clock (a vault's hands track body-vs-
    // corner). 0 for states with no natural progress. See CharacterAnimator.ResolveMovementOverlays.
    public readonly float   MovementProgress;
    // External limb pins active this frame (null/empty = none) — fixed-point constraints the
    // solver holds at the hard tier. Supplied by the host from surfaces the movement layer
    // resolved (wall-slide grip/foot, ledge corner). Render-only.
    public readonly ExternalPin[] Pins;
    // No-penetration half-planes active this frame (null/empty = none) — the solver keeps the
    // rig's limbs on the free side. Host-supplied from already-resolved surfaces (wall-slide
    // wall) and/or extracted terrain faces; only read by the solver path.
    public readonly SolverSurface[] Surfaces;
    // Logical count of valid entries in Surfaces — the array may be a reused oversized
    // scratch buffer. -1 (default) = use the full array length.
    public readonly int SurfaceCount;
    // Whether any surface is close enough to a limb tip to plausibly engage this frame.
    // Terrain planes exist near-permanently (feet live next to the ground) but at margin 0
    // they are inactive until something penetrates — this flag keeps the off-locomotion
    // STATIC solve from running every idle/flight frame for provably-dormant planes.
    // (The cadence solve carries all surfaces regardless — it runs anyway.)
    public readonly bool SurfacesNear;
    // A world point a limb should grip during a guided maneuver (a vault's ledge corner), from
    // CurrentState.TryAnimationGrip. The animator decides WHICH bone pins to it (naming is
    // animation policy) and WHEN (gated by MovementProgress). HasGrip false ⇒ GripTarget unused.
    public readonly bool    HasGrip;
    public readonly Vector2 GripTarget;
    // World AIM direction of the current input-parametrized action (a stab's StabDir), from
    // CurrentAction.TryAnimationAim. The animator rotates the authored horizontal overlay onto it.
    // HasAim false ⇒ AimDir unused. Render-only.
    public readonly bool    HasAim;
    public readonly Vector2 AimDir;
    // A solid ceiling sits right above the body this frame — the SAME signal CrouchedState
    // uses to stay crouched with Down released (CeilingChecker.TryFind: a solid tile within
    // 20px of the body's top for a crouch, within RunHeadroomSlack for any other grounded
    // state — so a 2-high corridor fires it and a 3-high one doesn't). On a still
    // crouch it selects the DuckUnder clip (head tucked, torso flat, free hand braced) so
    // squeezing through a low gap reads as "under something"; on a grounded run it softens
    // the solver's vertical com tie (GroundLocomotionDriver.Contribute → FrameInputs.Solver).
    // Render-only.
    public readonly bool    LowCeiling;
    // PHYSICAL ground gap (world px): how far the body floats ABOVE its supported rest height,
    // from the same GroundChecker probe the movement FSM uses. 0 = at/below rest (physically
    // supported); large (no floor within the probe) = clearly airborne. Distinct from
    // `Grounded`, which is FSM-derived and deliberately PERMISSIVE (StandingState engages with
    // the floor up to ProbeSlack away) — the gap is what lets the animator hold an airborne
    // pose instead of running in air during that window (GroundLocomotionDriver). Defaults to
    // 0 (supported) for hand-built samples/tests.
    public readonly float   GroundGap;

    public CharacterAnimSample(
        Vector2 position, Vector2 velocity, int facing, bool grounded,
        string movementState, string action, float dt, float actionTime = 0f,
        float actionProgress = -1f, float movementProgress = 0f, ExternalPin[] pins = null,
        SolverSurface[] surfaces = null, bool hasGrip = false, Vector2 gripTarget = default,
        bool hasAim = false, Vector2 aimDir = default, AnimTag tag = AnimTag.None,
        int surfaceCount = -1, bool? surfacesNear = null, bool lowCeiling = false,
        float groundGap = 0f)
    {
        Position = position; Velocity = velocity; Facing = facing; Grounded = grounded;
        MovementState = movementState; Action = action; Dt = dt; ActionTime = actionTime;
        ActionProgress = actionProgress; MovementProgress = movementProgress; Pins = pins;
        Surfaces = surfaces; SurfaceCount = surfaceCount; HasGrip = hasGrip; GripTarget = gripTarget;
        HasAim = hasAim; AimDir = aimDir; Tag = tag; LowCeiling = lowCeiling; GroundGap = groundGap;
        // Default (hand-built samples, tests): surfaces present ⇒ near — the pre-terrain behavior.
        SurfacesNear = surfacesNear ?? (surfaces != null && (surfaceCount < 0 ? surfaces.Length : surfaceCount) > 0);
    }

    // How far above the body's top (px) a solid tile counts as "right over the head" for a
    // grounded, non-crouched sample's LowCeiling. Deliberately TIGHTER than the FSM's
    // CeilingChecker.ProbeSlack (20px): Standing at fold hover clears a 2-high/32px corridor
    // by ~1px (fires) but sits ~17px under a 3-high roof (must NOT fire — that's the bumpy
    // corridor's ordinary interior, where the run is fine). Half a tile of slack so the
    // lattice's under-lip dips still register without reaching the next tile row up.
    public const float RunHeadroomSlack = 8f;

    // Scratch for the wall-slide half-plane, reused every frame instead of allocating a
    // fresh array (render-only + single-threaded: samples are built and consumed one
    // character at a time within the same Update, so reuse is safe).
    private static readonly SolverSurface[] _wallSurfaceScratch = new SolverSurface[1];

    // Pull the sample from a live character through its public surface only. The
    // direction of the dependency is animation -> character, never the reverse.
    // Signals that need WORLD state (terrain surfaces, ceiling queries) are passed IN by
    // the host (`surfaceBuf`, `chunks`) — PlayerCharacter doesn't store the world, and
    // the sample must stay buildable without one (recorder/tests pass null).
    // `surfaceBuf`/`surfaceCount`: optional caller-owned scratch pre-filled with terrain
    // half-planes (TerrainSurfaces.Extract); the wall-slide plane is appended into the
    // same buffer's spare capacity so the sample carries one combined list.
    public static CharacterAnimSample From(PlayerCharacter p, float dt,
                                           SolverSurface[] surfaceBuf = null, int surfaceCount = 0,
                                           bool terrainNear = false, ChunkMap chunks = null)
    {
        var pos = p.Body.Position;
        int facing = p.Facing;
        AnimTag tag = p.CurrentState?.AnimationTag ?? AnimTag.None;

        // Is there a solid ceiling right overhead? Reuse CeilingChecker.TryFind — the exact
        // query CrouchedState.CheckConditions uses to stay crouched with Down released (a 20px
        // strip above the body: a 2-high/32px corridor fires it, a 3-high one doesn't).
        // Evaluated for a crouch (→ the DuckUnder clip) AND for any grounded state — Standing
        // threads a 2-high corridor upright at fold hover, and the ground locomotion driver
        // softens the run's com tie under a roof (GroundLocomotionDriver.Contribute). Skipped
        // while airborne, where nothing reads it. Render-only read of the public body + chunks;
        // nothing flows back into the sim.
        bool lowCeiling = chunks != null
                          && (tag == AnimTag.Crouch
                                ? CeilingChecker.TryFind(p.Body, chunks, out _)
                                : p.IsGrounded && CeilingChecker.TryFind(p.Body, chunks, out _, RunHeadroomSlack));

        SolverSurface[] surfaces = null;
        int count = 0;
        bool near = terrainNear;
        if (surfaceBuf != null && surfaceCount > 0) { surfaces = surfaceBuf; count = surfaceCount; }

        // While wall-sliding the rig faces the wall (+X = the wall direction). The wall the
        // slide resolved sits at the body's leading edge; its outward normal points back into
        // open space. Hand it to the solver as a no-penetration half-plane (Position/Radius are
        // public, so this is a render-only read — §11.5) so the trailing limbs don't clip into
        // the wall. The braced grip hand/foot rest ON the surface (gap ≈ 0, just inside Margin).
        // Applies to ALL bones (BoneMask -1), unlike per-tip terrain planes.
        if (tag == AnimTag.WallSlide && facing != 0)
        {
            var wallPoint  = new Vector2(pos.X + facing * PlayerCharacter.Radius, pos.Y);
            var wallNormal = new Vector2(-facing, 0f);
            var wall = new SolverSurface(wallPoint, wallNormal, 1.5f);
            if (surfaces == null) { _wallSurfaceScratch[0] = wall; surfaces = _wallSurfaceScratch; count = 1; }
            else if (count < surfaces.Length) surfaces[count++] = wall;
            near = true;   // the braced limbs rest on the wall — always engageable
        }

        // A guided maneuver may expose a grip target (the vault ledge corner) — geometry only;
        // the animator turns it into a hand pin. Render-only, same as AnimationProgress.
        bool hasGrip = false; Vector2 gripTarget = default;
        if (p.CurrentState != null) hasGrip = p.CurrentState.TryAnimationGrip(out gripTarget);

        // An input-parametrized action may expose an aim direction (a stab's StabDir) — the
        // animator re-aims the authored horizontal overlay onto it. Render-only.
        bool hasAim = false; Vector2 aimDir = default;
        if (p.CurrentAction != null) hasAim = p.CurrentAction.TryAnimationAim(p.CurrentActionVars, out aimDir);

        // Physical ground gap: the same probe the FSM's permissive precondition uses
        // (EnvironmentContext.TryGetGround — halfHeight = floatHeight = Radius), reduced to
        // "how far above the supported rest height is the body". 0 when at/below rest.
        // Requires the world (chunks); without one, default 0 keeps the pre-gap behavior.
        float groundGap = 0f;
        if (chunks != null)
        {
            groundGap = 1e6f;   // no floor within the probe — clearly airborne
            if (GroundChecker.TryFind(p.Body, chunks, PlayerCharacter.Radius, PlayerCharacter.Radius,
                                      GroundChecker.ProbeSlack, dt, out var g))
                groundGap = MathF.Max(0f, (g.Position.Y - pos.Y) - g.MinDistance);
        }

        return new(pos, p.Body.Velocity, facing, p.IsGrounded,
               p.CurrentStateName, p.CurrentActionName, dt,
               p.CurrentActionVars.TimeInState,
               p.CurrentAction?.AnimationProgress(p.CurrentActionVars) ?? -1f,
               p.CurrentState?.AnimationProgress ?? 0f,
               surfaces: surfaces, surfaceCount: count, surfacesNear: near,
               hasGrip: hasGrip, gripTarget: gripTarget,
               hasAim: hasAim, aimDir: aimDir, tag: tag, lowCeiling: lowCeiling,
               groundGap: groundGap);
    }
}
