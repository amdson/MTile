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
        int H = Math.Min(PathTracker.Horizon, BallisticPredictor.MaxHorizon);

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

        // ── Nominal: the exact free rollout; masks/caps: the current state, held ──
        PathTracker.FreeRollout(s, body, ctx.Gravity, H, dt);
        PathTracker.HoldSamples(s, body, H, floorFound ? floorY : float.PositiveInfinity, anchored);
        // Plantable corners in hand reach (the cave-mouth lip; rising, a lip
        // above the head) — the one place the engine acts in free air. See
        // the Redirect mask below.
        // Every tick's sample is the same held state, so one scan serves all.
        CorrectorChannels.MarkCornerPlants(ctx.Chunks, s.Samples, 1, s.CornerPlant, risingLips: true);
        for (int k = 1; k < H; k++) s.CornerPlant[k] = s.CornerPlant[0];
        // Contact-push surfaces (walls, a hole's lips) — same held state.
        CorrectorChannels.MarkContactPush(ctx.Chunks, s.Samples, 1, s.ContactPushLeft, s.ContactPushRight);
        for (int k = 1; k < H; k++) { s.ContactPushLeft[k] = s.ContactPushLeft[0]; s.ContactPushRight[k] = s.ContactPushRight[0]; }

        // ── Reference polyline: body → first node ≥ one cell away → nodes ──
        int nv = 0;
        bool hoverColumnRows = hoverColumn;
        if (havePath)
        {
            int j = 0;
            while (j < count - 1 && (s.LatticePath[j].Pos - body.Position).Length() < 0.9f * cell) j++;
            nv = PathTracker.BeginPolyline(s, body.Position);
            for (; j < count; j++) PathTracker.AppendVertex(s, ref nv, s.LatticePath[j].Pos);
            if (nv < 2) { havePath = false; nv = 0; }
        }
        // The plan's first direction: where the drive pushes (below). A path
        // that follows intent starts along dir; a neutral launch's escape
        // from under a slab starts sideways toward the open side; a plain
        // neutral jump starts straight up (no x at all).
        Vector2 t0 = havePath ? Vector2.Normalize(s.BeadVerts[1] - s.BeadVerts[0]) : u;
        float yHover = floorY - fold.HoverOffset;
        // Grounded profiles (those that hover) cap the SPEED along the path
        // (a walk speed is a SPEED: capping x let a body on a 45° bevel run
        // at |v| = 141 — a staircase came out as a chain of hops); air
        // profiles cap x only — see LatticePolicy.AddRows.
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

        // ── Sliding beads: outer passes of project → rows → solve ────────
        var settings = new PathTrackSettings
        {
            H = H, Dt = dt, Origin = body.Position, VertexCount = nv,
            Band = band,
            ArcSpeedCap = arcSpeed ? speed : float.PositiveInfinity,
            HoverLineY  = hoverColumnRows ? yHover : float.NaN,
            FallbackTangent = u,
            WallRows = wallRows,
            Iterations = cfg.FoldIterations,
        };
        var policy = new LatticePolicy
        {
            Cfg = cfg, Fold = fold, Dir = dir, Speed = speed, ArcSpeed = arcSpeed,
            Origin = body.Position, Near = near, Ballistic = ballistic, Planning = planning,
            T0 = t0, SupportVy = supportVy, AirSpeed = cfg.MaxAirSpeed * ctx.Modifiers.MaxAirSpeed,
        };
        PathTracker.Solve(s, settings, ref policy);
        var pr = s.Problem;

        // ── Apply tick 0 of the last solve ────────────────────────────────
        for (int c = 0; c < pr.ChannelCount; c++) vars.AmbientChannelPrev[c] = s.Z[c * H];
        vars.AmbientPrevDv = s.TickDv[0];
        PathTracker.ApplyTick0(s, body, dt);

        if (s.CaptureTrajectories)
        {
            int m = Math.Min(count, BallisticPredictor.MaxHorizon);
            for (int i = 0; i < m; i++) s.SolvedTrajectory[i] = s.LatticePath[i];
            s.SolvedCount = m;
            for (int k = 0; k < H; k++) s.BallisticTrajectory[k] = s.DeliverySamples[k];
            s.BallisticCount = H;
        }
    }

    // The lattice's per-state parts of the path-tracking solve: the air
    // profiles' x-only speed cap, the fold channel stack with its masks, and
    // the progress authority those channels have near the floor.
    private struct LatticePolicy : IPathTrackPolicy
    {
        public MovementConfig Cfg;
        public FoldProfile Fold;
        public int Dir;
        public float Speed, SupportVy, AirSpeed;
        public bool ArcSpeed, Near, Ballistic, Planning;
        public Vector2 Origin, T0;

        public int AddRows(CorrectorScratch s, int H, float dt, int pass, int rowCount)
        {
            // Air profiles keep the x-only cap: an air speed bounds lateral
            // steering, while the vertical is gravity and the legs' launch
            // ("as fast as possible" along a rising path).
            if (!ArcSpeed && Dir != 0 && float.IsFinite(Speed))
            {
                for (int T = 0; T < H; T++)
                {
                    float along = (s.DeliverySamples[T].Pos.X - Origin.X) * Dir;
                    s.Rows[rowCount++] = new ClearanceRow
                        { Tick = T, Normal = new Vector2(-Dir, 0f), Depth = along - Speed * (T + 1) * dt, HingeScale = 1f, Reference = true };
                }
            }
            return rowCount;
        }

        // (zero in free air: no actuators there — see the masks below)
        public bool ProgressAuthority(CorrectorScratch s, Vector2 tLast, out float capAlong)
        {
            float capX = Near ? Cfg.FoldDriveForce : 0f;
            float capY = !Near ? 0f : tLast.Y < 0f ? Cfg.FoldLegForce : Cfg.FoldTuckForce;
            capAlong = MathF.Abs(tLast.X) * capX + MathF.Abs(tLast.Y) * capY;
            return Planning;
        }

        public int BuildChannels(CorrectorScratch s, int H, int rowCount)
        {
            var cfg = Cfg; var fold = Fold;
            bool near = Near, ballistic = Ballistic, planning = Planning;
            var t0 = T0; float speed = Speed;
            var ch = s.Problem.Channels;
            int count = CorrectorChannels.BuildFold(s, H, rowCount, Dir, SupportVy);
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
                //
                // ...and at a plantable corner: the one anchor free air has.
                // MarkCornerPlants' own gates apply there — descending onto a
                // corner, or rising into a lip above the center — so the lift
                // on a flat-ground launch above (no corner in reach) cannot
                // come back through it. Limits (measured 2026-09-30): the
                // plant exists only within CornerPlantReach of the lip, ~3
                // frames before contact for a running jump's arc, and inside
                // this 5-tick solve the lip's face row is cheaper to meet by
                // braking x than by dropping under it — so a body arriving
                // at a corridor mouth still rising hard brakes and wall-slides
                // anyway. It helps the slow, late contacts.
                s.ChannelMask[3][k] = cfg.FoldRedirectEnabled
                    && ((near && !ballistic) || s.CornerPlant[k]);
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
            ch[1].Axis = new Vector2(MathF.Sign(t0.X), 0f);
            ch[1].Unilateral = true;
            // The disc never grows forward speed past the state's cap (the
            // maneuver stack's speed-cap principle, movement_todo #5): a
            // deflection keeps |v|, so a 200 px/s drop rotated at a lip came
            // out as 200 px/s of walk (the corridor peaked at 168 grounded,
            // 193 airborne; qp/ref never exceed the 150 air cap). The cap is
            // the profile's speed where it has one (walk, crouch), else the
            // air cap; forward is the plan's first direction.
            ch[3].ForwardAxis = new Vector2(MathF.Sign(t0.X), 0f);
            ch[3].ForwardCap  = t0.X == 0f ? 0f
                : float.IsFinite(speed) ? speed : AirSpeed;
            // ...parametrized as a force (see CorrectionSolver.Project: the
            // same disc, in Δv/dt) so its lever is commensurate with the other
            // channels' — as a Δv lever, active on every near tick, it
            // inflated every variable's step bound and starved the legs and
            // drive — and priced like the legs (BuildFold's ε weight makes a
            // deflection free, which makes it the solver's first choice for
            // every band violation: rotating the walk's velocity, shedding it).
            ch[3].Lever  = LeverKind.Force;
            ch[3].Weight = ch[0].Weight;
            // (Band and speed rows are Reference rows, so BuildFold's corner /
            // redirect feature activation does not see them — the disc had
            // been "planting" against the band in free air, holding a 4-tile
            // fall to 27 px/s.)
            return count;
        }
    }
}
