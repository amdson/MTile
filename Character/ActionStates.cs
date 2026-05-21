using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// Concurrent second FSM, owned by PlayerCharacter alongside the MovementState FSM.
// Same shape (preconditions, conditions, Enter/Exit/Update, priority-based selection),
// separate registry, separate history.
//
// Coupling rule: actions may read movement (ctx.Body, ctx.TryGet*, ctx.PreviousState)
// but movement code MUST NOT read action state. Enforced by convention.
public abstract class ActionState
{
    public abstract int ActivePriority  { get; }
    public abstract int PassivePriority { get; }

    // CheckPreConditions (candidate selection) reads only ctx + abilities, never the
    // current activation's vars — so it keeps the lean signature. The lifecycle methods
    // below run on the active/transitioning action and carry ActionVars, the plain-data
    // per-activation state (see ActionVars). Read-only hooks take it by `in`.
    public abstract bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities);
    public abstract bool CheckConditions  (EnvironmentContext ctx, PlayerAbilityState abilities, ref ActionVars vars);

    public virtual void Enter(EnvironmentContext ctx, PlayerAbilityState abilities, ref ActionVars vars) {}
    public virtual void Exit (EnvironmentContext ctx, PlayerAbilityState abilities, ref ActionVars vars) {}

    public abstract void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref ActionVars vars);

    public virtual void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body, in ActionVars vars) {}

    // Declare multiplicative scalars on movement knobs (walk speed, friction, …).
    // Called by PlayerCharacter once per frame between action selection and
    // Movement.Update — the values go into ctx.Modifiers, movement reads them
    // at its config sites. Default no-op = identity, no effect on physics.
    public virtual void ApplyMovementModifiers(ref MovementModifiers m, in ActionVars vars) { }

    // Augment the player body's physics directly: add to AppliedForce for a
    // sustained push, or write Velocity for an impulse / "ensure-at-least" assist.
    // Called by PlayerCharacter AFTER Movement.Update has written its force for
    // the frame and BEFORE Action.Update — so action-driven forces stack on top
    // of, not in competition with, the movement-driven force. Default no-op.
    public virtual void ApplyActionForces(EnvironmentContext ctx, in ActionVars vars) { }
}

// Always-on fallback. Mirrors FallingState's role in the movement FSM.
public class NullAction : ActionState
{
    public override int ActivePriority  => 0;
    public override int PassivePriority => 0;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab) => true;
    public override bool CheckConditions  (EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars) => true;
    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars) {}
}

// Wind-up state. Entered on LMB-press edge; held while LMB is down. Doesn't commit
// to any specific move — Slash/Stab/etc. preempt it on their own preconditions.
// Visual is a small pulsing indicator at the body, colored by posture.
public class ReadyAction : ActionState
{
    private const float MaxHold = 1.0f;   // hard cap so a stuck button doesn't lock us forever
    public override int ActivePriority  => 10;
    public override int PassivePriority => 15;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (ab.Condition.RecoveryActive) return false;
        return ctx.Intents.Peek(IntentType.PressEdge, ctx.CurrentFrame, out _);
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        // Stay alive while LMB held, up to MaxHold. Release exits via Click/Stab preempt
        // (their preconditions fire as the release-edge intent appears) OR via Null fallback.
        if (!ctx.Input.LeftClick) return false;
        return vars.TimeInState < MaxHold;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState = 0f;
        vars.IsGrounded  = ctx.TryGetGround(out _);
        vars.Facing      = ab.Facing == 0 ? 1 : ab.Facing;
        ctx.Intents.Consume(IntentType.PressEdge, ctx.CurrentFrame);
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState += ctx.Dt;
    }

    // Light slowdown while charging — telegraphs commitment. Slashes flick through
    // Ready in 1–2 frames so the dip is imperceptible; a long-held stab charge
    // lingers and feels heavy. The GravityScale dip pairs with the horizontal
    // clamp to give a "floaty hover while you wind up" feel in the air; on the
    // ground the standing spring overrides gravity, so the scale is a no-op.
    public override void ApplyMovementModifiers(ref MovementModifiers m, in ActionVars vars)
    {
        m.MaxWalkSpeed   *= 0.6f;
        m.WalkAccel      *= 0.7f;
        m.GroundFriction *= 1.3f;
        m.MaxAirSpeed    *= 0.7f;
        m.GravityScale   *= 0.3f;
    }

    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body, in ActionVars vars)
    {
        // Pulsing dot offset slightly toward facing, color matches posture
        const float ArcR = PlayerCharacter.Radius * 1.5f;
        float pulse  = MathF.Sin(vars.TimeInState * MathF.PI * 4f) * 0.5f + 0.5f;
        float offset = ArcR * 0.5f * pulse;
        var pos = body.Position + new Vector2(vars.Facing * offset, 0f);
        var color = (vars.IsGrounded ? Color.Red : Color.DeepSkyBlue) * 0.7f;
        sb.Draw(pixel, new Rectangle((int)pos.X - 2, (int)pos.Y - 2, 3, 3), color);
    }
}

// Post-attack lockout. Owns the RecoveryActive flag; high active priority so most
// moves can't interrupt it. Combo moves (Slash2/3, AirSlash2) preempt via higher
// passive priority + their combo-flag gates.
public class RecoveryAction : ActionState
{
    public override int ActivePriority  => 40;
    public override int PassivePriority => 45;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
        => ab.Condition.RecoveryActive;

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
        => ab.Condition.RecoveryActive;

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars) {}
}

// Shared base for slash-shaped moves. Subclasses configure arc shape, color, posture
// requirements, and what combo flag they set on exit. The base handles: trigger via
// Click intent, lifetime, trail buffer, hurtbox publishing, the arc math, and Draw.
//
// Arc parametrization:
//   outwardFactor = sin(π t)                          0 → 1 → 0 (radial out-and-back)
//   angle         = (SweepAngleDeg/2) · (1 - 2t) · SweepDirection
//                                                    +half → 0 → -half through 0 at t=0.5
// The dot rotates `_slashDir` by `angle` and scales by ArcRadius · outwardFactor.
// Apex (max extent) is along `_slashDir` at t=0.5 — that's where the hurtbox sits.
public abstract class SlashLikeAction : ActionState
{
    // Damage window expressed as fractions of Duration so the window scales when
    // the slash duration is tuned. Window covers ~20%–70% of the slash, with the
    // hitbox apex (max radial extent) sitting at the 50% mark.
    private const float HurtboxStartFraction  = 0.20f;
    private const float HurtboxActiveFraction = 0.50f;
    // Per-frame damage tuned so 2 frames of active window at 30 fps total ≈ TileMaxHP.
    // (Slashes now fire fast enough that the active window is ~2 frames, not 4.)
    private const float SlashDamagePerFrame   = TileDamage.TileMaxHP / 2f;
    // Hitbox scale bump per roadmap §1.7 (1.75× — was 1.0×). Combat felt
    // unrewarding with apex-only hitboxes that just barely covered the dot's
    // visible reach; widening makes near-misses rarer without changing the
    // arc shape or apex position. Per-variant ArcRadiusScale still stacks on
    // top of this, so AirSlash1 (0.9×) and GroundSlash3 (1.3×) still differ.
    private const float BaseArcRadius         = PlayerCharacter.Radius * 1.5f * 1.75f;
    // Cosmetic trail behind the apex dot. Lifetime ≈ a few frames so the ribbon
    // reads as motion blur, not afterimage.
    private const int   TrailCapacity         = 8;
    private const float TrailLifetime         = 0.12f;


    // ----- per-variant knobs ------------------------------------------------
    protected abstract float   Duration            { get; }   // seconds
    protected abstract float   ArcRadiusScale      { get; }   // multiplier on BaseArcRadius
    protected abstract float   SweepAngleDeg       { get; }   // total sweep (90, 150, …)
    protected abstract float   SweepDirection      { get; }   // +1 CCW, -1 CW (mirror)
    protected abstract float   KnockbackMagnitude  { get; }
    protected abstract Color   SlashColor          { get; }
    protected abstract bool    RequireGround       { get; }
    protected abstract bool    RequireAir          { get; }
    // Override to gate on combo flags (Slash2 → cond.Slash2Ready).
    protected virtual  bool    CombosOk(ConditionState cond) => true;
    // Override to clear the flag we just used + the recovery flag.
    protected virtual  void    OnEnterClearFlags(ConditionState cond) { }
    // Override to set the next-stage flag + recovery duration.
    protected abstract void    OnExitSetFlags(ConditionState cond, int currentFrame);
    // AirTurnSlash overrides this to true so a click behind the player gives a
    // genuine backward slash instead of a perpendicular one (roadmap §1.6).
    protected virtual  bool    AllowBackward       => false;
    // -----------------------------------------------------------------------

    protected float ArcRadius => BaseArcRadius * ArcRadiusScale;

    private readonly Trail _trail = new(TrailCapacity, TrailLifetime);

    public override int ActivePriority  => 30;
    public override int PassivePriority => 30;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (!ctx.Intents.Peek(IntentType.Click, ctx.CurrentFrame, out _)) return false;
        // Stun gate: a stunned player can't initiate slashes. Guard is the
        // intended escape (it can fire during stun); see roadmap §1.5.
        if (ctx.Combat?.BlocksAttack == true) return false;
        bool grounded = ctx.TryGetGround(out _);
        if (RequireGround && !grounded) return false;
        if (RequireAir    &&  grounded) return false;
        if (!CombosOk(ab.Condition)) return false;
        return true;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
        => vars.TimeInState < Duration;

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState = 0f;
        vars.SlashDir    = ComputeSlashDir(ctx, ab);
        vars.HitId       = ctx.HitIds.Next();
        _trail.Clear();
        ctx.Intents.Consume(IntentType.Click, ctx.CurrentFrame);
        OnEnterClearFlags(ab.Condition);
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
        => OnExitSetFlags(ab.Condition, ctx.CurrentFrame);

