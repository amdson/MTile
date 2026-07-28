using System;
using Microsoft.Xna.Framework;

namespace MTile;

// What the active movement state allows the ambient corrector to do this frame.
// Published fresh every frame on the MovementModifiers pattern — a per-frame
// channel, not a lifecycle. Default = both assists on (plain run/jump/fall get
// the reflex layer); states that servo the body against fixed contacts (the
// climb family, the ledge family, launches) publish Off so the ambient layer
// never fights an owned maneuver.
public struct AmbientPolicy
{
    public bool Over;    // corner-lift assist (upward rows)
    public bool Under;   // head-tuck assist (downward rows)

    public static AmbientPolicy Default => new() { Over = true, Under = true };
    public static AmbientPolicy Off     => default;
}

// Ambient corrector mode (BALLISTIC_CORRECTOR_PLAN step 7) — the corrector run
// during FREE movement. The "reference" is the coast prediction itself (no
// authored arc, no fidelity term): predict the free coast under the current
// input, emit clearance rows for PASSABLE features (vertical faces — lips and
// corner tops; wall faces emit nothing and the coast deep-truncates), solve,
// apply z₀.
//
// ── The stand fold (Plans/CORRECTOR_CONSOLIDATION_PLAN.md) ────────────────────
// StandingState's hover spring + ground FSD are FOLDED INTO this solve — "the
// solver IS the locomotion controller" for the free states:
//  - While the current state is a fold state (Standing/Falling), soft per-tick
//    hover-support rows (Normal up, HingeScale ≪ 1) are synthesized wherever the
//    coast has a floor in reach below hover height. The solver's plan holds the
//    body at hover — and YIELDS under hard obstacle rows, which is the duck /
//    crest / landing-catch mechanic in one piece.
//  - The solve runs unconditionally (no input gate — hover must hold at rest,
//    which also removes the vx=0 liveness deadlock) and the plan is ALWAYS
//    applied in fold mode (support is load-bearing; feasible-only gating would
//    drop the body). Anti-autopilot refusal survives for non-fold states, and
//    returns for the fold's ELECTIVE tier via CORRECTOR_CONSOLIDATION_PLAN §4.
//  - Actuation is the restricted channel stack (BuildStandChannels): LegServo /
//    Traction / CornerAssist / Redirect / Tuck with per-tick masks and caps,
//    solved JOINTLY — senses are never partitioned (an up/down homotopy split
//    would strip support rows from a down pass).
public sealed class AmbientCorrector
{
    private const float HingeWeight     = 1e6f;    // stiffness constant, not a knob
    private const float RedirectEpsilon = 1e-6f;   // uniqueness regularizer, not a knob
    // Stand-fold constants: hover target ≈ the old spring equilibrium
    // (minDistance 24 − gravity/SpringK 2), so the rest pose matches the old
    // look. Support rows are soft (HingeScale ≪ 1) so hard clearance rows can
    // overpower them; the deeper fixed iteration budget keeps the joint
    // mixed-sense solve converged.
    private const float HoverDist       = 2f * PlayerCharacter.Radius - 2f;
    private const float HoverHingeScale = 0.02f;
    private const float ProgressHingeScale = 0.02f;
    private const int   FoldIterations  = 16;
    // Envelope reference: hover clearance above the C-obstacle top surface
    // (center rests ON the surface at 0; 10 ≈ the old spring equilibrium), and
    // the band of floors the reference may bind to — up to ClimbReachUp above
    // the coast (a 1-high ledge ramps the reference; a 2-high wall does not)
    // and LegReachDown below (further down = free fall, no hover request).
    private const float HoverAboveSurface = 10f;
    private const float ClimbReachUp      = 20f;
    private const float LegReachDown      = 30f;
    // Per-direction actuator honesty: the envelope may excursion BELOW the
    // anchor surface only within the tuck's authority (tiny standing-height
    // adjustments). A bigger drop (a lip) unbinds the reference — no down
    // rows, the tail is free fall; the landing catch resumes when the anchor
    // itself re-binds the lower floor. Mirrors ClimbReachUp's role for up.
    private const float TuckReachDown     = 4f;

