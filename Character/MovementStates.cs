using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

public class JumpingState : MovementState
{
    public override int ActivePriority => MovementPriorities.JumpActive;
    public override int PassivePriority => MovementPriorities.JumpPassive;
    public override MovementCapability RequiredCapabilities => MovementCapability.Jump;

    private FloatingSurfaceDistance _source;

    public override void ResetTransient() => _source = null;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        // Buffered jump intent rather than a raw edge: a press up to
        // JumpBufferFrames before landing still fires. Consumed in Enter.
        if (!ctx.Intents.Peek(IntentType.Jump, ctx.CurrentFrame, out _, ctx.JumpBufferFrames)) return false;
        if (!ctx.TryGetGround(out var ground)) return false;
        // Hitstun/stun lock-out is enforced centrally via RequiredCapabilities.Jump
        // (the selection loop drops jump candidates while BlocksJump). Movement
        // otherwise stays free — it only blocks the cheap vertical-reset option.
        // Low ceiling (≤ 2 tiles) overhead: head would smack — defer to CoveredJumpState.
        if (ctx.TryGetCeiling(out var ceiling)
            && ground.Position.Y - ceiling.Position.Y <= 2 * Chunk.TileSize) return false;
        return true;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (vars.JumpReleased || vars.TimeInState >= MovementConfig.Current.MaxJumpHoldTime) return false;
        // The jump is anchored to its source surface. Once the body has risen out
        // of the (wider-than-Standing) probe window, the "relative-to-source" frame
        // no longer means anything — end the jump and let Falling take over.
        return TryFindSource(ctx, out _);
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        vars.TimeInState = 0f;
        vars.JumpReleased = !ctx.Input.Space;
        ctx.Intents.Consume(IntentType.Jump, ctx.CurrentFrame, ctx.JumpBufferFrames);

        // Replace any pre-existing FSD (e.g. StandingState's _ground) with our own
        // source FSD: same kind of contact, just tuned for an airborne body.
        ctx.Body.Constraints.RemoveAll(c => c is FloatingSurfaceDistance);
        EnsureSource(ctx);

        // Vertical velocity is set *relative* to the source surface, not added to
        // the body's current vy. Adding to the current velocity produces pathological
        // launches when the body enters with redirected vy (e.g. mid-Parkour ramp).
        float sourceVy = _source?.SurfaceVelocity.Y ?? 0f;
        ctx.Body.Velocity.Y = sourceVy + MovementConfig.Current.JumpVelocity;
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (_source != null) ctx.Body.Constraints.Remove(_source);
        _source = null;
    }

    // Idempotent source-FSD acquisition — see StandingState.EnsureGround. No-op in
    // normal play (Enter established it); rebuilds after a restore drops it.
    private void EnsureSource(EnvironmentContext ctx)
    {
        if (_source != null) return;
        if (TryFindSource(ctx, out _source))
        {
            // Airborne — no tangential coupling to the source surface, else friction
            // would dominate the gentle air-drag tangential dynamics.
            _source.Friction = 0f;
            ctx.Body.Constraints.Add(_source);
        }
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        EnsureSource(ctx);
        vars.TimeInState += ctx.Dt;
        if (!ctx.Input.Space) vars.JumpReleased = true;

        // Refresh the source FSD's pose so the body's vertical motion is tracked
        // relative to a moving source surface throughout the jump.
        if (_source != null && TryFindSource(ctx, out var refreshed))
        {
            _source.Position        = refreshed.Position;
            _source.Normal          = refreshed.Normal;
            _source.MinDistance     = refreshed.MinDistance;
            _source.SurfaceVelocity = refreshed.SurfaceVelocity;
        }

        var cfg = MovementConfig.Current;
        var m   = ctx.Modifiers;
        var force = Vector2.Zero;
        force.Y += cfg.JumpHoldForce;
        if (vars.TimeInState <= ctx.Dt)
            force.Y += cfg.JumpInitForce;

        force.X = AirControl.Apply(ctx,
            cfg.AirAccel    * m.AirAccel,
            cfg.MaxAirSpeed * m.MaxAirSpeed,
            cfg.AirDrag     * m.AirDrag);

        ctx.Body.AppliedForce = force;
    }

    private static bool TryFindSource(EnvironmentContext ctx, out FloatingSurfaceDistance source)
        => GroundChecker.TryFind(
            ctx.Body, ctx.Chunks,
            PlayerCharacter.Radius, PlayerCharacter.Radius,
            MovementConfig.Current.JumpSourceProbeSlack,
            ctx.Dt,
            out source);
}

public class RunningJumpState : MovementState
{
    public override int ActivePriority => MovementPriorities.RunningJumpActive;
    public override int PassivePriority => MovementPriorities.RunningJumpPassive;
    public override MovementCapability RequiredCapabilities => MovementCapability.Jump;

    private FloatingSurfaceDistance _source;

    public override void ResetTransient() => _source = null;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (!ctx.Intents.Peek(IntentType.Jump, ctx.CurrentFrame, out _, ctx.JumpBufferFrames)) return false;
        if (!ctx.TryGetGround(out var ground)) return false;
        if (Math.Abs(ctx.Body.Velocity.X) < MovementConfig.Current.RunJumpMinSpeed) return false;
        if (ctx.TryGetCeiling(out var ceiling)
            && ground.Position.Y - ceiling.Position.Y <= 2 * Chunk.TileSize) return false;
        return true;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (vars.JumpReleased || vars.TimeInState >= MovementConfig.Current.MaxJumpHoldTime) return false;
        return TryFindSource(ctx, out _);
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        vars.TimeInState = 0f;
        vars.JumpReleased = !ctx.Input.Space;
        ctx.Intents.Consume(IntentType.Jump, ctx.CurrentFrame, ctx.JumpBufferFrames);