    // Mouse-to-body direction, hemisphere-clamped (unless AllowBackward) so a
    // click behind the player produces a perpendicular slash rather than a
    // backward one. Degenerate inputs fall back to (Facing, 0).
    private Vector2 ComputeSlashDir(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        int facing = ab.Facing == 0 ? 1 : ab.Facing;
        Vector2 raw = ctx.Input.MouseWorldPosition - ctx.Body.Position;
        if (!AllowBackward && raw.X * facing < 0f) raw.X = 0f;
        if (raw.LengthSquared() < 1e-4f) return new Vector2(facing, 0f);
        return Vector2.Normalize(raw);
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState += ctx.Dt;
        var dot = ComputeDotPosition(ctx.Body.Position, in vars);
        _trail.Tick(ctx.Dt);
        _trail.Push(dot);

        float windowStart = Duration * HurtboxStartFraction;
        float windowEnd   = windowStart + Duration * HurtboxActiveFraction;
        if (vars.TimeInState >= windowStart && vars.TimeInState <= windowEnd && ctx.Hitboxes != null)
        {
            var apex = ctx.Body.Position + vars.SlashDir * ArcRadius;
            var region = new BoundingBox(
                apex.X - ArcRadius * 0.5f, apex.Y - ArcRadius * 0.5f,
                apex.X + ArcRadius * 0.5f, apex.Y + ArcRadius * 0.5f);
            ctx.Hitboxes.Publish(new Hitbox(
                region, vars.HitId, SlashDamagePerFrame,
                vars.SlashDir * KnockbackMagnitude,
                ctx.Faction, this, SlashColor));
        }
    }

    private Vector2 ComputeDotPosition(Vector2 anchor, in ActionVars vars)
    {
        float t = MathHelper.Clamp(vars.TimeInState / Duration, 0f, 1f);
        float outF       = MathF.Sin(MathF.PI * t);
        float halfSweep  = SweepAngleDeg * 0.5f * MathF.PI / 180f;
        float angle      = halfSweep * (1f - 2f * t) * SweepDirection;
        float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
        Vector2 dir = new Vector2(
            vars.SlashDir.X * cos - vars.SlashDir.Y * sin,
            vars.SlashDir.X * sin + vars.SlashDir.Y * cos);
        return anchor + dir * (ArcRadius * outF);
    }

    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body, in ActionVars vars)
    {
        // Ribbon trails the apex dot — thick + saturated near the newest sample,
        // thinning and fading toward the tail.
        _trail.Draw(sb, pixel, SlashColor, SlashColor * 0f, 4f);
    }
}

// ---------- Ground combo: S1 → S2 → S3 -----------------------------------------

// Opening ground slash. Wide CCW sweep, red.
public class GroundSlash1 : SlashLikeAction
{
    // Slashes are fast — Duration tuned so the active damage window is ~2 frames at 30 fps.
    // Variants scale around this baseline for combo-feel variety.
    protected override float Duration            => 0.14f;
    protected override float ArcRadiusScale      => 1.0f;
    protected override float SweepAngleDeg       => 100f;
    protected override float SweepDirection      => +1f;
    protected override float KnockbackMagnitude  => 200f;
    protected override Color SlashColor          => Color.Red;
    protected override bool  RequireGround       => true;
    protected override bool  RequireAir          => false;
    protected override void OnExitSetFlags(ConditionState c, int f)
    {
        ConditionState.SetFor(ref c.Slash2Ready,    ref c.Slash2ExpireFrame,    30, f);
        ConditionState.SetFor(ref c.RecoveryActive, ref c.RecoveryExpireFrame,  3,  f);
    }
}

// Combo step 2 — mirror-handedness sweep, slightly faster, slightly harder hit.
public class GroundSlash2 : SlashLikeAction
{
    protected override float Duration            => 0.13f;
    protected override float ArcRadiusScale      => 1.05f;
    protected override float SweepAngleDeg       => 110f;
    protected override float SweepDirection      => -1f;
    protected override float KnockbackMagnitude  => 260f;
    protected override Color SlashColor          => Color.Red;
    protected override bool  RequireGround       => true;
    protected override bool  RequireAir          => false;

    // Combo moves preempt Recovery via higher passive priority.
    public override int PassivePriority => 50;

    protected override bool CombosOk(ConditionState c) => c.Slash2Ready;
    protected override void OnEnterClearFlags(ConditionState c)
    {
        c.Slash2Ready    = false;
        c.RecoveryActive = false;
    }
    protected override void OnExitSetFlags(ConditionState c, int f)
    {
        ConditionState.SetFor(ref c.Slash3Ready,    ref c.Slash3ExpireFrame,    30, f);
        ConditionState.SetFor(ref c.RecoveryActive, ref c.RecoveryExpireFrame,  3,  f);
    }
}

// Combo finisher — wide 160° CCW sweep, longer reach, hot color, big knockback.
public class GroundSlash3 : SlashLikeAction
{
    protected override float Duration            => 0.18f;
    protected override float ArcRadiusScale      => 1.30f;
    protected override float SweepAngleDeg       => 160f;
    protected override float SweepDirection      => +1f;
    protected override float KnockbackMagnitude  => 380f;
    protected override Color SlashColor          => Color.OrangeRed;
    protected override bool  RequireGround       => true;
    protected override bool  RequireAir          => false;

    public override int PassivePriority => 50;

    protected override bool CombosOk(ConditionState c) => c.Slash3Ready;
    protected override void OnEnterClearFlags(ConditionState c)
    {
        c.Slash3Ready    = false;
        c.RecoveryActive = false;
    }
    protected override void OnExitSetFlags(ConditionState c, int f)
    {
        // End of chain — no further combo flag.
        ConditionState.SetFor(ref c.RecoveryActive, ref c.RecoveryExpireFrame, 5, f);
    }
}

// Crouch slash — only fires from CrouchedState. Longer reach than a stand slash,
// no combo chain (deliberately a one-and-done from a low stance). Preempts
// GroundSlash1 via higher passive priority when crouched; precondition fails
// when not crouched so the regular slash takes over.
public class CrouchSlash : SlashLikeAction
{
    protected override float Duration            => 0.16f;
    protected override float ArcRadiusScale      => 1.45f;
    protected override float SweepAngleDeg       => 90f;
    protected override float SweepDirection      => +1f;
    protected override float KnockbackMagnitude  => 240f;
    protected override Color SlashColor          => Color.Goldenrod;
    protected override bool  RequireGround       => true;
    protected override bool  RequireAir          => false;

    // Beats GroundSlash1 (30/30) on ties without out-prioritizing Slash2/3 combos (50).
    public override int PassivePriority => 32;

    protected override bool CombosOk(ConditionState c) => true;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        // Standard slash gating PLUS the player must be in CrouchedState. The
        // base class doesn't expose CheckPreConditions in a way we can extend
        // cleanly, so we duplicate its check and add the crouch requirement.
        if (!ctx.Intents.Peek(IntentType.Click, ctx.CurrentFrame, out _)) return false;
        if (ctx.Combat?.BlocksAttack == true) return false;
        if (!ctx.TryGetGround(out _)) return false;
        if (ctx.PreviousState(0) is not CrouchedState) return false;
        return true;
    }

    // No combo flag set on exit — crouch slash terminates the chain.
    protected override void OnExitSetFlags(ConditionState c, int f)
    {
        ConditionState.SetFor(ref c.RecoveryActive, ref c.RecoveryExpireFrame, 5, f);
    }
}

// ---------- Air combo: AS1 → AS2 -----------------------------------------------

// Opening air slash. Tighter & faster than ground S1, blue.
public class AirSlash1 : SlashLikeAction
{
    protected override float Duration            => 0.12f;
    protected override float ArcRadiusScale      => 0.90f;
    protected override float SweepAngleDeg       => 110f;
    protected override float SweepDirection      => +1f;
    protected override float KnockbackMagnitude  => 180f;
    protected override Color SlashColor          => Color.DeepSkyBlue;
    protected override bool  RequireGround       => false;
    protected override bool  RequireAir          => true;
    protected override void OnExitSetFlags(ConditionState c, int f)
    {
        ConditionState.SetFor(ref c.AirSlash2Ready, ref c.AirSlash2ExpireFrame, 30, f);
        ConditionState.SetFor(ref c.RecoveryActive, ref c.RecoveryExpireFrame,  3,  f);
    }
}

// Air combo finisher — bigger CW sweep, more knockback.
public class AirSlash2 : SlashLikeAction
{
    protected override float Duration            => 0.14f;
    protected override float ArcRadiusScale      => 1.10f;
    protected override float SweepAngleDeg       => 140f;
    protected override float SweepDirection      => -1f;
    protected override float KnockbackMagnitude  => 280f;
    protected override Color SlashColor          => Color.DeepSkyBlue;
    protected override bool  RequireGround       => false;
    protected override bool  RequireAir          => true;

    public override int PassivePriority => 50;

    protected override bool CombosOk(ConditionState c) => c.AirSlash2Ready;
    protected override void OnEnterClearFlags(ConditionState c)
    {
        c.AirSlash2Ready = false;
        c.RecoveryActive = false;
    }
    protected override void OnExitSetFlags(ConditionState c, int f)
    {
        ConditionState.SetFor(ref c.RecoveryActive, ref c.RecoveryExpireFrame, 4, f);
    }
}

// Air turn-around slash. Roadmap §1.6: clicking on the opposite side of facing
// in air fires a fast, narrow, long-reach slash AND flips Facing in air (the
// only mechanism that does so, since PlayerCharacter.Update no longer writes
// Facing in air). Higher passive priority than AirSlash1 so a backward-click
// in air picks this instead of being clamped to perpendicular AirSlash1.
public class AirTurnSlash : SlashLikeAction
{
    protected override float Duration            => 0.11f;
    protected override float ArcRadiusScale      => 1.40f;   // long reach
    protected override float SweepAngleDeg       => 60f;     // narrow
    protected override float SweepDirection      => +1f;
    protected override float KnockbackMagnitude  => 240f;
    protected override Color SlashColor          => Color.Violet;
    protected override bool  RequireGround       => false;
    protected override bool  RequireAir          => true;
    protected override bool  AllowBackward       => true;

