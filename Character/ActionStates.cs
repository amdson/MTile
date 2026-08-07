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

    // How far through this activation we are, normalized [0,1], for the animation layer ONLY
    // (render-only — never read by the sim). The overlay clip is remapped onto it, so the
    // authored pose sweeps once over the action regardless of how long the action actually
    // runs or how long the clip's own timeline is. This is the action-side mirror of
    // MovementState.AnimationProgress.
    //
    // **Return negative to decline.** The animator then plays the clip at its own authored
    // seconds — which is the right answer for a HELD, open-ended action (Guard runs as long as
    // the button is down; its clip loops). Everything with a definite lifetime should report,
    // and a fixed-length action is simply `vars.TimeInState / Duration`. Reporting is what keeps
    // the overlay honest when the action's real length is variable (Grab's early throw) or
    // differs from the clip's (Beam outlives its 0.6s clip).
    public virtual float AnimationProgress(in ActionVars vars) => -1f;

    // The world AIM direction of an input-parametrized action (a stab's StabDir), exposed to the
    // animation layer so it can re-aim the authored (horizontal) overlay pose along the actual
    // input direction. Render-only, same contract as AnimationProgress — derived from ActionVars,
    // never read by the sim. Default none. The animator owns WHICH bones re-aim (see CharacterAnimator).
    public virtual bool TryAnimationAim(in ActionVars vars, out Vector2 dir) { dir = default; return false; }
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
        // broadcasting a weaker pull. vars.AttackDir survives from the slash's
        // activation (RecoveryAction never writes vars), so the field stays aimed.
        if ((ab.Condition.Slash2Ready || ab.Condition.Slash3Ready)
            && vars.AttackDir != Vector2.Zero)
            SlashLikeAction.PublishHoldField(ctx, vars.AttackDir,
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


    // Collision-mode strike parameters (HIT_MOMENTUM_PLAN). Slashes deliberately
    // strike LIGHTER than the player's body mass (2.5): they're quick arm arcs,
    // not full-body thrusts like the stab — so equal-or-heavier targets shrug
    // more and light targets still ping. MinLaunch keeps repeat juggle hits
    // alive: a ball already flying away faster than the swing (u ≤ 0) still
    // visibly pops instead of a dead connect.
    private   const   float    SlashStrikeMass     = 2.5f;
    private   const   float    SlashRestitution    = 0.5f;
    private   const   float    SlashMinLaunch      = 180f;

    // ----- per-variant knobs ------------------------------------------------
    protected abstract float   Duration            { get; }   // seconds
    protected abstract float   ArcRadiusScale      { get; }   // multiplier on BaseArcRadius
    protected abstract float   SweepAngleDeg       { get; }   // total sweep (90, 150, …)
    protected abstract float   SweepDirection      { get; }   // +1 CCW, -1 CW (mirror)
    protected abstract float   KnockbackMagnitude  { get; }
    // Collision-mode strike speed (px/s), stacked on the attacker's velocity at
    // publish. 0 (default) ⇒ the variant publishes legacy Impulse mode: the
    // hold-slashes (S1/S2) keep their designed tap so the hold field isn't
    // fighting a launch, and GrabbedSlash stays out of momentum entirely.
    // Launcher parity mapping from the old impulse numbers: vs a mass-1 target
    // the impulse Δv was KnockbackMagnitude; collision Δv = (1+e)·μ·u ≈ 0.75·u
    // at strike mass 1 ⇒ StrikeSpeed ≈ 1.33 × old magnitude.
    protected virtual  float   StrikeSpeed         => 1f;
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
    public override float AnimationProgress(in ActionVars vars) => vars.TimeInState / Duration;

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
        vars.AttackDir        = ComputeSlashDir(ctx, ab);
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
            var apex = ctx.Body.Position + vars.AttackDir * ArcRadius;
            var region = new BoundingBox(
                apex.X - ArcRadius * 0.5f, apex.Y - ArcRadius * 0.5f,
                apex.X + ArcRadius * 0.5f, apex.Y + ArcRadius * 0.5f);
            // Launcher variants (StrikeSpeed > 0) publish Collision mode; the
            // strike fields are ignored under Impulse, so one call covers both.
            // KnockbackImpulse stays authored either way — parry cone, bullet
            // deflect, and the OnHit early-outs read it as the swing direction.
            ctx.Hitboxes.Publish(new Hitbox(
                region, vars.HitId, SlashDamagePerFrame,
                vars.AttackDir * KnockbackMagnitude,
                ctx.Faction, ctx.SelfId, SlashColor,
                hitstunSecondsOverride: HitstunSecondsOverride,
                grabStrengthDamage: GrabStrengthDamage,
                mode: StrikeSpeed > 0f ? KnockbackMode.Collision : KnockbackMode.Impulse,
                strikeDir: vars.AttackDir,
                strikeVelocity: ctx.Body.Velocity + vars.AttackDir * StrikeSpeed,
                strikeMass: SlashStrikeMass,
                restitution: SlashRestitution,
                minLaunch: SlashMinLaunch));
        }

        // Holding slashes broadcast their pull field for the WHOLE slash (not just
        // the damage window) so a victim clipped early in the arc is still held
        // through the follow-through. Re-published every frame; see ForceField.
        if (HoldVictims)
            PublishHoldField(ctx, vars.AttackDir, ArcRadius, strengthScale: 1f);
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
            vars.AttackDir.X * cos - vars.AttackDir.Y * sin,
            vars.AttackDir.X * sin + vars.AttackDir.Y * cos);
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
    protected override float StrikeSpeed         => 500f;    // launcher — 1.33 × the old 380
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
    protected override float StrikeSpeed         => 320f;
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
    protected override float StrikeSpeed         => 240f;
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
    protected override float StrikeSpeed         => 375f;
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
    protected override float StrikeSpeed         => 320f;
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
    // Active window spans the strike + hold of the visual curve (TipExtension): the
    // box opens as the tip starts whipping forward out of the wind-up, SWEEPS OUTWARD
    // with the tip (each box's length tracks vars.TipExt below) through the strike,
    // then dwells at full reach through the early hold. In normalized state-time that's
    // ≈ 0.18–0.55 of Duration (WindupEnd → mid-hold). The hold tail matters now that
    // the boxes grow: the far cells are only covered late in the sweep, so the window
    // has to stay open long enough (≥ TileMaxHP/DamagePerFrame frames at full reach)
    // for them to break — otherwise the proportional box would dig only the near cells.
    // Startup bumped 0.12 → 0.18 s (≈11 frames at 60 fps) as part of the Phase 3
    // commitment spectrum: the stab is now a launcher (3× knockback below), so it
    // earns a real wind-up — whiffing it is punishable, landing it is a kill move.
    // (Entities are HitId-deduped, so the longer window doesn't multi-hit them; it
    // only gives tiles more frames to break and makes a point-blank connect easier.)
    private const float HurtboxStartTime      = 0.18f;
    private const float HurtboxActiveDuration = 0.37f;

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
    // Tile-shockwave box — wider than the entity-hitbox (digs a channel rather than a
    // thin slot) and extended past the visible tip by BlockReachFactor, so it ploughs
    // a little deeper than the entity reach. Both boxes' LENGTHS now track the live tip
    // extension (vars.TipExt) per frame rather than springing to a fixed reach, so the
    // dig propagates outward in sync with the thrust instead of detonating at once.
    private const float BlockHalfWidth        = PlayerCharacter.Radius * 0.9f * 1.75f;
    // Block tip overshoot past the primary (entity) tip — "35% deeper". Was an absolute
    // BlockReach ≈ 1.82× Reach; now relative to the live tip so it sweeps in lock-step.
    private const float BlockReachFactor      = 1.35f;
    // Floor on each box's length during the active window so a point-blank stab still
    // connects while the tip is still near the body (vars.TipExt dips slightly negative
    // at the wind-up tail) and the per-frame polygons never go degenerate.
    private const float MinHitLength          = PlayerCharacter.Radius * 2f;
    // Direction-hint magnitude only (HIT_MOMENTUM_PLAN): the stab is the first
    // Collision-mode move, so momentum comes from StrikeSpeed below, not this.
    // KnockbackImpulse stays authored because the parry cone (CombatState.TryParry)
    // and bullet deflection (BulletProjectile.OnHit) read it as the attack's
    // direction, and the parry/invuln early-outs echo it back as recoil.
    private const float KnockbackMagnitude    = 1140f;
    private const float DamagePerFrame        = TileDamage.TileMaxHP / 4f;
    // Collision-mode striker (HitResolver.Resolve): the stab is a virtual body
    // flying at StrikeSpeed along the thrust, on top of the attacker's real
    // velocity — so a dive stab genuinely hits (and pogos) harder than a
    // standing poke. 650 keeps parity with the old tuned PvP launch: a
    // stationary player target takes (1+e)·u/2 ≈ 488 px/s ≈ the old
    // 1140/2.5 = 456, and Strength = u clears the 440 stun threshold, so a
    // clean stab still launches AND stuns (→ Tumble). (First pass used 950 —
    // 1.56× the old player launch, way too hot.)
    private const float StrikeSpeed           = 650f;
    // Entity-collision restitution + minimum visible launch for a connect on a
    // fleeing target.
    private const float Restitution           = 0.5f;
    private const float MinLaunch             = 120f;
    // Tile recoil (HitResolver.TileRecoil): bounce = (1+e_material)·approach·RecoilScale,
    // fired ONCE PER ATTACK (CombatSystem latches on the dedupe set) against the
    // bounciest surviving surface, floored at MinRecoilSpeed. Tuned so the
    // DEFAULT stab-into-ground pogo is exactly a jump's worth of upward momentum
    // (full-hop launch ≈ -100 initial + -1500·0.12 hold ≈ 280 px/s): a standing
    // stab into stone computes 1.7·650·0.2 ≈ 221 and rides the 280 floor. Speed
    // earns more — a 400 px/s dive → 1.7·1050·0.2 ≈ 357, terminal ~700 → ≈ 459.
    // BreakProtected ⇒ cells the stab destroys don't contribute (ploughing
    // through sand / dirt stays thrust-positive); survivors (stone) pogo.
    private const float RecoilScale           = 0.2f;
    private const float MinRecoilSpeed        = 280f;
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
    public override float AnimationProgress(in ActionVars vars) => vars.TimeInState / Duration;

    // The stab's live aim (captured at commit, steered toward the cursor within MaxTotalSteer).
    // The animator rotates the authored horizontal thrust onto this so an up/diagonal stab reads.
    public override bool TryAnimationAim(in ActionVars vars, out Vector2 dir)
    { dir = vars.StabDir; return dir.LengthSquared() > 1e-6f; }

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

        // Air-stab dive boost: project velocity onto the captured stab direction at the
        // instant of commit. Ground stab + negative projection (velocity opposite to
        // stab) collapse to MinBoost; high-speed aligned dives saturate at MaxBoost.
        // Boost feeds the per-frame publish below: damage × boost on both boxes, and the
        // block box's length × boost / width × √boost so a clean dive digs deeper and a
        // bit wider without ballooning into a giant rectangle.
        if (vars.IsGrounded)
        {
            vars.Boost = 1f;
        }
        else
        {
            float velAlongStab = MathF.Max(0f, Vector2.Dot(vars.StabDir, ctx.Body.Velocity));
            float t = MathHelper.Clamp(velAlongStab / BoostReferenceSpeed, 0f, 1f);
            vars.Boost = MathHelper.Lerp(MinBoost, MaxBoost, t);
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

            // Each box grows from the body out to the LIVE tip (vars.TipExt, the same
            // curve that drives the visible thrust + glow), floored so a point-blank
            // connect still lands and the polygon never degenerates. A box of length L
            // centered at Body + dir*(L/2) spans Body → Body + dir*L, so its leading
            // edge sits exactly on the tip and the dig carves outward in sync with the
            // animation instead of detonating the whole reach at once. Built per frame
            // (varying length) instead of from a cached static polygon.
            float primaryLen = MathHelper.Clamp(vars.TipExt, MinHitLength, Reach);
            float blockLen   = MathF.Max(vars.TipExt * BlockReachFactor, MinHitLength) * vars.Boost;
            float blockHalfW = BlockHalfWidth * MathF.Sqrt(vars.Boost);

            // Primary thrust — entity + tile damage along the thrust line, body → tip.
            var primaryPoly   = Polygon.CreateRectangle(primaryLen, PrimaryHalfWidth * 2f);
            var primaryCenter = ctx.Body.Position + vars.StabDir * (primaryLen * 0.5f);
            var primaryAABB   = primaryPoly.GetBoundingBox(primaryCenter, rotation);
            ctx.Hitboxes.Publish(new Hitbox(
                primaryAABB, vars.HitId, dmg,
                vars.StabDir * KnockbackMagnitude,
                ctx.Faction, ctx.SelfId, ColorFor(vars.IsGrounded),
                shape: primaryPoly, shapePos: primaryCenter, shapeRotation: rotation,
                recoilScale: RecoilScale, recoilBreakProtected: true,
                recoilMinMaterialHP: RecoilMinMaterialHP,
                mode: KnockbackMode.Collision,
                strikeDir: vars.StabDir,
                strikeVelocity: ctx.Body.Velocity + vars.StabDir * StrikeSpeed,
                strikeMass: ctx.Mass,
                restitution: Restitution,
                minLaunch: MinLaunch,
                minRecoilSpeed: MinRecoilSpeed));

            // Block-shockwave — same HitId so entities that overlap both count once. No
            // knockback (knockback comes from the primary box). Tiles only — passes
            // cleanly past entities along the thrust axis. Tracks the tip like the primary
            // box but BlockReachFactor longer and boost-scaled in length×boost / width×√boost,
            // so a well-aligned air dive digs a substantially deeper + slightly wider channel.
            var blockPoly   = Polygon.CreateRectangle(blockLen, blockHalfW * 2f);
            var blockCenter = ctx.Body.Position + vars.StabDir * (blockLen * 0.5f);
            var blockAABB   = blockPoly.GetBoundingBox(blockCenter, rotation);
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
                shape: blockPoly, shapePos: blockCenter, shapeRotation: rotation));
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
        if (ctx.Input.RightClick)    return false;            // Shift+RMB is the build gesture
        if (ctx.Combat?.BlocksAttack == true) return false;
        if (ab.Condition.RecoveryActive)       return false;
        return true;
    }

    // Guard yields to Shift+RMB in both directions. Without this, Guard's Passive 40
    // buries BlockReadyAction (Passive 10) — which is capped low on purpose so an attack
    // can always cancel a charge — and the eruption gesture could never start. Building
    // isn't a guard stance, so declining is the honest reading rather than a workaround.
    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        if (!ctx.Input.Shift) return false;
        if (ctx.Input.Left || ctx.Input.Right) return false;
        if (ctx.Input.RightClick) return false;
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
    protected override float StrikeSpeed         => 560f;
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

    public override float AnimationProgress(in ActionVars vars) => vars.TimeInState / Duration;

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

