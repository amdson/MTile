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

    public abstract bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities);
    public abstract bool CheckConditions  (EnvironmentContext ctx, PlayerAbilityState abilities);

    public virtual void Enter(EnvironmentContext ctx, PlayerAbilityState abilities) {}
    public virtual void Exit (EnvironmentContext ctx, PlayerAbilityState abilities) {}

    public abstract void Update(EnvironmentContext ctx, PlayerAbilityState abilities);

    public virtual void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body) {}

    // Declare multiplicative scalars on movement knobs (walk speed, friction, …).
    // Called by PlayerCharacter once per frame between action selection and
    // Movement.Update — the values go into ctx.Modifiers, movement reads them
    // at its config sites. Default no-op = identity, no effect on physics.
    public virtual void ApplyMovementModifiers(ref MovementModifiers m) { }

    // Augment the player body's physics directly: add to AppliedForce for a
    // sustained push, or write Velocity for an impulse / "ensure-at-least" assist.
    // Called by PlayerCharacter AFTER Movement.Update has written its force for
    // the frame and BEFORE Action.Update — so action-driven forces stack on top
    // of, not in competition with, the movement-driven force. Default no-op.
    public virtual void ApplyActionForces(EnvironmentContext ctx) { }
}

// Always-on fallback. Mirrors FallingState's role in the movement FSM.
public class NullAction : ActionState
{
    public override int ActivePriority  => 0;
    public override int PassivePriority => 0;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab) => true;
    public override bool CheckConditions  (EnvironmentContext ctx, PlayerAbilityState ab) => true;
    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab) {}
}

// Wind-up state. Entered on LMB-press edge; held while LMB is down. Doesn't commit
// to any specific move — Slash/Stab/etc. preempt it on their own preconditions.
// Visual is a small pulsing indicator at the body, colored by posture.
public class ReadyAction : ActionState
{
    private const float MaxHold = 1.0f;   // hard cap so a stuck button doesn't lock us forever
    public override int ActivePriority  => 10;
    public override int PassivePriority => 15;

