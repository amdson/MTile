using System;
using Microsoft.Xna.Framework;

namespace MTile;

// The lattice fold engine's tracker (MovementConfig.FoldEngine "lattice" —
// Plans/LATTICE_PATH_PLANNER.md §3.7): a SHORT-HORIZON channel QP over the
// planned trajectory's first stretch, on CorrectionSolver.
//
//   nominal    the exact free rollout — v += (F_baseline + g)·dt, p += v·dt —
//              the dynamics with zero correction; channel forces enter
//              linearly, so nothing is linearized or predicted;
//   reference  a sliding BEAD per tick: the nearest point on the planned
//              polyline to the iterate's tick-T position, monotone along the
//              path — where along the path the body is at tick T is the
//              solve's own output (path following, not trajectory tracking);
//              alternated with the solve for BeadPasses outer passes;
//   channels   BuildFold's stack (legs / drive / tuck / redirect / air-lateral
//              / air-vertical) with caps and masks evaluated at the body's
//              current state and held for the horizon;
//   band       hard rows per tick: within half a lattice cell of p̂_T
//              PERPENDICULAR to t̂_T — stay on the path, progress along it is
//              free (the path's resolution, not a knob);
//   progress   one soft row at the last tick along t̂ whose depth is what the
//              channels could add at their caps — saturates the actuators and
//              rests: "as fast as the channels allow", no target speed;
//   limit      hard rows: displacement along intent ≤ state speed limit × t.
//
// Horizon = 5 ticks: stopping times here are 1–4 ticks (legs stop a 100 px/s
// descent in one; tuck + gravity stop a rise in ~3), so the QP sees the
// deceleration it will need — the switching behaviour a minimum-time
// controller has, found rather than derived, for every axis and asymmetry.
// Feedback is re-planning: the DP's seed is the body every tick.
//
// dir == 0 (plan §4.6) / no path: the hover column — band rows on y around
// the hover point under the body if the state hovers and is anchored; no
// path and nothing to hover on: nothing to track. Jump states plan with
// hover off and u tilted up (FoldProfile.Jump). CornerAssist is masked off (its meaning was the coast's
// hard rows).
public static class LatticeTracker
{
    private const int   Horizon = 5;
    private const int   BeadPasses = 3;   // project → rows → solve, alternated
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
        int H = Math.Min(Horizon, BallisticPredictor.MaxHorizon);

        // ── The body's situation (masks and caps are frozen from it) ─────
        var template = CObstacleTemplate.For(body.Polygon);
        float floorY = AmbientCorrector.FloorEnvelope(ctx.Chunks, template, body.Position.X,
            body.Position.Y - 2f, body.Position.Y + CorrectorChannels.LegReach, out bool floorFound);
        float dist = floorFound ? floorY - body.Position.Y : float.PositiveInfinity;
        bool near     = dist <= CorrectorChannels.LegReach;
        bool anchored = dist <= BallisticPredictor.SupportReach;

        // ── The plan ─────────────────────────────────────────────────────
        float speed = fold.MaxSpeed * ctx.Modifiers.MaxWalkSpeed;
        // u is intent: along dir, and up as well while a jump is held.
        bool planning = dir != 0 || fold.Rising;
        Vector2 u = fold.Rising ? Vector2.Normalize(new Vector2(dir, -1f)) : new Vector2(dir, 0f);
        int count = planning
            ? s.Lattice.Solve(ctx.Chunks, body.Polygon, body.Position, body.Velocity,
                u, fold.Hover, fold.HoverOffset, fold.RiseCost,
                s.LatticePath, out _, out _)
            : 0;
        float cell = (float)Chunk.TileSize / Math.Clamp(cfg.LatticeCellsPerTile, 2, 8);
        float band = 0.5f * cell;
        bool havePath = count >= 2;
        bool hoverColumn = !havePath && fold.Hover && anchored;
        if (!havePath && !hoverColumn && !planning)
        {
            vars.AmbientPrevDv = Vector2.Zero; vars.AmbientChannelPrev = default;
            return;                                              // free air, no plan: nothing to track
        }

        // ── Nominal: the exact free rollout ──────────────────────────────
        Vector2 aFree = body.AppliedForce + ctx.Gravity;
        Vector2 v = body.Velocity, p0 = body.Position, pos = p0;
        for (int k = 0; k < H; k++)
        {
            v += aFree * dt; pos += v * dt;
            s.DeliverySamples[k].Pos = pos;                      // p̄_T: free position after tick T
            s.CoastVel[k] = v;                                   // redirect-disc geometry
            s.Samples[k].Pos      = body.Position;               // masks/caps: the current state, held
            s.Samples[k].Vel      = body.Velocity;
            s.Samples[k].FloorY   = floorFound ? floorY : float.PositiveInfinity;
            s.Samples[k].Grounded = anchored;
            s.CornerPlant[k] = false;
        }