        ctx.Body.Constraints.RemoveAll(c => c is FloatingSurfaceDistance);
        EnsureSource(ctx);

        // See JumpingState.Enter — vy is relative to source, not additive.
        float sourceVy = _source?.SurfaceVelocity.Y ?? 0f;
        ctx.Body.Velocity.Y = sourceVy + MovementConfig.Current.RunJumpVelocity;
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (_source != null) ctx.Body.Constraints.Remove(_source);
        _source = null;
    }

    // Idempotent source-FSD acquisition — see JumpingState.EnsureSource.
    private void EnsureSource(EnvironmentContext ctx)
    {
        if (_source != null) return;
        if (TryFindSource(ctx, out _source))
        {
            _source.Friction = 0f;
            ctx.Body.Constraints.Add(_source);
        }
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        EnsureSource(ctx);
        vars.TimeInState += ctx.Dt;
        if (!ctx.Input.Space) vars.JumpReleased = true;

        if (_source != null && TryFindSource(ctx, out var refreshed))
        {
            _source.Position        = refreshed.Position;
            _source.Normal          = refreshed.Normal;
            _source.MinDistance     = refreshed.MinDistance;
            _source.SurfaceVelocity = refreshed.SurfaceVelocity;
        }

        var cfg = MovementConfig.Current;
        var m   = ctx.Modifiers;
        var force = Vector2.Zero;
        force.Y += cfg.RunJumpHoldForce;
        if (vars.TimeInState <= ctx.Dt)
            force.Y += cfg.JumpInitForce;

        force.X = AirControl.Apply(ctx,
            cfg.AirAccel    * m.AirAccel,
            cfg.MaxAirSpeed * m.MaxAirSpeed,
            cfg.AirDrag     * m.AirDrag);

        ctx.Body.AppliedForce = force;
    }

    private static bool TryFindSource(EnvironmentContext ctx, out FloatingSurfaceDistance source)
        => GroundChecker.TryFind(
            ctx.Body, ctx.Chunks,
            PlayerCharacter.Radius, PlayerCharacter.Radius,
            MovementConfig.Current.JumpSourceProbeSlack,
            ctx.Dt,
            out source);
}

public class WallSlidingState : MovementState
{
    private readonly int _wallDir;
    private FloatingSurfaceDistance _wall;
    private FloatingSurfaceDistance _ground;

    public override void ResetTransient() { _wall = null; _ground = null; }

    public WallSlidingState(int wallDir)
    {
        _wallDir = wallDir;
    }

    public override int ActivePriority => MovementPriorities.WallSlideActive;
    public override int PassivePriority => MovementPriorities.WallSlidePassive;
    // Blocked during combat hitstun/stun (Phase 4) — a hit toward a wall can't be
    // cancelled by clinging to it.
    public override MovementCapability RequiredCapabilities => MovementCapability.WallCling;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        bool pressingIntoWall = (_wallDir == 1 && ctx.Input.Right) || (_wallDir == -1 && ctx.Input.Left);
        return pressingIntoWall && !ctx.TryGetCeiling(out _) && !IsActuallyGrounded(ctx) && ctx.TryGetWall(_wallDir, out _);
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
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

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        EnsureContacts(ctx);
        // Face the wall while clinging. Facing is otherwise only refreshed while grounded
        // (PlayerCharacter.Update), so airborne it holds the last-grounded value — which can
        // point AWAY from the wall, leaving the rig facing outward. Entering a wall-slide always
        // means pressing into the wall (CheckConditions), so facing the wall direction matches
        // the held input. Snapshot-safe sim state (PlayerAbilityState.Facing).
        abilities.Facing = _wallDir;
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (_wall != null)
            ctx.Body.Constraints.Remove(_wall);
        _wall = null;
        if (_ground != null)
            ctx.Body.Constraints.Remove(_ground);
        _ground = null;
    }

    // Idempotent wall/ground acquisition — see StandingState.EnsureGround. Ground is
    // optional (a wall-slide with no floor in range keeps _ground null).
    private void EnsureContacts(EnvironmentContext ctx)
    {
        if (_wall == null && ctx.TryGetWall(_wallDir, out var contact))
        {
            _wall = contact;
            ctx.Body.Constraints.Add(_wall);
        }
        if (_ground == null && ctx.TryGetGround(out var ground))
        {
            _ground = ground;
            ctx.Body.Constraints.Add(_ground);
        }
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        EnsureContacts(ctx);
        abilities.Facing = _wallDir;   // hold the rig facing the wall for the whole slide

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
        // Restore double jump
        abilities.HasDoubleJumped = false;
    }
}

public class WallJumpingState : MovementState
{
    private readonly int _wallDir;

    public WallJumpingState(int wallDir)
    {
        _wallDir = wallDir;
    }

    public override int ActivePriority => MovementPriorities.WallJumpActive;
    public override int PassivePriority => MovementPriorities.WallJumpPassive;
    public override MovementCapability RequiredCapabilities => MovementCapability.Jump;

    // Read by LedgePullState.Suppresses to decide whether a mid-pull wall jump is an
    // away-press bail-out (allowed) or an inward press (suppressed → queues for LedgeJump).
    public int WallDir => _wallDir;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        // Any horizontal arrow held — either pressing INTO the wall (the classic wall-slide jump) or
        // pressing AWAY from it (falling alongside a wall, kicking off it). Both should fire WallJump.
        // The no-input case (`Space` with no arrow held) falls through to DoubleJumping.
        // (The mid-pull "inward press queues for LedgeJump instead" rule lives in
        // LedgePullState.Suppresses, not here.)
        bool pressingHorizontal = ctx.Input.Left || ctx.Input.Right;
        if (!pressingHorizontal) return false;
        if (!ctx.Intents.Peek(IntentType.Jump, ctx.CurrentFrame, out _, ctx.JumpBufferFrames)) return false;
        return ctx.TryGetWall(_wallDir, out _);
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        return !vars.JumpReleased && vars.TimeInState < MovementConfig.Current.WallJumpMaxHoldTime;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        vars.TimeInState = 0f;
        vars.JumpReleased = !ctx.Input.Space;
        ctx.Intents.Consume(IntentType.Jump, ctx.CurrentFrame, ctx.JumpBufferFrames);