    // Beat AirSlash1 (30/30) when both could fire; AirSlash2 combo (50) still wins.
    public override int PassivePriority => 35;

    // Mouse must be on the side opposite Facing for the turn-around to make
    // sense. Without this, this state would just steal every air-click.
    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (!base.CheckPreConditions(ctx, ab)) return false;
        int facing = ab.Facing == 0 ? 1 : ab.Facing;
        float dx = ctx.Input.MouseWorldPosition.X - ctx.Body.Position.X;
        return dx * facing < 0f;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        // Flip facing FIRST so the base class's ComputeSlashDir reads the new
        // facing when hemisphere-clamping (well, it doesn't clamp since
        // AllowBackward=true, but the fallback (Facing, 0) at degenerate input
        // still points the right way).
        ab.Facing = -(ab.Facing == 0 ? 1 : ab.Facing);
        base.Enter(ctx, ab, ref vars);
    }

    protected override void OnExitSetFlags(ConditionState c, int f)
    {
        // No combo follow-up — turn-around is one-and-done. Short recovery.
        ConditionState.SetFor(ref c.RecoveryActive, ref c.RecoveryExpireFrame, 4, f);
    }
}

// ---------- Stab — long-hold + swipe gesture -----------------------------------

// Linear thrust along the captured swipe direction. Longer duration, longer recovery,
// more knockback than a slash; no combo chain (can't immediately roll into another move).
public class StabAction : ActionState
{
    private const float Duration              = 0.60f;
    // Active window timed to the strike phase of the visual curve (Draw): the box
    // opens just as the tip starts whipping forward out of the wind-up, and stays
    // alive through the snap into the hold. In normalized state-time that's
    // ≈ 0.20–0.50 of Duration, matching WindupEnd → mid-hold.
    private const float HurtboxStartTime      = 0.12f;
    private const float HurtboxActiveDuration = 0.18f;

    // Lunge window: a short forward-glide phase AFTER the hitbox active window
    // (0.25–0.40) and BEFORE the settle (0.55–0.60). During this window the
    // ground-friction modifier dips so the velocity assist below can actually
    // translate the body — outside it, friction is back up to sell the plant.
    private const float LungeStart   = 0.10f;
    private const float LungeEnd     = 0.4f;
    private const float LungeSpeed   = 90f;     // px/s horizontal target during lunge

    // Roadmap §1.7 hitbox bump (1.75×). Reach + half-width both grow so the
    // stab feels longer AND wider, not just longer-thin. BlockReach/HalfWidth
    // below get the same scale.
    private const float Reach                 = PlayerCharacter.Radius * 3.3f  * 1.75f;
    private const float PrimaryHalfWidth      = PlayerCharacter.Radius * 0.55f * 1.75f;
    // Soft mid-attack steering. The captured _stabDir rotates toward the current
    // mouse direction at up to MaxSteerSpeed rad/s, with the total deviation from
    // the initial swipe angle capped at MaxTotalSteer. Lets the player adjust the
    // angle slightly during the wind-up + active window without making stab feel
    // like a homing missile.
    private const float MaxSteerSpeed = 1.8f;     // rad/s
    private const float MaxTotalSteer = 0.55f;    // rad (~31°)
    // Tile-shockwave reach — significantly longer + wider than the entity-hitbox.
    // Lets stab dig through several tiles at once without hitting balloons/enemies
    // beyond its visible thrust extent.
    private const float BlockReach            = PlayerCharacter.Radius * 6.0f * 1.75f;
    private const float BlockHalfWidth        = PlayerCharacter.Radius * 0.9f * 1.75f;
    private const float KnockbackMagnitude    = 380f;
    private const float DamagePerFrame        = TileDamage.TileMaxHP / 4f;

    // Air-stab dive boost. Velocity projected onto _stabDir at the moment of commit
    // maps via clamp + lerp to a scalar in [MinBoost, MaxBoost]. Applied to damage
    // on both hitboxes and to the tile-shockwave box's dimensions (length × boost,
    // width × √boost so the box doesn't grow disproportionately wide). Ground stab
    // always reads 1×.
    private const float MinBoost            = 1.0f;
    private const float MaxBoost            = 2.5f;
    // velAlongStab at which the boost saturates. A clean downward dive easily
    // reaches ~400 px/s (terminal-ish fast-fall); a casual mid-air stab sees
    // 50–100 px/s and stays near baseline.
    private const float BoostReferenceSpeed = 400f;


    // Static rectangle polygons in local space — long axis along +X. Rotation applied
    // at publish time via Polygon.GetVertices(pos, rotation) so the actual hit shape
    // tracks _stabDir, not just the loose AABB.
    private static readonly Polygon PrimaryPoly = Polygon.CreateRectangle(Reach,      PrimaryHalfWidth * 2f);
    private static readonly Polygon BlockPoly   = Polygon.CreateRectangle(BlockReach, BlockHalfWidth   * 2f);

    // Tip ribbon — short lifetime so the trail snaps with the strike rather than
    // lingering past the retract. Render-only; not part of ActionVars.
    private readonly Trail _tipTrail = new(capacity: 10, lifetime: 0.14f);

    public override int ActivePriority  => 30;
    public override int PassivePriority => 30;

    private static Color ColorFor(bool isGrounded) => isGrounded ? Color.Goldenrod : Color.MediumPurple;

    // AirSpinStab overrides true so the swipe (and mid-attack mouse-steer clamp)
    // can point backward relative to Facing. Default Stab still clamps to front.
    protected virtual bool AllowBackward => false;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (!ctx.Intents.Peek(IntentType.Stab, ctx.CurrentFrame, out _)) return false;
        if (ctx.Combat?.BlocksAttack == true) return false;
        // Shift+LMB-hold-swipe is reserved for BeamAction. A Stab intent
        // emitted from a Shift-held press would otherwise route to a normal
        // stab on release; gate it off so the beam path doesn't double-fire.
        // AirSpinStab overrides this by checking its own preconditions.
        if (ctx.Input.Shift) return false;
        return true;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
        => vars.TimeInState < Duration;

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState = 0f;
        vars.IsGrounded  = ctx.TryGetGround(out _);
        vars.HitId       = ctx.HitIds.Next();
        _tipTrail.Clear();

        // Capture swipe direction from the intent; hemisphere-clamp like slash
        // (unless AllowBackward — AirSpinStab keeps backward swipes intact).
        int facing = ab.Facing == 0 ? 1 : ab.Facing;
        if (ctx.Intents.Peek(IntentType.Stab, ctx.CurrentFrame, out var intent))
        {
            var raw = intent.Direction;
            if (!AllowBackward && raw.X * facing < 0f) raw.X = 0f;
            if (raw.LengthSquared() < 1e-4f) raw = new Vector2(facing, 0f);
            vars.StabDir = Vector2.Normalize(raw);
            ctx.Intents.Consume(IntentType.Stab, ctx.CurrentFrame);
        }
        else
        {
            vars.StabDir = new Vector2(facing, 0f);
        }
        vars.InitialStabAngle = MathF.Atan2(vars.StabDir.Y, vars.StabDir.X);

