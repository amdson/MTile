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
    private const int   BeadPasses = 3;
    private const int   ExactSweepCount = 3;   // exact coordinate sweeps after the gradient solve   // project → rows → solve, alternated
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
        // Rising faster than support could ever push: the body is leaving the
        // floor, so there is nothing to plant against (StandingState's entry
        // rule, and the climb family's launch gate). Support-relative
        // (BACKLOG 5.8): a floor rising under the body is a moving frame the
        // legs can still plant in, not a launch.
        float supportVy = ctx.TryGetGround(out var supportFsd) ? supportFsd.SurfaceVelocity.Y : 0f;
        bool ballistic = -(body.Velocity.Y - supportVy) > cfg.SpringMaxRiseSpeed;
        bool anchored = dist <= BallisticPredictor.SupportReach;

        // ── The plan ─────────────────────────────────────────────────────
        // The x speed cap: the profile's, under the walk or air modifier.
        float speed = fold.MaxSpeed * (fold.Hover ? ctx.Modifiers.MaxWalkSpeed : ctx.Modifiers.MaxAirSpeed);
        // u is intent: along dir, and up as well while a jump is held. A
        // launch's tilt is the direction the actuators produce — the legs'
        // ceiling (the push fade speed) up, the walk speed along — so the
        // band does not cap the rise at the x speed: with a fixed 1:1 tilt
        // a running hop reached 19 px against the neutral jump's 60.
        bool planning = dir != 0 || fold.Rising;
        Vector2 u = fold.Rising
            ? Vector2.Normalize(new Vector2(dir * cfg.MaxWalkSpeed * ctx.Modifiers.MaxWalkSpeed, -cfg.FoldLegPushFadeSpeed))
            : new Vector2(dir, 0f);
        bool bonk = false;
        int count = planning
            ? s.Lattice.Solve(ctx.Chunks, body.Polygon, body.Position, body.Velocity,
                u, fold.Hover, fold.HoverOffset, fold.RiseCost,
                s.LatticePath, out _, out bonk)
            : 0;
        // Freshness markers for render-side path consumers (LatticePathSampler) —
        // write-only diagnostics, never read by the sim.
        s.LatticePathCount = count;
        s.LatticePathFrame = ctx.CurrentFrame;
        // The plan's verdict, for tests and overlays (objection 4 of
        // Plans/LATTICE_PLANNING_OBJECTIONS.md). The tracker's behaviour per
        // regime is deliberate and identical to before the tag existed:
        //   Route    the far band was worth reaching: follow the polyline,
        //            its last segment extended as a ray (the carry past the
        //            window's edge);
        //   Refused  the DP found routes but none worth their cost (a wall
        //            too tall, a step a crouch won't mount), or nothing
        //            beyond the seed: the path up to the refusal is followed
        //            and its last segment extended INTO the refused obstacle
        //            — the tile rows brake, physics impacts. The honest bonk,
        //            never a planned stop;
        //   NoRoute  no polyline at all (seed snap failed, degenerate
        //            window): drive along intent under the progress row with
        //            no band — walk into it.
        // Only a Route entry is evidence the PLANNER did the work; the
        // corridor entry tests assert on it, so a fallback entry can no
        // longer pass as a planned one.
        s.LatticeOutcome = !planning ? LatticeOutcome.None
            : count == 0 ? LatticeOutcome.NoRoute
            : count >= 2 && !bonk ? LatticeOutcome.Route
            : LatticeOutcome.Refused;
        float cell = (float)Chunk.TileSize / Math.Clamp(cfg.LatticeCellsPerTile, 2, 8);
        // Half a cell: how far the body may stray from the polyline — the
        // path's resolution, a FOLLOWING tolerance. Not a clearance: the
        // tiles are the wall rows below, at LatticePathPlanner.Clearance.
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
        // The plan's first direction: where the drive pushes (below). A path
        // that follows intent starts along dir; a neutral launch's escape
        // from under a slab starts sideways toward the open side; a plain
        // neutral jump starts straight up (no x at all).
        Vector2 t0 = havePath ? Vector2.Normalize(s.BeadVerts[1] - s.BeadVerts[0]) : u;
        float yHover = floorY - fold.HoverOffset;

        // ── Sliding beads: outer passes of project → rows → solve ────────
        // Bead T = the nearest point on the polyline to the iterate's tick-T
        // position (pass 0: the free rollout; later: free + last solve's Δp),
        // made monotone along the path. In the bead's local frame the
        // along-path residual is zero by construction, so the rows are the
        // perpendicular band and the progress row along the last bead's
        // tangent — timing along the path is the solve's own output.
        var pr = s.Problem;
        // Grounded profiles (those that hover) cap the SPEED along the path;
        // air profiles cap x only — see the rows below.
        bool arcSpeed = fold.Hover && havePath && float.IsFinite(speed);

        // ── The walls: clearance rows from the free rollout ──────────────
        // The path is guidance; the tiles are the constraint. Without these
        // the QP knew only the band — symmetric, so lagging a bend toward
        // the wall cost no more than lagging it toward free space — and the
        // body hit every corner of the bumpy corridor (10 stalls; the ref
        // engine, whose QP has clearance rows, hits none). The rows are the
        // swept body's penetrations along the free rollout, hard, with tile
        // normals: the tuck/legs take their gradient from the actual face,
        // and the redirect can plant against it. All faces (the DP already
        // decided bonk-vs-route, so no anti-autopilot filter). Built once;
        // every bead pass starts from them. Margin = the engine's one
        // clearance number (LatticePathPlanner.Clearance), not
        // CorrectorMargin: that 2 px is the 10-tick ballistic engines'
        // prediction insurance, and on both faces of a 22 px corridor it
        // asked for 23.2 — the floor and ceiling rows could never both hold.
        int wallRows = ClearanceConstraintBuilder.Build(ctx.Chunks, body.Polygon, s.DeliverySamples, H,
            LatticePathPlanner.Clearance, ClearanceConstraintBuilder.DefaultDeepViolation, s.Rows, out _,
            verticalFacesOnly: false);
        wallRows = Math.Min(wallRows, ClearanceConstraintBuilder.MaxEvents - (3 * H + 1));

        int rowCount = 0;
        for (int pass = 0; pass < BeadPasses; pass++)
        {
            rowCount = wallRows;
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
                    if (arcSpeed)
                    {
                        // A walk speed is a SPEED: cap progress along the
                        // path, not along x. Capping x let a body on a 45°
                        // bevel run at |v| = 141 (a staircase came out at
                        // 150 px/s of x and 160 of rise — a chain of hops,
                        // where qp/ref walk at 100). The body's arc position
                        // is the bead's, so the cap is the bead's own
                        // coordinate: s_T ≤ speed·(T+1)·dt.
                        float sFree = sT - Vector2.Dot(pass == 0 ? Vector2.Zero : s.TrackDelta[T], t);
                        s.Rows[rowCount++] = new ClearanceRow
                            { Tick = T, Normal = -t, Depth = sFree - speed * (T + 1) * dt, HingeScale = 1f, Reference = true };
                    }
                }
                else if (hoverColumn)
                {
                    float e = s.DeliverySamples[T].Pos.Y - yHover;
                    s.Rows[rowCount++] = new ClearanceRow { Tick = T, Normal = new Vector2(0f, 1f),  Depth = -band - e, HingeScale = 1f, Reference = true };
                    s.Rows[rowCount++] = new ClearanceRow { Tick = T, Normal = new Vector2(0f, -1f), Depth = e - band,  HingeScale = 1f, Reference = true };
                }
            }
            // Air profiles keep the x-only cap: an air speed bounds lateral
            // steering, while the vertical is gravity and the legs' launch
            // ("as fast as possible" along a rising path).
            if (!arcSpeed && dir != 0 && float.IsFinite(speed))
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
                // (zero in free air: no actuators there — see the masks below)
                float capX = near ? cfg.FoldDriveForce : 0f;
                float capY = !near ? 0f : tLast.Y < 0f ? cfg.FoldLegForce : cfg.FoldTuckForce;
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
            pr.ChannelCount = CorrectorChannels.BuildFold(s, H, rowCount, dir, speed, supportVy);
            for (int k = 0; k < H; k++)
            {
                s.ChannelMask[2][k] = false;   // CornerAssist: not carried over
                // No actuators in free air. AirLateral/AirVertical are the qp
                // fold's flight steering — a second air control without the
                // state's speed cap, and a vertical nudge that bent a launch
                // toward a plan that knows nothing of the body's momentum (a
                // held-right double jump lost a third of its rise). Off the
                // ground the body follows physics and the state's own air
                // control; the engine acts where it can push: near the floor
                // (legs, drive, tuck, redirect) and at a plantable corner.
                s.ChannelMask[5][k] = false;
                s.ChannelMask[6][k] = false;
                // Redirect: wherever the legs are — a plant needs ground to
                // push against, nothing else. BuildFold's !Grounded gate
                // (no deflection on supported ticks, so a coast-tracking
                // solve could not trade a walk's vx for rise) is the qp
                // objective's worry: this solve maximizes progress along the
                // path and has no reason to. The push is bounded
                // (FoldRedirectForce), so a face slows the body, not halts it.
                // ...but NOT off a surface the body is already leaving. A
                // plant is a contact: MarkCornerPlants has always said a
                // hand-plant is a DESCENDING maneuver and that rising ticks
                // are climb work, the leg servo's domain — the tracker had
                // dropped that gate along with !Grounded. Without it the disc
                // spent up to 446 px/s^2 of LIFT on the first frames of every
                // jump, on flat ground, with no corner within plant reach: a
                // corner force firing where there is no corner, which reads as
                // the body floating off the launch. The threshold is the one
                // StandingState already uses to decide a body is ballistic
                // rather than supported (a rise no support could author).
                s.ChannelMask[3][k] = cfg.FoldRedirectEnabled && near && !ballistic;
                // No legs for a profile that neither hovers nor rises (Fall):
                // its plan is a level line at the body's height, and legs in
                // reach would hold it there — a re-planned level line, a hard
                // band and the legs make a fixed point (a wall slide hung 27
                // px above the floor once the solve converged). Falling has no
                // upward force; the catch is Standing's, where the legs are.
                if (!fold.Hover && !fold.Rising) s.ChannelMask[0][k] = false;
                // Drive: along the plan's first segment, wherever the legs
                // are. BuildFold masks it off at dir == 0 (qp's "no x channel
                // at station"); on the engine the plan is the authority — a
                // neutral covered jump's escape begins sideways and needs an
                // x push (from rest the disc has no radius). Where the plan
                // follows intent this is the same drive as before; a vertical
                // first segment turns it off (a neutral jump does not drift).
                s.ChannelMask[1][k] = near && planning && t0.X != 0f;
            }
            pr.Channels[1].Axis = new Vector2(MathF.Sign(t0.X), 0f);
            pr.Channels[1].Unilateral = true;
            // The disc never grows forward speed past the state's cap (the
            // maneuver stack's speed-cap principle, movement_todo #5): a
            // deflection keeps |v|, so a 200 px/s drop rotated at a lip came
            // out as 200 px/s of walk (the corridor peaked at 168 grounded,
            // 193 airborne; qp/ref never exceed the 150 air cap). The cap is
            // the profile's speed where it has one (walk, crouch), else the
            // air cap; forward is the plan's first direction.
            pr.Channels[3].ForwardAxis = new Vector2(MathF.Sign(t0.X), 0f);
            pr.Channels[3].ForwardCap  = t0.X == 0f ? 0f
                : float.IsFinite(speed) ? speed : cfg.MaxAirSpeed * ctx.Modifiers.MaxAirSpeed;
            // ...parametrized as a force (see CorrectionSolver.Project: the
            // same disc, in Δv/dt) so its lever is commensurate with the other
            // channels' — as a Δv lever, active on every near tick, it
            // inflated every variable's step bound and starved the legs and
            // drive — and priced like the legs (BuildFold's ε weight makes a
            // deflection free, which makes it the solver's first choice for
            // every band violation: rotating the walk's velocity, shedding it).
            pr.Channels[3].Lever  = LeverKind.Force;
            pr.Channels[3].Weight = pr.Channels[0].Weight;
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
            // The applied tick, exactly (the sweeps under-deliver: a channel a
            // row asks for at its cap reaches a few percent of it — from rest
            // the walk took 27 ticks to 90 px/s; converged, 3).
            CorrectionSolver.ExactSweeps(pr, s.Z, ExactSweepCount, H);
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
