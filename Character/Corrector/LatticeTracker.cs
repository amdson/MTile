using System;
using Microsoft.Xna.Framework;

namespace MTile;

// The lattice fold engine's tracker (MovementConfig.FoldEngine "lattice" —
// Plans/LATTICE_PATH_PLANNER.md §3.7 at horizon ONE). The lattice path DP
// gives the shape of the trajectory; this picks the channel forces for the
// current tick only:
//
//   v_des  = (first path point ≥ one cell from the body − body)^ · speed
//   need   = (v_des − v_free) / dt,   v_free = v + (F_baseline + g)·dt
//   F      = need projected onto the channels available NOW, each with its
//            axis, direction (unilateral) and cap — the same channel physics
//            as CorrectorChannels.BuildFold, evaluated at the body's state.
//
// No coast, no rows, no horizon, no linearization, no deform, no servo:
// feedback is re-planning (the DP's seed is the body, so tracking error is
// the next path's first segment pointing back at the line), and every force
// is a capped channel — a fall is never braked because no channel can beat
// gravity in the air. The channel axes are ±x / ±y, so the projection is
// separable and closed-form: per axis, the summed authority in the needed
// direction.
//
// dir == 0 (no progress axis, plan §4.6) and a solve with no path (seed
// pinned inside an obstacle's margin) use the hover column: carry along
// intent, vertical toward the hover line under the body.
//
// Not carried over from the qp stack in this first cut, so their absence is
// a measured thing, not an oversight: the Redirect disc (a velocity-update
// lever, not a force), CornerAssist (its mask was the coast's hard rows),
// the per-channel ledger attribution.
public static class LatticeTracker
{
    public static void Apply(EnvironmentContext ctx, CorrectorScratch s,
                             in FoldProfile fold, int dir, ref MovementVars vars)
    {
        var cfg = MovementConfig.Current;
        var body = ctx.Body;
        float dt = ctx.Dt;
        vars.AmbientPrevDv = Vector2.Zero;
        vars.AmbientChannelPrev = default;
        if (s == null || dt <= 0f) return;
        if (ctx.Modifiers.PreserveExternalVelocity) return;      // knockback: never fought

        // The body's situation, for the masks: the C-space floor under it.
        var template = CObstacleTemplate.For(body.Polygon);
        float floorY = AmbientCorrector.FloorEnvelope(ctx.Chunks, template, body.Position.X,
            body.Position.Y - 2f, body.Position.Y + CorrectorChannels.LegReach, out bool floorFound);
        float dist = floorFound ? floorY - body.Position.Y : float.PositiveInfinity;
        bool near     = dist <= CorrectorChannels.LegReach;         // legs / drive / tuck
        bool anchored = dist <= BallisticPredictor.SupportReach;    // hover line exists

        // ── Desired velocity ─────────────────────────────────────────────
        float speed = fold.MaxSpeed * ctx.Modifiers.MaxWalkSpeed;
        int count = dir != 0
            ? s.Lattice.Solve(ctx.Chunks, body.Polygon, body.Position, body.Velocity,
                new Vector2(dir, 0f), hover: true, fold.HoverOffset, s.LatticePath, out _, out _)
            : 0;
        Vector2 vDes;
        bool vertDemand = true;   // false = no vertical target at all (free air, no path): no vertical force
        if (count >= 2)
        {
            // The first step of the planned trajectory: the first node at
            // least one cell away (the seed cell contains the body).
            float cell = s.Lattice.DebugCell;
            int j = 0;
            while (j < count - 1 && (s.LatticePath[j].Pos - body.Position).Length() < 0.9f * cell) j++;
            var d = s.LatticePath[j].Pos - body.Position;
            vDes = d.LengthSquared() > 1e-6f ? Vector2.Normalize(d) * speed : new Vector2(dir * speed, 0f);
        }
        else
        {
            vertDemand = anchored;
            float vy = anchored ? (floorY - fold.HoverOffset - body.Position.Y) / dt : 0f;
            vDes = new Vector2(dir * speed, vy);
        }

        // ── One-tick channel projection ──────────────────────────────────
        Vector2 vFree = body.Velocity + (body.AppliedForce + ctx.Gravity) * dt;
        Vector2 need  = (vDes - vFree) / dt;                          // px/s² this tick

        // Caps: BuildFold's, at the body's state. Legs fade with rise speed
        // (VPushMax) and lose the landing catch past the engagement gate.
        float rise = MathF.Max(0f, -body.Velocity.Y);
        float catchScale = Math.Clamp(
            (cfg.MaxGroundEngageVnRel + CorrectorChannels.CatchFadeBand - body.Velocity.Y)
                / CorrectorChannels.CatchFadeBand, 0f, 1f);
        float legCap     = near ? cfg.FoldLegForce * Math.Clamp(1f - rise / cfg.FoldLegPushFadeSpeed, 0f, 1f) * catchScale : 0f;
        float tuckCap    = near ? cfg.FoldTuckForce : 0f;
        float driveCap   = near && dir != 0 ? cfg.FoldDriveForce : 0f;
        float airLatCap  = !near && dir != 0 ? cfg.FoldAirLateralForce : 0f;
        float airVertCap = !near ? cfg.FoldAirVerticalForce : 0f;

        Vector2 F = Vector2.Zero;
        // x: drive / air-lateral push only ALONG intent (anti-autopilot: no
        // channel brakes held momentum; friction is the baseline's).
        if (dir != 0 && need.X * dir > 0f)
            F.X = dir * MathF.Min(MathF.Abs(need.X), driveCap + airLatCap);
        // y: up = legs (+ air-vertical), down = tuck (+ air-vertical).
        if (!vertDemand)   F.Y = 0f;
        else if (need.Y < 0f) F.Y = -MathF.Min(-need.Y, legCap + airVertCap);
        else                  F.Y =  MathF.Min( need.Y, tuckCap + airVertCap);
        body.AppliedForce += F;

        if (s.CaptureTrajectories)
        {
            // Render-only: the planned shape as the "solved" trajectory.
            int m = Math.Min(count, BallisticPredictor.MaxHorizon);
            for (int i = 0; i < m; i++) s.SolvedTrajectory[i] = s.LatticePath[i];
            s.SolvedCount = m;
        }
    }
}
