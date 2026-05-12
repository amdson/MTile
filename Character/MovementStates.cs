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
        if (!abilities.JumpJustPressed || !ctx.TryGetGround(out _)) return false;
        // Fully covered by ceiling → no jump (CeilingJumpState handles the partial case)
        bool fullyCovered = ctx.TryGetCeiling(out _)
            && !ctx.TryGetExposedLowerCorner(1, out _)
            && !ctx.TryGetExposedLowerCorner(-1, out _);
        return !fullyCovered;
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
        if (!abilities.JumpJustPressed || !ctx.TryGetGround(out _)) return false;
        if (Math.Abs(ctx.Body.Velocity.X) < MovementConfig.Current.RunJumpMinSpeed) return false;
        bool fullyCovered = ctx.TryGetCeiling(out _)
            && !ctx.TryGetExposedLowerCorner(1, out _)
            && !ctx.TryGetExposedLowerCorner(-1, out _);
        return !fullyCovered;
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
        return pressingIntoWall && !ctx.TryGetCeiling(out _) && !ctx.TryGetGround(out _) && ctx.TryGetWall(_wallDir, out _);
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        bool pressingIntoWall = (_wallDir == 1 && ctx.Input.Right) || (_wallDir == -1 && ctx.Input.Left);
        return pressingIntoWall && !ctx.TryGetCeiling(out _) && !ctx.TryGetGround(out _) && ctx.TryGetWall(_wallDir, out _);
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
    public override int PassivePriority => 40;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
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
        return abilities.JumpJustPressed && !abilities.HasDoubleJumped && !ctx.TryGetGround(out _) && !ctx.TryGetWall(1, out _) && !ctx.TryGetWall(-1, out _);
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

// Launched when jump is pressed while partially under a ceiling (edge case of under-ledge exit).
// Pushes the player up and away from the overhang like a wall jump.
public class CeilingJumpState : MovementState
{
    public override int ActivePriority  => 52;
    public override int PassivePriority => 32;

    private bool _jumpReleased;
    private float _timeInState;
    private int _openDir;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (!abilities.JumpJustPressed) return false;
        if (!ctx.TryGetCeiling(out _)) return false;
        // Partially under overhang: one side has an exposed lower corner (the overhang edge)
        return ctx.TryGetExposedLowerCorner(1, out _) || ctx.TryGetExposedLowerCorner(-1, out _);
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
        => !_jumpReleased && _timeInState < MovementConfig.Current.WallJumpMaxHoldTime;

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        _timeInState = 0f;
        _jumpReleased = !ctx.Input.Space;
        // Jump away from whichever side has the overhang
        _openDir = ctx.TryGetExposedLowerCorner(1, out _) ? -1 : 1;
        ctx.Body.Velocity = new Vector2(_openDir * MovementConfig.Current.WallJumpInitialVelX,
                                        MovementConfig.Current.WallJumpInitialVelY);
        ctx.Body.Constraints.RemoveAll(c => c is FloatingSurfaceDistance);
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        _timeInState += ctx.Dt;
        if (!ctx.Input.Space) _jumpReleased = true;

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

        ctx.Body.AppliedForce = force;
    }
}

// Handles vault (upper corner), duck-under (lower corner), and overcrop (both simultaneously).
// Plans a Hermite path from current pos/vel to a goal point past the obstacle, then PD-tracks it.
// Phantom corner ramps remain as a safety net against geometric wedging if the controller slips.
public class ParkourState : GuidedState
{
    private readonly int _wallDir;

    public ParkourState(int wallDir) => _wallDir = wallDir;

    public override int ActivePriority  => MovementPriorities.GuidedActive;
    public override int PassivePriority => MovementPriorities.GuidedPassive;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        // Sustained press toward wall, OR moving fast enough into the obstacle to auto-fire
        bool pressingToward = ctx.Intent.HeldHorizontal == _wallDir;
        float autoFireSpeed = MathF.Min(MovementConfig.Current.VaultAutoFireSpeed,
                                        MovementConfig.Current.DuckAutoFireSpeed);
        bool runningInto = ctx.Body.Velocity.X * _wallDir > autoFireSpeed;
        if (!pressingToward && !runningInto) return false;

        bool hasUpper = ctx.TryGetExposedCorner(_wallDir, out _);
        bool hasLower = ctx.TryGetExposedLowerCorner(_wallDir, out _);
        bool onGround = ctx.TryGetGround(out _);

