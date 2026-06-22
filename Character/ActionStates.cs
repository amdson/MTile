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

    // Nominal lifetime of one activation, in seconds, for the animation layer ONLY
    // (render-only — never read by the sim). The action overlay clip is time-remapped
    // to span exactly [0, OverlayDuration] so it plays through once as the action runs,
    // independent of how long the authored clip's own timeline is. 0 = no fixed length;
    // the animator falls back to the clip's own Duration. See CharacterAnimSample.
    public virtual float OverlayDuration => 0f;
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

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        // Hold-field continuation (COMBAT_FEEL_PLAN Phase 2): the stateless field
        // dies with its publishing state, so the gap between a hold-slash and its
        // combo follow-up would drop the victim. Recovery is the live state during
        // that gap — while a combo window from a holding slash is open, keep
        // broadcasting a weaker pull. vars.SlashDir survives from the slash's
        // activation (RecoveryAction never writes vars), so the field stays aimed.
        if ((ab.Condition.Slash2Ready || ab.Condition.Slash3Ready)
            && vars.SlashDir != Vector2.Zero)
            SlashLikeAction.PublishHoldField(ctx, vars.SlashDir,
                SlashLikeAction.HoldFieldBaseRadius, strengthScale: 0.6f);
    }
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
    // Internal (not private) so RecoveryAction can publish the hold-field
    // continuation at the same geometry.
    internal const float BaseArcRadius        = PlayerCharacter.Radius * 1.5f * 1.75f;
    internal const float HoldFieldBaseRadius  = BaseArcRadius;

    // Hold-field tuning (COMBAT_FEEL_PLAN Phase 2). Variants with HoldVictims=true
    // broadcast a ForceField each frame of the slash that servo-pulls enemies
    // toward a focus in front of the attacker — keeping them in range for the next
    // slice instead of knocking them out of it. Stateless: the field is re-published
    // per frame and dies with the action (see ForceField). MaxAccel is the escape
    // valve — strong enough to beat hitstun-muted control, weak enough that a jump
    // or launch tears free.
    private const float HoldFieldTargetSpeed  = 160f;   // px/s toward the focus
    private const float HoldFieldMaxAccel     = 4000f;  // px/s² servo clamp
    private const float HoldFieldFocusDist    = 0.7f;   // focus at this × radius along SlashDir
    private const float HoldFieldRegionScale  = 1.4f;   // region half-size = this × radius
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
    // Override to set the next-stage flag + recovery duration. Durations are
    // authored in seconds (SetForSeconds) — `dt` is the step rate to convert at.
    // `connected` is the Phase 3 hit-confirm: true iff this slash landed on an
    // entity. Combo openers gate their follow-up flag on it (whiffed pokes don't
    // chain); finishers/one-shots ignore it and just schedule recovery.
    protected abstract void    OnExitSetFlags(ConditionState cond, int currentFrame, float dt, bool connected);
    // AirTurnSlash overrides this to true so a click behind the player gives a
    // genuine backward slash instead of a perpendicular one (roadmap §1.6).
    protected virtual  bool    AllowBackward       => false;
    // Hitstun override in seconds (< 0 ⇒ derive from impulse). Hold-slashes carry
    // a tiny impulse (they pull, not push) so they declare their hitstun explicitly.
    protected virtual  float   HitstunSecondsOverride => -1f;
    // When true, the slash broadcasts a holding ForceField each frame (see the
    // HoldField* constants above). Ground combo openers (S1/S2) hold; finishers
    // and pokes don't.
    protected virtual  bool    HoldVictims         => false;
    // When > 0, this slash erodes a grabber's grab strength instead of dealing the
    // usual knockback / hitstun (the struggle channel — see Hitbox.GrabStrengthDamage).
    // Only GrabbedSlash overrides this; every normal slash hits normally.
    protected virtual  float   GrabStrengthDamage  => 0f;
    // -----------------------------------------------------------------------

    protected float ArcRadius => BaseArcRadius * ArcRadiusScale;

    private readonly Trail _trail = new(TrailCapacity, TrailLifetime);

    // Render-only accessors so a glow pass (Game1) can render the slash apex as a glowing
    // shape + trail instead of the flat ribbon. The trail is the swept apex history.
    public Trail SlashTrail     => _trail;
    public Color SlashGlowColor => SlashColor;

    public override int ActivePriority  => 30;
    public override int PassivePriority => 30;

    // The slash lives for [0, Duration]; the overlay clip is remapped onto that so it
    // sweeps once over the swing regardless of the authored clip's own timeline length.
    public override float OverlayDuration => Duration;

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
        vars.TimeInState     = 0f;
        vars.SlashDir        = ComputeSlashDir(ctx, ab);
        vars.HitId           = ctx.HitIds.Next();
        vars.AttackConnected = false;
        _trail.Clear();
        ctx.Intents.Consume(IntentType.Click, ctx.CurrentFrame);
        OnEnterClearFlags(ab.Condition);
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
        => OnExitSetFlags(ab.Condition, ctx.CurrentFrame, ctx.Dt, vars.AttackConnected);

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

        // Hit-confirm latch (Phase 3): poll the prior frame's connection count for
        // this HitId. Entity hits are deduped per target, so the count is non-zero
        // only on the frame after a fresh connection — latch it so OnExitSetFlags
        // (which fires a few frames later, after the active window) can gate combos.
        if (!vars.AttackConnected && ctx.CombatSystem != null
            && ctx.CombatSystem.PeekHits(vars.HitId) > 0)
            vars.AttackConnected = true;

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
                ctx.Faction, ctx.SelfId, SlashColor,
                hitstunSecondsOverride: HitstunSecondsOverride,
                grabStrengthDamage: GrabStrengthDamage));
        }

        // Holding slashes broadcast their pull field for the WHOLE slash (not just
        // the damage window) so a victim clipped early in the arc is still held
        // through the follow-through. Re-published every frame; see ForceField.
        if (HoldVictims)
            PublishHoldField(ctx, vars.SlashDir, ArcRadius, strengthScale: 1f);
    }

    // Shared by the slash Update (full strength) and RecoveryAction's combo-gap
    // continuation (weaker). Focus sits in front of the attacker along `dir`;
    // the region is wide enough to cover the arc's reach so anything the slash
    // can touch is also held.
    internal static void PublishHoldField(EnvironmentContext ctx, Vector2 dir, float radius, float strengthScale)
    {
        if (ctx.ForceFields == null) return;
        var focus = ctx.Body.Position + dir * (radius * HoldFieldFocusDist);
        float r = radius * HoldFieldRegionScale;
        ctx.ForceFields.Publish(new ForceField(
            new BoundingBox(focus.X - r, focus.Y - r, focus.X + r, focus.Y + r),
            focus,
            HoldFieldTargetSpeed * strengthScale,
            HoldFieldMaxAccel   * strengthScale,
            ctx.Faction, ctx.SelfId));
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

    // The slash apex is rendered as a glowing triangle + trail by Game1's glow pass
    // (GlowRenderer), which needs its own PrimitiveBatch pass outside this SpriteBatch
    // block. SlashTrail/SlashGlowColor expose what it needs; nothing to draw here.
    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body, in ActionVars vars) { }
}

