using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

public class JumpingState : MovementState
{
    public override int ActivePriority => 50;
    public override int PassivePriority => 30;

    private bool _jumpReleased;
    private float _timeInState;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (!abilities.JumpJustPressed || !ctx.TryGetGround(out var ground)) return false;
        // Low ceiling (≤ 2 tiles) overhead: head would smack — defer to CoveredJumpState.
        if (ctx.TryGetCeiling(out var ceiling)
            && ground.Position.Y - ceiling.Position.Y <= 2 * Chunk.TileSize) return false;
        return true;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        return !_jumpReleased && _timeInState < MovementConfig.Current.MaxJumpHoldTime;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        _timeInState = 0f;
        _jumpReleased = !ctx.Input.Space;
        ctx.Body.Velocity.Y = MovementConfig.Current.JumpVelocity;

        ctx.Body.Constraints.RemoveAll(c => c is FloatingSurfaceDistance);
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        _timeInState += ctx.Dt;
        if (!ctx.Input.Space) _jumpReleased = true;

        var force = Vector2.Zero;
        force.Y += MovementConfig.Current.JumpHoldForce;
        if (_timeInState <= ctx.Dt) 
            force.Y += MovementConfig.Current.JumpInitForce;
        
        float inputX = (ctx.Input.Right ? 1f : 0f) - (ctx.Input.Left ? 1f : 0f);
        if (inputX != 0f)
        {
            force.X += inputX * MovementConfig.Current.AirAccel;
            float excess = MathF.Abs(ctx.Body.Velocity.X) - MovementConfig.Current.MaxAirSpeed;
            if (excess > 0f && MathF.Sign(ctx.Body.Velocity.X) == MathF.Sign(inputX) && ctx.Dt > 0f)
                force.X -= MathF.Sign(ctx.Body.Velocity.X) * excess / ctx.Dt;
        }
        else if (ctx.Dt > 0f)
        {
            force.X = Math.Clamp(-ctx.Body.Velocity.X / ctx.Dt, -MovementConfig.Current.AirDrag, MovementConfig.Current.AirDrag);
        }

        ctx.Body.AppliedForce = force;
    }
}

public class RunningJumpState : MovementState
{
    public override int ActivePriority => 55;
    public override int PassivePriority => 35;

    private bool _jumpReleased;
    private float _timeInState;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (!abilities.JumpJustPressed || !ctx.TryGetGround(out var ground)) return false;
        if (Math.Abs(ctx.Body.Velocity.X) < MovementConfig.Current.RunJumpMinSpeed) return false;
        if (ctx.TryGetCeiling(out var ceiling)
            && ground.Position.Y - ceiling.Position.Y <= 2 * Chunk.TileSize) return false;
        return true;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        return !_jumpReleased && _timeInState < MovementConfig.Current.MaxJumpHoldTime;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        _timeInState = 0f;
        _jumpReleased = !ctx.Input.Space;
        ctx.Body.Velocity.Y = MovementConfig.Current.RunJumpVelocity;
        
        ctx.Body.Constraints.RemoveAll(c => c is FloatingSurfaceDistance);
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        _timeInState += ctx.Dt;
        if (!ctx.Input.Space) _jumpReleased = true;

        var force = Vector2.Zero;
        force.Y += MovementConfig.Current.RunJumpHoldForce;
        if (_timeInState <= ctx.Dt) 
            force.Y += MovementConfig.Current.JumpInitForce;
        
        float inputX = (ctx.Input.Right ? 1f : 0f) - (ctx.Input.Left ? 1f : 0f);
        if (inputX != 0f)
        {
            force.X += inputX * MovementConfig.Current.AirAccel;
            float excess = MathF.Abs(ctx.Body.Velocity.X) - MovementConfig.Current.MaxAirSpeed;
            if (excess > 0f && MathF.Sign(ctx.Body.Velocity.X) == MathF.Sign(inputX) && ctx.Dt > 0f)
                force.X -= MathF.Sign(ctx.Body.Velocity.X) * excess / ctx.Dt;
        }
        else if (ctx.Dt > 0f)
        {
            force.X = Math.Clamp(-ctx.Body.Velocity.X / ctx.Dt, -MovementConfig.Current.AirDrag, MovementConfig.Current.AirDrag);
        }

        ctx.Body.AppliedForce = force;
    }
}

public class WallSlidingState : MovementState
{
    private readonly int _wallDir;
    private FloatingSurfaceDistance _wall;
    private FloatingSurfaceDistance _ground;

    public WallSlidingState(int wallDir)
    {
        _wallDir = wallDir;
    }

