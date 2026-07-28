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
    // Z layout is z[c·H + k] — sized for the full channel stack.
    public readonly Vector2[]      Z        = new Vector2[CorrectionSolver.MaxChannels * BallisticPredictor.MaxHorizon];
    public readonly Vector2[]      ZScratch = new Vector2[CorrectionSolver.MaxChannels * BallisticPredictor.MaxHorizon];
    public readonly Vector2[]      TickDv   = new Vector2[BallisticPredictor.MaxHorizon];
    // Per-channel per-tick activation masks + velocity-conditioned caps (frozen
    // from the coast each solve), and cross-frame Δ anchors (last applied z per
    // channel). TEMP EXPERIMENT: ChannelPrev is cross-frame state that is NOT
    // snapshotted — rollback determinism is suspended for the stack experiment.
    public readonly bool[][]  ChannelMask = MakeMasks();
    public readonly float[][] ChannelCap  = MakeCaps();
    public readonly Vector2[] ChannelPrev = new Vector2[CorrectionSolver.MaxChannels];
    private static bool[][] MakeMasks()
    {
        var m = new bool[CorrectionSolver.MaxChannels][];
        for (int c = 0; c < m.Length; c++) m[c] = new bool[BallisticPredictor.MaxHorizon];
        return m;
    }
    private static float[][] MakeCaps()
    {
        var m = new float[CorrectionSolver.MaxChannels][];
        for (int c = 0; c < m.Length; c++) m[c] = new float[BallisticPredictor.MaxHorizon];
        return m;
    }
    public readonly CorrectionProblem Problem = new()
    {
        Channels    = new ChannelDef[CorrectionSolver.MaxChannels],
        PrevApplied = new Vector2[CorrectionSolver.MaxChannels],
    };
    // Hypothetical-state probe for feasibility-as-trigger: CheckPreConditions
    // rolls the WOULD-BE maneuver (post-hop state) through the same predict →
    // rows → solve loop without touching the real body. Polygon is re-pointed at
    // the owning body's before every use.
    public readonly PhysicsBody ProbeBody =
        new(Polygon.CreateRegular(PlayerCharacter.Radius, 6), Vector2.Zero);

    // ── Trajectory capture for the debug overlay (render-only diagnostics) ──
    // CaptureTrajectories is set by the HOST from its draw flags; the sim only
    // gates capture WORK on it — the captured buffers are never read by sim
    // logic, so the flag cannot affect simulation state. Reference = the arc
    // planned at Enter (frozen for the maneuver); Ballistic = this tick's
    // uncorrected coast; Solved = this tick's coast with the final corrections
    // applied. Ballistic/Solved are cleared every frame (BeginFrame) so the
    // renderer only ever sees trajectories computed THIS timestep.
    public bool CaptureTrajectories;
    public readonly CoastSample[] ReferenceTrajectory = new CoastSample[BallisticPredictor.MaxHorizon];
    public int ReferenceCount;
    public readonly CoastSample[] BallisticTrajectory = new CoastSample[BallisticPredictor.MaxHorizon];
    public int BallisticCount;
    public readonly CoastSample[] SolvedTrajectory = new CoastSample[BallisticPredictor.MaxHorizon];
    public int SolvedCount;

    // Per-contact push attribution for the APPLIED solve this frame (ambient or
    // maneuver — at most one applies per frame): each clearance row's predicted
    // contact position (body center at the row's tick) and the δv it shoved into
    // the applied tick-0 correction (CorrectionProblem.RowPush; force = δv/dt).
    // Cleared every frame; empty whenever nothing was applied.
    public readonly Vector2[] RowPush    = new Vector2[ClearanceConstraintBuilder.MaxEvents];
    public readonly Vector2[] ContactPos = new Vector2[ClearanceConstraintBuilder.MaxEvents];
    public readonly Vector2[] ContactDv  = new Vector2[ClearanceConstraintBuilder.MaxEvents];
    public int ContactCount;

    public void BeginFrame() { BallisticCount = 0; SolvedCount = 0; ContactCount = 0; }
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
// as everywhere in the climb family. AmbientPolicy.Off — the ambient corrector
// must never fight an owned maneuver.
public abstract class CorrectorClimbBase : MovementState
{
    private const float RedirectEpsilon = 1e-6f;   // uniqueness regularizer, not a knob
    private const float HingeWeight     = 1e6f;    // stiffness constant, not a feel knob
    // TEMP EXPERIMENT (throwaway): see AmbientCorrector — force channel replaces
    // the redirect disc in RunCorrector below.
    private const float ForceRegWeight  = 1f;
    // Entry feasibility solves the FULL arc once per candidate frame and can afford
    // a deeper fixed iteration budget than the per-tick re-solve (opposed-row
    // schedules — the slalom class — need the extra sweeps; see the anchor tests).
    private const int FeasibilityIterations = 128;

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
    public override AmbientPolicy AmbientPolicy => AmbientPolicy.Off;

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