        // ── Reference polyline: body → first node ≥ one cell away → nodes ──
        int nv = 0;
        if (havePath)
        {
            int j = 0;
            while (j < count - 1 && (s.LatticePath[j].Pos - body.Position).Length() < 0.9f * cell) j++;
            s.BeadVerts[nv] = body.Position; s.BeadArc[nv] = 0f; nv++;
            for (; j < count; j++)
            {
                var q = s.LatticePath[j].Pos;
                if ((q - s.BeadVerts[nv - 1]).LengthSquared() < 1e-6f) continue;
                s.BeadArc[nv] = s.BeadArc[nv - 1] + (q - s.BeadVerts[nv - 1]).Length();
                s.BeadVerts[nv] = q; nv++;
            }
            if (nv < 2) havePath = false;
        }
        float yHover = floorY - fold.HoverOffset;

        // ── Sliding beads: outer passes of project → rows → solve ────────
        // Bead T = the nearest point on the polyline to the iterate's tick-T
        // position (pass 0: the free rollout; later: free + last solve's Δp),
        // made monotone along the path. In the bead's local frame the
        // along-path residual is zero by construction, so the rows are the
        // perpendicular band and the progress row along the last bead's
        // tangent — timing along the path is the solve's own output.
        var pr = s.Problem;
        int rowCount = 0;
        for (int pass = 0; pass < BeadPasses; pass++)
        {
            rowCount = 0;
            Vector2 tLast = u;
            float sPrev = 0f;
            for (int T = 0; T < H; T++)
            {
                Vector2 pT = s.DeliverySamples[T].Pos + (pass == 0 ? Vector2.Zero : s.TrackDelta[T]);
                if (havePath)
                {
                    float sT = MathF.Max(sPrev, ProjectArc(s, nv, pT));
                    sPrev = sT;
                    PointAt(s, nv, sT, out var q, out var t);
                    tLast = t;
                    var n = new Vector2(-t.Y, t.X);
                    float e = Vector2.Dot(pT - q, n) - Vector2.Dot(pass == 0 ? Vector2.Zero : s.TrackDelta[T], n);
                    // e is the FREE rollout's offset from the bead along n
                    // (rows measure Δp from the free rollout).
                    s.Rows[rowCount++] = new ClearanceRow { Tick = T, Normal = n,  Depth = -band - e, HingeScale = 1f, Reference = true };
                    s.Rows[rowCount++] = new ClearanceRow { Tick = T, Normal = -n, Depth = e - band,  HingeScale = 1f, Reference = true };
                }
                else if (hoverColumn)
                {
                    float e = s.DeliverySamples[T].Pos.Y - yHover;
                    s.Rows[rowCount++] = new ClearanceRow { Tick = T, Normal = new Vector2(0f, 1f),  Depth = -band - e, HingeScale = 1f, Reference = true };
                    s.Rows[rowCount++] = new ClearanceRow { Tick = T, Normal = new Vector2(0f, -1f), Depth = e - band,  HingeScale = 1f, Reference = true };
                }
            }
            if (dir != 0 && float.IsFinite(speed))              // no limit = "as fast as possible"
            {
                for (int T = 0; T < H; T++)
                {
                    float along = (s.DeliverySamples[T].Pos.X - p0.X) * dir;
                    s.Rows[rowCount++] = new ClearanceRow
                        { Tick = T, Normal = new Vector2(-dir, 0f), Depth = along - speed * (T + 1) * dt, HingeScale = 1f, Reference = true };
                }
            }
            if (planning)
            {
                // Progress: what the channels could add along t̂ by the last
                // tick at their caps — Σ_k (T−k+1)·dt² = dt²·(T+1)(T+2)/2.
                float capX = near ? cfg.FoldDriveForce : cfg.FoldAirLateralForce;
                float capY = tLast.Y < 0f
                    ? (near ? cfg.FoldLegForce : cfg.FoldAirVerticalForce)
                    : (near ? cfg.FoldTuckForce : cfg.FoldAirVerticalForce);
                float capAlong = MathF.Abs(tLast.X) * capX + MathF.Abs(tLast.Y) * capY;
                int T9 = H - 1;
                s.Rows[rowCount++] = new ClearanceRow
                {
                    Tick = T9, Normal = tLast, HingeScale = ProgressHingeScale,
                    Depth = capAlong * dt * dt * (T9 + 1) * (T9 + 2) * 0.5f,
                };
            }

            // ── Channels (BuildFold, frozen masks), solve ────────────────
            pr.H = H; pr.Dt = dt;
            pr.CoastVel = s.CoastVel;
            pr.Rows = s.Rows; pr.RowCount = rowCount;
            pr.ChannelCount = CorrectorChannels.BuildFold(s, H, rowCount, dir, speed, airUp: false);
            for (int k = 0; k < H; k++) s.ChannelMask[2][k] = false;   // CornerAssist: not carried over
            // (Band and speed rows are Reference rows, so BuildFold's corner /
            // redirect feature activation does not see them — the disc had
            // been "planting" against the band in free air, holding a 4-tile
            // fall to 27 px/s.)
            // No Δ-smoothing (the qp engine's anti-bang-bang regularizer, not
            // part of the §3.7 objective): caps and the band bound the plan,
            // and "as fast as possible" IS bang-bang — a launch is the legs
            // at their cap on tick 0. Measured with it on: a jump's tick-0
            // push was −2 px/s against gravity (the smoothing anchored the
            // launch to Standing's near-zero output) and the body fell.
            for (int c = 0; c < pr.ChannelCount; c++) pr.PrevApplied[c] = Vector2.Zero;
            pr.DeltaWeight = 0f;
            pr.HingeWeight = HingeWeight;
            pr.InnerIterations = cfg.FoldIterations;
            pr.RowPush = s.RowPush;

            CorrectionSolver.Solve(pr, s.Z, s.ZScratch);
            CorrectorChannels.ComputeTickDv(s, pr, H, dt);
            if (!havePath) break;                                // the hover column has no beads to slide
            // Corrected displacement per tick for the next pass's projection:
            // δv at tick k moves tick T by (T − k + 1)·dt.
            for (int T = 0; T < H; T++)
            {
                var d = Vector2.Zero;
                for (int k = 0; k <= T; k++) d += s.TickDv[k] * ((T - k + 1) * dt);
                s.TrackDelta[T] = d;
            }
        }