    public override int ActivePriority => 20;
    public override int PassivePriority => 20;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        bool pressingIntoWall = (_wallDir == 1 && ctx.Input.Right) || (_wallDir == -1 && ctx.Input.Left);
        return pressingIntoWall && !ctx.TryGetCeiling(out _) && !IsActuallyGrounded(ctx) && ctx.TryGetWall(_wallDir, out _);
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        bool pressingIntoWall = (_wallDir == 1 && ctx.Input.Right) || (_wallDir == -1 && ctx.Input.Left);
        return pressingIntoWall && !ctx.TryGetCeiling(out _) && !IsActuallyGrounded(ctx) && ctx.TryGetWall(_wallDir, out _);
    }

    // GroundChecker.TryFind reports "grounded" whenever the floor is within ProbeSlack (20px) below
    // the body's bottom vertex — i.e. a body still ~20px above its rest height counts. For most
    // states that slack is right (lets the body stick to the floor through small bounces / slopes),
    // but during a wall-slide it means a body that's visually still mid-air against a wall — but
    // happens to have a floor in range below — exits to FallingState→StandingState before it
    // actually lands. Use a tighter test here: only count as grounded once the body's reached its
    // rest height (≈ 2·Radius above the floor).
    private static bool IsActuallyGrounded(EnvironmentContext ctx)
    {
        if (!ctx.TryGetGround(out var ground)) return false;
        float dist = ground.Position.Y - ctx.Body.Position.Y;
        return dist <= 2f * PlayerCharacter.Radius + 2f;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (ctx.TryGetWall(_wallDir, out var contact))
        {
            _wall = contact;
            ctx.Body.Constraints.Add(_wall);
        }
        if (ctx.TryGetGround(out var ground))
        {
            _ground = ground;
            ctx.Body.Constraints.Add(_ground);
        }
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (_wall != null)
            ctx.Body.Constraints.Remove(_wall);
        _wall = null;
        if (_ground != null)
            ctx.Body.Constraints.Remove(_ground);
        _ground = null;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (ctx.TryGetWall(_wallDir, out var refreshed))
        {
            _wall.Position = refreshed.Position;
            _wall.Normal = refreshed.Normal;
            _wall.MinDistance = refreshed.MinDistance;
        }
        if (ctx.TryGetGround(out var refreshedGround) && _ground != null)
        {
            _ground.Position    = refreshedGround.Position;
            _ground.Normal      = refreshedGround.Normal;
            _ground.MinDistance = refreshedGround.MinDistance;
        }

        float terminalSpeed = ctx.Input.Down
            ? MovementConfig.Current.FastSlideTerminalSpeed
            : MovementConfig.Current.SlideTerminalSpeed;

        float vy = ctx.Body.Velocity.Y;
        ctx.Body.AppliedForce = vy > 0f
            ? new Vector2(0f, -(vy / terminalSpeed) * MovementConfig.Current.SlideDrag)
            : Vector2.Zero;
    }
}

public class WallJumpingState : MovementState
{
    private readonly int _wallDir;
    private float _timeInState;
    private bool _jumpReleased;

    public WallJumpingState(int wallDir)
    {
        _wallDir = wallDir;
    }

    public override int ActivePriority => 50;
    // Strictly above DoubleJumping's 40 — when both could fire (player near a wall, jump tapped,
    // double-jump still available), WallJump wins. DoubleJump still fires when no wall is detected.
    public override int PassivePriority => 45;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        // Any horizontal arrow held — either pressing INTO the wall (the classic wall-slide jump) or
        // pressing AWAY from it (falling alongside a wall, kicking off it). Both should fire WallJump.
        // The no-input case (`Space` with no arrow held) falls through to DoubleJumping.
        bool pressingHorizontal = ctx.Input.Left || ctx.Input.Right;
        return pressingHorizontal && abilities.JumpJustPressed && ctx.TryGetWall(_wallDir, out _);
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        return !_jumpReleased && _timeInState < MovementConfig.Current.WallJumpMaxHoldTime;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        _timeInState = 0f;
        _jumpReleased = !ctx.Input.Space;
        
        int dirAwayFromWall = _wallDir == 1 ? -1 : 1;
        ctx.Body.Velocity = new Vector2(dirAwayFromWall * MovementConfig.Current.WallJumpInitialVelX, MovementConfig.Current.WallJumpInitialVelY);
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        _timeInState += ctx.Dt;
        bool jumpHeld = ctx.Input.Space;
        if (!jumpHeld) _jumpReleased = true;

        var force = Vector2.Zero;
        force.Y += MovementConfig.Current.WallJumpHoldForce;

        float inputX = (ctx.Input.Right ? 1f : 0f) - (ctx.Input.Left ? 1f : 0f);
        if (inputX != 0f)
        {
            force.X += inputX * MovementConfig.Current.WallJumpAirAccel;
            float excess = MathF.Abs(ctx.Body.Velocity.X) - MovementConfig.Current.WallJumpMaxAirSpeed;
            if (excess > 0f && MathF.Sign(ctx.Body.Velocity.X) == MathF.Sign(inputX) && ctx.Dt > 0f)
                force.X -= MathF.Sign(ctx.Body.Velocity.X) * excess / ctx.Dt;
        }
        else if (ctx.Dt > 0f)
        {
            force.X = Math.Clamp(-ctx.Body.Velocity.X / ctx.Dt, -MovementConfig.Current.WallJumpAirDrag, MovementConfig.Current.WallJumpAirDrag);
        }