    // Highest (min-y) C-obstacle top surface at horizontal position x, among
    // solid standable tiles (open above) whose surface lies within the vertical
    // band [bandMinY, bandMaxY]. The bevel facets ramp this smoothly over lips.
    private static float FloorEnvelope(ChunkMap chunks, CObstacleTemplate t, float x,
                                       float bandMinY, float bandMaxY, out bool found)
    {
        int ts = Chunk.TileSize;
        float half = ts * 0.5f;
        found = false;
        float env = float.MaxValue;
        int cMin = (int)MathF.Floor((x - t.XExtent) / ts), cMax = (int)MathF.Floor((x + t.XExtent) / ts);
        int rMin = (int)MathF.Floor((bandMinY - t.Reach) / ts);
        int rMax = (int)MathF.Floor((bandMaxY + t.Reach) / ts);
        for (int gtx = cMin; gtx <= cMax; gtx++)
        for (int gty = rMin; gty <= rMax; gty++)
        {
            float cx = gtx * ts + half, cy = gty * ts + half;
            if (!TileQuery.IsSolidAt(chunks, cx, cy)) continue;
            if (TileQuery.IsSolidAt(chunks, cx, cy - ts)) continue;   // not standable
            float ry = t.TopSurfaceRy(x - cx, out bool valid);
            if (!valid) continue;
            float yTop = cy + ry;
            if (yTop < bandMinY || yTop > bandMaxY) continue;
            if (yTop < env) { env = yTop; found = true; }
        }
        return env;
    }