// ---------- Force Burst — RMB while holding a Ready wind-up ---------------------

// The "shove" out of a wind-up: hold LMB (→ ReadyAction) and click RMB to detonate a
// burst in front of the body. Design intent is displacement, not damage —
// it clears terrain and throws whatever it touches, but barely moves the percent
// meter, so it reads as a movement/space-making tool rather than a kill move.
//
// Two hitboxes per segment, sharing one HitId (same trick as StabAction):
//   • TilesOnly    — full TileMaxHP per frame, so dirt breaks on a single shell pass.
//   • EntitiesOnly — tiny percent contribution, big Impulse knockback.
// One box can't do both: Hitbox.Damage feeds the tile HP pool and the entity percent
// pool alike, so "breaks blocks but tickles players" needs the split.
//
// Priority 30/30 matches the other attacks. Passive 30 clears ReadyAction's Active 10,
// which is what lets it preempt the wind-up; the precondition below is what keeps it
// from stealing RMB from the build/eruption gesture (Passive 10) outside a wind-up.
public class BurstAction : ActionState
{
    private const float Duration             = 0.42f;
    // Fast detonation: the shell sweeps out over ~0.2s, well short of Pulse's 0.4s.
    private const float HitboxStartTime      = 0.06f;
    private const float HitboxActiveDuration = 0.22f;
    // 20 segments at EndRadius keeps the shell gap-free: spacing at r = 6R is
    // 2π·6R/20 ≈ 1.9R, just under each segment's 2R width.
    private const int   Segments             = 5;
    private const float StartDist          = PlayerCharacter.Radius * 1.5f;
    private const float EndDist            = PlayerCharacter.Radius * 2.5f;
    private const float SegmentHalfSize      = PlayerCharacter.Radius * 1.0f;
    // Hard shove — above GroundSlash3's 380 launch, since knockback is the whole point.
    private const float KnockbackMagnitude   = 700f;
    // Full tile HP per frame ⇒ dirt (1.0) breaks on one shell contact, stone (2.0)
    // needs the two frames the shell dwells over a cell.
    private const float TileDamagePerFrame   = TileDamage.TileMaxHP;
    // ~30% of a slash's percent contribution: enough to register a hit, not enough
    // to build a kill. Entities are HitId-deduped, so this lands exactly once.
    private const float EntityDamage         = 0.15f;
    // Declared rather than derived: the impulse is huge but the hit is meant to
    // displace, not to lock the victim down for a follow-up.
    private const float HitstunSeconds       = 0.20f;

    public override int ActivePriority  => 30;
    public override int PassivePriority => 30;

    public override float AnimationProgress(in ActionVars vars) => vars.TimeInState / Duration;

