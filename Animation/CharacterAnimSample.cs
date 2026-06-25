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
// the movement layer already resolved (wall-slide wall, ground line — Plans/ANIMATION_SOLVER_PLAN
// §11.5); NOT the full local-SDF terrain query (a later phase). Render-only, `Normal` is unit.
public readonly struct SolverSurface
{
    public readonly Vector2 Point;   // a point on the surface (world)
    public readonly Vector2 Normal;  // unit outward normal — points into the free half-space
    public readonly float   Margin;  // keep limb points at least this far out along Normal (world px)
    public SolverSurface(Vector2 point, Vector2 normal, float margin) { Point = point; Normal = normal; Margin = margin; }
}

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
    public readonly string  MovementState;  // PlayerCharacter.CurrentStateName
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
    // wall, ground); only read by the solver path, so zero-cost when the solver is off.
    public readonly SolverSurface[] Surfaces;

    public CharacterAnimSample(
        Vector2 position, Vector2 velocity, int facing, bool grounded,
        string movementState, string action, float dt, float actionTime = 0f,
        float actionDuration = 0f, float movementProgress = 0f, ExternalPin[] pins = null,
        SolverSurface[] surfaces = null)
    {
        Position = position; Velocity = velocity; Facing = facing; Grounded = grounded;
        MovementState = movementState; Action = action; Dt = dt; ActionTime = actionTime;
        ActionDuration = actionDuration; MovementProgress = movementProgress; Pins = pins;
        Surfaces = surfaces;
    }

    // Pull the sample from a live character through its public surface only. The
    // direction of the dependency is animation -> character, never the reverse.
    public static CharacterAnimSample From(PlayerCharacter p, float dt)
    {
        var pos = p.Body.Position;
        int facing = p.Facing;
        string state = p.CurrentStateName;

        // While wall-sliding the rig faces the wall (+X = the wall direction). The wall the
        // slide resolved sits at the body's leading edge; its outward normal points back into
        // open space. Hand it to the solver as a no-penetration half-plane (Position/Radius are
        // public, so this is a render-only read — §11.5) so the trailing limbs don't clip into
        // the wall. The braced grip hand/foot rest ON the surface (gap ≈ 0, just inside Margin).
        SolverSurface[] surfaces = null;
        if (state != null && state.Contains("WallSlid") && facing != 0)
        {
            var wallPoint  = new Vector2(pos.X + facing * PlayerCharacter.Radius, pos.Y);
            var wallNormal = new Vector2(-facing, 0f);
            surfaces = new[] { new SolverSurface(wallPoint, wallNormal, 1.5f) };
        }

        return new(pos, p.Body.Velocity, facing, p.IsGrounded,
               state, p.CurrentActionName, dt,
               p.CurrentActionVars.TimeInState,
               p.CurrentAction?.OverlayDuration ?? 0f,
               p.CurrentState?.AnimationProgress ?? 0f,
               surfaces: surfaces);
    }
}