        // ── Apply tick 0 of the last solve ────────────────────────────────
        for (int c = 0; c < pr.ChannelCount; c++) vars.AmbientChannelPrev[c] = s.Z[c * H];
        body.AppliedForce += s.TickDv[0] / dt;
        vars.AmbientPrevDv = s.TickDv[0];
        s.Ledger.Record(pr, s.Z, s.RowPush, s.Samples, dt);

        if (s.CaptureTrajectories)
        {
            int m = Math.Min(count, BallisticPredictor.MaxHorizon);
            for (int i = 0; i < m; i++) s.SolvedTrajectory[i] = s.LatticePath[i];
            s.SolvedCount = m;
            for (int k = 0; k < H; k++) s.BallisticTrajectory[k] = s.DeliverySamples[k];
            s.BallisticCount = H;
        }
    }

    // Arc length of the nearest point on the polyline (vertices 0..nv−1,
    // last segment extended as a ray — the honest carry past the end).
    private static float ProjectArc(CorrectorScratch s, int nv, Vector2 p)
    {
        float best = float.PositiveInfinity, bestArc = 0f;
        for (int i = 0; i + 1 < nv; i++)
        {
            var a = s.BeadVerts[i]; var d = s.BeadVerts[i + 1] - a;
            float len2 = d.LengthSquared();
            if (len2 < 1e-9f) continue;
            float t = Vector2.Dot(p - a, d) / len2;
            bool last = i + 2 == nv;
            t = last ? MathF.Max(0f, t) : Math.Clamp(t, 0f, 1f);
            var q = a + d * t;
            float dist2 = (p - q).LengthSquared();
            if (dist2 < best) { best = dist2; bestArc = s.BeadArc[i] + t * MathF.Sqrt(len2); }
        }
        return bestArc;
    }

    // Point and unit tangent at arc length `arc` (past the end: along the
    // last segment's ray).
    private static void PointAt(CorrectorScratch s, int nv, float arc, out Vector2 q, out Vector2 t)
    {
        int i = 0;
        while (i + 2 < nv && arc > s.BeadArc[i + 1]) i++;
        var a = s.BeadVerts[i]; var d = s.BeadVerts[i + 1] - a;
        float len = d.Length();
        t = len > 1e-6f ? d / len : new Vector2(1f, 0f);
        q = a + t * (arc - s.BeadArc[i]);
    }
}