        if (ctx.Input.Down)
        {
            force.Y += MovementConfig.Current.FastFallForce;
        }

        ctx.Body.AppliedForce = force;
    }
}

public class DoubleJumpingState : MovementState
{
    public override int ActivePriority => 60;
    public override int PassivePriority => 40;

    private bool _jumpReleased;
    private float _timeInState;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        // No wall check: when the player IS pressing into a wall, WallJumpingState wins the tie
        // via earlier registration (same Passive=40, first-found wins). When they're not pressing
        // into a wall — e.g. dropping off a platform while holding the away direction — DoubleJump
        // is the right fire here.
        return abilities.JumpJustPressed && !abilities.HasDoubleJumped && !ctx.TryGetGround(out _);
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        return !_jumpReleased && _timeInState < MovementConfig.Current.DoubleJumpMaxHoldTime;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        _timeInState = 0f;
        _jumpReleased = !ctx.Input.Space;
        abilities.HasDoubleJumped = true;
        
        // Kill existing vertical momentum entirely
        ctx.Body.Velocity.Y = MovementConfig.Current.DoubleJumpVelocity;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        _timeInState += ctx.Dt;
        if (!ctx.Input.Space) _jumpReleased = true;

        var force = Vector2.Zero;
        force.Y += MovementConfig.Current.DoubleJumpHoldForce;
        if (_timeInState <= ctx.Dt)
            force.Y += MovementConfig.Current.DoubleJumpInitForce;

        float inputX = (ctx.Input.Right ? 1f : 0f) - (ctx.Input.Left ? 1f : 0f);
        if (inputX != 0f)
        {
            force.X += inputX * MovementConfig.Current.AirAccel;
            float excess = MathF.Abs(ctx.Body.Velocity.X) - MovementConfig.Current.MaxAirSpeed;
            if (excess > 0f && MathF.Sign(ctx.Body.Velocity.X) == MathF.Sign(inputX) && ctx.Dt > 0f)
                force.X -= MathF.Sign(ctx.Body.Velocity.X) * excess / ctx.Dt;
        }
        else if (ctx.Dt > 0f)
        {
            force.X = Math.Clamp(-ctx.Body.Velocity.X / ctx.Dt, -MovementConfig.Current.AirDrag, MovementConfig.Current.AirDrag);
        }

        ctx.Body.AppliedForce = force;
    }
}

// Jump initiated while partially under an overhang. Phase 1 (SlidingOut): the jump impulse is
// withheld — the body stays grounded (just gravity + tile collision, so at its natural low resting
// height, which is what fits under a low slab) and walks toward the open side, an Under SteeringRamp
// stamped on the overhang's bottom corner keeping the head from clipping it. The instant nothing's
// overhead (!TryGetCeiling) it flips to phase 2 (Jumping): a verbatim ground jump (JumpVelocity +
// JumpHoldForce) that does NOT consume the double jump. Held-jump only for now (a tapped-jump
// buffered variant is TBD). Replaces the old diagonal "ceiling jump" launch.
public class CoveredJumpState : MovementState
{
    private enum Phase { SlidingOut, Jumping }

    private int _openDir;
    private float _slideSpeed;          // |horizontal velocity| at Enter, floored at MaxWalkSpeed — preserved through the slide
    private SteeringRamp _ramp;
    private FloatingSurfaceDistance _ground;  // held through phase 1: the body keeps its standing
                                              // float height so the ceiling probe (anchored on the
                                              // head) doesn't slip off the overhead slab and fire
                                              // phase 2 prematurely. Removed on the phase-2 transition.
    private Phase _phase;
    private float _slideTime;
    private float _jumpHoldTime;
    private bool _jumpReleased;

    public override int ActivePriority  => MovementPriorities.CoveredJumpActive;
    public override int PassivePriority => MovementPriorities.CoveredJumpPassive;

    // Side to exit toward: if the player's pressing a direction, honor it (never flip to the opposite
    // side even if its edge is closer). From a standstill, pick whichever edge is nearer. An edge
    // only "counts" if the body's leading vertex has actually pushed past it — until then the player
    // is still deep enough under the overhang that a slide-then-jump isn't the right move yet.
    // (Derived from CeilingChecker, not ExposedLowerCornerChecker: the latter only sees slabs whose
    // bottom is within ~Radius of the head, which never holds for a grounded body on a tile-aligned
    // floor — see CeilingChecker.TryFindExitEdge.)
    private static bool TryPickOpenDir(EnvironmentContext ctx, out int dir, out Vector2 corner)
    {
        int want = ctx.Intent.CurrentHorizontal;
        var bounds = ctx.Body.Bounds;

        if (want != 0)
        {
            if (CeilingChecker.TryFindExitEdge(ctx.Body, ctx.Chunks, want, out corner)
                && IsStickingOut(bounds, want, corner.X))
            { dir = want; return true; }
            dir = 0; corner = default; return false;
        }
        // Standstill: closer edge wins. If only one side has one (and the body's past it), pick it.
        bool hasR = CeilingChecker.TryFindExitEdge(ctx.Body, ctx.Chunks,  1, out var cR) && IsStickingOut(bounds,  1, cR.X);
        bool hasL = CeilingChecker.TryFindExitEdge(ctx.Body, ctx.Chunks, -1, out var cL) && IsStickingOut(bounds, -1, cL.X);
        if (!hasR && !hasL) { dir = 0; corner = default; return false; }
        if (!hasL) { dir =  1; corner = cR; return true; }
        if (!hasR) { dir = -1; corner = cL; return true; }
        float distR = cR.X - ctx.Body.Position.X;
        float distL = ctx.Body.Position.X - cL.X;
        if (distR <= distL) { dir =  1; corner = cR; return true; }
        dir = -1; corner = cL; return true;
    }