    private static Color BurstColorFor(bool grounded) => grounded ? Color.Orange : Color.MediumTurquoise;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        // Must be mid-wind-up with LMB still down. PreviousAction(0) is last frame's
        // settled action, so this needs one frame of Ready first — LMB-then-RMB in
        // the same frame just fires the wind-up, and the burst lands the frame after.
        if (ctx.PreviousAction(0) is not ReadyAction) return false;
        if (!ctx.Input.LeftClick) return false;
        // RMB press-edge only, so a held right button (drag-build) doesn't re-fire.
        if (!ctx.Input.RightClick) return false;
        if (ctx.Controller == null || ctx.Controller.GetPrevious(1).RightClick) return false;
        if (ctx.Combat?.BlocksAttack == true) return false;
        return true;
    }

        private Vector2 ComputeBurstDir(EnvironmentContext ctx, PlayerAbilityState ab)
        {
            int facing = ab.Facing == 0 ? 1 : ab.Facing;
            Vector2 raw = ctx.Input.MouseWorldPosition - ctx.Body.Position;
            if (raw.X * facing < 0f) raw.X = 0f;
            if (raw.LengthSquared() < 1e-4f) return new Vector2(facing, 0f);
            return Vector2.Normalize(raw);
        }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
        => vars.TimeInState < Duration;

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState = 0f;
        vars.IsGrounded  = ctx.TryGetGround(out _);
        vars.HitId       = ctx.HitIds.Next();
        vars.AttackDir   = ComputeBurstDir(ctx, ab); 
        // Eat the wind-up's press so releasing LMB after the burst can't also
        // spend the gesture on a slash.
        ctx.Intents.Consume(IntentType.PressEdge, ctx.CurrentFrame);
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        ConditionState.SetForSeconds(ref ab.Condition.RecoveryActive,
                              ref ab.Condition.RecoveryExpireFrame, 0.30f, ctx.CurrentFrame, ctx.Dt);
    }

    // Planted stance while the shell goes out — the player is bracing against the
    // shove, not steering through it.
    public override void ApplyMovementModifiers(ref MovementModifiers m, in ActionVars vars)
    {
        m.MaxWalkSpeed   *= 0.3f;
        m.WalkAccel      *= 0.5f;
        m.GroundFriction *= 1.5f;
        m.MaxAirSpeed    *= 0.3f;
        m.AirDrag        *= 1.5f;
        m.GravityScale   *= 0.4f;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState += ctx.Dt;

        if (vars.TimeInState < HitboxStartTime ||
            vars.TimeInState > HitboxStartTime + HitboxActiveDuration ||
            ctx.Hitboxes == null) return;

        float r = BurstDist(vars.TimeInState);
        // Anchored to the CURRENT body position, like Pulse — the shell rides with
        // the caster instead of hanging at the detonation point.
        var anchor  = ctx.Body.Position + PlayerCharacter.Radius * vars.AttackDir;
        var bodyVel = ctx.Body.Velocity;
        var color   = BurstColorFor(vars.IsGrounded);
        var tileColor = Color.Lerp(color, Color.Gray, 0.4f);
        var angleSpread = (float) 0.07 * MathHelper.TwoPi; 
        var anchorTheta = MathF.Atan2(vars.AttackDir.Y, vars.AttackDir.X);

        for (int i = 0; i < Segments; i++)
        {
            float angle = i * 2 * angleSpread / Segments - angleSpread + anchorTheta;
            var dir    = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            var center = anchor + dir * r;
            var region = new BoundingBox(
                center.X - SegmentHalfSize, center.Y - SegmentHalfSize,
                center.X + SegmentHalfSize, center.Y + SegmentHalfSize);

            // Terrain channel — breaks blocks, imparts nothing.
            ctx.Hitboxes.Publish(new Hitbox(
                region, vars.HitId, TileDamagePerFrame,
                Vector2.Zero,
                ctx.Faction, ctx.SelfId, tileColor,
                HitTargets.TilesOnly));

            // Knockback channel — radial shove plus the caster's momentum. Impulse
            // mode (not Collision): this is an AoE field, so every target in the
            // shell should get the same push regardless of closing speed.
            ctx.Hitboxes.Publish(new Hitbox(
                region, vars.HitId, EntityDamage,
                dir * KnockbackMagnitude + bodyVel,
                ctx.Faction, ctx.SelfId, color,
                HitTargets.EntitiesOnly,
                hitstunSecondsOverride: HitstunSeconds));
        }
    }

    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body, in ActionVars vars)
    {
        if (vars.TimeInState < HitboxStartTime ||
            vars.TimeInState > HitboxStartTime + HitboxActiveDuration) return;
        float r = BurstDist(vars.TimeInState);
        var color = BurstColorFor(vars.IsGrounded);
        for (int i = 0; i < Segments; i++)
        {
            float angle = i * MathHelper.TwoPi / Segments;
            var pos = body.Position + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * r;
            sb.Draw(pixel, new Rectangle((int)pos.X - 2, (int)pos.Y - 2, 5, 5), color);
        }
    }

    // Shell radius at state-time `t`. Eased out (1-(1-u)²) so the burst leaves the
    // body fast and decelerates at the rim — reads as a detonation rather than the
    // linear ring sweep Pulse uses.
    private static float BurstDist(float t)
    {
        float u = MathHelper.Clamp((t - HitboxStartTime) / HitboxActiveDuration, 0f, 1f);
        float eased = 1f - (1f - u) * (1f - u);
        return MathHelper.Lerp(StartDist, EndDist, eased);
    }
}


// ---------- Block Paint — plain RMB: paint outside terrain, charge inside ---------

// Plain RMB does two jobs, and which one depends entirely on where the cursor is:
//   cursor OUTSIDE solid — paint. A ball trails the cursor with inertia, leaking mass
//     into the cell underneath; TileMassField's cascade turns it into sprouts. Because
//     the ball lags and mass spills, a stroke lays down a mound that thickens where you
//     slow down instead of a 1-cell ribbon.
//   cursor INSIDE solid  — charge. Nothing is placed; BuildMeters converts reservoir into
//     eruption charge (see BuildMeters.StepCharging).
//
// That in-solid/out-of-solid split is doing more work than it looks. It's what keeps the
// two gestures from colliding: painting only happens in open space, charging only inside
// terrain, so they're mutually exclusive by construction rather than by a modifier key.
// It is ALSO the eruption's discriminator. An eruption arms only on the conjunction of
//   (a) real charge banked   — you spent time biting into terrain,
//   (b) a fast solid→air exit — the flick, not a drift,
//   (c) release soon after   — BlockEruptionAction's short window.
// Ordinary painting satisfies none of (a): the cursor never went inside anything, so
// there's no charge and a release is just a release. That's the answer to "don't erupt
// every time I let go while painting."
//
// Critically damped, so the ball never crosses the cursor — overshoot would deposit on
// the wrong side of the stroke, which reads as the build fighting your aim.
public class BlockPaintAction : ActionState
{
    // Ball lag time constant. Long enough to see the trail bend behind a fast stroke,
    // short enough that the deposit still lands where you're pointing.
    private const float SmoothTime    = 0.12f;
    // Baseline demand, in tiles/sec — what a parked cursor pours into one cell. The METER
    // is the real limiter for expensive material (stone lands ~4/sec); this caps how fast
    // cheap material can spray, and is what the painter's feel was tuned against before
    // costs existed.
    private const float TilesPerSecond = 12f;
    // Mass laid down per cell of ball travel, in tile-equivalents. Demand scales with
    // ball speed so a stroke's line density is speed-invariant — a fast flick spends
    // more per second instead of smearing mass too thin for any cell to reach the
    // sprout threshold (1.0). Above threshold so painted cells solidify with spill to
    // spare.
    private const float MassPerCell    = 6f;
    // Build reach (px) from the player center; a sprout/solid neighbour on the target
    // cell extends it so a build can chain outward. Same numbers the old drag paint
    // used, carried over from Simulation.HandleBuildInput before it.
    // Internal because BlockBurstAction needs the same reach to decide whether a held
    // RMB is actually placing at the cursor — one constant so the two can't disagree.
    internal const float BuildReach   = 64f;
    private const float ChainReachMul = 2f;

    public override int ActivePriority  => 8;
    public override int PassivePriority => 10;

