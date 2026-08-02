using System;
using Microsoft.Xna.Framework;

namespace MTile;

// The reference-rollout fold engine (MovementConfig.FoldEngine "ref") — the
// stand fold recast in the guided-state architecture. Instead of predicting a
// passive ballistic coast and asking a channel stack to synthesize locomotion
// (the "qp" engine), this engine GENERATES the trajectory the body should
// ride — a walk-speed carry along the terrain floor envelope at hover height
// — sweeps THAT through the constraint builder (rows live where the body will
// actually go, closing the convexification gap that let the qp plan cross
// exclusion regions), bends the path around terrain with a position-space
// deform (the bend IS the automatic duck / crest yield), and tracks the
// deformed tick-0 target with a velocity servo. Walls are CLASSIFIED at row
// emission (duck/step within budget, else give-up truncation — see Build's
// wallEscape params), never filtered invisible.
//
// Reference-generation rules (the physical-honesty set, enforced in the
// rollout itself rather than as row gating):
//   - x carries at dir·MaxSpeed, ramped by WalkAccel (friction ramp at rest)
//     — the reference never demands acceleration the drive doesn't have;
//   - y tracks envelope − hover, DESCENDING no faster than gravity (a floor
//     cannot pull — the old down-anchor rate limit, now per-rollout with no
//     cross-frame state) and RISING no faster than SpringMaxRiseSpeed;
//   - the climb band measures from the CURRENT support anchor (a transient
//     rise cannot ladder the reference up a wall);
//   - off a lip past SupportReach the tail free-falls.
//
// Scope: the ANCHORED standing regime only. Launched, plunging, knocked-back
// or airborne-beyond-reach bodies fall back to the ballistic-qp path
// (AmbientCorrector.Apply's normal flow) — TryApply returns false.
// Deliberately not carried over yet: elective climb refusal, corner plants,
// per-tile ledger attribution.
public static class FoldReference
{
    private const float HingeWeight  = 1e6f;    // stiffness constant, not a knob
    private const float DeformWeight = 1e-3f;   // regularizer: prefer the raw reference
    // Position-space deform authority: how far the path may deviate from the
    // reference (px), and how fast that deviation may build (px/s — the
    // implied extra velocity; the old velocity-update cap's role). Half a
    // tile of position authority covers ducks/steps the classification
    // admits; anything needing more was already a give-up at row emission.
    private const float DeformCap = Chunk.TileSize * 0.5f;
    private const float SlewCap   = 150f;