// ---------- Ground combo: S1 → S2 → S3 -----------------------------------------

// Opening ground slash. Wide CCW sweep, red. Holds rather than launches
// (COMBAT_FEEL_PLAN Phase 2): the knockback is a light tap and the slash
// broadcasts a holding field pulling the victim into S2's reach — the combo
// finisher (S3) is where the launch lives. Real hitstun comes from the
// explicit override, not the (now tiny) impulse.
public class GroundSlash1 : SlashLikeAction
{
    // Slashes are fast — Duration tuned so the active damage window is ~2 frames at 30 fps.
    // Variants scale around this baseline for combo-feel variety.
    protected override float Duration            => 0.14f;
    protected override float ArcRadiusScale      => 1.0f;
    protected override float SweepAngleDeg       => 100f;
    protected override float SweepDirection      => +1f;
    protected override float KnockbackMagnitude  => 60f;     // was 200 — hold, don't shove
    protected override Color SlashColor          => Color.Red;
    protected override bool  RequireGround       => true;
    protected override bool  RequireAir          => false;
    protected override float HitstunSecondsOverride => 0.30f;
    protected override bool  HoldVictims         => true;
    protected override void OnExitSetFlags(ConditionState c, int f, float dt, bool connected)
    {
        // Hit-confirm (`connected`) is tracked but intentionally does NOT gate the
        // chain right now — the S2 window opens whether or not S1 landed. To make
        // the combo hit-confirmed (Phase 3 whiff-punish), wrap the Slash2Ready set in
        // `if (connected)`.
        ConditionState.SetForSeconds(ref c.Slash2Ready, ref c.Slash2ExpireFrame, 1.0f, f, dt);
        ConditionState.SetForSeconds(ref c.RecoveryActive, ref c.RecoveryExpireFrame,  0.1f, f, dt);
    }
}

// Combo step 2 — mirror-handedness sweep, slightly faster, slightly harder hit.
public class GroundSlash2 : SlashLikeAction
{
    protected override float Duration            => 0.13f;
    protected override float ArcRadiusScale      => 1.05f;
    protected override float SweepAngleDeg       => 110f;
    protected override float SweepDirection      => -1f;
    protected override float KnockbackMagnitude  => 80f;     // was 260 — still holding
    protected override Color SlashColor          => Color.Red;
    protected override bool  RequireGround       => true;
    protected override bool  RequireAir          => false;
    protected override float HitstunSecondsOverride => 0.30f;
    protected override bool  HoldVictims         => true;

    // Combo moves preempt Recovery via higher passive priority.
    public override int PassivePriority => 50;