        int dirAwayFromWall = _wallDir == 1 ? -1 : 1;
        ctx.Body.Velocity = new Vector2(dirAwayFromWall * MovementConfig.Current.WallJumpInitialVelX, MovementConfig.Current.WallJumpInitialVelY);
        // Turn to face the launch direction. A wall-slide leaves Facing pointed at the wall
        // (WallSlidingState), so without this the rig would moonwalk — drift away while still
        // facing the wall — through the airborne jump until it next lands.
        abilities.Facing = dirAwayFromWall;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        vars.TimeInState += ctx.Dt;
        bool jumpHeld = ctx.Input.Space;
        if (!jumpHeld) vars.JumpReleased = true;

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
    public override int ActivePriority => MovementPriorities.DoubleJumpActive;
    public override int PassivePriority => MovementPriorities.DoubleJumpPassive;
    public override MovementCapability RequiredCapabilities => MovementCapability.Jump;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        // No wall check: when the player IS pressing into a wall, WallJumpingState wins outright
        // (its Passive 45 beats DoubleJump's 40). When they're NOT pressing into a wall — e.g.
        // dropping off a platform while holding the away direction — DoubleJump is the right fire.
        return ctx.Intents.Peek(IntentType.Jump, ctx.CurrentFrame, out _, ctx.JumpBufferFrames)
            && !abilities.HasDoubleJumped && !ctx.TryGetGround(out _);
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        return !vars.JumpReleased && vars.TimeInState < MovementConfig.Current.DoubleJumpMaxHoldTime;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        vars.TimeInState = 0f;
        vars.JumpReleased = !ctx.Input.Space;
        ctx.Intents.Consume(IntentType.Jump, ctx.CurrentFrame, ctx.JumpBufferFrames);
        abilities.HasDoubleJumped = true;

        // Kill existing vertical momentum entirely
        ctx.Body.Velocity.Y = MovementConfig.Current.DoubleJumpVelocity;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        vars.TimeInState += ctx.Dt;
        if (!ctx.Input.Space) vars.JumpReleased = true;

        var cfg = MovementConfig.Current;
        var m   = ctx.Modifiers;
        var force = Vector2.Zero;
        force.Y += cfg.DoubleJumpHoldForce;
        if (vars.TimeInState <= ctx.Dt)
            force.Y += cfg.DoubleJumpInitForce;

        force.X = AirControl.Apply(ctx,
            cfg.AirAccel    * m.AirAccel,
            cfg.MaxAirSpeed * m.MaxAirSpeed,
            cfg.AirDrag     * m.AirDrag);

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
    // Scalar per-activation state (OpenDir, SlideSpeed, CoveredPhase, SlideTime,
    // JumpHoldTime, JumpReleased) lives in MovementVars now; only the soft-contact
    // refs stay as transient instance caches (rebuilt by EnsureContacts).
    private SteeringRamp _ramp;
    public override void ResetTransient() { _ramp = null; _ground = null; }
    private FloatingSurfaceDistance _ground;  // held through phase 1: the body keeps its standing
                                              // float height so the ceiling probe (anchored on the
                                              // head) doesn't slip off the overhead slab and fire
                                              // phase 2 prematurely. Removed on the phase-2 transition.

