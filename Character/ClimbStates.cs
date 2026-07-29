using System;
using Microsoft.Xna.Framework;

namespace MTile;

// The climb family (BALLISTIC_CORRECTOR_PLAN steps 4 + 6): the at-speed 1-block
// vault (ParkourState), the slow/flush 1-block climb (MantleState), and the
// taller arc-jump band (ArcJumpState) share ClimbManeuverBase; each subclass
// owns only its rise band and entry-speed gate. Behind
// MovementConfig.CorrectorVaultEnabled for A/B; class names match the
// vault-family sim fixtures ("Parkour" / "Mantle" / "ArcJump").
//
// Shape: intent generates the reference (a one-shot entry hop sized to clear the
// lip with margin + a guided drive that preserves entry speed); the corrector
// solve (ManeuverCorrector.Run — shared infrastructure, not this family's
// private machinery) only deforms it. Each Update runs the two outer
// sequential-convexification passes, then applies the summed tick-0 correction
// through Body.AppliedForce. The ballistic crest envelope + gate delivery are
// the authored feel, not solver output.
//
// Split between the vault and the mantle is the entry-speed gate
// (MantleMaxEntrySpeed); cancel-on-release and MaxVaultTime liveness as
// everywhere in the climb family. AmbientPolicy.Off — the ambient corrector
// must never fight an owned maneuver.
public abstract class ClimbManeuverBase : MovementState
{
    // Entry feasibility solves the FULL arc once per candidate frame and can afford
    // a deeper fixed iteration budget than the per-tick re-solve (opposed-row
    // schedules — the slalom class — need the extra sweeps; see the anchor tests).
    private const int FeasibilityIterations = 128;

    protected readonly int _dir;
    protected ClimbManeuverBase(int dir) => _dir = dir;

    // The rise band this maneuver claims (px) and whether the body must arrive
    // at speed (the running/flush split between the vault and the mantle).
    protected abstract float RiseBandMin { get; }
    protected abstract float RiseBandMax { get; }
    protected abstract bool  RequiresRunningEntry { get; }

    public override AnimTag AnimationTag => AnimTag.Parkour;   // the vault clip family

    public override int ActivePriority  => MovementPriorities.ClimbActive;
    public override int PassivePriority => MovementPriorities.ClimbPassive;
    public override MovementCapability RequiredCapabilities => MovementCapability.LedgeGrab;
    public override AmbientPolicy AmbientPolicy => AmbientPolicy.Off;

    // Height-fraction progress + lip grip for the hands overlay — pure functions
    // of MovementVars/body, so a restore rebuilds them (LedgeStates idiom).
    private float   _progress;
    private Vector2 _gripCorner;
    private bool    _hasGrip;
    public override float AnimationProgress => _progress;
    public override bool TryAnimationGrip(out Vector2 target) { target = _gripCorner; return _hasGrip; }
    public override void ResetTransient() { _progress = 0f; _gripCorner = default; _hasGrip = false; }

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        var cfg = MovementConfig.Current;
        if (!cfg.CorrectorVaultEnabled) return false;
        if (ctx.Intent.HeldHorizontal != _dir || !ctx.TryGetGround(out _)) return false;
        // Launch gate (StandingState's entry rule): a body rising faster than
        // support could ever push it is ballistic — the ground probe merely
        // SEEING a floor doesn't make it standable. Without this the vault
        // re-triggers off a jump's ascent (the probe reaches ~SupportReach)
        // and re-injects hop energy mid-launch.
        if (-ctx.Body.Velocity.Y > cfg.SpringMaxRiseSpeed) return false;
        // Running/flush split: the 1-block vault leaves at-or-below-gate entries to
        // MantleState; the taller band has no mantle partner and fires at any speed.
        if (RequiresRunningEntry && _dir * ctx.Body.Velocity.X <= cfg.MantleMaxEntrySpeed) return false;

        var corridor = ctx.GetCorridor(_dir);
        if (!corridor.TryFirstRise(out var rise)) return false;
        if (rise.Delta < RiseBandMin || rise.Delta > RiseBandMax) return false;

