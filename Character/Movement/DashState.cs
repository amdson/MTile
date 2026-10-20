using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Air dash: the Shift+Space chord (IntentType.Dash, buffered DashBufferSeconds) while
// airborne — or in any jump state, so the chord on the ground jumps and dashes one frame
// into the take-off — throws the body along body → cursor. For BurstSeconds it holds
// BurstSpeed with gravity cancelled and the ambient corrector off; then it hands the
// body ExitCarry of that speed and runs ordinary air control through the tail, without
// braking the carry down to the air speed cap. Once per airtime: abilities.HasDashed,
// refreshed by PlayerCharacter on ground / wall slide / ledge hang.
//
// A MOVEMENT state, not an action, so the action FSM runs on top of it: DashSlash (a
// click during the dash) and its end-lag never shorten the burst. Like every movement
// state it ignores attack recovery — end-lag never limits movement. Mid-air the chord
// replaces the double jump rather than spending it (DoubleJumpingState declines while
// Shift is held; Enter here consumes the chord's Jump intent).
//
// Ends on: the clock, touching down after the launch, a registered hit, or a grab
// (GrabbedState's 70 outbids the Active). Hitstop pauses the clock, so a connecting
// DashSlash doesn't eat dash length.
public class DashState : MovementState
{
    private const float BurstSpeed   = 360f;   // px/s along the dash — ≈ 86 px (8 tiles) over the burst
    private const float BurstSeconds = 0.24f;
    private const float Duration     = 0.36f;  // burst + tail (== the Dash clip's Duration)
    // Fraction of BurstSpeed the body keeps when the burst ends; the tail doesn't
    // brake it to the air cap, so it decays by drag.
    private const float ExitCarry    = 0.5f;
    // Ceiling on the upward speed the dash leaves behind — an up-dash is a reach,
    // not a second double jump.
    private const float MaxExitRise  = 120f;
    // A dash fired with the ground probe still hitting (the take-off frames) can't
    // aim below this much rise (sin 15°), or it would skim along the floor and end.
    private const float MinTakeoffRise = 0.26f;

    public override int ActivePriority  => MovementPriorities.DashActive;
    public override int PassivePriority => MovementPriorities.DashPassive;
    // Hitstun / stun / grabbed block entry (CombatState.BlockedCapabilities).
    public override MovementCapability RequiredCapabilities => MovementCapability.Jump;
    public override AnimTag AnimationTag => AnimTag.Dash;

    // Airborne as the Shift+Space chord sees it: no footing, or a jump state (or the
    // dash itself) is driving the body even if the probe still sees the floor it is
    // leaving. `current` is the movement state in charge — PreviousState(0) during
    // movement selection, ctx.CurrentMovement from the action side (Guard).
    public static bool Airborne(EnvironmentContext ctx, MovementState current)
        => LaunchDriving(current)
        || !(ctx.TryGetGround(out _) || current?.GroundedForActions == true);

    private static bool LaunchDriving(MovementState s)
        => s is DashState or JumpingState or RunningJumpState or DoubleJumpingState
                or WallJumpingState or CoveredJumpState or LedgeJumpState;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (abilities.HasDashed) return false;
        if (!ctx.Intents.Peek(IntentType.Dash, ctx.CurrentFrame, out _, ctx.DashBufferFrames)) return false;
        return Airborne(ctx, ctx.PreviousState(0));
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (vars.TimeInState >= Duration) return false;
        if (abilities.Combat.HitstunActive || abilities.Combat.StunActive) return false;
        // Touching down after the launch ends it — a down-dash into the floor shouldn't
        // keep pressing the body into it.
        if (vars.TimeInState > 2f * ctx.Dt && ctx.TryGetGround(out _) && ctx.Body.Velocity.Y >= 0f)
            return false;
        return true;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        vars.TimeInState = 0f;
        ctx.Intents.Consume(IntentType.Dash, ctx.CurrentFrame, ctx.DashBufferFrames);
        // The chord's Space press belongs to the dash: a ground take-off already spent
        // it on the jump; mid-air it must not linger and double-jump (or wall-jump)
        // once Shift comes up.
        ctx.Intents.Consume(IntentType.Jump, ctx.CurrentFrame, ctx.JumpBufferFrames);
        abilities.HasDashed = true;

        int facing = abilities.Facing == 0 ? 1 : abilities.Facing;
        Vector2 raw = ctx.Input.MouseWorldPosition - ctx.Body.Position;
        Vector2 dir = raw.LengthSquared() > 1e-6f ? Vector2.Normalize(raw) : new Vector2(facing, 0f);
        if (ctx.TryGetGround(out _) && dir.Y > -MinTakeoffRise)
        {
            float x = MathF.Sqrt(1f - MinTakeoffRise * MinTakeoffRise);
            dir = new Vector2(dir.X >= 0f ? x : -x, -MinTakeoffRise);
        }
        if (MathF.Abs(dir.X) > 0.1f) abilities.Facing = dir.X > 0f ? 1 : -1;
        vars.DashDir = dir;
    }

    private static Vector2 ExitVelocity(Vector2 dir)
    {
        var v = dir * (BurstSpeed * ExitCarry);
        if (v.Y < -MaxExitRise) v.Y = -MaxExitRise;
        return v;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        float t = vars.TimeInState;
        // Hitstop freezes the dash with the body (PreStep parks it); the clock waits too.
        if (!abilities.Combat.HitstopActive) vars.TimeInState += ctx.Dt;

        if (t < BurstSeconds)
        {
            // Hold the dash velocity outright and cancel gravity: nothing else steers
            // the body during the burst, the corrector included.
            ctx.Body.Velocity     = vars.DashDir * BurstSpeed;
            ctx.Body.AppliedForce = -ctx.Gravity;
            ApplyAmbient(ctx, abilities, ref vars, AmbientPolicy.Off, FoldProfile.None);
            return;
        }
        if (t - BurstSeconds < ctx.Dt) ctx.Body.Velocity = ExitVelocity(vars.DashDir);

        // Tail: Falling's air control, with the cap raised to the carry so it isn't
        // braked away in a frame.
        var cfg = MovementConfig.Current;
        var m   = ctx.Modifiers;
        float cap = MathF.Max(cfg.MaxAirSpeed * m.MaxAirSpeed, MathF.Abs(ctx.Body.Velocity.X));
        ctx.Body.AppliedForce = new Vector2(
            AirControl.Apply(ctx, cfg.AirAccel * m.AirAccel, cap, cfg.AirDrag * m.AirDrag), 0f);
        ApplyAmbient(ctx, abilities, ref vars, AmbientPolicy.Default,
            OnLattice ? FoldProfile.Fall : FoldProfile.Stand);
    }
}