    // Body's leading edge has crossed the ceiling lip — i.e. some part of the polygon is no longer
    // shadowed by the overhang on side `dir`. Until this is true the player's still deep inside the
    // overhang and a slide-and-jump isn't the right maneuver yet.
    private static bool IsStickingOut(BoundingBox bounds, int dir, float edgeX)
        => dir == 1 ? bounds.Right > edgeX : bounds.Left < edgeX;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (!ctx.Input.Space) return false;            // held-jump (tapped-jump variant TBD)
        if (!ctx.TryGetGround(out var ground)) return false;
        if (!ctx.TryGetCeiling(out var ceiling)) return false;   // must actually be under something
        // Only relevant for low ceilings (≤ 2 tiles). At 3+ tiles a regular jump fits with margin —
        // JumpingState handles those, and its precondition is the complement of this one.
        if (ground.Position.Y - ceiling.Position.Y > 2 * Chunk.TileSize) return false;
        return TryPickOpenDir(ctx, out _, out _);
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (_phase == Phase.SlidingOut)
        {
            if (!ctx.Input.Space) return false;                                          // let go of jump → abort
            if (ctx.Intent.CurrentHorizontal == -_openDir) return false;                 // reversing interrupts cleanly
            return _slideTime < MovementConfig.Current.MaxCoveredSlideTime;              // stuck → bail to Falling
        }
        return !_jumpReleased && _jumpHoldTime < MovementConfig.Current.MaxJumpHoldTime;  // same as JumpingState
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        TryPickOpenDir(ctx, out _openDir, out var corner);
        _slideSpeed = MathF.Max(MathF.Abs(ctx.Body.Velocity.X), MovementConfig.Current.MaxWalkSpeed);
        _ramp = new SteeringRamp { Sense = SteeringSense.Under, ForwardDir = _openDir, Corner = corner };
        ctx.Body.Constraints.Add(_ramp);
        // Hold the ground constraint through phase 1 — see _ground.
        if (ctx.TryGetGround(out var ground))
        {
            _ground = ground;
            ctx.Body.Constraints.Add(_ground);
        }
        _phase = Phase.SlidingOut;
        _slideTime = 0f;
        // HasDoubleJumped intentionally left untouched (already false from being grounded) — this jump is "free".
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (_ramp   != null) ctx.Body.Constraints.Remove(_ramp);
        if (_ground != null) ctx.Body.Constraints.Remove(_ground);
        _ramp = null;
        _ground = null;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        var cfg = MovementConfig.Current;

        if (_phase == Phase.SlidingOut)
        {
            _slideTime += ctx.Dt;

            // Flip to the jump the instant nothing's overhead.
            if (!ctx.TryGetCeiling(out _))
            {
                if (_ground != null) { ctx.Body.Constraints.Remove(_ground); _ground = null; }
                // Drop the ramp too: with the Clearance shift it can spuriously catch a left-edge
                // vertex that's still 1-2px inside the slab's x-range right after clearing it, and
                // rotate the launch's upward velocity sideways. The slide is over — no more insurance.
                if (_ramp   != null) { ctx.Body.Constraints.Remove(_ramp);   _ramp   = null; }
                ctx.Body.Velocity.Y = cfg.JumpVelocity;
                _phase = Phase.Jumping;
                _jumpHoldTime = 0f;
                _jumpReleased = !ctx.Input.Space;
                ctx.Body.AppliedForce = Vector2.Zero;
                return;
            }

            // Keep the ramp anchored on the ceiling's exit edge (it'll go inert on its own once the body's past it).
            if (CeilingChecker.TryFindExitEdge(ctx.Body, ctx.Chunks, _openDir, out var freshCorner))
                _ramp.Corner = freshCorner;

            // Refresh the ground constraint's pose so a sloped/stepped floor doesn't fight the spring.
            if (_ground != null && ctx.TryGetGround(out var refreshedGround))
            {
                _ground.Position    = refreshedGround.Position;
                _ground.Normal      = refreshedGround.Normal;
                _ground.MinDistance = refreshedGround.MinDistance;
            }

            // Spring force toward float height (mirrors StandingState) so the body holds standing
            // height through the slide. Without this, gravity pulls the head down and the ceiling
            // probe slips off the overhead slab, firing phase 2 mid-tunnel.
            var slideForce = Vector2.Zero;
            if (_ground != null)
            {
                float dist           = Vector2.Dot(ctx.Body.Position - _ground.Position, _ground.Normal);
                float gap            = _ground.MinDistance - dist;
                float velAlongNormal = Vector2.Dot(ctx.Body.Velocity, _ground.Normal);
                if (gap > 0f)
                    slideForce += _ground.Normal * (gap * cfg.SpringK - velAlongNormal * cfg.SpringDamping);
                float velExcess = velAlongNormal - cfg.SpringMaxRiseSpeed;
                if (velExcess > 0f && ctx.Dt > 0f)
                    slideForce -= _ground.Normal * velExcess / ctx.Dt;
            }

            // Walk toward the open side, preserving entry speed (WalkAccel·dt ≈ MaxWalkSpeed ⇒ a
            // from-standstill press leaves the overhang in one frame). The Under ramp's redirect
            // (in StepSwept) handles the head if the body rises into the overhang's bottom edge.
            float along = _openDir * ctx.Body.Velocity.X;
            slideForce.X += _openDir * AirControl.SoftClampVelocity(along, _slideSpeed, cfg.WalkAccel, ctx.Dt);
            ctx.Body.AppliedForce = slideForce;
            return;
        }

