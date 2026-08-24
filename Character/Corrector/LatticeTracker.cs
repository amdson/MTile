using System;
using Microsoft.Xna.Framework;

namespace MTile;

// The lattice fold engine's tracker (MovementConfig.FoldEngine "lattice" —
// Plans/LATTICE_PATH_PLANNER.md §3.7): a SHORT-HORIZON channel QP over the
// planned trajectory's first stretch.
//
//   nominal    the exact free rollout — v += (F_baseline + g)·dt, p += v·dt —
//              not a prediction; channel forces enter linearly, so over the
//              horizon the model is exact and nothing is linearized;
//   channels   BuildFold's stack (legs / drive / tuck / redirect / air-lateral
//              / air-vertical) with caps and masks evaluated at the body's
//              CURRENT state and held for the horizon;
//   band       hard rows: stay within half a lattice cell of the reference
//              line — the path's own resolution, not a knob;
//   progress   a soft row at the last tick along the path direction whose
//              depth is what the channels could deliver at their caps (the
//              row saturates the actuators and then rests — "as fast as the
//              channels allow", no target speed);
//   limit      hard rows: displacement along intent ≤ state speed limit × t
//              (the plan's "progress rate ≤ progress-speed target"; jump
//              states will pass none).
//
// Why a horizon at all: a one-tick solve (the previous cut) cannot see that
// it will need to decelerate, so it overshoots — the switching behaviour a
// minimum-time controller needs is what the horizon lets the QP find, for
// every axis and channel asymmetry, without deriving it by hand. Feedback
// is re-planning: the DP's seed is the body every tick.
//
// Reference line: the path's direction at the first node ≥ one cell from
// the body, through that node. dir == 0 / no path (plan §4.6): a level line
// through the hover point under the body — the hover column; unanchored and
// pathless: nothing to track, no force. Not carried over from the qp stack:
// CornerAssist (its mask was the coast's hard rows).
public static class LatticeTracker
{
    private const float HingeWeight = 1e6f;      // the solver's stiffness constant (AmbientCorrector's)
    private const float ProgressHingeScale = 0.02f;