        // Air-stab dive boost: project velocity onto the captured stab direction
        // at the instant of commit. Ground stab + negative projection (velocity
        // opposite to stab) collapse to MinBoost; high-speed aligned dives saturate
        // at MaxBoost. Length × boost, width × √boost so the tile box gets visibly
        // longer without becoming a giant rectangle.
        if (vars.IsGrounded)
        {
            vars.Boost      = 1f;
            vars.BlockPoly  = BlockPoly;
            vars.BlockReach = BlockReach;
        }
        else
        {
            float velAlongStab = MathF.Max(0f, Vector2.Dot(vars.StabDir, ctx.Body.Velocity));
            float t = MathHelper.Clamp(velAlongStab / BoostReferenceSpeed, 0f, 1f);
            vars.Boost = MathHelper.Lerp(MinBoost, MaxBoost, t);
            if (vars.Boost > 1.001f)
            {
                vars.BlockReach = BlockReach * vars.Boost;
                float halfW = BlockHalfWidth * MathF.Sqrt(vars.Boost);
                vars.BlockPoly  = Polygon.CreateRectangle(vars.BlockReach, halfW * 2f);
            }
            else
            {
                vars.BlockPoly  = BlockPoly;
                vars.BlockReach = BlockReach;
            }
        }
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        // Larger recovery than slashes — stab can't roll directly into anything.
        ConditionState.SetFor(ref ab.Condition.RecoveryActive,
                              ref ab.Condition.RecoveryExpireFrame, 9, ctx.CurrentFrame);
    }

    // Heavy-stance modifiers throughout the stab; friction dips during the lunge
    // window so the ApplyActionForces velocity assist isn't immediately braked.
    public override void ApplyMovementModifiers(ref MovementModifiers m, in ActionVars vars)
    {
        bool inLunge = vars.TimeInState >= LungeStart && vars.TimeInState <= LungeEnd;
        m.MaxWalkSpeed   *= 0.35f;
        m.WalkAccel      *= 0.5f;
        m.GroundFriction *= inLunge ? 0.15f : 1.5f;
        m.MaxAirSpeed    *= 0.6f;
        m.AirDrag        *= 1.3f;
    }

    // Forward glide along the stab direction during the lunge window. Generalized
    // from "Velocity.X" to a full-vector projection so a diagonal or vertical
    // stab carries the player in that direction, not just horizontally (roadmap
    // §1.7). "Ensure-at-least" semantic preserved: project current velocity onto
    // _stabDir, raise to LungeSpeed if below. A player already moving faster
    // along the stab direction isn't nerfed.
    public override void ApplyActionForces(EnvironmentContext ctx, in ActionVars vars)
    {
        if (!vars.IsGrounded) return;
        if (vars.TimeInState < LungeStart || vars.TimeInState > LungeEnd) return;

        var v = ctx.Body.Velocity;
        float velAlongStab = Vector2.Dot(v, vars.StabDir);
        if (velAlongStab < LungeSpeed)
            ctx.Body.Velocity = v + vars.StabDir * (LungeSpeed - velAlongStab);
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState += ctx.Dt;
        _tipTrail.Tick(ctx.Dt);

        // Soft mouse-tracking: rotate _stabDir toward the cursor direction, with
        // both a per-frame angular-velocity cap and a total-deviation cap from
        // the initial angle. Hemisphere-clamp the target like the initial capture
        // so a cursor behind the player doesn't yank the stab backwards.
        int facing = ab.Facing == 0 ? 1 : ab.Facing;
        Vector2 toMouse = ctx.Input.MouseWorldPosition - ctx.Body.Position;
        if (!AllowBackward && toMouse.X * facing < 0f) toMouse.X = 0f;
        if (toMouse.LengthSquared() > 1e-4f)
        {
            float targetAngle = MathF.Atan2(toMouse.Y, toMouse.X);
            float currentAngle = MathF.Atan2(vars.StabDir.Y, vars.StabDir.X);
            float delta        = WrapAngle(targetAngle - currentAngle);
            float maxStep      = MaxSteerSpeed * ctx.Dt;
            if (delta >  maxStep) delta =  maxStep;
            if (delta < -maxStep) delta = -maxStep;
            float newAngle = currentAngle + delta;
            // Clamp total deviation from initial.
            float dev = WrapAngle(newAngle - vars.InitialStabAngle);
            if (dev >  MaxTotalSteer) newAngle = vars.InitialStabAngle + MaxTotalSteer;
            if (dev < -MaxTotalSteer) newAngle = vars.InitialStabAngle - MaxTotalSteer;
            vars.StabDir = new Vector2(MathF.Cos(newAngle), MathF.Sin(newAngle));
        }

        // Sample the visible tip into the trail so Draw can render a fading ribbon
        // chasing the strike. Cache the extension so Draw doesn't recompute it.
        vars.TipExt = TipExtension(vars.TimeInState / Duration);
        _tipTrail.Push(ctx.Body.Position + vars.StabDir * vars.TipExt);

        if (vars.TimeInState >= HurtboxStartTime &&
            vars.TimeInState <= HurtboxStartTime + HurtboxActiveDuration &&
            ctx.Hitboxes != null)
        {
            // Both stab hitboxes use the actual rotated-rectangle polygon for narrow-phase
            // intersection. Rotation = angle of _stabDir from +X. The broad-phase AABB
            // is computed from the rotated polygon so the cell sweep in CombatSystem
            // still reads correctly.
            float rotation = MathF.Atan2(vars.StabDir.Y, vars.StabDir.X);
            float dmg = DamagePerFrame * vars.Boost;

            // Primary thrust — entity + tile damage along the thrust line.
            var primaryCenter = ctx.Body.Position + vars.StabDir * (Reach * 0.5f);
            var primaryAABB   = PrimaryPoly.GetBoundingBox(primaryCenter, rotation);
            ctx.Hitboxes.Publish(new Hitbox(
                primaryAABB, vars.HitId, dmg,
                vars.StabDir * KnockbackMagnitude,
                ctx.Faction, this, ColorFor(vars.IsGrounded),
                shape: PrimaryPoly, shapePos: primaryCenter, shapeRotation: rotation));

            // Block-shockwave — same HitId so entities that overlap both count once. No
            // knockback (knockback comes from the primary box). Tiles only — passes
            // cleanly past entities along the thrust axis. Polygon + reach are pre-scaled
            // by _boost at Enter; a well-aligned air dive gets a substantially longer
            // and slightly wider box plus the damage multiplier.
            var blockCenter = ctx.Body.Position + vars.StabDir * (vars.BlockReach * 0.5f);
            var blockAABB   = vars.BlockPoly.GetBoundingBox(blockCenter, rotation);
            // Brighten the debug color in proportion to boost so the bigger box reads
            // visually distinct from a baseline stab when DebugDrawHitboxes is on.
            float boostT = (vars.Boost - MinBoost) / (MaxBoost - MinBoost);
            var blockColor = Color.Lerp(Color.Lerp(ColorFor(vars.IsGrounded), Color.Gray, 0.4f), Color.White, boostT * 0.5f);
            ctx.Hitboxes.Publish(new Hitbox(
                blockAABB, vars.HitId, dmg,
                Vector2.Zero,
                ctx.Faction, this,
                blockColor,
                HitTargets.TilesOnly,
                shape: vars.BlockPoly, shapePos: blockCenter, shapeRotation: rotation));
        }
    }

    // Wrap an angle into [-π, π] so steering math doesn't wind up around a full circle.
    private static float WrapAngle(float a)
    {
        while (a >  MathF.PI) a -= MathF.Tau;
        while (a < -MathF.PI) a += MathF.Tau;
        return a;
    }

    // Phase boundaries for the visible extension curve (in normalized state time
    // t = _timeInState / Duration). The strike phase is what the hurtbox active
    // window is timed against — it opens at ~WindupEnd and closes inside Hold.
    private const float WindupEnd      = 0.18f;  // small backward draw of the arm
    private const float StrikeEnd      = 0.42f;  // tip reaches full reach
    private const float HoldEnd        = 0.67f;  // holds at full reach before retracting
    private const float PullbackFrac   = 0.10f;  // how far back the tip pulls, as a fraction of Reach

    // Tip extension along _stabDir at normalized state-time `t`, in pixels.
    // Negative = pulled back behind the body. Built as four piecewise cubic
    // Béziers so each phase keeps a tangent we control: a soft windup, a fast
    // snap, a hold at full reach, and a smooth retract. The control-point
    // biases shape the easing:
    //   • Windup:   P1 near P0 → slow start (the wind-up "settles").
    //   • Strike:   P1 near P0, P2 near P3 → ease-in-out with a steep middle,
    //               reading as anticipation → snap.
    //   • Retract:  P1 near P0, P2 ~ P3 → ease-out, the arm relaxes.
    private static float TipExtension(float t)
    {
        t = MathHelper.Clamp(t, 0f, 1f);
        float pb = -PullbackFrac * Reach;
        if (t < WindupEnd)
        {
            float u = t / WindupEnd;
            return Bezier.Cubic(0f, 0f, pb, pb, u);
        }
        if (t < StrikeEnd)
        {
            float u = (t - WindupEnd) / (StrikeEnd - WindupEnd);
            return Bezier.Cubic(pb, pb, Reach * 1.08f, Reach, u);
        }
        if (t < HoldEnd) return Reach;
        {
            float u = (t - HoldEnd) / (1f - HoldEnd);
            return Bezier.Cubic(Reach, Reach, 0f, 0f, u);
        }
    }

    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body, in ActionVars vars)
    {
        // Trail first so the tip dot reads on top of the ribbon.
        var color = ColorFor(vars.IsGrounded);
        _tipTrail.Draw(sb, pixel, color, color * 0f, 4f);

        var tip   = body.Position + vars.StabDir * vars.TipExt;
        var mid   = body.Position + vars.StabDir * (vars.TipExt * 0.5f);
        sb.Draw(pixel, new Rectangle((int)tip.X - 2, (int)tip.Y - 2, 4, 4), color);
        sb.Draw(pixel, new Rectangle((int)mid.X - 1, (int)mid.Y - 1, 3, 3), color * 0.5f);
    }
}

// Air spin-stab. Roadmap §1.6: a Stab swipe pointed opposite of facing while in
// air. Inherits stab's hitboxes, dive-boost, and steer logic — the only delta is
// AllowBackward + an air+backward-swipe precondition + a Facing flip on Enter.
public class AirSpinStab : StabAction
{
    protected override bool AllowBackward => true;

    // Beat the default StabAction (30/30) on ties when both could fire.
    public override int PassivePriority => 35;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (!base.CheckPreConditions(ctx, ab)) return false;
        // Air-only.
        if (ctx.TryGetGround(out _)) return false;
        // Backward swipe — intent direction's X must oppose Facing.
        if (!ctx.Intents.Peek(IntentType.Stab, ctx.CurrentFrame, out var intent)) return false;
        int facing = ab.Facing == 0 ? 1 : ab.Facing;
        return intent.Direction.X * facing < 0f;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        // Flip facing so the spin-stab leaves the player oriented the new way.
        // Done BEFORE base.Enter so any facing-derived fallback in capture
        // matches the new direction.
        ab.Facing = -(ab.Facing == 0 ? 1 : ab.Facing);
        base.Enter(ctx, ab, ref vars);
    }
}

// ---------- Guard — hold Shift to parry incoming hits ---------------------------

// Defensive posture. Held while Shift is down (with no L/R held — moving cancels
// the guard). Sets Combat.GuardActive so PlayerCharacter.OnHit's parry path can
// run; applies a slowdown to walk/air speeds; draws a small shield indicator.
//
// A successful weak in-cone parry (Combat.TryParry) sets Combat.GuardCharged,
// arming GuardRetaliateAction (LMB-press while charged → fast forward slash).
// Air-allowed per user note in the roadmap §9: yes, allow guard in air. The
// slowdown via modifiers is identical air-vs-ground; no separate movement state.
public class GuardAction : ActionState
{
    public override int ActivePriority  => 35;   // beats slash candidates (30) but loses to Recovery (40)
    public override int PassivePriority => 40;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (!ctx.Input.Shift)        return false;
        if (ctx.Input.Left || ctx.Input.Right) return false;  // no activation while pushing L/R
        if (ctx.Combat?.BlocksAttack == true) return false;
        if (ab.Condition.RecoveryActive)       return false;
        return true;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        if (!ctx.Input.Shift) return false;
        if (ctx.Input.Left || ctx.Input.Right) return false;
        if (ctx.Combat?.BlocksAttack == true) return false;
        return true;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        if (ctx.Combat != null) ctx.Combat.GuardActive = true;
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        if (ctx.Combat != null) ctx.Combat.GuardActive = false;
    }

    // Slow walk, slower air. Gravity normal — guard doesn't levitate.
    public override void ApplyMovementModifiers(ref MovementModifiers m, in ActionVars vars)
    {
        m.MaxWalkSpeed *= 0.5f;
        m.WalkAccel    *= 0.5f;
        m.MaxAirSpeed  *= 0.8f;
        m.AirAccel     *= 0.8f;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars) { }

    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body, in ActionVars vars)
    {
        // Shield indicator above the head. Charged-state cue is the
        // GuardRetaliateAction firing on click, not a Draw tint (Draw doesn't
        // have ab and we'd rather not thread a static through for visuals).
        const int W = 4;
        const int H = 12;
        var pos = body.Position;
        var rect = new Rectangle((int)pos.X - W / 2, (int)pos.Y - (int)PlayerCharacter.Radius - H - 2, W, H);
        sb.Draw(pixel, rect, Color.LightSteelBlue * 0.8f);
    }
}