        // Phase.Jumping — verbatim ground jump.
        _jumpHoldTime += ctx.Dt;
        if (!ctx.Input.Space) _jumpReleased = true;

        var force = Vector2.Zero;
        force.Y += cfg.JumpHoldForce;
        if (_jumpHoldTime <= ctx.Dt)
            force.Y += cfg.JumpInitForce;
        force.X += AirControl.Apply(ctx, cfg.AirAccel, cfg.MaxAirSpeed, cfg.AirDrag);

        ctx.Body.AppliedForce = force;
    }
}

// Steers the body over a low step (an exposed upper corner — a "vault") and/or under a low ceiling
// (an exposed lower corner — an "overcrop"/duck), using one SteeringRamp per corner. The redirect
// (in PhysicsWorld.StepSwept) rotates velocity onto the shallowest trajectory that clears the
// corner(s); when both are present (a step with a slab right above it) the redirect's multi-ramp
// combine threads the body through the gap. This state owns the ramps' lifecycle, cancels gravity
// in proportion to ramp weight (so a walking-speed climb doesn't stall at the apex), and pushes
// along the surface toward the entry speed (so the maneuver preserves momentum and bootstraps a
// from-rest entry). Phase: one-block obstacles. See STEERING_RAMP_IMPL.md / STEERING_RAMPS.md.
public class ParkourState : MovementState
{
    // Caps the along-ramp speed target at entrySpeed / MinClimbCos (≈ 4×) on near-vertical sections,
    // so a steep climb happens fast instead of stalling the body's forward progress for several frames.
    private const float MinClimbCos = 0.25f;

    private readonly int _wallDir;
    private SteeringRamp _overRamp;    // from an exposed upper corner (vault), or null
    private SteeringRamp _underRamp;   // from an exposed lower corner (overcrop/duck), or null
    private float _entrySpeed;         // |horizontal velocity| at Enter, floored at MaxWalkSpeed — the maneuver preserves this

    public ParkourState(int wallDir) => _wallDir = wallDir;

    public override int ActivePriority  => MovementPriorities.GuidedActive;   // below the jumps ⇒ a jump preempts the maneuver
    public override int PassivePriority => MovementPriorities.GuidedPassive;   // above free air/ground ⇒ triggers automatically

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        // Deliberate hold into the obstacle, from the ground, with a vault-range upper corner OR an
        // overcrop lower corner ahead. The lower-corner case is suppressed when jump was just
        // pressed, so "press jump near an overhang" goes to a jump state, not a duck (interim D2).
        if (ctx.Intent.HeldHorizontal != _wallDir || !ctx.TryGetGround(out _)) return false;
        return ctx.TryGetExposedCorner(_wallDir, out _)
            || (ctx.TryGetExposedLowerCorner(_wallDir, out _) && !ctx.Intent.JumpJustPressed);
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        // Releasing direction interrupts cleanly (velocity was never snapped, so it just falls through).
        if (ctx.Intent.CurrentHorizontal != _wallDir) return false;
        // Stay alive while a corner is still detected, or a ramp is still doing something — the
        // checker drops the corner a frame or two before the ramp goes inert at the crest/clear point.
        if (ctx.TryGetExposedCorner(_wallDir, out _) || ctx.TryGetExposedLowerCorner(_wallDir, out _)) return true;
        return (_overRamp  != null && _overRamp.Weight  > SteeringRamp.WeightEpsilon)
            || (_underRamp != null && _underRamp.Weight > SteeringRamp.WeightEpsilon);
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        abilities.HasDoubleJumped = false;
        // Preserve whatever horizontal speed the body came in with (floored at walking speed so a
        // from-rest / flush-against-the-obstacle entry still has a target to drive toward).
        _entrySpeed = MathF.Max(MathF.Abs(ctx.Body.Velocity.X), MovementConfig.Current.MaxWalkSpeed);
        Reconcile(ctx);
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (_overRamp  != null) ctx.Body.Constraints.Remove(_overRamp);
        if (_underRamp != null) ctx.Body.Constraints.Remove(_underRamp);
        _overRamp = _underRamp = null;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        Reconcile(ctx);
        if (_overRamp == null && _underRamp == null) { ctx.Body.AppliedForce = Vector2.Zero; return; }