    public static void Apply(EnvironmentContext ctx, CorrectorScratch s,
                             in FoldProfile fold, int dir, ref MovementVars vars)
    {
        var cfg = MovementConfig.Current;
        var body = ctx.Body;
        float dt = ctx.Dt;
        if (s == null || dt <= 0f) return;
        if (ctx.Modifiers.PreserveExternalVelocity)             // knockback: never fought
        {
            vars.AmbientPrevDv = Vector2.Zero; vars.AmbientChannelPrev = default;
            return;
        }
        int H = Math.Min(cfg.AmbientHorizon, BallisticPredictor.MaxHorizon);

        // ── The body's situation (masks and caps are frozen from it) ─────
        var template = CObstacleTemplate.For(body.Polygon);
        float floorY = AmbientCorrector.FloorEnvelope(ctx.Chunks, template, body.Position.X,
            body.Position.Y - 2f, body.Position.Y + CorrectorChannels.LegReach, out bool floorFound);
        float dist = floorFound ? floorY - body.Position.Y : float.PositiveInfinity;
        bool near     = dist <= CorrectorChannels.LegReach;
        bool anchored = dist <= BallisticPredictor.SupportReach;

        // ── Reference line from the plan ─────────────────────────────────
        float speed = fold.MaxSpeed * ctx.Modifiers.MaxWalkSpeed;
        int count = dir != 0
            ? s.Lattice.Solve(ctx.Chunks, body.Polygon, body.Position, body.Velocity,
                new Vector2(dir, 0f), hover: true, fold.HoverOffset, s.LatticePath, out _, out _)
            : 0;
        float cell = (float)Chunk.TileSize / Math.Clamp(cfg.LatticeCellsPerTile, 2, 8);
        float band = 0.5f * cell;
        Vector2 tHat, node; bool haveLine;
        if (count >= 2)
        {
            int j = 0;
            while (j < count - 1 && (s.LatticePath[j].Pos - body.Position).Length() < 0.9f * cell) j++;
            node = s.LatticePath[j].Pos;
            var d = j + 1 < count ? s.LatticePath[j + 1].Pos - node : node - body.Position;
            tHat = d.LengthSquared() > 1e-6f ? Vector2.Normalize(d) : new Vector2(dir, 0f);
            haveLine = true;
        }
        else
        {
            tHat = new Vector2(dir, 0f);
            node = new Vector2(body.Position.X, floorY - fold.HoverOffset);
            haveLine = anchored;                                 // the hover column
        }
        if (!haveLine && dir == 0)
        {
            vars.AmbientPrevDv = Vector2.Zero; vars.AmbientChannelPrev = default;
            return;                                              // free air, no plan: nothing to track
        }
        Vector2 nHat = dir != 0 ? new Vector2(-tHat.Y, tHat.X) : new Vector2(0f, 1f);

        // ── Nominal: the exact free rollout ──────────────────────────────
        Vector2 aFree = body.AppliedForce + ctx.Gravity;
        Vector2 v = body.Velocity, p0 = body.Position, pos = p0;
        for (int k = 0; k < H; k++)
        {
            v += aFree * dt; pos += v * dt;
            s.DeliverySamples[k].Pos = pos;                      // p_free at the end of tick k
            s.CoastVel[k] = v;                                   // redirect-disc geometry
            // Masks/caps read the CURRENT state, held for the horizon.
            s.Samples[k].Pos      = body.Position;
            s.Samples[k].Vel      = body.Velocity;
            s.Samples[k].FloorY   = floorFound ? floorY : float.PositiveInfinity;
            s.Samples[k].Grounded = anchored;
            s.CornerPlant[k] = false;
        }

        // ── Rows ─────────────────────────────────────────────────────────
        int rowCount = 0;
        for (int T = 0; T < H && rowCount < ClearanceConstraintBuilder.MaxEvents - 2; T++)
        {
            var pf = s.DeliverySamples[T].Pos;
            if (haveLine)
            {
                float e = Vector2.Dot(pf - node, nHat);
                s.Rows[rowCount++] = new ClearanceRow { Tick = T, Normal = nHat,  Depth = -band - e, HingeScale = 1f };
                s.Rows[rowCount++] = new ClearanceRow { Tick = T, Normal = -nHat, Depth = e - band,  HingeScale = 1f };
            }
            if (dir != 0)
            {
                float along = (pf.X - p0.X) * dir;
                s.Rows[rowCount++] = new ClearanceRow
                    { Tick = T, Normal = new Vector2(-dir, 0f), Depth = along - speed * (T + 1) * dt, HingeScale = 1f };
            }
        }
        if (dir != 0 && rowCount < ClearanceConstraintBuilder.MaxEvents)
        {
            // Progress: what the channels could add along t̂ by the last tick
            // at their caps — Σ_k (T−k+1)·dt² = dt²·(T+1)(T+2)/2.
            float capX = near ? cfg.FoldDriveForce : cfg.FoldAirLateralForce;
            float capY = tHat.Y < 0f
                ? (near ? cfg.FoldLegForce : 0f) + (near ? 0f : cfg.FoldAirVerticalForce)
                : (near ? cfg.FoldTuckForce : 0f) + (near ? 0f : cfg.FoldAirVerticalForce);
            float capAlong = MathF.Abs(tHat.X) * capX + MathF.Abs(tHat.Y) * capY;
            int T = H - 1;
            s.Rows[rowCount++] = new ClearanceRow
            {
                Tick = T, Normal = tHat, HingeScale = ProgressHingeScale,
                Depth = capAlong * dt * dt * (T + 1) * (T + 2) * 0.5f,
            };
        }

        // ── Channels (BuildFold, frozen masks), solve, apply tick 0 ──────
        var pr = s.Problem;
        pr.H = H; pr.Dt = dt;
        pr.CoastVel = s.CoastVel;
        pr.Rows = s.Rows; pr.RowCount = rowCount;
        pr.ChannelCount = CorrectorChannels.BuildFold(s, H, rowCount, dir, speed);
        for (int k = 0; k < H; k++) s.ChannelMask[2][k] = false;   // CornerAssist: not carried over
        for (int c = 0; c < pr.ChannelCount; c++)
            pr.PrevApplied[c] = vars.AmbientChannelPrev[c] * CorrectorChannels.AnchorLeak;
        pr.DeltaWeight = cfg.CorrectorDeltaWeight;
        pr.HingeWeight = HingeWeight;
        pr.InnerIterations = cfg.FoldIterations;
        pr.RowPush = s.RowPush;

        CorrectionSolver.Solve(pr, s.Z, s.ZScratch);
        CorrectorChannels.ComputeTickDv(s, pr, H, dt);
        for (int c = 0; c < pr.ChannelCount; c++) vars.AmbientChannelPrev[c] = s.Z[c * H];
        body.AppliedForce += s.TickDv[0] / dt;
        vars.AmbientPrevDv = s.TickDv[0];
        s.Ledger.Record(pr, s.Z, s.RowPush, s.Samples, dt);

        if (s.CaptureTrajectories)
        {
            // Render-only: the planned shape as the "solved" trajectory, the
            // free rollout as the "ballistic" one.
            int m = Math.Min(count, BallisticPredictor.MaxHorizon);
            for (int i = 0; i < m; i++) s.SolvedTrajectory[i] = s.LatticePath[i];
            s.SolvedCount = m;
            for (int k = 0; k < H; k++) s.BallisticTrajectory[k] = s.DeliverySamples[k];
            s.BallisticCount = H;
        }
    }
}