    // Generates the reference, deforms it around terrain, and servos the body
    // toward its tick-0 target. Returns false when the regime doesn't apply
    // (caller falls back to the ballistic-qp path).
    public static bool TryApply(EnvironmentContext ctx, CorrectorScratch s,
                                in FoldProfile fold, AmbientPolicy policy, int dir,
                                ref MovementVars vars)
    {
        var cfg = MovementConfig.Current;
        if (s == null || ctx.Dt <= 0f) return false;
        var body = ctx.Body;

        if (ctx.Modifiers.PreserveExternalVelocity) return false;          // knockback
        if (-body.Velocity.Y > cfg.SpringMaxRiseSpeed) return false;       // launched
        if (body.Velocity.Y > cfg.MaxGroundEngageVnRel) return false;      // plunging

        var template = CObstacleTemplate.For(body.Polygon);
        float anchorY = AmbientCorrector.FloorEnvelope(ctx.Chunks, template, body.Position.X,
            body.Position.Y - 2f, body.Position.Y + BallisticPredictor.SupportReach,
            out bool anchored);
        if (!anchored) return false;                                       // free flight

        // ── The reference rollout ────────────────────────────────────────
        float dt = ctx.Dt;
        int n = Math.Min(cfg.AmbientHorizon, BallisticPredictor.MaxHorizon);
        float target = dir * fold.MaxSpeed * ctx.Modifiers.MaxWalkSpeed;
        Vector2 pos = body.Position, prev = pos;
        float vx = body.Velocity.X;
        float vy = MathF.Max(0f, body.Velocity.Y);   // descent budget; rising is servo-capped

        for (int k = 0; k < n; k++)
        {
            float accel = dir != 0 ? cfg.WalkAccel
                                   : cfg.GroundFriction * ctx.Modifiers.GroundFriction;
            vx += Math.Clamp(target - vx, -accel * dt, accel * dt);
            pos.X += vx * dt;

            float env = AmbientCorrector.FloorEnvelope(ctx.Chunks, template, pos.X,
                anchorY - fold.ClimbReachUp, anchorY + BallisticPredictor.SupportReach,
                out bool found);
            bool grounded = false;
            if (found)
            {
                float yRef = env - fold.HoverOffset;
                if (yRef >= pos.Y)
                {
                    // Reference below: fall toward it at gravity, no faster.
                    vy += ctx.Gravity.Y * dt;
                    float yNext = pos.Y + vy * dt;
                    if (yNext >= yRef) { pos.Y = yRef; vy = 0f; grounded = true; }
                    else pos.Y = yNext;
                }
                else
                {
                    // Reference above: rise at most SpringMaxRiseSpeed.
                    pos.Y -= MathF.Min(pos.Y - yRef, cfg.SpringMaxRiseSpeed * dt);
                    vy = 0f;
                    grounded = true;
                }
            }
            else
            {
                vy += ctx.Gravity.Y * dt;
                pos.Y += vy * dt;
            }

            s.Samples[k].Pos      = pos;
            s.Samples[k].Vel      = (pos - prev) / dt;
            s.Samples[k].Grounded = grounded;
            s.Samples[k].FloorY   = found ? env : float.PositiveInfinity;
            s.CoastVel[k] = s.Samples[k].Vel;
            prev = pos;
        }

        // ── Rows along the reference, walls CLASSIFIED (not filtered): a
        // frontal obstacle becomes a duck/step row when its vertical escape
        // fits the climb/duck budgets, else the scan truncates there — the
        // give-up. No rows behind an unservable wall means the deform is
        // identity on the approach and the servo degenerates to the raw
        // flat-ground carry: full-speed honest bonk.
        int rowCount = ClearanceConstraintBuilder.Build(
            ctx.Chunks, body.Polygon, s.Samples, n,
            cfg.CorrectorMargin, ClearanceConstraintBuilder.DefaultDeepViolation,
            s.Rows, out _,
            wallEscapeUp: fold.ClimbReachUp, wallEscapeDown: cfg.FoldDuckReach);
        int kept = 0;
        for (int j = 0; j < rowCount; j++)
        {
            bool up = s.Rows[j].Normal.Y < 0f;
            if (up && s.Samples[s.Rows[j].Tick].Vel.Y > cfg.MaxGroundEngageVnRel) continue;
            if (up ? policy.Over : policy.Under)
                s.Rows[kept++] = s.Rows[j];
        }
        rowCount = kept;

        if (s.CaptureTrajectories)
        {
            Array.Copy(s.Samples, s.BallisticTrajectory, n);   // the raw reference
            s.BallisticCount = n;
        }

        // ── Deform (PathDeform), then servo the tick-0 target ────────────
        Vector2 z0 = Vector2.Zero;
        if (rowCount > 0)
        {
            var p = s.Problem;
            p.H = n; p.Dt = dt;
            p.CoastVel = s.CoastVel;
            p.Rows = s.Rows; p.RowCount = rowCount;
            p.ChannelCount = 1;
            // VERTICAL-ONLY deform (the anti-autopilot doctrine as an axis
            // lock): the fold reshapes the path up/down — hover yields, ducks,
            // crest bends — never along x. Without the lock, a 1px lip-bevel
            // graze at tick 0 gets served along its diagonal normal, and the
            // backward component cancels the whole carry (measured: the
            // corridor-mouth stall). Braking-for-terrain is a give-up, not a
            // deformation.
            p.Channels[0] = new ChannelDef
            {
                Id = CorrectionChannel.PathDeform,
                Lever = LeverKind.PositionOffset, Weight = DeformWeight,
                Cap = DeformCap, SlewCap = SlewCap,
                Axis = new Vector2(0f, 1f), AxisOnly = true,
                ActiveFrom = 0, ActiveTo = n,
            };
            // d[−1] ≡ 0: the path starts AT the body. The Δ-chain's k = 0
            // anchor doubles as the seam term (no cross-frame leak — the
            // receding horizon re-derives the offsets each frame).
            p.PrevApplied[0] = Vector2.Zero;
            p.DeltaWeight = cfg.CorrectorDeltaWeight;
            p.HingeWeight = HingeWeight;
            p.InnerIterations = cfg.FoldIterations;
            p.RowPush = s.RowPush;

            CorrectionSolver.Solve(p, s.Z, s.ZScratch);
            z0 = s.Z[0];

            if (s.CaptureTrajectories)
            {
                // Deformed path: pos′_T = pos_T + d_T (position-space offsets).
                for (int T = 0; T < n; T++)
                {
                    s.SolvedTrajectory[T] = s.Samples[T];
                    s.SolvedTrajectory[T].Pos += s.Z[T];
                }
                s.SolvedCount = n;
                s.ContactCount = rowCount;
                for (int j = 0; j < rowCount; j++)
                {
                    s.ContactPos[j] = s.Samples[s.Rows[j].Tick].Pos;
                    s.ContactDv[j]  = s.RowPush[j];
                }
            }
        }
        vars.AmbientChannelPrev = default;
        vars.AmbientPrevDv = Vector2.Zero;

        // The rollout starts AT the body, so tick-0 position error is zero by
        // construction and a velocity servo suffices: correct the next
        // integrated velocity toward the deformed reference's. z0 is a
        // position offset (px); with d[−1] = 0 its velocity contribution is
        // z0/dt, slew-bounded. Measured against what AppliedForce already
        // holds (state baseline + action forces) so nothing double-counts;
        // capped like the guided servos.
        Vector2 v1Des = s.Samples[0].Vel + z0 / dt;
        Vector2 v1Cur = body.Velocity + (body.AppliedForce + ctx.Gravity) * dt;
        Vector2 dF = (v1Des - v1Cur) / dt;
        float mag = dF.Length();
        if (mag > cfg.GuidedMaxForce) dF *= cfg.GuidedMaxForce / mag;
        body.AppliedForce += dF;
        return true;
    }
}