        // A climb lands to STAY. A landing under a low ceiling is fine when it
        // is a PLATEAU (the corridor's crouch-raised gate exists for exactly
        // that — the flattened tunnel vault), but a narrow PEDESTAL under a
        // ceiling — the tunnel floor bump — is terrain to CROSS: delivering on
        // top parks the body head-high a stride away from the next ceiling
        // feature, and the exit carry eats the bonk. Crossing bumps is the
        // ambient fold's job; refuse and stay out of its way.
        if (rise.Column < corridor.ColumnCount
            && !float.IsNegativeInfinity(corridor.CeilY[rise.Column])
            && corridor.FloorY[rise.Column] - corridor.CeilY[rise.Column]
               < PlayerCharacter.StandingHeight)
        {
            int look = Math.Min(rise.Column + 2, corridor.ColumnCount - 1);
            for (int i = rise.Column + 1; i <= look; i++)
                if (corridor.FloorY[i] > corridor.FloorY[rise.Column] + rise.Delta * 0.5f)
                    return false;   // floor drops back off: a bump, not a landing
        }

        float dist = _dir * (rise.Pos.X - ctx.Body.Bounds.Side(_dir));
        if (dist > cfg.CorrectorVaultTriggerDistance) return false;

        // Trigger-by-feasibility (plan step 5): the same solve that will run every
        // Update, run at entry over the full arc — a maneuver may fire iff its
        // corrected arc is feasible. Refusal is measured on the final TRUE corrected
        // rollout, never on the surrogate residual: normal wall-scrape rows carry a
        // nonzero tracking residual by design (feel > model purity), so the honest
        // question is "does the corrected arc DELIVER into the gate before any
        // unavoided impact", not "is the residual ≈ 0".
        var s = ctx.Corrector;
        if (s == null) return true;   // hand-built test contexts: cheap gates only

        float targetY = corridor.ClimbTargetY(rise.Column);
        var probe = s.ProbeBody;
        probe.Polygon  = ctx.Body.Polygon;
        probe.Position = ctx.Body.Position;
        float vy0 = HopVy(ctx, rise, targetY);
        probe.Velocity = new Vector2(ctx.Body.Velocity.X,
                                     MathF.Min(ctx.Body.Velocity.Y, -vy0));
        float entrySpeed = MathF.Max(_dir * ctx.Body.Velocity.X, cfg.MaxWalkSpeed);

        ManeuverCorrector.Run(ctx, probe, _dir, entrySpeed, default, out int rowCount,
                              FeasibilityIterations);
        int H = Math.Min(cfg.CorrectorHorizon, BallisticPredictor.MaxHorizon);
        int n = BallisticPredictor.PredictGuided(
            probe, ctx.Chunks, _dir, entrySpeed, startGrounded: false,
            ctx.Gravity, ctx.Dt, H, s.Samples, rowCount > 0 ? s.TickDv : null);
        ClearanceConstraintBuilder.Build(
            ctx.Chunks, probe.Polygon, s.Samples, n,
            cfg.CorrectorMargin, ClearanceConstraintBuilder.DefaultDeepViolation,
            s.Rows, out int truncatedAt);
        // Delivered: some sample BEFORE any unavoided impact reaches the gate band
        // past the lip. CorrectorRefusalResidual is the gate tolerance — the spring
        // settles a couple px below the exact gate line.
        int usable = Math.Min(truncatedAt, n);
        for (int k = 0; k < usable; k++)
        {
            if (s.Samples[k].Pos.Y <= targetY + cfg.CorrectorRefusalResidual
                && _dir * (s.Samples[k].Pos.X - rise.Pos.X) > 0f)
                return true;
        }
        return false;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (ctx.Intent.CurrentHorizontal != _dir) return false;                    // release cancels
        if (vars.TimeInState >= MovementConfig.Current.MaxVaultTime) return false; // stuck → bail
        // Delivered: at gate height and past the lip — then normal arbitration
        // (Standing on the step) claims the next frame.
        bool atHeight = ctx.Body.Position.Y <= vars.MantleTargetY + 1f;
        bool past = _dir == 1
            ? ctx.Body.Position.X > vars.MantleCorner.X + PlayerCharacter.Radius * 0.5f
            : ctx.Body.Position.X < vars.MantleCorner.X - PlayerCharacter.Radius * 0.5f;
        return !(atHeight && past);
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        var cfg = MovementConfig.Current;
        vars.TimeInState = 0f;
        var corridor = ctx.GetCorridor(_dir);
        corridor.TryFirstRise(out var rise);   // precondition just verified it exists
        vars.MantleCorner    = rise.Pos;
        vars.MantleTargetY   = corridor.ClimbTargetY(rise.Column);
        vars.MantleEntryY    = ctx.Body.Position.Y;
        vars.EntrySpeed      = MathF.Max(_dir * ctx.Body.Velocity.X, cfg.MaxWalkSpeed);
        vars.ManeuverChannelPrev = default;
        abilities.Facing = _dir;
        abilities.HasDoubleJumped = false;

