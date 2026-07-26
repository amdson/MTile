using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Pooled per-player scratch for the corrector's predict → build → solve loop.
// Pure derived data, fully rewritten every solve — never snapshot state. The only
// cross-frame corrector state is MovementVars.CorrectorPrevDv (the Δu anchor).
public sealed class CorrectorScratch
{
    public readonly CoastSample[]  Samples  = new CoastSample[BallisticPredictor.MaxHorizon];
    public readonly ClearanceRow[] Rows     = new ClearanceRow[ClearanceConstraintBuilder.MaxEvents];
    public readonly Vector2[]      CoastVel = new Vector2[BallisticPredictor.MaxHorizon];
    public readonly Vector2[]      Z        = new Vector2[BallisticPredictor.MaxHorizon];
    public readonly Vector2[]      ZScratch = new Vector2[BallisticPredictor.MaxHorizon];
    public readonly Vector2[]      TickDv   = new Vector2[BallisticPredictor.MaxHorizon];
    public readonly CorrectionProblem Problem = new()
    {
        Channels    = new ChannelDef[1],
        PrevApplied = new Vector2[1],
    };
}

// Corrector-driven climb family (BALLISTIC_CORRECTOR_PLAN steps 4 + 6): the
// at-speed 1-block vault (ParkourCorrectorState) and the taller arc-jump band
// (ArcJumpCorrectorState) share this machinery; each subclass owns only its rise
// band and entry-speed gate. Behind MovementConfig.CorrectorVaultEnabled for A/B;
// subclass names match the vault-family sim fixtures ("Parkour" / "ArcJump").
//
// Shape: intent generates the reference (a one-shot entry hop sized to clear the
// lip with margin + a guided drive that preserves entry speed); the solver only
// deforms it. Each Update runs the two outer sequential-convexification passes:
// predict the guided coast → build clearance rows → solve (Redirect channel only —
// passive deflections; the hop already injected the maneuver's energy) →
// re-predict WITH the corrections → rebuild → re-solve — then applies z₀ through
// Body.AppliedForce (a velocity-update δv applies as force δv/dt). The ballistic
// crest envelope + gate delivery are the authored feel, not solver output.
//
// Split with MantleState is the existing speed gate: at or below MantleMaxEntrySpeed
// the flush/slow climb belongs to the mantle; above it this state claims the
// approach inside its trigger window. Cancel-on-release and MaxVaultTime liveness
// as everywhere in the climb family. RampPolicy.Off while ReflexSystem coexists.
public abstract class CorrectorClimbBase : MovementState
{
    private const float RedirectEpsilon = 1e-6f;   // uniqueness regularizer, not a knob
    private const float HingeWeight     = 1e6f;    // stiffness constant, not a feel knob

    protected readonly int _dir;
    protected CorrectorClimbBase(int dir) => _dir = dir;

    // The rise band this maneuver claims (px) and whether the body must arrive
    // at speed (the running/flush split against MantleState).
    protected abstract float RiseBandMin { get; }
    protected abstract float RiseBandMax { get; }
    protected abstract bool  RequiresRunningEntry { get; }

    public override AnimTag AnimationTag => AnimTag.Parkour;   // the vault clip family

    public override int ActivePriority  => MovementPriorities.ArcJumpActive;
    public override int PassivePriority => MovementPriorities.ArcJumpPassive;
    public override MovementCapability RequiredCapabilities => MovementCapability.LedgeGrab;
    public override RampPolicy RampPolicy => RampPolicy.Off;

    // Height-fraction progress + lip grip for the hands overlay — pure functions
    // of MovementVars/body, so a restore rebuilds them (ClimbStateBase idiom).
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
        // Running/flush split: the 1-block state leaves at-or-below-gate entries to
        // MantleState; the taller band has no mantle partner and fires at any speed.
        if (RequiresRunningEntry && _dir * ctx.Body.Velocity.X <= cfg.MantleMaxEntrySpeed) return false;

        var corridor = ctx.GetCorridor(_dir);
        if (!corridor.TryFirstRise(out var rise)) return false;
        if (rise.Delta < RiseBandMin || rise.Delta > RiseBandMax) return false;

        float dist = _dir * (rise.Pos.X - ctx.Body.Bounds.Side(_dir));
        if (dist > cfg.CorrectorVaultTriggerDistance) return false;

        // Climb-volume headroom over the body's OWN columns (the corridor only sees
        // ahead of the leading face): a low lip over the trailing half blocks the
        // hop invisibly — refuse rather than wedge (ArcJumpState's lesson).
        float targetY = corridor.ClimbTargetY(rise.Column);
        var bounds = ctx.Body.Bounds;
        foreach (var _ in TileQuery.SolidTilesInRect(ctx.Chunks,
            bounds.Left + 0.5f, targetY - PlayerCharacter.Radius,
            bounds.Right - 0.5f, bounds.Top - 0.5f))
            return false;

        return true;
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
        vars.CorrectorPrevDv = Vector2.Zero;
        abilities.Facing = _dir;
        abilities.HasDoubleJumped = false;