    // Unbounded hold → no meaningful progress fraction, so the clip must loop.
    public override float AnimationProgress(in ActionVars vars) => -1f;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        // Plain RMB, no press-edge requirement: this is the resting state of a held right
        // button, so it also resumes after an attack interrupted a stroke, and catches the
        // handoff back from BlockEruptionAction when its arming window lapses unspent.
        if (!ctx.Input.RightClick || ctx.Input.Shift) return false;
        return ctx.Combat?.HitstunActive != true;
    }

    // Alive while RMB is held. Deliberately not gated on Shift: tapping Shift midway
    // through a stroke shouldn't tear the ball away.
    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
        => ctx.Input.RightClick;

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState = 0f;
        vars.BallPos     = ctx.Input.MouseWorldPosition;   // seeded under the cursor
        vars.BallVel     = Vector2.Zero;
        // The mode is decided HERE and does not get re-derived per frame: cursor buried at
        // the press means this hold is a charge, open air means it's a paint stroke.
        vars.ChargeGesture    = BlockEruptionHelpers.IsCursorInSolid(ctx);
        vars.InSolidLastFrame = vars.ChargeGesture;
        ab.Condition.BlockEruptionArmed = false;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState += ctx.Dt;
        SmoothPen.CriticallyDampedStep(ref vars.BallPos, ref vars.BallVel,
                                       ctx.Input.MouseWorldPosition, SmoothTime, ctx.Dt);

        if (!vars.ChargeGesture) { Paint(ctx, ab, vars.BallPos, vars.BallVel.Length()); return; }

        bool inSolid = BlockEruptionHelpers.IsCursorInSolid(ctx);
        if (inSolid)
        {
            // Charging. Re-anchor the ignition point every frame, so after the flick it
            // holds the last solid cell visited.
            ab.Meters.ChargingRequested = true;
            var (cgtx, cgty) = BlockEruptionHelpers.CursorCell(ctx);
            vars.OriginCell = BlockEruptionHelpers.CellCenter(cgtx, cgty);
        }
        else if (vars.InSolidLastFrame)
        {
            // The cursor crossed solid→air. With enough banked charge that crossing arms
            // the eruption — the FSM picks the flag up on the next scan and
            // BlockEruptionAction takes over; a release inside its short arming window
            // fires, anything later lapses back here. No speed gate: the crossing itself
            // is the signal, the recency-of-release check lives in BlockEruptionAction.
            // (An earlier ball-velocity gate made arming nearly impossible by hand — the
            // damped ball is still slow on the frame a real flick exits the terrain.)
            //
            // An undercharged crossing is a fizzle, and rather than leave the button
            // doing nothing for the rest of the hold, the gesture demotes to painting.
            // The demotion is deliberately one-way. Charge→paint can't loop back, so the
            // self-triggering that a per-frame mode test had is impossible, and a stroke
            // that has become a paint stroke can never reach an eruption.
            if (ab.Meters.CanFireEruption)
            {
                ab.Condition.BlockEruptionArmed = true;
                ab.Condition.BlockChargeOrigin  = vars.OriginCell;
            }
            else vars.ChargeGesture = false;
        }
        vars.InSolidLastFrame = inSolid;
    }

    private static void Paint(EnvironmentContext ctx, PlayerAbilityState ab, Vector2 ballPos, float ballSpeed)
    {
        int gtx = (int)MathF.Floor(ballPos.X / Chunk.TileSize);
        int gty = (int)MathF.Floor(ballPos.Y / Chunk.TileSize);
        var cell = BlockEruptionHelpers.CellCenter(gtx, gty);

        // Reach is measured to the ball's cell, not the cursor — the ball is what's
        // actually depositing, so a stroke flicked out of range stops where it lags to.
        float maxReach = HasSproutNeighbour(ctx.Chunks, gtx, gty)
            ? BuildReach * ChainReachMul : BuildReach;
        if (Vector2.DistanceSquared(ctx.Body.Position, cell) > maxReach * maxReach) return;

        // Ask for the demand rate, get back what the meters could actually fund, and emit
        // exactly that. Paying at emission (rather than on commit) means mass that flows
        // into unsupported air and dies is still charged for — which is visible to the
        // player, since nothing appears.
        float rate = MathF.Max(TilesPerSecond, MassPerCell * ballSpeed / Chunk.TileSize);
        float want = rate * ctx.Dt;
        float paid = ab.Meters.SpendForTiles(want, ctx.ActiveBlockType);
        if (paid <= 0f) return;

        ctx.Chunks.Mass.Deposit(ctx.Chunks, gtx, gty, paid, ctx.ActiveBlockType);
    }

    internal static bool HasSproutNeighbour(ChunkMap chunks, int gtx, int gty) =>
        chunks.Graph.TryGet(gtx,     gty + 1, out _) ||
        chunks.Graph.TryGet(gtx - 1, gty,     out _) ||
        chunks.Graph.TryGet(gtx + 1, gty,     out _) ||
        chunks.Graph.TryGet(gtx,     gty - 1, out _);

    // Ordinary building stays nimble — no stance penalty. The heavy stance belongs to the
    // charge, which is the committed half of the gesture.
    public override void ApplyMovementModifiers(ref MovementModifiers m, in ActionVars vars)
    {
        m.MaxWalkSpeed *= 0.85f;
        m.MaxAirSpeed  *= 0.85f;
    }

    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body, in ActionVars vars)
    {
        // The ball, plus a bright core, so the lag behind the cursor is legible.
        sb.Draw(pixel, new Rectangle((int)vars.BallPos.X - 3, (int)vars.BallPos.Y - 3, 7, 7),
                new Color(230, 200, 140));
        sb.Draw(pixel, new Rectangle((int)vars.BallPos.X - 1, (int)vars.BallPos.Y - 1, 3, 3),
                Color.White);
    }
}

// ---------- Block Place — Shift+RMB, one deliberate block at a time --------------

// The precise counterpart to the painter: no ball, no cascade, no partial mass. One tile
// per cell, paid for in full, placed the moment the cursor enters a new cell. Use it to
// finish an edge the mound rounded off, or to spend the reservoir carefully instead of
// spraying it.
//
// Deliberately bypasses TileMassField entirely. Accumulating fractional mass is what
// gives the painter its organic shape; for single placement that same behaviour would be
// a bug (a tile that appears one frame after you clicked, one cell off where you aimed).
public class BlockPlaceAction : ActionState
{
    private const float Reach         = BlockPaintAction.BuildReach;
    private const float ChainReachMul = 2f;

    public override int ActivePriority  => 8;
    public override int PassivePriority => 10;

    public override float AnimationProgress(in ActionVars vars) => -1f;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (!ctx.Input.RightClick || !ctx.Input.Shift) return false;
        return ctx.Combat?.HitstunActive != true;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
        => ctx.Input.RightClick && ctx.Input.Shift;

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState = 0f;
        // Sentinel so the press frame itself places: no cell has been placed yet.
        vars.OriginCell = new Vector2(float.NaN, float.NaN);
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState += ctx.Dt;

        var (gtx, gty) = BlockEruptionHelpers.CursorCell(ctx);
        var cell = BlockEruptionHelpers.CellCenter(gtx, gty);
        // One placement per cell entered, so holding the button and dragging lays a
        // single-tile line rather than re-requesting the same cell every frame.
        if (cell == vars.OriginCell) return;

        float maxReach = BlockPaintAction.HasSproutNeighbour(ctx.Chunks, gtx, gty)
            ? Reach * ChainReachMul : Reach;
        if (Vector2.DistanceSquared(ctx.Body.Position, cell) > maxReach * maxReach) return;

        if (!ab.Meters.CanAfford(ctx.ActiveBlockType)) return;
        if (ctx.Chunks.TryRequestTile(gtx, gty, ctx.ActiveBlockType) == null) return;

        // Charge only for a placement that actually took.
        ab.Meters.SpendForTiles(1f, ctx.ActiveBlockType);
        vars.OriginCell = cell;
    }

    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body, in ActionVars vars)
    {
        // Nothing to draw — the placed tile is the feedback.
    }
}

// ---------- Block Burst — LMB while holding RMB over dead air -------------------

// The block-side twin of BurstAction: where that one is LMB-hold → RMB-click and shoves
// terrain away, this is RMB-hold → LMB-click and conjures terrain out of nothing — a
// plus-shaped puff of foam (cursor cell + its 4 neighbours) in open air. Foam is the
// right material for it: half of dirt's HP and it decays on its own, so a free
// mid-air platform is scaffolding rather than permanent level editing.
//
// Two things guard against stealing a press that RMB was already spending:
//   • Precondition — declines whenever the cursor cell is one the drag-build could
//     actually paint (in reach, free, touching support). That is exactly ChunkMap's
//     placement rule, so "not placing a block" is decided by the same predicate
//     BlockReadyAction paints with rather than by a guess.
//   • Phase gate — the RMB gesture must currently be BlockPaintAction (plain painting).
//     A Shift+RMB charge, an armed flag, or a running eruption all decline the click.
//     That covers the case the predicate can't see: cursor over air, nothing placeable
//     there, but RMB is mid-eruption.
//
// Priority can NOT do that job here: ReadyAction's Passive is 15, and the FSM scan takes
// the single highest-Passive candidate, so anything below 15 loses the LMB press-edge to
// the wind-up and never fires. Hence 30/30 (matching BurstAction and the other attacks)
// plus the explicit gate above.
public class BlockBurstAction : ActionState
{
    private const float Duration      = 0.26f;
    // Where the foam appears. Beyond BlockReadyAction.BuildReach on purpose — this is
    // the ranged option, so it stays useful past where drag-building gives out.
    private const float BurstReach    = Chunk.TileSize * 8f;
    private const float RecoverySeconds = 0.18f;
    // Mass dropped on the (force-sprouted) center cell. Four units is exactly one per
    // neighbour once the center forwards them, i.e. the plus. See Enter.
    private const float MassInjection = 4f * TileMassField.Threshold;

    public override int ActivePriority  => 30;
    public override int PassivePriority => 30;