    public override int ActivePriority  => MovementPriorities.CoveredJumpActive;
    public override int PassivePriority => MovementPriorities.CoveredJumpPassive;
    // Like the rest of the jump family, the hitstun/stun lock-out applies — a stunned
    // player under an overhang can't covered-jump out. (Previously missing: the inline
    // BlocksJump gate the other jumps carried was never added here.)
    public override MovementCapability RequiredCapabilities => MovementCapability.Jump;

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
        if (!ctx.Input.Left && !ctx.Input.Right) return false;  // must be pressing a direction 
        // Only relevant for low ceilings (≤ 2 tiles). At 3+ tiles a regular jump fits with margin —
        // JumpingState handles those, and its precondition is the complement of this one.
        if (ground.Position.Y - ceiling.Position.Y > 2 * Chunk.TileSize) return false;
        return TryPickOpenDir(ctx, out _, out _);
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (vars.CoveredPhase == CoveredJumpPhase.SlidingOut)
        {
            if (!ctx.Input.Space) return false;                                          // let go of jump → abort
            if (ctx.Intent.CurrentHorizontal == -vars.OpenDir) return false;             // reversing interrupts cleanly
            return vars.SlideTime < MovementConfig.Current.MaxCoveredSlideTime;          // stuck → bail to Falling
        }
        return !vars.JumpReleased && vars.JumpHoldTime < MovementConfig.Current.MaxJumpHoldTime;  // same as JumpingState
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        TryPickOpenDir(ctx, out vars.OpenDir, out _);
        vars.SlideSpeed = MathF.Max(MathF.Abs(ctx.Body.Velocity.X), MovementConfig.Current.MaxWalkSpeed);
        vars.CoveredPhase = CoveredJumpPhase.SlidingOut;
        vars.SlideTime = 0f;
        EnsureContacts(ctx, ref vars);
        // HasDoubleJumped intentionally left untouched (already false from being grounded) — this jump is "free".
    }

    // Idempotent contact acquisition for the slide-out phase. The ramp's corner is
    // re-derived from vars.OpenDir (same source TryPickOpenDir used), and the ground
    // contact is held through phase 1 — see _ground. No-op once phase 2 (Jumping)
    // has dropped both. Rebuilds after a restore drops the soft contacts.
    private void EnsureContacts(EnvironmentContext ctx, ref MovementVars vars)
    {
        if (vars.CoveredPhase != CoveredJumpPhase.SlidingOut) return;
        if (_ramp == null && CeilingChecker.TryFindExitEdge(ctx.Body, ctx.Chunks, vars.OpenDir, out var corner))
        {
            _ramp = new SteeringRamp { Sense = SteeringSense.Under, ForwardDir = vars.OpenDir, Corner = corner };
            ctx.Body.Constraints.Add(_ramp);
        }
        if (_ground == null && ctx.TryGetGround(out var ground))
        {
            _ground = ground;
            ctx.Body.Constraints.Add(_ground);
        }
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (_ramp   != null) ctx.Body.Constraints.Remove(_ramp);
        if (_ground != null) ctx.Body.Constraints.Remove(_ground);
        _ramp = null;
        _ground = null;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        EnsureContacts(ctx, ref vars);
        var cfg = MovementConfig.Current;

        if (vars.CoveredPhase == CoveredJumpPhase.SlidingOut)
        {
            vars.SlideTime += ctx.Dt;

            // Flip to the jump the instant nothing's overhead.
            if (!ctx.TryGetCeiling(out _))
            {
                if (_ground != null) { ctx.Body.Constraints.Remove(_ground); _ground = null; }
                // Drop the ramp too: with the Clearance shift it can spuriously catch a left-edge
                // vertex that's still 1-2px inside the slab's x-range right after clearing it, and
                // rotate the launch's upward velocity sideways. The slide is over — no more insurance.
                if (_ramp   != null) { ctx.Body.Constraints.Remove(_ramp);   _ramp   = null; }
                // Add (don't overwrite) so a moving floor's vertical velocity carries
                // into the launch — see JumpingState.Enter.
                ctx.Body.Velocity.Y += cfg.JumpVelocity;
                vars.CoveredPhase = CoveredJumpPhase.Jumping;
                vars.JumpHoldTime = 0f;
                vars.JumpReleased = !ctx.Input.Space;
                ctx.Body.AppliedForce = Vector2.Zero;
                return;
            }

            // Keep the ramp anchored on the ceiling's exit edge (it'll go inert on its own once the body's past it).
            if (CeilingChecker.TryFindExitEdge(ctx.Body, ctx.Chunks, vars.OpenDir, out var freshCorner))
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
            float along = vars.OpenDir * ctx.Body.Velocity.X;
            slideForce.X += vars.OpenDir * AirControl.SoftClampVelocity(along, vars.SlideSpeed, cfg.WalkAccel, ctx.Dt);
            ctx.Body.AppliedForce = slideForce;
            return;
        }

        // Phase.Jumping — verbatim ground jump.
        vars.JumpHoldTime += ctx.Dt;
        if (!ctx.Input.Space) vars.JumpReleased = true;

        var force = Vector2.Zero;
        force.Y += cfg.JumpHoldForce;
        if (vars.JumpHoldTime <= ctx.Dt)
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

    private Vector2 _entryPos;         // body pos at Enter — the spatial origin for AnimationProgress
    private float   _vaultProgress;    // [0,1]: body's advance from entry toward the vaulted corner

    // Spatial vault progress for the hands overlay (animation): the body's projected travel from
    // where the maneuver began toward the ledge corner. Input-driven (a held climb advances it),
    // not a clock — exactly what the overlay's clip time needs. 0 for the duck-under-only case.
    public override float AnimationProgress => _vaultProgress;

    // The ledge corner the lead hand grips during a vault (the over-ramp's corner). The animation
    // layer pins a hand to it over the grab window (gated by AnimationProgress) so the hand lands
    // on the actual edge, not an approximate clip pose. Null for a duck-under (no over-ramp/reach).
    public override bool TryAnimationGrip(out Vector2 target)
    {
        if (_overRamp != null) { target = _overRamp.Corner; return true; }
        target = default; return false;
    }

    public override void ResetTransient() { _overRamp = null; _underRamp = null; _vaultProgress = 0f; }
    // _entrySpeed (preserved entry speed) now lives in MovementVars.EntrySpeed.

    public ParkourState(int wallDir) => _wallDir = wallDir;

    public override int ActivePriority  => MovementPriorities.GuidedActive;   // below the jumps ⇒ a jump preempts the maneuver
    public override int PassivePriority => MovementPriorities.GuidedPassive;   // above free air/ground ⇒ triggers automatically

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        // Deliberate hold into the obstacle, from the ground, with a vault-range upper corner OR an
        // overcrop lower corner ahead. The lower-corner case is suppressed when jump was just
        // pressed, so "press jump near an overhang" goes to a jump state, not a duck (interim D2).
        // (An in-progress ledge pull suppresses this vault — see LedgePullState.Suppresses — so
        // the lip reading as ground mid-pull can't steal the maneuver.)
        if (ctx.Intent.HeldHorizontal != _wallDir || !ctx.TryGetGround(out _)) return false;
        return ctx.TryGetExposedCorner(_wallDir, out _)
            || (ctx.TryGetExposedLowerCorner(_wallDir, out _) && !ctx.Intent.JumpJustPressed);
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        // Releasing direction interrupts cleanly (velocity was never snapped, so it just falls through).
        if (ctx.Intent.CurrentHorizontal != _wallDir) return false;
        // Stay alive while a corner is still detected, or a ramp is still doing something — the
        // checker drops the corner a frame or two before the ramp goes inert at the crest/clear point.
        if (ctx.TryGetExposedCorner(_wallDir, out _) || ctx.TryGetExposedLowerCorner(_wallDir, out _)) return true;
        return (_overRamp  != null && _overRamp.Weight  > SteeringRamp.WeightEpsilon)
            || (_underRamp != null && _underRamp.Weight > SteeringRamp.WeightEpsilon);
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        abilities.HasDoubleJumped = false;
        // Preserve whatever horizontal speed the body came in with (floored at walking speed so a
        // from-rest / flush-against-the-obstacle entry still has a target to drive toward).
        vars.EntrySpeed = MathF.Max(MathF.Abs(ctx.Body.Velocity.X), MovementConfig.Current.MaxWalkSpeed);
        _entryPos = ctx.Body.Position;   // spatial origin for the hands-overlay progress
        _vaultProgress = 0f;
        Reconcile(ctx);
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (_overRamp  != null) ctx.Body.Constraints.Remove(_overRamp);
        if (_underRamp != null) ctx.Body.Constraints.Remove(_underRamp);
        _overRamp = _underRamp = null;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        Reconcile(ctx);
        // Spatial vault progress (for the hands overlay): the body's projected travel from the
        // entry point toward the live ledge corner. Over-ramp only (a duck-under has no reach).
        if (_overRamp != null)
        {
            Vector2 toCorner = _overRamp.Corner - _entryPos;
            float denom = toCorner.LengthSquared();
            _vaultProgress = denom < 1e-3f ? 0f
                : Math.Clamp(Vector2.Dot(ctx.Body.Position - _entryPos, toCorner) / denom, 0f, 1f);
        }
        ctx.Body.AppliedForce = Vector2.Zero;   // ParkourState never writes raw force — all routing goes through the ramps.
        if (_overRamp == null && _underRamp == null) return;

        var verts = ctx.Body.Polygon.GetVertices(ctx.Body.Position);
        _overRamp?.Recompute(verts);
        _underRamp?.Recompute(verts);

        // Pick target direction: with exactly one ramp engaged, drive along its implicit surface and
        // scale the target by 1/cos(θ*) so the horizontal component stays near the entry speed even
        // on a steep section. Otherwise (none engaged yet, or both — a "jump and duck" gap) drive
        // straight forward and let the redirect's combine do the steering.
        SteeringRamp solo = null; int engaged = 0;
        if (_overRamp  != null && _overRamp.Weight  > SteeringRamp.WeightEpsilon) { engaged++; solo = _overRamp; }
        if (_underRamp != null && _underRamp.Weight > SteeringRamp.WeightEpsilon) { engaged++; solo = _underRamp; }

        Vector2 dir  = engaged == 1 ? solo.SurfaceDir : new Vector2(_wallDir, 0f);
        float   cosT = engaged == 1 ? MathF.Max(MathF.Cos(solo.ThetaStar), MinClimbCos) : 1f;
        float   targetAlong = vars.EntrySpeed / cosT;
        Vector2 targetVelocity = dir * targetAlong;

        // Write the target onto each ramp so SteeringRamp.ResolveVelocity drives the body
        // toward it (subject to MaxForce·dt clipping). The same target goes on both engaged
        // ramps in the multi-ramp case — averaging in ResolveVelocity then collapses to that
        // target. The anti-gravity term ParkourState used to add explicitly is now absorbed
        // into the drive: each frame the ramp computes dv = target − vBefore, and that
        // delta naturally counteracts whatever gravity·dt was just added to vBefore.
        if (_overRamp  != null)
        {
            _overRamp.HasTarget      = _overRamp.Weight  > SteeringRamp.WeightEpsilon;
            _overRamp.TargetVelocity = targetVelocity;
            _overRamp.MaxSpeed       = float.PositiveInfinity;   // target mode supersedes MaxSpeed
            _overRamp.MaxRedirectVy  = MovementConfig.Current.ParkourRampMaxVy;
            _overRamp.MaxForce       = MovementConfig.Current.ParkourRampMaxForce;
        }
        if (_underRamp != null)
        {
            _underRamp.HasTarget      = _underRamp.Weight > SteeringRamp.WeightEpsilon;
            _underRamp.TargetVelocity = targetVelocity;
            _underRamp.MaxSpeed       = float.PositiveInfinity;
            _underRamp.MaxRedirectVy  = MovementConfig.Current.ParkourRampMaxVy;
            _underRamp.MaxForce       = MovementConfig.Current.ParkourRampMaxForce;
        }
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

    public override void ResetTransient() { _wall = null; _floor = null; }

    public LedgeGrabState(int wallDir) => _wallDir = wallDir;

    public override int ActivePriority  => MovementPriorities.LedgeGrabActive;
    public override int PassivePriority => MovementPriorities.LedgeGrabPassive;
    // Blocked during combat hitstun/stun (Phase 4) — a launch past a ledge can't be
    // cancelled by catching it. The pull (LedgePullState) is entered FROM a grab, so
    // gating the grab already prevents the pull; it carries the flag too for clarity.
    public override MovementCapability RequiredCapabilities => MovementCapability.LedgeGrab;

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
        // Path C: re-grab after an abandoned pull — the pull ended (Up released, or
        // MaxVaultTime ran out) before the body made it over the corner, so the hands
        // are still on the lip. Re-entering the hang lets its spring/damper absorb the
        // pull's velocity through the contact, instead of the body exiting airborne
        // with a jump-sized vy (Plans/LEDGE_PULL_INPUT_MATRIX.md rows D-H, N).
        if (ctx.PreviousState(0) is LedgePullState pull && pull.WallDir == _wallDir
            && !PullCompleted(ctx, abilities))
            return true;
        return false;
    }

    // Same geometry as LedgePullState's completion test: standing height AND past the
    // corner horizontally. A completed pull must exit onto the platform, not re-grab.
    private bool PullCompleted(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        var corner = abilities.GrabbedCorner;
        bool atStandingHeight = ctx.Body.Position.Y < corner.Y - 2f * PlayerCharacter.Radius;
        bool pastCorner       = _wallDir == 1
            ? ctx.Body.Position.X > corner.X
            : ctx.Body.Position.X < corner.X;
        return atStandingHeight && pastCorner;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        // Entry grace (~3 frames): drop-in enters with Down still held, and a re-grab
        // from an abandoned pull needs the damper a few frames to absorb the pull's
        // velocity before an away/Down exit may carry it out of the state.
        if (vars.TimeInState < 0.1f) return true;
        bool pressingAway = (_wallDir == 1 && ctx.Input.Left) || (_wallDir == -1 && ctx.Input.Right);
        if (pressingAway) return false;
        return !ctx.Input.Down;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        vars.TimeInState = 0f;

        // Re-grab (path C) keeps the corner the pull was working on — the checkers
        // can't see it from the risen pose — and keeps the body's velocity: the hang
        // spring/damper dissipates it through the hand contact rather than an
        // impulsive write. Fresh grabs zero velocity (an impulsive catch) and read
        // the corner from the checkers as before.
        bool regrab = ctx.PreviousState(0) is LedgePullState;
        if (!regrab)
        {
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
            abilities.GrabbedCorner = cornerEdge;
        }

        abilities.IsLedgeGrabbing  = true;
        abilities.GrabWallDir      = _wallDir;
        abilities.HasDoubleJumped  = false;

        EnsureContacts(ctx, abilities);
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (_wall  != null) ctx.Body.Constraints.Remove(_wall);
        if (_floor != null) ctx.Body.Constraints.Remove(_floor);
        _wall  = null;
        _floor = null;
        abilities.IsLedgeGrabbing = false;
        abilities.GrabWallDir     = 0;
    }

    // Idempotent pin acquisition, rebuilt from the (snapshotted) GrabbedCorner.
    // Wall pin: detected wall, or derived from corner X when approaching from above.
    // Floor pin: a horizontal plane two radii below the corner. No-op in normal play.
    private void EnsureContacts(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        var cornerEdge = abilities.GrabbedCorner;
        if (_wall == null)
        {
            if (!ctx.TryGetWall(_wallDir, out _wall))
                _wall = new FloatingSurfaceDistance(
                    new Vector2(cornerEdge.X, ctx.Body.Position.Y),
                    new Vector2(-_wallDir, 0f),
                    PlayerCharacter.Radius);
            ctx.Body.Constraints.Add(_wall);
        }
        if (_floor == null)
        {
            _floor = new FloatingSurfaceDistance(
                new Vector2(ctx.Body.Position.X, cornerEdge.Y + 2f * PlayerCharacter.Radius),
                new Vector2(0f, -1f),
                PlayerCharacter.Radius);
            ctx.Body.Constraints.Add(_floor);
        }
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        EnsureContacts(ctx, abilities);
        vars.TimeInState += ctx.Dt;
        var cfg = MovementConfig.Current;
        var corner = abilities.GrabbedCorner;

        // The hang is a 2D anchor — hands gripping a fixed corner — so spring-damp the
        // body toward the hang point on BOTH axes (symmetric: Radius below the corner
        // top, Radius to the body's side of the corner X). The horizontal term is what
        // absorbs a re-grab's retained away-from-wall velocity; without it the body
        // coasts off forever, since the wall pin is one-sided (blocks moving INTO the
        // wall only) and nothing else damps X.
        float hangY = corner.Y + PlayerCharacter.Radius;
        float hangX = corner.X - _wallDir * PlayerCharacter.Radius;

        var force = Vector2.Zero;
        force.X = SpringDampForce(ctx.Body.Position.X - hangX, ctx.Body.Velocity.X, cfg, ctx.Dt);
        force.Y = -cfg.GrabGravityCancel
                + SpringDampForce(ctx.Body.Position.Y - hangY, ctx.Body.Velocity.Y, cfg, ctx.Dt);
        ctx.Body.AppliedForce = force;
    }

    // Spring toward an anchor with a saturated damper. The raw linear damping term
    // (GrabDamping=100 vs 1/dt=30) overshoots per Euler step — harmless while the hang
    // FSDs clamp the body, but a re-grab from an abandoned pull arrives at pull speed
    // and would oscillate divergently. Clamp the damper at the force that exactly zeroes
    // velocity this frame. Same saturated-brake idiom as LedgePull's crest.
    private static float SpringDampForce(float error, float vel, MovementConfig cfg, float dt)
    {
        float damping = -vel * cfg.GrabDamping;
        if (dt > 0f)
        {
            float cancel = -vel / dt;
            if (MathF.Abs(damping) > MathF.Abs(cancel)) damping = cancel;
        }
        return -error * cfg.GrabSpringK + damping;
    }
}

