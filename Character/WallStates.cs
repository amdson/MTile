using System;
using Microsoft.Xna.Framework;

namespace MTile;

// The wall family: cling/slide against a vertical face and the kick-off jump.
// Both are direction-flyweights (registered once per wall side). WallSliding
// servos against fixed contacts, so the ambient corrector is Off there.

public class WallSlidingState : MovementState
{
    public override AnimTag AnimationTag => AnimTag.WallSlide;

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

        // Off: owned state servoing against fixed contacts (wall FSD + optional ground
        // FSD) — the ambient layer must never fight it (CONSOLIDATION_PLAN §3.4).
        // Still called so Apply's early-out clears cross-frame anchor state.
        ApplyAmbient(ctx, abilities, ref vars, AmbientPolicy.Off, FoldProfile.None);
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
    public override AnimTag AnimationTag => AnimTag.WallJump;

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

        ApplyAmbient(ctx, abilities, ref vars, AmbientPolicy.Default, FoldProfile.None);
    }
}