    public override float AnimationProgress(in ActionVars vars) => vars.TimeInState / Duration;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        // The RMB gesture must be plain painting. Anything else — a Shift+RMB charge, an
        // armed or running eruption — owns the button, so keep hands off.
        if (ctx.PreviousAction(0) is not BlockPaintAction) return false;
        if (ab.Condition.BlockEruptionArmed) return false;
        // RMB held (not the press-edge — that frame belongs to BlockReady) + LMB press-edge.
        if (!ctx.Input.RightClick || !ctx.Input.LeftClick) return false;
        if (ctx.Controller == null || ctx.Controller.GetPrevious(1).LeftClick) return false;
        if (ctx.Combat?.BlocksAttack == true) return false;
        if (ctx.Combat?.HitstunActive == true) return false;
        if (ab.Condition.RecoveryActive) return false;

        var (gtx, gty) = BlockEruptionHelpers.CursorCell(ctx);
        var center = BlockEruptionHelpers.CellCenter(gtx, gty);
        float distSq = Vector2.DistanceSquared(ctx.Body.Position, center);
        if (distSq > BurstReach * BurstReach) return false;
        // Empty air only — a cursor in solid is the eruption gesture's territory.
        if (ctx.Chunks.GetCellState(gtx, gty) != TileState.Empty) return false;
        // If the drag-build could paint here, RMB is placing: leave the press alone.
        if (distSq <= BlockPaintAction.BuildReach * BlockPaintAction.BuildReach &&
            ctx.Chunks.CanRequestTile(gtx, gty)) return false;
        return true;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
        => vars.TimeInState < Duration;

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState = 0f;
        var (gtx, gty) = BlockEruptionHelpers.CursorCell(ctx);
        vars.OriginCell = BlockEruptionHelpers.CellCenter(gtx, gty);

        // The center goes in unsupported — ForceSprout is the only path that conjures
        // matter with nothing to build from, which is the whole point of this move. The
        // arms are then left to the mass cascade: injecting MassInjection units at the
        // (now occupied) center makes it forward one unit at a time to each neighbour,
        // and those commit off the center once they have a full unit. So the plus isn't
        // hardcoded — it's the shape four units of mass takes when it flows out of a
        // filled cell. Raise MassInjection and it grows a fatter blob for free.
        ctx.Chunks.ForceSprout(gtx, gty, TileType.Foam);
        ctx.Chunks.Mass.Deposit(ctx.Chunks, gtx, gty, MassInjection, TileType.Foam);

        // Spend the click so releasing LMB afterwards can't also buy a slash.
        ctx.Intents.Consume(IntentType.PressEdge, ctx.CurrentFrame);
        ctx.Intents.Consume(IntentType.Click, ctx.CurrentFrame);
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
        => vars.TimeInState += ctx.Dt;

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        ConditionState.SetForSeconds(ref ab.Condition.RecoveryActive,
                              ref ab.Condition.RecoveryExpireFrame, RecoverySeconds, ctx.CurrentFrame, ctx.Dt);
    }

    // Light planted stance — a flick of the wrist, not a commitment.
    public override void ApplyMovementModifiers(ref MovementModifiers m, in ActionVars vars)
    {
        m.MaxWalkSpeed *= 0.6f;
        m.WalkAccel    *= 0.7f;
        m.MaxAirSpeed  *= 0.7f;
    }

    // A plus of expanding foam-white brackets over the target cells — the placement is
    // already visible as sprouting tiles, so this just marks the shape that was chosen.
    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body, in ActionVars vars)
    {
        float u = MathHelper.Clamp(vars.TimeInState / Duration, 0f, 1f);
        var color = Color.Lerp(new Color(235, 245, 255), Color.Transparent, u);
        float half = Chunk.TileSize * 0.5f * (0.4f + 0.6f * u);
        Span<Vector2> cells = stackalloc Vector2[5]
        {
            vars.OriginCell,
            vars.OriginCell + new Vector2(0f, -Chunk.TileSize),
            vars.OriginCell + new Vector2(Chunk.TileSize, 0f),
            vars.OriginCell + new Vector2(0f, Chunk.TileSize),
            vars.OriginCell + new Vector2(-Chunk.TileSize, 0f),
        };
        foreach (var c in cells)
            sb.Draw(pixel, new Rectangle((int)(c.X - half), (int)(c.Y - half),
                                         (int)(half * 2f), (int)(half * 2f)), color);
    }
}

// ---------- Block Eruption — the primed window between flick and release ---------

// The third phase of the plain-RMB gesture. BlockPaintAction does the charging (cursor in
// solid) and the arming (fast exit from solid with charge banked); this state is the brief
// window that follows, in which releasing the button fires the eruption. Let the window
// lapse and it hands back to painting with the charge intact but unspent.
//
// Priority arrangement:
//   BlockPaint     Active 8,  Passive 10
//   BlockPlace     Active 8,  Passive 10
//   BlockEruption  Active 10, Passive 10  — Passive 10 > Paint's Active 8, so it takes
//     over when the flag flips; Active 10 is NOT less than Paint's Passive 10, so the
//     painter can't immediately steal it back (preemption needs strictly greater). Both
//     sit under ReadyAction.Active (10) with Passive ≤ 10 so an attack (Passive 15) can
//     always cancel out of a build or a charge.

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


public class BlockEruptionAction : ActionState
{
    // How long after the flick a release still counts as an eruption. Short on purpose:
    // it IS the "shortly after" half of the discriminator, and it is what sends an unspent
    // charge back to the painter instead of letting a release five seconds later surprise
    // the player with an eruption.
    private const float ArmingWindow   = 0.35f;

    // Ball lag while tethered. Same filter BlockPaintAction uses, so the eruption ball
    // and the paint ball handle identically — the only difference is what happens on
    // release.
    private const float SmoothTime     = 0.12f;

    public override int ActivePriority  => 10;
    public override int PassivePriority => 10;

    // No per-activation reference state. The whole gesture is (BallPos, BallVel) in
    // ActionVars, which snapshots with the struct copy — the PathSample history and the
    // EruptionGestureState deep-copy that used to live here went away with the planner.

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
        => ctx.Input.RightClick && ab.Condition.BlockEruptionArmed;

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
        => ctx.Input.RightClick && vars.TimeInState < ArmingWindow;

    public override float AnimationProgress(in ActionVars vars) => vars.TimeInState / ArmingWindow;

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        // Consume the armed flag + capture the charge/origin handoff.
        ab.Condition.BlockEruptionArmed = false;
        vars.Origin      = ab.Condition.BlockChargeOrigin;
        vars.TimeInState = 0f;

        // The ball starts at the ignition cell, not the cursor: the charge happened
        // inside the terrain, and the sweep out of it is what accelerates the ball. So
        // the tether immediately starts dragging it toward the cursor, and by release it
        // is moving in the direction the player swept.
        vars.BallPos = vars.Origin;
        vars.BallVel = Vector2.Zero;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState += ctx.Dt;
        SmoothPen.CriticallyDampedStep(ref vars.BallPos, ref vars.BallVel,
                                       ctx.Input.MouseWorldPosition, SmoothTime, ctx.Dt);
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        // Only fire when naturally exited (RMB released). A preempt by ReadyAction
        // / attack leaves RMB held and silently cancels the eruption.
        if (ctx.Input.RightClick) return;

        // Budget is whatever charge the meters banked, converted at the active material.s
        // cost — so a full charge is 60 stone or a great deal more foam.
        float mass = ab.Meters.ConsumeEruptionMass(ctx.ActiveBlockType);
        if (mass <= 0f || ctx.Spawner == null) return;

        // Cut the tether. The ball keeps the velocity the sweep gave it, so the eruption
        // carries past wherever the cursor happened to be at release — the property the
        // old planner faked by extrapolating its puller off the end of a recorded path.
        ctx.Spawner.SpawnEntity(new MassBall(
            vars.BallPos, vars.BallVel, mass, ctx.ActiveBlockType, ctx.Faction));
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