        // One-shot entry hop — all the maneuver's injected energy (same sizing the
        // feasibility probe planned with).
        float vy0 = HopVy(ctx, rise, vars.MantleTargetY);
        ctx.Body.Velocity.Y = MathF.Min(ctx.Body.Velocity.Y, -vy0);

        // Reference capture (render-only): the authored arc as planned from the
        // post-hop entry state, uncorrected — frozen for the maneuver's duration.
        if (ctx.Corrector is { CaptureTrajectories: true } cap)
            cap.ReferenceCount = BallisticPredictor.PredictGuided(
                ctx.Body, ctx.Chunks, _dir, vars.EntrySpeed, startGrounded: false,
                ctx.Gravity, ctx.Dt,
                Math.Min(cfg.CorrectorHorizon, BallisticPredictor.MaxHorizon),
                cap.ReferenceTrajectory);
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        // Drop captured trajectories with the maneuver — the overlay must not keep
        // drawing a stale plan after the state hands the body off.
        if (ctx.Corrector is { } s) { s.ReferenceCount = 0; s.BeginFrame(); }
    }

    // Hop sizing: vy so the arc clears the gate + margin BOTH at apex (the
    // pure-ballistic floor) and at the moment the body's face reaches the lip at
    // current speed. The lip term only applies to running entries — for flush/slow
    // starts tLip blows up and the pure apex hop is the honest arc.
    //
    // CEILING CAP: the hop is sized for open sky, but the corridor scan knows
    // the ceiling over the whole approach — an arc whose apex would put the
    // head into it is a head-bonk, not a climb. Cap vy so the apex keeps head
    // clearance under the LOWEST ceiling across the arc's columns; if the
    // capped arc can't deliver, the feasibility gate refuses and the ambient
    // fold (which hops tunnel bumps within its own ceiling budget) keeps the
    // body. This is what keeps the climb family out of tight tunnels.
    protected float HopVy(EnvironmentContext ctx, in CorridorCorner rise, float targetY)
    {
        var cfg = MovementConfig.Current;
        float needH = MathF.Max(0f, ctx.Body.Position.Y - targetY) + cfg.ArcJumpApexMargin;
        float vyApex = BallisticPredictor.BallisticVy(needH);
        float vy0    = vyApex;
        float vx     = _dir * ctx.Body.Velocity.X;
        float dist   = _dir * (rise.Pos.X - ctx.Body.Bounds.Side(_dir));
        // The lip term is a TIMING model — "clear the gate by the time the face
        // arrives" — and only means something while the face is genuinely
        // ahead. At dist → 0 it degenerates (tLip → 0, vyLip → ∞): a face
        // already at the leading edge can't be beaten by any launch, so the
        // apex hop is the honest arc there.
        if (vx > cfg.MantleMaxEntrySpeed && dist > 2f)
        {
            float tLip  = dist / vx;
            float vyLip = needH / tLip + 0.5f * Simulation.WorldGravityY * tLip;
            vy0 = MathF.Max(vyApex, vyLip);
            // Assist cap (movement_todo #3): the lip term is a demand, not an
            // entitlement — an automatic hop never exceeds what a deliberate
            // ground jump could author. If the capped arc can't make the
            // timing, the feasibility gate (which plans with this same
            // sizing) refuses and the player jumps it themselves.
            vy0 = MathF.Min(vy0, cfg.MaxAssistRiseSpeed);
        }

        var corridor = ctx.GetCorridor(_dir);
        float ceil = float.NegativeInfinity;   // y-down: larger = lower = more restrictive
        int lastCol = Math.Min(rise.Column + 2, corridor.ColumnCount - 1);
        for (int i = 0; i <= lastCol; i++)
            if (!float.IsNegativeInfinity(corridor.CeilY[i]) && corridor.CeilY[i] > ceil)
                ceil = corridor.CeilY[i];
        if (!float.IsNegativeInfinity(ceil))
        {
            float halfHeight = (PlayerCharacter.StandingHeight - PlayerCharacter.Radius) * 0.5f;
            float apexCenterMin = ceil + halfHeight + CeilingClearMargin;
            float vyCap = BallisticPredictor.BallisticVy(MathF.Max(0f, ctx.Body.Position.Y - apexCenterMin));
            vy0 = MathF.Min(vy0, vyCap);
        }
        return vy0;
    }

    private const float CeilingClearMargin = 2f;   // px of head clearance the hop must keep

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        var cfg = MovementConfig.Current;
        vars.TimeInState += ctx.Dt;

        _gripCorner = vars.MantleCorner;
        _hasGrip = true;
        float span = vars.MantleEntryY - vars.MantleTargetY;
        _progress = span > 1f
            ? Math.Clamp((vars.MantleEntryY - ctx.Body.Position.Y) / span, 0f, 1f)
            : 0f;

        // ── Authored feedforward: entry-speed drive + ballistic envelope ──
        var force = Vector2.Zero;
        force.X = AirControl.SoftClampVelocity(ctx.Body.Velocity.X, _dir * vars.EntrySpeed,
                                               cfg.WalkAccel, ctx.Dt);
        float remaining = ctx.Body.Position.Y - vars.MantleTargetY;
        if (remaining > 1f)
        {
            // Ballistic honesty: gravity owns the ascent; brake only past the
            // free-fall envelope, lift only if the rollout went under-budget.
            float vyAllow = BallisticPredictor.BallisticVy(remaining);
            if (ctx.Body.Velocity.Y < -vyAllow && ctx.Dt > 0f)
                force.Y = (-vyAllow - ctx.Body.Velocity.Y) / ctx.Dt;
            else if (ctx.Body.Velocity.Y > -0.25f * vyAllow)
                force.Y = -cfg.VaultLiftForce;
        }
        else
        {
            // Crest: kill residual rise, push over the lip. The push is a
            // CLAMP toward entry speed, not an open throttle (movement_todo
            // #5): a stalled crest gets the full shove up to EntrySpeed, a
            // fast one gets nothing — the vault keeps the speed it arrived
            // with, it doesn't mint more (this line was +500 unconditional
            // and pushed a 150 px/s entry to 175 over the lip).
            if (ctx.Body.Velocity.Y < 0f && ctx.Dt > 0f)
                force.Y = MathF.Min(-ctx.Body.Velocity.Y / ctx.Dt, 2f * cfg.VaultLiftForce);
            force.X = AirControl.SoftClampVelocity(ctx.Body.Velocity.X, _dir * vars.EntrySpeed,
                                                   cfg.VaultPushForce, ctx.Dt);
        }
        ctx.Body.AppliedForce = force;

        // ── Corrector: two outer passes of predict → rows → solve, apply z₀
        // (ManeuverCorrector.Apply: correction into AppliedForce, Δ anchors,
        // ledger record). ──
        ManeuverCorrector.Apply(ctx, _dir, vars.EntrySpeed, ref vars.ManeuverChannelPrev);
    }
}