    protected override bool CombosOk(ConditionState c) => c.Slash2Ready;
    protected override void OnEnterClearFlags(ConditionState c)
    {
        c.Slash2Ready    = false;
        c.RecoveryActive = false;
    }
    protected override void OnExitSetFlags(ConditionState c, int f, float dt, bool connected)
    {
        // `connected` tracked but not gating — see GroundSlash1.OnExitSetFlags.
        ConditionState.SetForSeconds(ref c.Slash3Ready, ref c.Slash3ExpireFrame, 1.0f, f, dt);
        ConditionState.SetForSeconds(ref c.RecoveryActive, ref c.RecoveryExpireFrame,  0.1f, f, dt);
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
    protected override void OnExitSetFlags(ConditionState c, int f, float dt, bool connected)
    {
        // End of chain — no further combo flag.
        ConditionState.SetForSeconds(ref c.RecoveryActive, ref c.RecoveryExpireFrame, 0.167f, f, dt);
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
    protected override void OnExitSetFlags(ConditionState c, int f, float dt, bool connected)
    {
        ConditionState.SetForSeconds(ref c.RecoveryActive, ref c.RecoveryExpireFrame, 0.167f, f, dt);
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
    protected override void OnExitSetFlags(ConditionState c, int f, float dt, bool connected)
    {
        // `connected` tracked but not gating — see GroundSlash1.OnExitSetFlags.
        ConditionState.SetForSeconds(ref c.AirSlash2Ready, ref c.AirSlash2ExpireFrame, 1.0f, f, dt);
        ConditionState.SetForSeconds(ref c.RecoveryActive, ref c.RecoveryExpireFrame,  0.1f, f, dt);
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
    protected override void OnExitSetFlags(ConditionState c, int f, float dt, bool connected)
    {
        ConditionState.SetForSeconds(ref c.RecoveryActive, ref c.RecoveryExpireFrame, 0.133f, f, dt);
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

    protected override void OnExitSetFlags(ConditionState c, int f, float dt, bool connected)
    {
        // No combo follow-up — turn-around is one-and-done. Short recovery.
        ConditionState.SetForSeconds(ref c.RecoveryActive, ref c.RecoveryExpireFrame, 0.133f, f, dt);
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
    // Startup bumped 0.12 → 0.18 s (≈11 frames at 60 fps) as part of the Phase 3
    // commitment spectrum: the stab is now a launcher (3× knockback below), so it
    // earns a real wind-up — whiffing it is punishable, landing it is a kill move.
    private const float HurtboxStartTime      = 0.18f;
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
    // Phase 3 power spectrum: 3× the old 380. At Mass 2.5 that's ~456 px/s of
    // launch — well over the 350 stun threshold, so a clean stab launches AND
    // stuns (→ TumbleState in the air). The payoff that justifies the longer
    // startup + recovery and the commitment of the long swipe gesture.
    private const float KnockbackMagnitude    = 1140f;
    private const float DamagePerFrame        = TileDamage.TileMaxHP / 4f;
    // Recoil per connecting entity/cell, scaled against KnockbackImpulse and
    // negated when applied to the attacker. BreakProtected ⇒ cells that the
    // stab destroys this frame don't contribute (so ploughing through sand /
    // dirt remains thrust-positive). Survivors (e.g. stone tiles) do — pogo.
    // Tuned at 1.0: a single connecting stone cell delivers -380 px/s of
    // recoil per active frame, easily overwhelming the lunge (+90) and ground
    // friction, so the stab clearly pogos off hard surfaces.
    private const float RecoilScale           = 0.0f;
    // Hardness floor: sand (MaxHP 0.5) doesn't pogo even on its first contact
    // frame (when BreakProtected can't yet save us — sand takes 2 hits to
    // break). Dirt (1.0) and stone (2.0) still pogo.
    private const float RecoilMinMaterialHP   = 0.5f;

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

    // Render-only accessors so Game1's glow pass can render the stab tip as a glowing
    // sphere + trail instead of the flat ribbon. Color depends on grounded-ness.
    public Trail TipTrail => _tipTrail;
    public Color StabColorFor(bool grounded) => ColorFor(grounded);

    public override int ActivePriority  => 30;
    public override int PassivePriority => 30;

    // Remap the overlay clip onto the stab's [0, Duration] so the authored thrust sweeps
    // once over the swing — windup/strike/hold/retract stay synced to the hitbox windows.
    public override float OverlayDuration => Duration;

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
        ConditionState.SetForSeconds(ref ab.Condition.RecoveryActive,
                              ref ab.Condition.RecoveryExpireFrame, 0.3f, ctx.CurrentFrame, ctx.Dt);
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
        // Lunge first (the "ensure ≥ LungeSpeed along stab" assist), then recoil.
        // Order matters: recoil after lunge means a hard surface's back-impulse
        // can actually flip Vx negative (pogo); recoil before lunge would let
        // the lunge re-positivize Vx and erase the pogo entirely.
        if (vars.IsGrounded && vars.TimeInState >= LungeStart && vars.TimeInState <= LungeEnd)
        {
            var v = ctx.Body.Velocity;
            float velAlongStab = Vector2.Dot(v, vars.StabDir);
            if (velAlongStab < LungeSpeed)
                ctx.Body.Velocity = v + vars.StabDir * (LungeSpeed - velAlongStab);
        }

        // Newton's-third-law recoil from last frame's connecting hits. Read once
        // per frame; applied as an instantaneous Δv (body has no mass, impulse
        // and Δv coincide). BreakProtected on the primary box means only cells
        // that survived the hit / entities that were struck contribute, so
        // ploughing through sand stays thrust-positive while a stab into stone
        // bounces the player off. Runs regardless of grounded/lunge windows so
        // air-stab pogo (the canonical pogo case) also fires.
        if (ctx.CombatSystem != null)
        {
            var recoil = ctx.CombatSystem.PeekRecoil(vars.HitId);
            if (recoil != Vector2.Zero) ctx.Body.Velocity += recoil;
        }
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
                ctx.Faction, ctx.SelfId, ColorFor(vars.IsGrounded),
                shape: PrimaryPoly, shapePos: primaryCenter, shapeRotation: rotation,
                recoilScale: RecoilScale, recoilBreakProtected: true,
                recoilMinMaterialHP: RecoilMinMaterialHP));

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
                ctx.Faction, ctx.SelfId,
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

    // The stab tip is rendered as a glowing sphere + trail by Game1's glow pass
    // (GlowRenderer), which needs its own PrimitiveBatch pass outside this SpriteBatch
    // block. TipTrail/StabColorFor expose what it needs; nothing to draw here.
    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body, in ActionVars vars) { }
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

    protected override void OnExitSetFlags(ConditionState c, int f, float dt, bool connected)
    {
        ConditionState.SetForSeconds(ref c.RecoveryActive, ref c.RecoveryExpireFrame, 0.1f, f, dt);
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
        ConditionState.SetForSeconds(ref ab.Condition.RecoveryActive,
                              ref ab.Condition.RecoveryExpireFrame, 0.4f, ctx.CurrentFrame, ctx.Dt);
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
                ctx.Faction, ctx.SelfId, color));
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

// The RMB ground-editing action. One state owns the whole right-button gesture:
//   • Drag-to-build — every cell the cursor sweeps through (within reach) is fed to
//     TryRequestTile, so a held RMB paints tiles. Folded in from what used to be
//     Simulation.HandleBuildInput; living inside the FSM means building can't run
//     concurrently with an attack or with the eruption it charges into.
//   • Charge — accumulates while the cursor CAN'T place (over a wall); decays while it
//     is actively placing. So painting tiles keeps the charge near zero, while holding
//     over solid terrain builds it toward an eruption.
//   • Arm — an in→out sweep (cursor leaving solid) past MinChargeToArm hands off to
//     BlockEruptionAction, which fires on release.
public class BlockReadyAction : ActionState
{
    // Minimum hold-in-solid time before exiting-out-of-solid arms the eruption.
    // Below this, BlockReady just keeps painting tiles (the charge stays near zero
    // while placing), so the player can tap RMB on a block and start building without
    // committing to an eruption charge.
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

    // Drag-to-build reach (px) from the player center; a sprout/solid neighbour on the
    // target cell extends it so a build can chain outward. Moved here verbatim from the
    // old Simulation.HandleBuildInput.
    private const float BuildReach         = 64f;
    private const float ChainBuildReachMul = 2f;
    // The heavy "committed" stance only kicks in once the charge has actually started
    // building (over a wall). While painting tiles the charge stays below this, so
    // ordinary drag-building leaves movement unencumbered.
    private const float StanceChargeFloor  = 0.15f;

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
        // Once the charge passes the arm threshold the player is committed to an
        // eruption, not painting — so building stops. That keeps the in→out sweep that
        // arms the eruption from also drag-placing a stray tile at the exit cell (the
        // bug the old before-the-FSM HandleBuildInput had). Below the threshold, paint
        // freely.
        bool committed = vars.ChargeTime >= MinChargeToArm;

        // Drag-to-build: paint tiles along the cursor sweep and learn whether we
        // actually committed any this frame.
        bool placedThisFrame = !committed && TryDragPlace(ctx);

        // Charge grows when nothing was placed this frame; decays at the same rate when
        // we just committed a tile. A held RMB that's actively building stays near zero,
        // while a held RMB over a wall (no placement possible) accumulates normally.
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
        vars.CursorPosition = ctx.Input.MouseWorldPosition;

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

    // Drag-to-build sweep (folded in from Simulation.HandleBuildInput). While RMB is
    // held, every cell the cursor passes through (within reach of the player center) is
    // requested as a tile. Returns true iff at least one tile was actually committed
    // this frame — the charge logic uses that to decide grow-vs-decay.
    private static bool TryDragPlace(EnvironmentContext ctx)
    {
        var input = ctx.Input;
        var prev  = ctx.Controller.GetPrevious(1);
        // On the press-edge frame prev.RightClick is false, so the segment collapses to
        // the current point (a single cell) rather than sweeping from a stale position.
        var segStart = prev.RightClick ? prev.MouseWorldPosition : input.MouseWorldPosition;
        var segEnd   = input.MouseWorldPosition;

        bool placed = false;
        foreach (var (gtx, gty) in MouseSweep.Cells(segStart, segEnd))
        {
            var cellCenter = BlockEruptionHelpers.CellCenter(gtx, gty);
            float maxReach = HasSproutNeighbour(ctx.Chunks, gtx, gty) ? BuildReach * ChainBuildReachMul : BuildReach;
            if (Vector2.DistanceSquared(ctx.Body.Position, cellCenter) > maxReach * maxReach)
                continue;
            if (ctx.Chunks.TryRequestTile(gtx, gty, ctx.ActiveBlockType) != null)
                placed = true;
        }
        return placed;
    }

    private static bool HasSproutNeighbour(ChunkMap chunks, int gtx, int gty) =>
        chunks.Graph.TryGet(gtx,     gty + 1, out _) ||
        chunks.Graph.TryGet(gtx - 1, gty,     out _) ||
        chunks.Graph.TryGet(gtx + 1, gty,     out _) ||
        chunks.Graph.TryGet(gtx,     gty - 1, out _);

    // Heavy stance — same shape as Stab's modifier set, on ground + air. Gated on the
    // charge actually having started (over a wall): while merely painting tiles the
    // charge stays below StanceChargeFloor, so ordinary drag-building stays nimble; the
    // commitment slow only bites once the player is genuinely charging an eruption.
    public override void ApplyMovementModifiers(ref MovementModifiers m, in ActionVars vars)
    {
        if (vars.ChargeTime < StanceChargeFloor) return;
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
            var p = vars.CursorPosition + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
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
    private const float BudgetMax      = 240f;
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
    private const float Duration        = 0.15f;
    private const float RecoverySeconds = 0.133f;
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
        ConditionState.SetForSeconds(ref ab.Condition.RecoveryActive, ref ab.Condition.RecoveryExpireFrame, RecoverySeconds, ctx.CurrentFrame, ctx.Dt);
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
    // Hard cap on reach in world pixels. The beam now marches in fixed StepSize
    // increments out to this length; the *effective* reach is usually shorter,
    // cut off wherever the energy model (below) decays past EnergyCutoff. Through
    // open air the beam lances the full length; boring into stone it dies in a
    // few cells. Extended well past the old 220px so it reads as a long lance.
    private const float MaxBeamLength    = 420f;
    private const float StepSize         = 14f;                              // world-px between sampled segments
    private const int   MaxSteps         = (int)(MaxBeamLength / StepSize) + 1;
    private const float SegmentHalfSize  = 6f;
    private const float DamagePerFrame   = TileDamage.TileMaxHP * 0.45f;   // full-energy damage; breaks Stone in 2-3 frames of overlap
    private const float KnockbackImpulse = 320f;
    private const float RecoverySeconds  = 0.2f;

    // --- Energy model (the "strength through blocks / air" math) ---------------
    // The beam carries a normalized energy that starts at 1.0 at the muzzle and is
    // multiplicatively attenuated each step. Damage + knockback delivered to a cell
    // scale with the energy ARRIVING at it (before that cell's own absorption), so a
    // tile shields whatever sits behind it. Air bleeds energy slowly (beam diffuses
    // over distance); solids bleed it hard, weighted by the material's durability so
    // stone chokes the beam far faster than sand.
    private const float AirRetentionPerStep  = 0.992f;  // ~0.79 over the full air run — stays strong
    private const float SolidRetentionBase   = 0.55f;   // per-step retention for a 1×TileMaxHP (Dirt) cell
    private const float EnergyCutoff         = 0.05f;   // below this the beam is spent; reach ends here

    // Per-step solid retention, tied to material durability so the falloff curve is
    // driven by the same numbers that set break HP. Stone (2.0) chokes hardest.
    private static float SolidRetention(TileType type)
        => MathF.Pow(SolidRetentionBase, TileDamage.MaxHPFor(type) / TileDamage.TileMaxHP);

    // Streaming-particle look: a handful of motes ride outward along the beam, each
    // dragging a fading Trail ribbon (Drawing/Trail.cs). They re-launch from the
    // muzzle on a staggered cycle so there's always a steady stream in flight.
    private const int   MoteCount    = 5;
    private const float MoteHz       = 12.25f;   // outward runs per second per mote
    private const int   MoteTrailCap = 12;
    private const float MoteTrailLife = 0.11f;

    // Render-only cache. The beam's live sim state (charge/firing timers, hitId,
    // locked BeamDir) lives in ActionVars; these only feed Draw and self-heal on the
    // next firing Update (Trails are advanced there, where ctx.Dt is available, just
    // like the cursor trail is ticked from Game1.Update), so they stay out of the
    // snapshot. See ActionVars header.
    private Vector2   _lastBeamDir   = Vector2.UnitX;
    private float     _lastBeamReach;
    private int       _segCount;
    private readonly Vector2[] _segPos    = new Vector2[MaxSteps];
    private readonly float[]   _segEnergy = new float[MaxSteps];
    private readonly Trail[]   _motes     = new Trail[MoteCount];
    private readonly int[]     _moteCycle = new int[MoteCount];   // last sweep index per mote; change ⇒ re-launch

    public BeamAction()
    {
        for (int m = 0; m < MoteCount; m++)
            _motes[m] = new Trail(MoteTrailCap, MoteTrailLife);
    }

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
        // Moving or jumping breaks concentration — the beam demands a planted stance.
        // Cancels during charge AND firing, so any L/R/Space input drops the beam.
        if (ctx.Input.Left || ctx.Input.Right || ctx.Input.Space) return false;
        if (vars.Firing && vars.FiringTime >= MaxFiringTime) return false;
        return true;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.ChargeTime = 0f;
        vars.FiringTime = 0f;
        vars.Firing     = false;
        vars.HitId      = ctx.HitIds.Next();
        // Drop any ribbons left over from a previous activation.
        for (int m = 0; m < MoteCount; m++)
        {
            _motes[m].Clear();
            _moteCycle[m] = int.MinValue;
        }
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        if (!vars.Firing)
        {
            vars.ChargeTime += ctx.Dt;
            if (vars.ChargeTime >= MinChargeTime)
            {
                vars.Firing = true;
                // Lock the aim the instant firing begins. The player aims freely
                // during the charge wind-up; once the beam lights it commits to a
                // fixed angle for the rest of the burst (the player can still walk,
                // so the muzzle origin tracks the body — only the direction sticks).
                var aim = ctx.Input.MouseWorldPosition - ctx.Body.Position;
                vars.BeamDir = aim.LengthSquared() > 1e-6f
                    ? Vector2.Normalize(aim)
                    : new Vector2(ab.Facing, 0f);
            }
            return;
        }
        vars.FiringTime += ctx.Dt;
        if (ctx.Hitboxes == null) return;

        // Beam emanates from the (moving) muzzle along the LOCKED direction.
        var start = ctx.Body.Position;
        var dir   = vars.BeamDir;
        if (dir.LengthSquared() < 1e-6f) return;

        // March outward in fixed StepSize cells, attenuating energy as we cross air
        // and solids. Each step publishes a hitbox whose damage scales with the
        // energy arriving there; we stop once the beam is spent (EnergyCutoff) so a
        // wall of stone visibly shortens the beam while open air lets it run long.
        //
        // HitTargets.All so each segment damages BOTH tiles and entities — that's
        // what carves tunnels while also hurting anything in the line of fire. The
        // shared HitId means CombatSystem's (HitId,Target) dedupe treats the whole
        // beam as ONE attack per entity, so chained segments don't multi-hit a body.
        float energy = 1f;
        int   count  = 0;
        for (int s = 0; s < MaxSteps; s++)
        {
            float dist   = (s + 0.5f) * StepSize;
            if (dist > MaxBeamLength) break;
            var   center = start + dir * dist;

            // Damage uses the energy ARRIVING at this cell (before its absorption).
            float arriving = energy;
            _segPos[count]    = center;
            _segEnergy[count] = arriving;
            count++;

            var region = new BoundingBox(
                center.X - SegmentHalfSize, center.Y - SegmentHalfSize,
                center.X + SegmentHalfSize, center.Y + SegmentHalfSize);
            ctx.Hitboxes.Publish(new Hitbox(
                region, vars.HitId, DamagePerFrame * arriving,
                dir * (KnockbackImpulse * arriving),
                ctx.Faction, ctx.SelfId, Color.Magenta));

            // Attenuate for the next step based on what THIS cell is made of.
            int gtx = (int)MathF.Floor(center.X / Chunk.TileSize);
            int gty = (int)MathF.Floor(center.Y / Chunk.TileSize);
            if (ctx.Chunks.GetCellState(gtx, gty) == TileState.Solid)
                energy *= SolidRetention(ctx.Chunks.GetCellType(gtx, gty));
            else
                energy *= AirRetentionPerStep;

            if (energy < EnergyCutoff) break;
        }

        _lastBeamDir   = dir;
        _lastBeamReach = count * StepSize;
        _segCount      = count;

        // Advance the streaming motes. Each rides a staggered phase from muzzle (f=0)
        // to tip (f=1); when its phase rolls over to a new cycle it re-launches from
        // the muzzle, so we Clear the ribbon to avoid a streak snapping back across
        // the beam. Tick-then-Push mirrors the cursor trail in Game1.Update.
        for (int m = 0; m < MoteCount; m++)
        {
            float phase = vars.FiringTime * MoteHz + (float)m / MoteCount;
            int   cycle = (int)MathF.Floor(phase);
            float f     = phase - cycle;
            if (cycle != _moteCycle[m])
            {
                _motes[m].Clear();
                _moteCycle[m] = cycle;
            }
            _motes[m].Tick(ctx.Dt);
            _motes[m].Push(start + dir * (_lastBeamReach * f));
        }
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        // Recovery on exit no matter how we left — a successful beam locks the
        // player out of follow-up Shift+LMB for a moment, an interrupted charge
        // does likewise (which is a feel-call; it punishes spamming).
        ConditionState.SetForSeconds(ref ab.Condition.RecoveryActive, ref ab.Condition.RecoveryExpireFrame, RecoverySeconds, ctx.CurrentFrame, ctx.Dt);
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
        if (_segCount <= 0) return;

        // Faint beam core: a thin dot at each marched step, dimmed/brightened by the
        // energy that reached it, so the taper toward the tip (and the abrupt end
        // where the beam bored into stone) still reads. Kept subtle — the streaming
        // ribbons below are the main event.
        var dimCol  = new Color(90, 0, 110);
        var coreCol = new Color(220, 140, 255);
        for (int i = 0; i < _segCount; i++)
        {
            float e   = _segEnergy[i];
            var   col = Color.Lerp(dimCol, coreCol, e) * 0.5f;
            int   r   = (int)(1f + 2f * e);
            var   p   = _segPos[i];
            sb.Draw(pixel, new Rectangle((int)p.X - r, (int)p.Y - r, r * 2, r * 2), col);
        }

        // Streaming particles: each mote's fading Trail ribbon, advanced in Update.
        // Newer (head) end is bright white-magenta; it tapers to transparent.
        var head = new Color(255, 220, 255);
        var tail = new Color(180, 40, 220, 0);
        for (int m = 0; m < MoteCount; m++)
            _motes[m].Draw(sb, pixel, head, tail, startWidth: 3.5f);
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
    private const float RecoverySeconds = 0.2f;
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
        ConditionState.SetForSeconds(ref ab.Condition.RecoveryActive, ref ab.Condition.RecoveryExpireFrame, RecoverySeconds, ctx.CurrentFrame, ctx.Dt);

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
    private const float RecoverySeconds = 0.167f;
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
        ConditionState.SetForSeconds(ref ab.Condition.RecoveryActive, ref ab.Condition.RecoveryExpireFrame, RecoverySeconds, ctx.CurrentFrame, ctx.Dt);
    }
}

// ---------- Grab — Shift + RMB: hold an opponent, then throw ---------------------
//
// COMBAT_FEEL_PLAN Phase 6: the grab completes the RPS triangle (grab beats guard,
// attack beats grab, guard beats attack). It's the Phase 2 hold-field turned up — a
// strong short-range ForceField in front of the grabber that flags whoever it holds
// `GrabbedActive` (so their normal attacks/jump gate off; only struggle attacks fire).
// It is stateless like every field: the "grab" persists only while this action keeps
// broadcasting. It IGNORES guard for free (a field never goes through the OnHit/parry
// path). Releasing RMB (or hitting the hold cap) flings the victim with a brief
// high-speed directional field — into terrain at high percent that's the Phase 5 KO.
//
// Grab-break is a strength contest: the hold starts at GrabStrengthMax, and each
// connecting struggle slash erodes it (the struggle hit deliberately deals no stun —
// see GrabbedSlash). CheckConditions releases the grab once GrabStrength hits 0, which
// clears the victim's GrabbedActive a couple frames later. A heavier hit on the grabber
// (real hitstun, e.g. a third party) still drops the hold immediately. A whiffed grab
// runs its hold→throw→recovery, so an opponent who reads it punishes the lag.
public class GrabAction : ActionState
{
    private const float GrabHoldMaxSeconds = 1.2f;    // auto-throw if held this long
    // Grab strength the hold starts with; each connecting struggle slash erodes it by
    // GrabbedSlash.GrabStrengthDamage (1.0), so a fresh grab survives 2 struggles and
    // breaks on the 3rd. Bump for a stickier grab, lower for an easier mash-out.
    private const float GrabStrengthMax    = 3f;
    private const float ThrowSeconds       = 0.12f;   // throw-field duration
    private const float RecoverySeconds    = 0.3f;    // lag after a grab (the whiff-punish window)
    private const float Range       = PlayerCharacter.Radius * 2.4f;   // field region half-size
    private const float FocusDist   = PlayerCharacter.Radius * 1.6f;   // hold focus in front of the grabber
    private const float PullSpeed   = 320f;
    private const float PullAccel   = 9000f;          // strong — overpowers the victim walking away
    private const float ThrowSpeed  = 520f;
    private const float ThrowAccel  = 12000f;

    public override int ActivePriority  => 46;   // above LobbedArea(45)/Guard(40), below GuardRetaliate(55)
    public override int PassivePriority => 46;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (!ctx.Input.Shift || !ctx.Input.RightClick) return false;
        if (ctx.Controller.GetPrevious(1).RightClick) return false;     // press-edge only
        if (ctx.Combat?.BlocksAttack == true) return false;             // not while stunned/grabbed
        if (ctx.Combat?.HitstunActive == true) return false;            // not while in hitstun
        if (ab.Condition.RecoveryActive) return false;
        return true;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        // Grab-break: a hard hit on the grabber (hitstun) drops the hold outright, and
        // the struggle attack wears the hold down — once the victim's struggles have
        // eroded GrabStrength to 0, the grab releases (the new primary break path).
        if (ctx.Combat?.HitstunActive == true) return false;
        if (ctx.Combat != null && ctx.Combat.GrabStrength <= 0f) return false;
        if (vars.GrabThrowing) return vars.ChargeTime < ThrowSeconds;
        return true;   // hold phase persists; Update transitions to the throw
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState  = 0f;
        vars.ChargeTime   = 0f;
        vars.GrabThrowing = false;
        vars.GrabDir      = AimDir(ctx, ab);
        if (ctx.Combat != null) ctx.Combat.GrabStrength = GrabStrengthMax;
    }

    private static Vector2 AimDir(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        int facing = ab.Facing == 0 ? 1 : ab.Facing;
        Vector2 raw = ctx.Input.MouseWorldPosition - ctx.Body.Position;
        return raw.LengthSquared() > 1e-2f ? Vector2.Normalize(raw) : new Vector2(facing, 0f);
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        int facing = ab.Facing == 0 ? 1 : ab.Facing;
        if (!vars.GrabThrowing)
        {
            vars.TimeInState += ctx.Dt;
            bool holding = ctx.Input.RightClick && vars.TimeInState < GrabHoldMaxSeconds;
            if (holding)
            {
                var focus = ctx.Body.Position + new Vector2(facing, 0f) * FocusDist;
                if (ctx.ForceFields != null)
                    ctx.ForceFields.Publish(new ForceField(
                        new BoundingBox(focus.X - Range, focus.Y - Range, focus.X + Range, focus.Y + Range),
                        focus, PullSpeed, PullAccel, ctx.Faction, ctx.SelfId, Color.Magenta, isGrab: true));
                return;
            }
            // Release or hold-cap → enter the throw.
            vars.GrabThrowing = true;
            vars.ChargeTime   = 0f;
            vars.GrabDir      = AimDir(ctx, ab);
        }

        // Throw phase: a brief high-speed directional field flings whoever's still
        // held-adjacent along GrabDir (no IsGrab — the victim is released, not held).
        vars.ChargeTime += ctx.Dt;
        if (ctx.ForceFields != null)
        {
            // Region hugs the held position; focus is far down GrabDir so the servo
            // drives the victim to ThrowSpeed away from the grabber.
            var hold  = ctx.Body.Position + vars.GrabDir * FocusDist;
            var focus = ctx.Body.Position + vars.GrabDir * 400f;
            ctx.ForceFields.Publish(new ForceField(
                new BoundingBox(hold.X - Range, hold.Y - Range, hold.X + Range, hold.Y + Range),
                focus, ThrowSpeed, ThrowAccel, ctx.Faction, ctx.SelfId, Color.HotPink,
                isGrab: false, isThrow: true));
        }
    }

    // Heavy stance while grabbing — the grabber is committed.
    public override void ApplyMovementModifiers(ref MovementModifiers m, in ActionVars vars)
    {
        m.MaxWalkSpeed *= 0.4f;
        m.WalkAccel    *= 0.5f;
        m.MaxAirSpeed  *= 0.6f;
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        ConditionState.SetForSeconds(ref ab.Condition.RecoveryActive,
            ref ab.Condition.RecoveryExpireFrame, RecoverySeconds, ctx.CurrentFrame, ctx.Dt);
    }

    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body, in ActionVars vars)
    {
        int facing = vars.GrabDir.X >= 0f ? 1 : -1;
        var focus = body.Position + (vars.GrabThrowing ? vars.GrabDir : new Vector2(facing, 0f)) * FocusDist;
        var color = vars.GrabThrowing ? Color.HotPink : Color.Magenta;
        sb.Draw(pixel, new Rectangle((int)focus.X - 3, (int)focus.Y - 3, 6, 6), color * 0.8f);
    }
}

// ---------- Struggle: the one attack a grabbed player can throw -------------------
//
// COMBAT_FEEL_PLAN Phase 6. A slash exempt from the BlocksAttack gate that grabs impose
// — it requires GrabbedActive and skips the gate the normal slashes obey. It's
// short-range (the grab holds you adjacent) and does NOT stun or knock back the grabber:
// instead each connecting hit erodes the grabber's GrabStrength (GrabStrengthDamage),
// and the grab releases once that reaches 0. Stunning the grabber would let the victim
// trade out of every grab and unbalanced the exchange, so the struggle just wears the
// hold down. Its startup is the grabber's window to throw first: a prompt throw beats a
// struggle, a greedy hold eats it.
public class GrabbedSlash : SlashLikeAction
{
    protected override float Duration            => 0.16f;
    protected override float ArcRadiusScale      => 0.9f;     // short — held adjacent
    protected override float SweepAngleDeg       => 80f;
    protected override float SweepDirection      => +1f;
    protected override float KnockbackMagnitude  => 0f;       // no knockback — erodes grab strength instead
    protected override Color SlashColor          => Color.Yellow;
    protected override bool  RequireGround       => false;
    protected override bool  RequireAir          => false;
    // The struggle channel: each connecting hit removes this much grab strength from
    // the grabber (GrabStrengthMax 3 ⇒ breaks on the 3rd). No hitstun is dealt.
    protected override float GrabStrengthDamage  => 1f;

    // Beats NullAction; no combo. Normal slashes are gated off while grabbed, so this
    // is the only attack available.
    public override int PassivePriority => 36;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        // EXEMPT from the BlocksAttack gate (which a grab raises) — that's the whole
        // point. Requires being grabbed + a click intent.
        if (ctx.Combat?.GrabbedActive != true) return false;
        if (!ctx.Intents.Peek(IntentType.Click, ctx.CurrentFrame, out _)) return false;
        return true;
    }

    protected override void OnExitSetFlags(ConditionState c, int f, float dt, bool connected)
        => ConditionState.SetForSeconds(ref c.RecoveryActive, ref c.RecoveryExpireFrame, 0.15f, f, dt);
}