// Executes the pull-up from a ledge grab. Activated by pressing Up while grabbed.
// Releasing Up during the pull interrupts it.
public class LedgePullState : MovementState
{
    private readonly int _wallDir;
    private PointForceContact _spring;
    private FloatingSurfaceDistance _ramp;

    public override void ResetTransient() { _spring = null; _ramp = null; }

    public LedgePullState(int wallDir) => _wallDir = wallDir;

    // Read by LedgeGrabState's re-grab path and LedgeJumpState's preconditions to
    // match the side they're taking over from.
    public int WallDir => _wallDir;

    public override int ActivePriority  => MovementPriorities.LedgePullActive;
    public override int PassivePriority => MovementPriorities.LedgePullPassive;
    public override MovementCapability RequiredCapabilities => MovementCapability.LedgeGrab;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
        => abilities.UpJustPressed
        && abilities.IsLedgeGrabbing
        && abilities.GrabWallDir == _wallDir;

    // The pull owns the corner while it executes — and for the one frame after it exits,
    // since the selection loop calls Suppresses on PreviousState(0). Both rules below
    // previously lived as `PreviousState(0) is LedgePullState` gates inside the candidate
    // states; centralizing them here makes the pull's contract local and complete.
    public override bool Suppresses(MovementState candidate, EnvironmentContext ctx)
    {
        // Once the body rises beside the lip the ledge top reads as ground, so an inward
        // hold satisfies ParkourState's preconditions and its GuidedPassive (45) would
        // steal the maneuver from the pull (43). The pull completes/exits on its own terms
        // and a queued jump routes to LedgeJumpState (LEDGE_PULL_INPUT_MATRIX.md rows B, K).
        if (candidate is ParkourState) return true;
        // Mid-pull, only an *away* press reads as "kick off the wall and bail". An inward
        // press means "jump up onto the ledge" — suppress WallJump so the intent stays
        // queued for LedgeJumpState at the top (row K). Use the candidate's own wall side,
        // exactly as the old in-WallJump gate did.
        if (candidate is WallJumpingState wj)
        {
            bool pressingAway = wj.WallDir == 1 ? ctx.Input.Left : ctx.Input.Right;
            return !pressingAway;
        }
        return false;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (!ctx.Input.Up) return false;
        if (_spring == null) return false;
        if (vars.TimeInState >= MovementConfig.Current.MaxVaultTime) return false;

        float cornerTopY = _spring.Position.Y;
        float cornerX    = _spring.Position.X;
        bool atStandingHeight = ctx.Body.Position.Y < cornerTopY - 2f * PlayerCharacter.Radius;
        bool pastCorner       = _wallDir == 1
            ? ctx.Body.Position.X > cornerX
            : ctx.Body.Position.X < cornerX;
        return !(atStandingHeight && pastCorner);
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        vars.TimeInState = 0f;
        EnsureContacts(ctx, abilities);
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (_spring != null) ctx.Body.Constraints.Remove(_spring);
        if (_ramp   != null) ctx.Body.Constraints.Remove(_ramp);
        _spring = null;
        _ramp   = null;
    }