    // Runs after the movement state's Update (its policy + forces are final),
    // before the physics step reads AppliedForce — the slot Reconcile had.
    // solverStand = current state is Standing/Falling: hover support is this
    // solve's job (see the fold note above).
    public void Apply(EnvironmentContext ctx, AmbientPolicy policy, bool startGrounded,
                      bool solverStand, ref MovementVars vars)
    {
        var cfg = MovementConfig.Current;
        var s = ctx.Corrector;
        int dir = ctx.Intent.CurrentHorizontal;

        if (s == null || !cfg.AmbientCorrectorEnabled
            || (!policy.Over && !policy.Under && !solverStand))
        {
            vars.AmbientPrevDv = Vector2.Zero;
            vars.AmbientChannelPrev = default;
            return;
        }

        // Short horizon — within a body length reads as reflexes, beyond reads as
        // autopilot (the corridor plan's boundary, carried over unchanged).
        int H = Math.Min(cfg.AmbientHorizon, BallisticPredictor.MaxHorizon);
        int n = BallisticPredictor.Predict(
            ctx.Body, ctx.Chunks, dir, ctx.Input.Down, startGrounded,
            ctx.Modifiers, ctx.Gravity, ctx.Dt, H, s.Samples);
        int rowCount = ClearanceConstraintBuilder.Build(
            ctx.Chunks, ctx.Body.Polygon, s.Samples, n,
            cfg.CorrectorMargin, ClearanceConstraintBuilder.DefaultDeepViolation,
            s.Rows, out int truncatedAt, verticalFacesOnly: true);

        // Per-sense policy: Over gates upward rows (corner-vault assist), Under
        // gates downward rows (head-tuck assist) — same split AmbientPolicy always had.
        int kept = 0;
        for (int j = 0; j < rowCount; j++)
        {
            bool up = s.Rows[j].Normal.Y < 0f;
            if (up ? policy.Over : policy.Under)
                s.Rows[kept++] = s.Rows[j];
        }
        rowCount = kept;

        // Stand fold: the ENVELOPE REFERENCE — a per-tick reference trajectory
        // (x from held input at target speed; y from the C-space floor envelope
        // at that x, offset by hover clearance) tracked with TWO-SIDED soft
        // rows. The envelope steps up over ledges (smoothly, via the template's
        // corner bevels), so climbing is tracking, overshoot above the reference
        // costs (the crest lands AT hover), and progress requests survive across
        // obstacles instead of dying at truncation.
        if (solverStand)
        {
            var template = CObstacleTemplate.For(ctx.Body.Polygon);
            int lim = Math.Min(n, truncatedAt);
            float target = dir * cfg.MaxWalkSpeed * ctx.Modifiers.MaxWalkSpeed;
            float x0 = ctx.Body.Position.X;
            float y0 = ctx.Body.Position.Y;

            // Support anchor: the surface the body is ACTUALLY standing on (the
            // highest standable surface at-or-below the current pose, within leg
            // reach). The climb band is measured from THIS, not from the coast's
            // own height — so a transient rise cannot re-bind higher floors and
            // ladder the reference up a wall. One-high (16 ≤ ClimbReachUp) binds;
            // two-high (32) never does, whatever the mid-scramble pose.
            float anchorY = FloorEnvelope(ctx.Chunks, template, x0,
                                          y0 - 2f, y0 + LegReachDown, out bool anchored);

            for (int k = 0; k < lim && rowCount < ClearanceConstraintBuilder.MaxEvents - 1; k++)
            {
                float xRef = x0 + target * (k + 1) * ctx.Dt;

                // x-progress tracking (two-sided; dir 0 holds station = brake).
                float errX = xRef - s.Samples[k].Pos.X;
                if (MathF.Abs(errX) > 0.01f)
                    s.Rows[rowCount++] = new ClearanceRow
                    {
                        Tick = k, Normal = new Vector2(MathF.Sign(errX), 0f),
                        Depth = MathF.Abs(errX), HingeScale = ProgressHingeScale,
                    };

                // y tracking of the floor envelope at the reference x, bound to
                // the anchor's climb band. Unanchored (airborne beyond leg
                // reach) ⇒ no hover request: free flight.
                if (!anchored) continue;
                float envY = FloorEnvelope(ctx.Chunks, template, xRef,
                                           anchorY - ClimbReachUp, anchorY + LegReachDown, out bool found);
                if (!found) continue;
                if (envY > anchorY + TuckReachDown) continue;   // lip: unbind, fall naturally
                float yRef = envY - HoverAboveSurface;
                float errY = yRef - s.Samples[k].Pos.Y;   // >0: need down, <0: need up
                if (MathF.Abs(errY) > 0.01f)
                    s.Rows[rowCount++] = new ClearanceRow
                    {
                        Tick = k, Normal = new Vector2(0f, MathF.Sign(errY)),
                        Depth = MathF.Abs(errY), HingeScale = HoverHingeScale,
                    };
            }
        }

        if (rowCount == 0)
        {
            vars.AmbientPrevDv = Vector2.Zero;
            vars.AmbientChannelPrev = default;
            return;
        }

        // Ballistic capture (render-only): the uncorrected coast, published only
        // when it actually implies rows — "the corrector saw something". The
        // always-on preview of a silent coast is DebugDrawCoast's job. In ambient
        // mode this is also the reference: the reference IS the coast (no
        // authored arc, fidelity vanishes), so gold would just duplicate aqua.
        if (s.CaptureTrajectories)
        {
            Array.Copy(s.Samples, s.BallisticTrajectory, n);
            s.BallisticCount = n;
        }

        for (int k = 0; k < n; k++) s.CoastVel[k] = s.Samples[k].Vel;
        var p = s.Problem;
        p.H = n; p.Dt = ctx.Dt;
        p.CoastVel = s.CoastVel;
        p.Rows = s.Rows; p.RowCount = rowCount;

        if (solverStand)
        {
            // The restricted channel stack, solved jointly — every channel
            // contributes perturbations to one plan. Δ anchors live in
            // MovementVars (snapshot-covered), not scratch: rollback restore
            // must reproduce the smoothness chain exactly.
            p.ChannelCount = BuildStandChannels(s, n, rowCount);
            for (int c = 0; c < p.ChannelCount; c++) p.PrevApplied[c] = vars.AmbientChannelPrev[c];
        }
        else
        {
            // Non-fold states get the original ambient channel: the redirect disc,
            // feasible-only (refusal below). Energy honesty — the disc only rotates
            // or sheds the velocity the body already has; ambient assists during an
            // owned state's Default-policy window never inject speed. (An unbounded
            // force channel stood in here during the stand-fold experiments; the
            // disc is restored deliberately.)
            p.ChannelCount = 1;
            p.Channels[0] = new ChannelDef
            {
                Lever = LeverKind.VelocityUpdate, Weight = RedirectEpsilon,
                Redirect = true, ActiveFrom = 0, ActiveTo = n,
            };
            p.PrevApplied[0] = vars.AmbientPrevDv;
        }
        p.DeltaWeight = cfg.CorrectorDeltaWeight;
        p.HingeWeight = HingeWeight;
        p.InnerIterations = solverStand ? FoldIterations : CorrectionSolver.DefaultInnerIterations;
        // Contact-push attribution (render-only; the pooled Problem is shared
        // with the maneuver states, so set it explicitly either way).
        p.RowPush = s.CaptureTrajectories ? s.RowPush : null;

        float residual = CorrectionSolver.Solve(p, s.Z, s.ZScratch);

        // Feasible-only refusal survives ONLY outside the fold: in solverStand
        // mode the plan carries hover support and must always apply.
        if (!solverStand && residual > cfg.AmbientRefusalResidual)
        {
            vars.AmbientPrevDv = Vector2.Zero;
            vars.AmbientChannelPrev = default;
            return;
        }

        // Apply the summed tick-0 perturbation of every channel (VelocityUpdate
        // z is a δv → force δv/dt; Force z is already an acceleration). TickDv
        // accumulates the per-tick total δv for the solved-rollout capture.
        var applied = Vector2.Zero;
        for (int k = 0; k < n; k++) s.TickDv[k] = Vector2.Zero;
        if (!solverStand) vars.AmbientChannelPrev = default;   // fold anchors die with the fold
        for (int c = 0; c < p.ChannelCount; c++)
        {
            var z0 = s.Z[c * n];
            if (solverStand) vars.AmbientChannelPrev[c] = z0;
            bool vu = p.Channels[c].Lever == LeverKind.VelocityUpdate;
            applied += vu && ctx.Dt > 0f ? z0 / ctx.Dt : z0;
            for (int k = 0; k < n; k++)
                s.TickDv[k] += vu ? s.Z[c * n + k] : s.Z[c * n + k] * ctx.Dt;
        }
        ctx.Body.AppliedForce += applied;
        vars.AmbientPrevDv = s.TickDv[0];

        if (s.CaptureTrajectories)
        {
            s.ContactCount = rowCount;
            for (int j = 0; j < rowCount; j++)
            {
                s.ContactPos[j] = s.Samples[s.Rows[j].Tick].Pos;
                s.ContactDv[j]  = s.RowPush[j];
            }
            // Solved capture (render-only): one extra rollout with the full
            // applied plan — the corrected coast the residual story is about.
            s.SolvedCount = BallisticPredictor.Predict(
                ctx.Body, ctx.Chunks, dir, ctx.Input.Down, startGrounded,
                ctx.Modifiers, ctx.Gravity, ctx.Dt, n, s.SolvedTrajectory, s.TickDv);
        }
    }