// The at-speed 1-block vault (build step 4). Slow/flush 1-block entries belong
// to MantleState.
public class ParkourState : ClimbManeuverBase
{
    public ParkourState(int dir) : base(dir) { }
    protected override float RiseBandMin => MovementConfig.Current.MantleMinRise;
    protected override float RiseBandMax => MovementConfig.Current.MantleMaxRise;
    protected override bool  RequiresRunningEntry => true;
}

// The slow/flush 1-block climb: same band as the Parkour vault, claiming the
// entries AT or below the speed gate — the deliberate walk-up/stalled case.
// Same hop-plus-envelope motion; from near-rest the hop is the pure-apex
// ballistic, which reads as the old servo climb without the bespoke shepherd.
public class MantleState : ClimbManeuverBase
{
    public MantleState(int dir) : base(dir) { }
    protected override float RiseBandMin => MovementConfig.Current.MantleMinRise;
    protected override float RiseBandMax => MovementConfig.Current.MantleMaxRise;
    protected override bool  RequiresRunningEntry => false;

    // Complement of the Parkour gate so exactly one of the pair bids per frame.
    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
        => _dir * ctx.Body.Velocity.X <= MovementConfig.Current.MantleMaxEntrySpeed
           && base.CheckPreConditions(ctx, abilities);
}

// The taller climb band (build step 6): rises above the mantle band up to the
// corridor's maneuver envelope (~2 blocks). No mantle partner ⇒ fires at any
// entry speed, including flush-from-rest against the step.
public class ArcJumpState : ClimbManeuverBase
{
    public ArcJumpState(int dir) : base(dir) { }
    protected override float RiseBandMin => MovementConfig.Current.MantleMaxRise;
    protected override float RiseBandMax => MovementConfig.Current.CorridorMaxRise;
    protected override bool  RequiresRunningEntry => false;

    // movement_todo #6: the 2-block arc is a DELIBERATE move — genuinely
    // running in (≥ ArcJumpRunSpeed) with Up held. Standing/slow near a
    // 2-block ledge with Up held routes to LedgeGrab instead (its Passive 42
    // wins automatically once this precondition refuses the still case).
    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
        => ctx.Input.Up
           && _dir * ctx.Body.Velocity.X >= MovementConfig.Current.ArcJumpRunSpeed
           && base.CheckPreConditions(ctx, abilities);
}