    // Visual: the ignition cell plus the tethered ball, so the player can see the ball
    // lagging behind the cursor and judge how much velocity the sweep has built. The old
    // breadcrumb trail and the mass-ball footprint preview are gone with the planner —
    // the released ball is visible in flight, so it previews itself.
    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body, in ActionVars vars)
    {
        sb.Draw(pixel, new Rectangle((int)vars.Origin.X - 2, (int)vars.Origin.Y - 2, 5, 5), Color.Gold);
        sb.Draw(pixel, new Rectangle((int)vars.BallPos.X - 4, (int)vars.BallPos.Y - 4, 9, 9),
                new Color(255, 170, 60));
        sb.Draw(pixel, new Rectangle((int)vars.BallPos.X - 1, (int)vars.BallPos.Y - 1, 3, 3), Color.White);
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

    public override float AnimationProgress(in ActionVars vars) => vars.TimeInState / Duration;

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

    // Charge then fire, both bounded — so the overlay spans the WHOLE activation rather
    // than running out partway through the burst (the authored clip is shorter than
    // MinChargeTime + MaxFiringTime). The split is time-proportional; retune it here if the
    // clip is ever authored with a deliberate wind-up/fire ratio.
    private const float ChargeShare = MinChargeTime / (MinChargeTime + MaxFiringTime);
    public override float AnimationProgress(in ActionVars vars)
        => vars.Firing
            ? ChargeShare + (1f - ChargeShare) * (vars.FiringTime / MaxFiringTime)
            : ChargeShare * (vars.ChargeTime / MinChargeTime);

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
    // Charge-tracking pose, same shape as BlockReadyAction: holds at full once saturated.
    // (Dormant — the registration is commented out in PlayerCharacter — but kept correct so
    // re-enabling the binding doesn't reintroduce an unpaced overlay.)
    public override float AnimationProgress(in ActionVars vars) => vars.ChargeTime / SaturationTime;

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
        ctx.Spawner.SpawnEntity(new LobbedAreaProjectile(spawnPos, launchVel, budget, ctx.ActiveBlockType, ctx.HitIds.Next(), ctx.Faction));
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

    public override float AnimationProgress(in ActionVars vars) => vars.TimeInState / Duration;

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

// ---------- Block Grab — Shift + LMB on terrain: peel blocks out, throw them -----

// The mirror of the RMB build/eruption gesture: instead of pushing terrain out of the
// world, this pulls it in. Shift+LMB press with the cursor on a solid cell within
// reach starts a grab; how blocks come free depends on MovementConfig.BlockPeelEnabled.
//
// PEEL MODE (BlockPeelEnabled, the live design): paint and pull are ONE phase, and the
// gaussian paint kernel is itself the mode switch. While the held cursor sweeps over
// terrain it deposits "tether" onto nearby solid cells (they join the grab group, cap
// PeelMemberBuffer.Capacity); because the cursor is near the group, the player→group
// spring — force superlinear in |mouse − group COM| — is slack. Sweep AWAY and the
// kernel stops reaching terrain while the spring ramps: the pull. Each frame the
// spring force is divided among members by tether share; that share erodes both the
// group→block tether (at zero the block drops from the group, staying in the world)
// and the block→world glue (weight(material) × (core + outward solid edges), the
// viscoelastic attachment). When the force beats the group's aggregate remaining glue,
// every member is broken out at once and collapses into the carried orb. Pull harder
// than PeelSpringMax and the spring SNAPS — the whole attempt cancels, nothing
// persists. Overpainting is therefore a hazard: every block added is more glue to
// beat, and too many makes the group unliftable outright. A small or free-hanging
// group (near-zero glue) pops in a single sweep-and-release.
//
// LEGACY MODE (flag off): press, then DRAG past a threshold — cells in a small radius
// around the PRESS site are destroyed outright in one frame. Kept as the A/B baseline.
// Either way the harvest becomes an orb carried in the hand, tinted with the material
// it came from.
//
// Two exits from the carry phase:
//   • Release LMB  → the orb is thrown at the cursor as a LobbedAreaProjectile whose
//                     budget is whatever blocks are left. It lands, erupts the stolen
//                     material back into the world, and shoves what's nearby.
//   • Keep holding → the orb dissipates linearly over DissipateSeconds; the throw
//                     budget bleeds with it, and at zero the action just ends. So
//                     carrying terrain has a cost and the grab can't be banked.
//
// The thrown payload deliberately reuses LobbedAreaProjectile (the deactivated
// Shift+RMB ranged eruption) rather than introducing a new projectile: it already
// carries budget/material/mode, snapshots them for rollback, and does exactly the
// "erupt on landing + radial shove" this wants. Its EruptionPlannerMode comes from
// ctx.EruptionMode, so the orb builds with whatever shape the player has selected.
//
// Priority 46/46, above Beam/EnergyBall's 40/45 — both also live on Shift+LMB, and
// this has to win the press frame AND still be holding the button when they'd
// otherwise fire on release. The cursor-in-solid gate is what keeps the three from
// fighting: on terrain you grab, off terrain you beam. Nothing preempts the carry
// (46 Active), which is intentional — the orb is a commitment.
public class BlockGrabAction : ActionState
{
    // Reach from body center, in tiles so it tracks Chunk.TileSize like the rest of
    // the terrain verbs (BlockReadyAction's BuildReach is the px-authored analogue).
    // Internal because GrabAction defers to this exact reach when deciding whether a
    // Shift+LMB press is aimed at terrain — one constant, so the two can't disagree.
    internal const float GrabReach      = Chunk.TileSize * 6f;
    // Cursor travel from the press point that counts as "a drag". Under this it's a
    // click, and the action lapses without taking anything.
    private const float DragThreshold   = Chunk.TileSize * 0.75f;
    // How long the press waits for that drag before giving up.
    private const float GrabWindow      = 0.60f;
    // Harvest radius around the press site. 1.6 tiles ⇒ the pressed cell plus its
    // immediate neighbours, ~9 blocks on open ground.
    private const float GrabRadiusTiles = 1.6f;
    // Carry budget bleeds to nothing over this long, then the action ends empty.
    private const float DissipateSeconds = 2.0f;
    private const float ThrowSpeed      = 620f;
    private const float RecoverySeconds = 0.20f;
    // Hand offset for the carried orb, along the aim direction.
    private const float HandDistance    = PlayerCharacter.Radius * 1.4f;
    // Orb draw radius at full charge; scales with the blocks still held.
    private const float OrbMaxRadius    = PlayerCharacter.Radius * 0.9f;
    // Width of the material tally in RipBlocks. TileType is a contiguous byte enum;
    // bump this when a value is added there.
    private const int   TileTypeCount   = 4;

    public override int ActivePriority  => 46;
    public override int PassivePriority => 46;

    // Only the carry phase has a meaningful arc, and its length is player-controlled,
    // so decline and let the overlay clip play at its authored rate.
    public override float AnimationProgress(in ActionVars vars) => -1f;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (!ctx.Input.Shift || !ctx.Input.LeftClick) return false;
        if (ctx.Controller == null || ctx.Controller.GetPrevious(1).LeftClick) return false;  // press-edge only
        if (ab.Condition.RecoveryActive) return false;
        // Terrain-only gesture: the cursor must be ON a block, and within arm's reach.
        if (!BlockEruptionHelpers.IsCursorInSolid(ctx)) return false;
        return (ctx.Input.MouseWorldPosition - ctx.Body.Position).LengthSquared() <= GrabReach * GrabReach;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        // Release always ends the state — Exit decides whether that release is a
        // throw (orb in hand) or nothing (nothing peeled free / orb already gone).
        if (!ctx.Input.LeftClick) return false;
        if (vars.OrbHeld) return RemainingBlocks(in vars) > 0;     // carrying until it bleeds out
        if (MovementConfig.Current.BlockPeelEnabled)
        {
            // A snapped spring kills the attempt outright; otherwise a live group
            // keeps the state open past the press window — the pull takes as long
            // as it takes.
            if (vars.PeelSnapped) return false;
            return vars.PeelCount > 0 || vars.TimeInState < GrabWindow;
        }
        return vars.TimeInState < GrabWindow;                      // still waiting for the drag
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState   = 0f;
        vars.ChargeTime    = 0f;
        vars.OrbHeld       = false;
        vars.OrbBlocks     = 0;
        vars.OrbType       = ctx.ActiveBlockType;
        vars.CursorAtPress = ctx.Input.MouseWorldPosition;
        vars.IsGrounded    = ctx.TryGetGround(out _);
        vars.GrabDir       = new Vector2(ab.Facing == 0 ? 1f : ab.Facing, 0f);
        vars.PeelCount     = 0;
        vars.PeelStrain    = 0f;
        vars.PeelSnapped   = false;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState += ctx.Dt;

        // Live aim, so the orb rides the hand on the cursor side and the throw
        // direction is already known to Draw (which has no cursor of its own).
        var aim = ctx.Input.MouseWorldPosition - ctx.Body.Position;
        if (aim.LengthSquared() > 1e-4f) vars.GrabDir = Vector2.Normalize(aim);

        if (!vars.OrbHeld)
        {
            if (MovementConfig.Current.BlockPeelEnabled)
            {
                UpdatePeel(ctx, ref vars);
                return;
            }
            // Legacy drag-rip. The harvest is centered on CursorAtPress, not the
            // current cursor — the player marks the site with the press and the
            // drag is just the commit gesture, so a fast flick can't smear the dig.
            var travel = ctx.Input.MouseWorldPosition - vars.CursorAtPress;
            if (travel.LengthSquared() >= DragThreshold * DragThreshold)
                RipBlocks(ctx, ref vars);
            return;
        }

        vars.ChargeTime += ctx.Dt;   // carry clock — drives the dissipation
    }

    // One frame of the paint/pull phase. Order is fixed and load-bearing for
    // determinism: prune → paint → spring → wear → compact → break-out, with every
    // scan in ascending index / row-major cell order.
    private static void UpdatePeel(EnvironmentContext ctx, ref ActionVars vars)
    {
        if (ctx.Chunks == null) return;
        var cfg = MovementConfig.Current;

        // 1. Cells broken out from under us (another player, decay) leave the group.
        for (int i = vars.PeelCount - 1; i >= 0; i--)
            if (ctx.Chunks.GetCellState(vars.PeelMembers[i].Gtx, vars.PeelMembers[i].Gty) != TileState.Solid)
                RemovePeelMember(ref vars, i);

        // 2. Paint: deposit tether on solid cells under the kernel.
        PaintTether(ctx, ref vars, cfg);

        if (vars.PeelCount == 0) { vars.PeelStrain = 0f; return; }

        // 3. The player→group spring, superlinear in cursor distance from the COM.
        var com = Vector2.Zero;
        for (int i = 0; i < vars.PeelCount; i++)
            com += CellCenter(vars.PeelMembers[i].Gtx, vars.PeelMembers[i].Gty);
        com /= vars.PeelCount;

        float dist  = (ctx.Input.MouseWorldPosition - com).Length();
        float force = cfg.PeelSpringCoeff * MathF.Pow(dist / Chunk.TileSize, cfg.PeelSpringPower);
        vars.PeelStrain = Math.Clamp(force / MathF.Max(1e-3f, cfg.PeelSpringMax), 0f, 1f);

        if (force > cfg.PeelSpringMax)
        {
            // Pulled harder than the grip holds: the spring snaps and the whole
            // attempt dies. Nothing persists — glue wear resets with the group.
            vars.PeelSnapped = true;
            vars.PeelCount   = 0;
            return;
        }

        // 4. Divide the force among members by tether share; each share erodes that
        // member's tether AND its world glue.
        float tetherSum = 0f;
        for (int i = 0; i < vars.PeelCount; i++) tetherSum += vars.PeelMembers[i].Tether;
        if (tetherSum <= 1e-6f) return;

        for (int i = 0; i < vars.PeelCount; i++)
        {
            ref var m = ref vars.PeelMembers[i];
            float share = force * m.Tether / tetherSum;
            m.Tether   -= cfg.PeelTetherWear * share * ctx.Dt;
            m.GlueWear += cfg.PeelGlueWear  * share * ctx.Dt;
        }

        // 5. Members whose tether wore through drop off — the block stays in the world.
        for (int i = vars.PeelCount - 1; i >= 0; i--)
            if (vars.PeelMembers[i].Tether <= 0f)
                RemovePeelMember(ref vars, i);
        if (vars.PeelCount == 0) return;

        // 6. Aggregate remaining glue of the survivors vs the pull. Glue base is
        // recomputed live (neighbors join the group / get broken), floored so an
        // oversized group stays unliftable no matter how long it's worked.
        float glueTotal = 0f;
        for (int i = 0; i < vars.PeelCount; i++)
        {
            ref var m = ref vars.PeelMembers[i];
            float baseGlue = BaseGlue(ctx, in vars, m.Gtx, m.Gty, cfg);
            glueTotal += MathF.Max(baseGlue * cfg.PeelGlueFloor, baseGlue - m.GlueWear);
        }

        if (force >= glueTotal)
            BreakOutGroup(ctx, ref vars);
    }

    // Gaussian deposit around the cursor. Admission and accumulation share the kernel:
    // a cell is admitted when the kernel weight over it reaches PeelJoinThreshold (a
    // real pass, not a graze at the skirt), and every member under the kernel keeps
    // accumulating — "time spent over the block, weighted by a fast-die-off kernel".
    // Cells beyond GrabReach of the body never join (same arm's-reach rule as the
    // press gate), and a full buffer admits nobody: paint deliberately.
    private static void PaintTether(EnvironmentContext ctx, ref ActionVars vars, MovementConfig cfg)
    {
        var   cursor = ctx.Input.MouseWorldPosition;
        float sigma  = MathF.Max(1f, cfg.PeelKernelSigma);
        float extent = 2.5f * sigma;
        float inv2s2 = 1f / (2f * sigma * sigma);

        int cx   = (int)MathF.Floor(cursor.X / Chunk.TileSize);
        int cy   = (int)MathF.Floor(cursor.Y / Chunk.TileSize);
        int span = (int)MathF.Ceiling(extent / Chunk.TileSize);

        for (int dy = -span; dy <= span; dy++)
        for (int dx = -span; dx <= span; dx++)
        {
            int gtx = cx + dx, gty = cy + dy;
            if (ctx.Chunks.GetCellState(gtx, gty) != TileState.Solid) continue;

            var center = CellCenter(gtx, gty);
            float r2 = (center - cursor).LengthSquared();
            if (r2 > extent * extent) continue;
            if ((center - ctx.Body.Position).LengthSquared() > GrabReach * GrabReach) continue;

            float weight = MathF.Exp(-r2 * inv2s2);
            int idx = FindPeelMember(in vars, gtx, gty);
            if (idx < 0)
            {
                if (weight < cfg.PeelJoinThreshold) continue;        // skirt graze — no admission
                if (vars.PeelCount >= PeelMemberBuffer.Capacity) continue;
                idx = vars.PeelCount++;
                vars.PeelMembers[idx] = new PeelMember { Gtx = gtx, Gty = gty };
            }
            vars.PeelMembers[idx].Tether += cfg.PeelTetherRate * weight * ctx.Dt;
        }
    }

    // Block→world attachment: weight(material) × (core + Σ outward edges), where an
    // outward edge is a solid neighbor OUTSIDE the group — 1 for same material,
    // PeelCrossMaterialEdge for different. Edges into the group don't anchor (the
    // group moves as one), which is what makes painting a block's neighbors loosen it.
    private static float BaseGlue(EnvironmentContext ctx, in ActionVars vars, int gtx, int gty, MovementConfig cfg)
    {
        var  myType = ctx.Chunks.GetCellType(gtx, gty);
        float edges = 0f;
        Span<int> nx = stackalloc int[4] { gtx, gtx + 1, gtx, gtx - 1 };
        Span<int> ny = stackalloc int[4] { gty - 1, gty, gty + 1, gty };
        for (int k = 0; k < 4; k++)
        {
            if (ctx.Chunks.GetCellState(nx[k], ny[k]) != TileState.Solid) continue;
            if (FindPeelMember(in vars, nx[k], ny[k]) >= 0) continue;
            edges += ctx.Chunks.GetCellType(nx[k], ny[k]) == myType ? 1f : cfg.PeelCrossMaterialEdge;
        }
        return cfg.PeelWeight(myType) * (cfg.PeelGlueCore + edges);
    }

    // The pull beat the glue: every member breaks out at once and collapses into the
    // carried orb — count is the throw budget, dominant material the orb's type.
    private static void BreakOutGroup(EnvironmentContext ctx, ref ActionVars vars)
    {
        Span<int> counts = stackalloc int[TileTypeCount];
        int taken = 0;
        for (int i = 0; i < vars.PeelCount; i++)
        {
            ref var m = ref vars.PeelMembers[i];
            var type = ctx.Chunks.GetCellType(m.Gtx, m.Gty);
            if (!ctx.Chunks.BreakCell(m.Gtx, m.Gty)) continue;
            counts[(int)type]++;
            taken++;
        }
        vars.PeelCount  = 0;
        vars.PeelStrain = 0f;
        if (taken == 0) return;

        var best = vars.OrbType;
        int bestN = 0;
        for (int t = 0; t < TileTypeCount; t++)
            if (counts[t] > bestN) { bestN = counts[t]; best = (TileType)t; }

        vars.OrbType    = best;
        vars.OrbBlocks  = taken;
        vars.ChargeTime = 0f;
        vars.OrbHeld    = true;
    }

    private static int FindPeelMember(in ActionVars vars, int gtx, int gty)
    {
        for (int i = 0; i < vars.PeelCount; i++)
            if (vars.PeelMembers[i].Gtx == gtx && vars.PeelMembers[i].Gty == gty) return i;
        return -1;
    }

    // Order-preserving removal (shift the tail down), so member iteration order is
    // identical before and after a rollback restore.
    private static void RemovePeelMember(ref ActionVars vars, int index)
    {
        for (int i = index; i < vars.PeelCount - 1; i++)
            vars.PeelMembers[i] = vars.PeelMembers[i + 1];
        vars.PeelCount--;
    }

    private static Vector2 CellCenter(int gtx, int gty) => new(
        gtx * Chunk.TileSize + Chunk.TileSize * 0.5f,
        gty * Chunk.TileSize + Chunk.TileSize * 0.5f);

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        // A live orb at exit means the player let go: throw it. Fired from Exit (not
        // Update) for the same reason BlockEruptionAction fires there — the release
        // that ends the state IS the trigger.
        int blocks = RemainingBlocks(in vars);
        if (vars.OrbHeld && blocks > 0 && ctx.Spawner != null)
        {
            var toCursor = ctx.Input.MouseWorldPosition - ctx.Body.Position;
            var dir = toCursor.LengthSquared() < 1e-4f
                ? new Vector2(ab.Facing == 0 ? 1f : ab.Facing, 0f)
                : Vector2.Normalize(toCursor);
            var spawnPos = ctx.Body.Position + dir * HandDistance;
            // Inherit the thrower's velocity so a running throw carries — the orb is a
            // physical mass leaving the hand, not a fresh muzzle.
            ctx.Spawner.SpawnEntity(new LobbedAreaProjectile(
                spawnPos, ctx.Body.Velocity + dir * ThrowSpeed,
                blocks, vars.OrbType,
                ctx.HitIds.Next(), ctx.Faction));
        }

        // Spend the gesture either way, so the release frame can't also route a Click
        // intent into EnergyBallAction.
        ctx.Intents.Consume(IntentType.Click, ctx.CurrentFrame);
        ctx.Intents.Consume(IntentType.PressEdge, ctx.CurrentFrame);
        vars.OrbHeld = false;

        // Recovery only for a grab that actually took something — a lapsed press
        // shouldn't cost the player lag.
        if (vars.OrbBlocks > 0)
            ConditionState.SetForSeconds(ref ab.Condition.RecoveryActive,
                                  ref ab.Condition.RecoveryExpireFrame, RecoverySeconds, ctx.CurrentFrame, ctx.Dt);
    }

    // Carrying terrain is heavy: hauling an orb slows the walk and drags in air. The
    // pre-rip wait phase leaves movement alone, since nothing's been picked up yet.
    public override void ApplyMovementModifiers(ref MovementModifiers m, in ActionVars vars)
    {
        if (!vars.OrbHeld) return;
        m.MaxWalkSpeed *= 0.75f;
        m.WalkAccel    *= 0.8f;
        m.MaxAirSpeed  *= 0.8f;
        m.AirDrag      *= 1.2f;
    }

    // Destroy every solid cell within GrabRadiusTiles of the press site and bank the
    // count. BreakCell (not DamageCell) because a grab takes the whole block — no
    // partial-HP state is left behind. The dominant material becomes the orb's type,
    // so a dig through mixed ground throws back whatever it was mostly made of.
    private static void RipBlocks(EnvironmentContext ctx, ref ActionVars vars)
    {
        if (ctx.Chunks == null) return;

        var  site   = vars.CursorAtPress;
        int  cx     = (int)MathF.Floor(site.X / Chunk.TileSize);
        int  cy     = (int)MathF.Floor(site.Y / Chunk.TileSize);
        int  span   = (int)MathF.Ceiling(GrabRadiusTiles);
        float r2    = GrabRadiusTiles * GrabRadiusTiles;

        int taken = 0;
        // Tally by material so the orb's color/payload reflects the bulk of the dig.
        // A fixed array indexed by TileType (a contiguous byte enum) rather than a
        // Dictionary: no per-frame allocation on the sim path, and the winner scan
        // below has a fixed iteration order, which a Dictionary wouldn't guarantee.
        Span<int> counts = stackalloc int[TileTypeCount];
        for (int dy = -span; dy <= span; dy++)
        for (int dx = -span; dx <= span; dx++)
        {
            if (dx * dx + dy * dy > r2) continue;
            int gtx = cx + dx, gty = cy + dy;
            if (ctx.Chunks.GetCellState(gtx, gty) != TileState.Solid) continue;
            var type = ctx.Chunks.GetCellType(gtx, gty);
            if (!ctx.Chunks.BreakCell(gtx, gty)) continue;
            counts[(int)type]++;
            taken++;
        }

        // Nothing solid left at the site (someone else broke it mid-press) — the grab
        // lapses rather than handing over an empty orb.
        if (taken == 0) return;

        var best = vars.OrbType;
        int bestN = 0;
        for (int t = 0; t < TileTypeCount; t++)
            if (counts[t] > bestN) { bestN = counts[t]; best = (TileType)t; }

        vars.OrbType   = best;
        vars.OrbBlocks = taken;
        vars.ChargeTime = 0f;
        vars.OrbHeld   = true;
    }

    // Blocks still in the orb: the harvest linearly bled down by carry time. This is
    // both the render size and the throw budget, so what you see is what you throw.
    private static int RemainingBlocks(in ActionVars vars)
    {
        if (!vars.OrbHeld) return 0;
        float frac = 1f - vars.ChargeTime / DissipateSeconds;
        if (frac <= 0f) return 0;
        return (int)MathF.Floor(vars.OrbBlocks * frac);
    }

    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body, in ActionVars vars)
    {
        // Peel-phase feedback: tethered cells darken with tether strength, and the
        // shade slides toward red as the spring nears its snap cap. Pure render —
        // reads the sim-written member buffer and strain, feeds nothing back.
        if (!vars.OrbHeld && vars.PeelCount > 0)
        {
            var tint = Color.Lerp(Color.Black, Color.DarkRed, vars.PeelStrain);
            for (int i = 0; i < vars.PeelCount; i++)
            {
                var m = vars.PeelMembers[i];
                float a = MathHelper.Clamp(0.12f + 0.35f * (m.Tether / 1.5f), 0f, 0.55f);
                sb.Draw(pixel, new Rectangle(m.Gtx * Chunk.TileSize, m.Gty * Chunk.TileSize,
                                             Chunk.TileSize, Chunk.TileSize), tint * a);
            }
            return;
        }

        if (!vars.OrbHeld) return;
        int blocks = RemainingBlocks(in vars);
        if (blocks <= 0 || vars.OrbBlocks <= 0) return;

        // GrabDir is last frame's aim (Update tracks the cursor), so the orb sits on
        // the side the player is about to throw toward.
        var aim  = vars.GrabDir.LengthSquared() > 1e-6f ? vars.GrabDir : Vector2.UnitX;
        var hand = body.Position + aim * HandDistance;
        float r  = OrbMaxRadius * MathF.Sqrt((float)blocks / vars.OrbBlocks);
        int   d  = (int)MathF.Max(2f, r * 2f);
        var color = TilePalette.BaseColor(vars.OrbType);
        sb.Draw(pixel, new Rectangle((int)(hand.X - r), (int)(hand.Y - r), d, d), color);
    }
}