// ---------- GuardRetaliate — fast counter from a charged parry ------------------

// Fires when LMB is pressed during the GuardCharged window. A forward slash with
// short duration, narrow sweep, big knockback. Consumes the charge on Enter so
// it doesn't refire while the click is held. Higher passive priority than the
// regular slashes so the click goes here instead of a normal GroundSlash1.
public class GuardRetaliateAction : SlashLikeAction
{
    protected override float Duration            => 0.10f;
    protected override float ArcRadiusScale      => 1.20f;
    protected override float SweepAngleDeg       => 70f;
    protected override float SweepDirection      => +1f;
    protected override float KnockbackMagnitude  => 420f;     // top-end — counters reward heavily
    protected override Color SlashColor          => Color.Cyan;
    protected override bool  RequireGround       => false;
    protected override bool  RequireAir          => false;

    public override int PassivePriority => 55;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        // Must be charged AND have a click intent. Don't gate on GuardActive
        // itself — the charge persists for a window even after the player
        // releases Shift (and lets go of Guard), so they can parry → release
        // → retaliate in a quick sequence.
        if (ctx.Combat?.GuardCharged != true) return false;
        if (!ctx.Intents.Peek(IntentType.Click, ctx.CurrentFrame, out _)) return false;
        if (ctx.Combat?.BlocksAttack == true) return false;
        return true;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        // Consume the charge — one retaliate per parry. Set GuardCharged = false
        // BEFORE base.Enter so a held-click doesn't immediately re-enter from
        // CheckPreConditions (which would still see the click intent until base
        // consumes it).
        if (ctx.Combat != null) ctx.Combat.GuardCharged = false;
        base.Enter(ctx, ab, ref vars);
    }

    protected override void OnExitSetFlags(ConditionState c, int f)
    {
        ConditionState.SetFor(ref c.RecoveryActive, ref c.RecoveryExpireFrame, 3, f);
    }
}

// ---------- Pulse — long-hold + circular drag → expanding ring -------------------

// Wide-area attack: N segment hitboxes arranged at a common radius around a captured
// origin point, the radius lerping from StartRadius → EndRadius across the active
// window. All segments share one HitId so an entity in the ring's path takes a
// single hit; tile damage is per-segment-per-frame (CombatSystem doesn't dedupe
// tiles), so a tile under any segment for a couple of frames breaks reliably.
public class PulseAction : ActionState
{
    private const float Duration             = 0.70f;
    private const float HitboxStartTime      = 0.15f;
    private const float HitboxActiveDuration = 0.40f;
    private const int   Segments             = 12;
    private const float StartRadius          = PlayerCharacter.Radius * 1.2f;
    private const float EndRadius            = PlayerCharacter.Radius * 5.0f;
    // Segment AABB half-size. Larger → fewer gaps between segments at full radius,
    // but more tile overlap per frame. ~70% of body radius is a clean balance.
    private const float SegmentHalfSize      = PlayerCharacter.Radius * 0.7f;
    private const float KnockbackMagnitude   = 450f;
    // Damage per frame matches SlashLikeAction.SlashDamagePerFrame so a sand tile
    // crumbles in one ring-pass and dirt cracks meaningfully — same feel as a slash.
    private const float DamagePerFrame       = TileDamage.TileMaxHP / 2f;


    public override int ActivePriority  => 30;
    public override int PassivePriority => 30;

    private static Color PulseColorFor(bool grounded) => grounded ? Color.Gold : Color.Cyan;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
        => ctx.Intents.Peek(IntentType.Circle, ctx.CurrentFrame, out _);

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
        => vars.TimeInState < Duration;

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState = 0f;
        vars.IsGrounded  = ctx.TryGetGround(out _);
        vars.HitId       = ctx.HitIds.Next();
        ctx.Intents.Consume(IntentType.Circle, ctx.CurrentFrame);
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        // Long recovery — pulse is the biggest single attack, can't roll directly
        // into anything else.
        ConditionState.SetFor(ref ab.Condition.RecoveryActive,
                              ref ab.Condition.RecoveryExpireFrame, 12, ctx.CurrentFrame);
    }

    // Heavy stance throughout the pulse — applies on ground AND in air, unlike
    // Stab which leaves air movement mostly alone. Pairs with the gravity scale
    // to give a hovering "cast" feel mid-air.
    public override void ApplyMovementModifiers(ref MovementModifiers m, in ActionVars vars)
    {
        m.MaxWalkSpeed   *= 0.25f;
        m.WalkAccel      *= 0.5f;
        m.GroundFriction *= 1.5f;
        m.MaxAirSpeed    *= 0.25f;
        m.AirAccel       *= 0.5f;
        m.AirDrag        *= 1.5f;
        m.GravityScale   *= 0.3f;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState += ctx.Dt;

        if (vars.TimeInState < HitboxStartTime ||
            vars.TimeInState > HitboxStartTime + HitboxActiveDuration ||
            ctx.Hitboxes == null) return;

        // Radius lerps from start → end across the active window.
        float u = (vars.TimeInState - HitboxStartTime) / HitboxActiveDuration;
        if (u < 0f) u = 0f; else if (u > 1f) u = 1f;
        float r = MathHelper.Lerp(StartRadius, EndRadius, u);

        // Anchor to the player's CURRENT position each frame — the ring drifts
        // with the caster rather than hanging at the cast point. Each segment's
        // knockback also picks up the body's velocity so a moving caster imparts
        // their momentum to anything the ring sweeps.
        var anchor  = ctx.Body.Position;
        var bodyVel = ctx.Body.Velocity;

        var color = PulseColorFor(vars.IsGrounded);
        for (int i = 0; i < Segments; i++)
        {
            float angle = i * MathHelper.TwoPi / Segments;
            var dir    = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            var center = anchor + dir * r;
            var region = new BoundingBox(
                center.X - SegmentHalfSize, center.Y - SegmentHalfSize,
                center.X + SegmentHalfSize, center.Y + SegmentHalfSize);
            ctx.Hitboxes.Publish(new Hitbox(
                region, vars.HitId, DamagePerFrame,
                dir * KnockbackMagnitude + bodyVel,
                ctx.Faction, this, color));
        }
    }

    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body, in ActionVars vars)
    {
        if (vars.TimeInState < HitboxStartTime ||
            vars.TimeInState > HitboxStartTime + HitboxActiveDuration) return;
        float u = (vars.TimeInState - HitboxStartTime) / HitboxActiveDuration;
        if (u < 0f) u = 0f; else if (u > 1f) u = 1f;
        float r = MathHelper.Lerp(StartRadius, EndRadius, u);
        var color = PulseColorFor(vars.IsGrounded);
        for (int i = 0; i < Segments; i++)
        {
            float angle = i * MathHelper.TwoPi / Segments;
            var pos = body.Position + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * r;
            sb.Draw(pixel, new Rectangle((int)pos.X - 2, (int)pos.Y - 2, 4, 4), color);
        }
    }
}

// ---------- Block Eruption — RMB-in-solid charge + sweep ------------------------

// Two-phase move:
//   BlockReadyAction      — RMB held with cursor INSIDE a solid cell. Accumulates
//                            charge time; arms the eruption when the cursor leaves
//                            solid (the "ignition" event).
//   BlockEruptionAction   — RMB held with cursor OUTSIDE solid AND BlockEruption-
//                            Armed flag set. Samples a smoothed path of cursor
//                            positions/velocities. On RMB release, runs the
//                            EruptionPlanner to spawn N tile sprouts.
//
// Priority arrangement:
//   BlockReady     Active 8,  Passive 10  — under ReadyAction.Active (10), so
//                                            ReadyAction (Passive 15) can preempt
//                                            to cancel via attack input.
//   BlockEruption  Active 9,  Passive 10  — preempts BlockReady (8) when the
//                                            armed flag flips; same Passive ceiling
//                                            so ReadyAction can still cancel.

internal static class BlockEruptionHelpers
{
    // True if the cell under the cursor is currently solid. Sprouting cells do
    // NOT count — the move is for shoving *out of* committed terrain, not for
    // chaining off your own growing sprouts.
    public static bool IsCursorInSolid(EnvironmentContext ctx)
    {
        var p = ctx.Input.MouseWorldPosition;
        int gtx = (int)MathF.Floor(p.X / Chunk.TileSize);
        int gty = (int)MathF.Floor(p.Y / Chunk.TileSize);
        return ctx.Chunks.GetCellState(gtx, gty) == TileState.Solid;
    }

    public static (int gtx, int gty) CursorCell(EnvironmentContext ctx)
    {
        var p = ctx.Input.MouseWorldPosition;
        return ((int)MathF.Floor(p.X / Chunk.TileSize),
                (int)MathF.Floor(p.Y / Chunk.TileSize));
    }

    public static Vector2 CellCenter(int gtx, int gty)
        => new Vector2(
            gtx * Chunk.TileSize + Chunk.TileSize * 0.5f,
            gty * Chunk.TileSize + Chunk.TileSize * 0.5f);
}