        // One-shot entry hop — all the maneuver's injected energy. vy sized so the
        // arc clears the gate + margin BOTH at apex (the pure-ballistic floor) and
        // at the moment the body's face reaches the lip at current speed (the
        // early-fire case: a shallow apex far from the step is no use).
        float needH = MathF.Max(0f, ctx.Body.Position.Y - vars.MantleTargetY) + cfg.ArcJumpApexMargin;
        float vyApex = SteeringRamp.BallisticVy(needH);
        float vy0    = vyApex;
        float vx     = _dir * ctx.Body.Velocity.X;
        if (vx > cfg.MantleMaxEntrySpeed)
        {
            // At speed the pure-ballistic apex can sit past the lip — also require
            // clearance at the moment the face reaches the lip at current speed.
            // Meaningless for flush/slow entries (tLip blows up), hence the gate.
            float dist  = MathF.Max(1f, _dir * (rise.Pos.X - ctx.Body.Bounds.Side(_dir)));
            float tLip  = dist / vx;
            float vyLip = needH / tLip + 0.5f * Simulation.WorldGravityY * tLip;
            vy0 = MathF.Max(vyApex, vyLip);
        }
        ctx.Body.Velocity.Y = MathF.Min(ctx.Body.Velocity.Y, -vy0);
    }

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
            float vyAllow = SteeringRamp.BallisticVy(remaining);
            if (ctx.Body.Velocity.Y < -vyAllow && ctx.Dt > 0f)
                force.Y = (-vyAllow - ctx.Body.Velocity.Y) / ctx.Dt;
            else if (ctx.Body.Velocity.Y > -0.25f * vyAllow)
                force.Y = -cfg.VaultLiftForce;
        }
        else
        {
            // Crest: kill residual rise, push over the lip.
            if (ctx.Body.Velocity.Y < 0f && ctx.Dt > 0f)
                force.Y = MathF.Min(-ctx.Body.Velocity.Y / ctx.Dt, 2f * cfg.VaultLiftForce);
            force.X = _dir * cfg.VaultPushForce;
        }
        ctx.Body.AppliedForce = force;

        // ── Corrector: two outer passes of predict → rows → solve, apply z₀ ──
        var s = ctx.Corrector;
        if (s == null) return;   // hand-built test contexts without scratch: authored arc only

        int H = Math.Min(cfg.CorrectorHorizon, BallisticPredictor.MaxHorizon);
        float residual = 0f;
        int rowCount = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            int n = BallisticPredictor.PredictGuided(
                ctx.Body, ctx.Chunks, _dir, vars.EntrySpeed, startGrounded: false,
                ctx.Gravity, ctx.Dt, H, s.Samples, pass == 0 ? null : s.TickDv);
            rowCount = ClearanceConstraintBuilder.Build(
                ctx.Chunks, ctx.Body.Polygon, s.Samples, n,
                cfg.CorrectorMargin, ClearanceConstraintBuilder.DefaultDeepViolation,
                s.Rows, out _);
            if (rowCount == 0 && pass == 0) { residual = 0f; break; }   // provably silent coast

            for (int k = 0; k < n; k++) s.CoastVel[k] = s.Samples[k].Vel;
            var p = s.Problem;
            p.H = n; p.Dt = ctx.Dt;
            p.CoastVel = s.CoastVel;
            p.Rows = s.Rows; p.RowCount = rowCount;
            p.ChannelCount = 1;
            p.Channels[0] = new ChannelDef
            {
                Lever = LeverKind.VelocityUpdate, Weight = RedirectEpsilon,
                Redirect = true, ActiveFrom = 0, ActiveTo = n,
            };
            p.PrevApplied[0] = vars.CorrectorPrevDv;
            p.DeltaWeight = cfg.CorrectorDeltaWeight;
            p.HingeWeight = HingeWeight;

            residual = CorrectionSolver.Solve(p, s.Z, s.ZScratch);
            for (int k = 0; k < n; k++) s.TickDv[k] = s.Z[k];
        }

        if (rowCount > 0)
        {
            // z₀ exits through AppliedForce: a velocity-update δv₀ applies as δv₀/dt
            // (identical under semi-implicit Euler). Mid-commitment: least-violation
            // best-effort — the residual is a signal, never a silent clip.
            if (ctx.Dt > 0f) ctx.Body.AppliedForce += s.Z[0] / ctx.Dt;
            vars.CorrectorPrevDv = s.Z[0];
        }
        else
        {
            vars.CorrectorPrevDv = Vector2.Zero;
        }
    }
}

// The at-speed 1-block vault (build step 4) — the case the benched ParkourState's
// reflex ramps vacated. Slow/flush 1-block entries belong to MantleState.
public class ParkourCorrectorState : CorrectorClimbBase
{
    public ParkourCorrectorState(int dir) : base(dir) { }
    protected override float RiseBandMin => MovementConfig.Current.MantleMinRise;
    protected override float RiseBandMax => MovementConfig.Current.MantleMaxRise;
    protected override bool  RequiresRunningEntry => true;
}

// The taller climb band (build step 6): rises above the mantle band up to the
// corridor's maneuver envelope (~2 blocks). No mantle partner ⇒ fires at any
// entry speed, including flush-from-rest against the step.
public class ArcJumpCorrectorState : CorrectorClimbBase
{
    public ArcJumpCorrectorState(int dir) : base(dir) { }
    protected override float RiseBandMin => MovementConfig.Current.MantleMaxRise;
    protected override float RiseBandMax => MovementConfig.Current.CorridorMaxRise;
    protected override bool  RequiresRunningEntry => false;
}
