using System;
using Microsoft.Xna.Framework;

namespace MTile;

// The fold engine's tracker (Plans/LATTICE_PATH_PLANNER.md §3.7): a SHORT-HORIZON channel QP over the
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
                             in FoldProfile fold, MovementResource uses, int dir, ref MovementVars vars)
    {
        var cfg = MovementConfig.Current;
        var body = ctx.Body;
        float dt = ctx.Dt;
        if (s == null || dt <= 0f) return;
        int H = Math.Min(PathTracker.Horizon, BallisticPredictor.MaxHorizon);

        // ── The body's situation (masks and caps are frozen from it) ─────
        // The channel probes (EnvironmentContext; plan §3.1).
        var floorProbe = ctx.Floor;
        bool floorFound = floorProbe.Found;
        float floorY = floorProbe.Y;
        float dist = floorProbe.Distance;
        bool near     = ctx.LegsInReach;
        float supportVy = floorProbe.SurfaceVelocity.Y;
        bool anchored = ctx.Support(out _);
        // The tracker's samples carry the floor only where the legs reach it.
        if (!near) { floorFound = false; dist = float.PositiveInfinity; }

        // ── The plan ─────────────────────────────────────────────────────
        // u is intent: along dir, and up as well while a jump is held. A
        // launch's tilt is the direction the actuators produce — the legs'
        // ceiling (the push fade speed) up, the walk speed along — so the
        // band does not cap the rise at the x speed: with a fixed 1:1 tilt
        // a running hop reached 19 px against the neutral jump's 60.
        bool planning = dir != 0 || fold.Rising;
        // One along-path speed per intent, in every state (plan stage 6): the
        // walk (or the crouch's crawl) under the MaxWalkSpeed modifier along
        // dir, and while a jump plans a rise, the intent vector's magnitude
        // (the walk along, the rise ceiling up).
        // While the jump channel lives, the rise actuator is it, and its
        // ceiling is JumpTopSpeed (plan §11); otherwise the legs' push fade.
        float riseCeiling = vars.JumpHoldFrames > 0 ? cfg.JumpTopSpeed : cfg.FoldLegPushFadeSpeed;
        Vector2 u = fold.Rising
            ? Vector2.Normalize(new Vector2(dir * cfg.MaxWalkSpeed * ctx.Modifiers.MaxWalkSpeed, -riseCeiling))
            : new Vector2(dir, 0f);
        float ground = fold.MaxSpeed * ctx.Modifiers.MaxWalkSpeed;
        float speed = fold.Rising
            ? new Vector2(dir * ground, riseCeiling).Length()
            : ground;
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
        bool handCorner = ctx.HandCorner;
        for (int k = 0; k < H; k++) s.CornerPlant[k] = handCorner;
        // Contact-push surfaces (walls, a hole's lips) — same held state.
        ctx.Walls(out bool pushLeft, out bool pushRight);
        for (int k = 0; k < H; k++) { s.ContactPushLeft[k] = pushLeft; s.ContactPushRight[k] = pushRight; }

        // ── Reference polyline: the plan's nodes, and nothing else ────────
        // The body is NOT a vertex. It is a point near the route, projected
        // onto it like every predicted tick is (PathTracker.Solve): its
        // offset from the route is the band rows' business (perpendicular),
        // and the route's own tangent is the only "along". With the body as
        // vertex 0 the segment body → first node was read as path: its x
        // sign is the seed's grid-snap offset (a running jump's plan is a
        // column at the seed's x; one frame later the body is past it and
        // the drive pushed BACKWARD at its cap), and its length counted
        // against the arc-speed cap.
        int nv = 0;
        bool hoverColumnRows = hoverColumn;
        float arcStart = 0f;
        Vector2 t0 = u;
        if (havePath)
        {
            nv = PathTracker.BeginPolyline(s, s.LatticePath[0].Pos);
            for (int j = 1; j < count; j++) PathTracker.AppendVertex(s, ref nv, s.LatticePath[j].Pos);
            if (nv < 2) { havePath = false; nv = 0; }
            else
            {
                // Where the body is along the route (first segment extended
                // backward as a ray, so a body behind the seed still projects),
                // and the route's direction there: where the drive pushes. A
                // path that follows intent runs along dir; a neutral launch's
                // escape from under a slab starts sideways toward the open
                // side; a plain jump's column is straight up (no x at all).
                arcStart = PathTracker.ProjectArc(s.BeadVerts, s.BeadArc, nv, body.Position);
                PathTracker.PointAt(s.BeadVerts, s.BeadArc, nv, arcStart, out _, out t0);
            }
        }
        float yHover = floorY - fold.HoverOffset;
        // The SPEED along the path is capped, in every state (motion splits
        // along the path versus across it, never x versus y; a walk speed
        // capped in x let a body on a 45° bevel run at |v| = 141).
        bool arcSpeed = havePath && float.IsFinite(speed);
        // Walk speed is the most the legs can ADD, never a demand to brake
        // (plan §9.1): a body already moving faster along the route than the
        // state's speed is capped at its own speed, so no speed row asks the
        // solver to shed it. Drive itself cannot add past walk speed (its
        // per-side cut in BuildFold). The speed is measured along the route
        // tangent at the body, as the arc rows measure it (§10 q1, open).
        float arcCap = arcSpeed ? MathF.Max(speed, Vector2.Dot(body.Velocity, t0)) : float.PositiveInfinity;
        // Grip and slip (§9.1): Drive's one cap, weak while the feet slip,
        // scaled by the action modifier WalkAccel (§13; Drive only).
        bool slipping = StandingState.Slipping(ctx, out float vRelX);
        float driveCap = (slipping ? cfg.SlipDriveForce : cfg.FoldDriveForce) * ctx.Modifiers.WalkAccel;

        // ── The walls: clearance rows from the free rollout ──────────────
        // The path is guidance; the tiles are the constraint: the band lets
        // the body stray half a cell from a route the planner keeps only
        // LatticePathPlanner.Clearance clear of tiles, so without these the
        // body hit every corner of the bumpy corridor. The rows are the swept
        // body's penetrations along the free rollout, hard, with tile normals.
        // PathTracker.Solve keeps a row only while the route itself satisfies
        // it (Plans/MOVEMENT_RESOURCE_MODEL.md §12): the planner decides which
        // tiles are avoided, and a wall or ceiling the plan runs into (a
        // refused plan's ray, a jump's column) is hit, not braked for.
        // Margin = the engine's one clearance number.
        int wallRows = ClearanceConstraintBuilder.Build(ctx.Chunks, body.Polygon, s.DeliverySamples, H,
            LatticePathPlanner.Clearance, ClearanceConstraintBuilder.DefaultDeepViolation, s.Rows, out _,
            verticalFacesOnly: false);
        // Reserve: band 2H + arc H + speed rows 2H (zero input) + progress.
        wallRows = Math.Min(wallRows, ClearanceConstraintBuilder.MaxEvents - (5 * H + 1));

        // ── Sliding beads: outer passes of project → rows → solve ────────
        var settings = new PathTrackSettings
        {
            H = H, Dt = dt, Origin = body.Position, VertexCount = nv, ArcStart = arcStart,
            Band = band,
            ArcSpeedCap = arcCap,
            HoverLineY  = hoverColumnRows ? yHover : float.NaN,
            FallbackTangent = u,
            WallRows = wallRows,
            Iterations = cfg.FoldIterations,
        };
        var policy = new LatticePolicy
        {
            Cfg = cfg, Fold = fold, Dir = dir, Speed = speed,
            Origin = body.Position, Planning = planning,
            Granted = MovementResources.Available(ctx) & uses,
            DriveCap = driveCap, VRelX = vRelX, FloorVx = floorProbe.SurfaceVelocity.X, VelX = body.Velocity.X,
            JumpTicks = vars.JumpHoldFrames,
            JumpRiseCap = cfg.JumpTopSpeed + MathF.Max(0f, -vars.JumpSourceVy),
            T0 = t0, SupportVy = supportVy, WalkSpeed = cfg.MaxWalkSpeed * ctx.Modifiers.MaxWalkSpeed, AirSpeed = cfg.MaxAirSpeed * ctx.Modifiers.MaxAirSpeed,   // the PLAYER's cap: these arcs are flown on air control
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
        public float Speed, SupportVy, AirSpeed, WalkSpeed;
        public bool Planning;
        public MovementResource Granted;
        public float DriveCap, VRelX, FloorVx, VelX, JumpRiseCap;
        public int JumpTicks;
        public Vector2 Origin, T0;

        public int AddRows(CorrectorScratch s, int H, float dt, int pass, int rowCount)
        {
            // Zero input commands zero speed relative to the floor (§9.2 step
            // 2): braking is asked for by a row like everything else, and the
            // Drive channel's braking side answers it. The commanded motion is
            // the speed going to zero at the rate Drive can brake (its cap, on
            // top of whatever the baseline — slip friction — already does),
            // and no further. One row per tick bounds the displacement
            // relative to the floor, in the direction of motion, by that
            // profile (a lower bound as well made Drive's two sides fight
            // across the horizon; nothing asks for a reversal without it). A
            // body at rest gets the bound on both sides: hold station.
            if (Dir == 0 && Granted.Has(MovementResource.Legs))
            {
                float sgn = MathF.Sign(VRelX);
                float cmd = 0f;   // commanded displacement along sgn, relative to the floor
                for (int T = 0; T < H; T++)
                {
                    float vFree = sgn * (s.CoastVel[T].X - FloorVx);
                    cmd += MathF.Max(0f, vFree - DriveCap * dt * (T + 1)) * dt;
                    float dFree = sgn * (s.DeliverySamples[T].Pos.X - Origin.X - FloorVx * (T + 1) * dt);
                    var n = new Vector2(sgn == 0f ? 1f : sgn, 0f);
                    s.Rows[rowCount++] = new ClearanceRow
                        { Tick = T, Normal = -n, Depth = dFree - cmd, HingeScale = 1f, Reference = true };
                    if (sgn == 0f)
                        s.Rows[rowCount++] = new ClearanceRow
                            { Tick = T, Normal = n, Depth = cmd - dFree, HingeScale = 1f, Reference = true };
                }
            }
            return rowCount;
        }

        // (zero in free air: no actuators there — see the masks below)
        public bool ProgressAuthority(CorrectorScratch s, Vector2 tLast, out float capAlong)
        {
            bool legs = Granted.Has(MovementResource.Legs);
            float capX = legs ? DriveCap : 0f;
            float capY = tLast.Y < 0f
                ? (JumpTicks > 0 ? Cfg.JumpPushForce : legs ? Cfg.FoldLegForce : 0f)
                : (legs ? Cfg.FoldTuckForce : 0f);
            capAlong = MathF.Abs(tLast.X) * capX + MathF.Abs(tLast.Y) * capY;
            return Planning;
        }

        public int BuildChannels(CorrectorScratch s, int H, int rowCount)
        {
            float speed = Speed;
            var t0 = T0;
            var ch = s.Problem.Channels;
            // Masks: the resource layer's grant (probes ∧ the state's Uses),
            // nothing else (plan §3.2). Air control is the state's baseline,
            // not a solver channel.
            int count = CorrectorChannels.BuildFold(s, H, Dir, SupportVy, Granted,
                DriveCap, VRelX, Cfg.MaxWalkSpeed, JumpTicks, JumpRiseCap);
            // Redirect (left as it is, plan §0): the disc never grows forward
            // speed past min(walk speed, the state's speed) along the route's
            // direction at the body — a deflection keeps |v|, so a 200 px/s
            // drop rotated at a lip came out as 200 px/s of walk.
            ch[2].ForwardAxis = new Vector2(MathF.Sign(t0.X), 0f);
            ch[2].ForwardCap  = t0.X == 0f ? 0f
                : MathF.Min(WalkSpeed, float.IsFinite(speed) ? speed : AirSpeed);
            // ...parametrized as a force (CorrectionSolver.Project: the same
            // disc, in Δv/dt) so its lever is commensurate with the other
            // channels', and priced like the legs (an ε weight made a
            // deflection free — the solver's first choice for every band
            // violation, shedding the walk's velocity).
            ch[2].Lever  = LeverKind.Force;
            ch[2].Weight = ch[0].Weight;
            return count;
        }
    }
}
