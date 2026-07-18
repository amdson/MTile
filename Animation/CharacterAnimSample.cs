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
public enum AnimTag { None, Parkour, WallSlide, Crouch, LedgeGrab, LedgePull, Stunned }

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
    // Nominal total length of the current action (ActionState.OverlayDuration), seconds.
    // The overlay clip is remapped to span exactly [0, ActionDuration] so it plays
    // through once over the action's lifetime. 0 = no fixed length → the animator uses
    // the clip's own Duration instead.
    public readonly float   ActionDuration;
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
    // A solid ceiling sits right above the (crouched) body this frame — the SAME signal
    // CrouchedState uses to stay crouched with Down released (CeilingChecker.TryFind). When
    // set on a still crouch it selects the DuckUnder clip (head tucked, torso flat, free hand
    // braced) so squeezing through a low gap reads as "under something". Render-only.
    public readonly bool    LowCeiling;

    public CharacterAnimSample(
        Vector2 position, Vector2 velocity, int facing, bool grounded,
        string movementState, string action, float dt, float actionTime = 0f,
        float actionDuration = 0f, float movementProgress = 0f, ExternalPin[] pins = null,
        SolverSurface[] surfaces = null, bool hasGrip = false, Vector2 gripTarget = default,
        bool hasAim = false, Vector2 aimDir = default, AnimTag tag = AnimTag.None,
        int surfaceCount = -1, bool? surfacesNear = null, bool lowCeiling = false)
    {
        Position = position; Velocity = velocity; Facing = facing; Grounded = grounded;
        MovementState = movementState; Action = action; Dt = dt; ActionTime = actionTime;
        ActionDuration = actionDuration; MovementProgress = movementProgress; Pins = pins;
        Surfaces = surfaces; SurfaceCount = surfaceCount; HasGrip = hasGrip; GripTarget = gripTarget;
        HasAim = hasAim; AimDir = aimDir; Tag = tag; LowCeiling = lowCeiling;
        // Default (hand-built samples, tests): surfaces present ⇒ near — the pre-terrain behavior.
        SurfacesNear = surfacesNear ?? (surfaces != null && (surfaceCount < 0 ? surfaces.Length : surfaceCount) > 0);
    }

    // Scratch for the wall-slide half-plane, reused every frame instead of allocating a
    // fresh array (render-only + single-threaded: samples are built and consumed one
    // character at a time within the same Update, so reuse is safe).
    private static readonly SolverSurface[] _wallSurfaceScratch = new SolverSurface[1];

    // Pull the sample from a live character through its public surface only. The
    // direction of the dependency is animation -> character, never the reverse.
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

        // Is there a solid ceiling right overhead while crouched? Reuse CeilingChecker.TryFind —
        // the exact query CrouchedState.CheckConditions uses to stay crouched with Down released.
        // Only meaningful for a crouch, so skip the tile query otherwise. Render-only read of the
        // public body + chunks; nothing flows back into the sim.
        bool lowCeiling = tag == AnimTag.Crouch && chunks != null
                          && CeilingChecker.TryFind(p.Body, chunks, out _);

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

        return new(pos, p.Body.Velocity, facing, p.IsGrounded,
               p.CurrentStateName, p.CurrentActionName, dt,
               p.CurrentActionVars.TimeInState,
               p.CurrentAction?.OverlayDuration ?? 0f,
               p.CurrentState?.AnimationProgress ?? 0f,
               surfaces: surfaces, surfaceCount: count, surfacesNear: near,
               hasGrip: hasGrip, gripTarget: gripTarget,
               hasAim: hasAim, aimDir: aimDir, tag: tag, lowCeiling: lowCeiling);
    }
}