public class BlockReadyAction : ActionState
{
    // Minimum hold-in-solid time before exiting-out-of-solid arms the eruption.
    // Below this, BlockReady ends quietly (no eruption armed); the still-held
    // RMB then drops back to NullAction and HandleBuildInput in Game1 picks it
    // up as a normal tile-placement drag. Lets the player tap RMB on a block
    // and start building without committing to an eruption charge.
    private const float MinChargeToArm  = 1.0f;
    // Saturation point — best release timing for max budget.
    private const float SaturationTime  = 2.0f;
    // Past saturation, budget drops by 35% (the "timing penalty"). Sharp transition
    // at the SaturationTime mark gives the player a clean target.
    private const float DipFactor       = 0.65f;
    // Min/Max blocks at the budget endpoints.
    private const float BudgetMin       = 0f;
    private const float BudgetMax       = 60f;
    // Visual ring on the origin cell — radius grows with charge fraction.
    private const float MaxIndicatorRadius = Chunk.TileSize * 1.8f;

    public override int ActivePriority  => 8;
    public override int PassivePriority => 10;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        // RMB press-edge anywhere — the charge starts wherever the press
        // happens. Arming for BlockEruption is decided later by Update when
        // the cursor sweeps out of a solid cell.
        if (!ctx.Input.RightClick) return false;
        var prev = ctx.Controller.GetPrevious(1);
        return !prev.RightClick;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
        // Alive while RMB held. Cursor position no longer ends the state —
        // it's checked in Update to arm BlockEruption on the in→out sweep.
        => ctx.Input.RightClick;

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.ChargeTime = 0f;
        // Fresh charge starts unarmed — Update sets the flag on the sweep.
        ab.Condition.BlockEruptionArmed = false;
        vars.InSolidLastFrame = BlockEruptionHelpers.IsCursorInSolid(ctx);
        if (vars.InSolidLastFrame)
        {
            var (gtx, gty) = BlockEruptionHelpers.CursorCell(ctx);
            vars.OriginCell = BlockEruptionHelpers.CellCenter(gtx, gty);
        }
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        // Charge grows when nothing was placed this frame; decays at the same
        // rate when HandleBuildInput just committed a tile. Lets a held RMB
        // that's actively building stay near zero, while a held RMB over a
        // wall (no placement possible) accumulates normally.
        bool placedThisFrame = ctx.LastTilePlacedFrame == ctx.CurrentFrame;
        if (placedThisFrame) vars.ChargeTime = MathF.Max(0f, vars.ChargeTime - ctx.Dt);
        else                 vars.ChargeTime += ctx.Dt;

        bool inSolid = BlockEruptionHelpers.IsCursorInSolid(ctx);
        if (inSolid)
        {
            // Re-anchor origin to the current solid cell. After the cursor
            // sweeps out, OriginCell retains the last solid cell visited —
            // that's the ignition point used by BlockEruption.
            var (gtx, gty) = BlockEruptionHelpers.CursorCell(ctx);
            vars.OriginCell = BlockEruptionHelpers.CellCenter(gtx, gty);
        }

        // In→out sweep arms BlockEruption (set BlockEruptionArmed + handoff
        // fields). The FSM picks up the armed flag on the following frame's
        // scan; BlockEruption.Enter consumes it.
        if (vars.InSolidLastFrame && !inSolid && vars.ChargeTime >= MinChargeToArm)
        {
            ab.Condition.BlockEruptionArmed = true;
            ab.Condition.BlockChargeTime   = vars.ChargeTime;
            ab.Condition.BlockChargeOrigin = vars.OriginCell;
        }
        vars.InSolidLastFrame = inSolid;
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        // Arming happens in Update on the in→out sweep. On a natural release
        // (RMB up) with no pending armed handoff, clear the flag so a stale
        // arm doesn't leak into a future press. If RMB is still held, we're
        // being preempted by another action (likely BlockEruption itself);
        // leave the flag intact so its Enter can consume it.
        if (!ctx.Input.RightClick)
            ab.Condition.BlockEruptionArmed = false;
    }

    // Heavy stance during the charge — same shape as Stab's modifier set, applies
    // on ground + air. Players can still nudge but not sprint mid-charge.
    public override void ApplyMovementModifiers(ref MovementModifiers m, in ActionVars vars)
    {
        m.MaxWalkSpeed   *= 0.35f;
        m.WalkAccel      *= 0.5f;
        m.GroundFriction *= 1.5f;
        m.MaxAirSpeed    *= 0.5f;
        m.AirAccel       *= 0.6f;
        m.AirDrag        *= 1.3f;
        m.GravityScale   *= 0.4f;
    }

    // Visual: an N-segment ring at the origin cell. Radius and color encode
    // charge progress; at the SaturationTime mark, the ring "snaps" to its peak
    // (bright gold). Past saturation, the ring shrinks by 35% and tints toward
    // dim orange-red — the visible cue for "you held too long."
    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body, in ActionVars vars)
    {
        const int segments = 16;
        bool saturated = vars.ChargeTime >= SaturationTime;

        float chargeFrac = saturated ? 1f : (vars.ChargeTime / SaturationTime);
        float r = MaxIndicatorRadius * chargeFrac * (saturated ? DipFactor : 1f);

        Color color = saturated
            ? new Color(220, 90, 40)                                       // dimmed orange-red after dip
            : Color.Lerp(new Color(150, 100, 60), Color.Gold, chargeFrac); // brown → gold ramping

        for (int i = 0; i < segments; i++)
        {
            float a = i * MathHelper.TwoPi / segments;
            var p = vars.OriginCell + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
            sb.Draw(pixel, new Rectangle((int)p.X - 1, (int)p.Y - 1, 3, 3), color);
        }
    }
}

public class BlockEruptionAction : ActionState
{
    // Mirror BlockReady's budget curve.
    private const float SaturationTime = 2.0f;
    private const float DipFactor      = 0.65f;
    private const float BudgetMin      = 0f;
    private const float BudgetMax      = 60f;
    // Trigger-arming window. Once BlockReady arms the eruption (cursor swept
    // out of solid), the player has this long to release RMB before the
    // arming auto-cancels. Avoids "I swept past a wall 5 seconds ago and
    // forgot, then released somewhere weird" surprise fires.
    private const float ArmingWindow   = 0.6f;

    public override int ActivePriority  => 9;
    public override int PassivePriority => 10;

    // Reference-type per-activation buffers. NOT in ActionVars (a value-type struct
    // copy can't safely capture a growing list / mutable pen). The accumulating
    // gesture history + smoothing pen are deep-copied at snapshot time (goal 6);
    // _simResult is a render-only cache. See ActionVars header.
    private SmoothPen      _pen;
    private List<PathSample> _samples;
    private MassBallPlanner.SimulationResult _simResult;

    // ── Snapshot/restore (roadmap goal 4 §F note) ──────────────────────────────
    // _pen + _samples are the one residual reference-type per-activation buffer that
    // can't ride in the flat ActionVars struct: a growing gesture history + a mutable
    // smoothing pen. They get a genuine deep copy here. _simResult is render-only —
    // excluded (it self-heals on the next Update while the preview is on).
    public EruptionGestureState CaptureGesture() => new()
    {
        HasPen      = _pen != null,
        PenPosition = _pen?.Position ?? Vector2.Zero,
        PenVelocity = _pen?.Velocity ?? Vector2.Zero,
        Samples     = _samples?.ToArray(),
    };

    public void RestoreGesture(in EruptionGestureState s)
    {
        if (s.HasPen)
        {
            _pen ??= new SmoothPen(s.PenPosition);
            _pen.Position = s.PenPosition;
            _pen.Velocity = s.PenVelocity;
        }
        else _pen = null;

        _samples  = s.Samples != null ? new List<PathSample>(s.Samples) : null;
        _simResult = null;   // render-only cache; rebuilt next Update if preview is on
    }

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
        => ctx.Input.RightClick && ab.Condition.BlockEruptionArmed;

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
        => ctx.Input.RightClick && vars.TimeInState < ArmingWindow;

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        // Consume the armed flag + capture the charge/origin handoff.
        ab.Condition.BlockEruptionArmed = false;
        vars.ChargeTime  = ab.Condition.BlockChargeTime;
        vars.Origin      = ab.Condition.BlockChargeOrigin;
        vars.TimeInState = 0f;

        _pen     = new SmoothPen(ctx.Input.MouseWorldPosition);
        _samples = new List<PathSample>(64);
        // Seed the first sample at the eruption origin with zero velocity — that
        // gives the EruptionPlanner a wide-radius "base" deposit at the ignition
        // cell, producing the pyramid's wide base.
        _samples.Add(new PathSample(vars.Origin, Vector2.Zero));
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState += ctx.Dt;
        _pen.Update(ctx.Input.MouseWorldPosition, ctx.Dt);
        _samples.Add(new PathSample(_pen.Position, _pen.Velocity));

        // Re-simulate when the preview is on so Draw can render an up-to-date
        // ball trajectory + landing cells. Skipped when off so we don't burn
        // cycles every frame on a sim no one is looking at.
        if (EruptionPlanner.DebugDrawMassBall && ctx.EruptionMode == EruptionPlannerMode.MassBall)
        {
            int budget = ComputeBudget(vars.ChargeTime);
            _simResult = MassBallPlanner.Simulate(ctx.Chunks, vars.Origin, _samples, budget);
        }
        else
        {
            _simResult = null;
        }
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        // Only fire when naturally exited (RMB released). A preempt by ReadyAction
        // / attack leaves RMB held and silently cancels the eruption.
        if (ctx.Input.RightClick) return;