        var verts = ctx.Body.Polygon.GetVertices(ctx.Body.Position);
        _overRamp?.Recompute(verts);
        _underRamp?.Recompute(verts);

        var cfg = MovementConfig.Current;

        // Cancel gravity in proportion to the strongest engaged ramp: the redirect (in StepSwept)
        // rescales velocity to its own magnitude every step, so without this the climb bleeds speed.
        float maxWeight = MathF.Max(_overRamp?.Weight ?? 0f, _underRamp?.Weight ?? 0f);
        Vector2 force = new Vector2(0f, -cfg.RampAntiGravForce * maxWeight);

        // Pick a push direction: with exactly one ramp engaged, push along its implicit surface and
        // scale the target by 1/cos(θ*) so the horizontal component stays near the entry speed even
        // on a steep section; otherwise (none engaged yet, or both — a "jump and duck" gap) push
        // straight forward and let the redirect's combine do the steering.
        SteeringRamp solo = null; int engaged = 0;
        if (_overRamp  != null && _overRamp.Weight  > SteeringRamp.WeightEpsilon) { engaged++; solo = _overRamp; }
        if (_underRamp != null && _underRamp.Weight > SteeringRamp.WeightEpsilon) { engaged++; solo = _underRamp; }

        Vector2 dir  = engaged == 1 ? solo.SurfaceDir : new Vector2(_wallDir, 0f);
        float   cosT = engaged == 1 ? MathF.Max(MathF.Cos(solo.ThetaStar), MinClimbCos) : 1f;
        float   targetAlong = _entrySpeed / cosT;
        force += dir * AirControl.SoftClampVelocity(Vector2.Dot(ctx.Body.Velocity, dir), targetAlong, cfg.WalkAccel, ctx.Dt);

        // Cap |velocity| at the target only when steering off a single surface (so the climb force
        // can't inflate it through the redirect's rescale); the multi-ramp combine manages its own.
        if (_overRamp  != null) _overRamp.MaxSpeed  = (engaged == 1 && solo == _overRamp)  ? targetAlong : float.PositiveInfinity;
        if (_underRamp != null) _underRamp.MaxSpeed = (engaged == 1 && solo == _underRamp) ? targetAlong : float.PositiveInfinity;

        ctx.Body.AppliedForce = force;
    }

    // Add/remove the Over and Under SteeringRamps to match the corners currently detected ahead,
    // and keep their Corner anchors fresh. Idempotent; runs every frame so a staircase or a chain
    // of obstacles is handled without a one-frame gap where the ramps blink out.
    private void Reconcile(EnvironmentContext ctx)
    {
        if (ctx.TryGetExposedCorner(_wallDir, out var up))
        {
            if (_overRamp == null)
            {
                _overRamp = new SteeringRamp { Sense = SteeringSense.Over, ForwardDir = _wallDir };
                ctx.Body.Constraints.Add(_overRamp);
            }
            _overRamp.Corner = up.InnerEdge;
        }
        else if (_overRamp != null) { ctx.Body.Constraints.Remove(_overRamp); _overRamp = null; }

        if (ctx.TryGetExposedLowerCorner(_wallDir, out var lo))
        {
            if (_underRamp == null)
            {
                _underRamp = new SteeringRamp { Sense = SteeringSense.Under, ForwardDir = _wallDir };
                ctx.Body.Constraints.Add(_underRamp);
            }
            _underRamp.Corner = lo.InnerEdge;
        }
        else if (_underRamp != null) { ctx.Body.Constraints.Remove(_underRamp); _underRamp = null; }
    }
}

// Attaches the player to a ledge. Stays active until the player drops (Down/away)
// or pulls up (Up just pressed → transitions to LedgePullState).
public class LedgeGrabState : MovementState
{
    private readonly int _wallDir;
    private FloatingSurfaceDistance _wall;
    private FloatingSurfaceDistance _floor;
    private float _timeInState;

    public LedgeGrabState(int wallDir) => _wallDir = wallDir;