        if (!hasUpper && !hasLower) return false;
        // Vault from air requires explicit Up press; duck always requires ground
        //TODO duck should not always require ground
        if (hasUpper && !onGround && !(pressingToward && ctx.Input.Up)) return false;
        if (hasLower && !onGround) return false;
        return true;
    }

    protected override bool IntentHeld(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        // Releasing direction interrupts cleanly
        return ctx.Intent.CurrentHorizontal == _wallDir;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        // Instantaneous velocity bump at vault entry — gives the move a "pop"
        // and seeds the path's V0 with non-degenerate velocity for at-rest entries.
        var cfg = MovementConfig.Current;
        ctx.Body.Velocity += new Vector2(_wallDir * cfg.VaultKickForward, cfg.VaultKickUp);
        base.Enter(ctx, abilities);
    }

    protected override bool TryPlan(EnvironmentContext ctx, PlayerAbilityState abilities,
                                    out GuidedPath path,
                                    out List<FloatingSurfaceDistance> safetyConstraints)
    {
        path = null;
        safetyConstraints = new List<FloatingSurfaceDistance>();

        bool hasUpper = ctx.TryGetExposedCorner(_wallDir, out var upper);
        bool hasLower = ctx.TryGetExposedLowerCorner(_wallDir, out var lower);
        if (!hasUpper && !hasLower) return false;

        var cfg = MovementConfig.Current;
        float radius = PlayerCharacter.Radius;
        Vector2 startPos = ctx.Body.Position;
        Vector2 startVel = ctx.Body.Velocity;

        // Don't let downward body velocity become the path's initial tangent —
        // a positive V0.Y makes the Hermite curve dip below startPos before
        // climbing to the goal, and the dip can pierce nearby terrain. PD
        // damping reconciles the body's actual Vy in the first few frames.
        if (startVel.Y > 0f) startVel.Y = 0f;

        // Goal pos depends on which corners are present
        Vector2 goalPos;
        if (hasUpper && hasLower)
        {
            // Overcrop — go through the gap, midway between corners
            float gx = (upper.InnerEdge.X + lower.InnerEdge.X) * 0.5f + _wallDir * 2f * radius;
            float gy = (upper.InnerEdge.Y + lower.InnerEdge.Y) * 0.5f;
            goalPos  = new Vector2(gx, gy);
        }
        else if (hasUpper)
        {
            // Vault — above and past the corner
            goalPos = new Vector2(upper.InnerEdge.X + _wallDir * 2f * radius,
                                  upper.InnerEdge.Y - 1.5f * radius);
            // If a ceiling sits above the landing spot (low-clearance corridor with
            // a stalactite over the next floor), keep the body's top below it.
            // Otherwise the path puts body.top inside the stalactite and X-velocity
            // gets clamped on contact.
            ClampApexBelowCeiling(ctx.Chunks, ref goalPos, radius);
        }
        else
        {
            // Duck — below the overhang and past it
            goalPos = new Vector2(lower.InnerEdge.X + _wallDir * 2f * radius,
                                  lower.InnerEdge.Y + radius);
        }
        // Preserve incoming horizontal momentum: body exits the vault at
        // max(|startVel.X|, MaxWalkSpeed) in the vault direction. Capping at
        // MaxWalkSpeed would force the PD to bleed off speed during chained
        // vaults, making the course feel sluggish even at full pace.
        float goalSpeed = MathF.Max(MathF.Abs(startVel.X), cfg.MaxWalkSpeed);
        Vector2 goalVel = new Vector2(_wallDir * goalSpeed, 0f);

        // Insert the intermediate (forced vertical climb) only when the body has
        // already reached the wall — i.e. it can't naturally arc over via momentum.
        // For approaching-with-speed entries, a single-segment Hermite is smoother
        // because the multi-segment plan has a degenerate first segment (chord ≪
        // V·Dur) which makes the PD fight the body's actual velocity.
        float wallTouchX = upper.InnerEdge.X - _wallDir * radius;
        bool atOrPastWall = (startPos.X - wallTouchX) * _wallDir >= 0f;

        if (hasUpper && !hasLower && atOrPastWall)
        {
            // Vault from at-or-past wall: 2-segment path with an intermediate above
            // the corner. Forces the first segment to be a vertical climb so the
            // path can't cut through the wall.
            //
            // Intermediate body position:
            //   X — body's current X if it hasn't reached "wall touch" yet, otherwise
            //       the wall-touch X. Picking max-or-min by wallDir keeps the segment
            //       monotone (never doubles back) and degenerates to a vertical line
            //       when the body is already at the wall.
            //   Y — corner.Y - radius - clearance, so the body's bottom-most extent
            //       sits a small margin above the corner top. Without clearance the
            //       hexagon's edge brushes the corner and chunk collision pins X.
            const float ApexClearance = 5f;
            float midX = (_wallDir == 1)
                ? MathF.Max(startPos.X, wallTouchX)
                : MathF.Min(startPos.X, wallTouchX);
            Vector2 midPos = new Vector2(midX + _wallDir * ApexClearance, upper.InnerEdge.Y - radius - ApexClearance);
            ClampApexBelowCeiling(ctx.Chunks, ref midPos, radius);

            // Velocity at the intermediate carries the body's current horizontal speed
            // forward (no artificial deceleration) but zeros vertical motion (apex).
            // This also keeps segment 1's V0=V1 X-component equal when the body is at
            // the wall (Vx=0), so segment 1 collapses to a true vertical climb.
            Vector2 midVel = new Vector2(startVel.X, 0f);

            float chord1 = (midPos  - startPos).Length();
            float chord2 = (goalPos - midPos).Length();
            float refSpd = cfg.GuidedRefSpeed;
            float speed1 = MathF.Max(MathF.Abs(startVel.X), refSpd);
            float speed2 = MathF.Max(MathF.Abs(midVel.X),   refSpd);
            float dur1   = MathHelper.Clamp(chord1 / speed1, cfg.GuidedMinDuration, cfg.GuidedMaxDuration);
            float dur2   = MathHelper.Clamp(chord2 / speed2, cfg.GuidedMinDuration, cfg.GuidedMaxDuration);

            path = new GuidedPath(new[]
            {
                new GuidedPath.Segment(startPos, startVel, midPos,  midVel,  dur1),
                new GuidedPath.Segment(midPos,   midVel,   goalPos, goalVel, dur2),
            });
        }
        else
        {
            // Overcrop or duck: single segment goes between corners or under the
            // overhang. Wall geometry doesn't block these the way it blocks vaults.
            float chord = (goalPos - startPos).Length();
            float speed = MathF.Max(MathF.Abs(startVel.X), cfg.GuidedRefSpeed);
            float dur   = MathHelper.Clamp(chord / speed, cfg.GuidedMinDuration, cfg.GuidedMaxDuration);
            path = GuidedPath.Plan(startPos, startVel, goalPos, goalVel, dur);
        }

        // Phantom safety ramps near corners. Toggled via MovementConfig so we
        // can compare path-only vs path+ramp behavior in testing.
        if (cfg.ParkourSafetyRamps)
        {
            if (hasUpper)
                safetyConstraints.Add(new FloatingSurfaceDistance(
                    upper.InnerEdge,
                    new Vector2(-_wallDir * 0.5f, -0.5f),
                    1000f));
            if (hasLower)
                safetyConstraints.Add(new FloatingSurfaceDistance(
                    lower.InnerEdge,
                    new Vector2(-_wallDir * MathF.Sqrt(3f) * 0.5f, 0.5f),
                    1000f));
        }

        abilities.HasDoubleJumped = false;
        return true;
    }

    // Pushes apex Y down so the body's top stays below the lowest ceiling tile
    // hanging over the apex. Prevents the vault path from sending body.top into
    // a stalactite in low-clearance corridors. Probes upward over the body's
    // top-edge X-extent (≈ radius/2 either side of apex.X), 4 tiles above.
    private static void ClampApexBelowCeiling(ChunkMap chunks, ref Vector2 apex, float radius)
    {
        float halfTopWidth = radius * 0.5f;
        float left   = apex.X - halfTopWidth;
        float right  = apex.X + halfTopWidth;
        float top    = apex.Y - 4f * Chunk.TileSize;
        float bottom = apex.Y;

        float lowestCeilingBottom = float.MinValue;
        foreach (var tile in TileQuery.SolidTilesInRect(chunks, left, top, right, bottom))
        {
            if (tile.WorldBottom > lowestCeilingBottom) lowestCeilingBottom = tile.WorldBottom;
        }
        if (lowestCeilingBottom == float.MinValue) return;

        // body.Y - radius >= ceilingBottom + 1 → body.Y >= ceilingBottom + radius + 1
        float minY = lowestCeilingBottom + radius + 1f;
        if (apex.Y < minY) apex.Y = minY;
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