    private float _timeInState;
    private bool  _isGrounded;
    private int   _facing;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (ab.Condition.RecoveryActive) return false;
        return ctx.Intents.Peek(IntentType.PressEdge, ctx.CurrentFrame, out _);
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        // Stay alive while LMB held, up to MaxHold. Release exits via Click/Stab preempt
        // (their preconditions fire as the release-edge intent appears) OR via Null fallback.
        if (!ctx.Input.LeftClick) return false;
        return _timeInState < MaxHold;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        _timeInState = 0f;
        _isGrounded  = ctx.TryGetGround(out _);
        _facing      = ab.Facing == 0 ? 1 : ab.Facing;
        ctx.Intents.Consume(IntentType.PressEdge, ctx.CurrentFrame);
    }
    
    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        _timeInState += ctx.Dt;
    }

    // Light slowdown while charging — telegraphs commitment. Slashes flick through
    // Ready in 1–2 frames so the dip is imperceptible; a long-held stab charge
    // lingers and feels heavy. The GravityScale dip pairs with the horizontal
    // clamp to give a "floaty hover while you wind up" feel in the air; on the
    // ground the standing spring overrides gravity, so the scale is a no-op.
    public override void ApplyMovementModifiers(ref MovementModifiers m)
    {
        m.MaxWalkSpeed   *= 0.6f;
        m.WalkAccel      *= 0.7f;
        m.GroundFriction *= 1.3f;
        m.MaxAirSpeed    *= 0.7f;
        m.GravityScale   *= 0.3f;
    }

    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body)
    {
        // Pulsing dot offset slightly toward facing, color matches posture
        const float ArcR = PlayerCharacter.Radius * 1.5f;
        float pulse  = MathF.Sin(_timeInState * MathF.PI * 4f) * 0.5f + 0.5f;
        float offset = ArcR * 0.5f * pulse;
        var pos = body.Position + new Vector2(_facing * offset, 0f);
        var color = (_isGrounded ? Color.Red : Color.DeepSkyBlue) * 0.7f;
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

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab)
        => ab.Condition.RecoveryActive;

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab) {}
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
    private const float BaseArcRadius         = PlayerCharacter.Radius * 1.5f;
    private const int   TrailLen              = 6;

    // Shared monotonic counter so all slash variants generate unique HitIds; CombatSystem
    // dedupes per (HitId, Target), so multi-frame hitboxes only land once per entity.
    private static int _nextHitId = 1;

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
    // -----------------------------------------------------------------------

    protected float ArcRadius => BaseArcRadius * ArcRadiusScale;

    private float   _timeInState;
    private Vector2 _slashDir;
    private int     _hitId;
    private readonly Vector2[] _trail = new Vector2[TrailLen];
    private int     _trailHead;
    private int     _trailFilled;

    public override int ActivePriority  => 30;
    public override int PassivePriority => 30;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (!ctx.Intents.Peek(IntentType.Click, ctx.CurrentFrame, out _)) return false;
        bool grounded = ctx.TryGetGround(out _);
        if (RequireGround && !grounded) return false;
        if (RequireAir    &&  grounded) return false;
        if (!CombosOk(ab.Condition)) return false;
        return true;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab)
        => _timeInState < Duration;

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        _timeInState = 0f;
        _slashDir    = ComputeSlashDir(ctx, ab);
        _hitId       = System.Threading.Interlocked.Increment(ref _nextHitId);
        _trailHead   = 0;
        _trailFilled = 0;
        ctx.Intents.Consume(IntentType.Click, ctx.CurrentFrame);
        OnEnterClearFlags(ab.Condition);
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab)
        => OnExitSetFlags(ab.Condition, ctx.CurrentFrame);

    // Mouse-to-body direction, hemisphere-clamped so a click behind the player
    // produces a perpendicular slash rather than a backward one. Degenerate inputs
    // fall back to (Facing, 0).
    private static Vector2 ComputeSlashDir(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        int facing = ab.Facing == 0 ? 1 : ab.Facing;
        Vector2 raw = ctx.Input.MouseWorldPosition - ctx.Body.Position;
        if (raw.X * facing < 0f) raw.X = 0f;
        if (raw.LengthSquared() < 1e-4f) return new Vector2(facing, 0f);
        return Vector2.Normalize(raw);
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        _timeInState += ctx.Dt;
        var dot = ComputeDotPosition(ctx.Body.Position);
        _trail[_trailHead] = dot;
        _trailHead = (_trailHead + 1) % TrailLen;
        if (_trailFilled < TrailLen) _trailFilled++;

        float windowStart = Duration * HurtboxStartFraction;
        float windowEnd   = windowStart + Duration * HurtboxActiveFraction;
        if (_timeInState >= windowStart && _timeInState <= windowEnd && ctx.Hitboxes != null)
        {
            var apex = ctx.Body.Position + _slashDir * ArcRadius;
            var region = new BoundingBox(
                apex.X - ArcRadius * 0.5f, apex.Y - ArcRadius * 0.5f,
                apex.X + ArcRadius * 0.5f, apex.Y + ArcRadius * 0.5f);
            ctx.Hitboxes.Publish(new Hitbox(
                region, _hitId, SlashDamagePerFrame,
                _slashDir * KnockbackMagnitude,
                Faction.Player, this, SlashColor));
        }
    }

    private Vector2 ComputeDotPosition(Vector2 anchor)
    {
        float t = MathHelper.Clamp(_timeInState / Duration, 0f, 1f);
        float outF       = MathF.Sin(MathF.PI * t);
        float halfSweep  = SweepAngleDeg * 0.5f * MathF.PI / 180f;
        float angle      = halfSweep * (1f - 2f * t) * SweepDirection;
        float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
        Vector2 dir = new Vector2(
            _slashDir.X * cos - _slashDir.Y * sin,
            _slashDir.X * sin + _slashDir.Y * cos);
        return anchor + dir * (ArcRadius * outF);
    }

    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body)
    {
        for (int i = 0; i < _trailFilled; i++)
        {
            int idx = (_trailHead - 1 - i + TrailLen) % TrailLen;
            float alpha = 1f - (float)i / TrailLen;
            int size = i == 0 ? 4 : 3;
            var p = _trail[idx];
            sb.Draw(pixel,
                new Rectangle((int)p.X - size / 2, (int)p.Y - size / 2, size, size),
                SlashColor * alpha);
        }
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

// ---------- Stab — long-hold + swipe gesture -----------------------------------

// Linear thrust along the captured swipe direction. Longer duration, longer recovery,
// more knockback than a slash; no combo chain (can't immediately roll into another move).
public class StabAction : ActionState
{
    private const float Duration              = 0.60f;
    private const float HurtboxStartTime      = 0.25f;
    private const float HurtboxActiveDuration = 0.15f;

    // Lunge window: a short forward-glide phase AFTER the hitbox active window
    // (0.25–0.40) and BEFORE the settle (0.55–0.60). During this window the
    // ground-friction modifier dips so the velocity assist below can actually
    // translate the body — outside it, friction is back up to sell the plant.
    private const float LungeStart   = 0.10f;
    private const float LungeEnd     = 0.4f;
    private const float LungeSpeed   = 90f;     // px/s horizontal target during lunge

    private const float Reach                 = PlayerCharacter.Radius * 3.0f;
    private const float PrimaryHalfWidth      = PlayerCharacter.Radius * 0.35f;
    // Tile-shockwave reach — significantly longer + wider than the entity-hitbox.
    // Lets stab dig through several tiles at once without hitting balloons/enemies
    // beyond its visible thrust extent.
    private const float BlockReach            = PlayerCharacter.Radius * 6.0f;
    private const float BlockHalfWidth        = PlayerCharacter.Radius * 0.9f;
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

    private static int _nextHitId = 1_000_001;   // separate range from slashes for log clarity

    // Static rectangle polygons in local space — long axis along +X. Rotation applied
    // at publish time via Polygon.GetVertices(pos, rotation) so the actual hit shape
    // tracks _stabDir, not just the loose AABB.
    private static readonly Polygon PrimaryPoly = Polygon.CreateRectangle(Reach,      PrimaryHalfWidth * 2f);
    private static readonly Polygon BlockPoly   = Polygon.CreateRectangle(BlockReach, BlockHalfWidth   * 2f);

    private float   _timeInState;
    private Vector2 _stabDir;
    private bool    _isGrounded;
    private int     _hitId;
    // Captured once at Enter; constant for the duration of the stab. 1× on ground,
    // in [MinBoost, MaxBoost] in air based on velocity-vs-stabDir alignment.
    private float   _boost;
    // Block-shockwave polygon + reach resized by boost. Aliases the static BlockPoly
    // when boost is ~1× so an unboosted stab does zero extra allocation.
    private Polygon _blockPoly;
    private float   _blockReach;

    public override int ActivePriority  => 30;
    public override int PassivePriority => 30;

    private Color CurrentColor => _isGrounded ? Color.Goldenrod : Color.MediumPurple;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
        => ctx.Intents.Peek(IntentType.Stab, ctx.CurrentFrame, out _);

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab)
        => _timeInState < Duration;

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        _timeInState = 0f;
        _isGrounded  = ctx.TryGetGround(out _);
        _hitId       = System.Threading.Interlocked.Increment(ref _nextHitId);

        // Capture swipe direction from the intent; hemisphere-clamp like slash.
        int facing = ab.Facing == 0 ? 1 : ab.Facing;
        if (ctx.Intents.Peek(IntentType.Stab, ctx.CurrentFrame, out var intent))
        {
            var raw = intent.Direction;
            if (raw.X * facing < 0f) raw.X = 0f;
            if (raw.LengthSquared() < 1e-4f) raw = new Vector2(facing, 0f);
            _stabDir = Vector2.Normalize(raw);
            ctx.Intents.Consume(IntentType.Stab, ctx.CurrentFrame);
        }
        else
        {
            _stabDir = new Vector2(facing, 0f);
        }

        // Air-stab dive boost: project velocity onto the captured stab direction
        // at the instant of commit. Ground stab + negative projection (velocity
        // opposite to stab) collapse to MinBoost; high-speed aligned dives saturate
        // at MaxBoost. Length × boost, width × √boost so the tile box gets visibly
        // longer without becoming a giant rectangle.
        if (_isGrounded)
        {
            _boost      = 1f;
            _blockPoly  = BlockPoly;
            _blockReach = BlockReach;
        }
        else
        {
            float velAlongStab = MathF.Max(0f, Vector2.Dot(_stabDir, ctx.Body.Velocity));
            float t = MathHelper.Clamp(velAlongStab / BoostReferenceSpeed, 0f, 1f);
            _boost = MathHelper.Lerp(MinBoost, MaxBoost, t);
            if (_boost > 1.001f)
            {
                _blockReach = BlockReach * _boost;
                float halfW = BlockHalfWidth * MathF.Sqrt(_boost);
                _blockPoly  = Polygon.CreateRectangle(_blockReach, halfW * 2f);
            }
            else
            {
                _blockPoly  = BlockPoly;
                _blockReach = BlockReach;
            }
        }
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        // Larger recovery than slashes — stab can't roll directly into anything.
        ConditionState.SetFor(ref ab.Condition.RecoveryActive,
                              ref ab.Condition.RecoveryExpireFrame, 9, ctx.CurrentFrame);
    }

    // Heavy-stance modifiers throughout the stab; friction dips during the lunge
    // window so the ApplyActionForces velocity assist isn't immediately braked.
    public override void ApplyMovementModifiers(ref MovementModifiers m)
    {
        bool inLunge = _timeInState >= LungeStart && _timeInState <= LungeEnd;
        m.MaxWalkSpeed   *= 0.35f;
        m.WalkAccel      *= 0.5f;
        m.GroundFriction *= inLunge ? 0.15f : 1.5f;
        m.MaxAirSpeed    *= 0.6f;
        m.AirDrag        *= 1.3f;
    }

    // Forward glide during the lunge window. "Ensure-at-least" semantics: raises
    // velocity toward the target each frame but never lowers it, so a player
    // already moving faster than the lunge speed doesn't get nerfed, and the
    // assist re-applies every frame inside the window so per-frame friction can't
    // brake it down between calls.
    public override void ApplyActionForces(EnvironmentContext ctx)
    {
        if (!_isGrounded) return;
        if (_timeInState < LungeStart || _timeInState > LungeEnd) return;

        float dirX = MathF.Sign(_stabDir.X);
        if (dirX == 0f) return;     // vertical stab — no horizontal lunge

        float target = dirX * LungeSpeed;
        var v = ctx.Body.Velocity;
        if (dirX > 0f && v.X < target) v.X = target;
        else if (dirX < 0f && v.X > target) v.X = target;
        ctx.Body.Velocity = v;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        _timeInState += ctx.Dt;

        if (_timeInState >= HurtboxStartTime &&
            _timeInState <= HurtboxStartTime + HurtboxActiveDuration &&
            ctx.Hitboxes != null)
        {
            // Both stab hitboxes use the actual rotated-rectangle polygon for narrow-phase
            // intersection. Rotation = angle of _stabDir from +X. The broad-phase AABB
            // is computed from the rotated polygon so the cell sweep in CombatSystem
            // still reads correctly.
            float rotation = MathF.Atan2(_stabDir.Y, _stabDir.X);
            float dmg = DamagePerFrame * _boost;

            // Primary thrust — entity + tile damage along the thrust line.
            var primaryCenter = ctx.Body.Position + _stabDir * (Reach * 0.5f);
            var primaryAABB   = PrimaryPoly.GetBoundingBox(primaryCenter, rotation);
            ctx.Hitboxes.Publish(new Hitbox(
                primaryAABB, _hitId, dmg,
                _stabDir * KnockbackMagnitude,
                Faction.Player, this, CurrentColor,
                shape: PrimaryPoly, shapePos: primaryCenter, shapeRotation: rotation));

            // Block-shockwave — same HitId so entities that overlap both count once. No
            // knockback (knockback comes from the primary box). Tiles only — passes
            // cleanly past entities along the thrust axis. Polygon + reach are pre-scaled
            // by _boost at Enter; a well-aligned air dive gets a substantially longer
            // and slightly wider box plus the damage multiplier.
            var blockCenter = ctx.Body.Position + _stabDir * (_blockReach * 0.5f);
            var blockAABB   = _blockPoly.GetBoundingBox(blockCenter, rotation);
            // Brighten the debug color in proportion to boost so the bigger box reads
            // visually distinct from a baseline stab when DebugDrawHitboxes is on.
            float boostT = (_boost - MinBoost) / (MaxBoost - MinBoost);
            var blockColor = Color.Lerp(Color.Lerp(CurrentColor, Color.Gray, 0.4f), Color.White, boostT * 0.5f);
            ctx.Hitboxes.Publish(new Hitbox(
                blockAABB, _hitId, dmg,
                Vector2.Zero,
                Faction.Player, this,
                blockColor,
                HitTargets.TilesOnly,
                shape: _blockPoly, shapePos: blockCenter, shapeRotation: rotation));
        }
    }

    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body)
    {
        // Thrust extends along _stabDir, peaks at t=0.5, retracts. Draw a dot at the tip
        // plus a short trailing dot at half-extent so the motion reads as linear.
        float t = MathHelper.Clamp(_timeInState / Duration, 0f, 1f);
        float ext = Reach * MathF.Sin(MathF.PI * t);
        var tip   = body.Position + _stabDir * ext;
        var mid   = body.Position + _stabDir * (ext * 0.5f);
        var color = CurrentColor;
        sb.Draw(pixel, new Rectangle((int)tip.X - 2, (int)tip.Y - 2, 4, 4), color);
        sb.Draw(pixel, new Rectangle((int)mid.X - 1, (int)mid.Y - 1, 3, 3), color * 0.5f);
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

    private static int _nextHitId = 2_000_001;

    private float   _timeInState;
    private bool    _isGrounded;
    private int     _hitId;

    public override int ActivePriority  => 30;
    public override int PassivePriority => 30;

    private Color PulseColor => _isGrounded ? Color.Gold : Color.Cyan;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
        => ctx.Intents.Peek(IntentType.Circle, ctx.CurrentFrame, out _);

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab)
        => _timeInState < Duration;

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        _timeInState = 0f;
        _isGrounded  = ctx.TryGetGround(out _);
        _hitId       = System.Threading.Interlocked.Increment(ref _nextHitId);
        ctx.Intents.Consume(IntentType.Circle, ctx.CurrentFrame);
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        // Long recovery — pulse is the biggest single attack, can't roll directly
        // into anything else.
        ConditionState.SetFor(ref ab.Condition.RecoveryActive,
                              ref ab.Condition.RecoveryExpireFrame, 12, ctx.CurrentFrame);
    }

    // Heavy stance throughout the pulse — applies on ground AND in air, unlike
    // Stab which leaves air movement mostly alone. Pairs with the gravity scale
    // to give a hovering "cast" feel mid-air.
    public override void ApplyMovementModifiers(ref MovementModifiers m)
    {
        m.MaxWalkSpeed   *= 0.25f;
        m.WalkAccel      *= 0.5f;
        m.GroundFriction *= 1.5f;
        m.MaxAirSpeed    *= 0.25f;
        m.AirAccel       *= 0.5f;
        m.AirDrag        *= 1.5f;
        m.GravityScale   *= 0.3f;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        _timeInState += ctx.Dt;

        if (_timeInState < HitboxStartTime ||
            _timeInState > HitboxStartTime + HitboxActiveDuration ||
            ctx.Hitboxes == null) return;

        // Radius lerps from start → end across the active window.
        float u = (_timeInState - HitboxStartTime) / HitboxActiveDuration;
        if (u < 0f) u = 0f; else if (u > 1f) u = 1f;
        float r = MathHelper.Lerp(StartRadius, EndRadius, u);

        // Anchor to the player's CURRENT position each frame — the ring drifts
        // with the caster rather than hanging at the cast point. Each segment's
        // knockback also picks up the body's velocity so a moving caster imparts
        // their momentum to anything the ring sweeps.
        var anchor  = ctx.Body.Position;
        var bodyVel = ctx.Body.Velocity;

        var color = PulseColor;
        for (int i = 0; i < Segments; i++)
        {
            float angle = i * MathHelper.TwoPi / Segments;
            var dir    = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            var center = anchor + dir * r;
            var region = new BoundingBox(
                center.X - SegmentHalfSize, center.Y - SegmentHalfSize,
                center.X + SegmentHalfSize, center.Y + SegmentHalfSize);
            ctx.Hitboxes.Publish(new Hitbox(
                region, _hitId, DamagePerFrame,
                dir * KnockbackMagnitude + bodyVel,
                Faction.Player, this, color));
        }
    }

    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body)
    {
        if (_timeInState < HitboxStartTime ||
            _timeInState > HitboxStartTime + HitboxActiveDuration) return;
        float u = (_timeInState - HitboxStartTime) / HitboxActiveDuration;
        if (u < 0f) u = 0f; else if (u > 1f) u = 1f;
        float r = MathHelper.Lerp(StartRadius, EndRadius, u);
        var color = PulseColor;
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
//                            EruptionPlanner to spawn N dirt sprouts.
//
// Priority arrangement (per the user's spec):
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

    private float   _chargeTime;
    private Vector2 _originCell;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        // RMB press-edge + cursor currently in solid.
        if (!ctx.Input.RightClick) return false;
        var prev = ctx.Controller.GetPrevious(1);
        if (prev.RightClick) return false;
        return BlockEruptionHelpers.IsCursorInSolid(ctx);
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        // Alive only while RMB held AND cursor still in solid. When the cursor
        // exits, this returns false → BlockEruptionAction's precondition picks up
        // via the armed flag set in Exit.
        if (!ctx.Input.RightClick) return false;
        return BlockEruptionHelpers.IsCursorInSolid(ctx);
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        _chargeTime = 0f;
        var (gtx, gty) = BlockEruptionHelpers.CursorCell(ctx);
        _originCell = BlockEruptionHelpers.CellCenter(gtx, gty);
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        _chargeTime += ctx.Dt;
        // Re-anchor origin to the current cell so a slow drag through several
        // solid cells uses the last one visited as the ignition point.
        var (gtx, gty) = BlockEruptionHelpers.CursorCell(ctx);
        _originCell = BlockEruptionHelpers.CellCenter(gtx, gty);
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        // Arm the eruption only if the natural exit happened: cursor swept OUT
        // of solid while RMB is still held. Other exits (RMB released without
        // exiting solid, attack-preempt with cursor still in solid) disarm.
        if (ctx.Input.RightClick && !BlockEruptionHelpers.IsCursorInSolid(ctx))
        {
            ab.Condition.BlockEruptionArmed = true;
            ab.Condition.BlockChargeTime   = _chargeTime;
            ab.Condition.BlockChargeOrigin = _originCell;
        }
        else
        {
            ab.Condition.BlockEruptionArmed = false;
        }
    }

    // Heavy stance during the charge — same shape as Stab's modifier set, applies
    // on ground + air. Players can still nudge but not sprint mid-charge.
    public override void ApplyMovementModifiers(ref MovementModifiers m)
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
    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body)
    {
        const int segments = 16;
        bool saturated = _chargeTime >= SaturationTime;

        float chargeFrac = saturated ? 1f : (_chargeTime / SaturationTime);
        float r = MaxIndicatorRadius * chargeFrac * (saturated ? DipFactor : 1f);

        Color color = saturated
            ? new Color(220, 90, 40)                                       // dimmed orange-red after dip
            : Color.Lerp(new Color(150, 100, 60), Color.Gold, chargeFrac); // brown → gold ramping

        for (int i = 0; i < segments; i++)
        {
            float a = i * MathHelper.TwoPi / segments;
            var p = _originCell + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
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

    public override int ActivePriority  => 9;
    public override int PassivePriority => 10;

    private float          _chargeTime;
    private Vector2        _origin;
    private SmoothPen      _pen;
    private List<PathSample> _samples;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
        => ctx.Input.RightClick && ab.Condition.BlockEruptionArmed;

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab)
        => ctx.Input.RightClick;

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        // Consume the armed flag + capture the charge/origin handoff.
        ab.Condition.BlockEruptionArmed = false;
        _chargeTime = ab.Condition.BlockChargeTime;
        _origin     = ab.Condition.BlockChargeOrigin;

        _pen     = new SmoothPen(ctx.Input.MouseWorldPosition);
        _samples = new List<PathSample>(64);
        // Seed the first sample at the eruption origin with zero velocity — that
        // gives the EruptionPlanner a wide-radius "base" deposit at the ignition
        // cell, producing the pyramid's wide base.
        _samples.Add(new PathSample(_origin, Vector2.Zero));
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        _pen.Update(ctx.Input.MouseWorldPosition, ctx.Dt);
        _samples.Add(new PathSample(_pen.Position, _pen.Velocity));
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        // Only fire when naturally exited (RMB released). A preempt by ReadyAction
        // / attack leaves RMB held and silently cancels the eruption.
        if (ctx.Input.RightClick) return;

        int budget = ComputeBudget(_chargeTime);
        if (budget > 0 && _samples != null && _samples.Count > 0)
            EruptionPlanner.Plan(ctx.Chunks, _origin, _samples, budget);
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
    public override void ApplyMovementModifiers(ref MovementModifiers m)
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
    public override void Draw(SpriteBatch sb, Texture2D pixel, PhysicsBody body)
    {
        if (_samples == null) return;
        // Origin marker — bright dot.
        sb.Draw(pixel, new Rectangle((int)_origin.X - 2, (int)_origin.Y - 2, 5, 5), Color.Gold);
        // Path trail — earlier samples slightly transparent so the "head" of the
        // gesture reads as the latest position.
        int n = _samples.Count;
        for (int i = 1; i < n; i++)
        {
            float t = (float)i / n;
            var p = _samples[i].Position;
            sb.Draw(pixel, new Rectangle((int)p.X - 1, (int)p.Y - 1, 3, 3),
                Color.SandyBrown * (0.35f + 0.5f * t));
        }
    }
}