// ---------- Grab — Shift + LMB: hold an opponent, then throw ---------------------
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

    // Two phases with INDEPENDENT lengths, which is why the old fixed-duration remap could
    // not express this: the hold runs until the player releases (up to GrabHoldMaxSeconds),
    // then the throw runs its own ThrowSeconds. The authored clip devotes its tail to the
    // throw, so map the hold onto everything before HoldShare and the throw onto the rest —
    // a short hold jump-cuts forward to the throw, which is right: the throw pose must play
    // WHEN the throw happens, not whenever the clip's own clock reaches it.
    private const float HoldShare = GrabHoldMaxSeconds / (GrabHoldMaxSeconds + ThrowSeconds);
    public override float AnimationProgress(in ActionVars vars)
        => vars.GrabThrowing
            ? HoldShare + (1f - HoldShare) * (vars.ChargeTime / ThrowSeconds)
            : HoldShare * (vars.TimeInState / GrabHoldMaxSeconds);
    private const float ThrowAccel  = 12000f;

    // 48/48 — above BlockGrabAction (46/46), which shares the Shift+LMB press. The two
    // deliberately collide: grabbing a body is the more urgent read, so it takes the
    // press whenever a body is there to take, and falls through to the block grab when
    // there isn't. Still below GuardRetaliate(55).
    public override int ActivePriority  => 48;
    public override int PassivePriority => 48;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (!ctx.Input.Shift || !ctx.Input.LeftClick) return false;
        if (ctx.Controller.GetPrevious(1).LeftClick) return false;      // press-edge only
        if (ctx.Combat?.BlocksAttack == true) return false;             // not while stunned/grabbed
        if (ctx.Combat?.HitstunActive == true) return false;            // not while in hitstun
        if (ab.Condition.RecoveryActive) return false;
        // Yield the press to BlockGrabAction ONLY when it could actually use it: the
        // cursor is on a block inside its reach AND there's nobody to grab. Aim at a
        // body and the grab wins on priority; aim at open air and the grab still fires
        // and WHIFFS — which preserves the punish window (the unconditional recovery in
        // Exit is the whole punish, see the header). Terrain aim is the only thing that
        // hands the gesture over.
        if (!AimedAtGrabbableTerrain(ctx)) return true;
        return HasVictimInRange(ctx, ab);
    }

    // Cursor over a solid cell within BlockGrabAction's own reach — i.e. that action's
    // precondition, minus the press-edge. Shares BlockGrabAction.GrabReach so "aimed at
    // terrain" means the same thing on both sides of the handoff.
    private static bool AimedAtGrabbableTerrain(EnvironmentContext ctx)
    {
        if (!BlockEruptionHelpers.IsCursorInSolid(ctx)) return false;
        float reach = BlockGrabAction.GrabReach;
        return (ctx.Input.MouseWorldPosition - ctx.Body.Position).LengthSquared() <= reach * reach;
    }

    // Any non-self hurtbox inside the region the hold field would cover. Same geometry
    // as PublishHoldField below (focus in front along the aim, Range half-size), so the
    // gate and the field can't disagree about who is grabbable. Hurtboxes for the frame
    // are already published by the time the action FSM runs (Simulation.Step).
    private static bool HasVictimInRange(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (ctx.Hurtboxes == null) return false;
        var focus = ctx.Body.Position + AimDir(ctx, ab) * FocusDist;
        var region = new BoundingBox(
            focus.X - Range, focus.Y - Range,
            focus.X + Range, focus.Y + Range);
        foreach (var _ in ctx.Hurtboxes.Overlapping(region, exclude: ctx.Faction)) return true;
        return false;
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
            bool holding = ctx.Input.LeftClick && vars.TimeInState < GrabHoldMaxSeconds;
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

