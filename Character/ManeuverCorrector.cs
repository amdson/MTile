using System;
using Microsoft.Xna.Framework;

namespace MTile;

// The committed-maneuver corrector loop — the second of the two ways a movement
// state uses the corrector solve (the first is the per-frame ambient layer,
// AmbientCorrector). Shared infrastructure, not a class of state: any state
// with an authored arc can call Run to deform that arc around terrain — the
// climb family (ClimbStates.cs) is the current client, both for its per-tick
// correction and for trigger-by-feasibility on a probe body.
//
// Run executes the two outer sequential-convexification passes over `body`'s
// state (the real body during Update; a pooled probe during trigger
// feasibility): predict the guided coast → build clearance rows → solve on the
// MANEUVER channel stack (CorrectorChannels.BuildManeuver — legs/redirect near
// ground, air control in flight); re-rollout WITH the corrections → rebuild →
// re-solve. Leaves s.Z holding the plan (s.TickDv the per-tick δv totals) and
// returns the linear-model residual of the last pass (0 when the coast is
// provably silent — no rows on pass 1). The caller applies the summed tick-0
// correction through Body.AppliedForce and records the ledger.
public static class ManeuverCorrector
{
    private const float HingeWeight = 1e6f;   // stiffness constant, not a feel knob

    public static float Run(EnvironmentContext ctx, PhysicsBody body, int dir,
                            float entrySpeed, in ChannelAnchors prevAnchors, out int rowCount,
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
                body, ctx.Chunks, dir, entrySpeed, startGrounded: false,
                ctx.Gravity, ctx.Dt, H, s.Samples, pass == 0 ? null : s.TickDv);
            CorrectorChannels.MarkCornerPlants(ctx.Chunks, s.Samples, n, s.CornerPlant);
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
            // The maneuver channel stack (CorrectorChannels.BuildManeuver):
            // legs/redirect near the ground, air control in flight. The entry
            // hop still injects the maneuver's launch energy; the stack
            // corrects the committed arc around it, with leaky per-channel Δ
            // anchors carrying continuity across frames.
            p.ChannelCount = CorrectorChannels.BuildManeuver(s, n, rowCount, dir, entrySpeed);
            for (int c = 0; c < p.ChannelCount; c++)
                p.PrevApplied[c] = prevAnchors[c] * CorrectorChannels.AnchorLeak;
            p.DeltaWeight = cfg.CorrectorDeltaWeight;
            p.HingeWeight = HingeWeight;
            p.InnerIterations = iterations;
            // Contact-push attribution on the final pass only (the applied plan).
            // Always on — the caller's ledger record reads it (block-breaking
            // bookkeeping), not just the capture overlay.
            p.RowPush = pass == 1 ? s.RowPush : null;

            residual = CorrectionSolver.Solve(p, s.Z, s.ZScratch);
            CorrectorChannels.ComputeTickDv(s, p, n, ctx.Dt);

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
                body, ctx.Chunks, dir, entrySpeed, startGrounded: false,
                ctx.Gravity, ctx.Dt, H, s.SolvedTrajectory, s.TickDv);
        }
        return residual;
    }
}