    // Idempotent acquisition, rebuilt from the (snapshotted) GrabbedCorner. The
    // spring lasts the whole pull; the ramp only applies until the body rises past
    // the corner lip (Update removes it then) — so its rebuild is gated on the same
    // height test, otherwise a restore taken after the ramp was dropped would wrongly
    // re-add it. No-op in normal play.
    private void EnsureContacts(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        var cornerEdge = abilities.GrabbedCorner;
        if (_spring == null)
        {
            _spring = new PointForceContact(cornerEdge);
            ctx.Body.Constraints.Add(_spring);
        }
        bool rampApplies = ctx.Body.Position.Y >= cornerEdge.Y - 2f * PlayerCharacter.Radius;
        if (_ramp == null && rampApplies)
        {
            var rampNormal = new Vector2(-_wallDir * 0.5f, -0.5f);
            _ramp = new FloatingSurfaceDistance(cornerEdge, rampNormal, 1000f);
            ctx.Body.Constraints.Add(_ramp);
        }
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        EnsureContacts(ctx, abilities);
        vars.TimeInState += ctx.Dt;

        // Keep a queued jump alive while committed to the pull: LedgeJumpState
        // consumes it at the natural jump point (standing height beside the lip).
        // The keep-alive stops when the pull ends, so an unconsumed press expires
        // on its normal window (e.g. after a release → re-grab).
        ctx.Intents.Refresh(IntentType.Jump, ctx.CurrentFrame, ctx.JumpBufferFrames);

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

// Jump executed at the top of a ledge pull — the natural jump point where the body
// reaches standing height beside the lip — without completing the over-the-corner
// push or touching ground. Fires from a queued (or fresh) Jump intent that
// LedgePullState keep-alives; anchors to abilities.GrabbedCorner rather than a
// ground probe, because beside the lip there is no reliable ground yet. The launch
// is a JumpServo toward LedgeJumpTargetVy relative to the (static) ledge, so the
// pull's surplus vy is braked down to a normal jump, not stacked onto one
// (Plans/LEDGE_PULL_INPUT_MATRIX.md rows I, J′, K).
public class LedgeJumpState : MovementState
{
    private readonly int _wallDir;

    public LedgeJumpState(int wallDir) => _wallDir = wallDir;

    public override int ActivePriority  => MovementPriorities.LedgeJumpActive;
    public override int PassivePriority => MovementPriorities.LedgeJumpPassive;
    public override MovementCapability RequiredCapabilities => MovementCapability.Jump;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (ctx.PreviousState(0) is not LedgePullState pull || pull.WallDir != _wallDir) return false;
        if (!ctx.Intents.Peek(IntentType.Jump, ctx.CurrentFrame, out _, ctx.JumpBufferFrames)) return false;
        // Natural jump point: the body has risen to standing height beside the lip.
        return ctx.Body.Position.Y <= abilities.GrabbedCorner.Y - 2f * PlayerCharacter.Radius;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
        => !vars.JumpReleased && vars.TimeInState < MovementConfig.Current.MaxJumpHoldTime;

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        vars.TimeInState  = 0f;
        vars.JumpReleased = !ctx.Input.Space;
        ctx.Intents.Consume(IntentType.Jump, ctx.CurrentFrame, ctx.JumpBufferFrames);
        // No velocity write — the servo in Update IS the launch.
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        vars.TimeInState += ctx.Dt;
        if (!ctx.Input.Space) vars.JumpReleased = true;

        var cfg = MovementConfig.Current;
        var m   = ctx.Modifiers;
        var force = Vector2.Zero;
        // Ledge terrain is static — sourceVy = 0.
        force.Y = JumpServo.Force(ctx.Body.Velocity.Y, 0f,
            cfg.LedgeJumpTargetVy, cfg.LedgeJumpServoAccel, cfg.LedgeJumpGravityCancel, ctx.Dt);
        force.X = AirControl.Apply(ctx,
            cfg.AirAccel    * m.AirAccel,
            cfg.MaxAirSpeed * m.MaxAirSpeed,
            cfg.AirDrag     * m.AirDrag);

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
    private SteeringRamp _ramp;
    // DropDir, SlideSpeed, SlideTime, ExitingAirborne now live in MovementVars.

    public override void ResetTransient() => _ramp = null;

    public override int ActivePriority  => MovementPriorities.DropdownActive;
    public override int PassivePriority => MovementPriorities.DropdownPassive;

    // Same pattern as CoveredJumpState.TryPickOpenDir: honor input direction strictly when held,
    // closer edge from a standstill, never flip to the opposite side. Edge from GroundChecker.
    //
    // The IsHangingOver gate (mirrors CoveredJump's IsStickingOut) keeps Dropdown from firing
    // when the body is fully on the platform — the player should crouch in that case, not slip.
    // Only fires once some portion of the body's bounding box has pushed past the drop edge.
    private static bool TryPickDropDir(EnvironmentContext ctx, out int dir, out Vector2 corner)
    {
        int want = ctx.Intent.CurrentHorizontal;
        var bounds = ctx.Body.Bounds;

        if (want != 0)
        {
            if (GroundChecker.TryFindDropEdge(ctx.Body, ctx.Chunks, want, out corner)
                && IsHangingOver(bounds, want, corner.X))
            { dir = want; return true; }
            dir = 0; corner = default; return false;
        }
        bool hasR = GroundChecker.TryFindDropEdge(ctx.Body, ctx.Chunks,  1, out var cR) && IsHangingOver(bounds,  1, cR.X);
        bool hasL = GroundChecker.TryFindDropEdge(ctx.Body, ctx.Chunks, -1, out var cL) && IsHangingOver(bounds, -1, cL.X);
        if (!hasR && !hasL) { dir = 0; corner = default; return false; }
        if (!hasL) { dir =  1; corner = cR; return true; }
        if (!hasR) { dir = -1; corner = cL; return true; }
        float distR = cR.X - ctx.Body.Position.X;
        float distL = ctx.Body.Position.X - cL.X;
        if (distR <= distL) { dir =  1; corner = cR; return true; }
        dir = -1; corner = cL; return true;
    }

    // Some portion of the body's bounding box has crossed the drop edge — i.e. is over
    // empty air rather than over the platform. Mirrors CoveredJumpState.IsStickingOut.
    private static bool IsHangingOver(BoundingBox bounds, int dir, float edgeX)
        => dir == 1 ? bounds.Right > edgeX : bounds.Left < edgeX;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (!ctx.Input.Down) return false;
        if (!ctx.TryGetGround(out _)) return false;
        return TryPickDropDir(ctx, out _, out _);
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (!ctx.Input.Down) return false;
        if (!ctx.TryGetGround(out _)) { vars.ExitingAirborne = true; return false; }   // body's airborne ⇒ Falling takes over
        return vars.SlideTime < MovementConfig.Current.MaxDropdownTime;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        TryPickDropDir(ctx, out vars.DropDir, out _);
        // MaxWalkSpeed is the slide target — fast enough to clear the corner within MaxDropdownTime
        // from a standstill. Running entries keep their momentum since Update only applies force when
        // the body's slower than the target.
        vars.SlideSpeed = MovementConfig.Current.MaxWalkSpeed;
        vars.SlideTime = 0f;
        vars.ExitingAirborne = false;
        EnsureRamp(ctx, vars.DropDir);
        // No FloatingSurfaceDistance: the body's leaving the surface, so don't spring it back up.
        // StandingState/CrouchedState's ground constraint was already removed on their Exit.
    }

    // Idempotent Over-ramp acquisition; corner re-derived from dropDir (same source
    // TryPickDropDir used). No-op in normal play; rebuilds after a restore drops it.
    private void EnsureRamp(EnvironmentContext ctx, int dropDir)
    {
        if (_ramp != null) return;
        if (GroundChecker.TryFindDropEdge(ctx.Body, ctx.Chunks, dropDir, out var corner))
        {
            _ramp = new SteeringRamp { Sense = SteeringSense.Over, ForwardDir = dropDir, Corner = corner };
            ctx.Body.Constraints.Add(_ramp);
        }
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (_ramp != null) ctx.Body.Constraints.Remove(_ramp);
        _ramp = null;
        // Soften the horizontal velocity on the slip-off so the drop lands close to the wall rather
        // than flinging the body forward at the full slide speed. Only apply when we exited via going
        // airborne (not on cancel via !Down or timeout).
        if (vars.ExitingAirborne)
            ctx.Body.Velocity.X *= MovementConfig.Current.DropdownExitVelMult;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        EnsureRamp(ctx, vars.DropDir);
        vars.SlideTime += ctx.Dt;
        var cfg = MovementConfig.Current;

        // Refresh ramp anchor (the corner may shift slightly as the body crosses tile boundaries).
        if (GroundChecker.TryFindDropEdge(ctx.Body, ctx.Chunks, vars.DropDir, out var freshCorner))
            _ramp.Corner = freshCorner;

        // Slide toward the edge, but never brake a faster-than-target body — a running entry should
        // keep its momentum through the slide. Gravity does the vertical work.
        float along = vars.DropDir * ctx.Body.Velocity.X;
        float fx = 0f;
        if (along < vars.SlideSpeed)
            fx = vars.DropDir * AirControl.SoftClampVelocity(along, vars.SlideSpeed, cfg.WalkAccel, ctx.Dt);
        ctx.Body.AppliedForce = new Vector2(fx, 0f);
    }
}