        int budget = ComputeBudget(vars.ChargeTime);
        if (budget > 0 && _samples != null && _samples.Count > 0)
            EruptionPlanner.Plan(ctx.Chunks, vars.Origin, _samples, budget, ctx.EruptionMode, ctx.ActiveBlockType);
    }

    private static int ComputeBudget(float chargeTime)
    {
        float raw;
        if (chargeTime < SaturationTime)
            raw = MathHelper.Lerp(BudgetMin, BudgetMax, chargeTime / SaturationTime);
        else
            raw = BudgetMax * DipFactor;
        return (int)MathF.Round(raw);
    }

    // Same heavy stance during sample/sweep — keeps the charge feel continuous.
    public override void ApplyMovementModifiers(ref MovementModifiers m, in ActionVars vars)
    {
        m.MaxWalkSpeed   *= 0.35f;
        m.WalkAccel      *= 0.5f;
        m.GroundFriction *= 1.5f;
        m.MaxAirSpeed    *= 0.5f;
        m.AirAccel       *= 0.6f;
        m.AirDrag        *= 1.3f;
        m.GravityScale   *= 0.4f;
    }

    // Visual: small breadcrumb dots along the sampled path. Players see the path
    // their pen has traced; on release the eruption fires along it.
    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body, in ActionVars vars)
    {
        if (_samples == null) return;
        // Origin marker — bright dot.
        sb.Draw(pixel, new Rectangle((int)vars.Origin.X - 2, (int)vars.Origin.Y - 2, 5, 5), Color.Gold);
        // Path trail — only the tail end of the gesture is drawn so the
        // breadcrumb doesn't run all the way back to the origin for long
        // charges; the planner still receives the full _samples list. Dot
        // size shrinks monotonically from head (latest) to tail (oldest)
        // so the gesture direction reads at a glance.
        const int VisibleTrailSamples = 12;
        const int HeadHalf = 2;  // newest dot → 5x5
        const int TailHalf = 0;  // oldest visible dot → 1x1
        int n = _samples.Count;
        int start = Math.Max(1, n - VisibleTrailSamples);
        int span  = n - start;
        for (int i = start; i < n; i++)
        {
            float t = span <= 0 ? 1f : (float)(i - start) / span;
            var p = _samples[i].Position;
            int half = (int)MathF.Round(MathHelper.Lerp(TailHalf, HeadHalf, t));
            int size = half * 2 + 1;
            sb.Draw(pixel, new Rectangle((int)p.X - half, (int)p.Y - half, size, size),
                Color.SandyBrown * (0.35f + 0.5f * t));
        }

        // Mass-ball simulation preview. Renders the predicted ball trajectory as
        // tiny dim dots and outlines the cells the ball is expected to sprout —
        // so the player can see *where* their gesture is going to deposit before
        // they release. Off by default; toggle via game_config.json.
        if (_simResult == null) return;
        var traj = _simResult.BallTrajectory;
        for (int i = 0; i < traj.Count; i++)
        {
            var p = traj[i];
            float t = traj.Count <= 1 ? 1f : (float)i / (traj.Count - 1);
            sb.Draw(pixel,
                new Rectangle((int)p.X - 1, (int)p.Y - 1, 2, 2),
                new Color(220, 180, 80) * (0.35f + 0.4f * t));
        }
        // Final ball-rest position — chunky dot at the end of the trajectory.
        if (traj.Count > 0)
        {
            var last = traj[traj.Count - 1];
            sb.Draw(pixel,
                new Rectangle((int)last.X - 3, (int)last.Y - 3, 7, 7),
                new Color(255, 140, 40));
        }
        // Predicted sprout cells — translucent outline on each tile so the
        // player sees the deposit footprint.
        foreach (var (gtx, gty) in _simResult.SproutCells)
        {
            int x = gtx * Chunk.TileSize;
            int y = gty * Chunk.TileSize;
            int s = Chunk.TileSize;
            var c = new Color(180, 120, 60) * 0.45f;
            sb.Draw(pixel, new Rectangle(x,         y,         s, 1), c);
            sb.Draw(pixel, new Rectangle(x,         y + s - 1, s, 1), c);
            sb.Draw(pixel, new Rectangle(x,         y,         1, s), c);
            sb.Draw(pixel, new Rectangle(x + s - 1, y,         1, s), c);
        }
    }
}

// ---------- Ranged: EnergyBall (Shift + LMB tap) --------------------------------

// Roadmap §4.1. Short action that spawns one EnergyBallProjectile toward the
// cursor and sets a brief recovery. Priority sits ABOVE GuardAction's Active 35
// so a Shift+click during a guard stance momentarily preempts the guard to fire,
// then the FSM re-evaluates and Guard re-arms on the next frame.
public class EnergyBallAction : ActionState
{
    private const float Duration       = 0.15f;
    private const int   RecoveryFrames = 4;
    // Distance ahead of the player center where the projectile spawns. Keeps
    // the ball from immediately overlapping the player's body/hurtbox.
    private const float SpawnOffset    = PlayerCharacter.Radius * 1.2f;

    public override int ActivePriority  => 40;
    public override int PassivePriority => 45;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (!ctx.Input.Shift) return false;
        if (!ctx.Intents.Peek(IntentType.Click, ctx.CurrentFrame, out _)) return false;
        if (ctx.Combat?.BlocksAttack == true) return false;
        if (ab.Condition.RecoveryActive)    return false;
        return true;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
        => vars.TimeInState < Duration;

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState = 0f;
        ctx.Intents.Consume(IntentType.Click, ctx.CurrentFrame);

        if (ctx.Spawner == null) return;
        Vector2 toCursor = ctx.Input.MouseWorldPosition - ctx.Body.Position;
        Vector2 dir = toCursor.LengthSquared() < 1e-4f
            ? new Vector2(ab.Facing == 0 ? 1f : ab.Facing, 0f)
            : Vector2.Normalize(toCursor);
        var spawnPos = ctx.Body.Position + dir * SpawnOffset;
        ctx.Spawner.SpawnEntity(new EnergyBallProjectile(spawnPos, dir, ctx.HitIds.Next(), ctx.Faction));
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState += ctx.Dt;
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        ConditionState.SetFor(ref ab.Condition.RecoveryActive, ref ab.Condition.RecoveryExpireFrame, RecoveryFrames, ctx.CurrentFrame);
    }
}

// ---------- Ranged: Beam (Shift + LMB hold) -------------------------------------

// Roadmap §4.2 — sustained particle beam. Shift+LMB press-edge starts charging;
// once _chargeTime ≥ MinChargeTime AND LMB still held, the beam fires every
// frame for up to MaxFiringTime. Release LMB at any point during charge → no
// beam fires (the user's "fails when interrupted early" requirement makes the
// move non-spammable).
//
// Why not piggy-back on Stab intent (which is the roadmap's original gesture
// suggestion): Stab fires on RELEASE, after a long hold + swipe. We want the
// beam visible DURING the hold so the player can sweep it across targets while
// firing. Press-edge + per-frame LMB poll matches the intended feel.
//
// Click coexistence: when a short Shift+LMB tap releases inside the charge
// window, BeamAction.CheckConditions returns false (LMB released), BeamAction
// exits without firing, and the same release frame's Click intent routes to
// EnergyBallAction. So short Shift+LMB = energy ball, long Shift+LMB = beam.
public class BeamAction : ActionState
{
    private const float MinChargeTime    = 0.35f;
    private const float MaxFiringTime    = 0.55f;
    // Beam reach in world pixels — beyond this the segments stop publishing.
    private const float MaxBeamLength    = 220f;
    private const int   BeamSegments     = 14;
    private const float SegmentHalfSize  = 6f;
    private const float DamagePerFrame   = TileDamage.TileMaxHP * 0.45f;   // breaks Stone in 2-3 frames of overlap
    private const float KnockbackImpulse = 320f;
    private const int   RecoveryFrames   = 6;


    // Render-only cache. The beam's live sim state (charge/firing timers, hitId)
    // lives in ActionVars; these two only feed Draw's dot chain and self-heal on
    // the next firing Update, so they stay out of the snapshot. See ActionVars header.
    private Vector2 _lastBeamDir   = Vector2.UnitX;
    private float   _lastBeamReach;

    public override int ActivePriority  => 40;
    public override int PassivePriority => 45;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (!ctx.Input.Shift) return false;
        if (!ctx.Input.LeftClick) return false;
        var prev = ctx.Controller.GetPrevious(1);
        if (prev.LeftClick) return false;
        if (ctx.Combat?.BlocksAttack == true) return false;
        if (ab.Condition.RecoveryActive)    return false;
        return true;
    }

    // Alive while Shift + LMB both held AND we're either charging or within the
    // firing window. Releasing LMB during charge cancels the beam; releasing
    // during firing ends the beam cleanly.
    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        if (!ctx.Input.LeftClick) return false;
        if (!ctx.Input.Shift)     return false;
        if (ctx.Combat?.BlocksAttack == true) return false;
        if (vars.Firing && vars.FiringTime >= MaxFiringTime) return false;
        return true;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.ChargeTime = 0f;
        vars.FiringTime = 0f;
        vars.Firing     = false;
        vars.HitId      = ctx.HitIds.Next();
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        if (!vars.Firing)
        {
            vars.ChargeTime += ctx.Dt;
            if (vars.ChargeTime >= MinChargeTime) vars.Firing = true;
            return;
        }
        vars.FiringTime += ctx.Dt;
        if (ctx.Hitboxes == null) return;

        // Beam reaches from player center toward cursor, clamped to MaxBeamLength.
        var start = ctx.Body.Position;
        var toCursor = ctx.Input.MouseWorldPosition - start;
        float len = toCursor.Length();
        if (len < 1e-3f) return;
        var dir = toCursor / len;
        float reach = MathF.Min(len, MaxBeamLength);
        _lastBeamDir   = dir;
        _lastBeamReach = reach;

        // Publish a chain of segment hitboxes along the beam. HitTargets.All so
        // each segment damages BOTH tiles and entities — that's what makes the
        // beam carve tunnels through terrain while also hurting anything in its
        // line of fire. Shared HitId across segments means the (HitId,Target)
        // dedupe in CombatSystem treats the whole beam as ONE attack per
        // entity — chained segments don't multi-hit a single body.
        for (int i = 0; i < BeamSegments; i++)
        {
            float t = (i + 1f) / BeamSegments;
            var center = start + dir * (reach * t);
            var region = new BoundingBox(
                center.X - SegmentHalfSize, center.Y - SegmentHalfSize,
                center.X + SegmentHalfSize, center.Y + SegmentHalfSize);
            ctx.Hitboxes.Publish(new Hitbox(
                region, vars.HitId, DamagePerFrame,
                dir * KnockbackImpulse,
                ctx.Faction, this, Color.Magenta));
        }
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        // Recovery on exit no matter how we left — a successful beam locks the
        // player out of follow-up Shift+LMB for a moment, an interrupted charge
        // does likewise (which is a feel-call; it punishes spamming).
        ConditionState.SetFor(ref ab.Condition.RecoveryActive, ref ab.Condition.RecoveryExpireFrame, RecoveryFrames, ctx.CurrentFrame);
    }

    // Heavy stance while charging + firing — beam needs the player committed.
    public override void ApplyMovementModifiers(ref MovementModifiers m, in ActionVars vars)
    {
        m.MaxWalkSpeed *= 0.4f;
        m.WalkAccel    *= 0.6f;
        m.MaxAirSpeed  *= 0.5f;
        m.AirAccel     *= 0.6f;
    }

    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body, in ActionVars vars)
    {
        if (!vars.Firing)
        {
            // Charge ring at the player so the player can see the wind-up. Single
            // dot pulse — keep cheap; tune later if it needs more presence.
            float frac = vars.ChargeTime / MinChargeTime;
            int   r    = (int)(2 + 6 * frac);
            var col = Color.Lerp(new Color(80, 0, 100), Color.Magenta, frac);
            sb.Draw(pixel, new Rectangle((int)body.Position.X - r, (int)body.Position.Y - r, r * 2, r * 2), col * 0.6f);
            return;
        }
        // Active beam: dot chain along the firing line, using the cached
        // direction + reach from the most recent Update.
        for (int i = 0; i < BeamSegments; i++)
        {
            float t = (i + 1f) / BeamSegments;
            var p = body.Position + _lastBeamDir * (_lastBeamReach * t);
            sb.Draw(pixel, new Rectangle((int)p.X - 2, (int)p.Y - 2, 4, 4), Color.Magenta);
        }
    }
}