        RunCorrector(ctx, probe, entrySpeed, Vector2.Zero, out int rowCount,
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
        vars.CorrectorPrevDv = Vector2.Zero;
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
    protected float HopVy(EnvironmentContext ctx, in CorridorCorner rise, float targetY)
    {
        var cfg = MovementConfig.Current;
        float needH = MathF.Max(0f, ctx.Body.Position.Y - targetY) + cfg.ArcJumpApexMargin;
        float vyApex = BallisticPredictor.BallisticVy(needH);
        float vy0    = vyApex;
        float vx     = _dir * ctx.Body.Velocity.X;
        if (vx > cfg.MantleMaxEntrySpeed)
        {
            float dist  = MathF.Max(1f, _dir * (rise.Pos.X - ctx.Body.Bounds.Side(_dir)));
            float tLip  = dist / vx;
            float vyLip = needH / tLip + 0.5f * Simulation.WorldGravityY * tLip;
            vy0 = MathF.Max(vyApex, vyLip);
        }
        return vy0;
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
            float vyAllow = BallisticPredictor.BallisticVy(remaining);
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

        RunCorrector(ctx, ctx.Body, vars.EntrySpeed, vars.CorrectorPrevDv, out int rowCount,
                     capture: s.CaptureTrajectories);

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

    // The two outer sequential-convexification passes over `body`'s state (the real
    // body during Update; the pooled probe during trigger feasibility): predict the
    // guided coast → build rows → solve; re-rollout WITH the corrections → rebuild →
    // re-solve. Leaves s.Z holding the plan and returns the linear-model residual of
    // the last pass (0 when the coast is provably silent — no rows on pass 1).
    private float RunCorrector(EnvironmentContext ctx, PhysicsBody body,
                               float entrySpeed, Vector2 prevDv, out int rowCount,
                               int iterations = CorrectionSolver.DefaultInnerIterations,
                               bool capture = false)
    {
        var cfg = MovementConfig.Current;
        var s = ctx.Corrector;
        int H = Math.Min(cfg.CorrectorHorizon, BallisticPredictor.MaxHorizon);
        float residual = 0f;
        rowCount = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            int n = BallisticPredictor.PredictGuided(
                body, ctx.Chunks, _dir, entrySpeed, startGrounded: false,
                ctx.Gravity, ctx.Dt, H, s.Samples, pass == 0 ? null : s.TickDv);
            if (capture && pass == 0)
            {
                Array.Copy(s.Samples, s.BallisticTrajectory, n);
                s.BallisticCount = n;
            }
            rowCount = ClearanceConstraintBuilder.Build(
                ctx.Chunks, body.Polygon, s.Samples, n,
                cfg.CorrectorMargin, ClearanceConstraintBuilder.DefaultDeepViolation,
                s.Rows, out _);
            if (rowCount == 0 && pass == 0) return 0f;   // provably silent coast

            for (int k = 0; k < n; k++) s.CoastVel[k] = s.Samples[k].Vel;
            var p = s.Problem;
            p.H = n; p.Dt = ctx.Dt;
            p.CoastVel = s.CoastVel;
            p.Rows = s.Rows; p.RowCount = rowCount;
            p.ChannelCount = 1;
            // TEMP EXPERIMENT (throwaway): force channel instead of the redirect
            // disc — see AmbientCorrector. Original:
            //   Lever = VelocityUpdate, Weight = RedirectEpsilon, Redirect = true
            p.Channels[0] = new ChannelDef
            {
                Lever = LeverKind.VelocityUpdate, Weight = ForceRegWeight,
                Redirect = false, Cap = float.MaxValue, ActiveFrom = 0, ActiveTo = n,
            };
            p.PrevApplied[0] = prevDv;
            p.DeltaWeight = cfg.CorrectorDeltaWeight;
            p.HingeWeight = HingeWeight;
            p.InnerIterations = iterations;
            // Contact-push attribution on the final pass only (the applied plan).
            p.RowPush = capture && pass == 1 ? s.RowPush : null;

            residual = CorrectionSolver.Solve(p, s.Z, s.ZScratch);
            for (int k = 0; k < n; k++) s.TickDv[k] = s.Z[k];

            if (capture && pass == 1)
            {
                s.ContactCount = rowCount;
                for (int j = 0; j < rowCount; j++)
                {
                    s.ContactPos[j] = s.Samples[s.Rows[j].Tick].Pos;
                    s.ContactDv[j]  = s.RowPush[j];
                }
            }
        }
        if (capture && rowCount > 0)
        {
            // Solved capture (render-only): one extra rollout with the FINAL plan
            // applied — the corrected trajectory the residual story is about.
            s.SolvedCount = BallisticPredictor.PredictGuided(
                body, ctx.Chunks, _dir, entrySpeed, startGrounded: false,
                ctx.Gravity, ctx.Dt, H, s.SolvedTrajectory, s.TickDv);
        }
        return residual;
    }
}

// The at-speed 1-block vault (build step 4) — the case the benched ParkourState's
// reflex ramps vacated. Slow/flush 1-block entries belong to MantleCorrectorState.
public class ParkourCorrectorState : CorrectorClimbBase
{
    public ParkourCorrectorState(int dir) : base(dir) { }
    protected override float RiseBandMin => MovementConfig.Current.MantleMinRise;
    protected override float RiseBandMax => MovementConfig.Current.MantleMaxRise;
    protected override bool  RequiresRunningEntry => true;
}

// The slow/flush 1-block climb (removal-pass port of the old ClimbStateBase
// MantleState onto the corrector loop): same band as the Parkour vault, claiming
// the entries AT or below the speed gate — the deliberate walk-up/stalled case.
// Same hop-plus-envelope motion; from near-rest the hop is the pure-apex
// ballistic, which reads as the old servo climb without the bespoke shepherd.
public class MantleCorrectorState : CorrectorClimbBase
{
    public MantleCorrectorState(int dir) : base(dir) { }
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
public class ArcJumpCorrectorState : CorrectorClimbBase
{
    public ArcJumpCorrectorState(int dir) : base(dir) { }
    protected override float RiseBandMin => MovementConfig.Current.MantleMaxRise;
    protected override float RiseBandMax => MovementConfig.Current.CorridorMaxRise;
    protected override bool  RequiresRunningEntry => false;
}