    public override int ActivePriority  => 42;
    public override int PassivePriority => 42;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        // Path A: approach from side — Up pressed, wall + ledge corner above head detected
        if (ctx.TryGetWall(_wallDir, out _) && ctx.TryGetLedgeCorner(_wallDir, out _))
        {
            const int Buffer = 6;
            for (int i = 0; i < Buffer; i++)
            {
                if (ctx.PreviousState(i + 1) is LedgeGrabState or LedgePullState) break;
                if (ctx.Controller.GetPrevious(i).Up && !ctx.Controller.GetPrevious(i + 1).Up)
                    return true;
            }
        }
        // Path B: drop from above — Down just pressed, exposed corner at foot level
        if (abilities.DownJustPressed && ctx.TryGetExposedCorner(_wallDir, out _))
            return true;
        return false;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        bool pressingAway = (_wallDir == 1 && ctx.Input.Left) || (_wallDir == -1 && ctx.Input.Right);
        if (pressingAway) return false;
        // Grace period: drop-in enters with Down still held, so ignore Down for first ~3 frames.
        if (_timeInState < 0.1f) return true;
        return !ctx.Input.Down;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        _timeInState = 0f;

        // Prefer above-head corner (approach from side); fall back to foot-level (drop from above)
        Vector2 cornerEdge;
        if (ctx.TryGetLedgeCorner(_wallDir, out var grabCorner))
            cornerEdge = grabCorner.InnerEdge;
        else
        {
            ctx.TryGetExposedCorner(_wallDir, out var dropCorner);
            cornerEdge = dropCorner.InnerEdge;
        }

        ctx.Body.Velocity = Vector2.Zero;

        // Wall pin: use detected wall, or derive from corner X when approaching from above
        if (!ctx.TryGetWall(_wallDir, out _wall))
        {
            _wall = new FloatingSurfaceDistance(
                new Vector2(cornerEdge.X, ctx.Body.Position.Y),
                new Vector2(-_wallDir, 0f),
                PlayerCharacter.Radius);
        }

        _floor = new FloatingSurfaceDistance(
            new Vector2(ctx.Body.Position.X, cornerEdge.Y + 2f * PlayerCharacter.Radius),
            new Vector2(0f, -1f),
            PlayerCharacter.Radius);

        ctx.Body.Constraints.Add(_wall);
        ctx.Body.Constraints.Add(_floor);

        abilities.IsLedgeGrabbing  = true;
        abilities.GrabWallDir      = _wallDir;
        abilities.GrabbedCorner    = cornerEdge;
        abilities.HasDoubleJumped  = false;
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (_wall  != null) ctx.Body.Constraints.Remove(_wall);
        if (_floor != null) ctx.Body.Constraints.Remove(_floor);
        _wall  = null;
        _floor = null;
        abilities.IsLedgeGrabbing = false;
        abilities.GrabWallDir     = 0;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        _timeInState += ctx.Dt;
        var cfg = MovementConfig.Current;
        float hangY  = abilities.GrabbedCorner.Y + PlayerCharacter.Radius;
        float error  = ctx.Body.Position.Y - hangY;
        float vy     = ctx.Body.Velocity.Y;
        ctx.Body.AppliedForce = new Vector2(0f, -cfg.GrabGravityCancel - error * cfg.GrabSpringK - vy * cfg.GrabDamping);
    }
}

// Executes the pull-up from a ledge grab. Activated by pressing Up while grabbed.
// Releasing Up during the pull interrupts it.
public class LedgePullState : MovementState
{
    private readonly int _wallDir;
    private PointForceContact _spring;
    private FloatingSurfaceDistance _ramp;
    private float _timeInState;

    public LedgePullState(int wallDir) => _wallDir = wallDir;

    public override int ActivePriority  => 43;
    public override int PassivePriority => 43;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
        => abilities.UpJustPressed
        && abilities.IsLedgeGrabbing
        && abilities.GrabWallDir == _wallDir;

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (!ctx.Input.Up) return false;
        if (_spring == null) return false;
        if (_timeInState >= MovementConfig.Current.MaxVaultTime) return false;

        float cornerTopY = _spring.Position.Y;
        float cornerX    = _spring.Position.X;
        bool atStandingHeight = ctx.Body.Position.Y < cornerTopY - 2f * PlayerCharacter.Radius;
        bool pastCorner       = _wallDir == 1
            ? ctx.Body.Position.X > cornerX
            : ctx.Body.Position.X < cornerX;
        return !(atStandingHeight && pastCorner);
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        _timeInState = 0f;

        var cornerEdge = abilities.GrabbedCorner;
        _spring = new PointForceContact(cornerEdge);
        var rampNormal = new Vector2(-_wallDir * 0.5f, -0.5f);
        _ramp = new FloatingSurfaceDistance(cornerEdge, rampNormal, 1000f);

        ctx.Body.Constraints.Add(_spring);
        ctx.Body.Constraints.Add(_ramp);
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (_spring != null) ctx.Body.Constraints.Remove(_spring);
        if (_ramp   != null) ctx.Body.Constraints.Remove(_ramp);
        _spring = null;
        _ramp   = null;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        _timeInState += ctx.Dt;

        float cornerTopY = _spring.Position.Y;
        var   cfg        = MovementConfig.Current;
        var   force      = Vector2.Zero;