// ---------- Ranged: LobbedArea (Shift + RMB charge) -----------------------------

// Roadmap §4.3 — ranged eruption. Hold Shift+RMB to charge a budget like
// BlockReadyAction does; on release, launch a LobbedAreaProjectile on a
// ballistic arc toward the cursor that detonates at landing into a mass-ball
// eruption + radial AOE.
//
// Why this collides with BlockReadyAction's RMB-anywhere charge: BlockReady
// doesn't gate on Shift. LobbedArea adds the Shift requirement, and its
// higher priority (45 passive) wins press-edge selection when Shift is held.
// Non-Shift RMB still routes to BlockReady as before. Once LobbedAreaAction
// is current, RMB-held keeps it active even if the player releases Shift —
// the gesture is committed at press-edge.
public class LobbedAreaAction : ActionState
{
    private const float MinChargeToFire = 0.4f;
    private const float SaturationTime  = 1.8f;
    private const float DipFactor       = 0.7f;
    private const float BudgetMin       = 0f;
    private const float BudgetMax       = 50f;
    private const int   RecoveryFrames  = 6;
    // Ballistic arc: vertical speed at launch lifts the ball over a tunable apex
    // height; horizontal speed is derived from cursor-distance / time-of-flight
    // so the ball lands AT the cursor under standard MovementConfig gravity.
    // We don't actually integrate gravity ourselves — PhysicsBody handles that;
    // we just pick (vx, vy) such that the parabola hits the cursor.
    private const float LaunchApexBoost = 180f;       // upward velocity at launch (px/s)
    private const float SpawnOffset     = PlayerCharacter.Radius * 1.2f;

    public override int ActivePriority  => 40;
    public override int PassivePriority => 45;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (!ctx.Input.Shift) return false;
        if (!ctx.Input.RightClick) return false;
        var prev = ctx.Controller.GetPrevious(1);
        if (prev.RightClick) return false;
        if (ctx.Combat?.BlocksAttack == true) return false;
        if (ab.Condition.RecoveryActive)    return false;
        return true;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
        => ctx.Input.RightClick;

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.ChargeTime    = 0f;
        vars.CursorAtPress = ctx.Input.MouseWorldPosition;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.ChargeTime += ctx.Dt;
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        // Recovery regardless — short-charge release still locks out a follow-up
        // throw for a moment so spamming low-budget lobs is throttled.
        ConditionState.SetFor(ref ab.Condition.RecoveryActive, ref ab.Condition.RecoveryExpireFrame, RecoveryFrames, ctx.CurrentFrame);

        // RMB still held = forced exit (preemption) — don't fire.
        if (ctx.Input.RightClick) return;
        if (vars.ChargeTime < MinChargeToFire) return;
        if (ctx.Spawner == null) return;

        int budget = ComputeBudget(vars.ChargeTime);
        if (budget <= 0) return;

        // Capture cursor at release (player may have re-aimed during the charge).
        var target = ctx.Input.MouseWorldPosition;
        var spawnPos = ctx.Body.Position + Vector2.Normalize(target - ctx.Body.Position + new Vector2(1e-3f, 0f)) * SpawnOffset;
        var launchVel = ComputeBallisticLaunch(spawnPos, target);

        // Pick up the player's active block type for the eruption shape — same
        // material the BlockReady charge would have used.
        ctx.Spawner.SpawnEntity(new LobbedAreaProjectile(spawnPos, launchVel, budget, ctx.ActiveBlockType, ctx.EruptionMode, ctx.HitIds.Next(), ctx.Faction));
    }

    // Ballistic solve: given gravity g (from MovementConfig.Current.Gravity),
    // pick vy = -LaunchApexBoost (upward), then time-of-flight to reach the
    // target's Y under gravity, then vx = dx / t. Clamps t to a minimum so a
    // target right on top of the player doesn't divide by zero.
    private static Vector2 ComputeBallisticLaunch(Vector2 from, Vector2 to)
    {
        float g = Simulation.WorldGravityY;
        if (g <= 0f) g = 1f;
        Vector2 d = to - from;
        // Solve d.y = vy * t + 0.5 * g * t^2  with vy = -LaunchApexBoost.
        // → 0.5 g t^2 + (-LaunchApexBoost) t - d.y = 0.
        float a = 0.5f * g;
        float b = -LaunchApexBoost;
        float c = -d.Y;
        float disc = b * b - 4f * a * c;
        float t;
        if (disc < 0f)
        {
            // Target above max apex — fall back to a fixed time.
            t = 0.8f;
        }
        else
        {
            float sqrtDisc = MathF.Sqrt(disc);
            // Both roots positive when target below apex; pick the LATER one
            // (descending arc into target). When target above launch point,
            // there's one positive root — Max picks it.
            float t1 = (-b - sqrtDisc) / (2f * a);
            float t2 = (-b + sqrtDisc) / (2f * a);
            t = MathF.Max(t1, t2);
            if (t < 0.1f) t = 0.1f;
        }
        return new Vector2(d.X / t, -LaunchApexBoost);
    }

    private static int ComputeBudget(float chargeTime)
    {
        float raw;
        if (chargeTime < SaturationTime)
            raw = MathHelper.Lerp(BudgetMin, BudgetMax, chargeTime / SaturationTime);
        else
            raw = BudgetMax * DipFactor;
        return (int)MathF.Round(raw);
    }

    // Heavy stance while charging — same shape as BlockReady's.
    public override void ApplyMovementModifiers(ref MovementModifiers m, in ActionVars vars)
    {
        m.MaxWalkSpeed *= 0.4f;
        m.WalkAccel    *= 0.5f;
        m.MaxAirSpeed  *= 0.5f;
        m.AirAccel     *= 0.6f;
        m.GravityScale *= 0.5f;
    }

    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body, in ActionVars vars)
    {
        // Charge ring at the player. Color ramps from olive → goldenrod as the
        // budget grows; past saturation it dims to indicate the budget dip.
        bool saturated = vars.ChargeTime >= SaturationTime;
        float frac = saturated ? 1f : (vars.ChargeTime / SaturationTime);
        int r = (int)(2 + 8f * frac);
        Color col = saturated
            ? new Color(160, 120, 40)
            : Color.Lerp(new Color(80, 60, 20), Color.Goldenrod, frac);
        sb.Draw(pixel, new Rectangle((int)body.Position.X - r, (int)body.Position.Y - r, r * 2, r * 2), col * 0.55f);
    }
}

// ---------- Ranged: StickyGrenade (F key press) ---------------------------------

// Roadmap §4.4 — sticky-grenade throw. F press-edge spawns a grenade toward
// the cursor. Shift+RMB was the original roadmap binding but that gesture is
// now taken by LobbedAreaAction (charge + release for ranged eruption); F is
// the unambiguous fallback. No charging — single-tap throw at fixed velocity.
public class GrenadeAction : ActionState
{
    private const float Duration       = 0.15f;
    private const int   RecoveryFrames = 5;
    private const float SpawnOffset    = PlayerCharacter.Radius * 1.2f;

    public override int ActivePriority  => 40;
    public override int PassivePriority => 45;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (!ctx.Input.F) return false;
        var prev = ctx.Controller.GetPrevious(1);
        if (prev.F) return false;
        if (ctx.Combat?.BlocksAttack == true) return false;
        if (ab.Condition.RecoveryActive)    return false;
        return true;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
        => vars.TimeInState < Duration;

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState = 0f;
        if (ctx.Spawner == null) return;
        Vector2 toCursor = ctx.Input.MouseWorldPosition - ctx.Body.Position;
        Vector2 dir = toCursor.LengthSquared() < 1e-4f
            ? new Vector2(ab.Facing == 0 ? 1f : ab.Facing, 0f)
            : Vector2.Normalize(toCursor);
        var spawnPos = ctx.Body.Position + dir * SpawnOffset;
        ctx.Spawner.SpawnEntity(new StickyGrenadeProjectile(spawnPos, dir, ctx.HitIds.Next(), ctx.Faction));
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState += ctx.Dt;
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        ConditionState.SetFor(ref ab.Condition.RecoveryActive, ref ab.Condition.RecoveryExpireFrame, RecoveryFrames, ctx.CurrentFrame);
    }
}