    // TEMP EXPERIMENT (channel stack): builds the restricted standing channels
    // from the coast — masks and velocity-conditioned caps are frozen per solve
    // (tick 0 uses the body's actual state, so applied perturbations are always
    // truly admissible). Returns the channel count.
    private static int BuildStandChannels(CorrectorScratch s, int n, int rowCount)
    {
        const float LegReach     = HoverDist + 20f;  // px — floor within leg range
        const float LegForce     = 6000f;            // ~10× gravity
        const float VPushMax     = 200f;             // px/s — servo fades out at this rise speed
        const float WalkForce    = 3000f;            // traction cap (≈ WalkAccel)
        const float WeakTraction = 800f;             // scenario: deliberately underpowered legs-forward
        const float CornerForce  = 1500f;
        const float TuckForce    = 1200f;

        Span<bool> near = stackalloc bool[BallisticPredictor.MaxHorizon];
        for (int k = 0; k < n; k++)
        {
            float dist = s.Samples[k].FloorY - s.Samples[k].Pos.Y;
            near[k] = !float.IsPositiveInfinity(s.Samples[k].FloorY) && dist <= LegReach;
        }
        var ch = s.Problem.Channels;

        // TEMP EXPERIMENT (scenario harness): limited channel sets for capability
        // probing, hot-swapped via movement_config.json "CorrectorScenario".
        switch (MovementConfig.Current.CorrectorScenario)
        {
            // Redirect disc (all ticks, free) + weak forward traction, nothing
            // else: can momentum-steering alone deliver the body onto a ledge?
            case "redirect-traction":
                for (int k = 0; k < n; k++)
                {
                    s.ChannelMask[0][k] = near[k];
                    s.ChannelMask[1][k] = true;
                }
                ch[0] = new ChannelDef {
                    Lever = LeverKind.Force, Weight = 0.05f, AxisOnly = true,
                    Axis = new Vector2(1f, 0f), Cap = WeakTraction, ActiveMask = s.ChannelMask[0] };
                ch[1] = new ChannelDef {
                    Lever = LeverKind.VelocityUpdate, Weight = 1e-6f, Redirect = true,
                    ActiveMask = s.ChannelMask[1] };
                return 2;

            // Ground actuation only — no air authority at all: does pure leg
            // servo + traction hold hover and refuse everything aerial?
            case "legs-only":
                for (int k = 0; k < n; k++)
                {
                    s.ChannelMask[0][k] = near[k];
                    s.ChannelMask[1][k] = near[k];
                    float sepL = MathF.Max(0f, -s.Samples[k].Vel.Y);
                    s.ChannelCap[0][k] = LegForce * Math.Clamp(1f - sepL / VPushMax, 0f, 1f);
                }
                ch[0] = new ChannelDef {
                    Lever = LeverKind.Force, Weight = 0.01f, AxisOnly = true, Unilateral = true,
                    Axis = new Vector2(0f, -1f), CapPerTick = s.ChannelCap[0], ActiveMask = s.ChannelMask[0] };
                ch[1] = new ChannelDef {
                    Lever = LeverKind.Force, Weight = 0.05f, AxisOnly = true,
                    Axis = new Vector2(1f, 0f), Cap = WalkForce, ActiveMask = s.ChannelMask[1] };
                return 2;
        }

        // "full": the whole stack.
        for (int k = 0; k < n; k++)
        {
            s.ChannelMask[0][k] = near[k];    // LegServo
            s.ChannelMask[1][k] = near[k];    // Traction
            s.ChannelMask[3][k] = !near[k];   // Redirect
            s.ChannelMask[4][k] = true;       // Tuck — grounded too: with the gravity
                                              // hold in the baseline, "release the
                                              // floor" (ducks, drop-below-hover) is
                                              // a down-channel job on ground ticks
            // LegServo cap fades as separation (upward) speed rises — the servo
            // can push, but never past VPushMax.
            float sep = MathF.Max(0f, -s.Samples[k].Vel.Y);
            s.ChannelCap[0][k] = LegForce * Math.Clamp(1f - sep / VPushMax, 0f, 1f);
        }
        // CornerAssist: active near obstacle features — ticks within ±2 of any
        // HARD row (proxy for "close to the corner").
        for (int k = 0; k < n; k++) s.ChannelMask[2][k] = false;
        for (int j = 0; j < rowCount; j++)
        {
            if (s.Rows[j].HingeScale < 1f) continue;
            for (int k = Math.Max(0, s.Rows[j].Tick - 2); k <= Math.Min(n - 1, s.Rows[j].Tick + 2); k++)
                s.ChannelMask[2][k] = true;
        }

        ch[0] = new ChannelDef {   // LegServo: strong, up-only, near ground
            Lever = LeverKind.Force, Weight = 0.01f, AxisOnly = true, Unilateral = true,
            Axis = new Vector2(0f, -1f), CapPerTick = s.ChannelCap[0], ActiveMask = s.ChannelMask[0] };
        ch[1] = new ChannelDef {   // Traction: horizontal, capped, near ground
            Lever = LeverKind.Force, Weight = 0.05f, AxisOnly = true,
            Axis = new Vector2(1f, 0f), Cap = WalkForce, ActiveMask = s.ChannelMask[1] };
        ch[2] = new ChannelDef {   // CornerAssist: weak, any direction, near features
            Lever = LeverKind.Force, Weight = 0.5f, Cap = CornerForce, ActiveMask = s.ChannelMask[2] };
        ch[3] = new ChannelDef {   // Redirect: Thales disc, airborne, free
            Lever = LeverKind.VelocityUpdate, Weight = 1e-6f, Redirect = true, ActiveMask = s.ChannelMask[3] };
        ch[4] = new ChannelDef {   // Tuck: down-only fast-fall, airborne
            Lever = LeverKind.Force, Weight = 0.5f, AxisOnly = true, Unilateral = true,
            Axis = new Vector2(0f, 1f), Cap = TuckForce, ActiveMask = s.ChannelMask[4] };
        return 5;
    }
}