        if (ctx.Body.Position.Y >= cornerTopY - 2f * PlayerCharacter.Radius)
        {
            force.Y = -cfg.VaultLiftForce;
        }
        else
        {
            if (_ramp != null) { ctx.Body.Constraints.Remove(_ramp); _ramp = null; }
            if (ctx.Body.Velocity.Y < 0f && ctx.Dt > 0f)
                force.Y = Math.Min(-ctx.Body.Velocity.Y / ctx.Dt, 2f * cfg.VaultLiftForce);
            force.X = _wallDir * cfg.VaultPushForce;
        }

        ctx.Body.AppliedForce = force;
    }
}

// Hold Down while standing on the edge of a platform → slip off. Removes the float-height ground
// constraint so the body's no longer spring-held above the surface (gravity + tile collision keep
// it on the platform until its center clears the edge), applies a horizontal slide force in the
// chosen direction (so a crouched/sunken body actually gets pushed out from over the platform —
// same idiom as CoveredJumpState's phase 1), and stamps an Over SteeringRamp on the platform's edge
// corner as insurance against clipping the corner mid-slip. Exits to FallingState the instant the
// body's no longer over any floor.
public class DropdownState : MovementState
{
    private int _dropDir;
    private SteeringRamp _ramp;
    private float _slideSpeed;
    private float _slideTime;
    private bool _exitingAirborne;   // set when CheckConditions detects the body just left the platform — Exit dampens Vx accordingly

    public override int ActivePriority  => MovementPriorities.DropdownActive;
    public override int PassivePriority => MovementPriorities.DropdownPassive;

    // Same pattern as CoveredJumpState.TryPickOpenDir: honor input direction strictly when held,
    // closer edge from a standstill, never flip to the opposite side. Edge from GroundChecker.
    private static bool TryPickDropDir(EnvironmentContext ctx, out int dir, out Vector2 corner)
    {
        int want = ctx.Intent.CurrentHorizontal;
        if (want != 0)
        {
            if (GroundChecker.TryFindDropEdge(ctx.Body, ctx.Chunks, want, out corner)) { dir = want; return true; }
            dir = 0; corner = default; return false;
        }
        bool hasR = GroundChecker.TryFindDropEdge(ctx.Body, ctx.Chunks,  1, out var cR);
        bool hasL = GroundChecker.TryFindDropEdge(ctx.Body, ctx.Chunks, -1, out var cL);
        if (!hasR && !hasL) { dir = 0; corner = default; return false; }
        if (!hasL) { dir =  1; corner = cR; return true; }
        if (!hasR) { dir = -1; corner = cL; return true; }
        float distR = cR.X - ctx.Body.Position.X;
        float distL = ctx.Body.Position.X - cL.X;
        if (distR <= distL) { dir =  1; corner = cR; return true; }
        dir = -1; corner = cL; return true;
    }

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (!ctx.Input.Down) return false;
        if (!ctx.TryGetGround(out _)) return false;
        return TryPickDropDir(ctx, out _, out _);
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (!ctx.Input.Down) return false;
        if (!ctx.TryGetGround(out _)) { _exitingAirborne = true; return false; }   // body's airborne ⇒ Falling takes over
        return _slideTime < MovementConfig.Current.MaxDropdownTime;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        TryPickDropDir(ctx, out _dropDir, out var corner);
        // MaxWalkSpeed is the slide target — fast enough to clear the corner within MaxDropdownTime
        // from a standstill. Running entries keep their momentum since Update only applies force when
        // the body's slower than the target.
        _slideSpeed = MovementConfig.Current.MaxWalkSpeed;
        _ramp = new SteeringRamp { Sense = SteeringSense.Over, ForwardDir = _dropDir, Corner = corner };
        ctx.Body.Constraints.Add(_ramp);
        _slideTime = 0f;
        _exitingAirborne = false;
        // No FloatingSurfaceDistance: the body's leaving the surface, so don't spring it back up.
        // StandingState/CrouchedState's ground constraint was already removed on their Exit.
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (_ramp != null) ctx.Body.Constraints.Remove(_ramp);
        _ramp = null;
        // Soften the horizontal velocity on the slip-off so the drop lands close to the wall rather
        // than flinging the body forward at the full slide speed. Only apply when we exited via going
        // airborne (not on cancel via !Down or timeout).
        if (_exitingAirborne)
            ctx.Body.Velocity.X *= MovementConfig.Current.DropdownExitVelMult;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        _slideTime += ctx.Dt;
        var cfg = MovementConfig.Current;

        // Refresh ramp anchor (the corner may shift slightly as the body crosses tile boundaries).
        if (GroundChecker.TryFindDropEdge(ctx.Body, ctx.Chunks, _dropDir, out var freshCorner))
            _ramp.Corner = freshCorner;

        // Slide toward the edge, but never brake a faster-than-target body — a running entry should
        // keep its momentum through the slide. Gravity does the vertical work.
        float along = _dropDir * ctx.Body.Velocity.X;
        float fx = 0f;
        if (along < _slideSpeed)
            fx = _dropDir * AirControl.SoftClampVelocity(along, _slideSpeed, cfg.WalkAccel, ctx.Dt);
        ctx.Body.AppliedForce = new Vector2(fx, 0f);
    }
}
