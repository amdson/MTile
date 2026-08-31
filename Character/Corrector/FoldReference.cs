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
//
// Structure: Admit (regime guards) → Rollout (the reference) → Track (rows →
// deform → servo). The lattice engine (FoldLattice, FoldEngine "lattice")
// reuses Admit and Track with its own rollout — the tail is engine-agnostic.
public static class FoldReference
{
    private const float HingeWeight  = 1e6f;    // stiffness constant, not a knob
    private const float DeformWeight = 1e-3f;   // regularizer: prefer the raw reference
    // Position-space deform authority: how far the path may deviate from the
    // reference (px), and how fast that deviation may build (px/s — the
    // implied extra velocity; the old velocity-update cap's role). 8 px of
    // position authority covers ducks/steps the classification admits;
    // anything needing more was already a give-up at row emission.
    // Body-relative px, deliberately independent of Chunk.TileSize.
    private const float DeformCap = 8f;
    private const float SlewCap   = 150f;

    // TEMP EXPERIMENT: redirect audit counters (write-only debug stats).
    public static int AuditSolves, AuditMaskFrames, AuditFireFrames;
    public static float AuditMaxZr;
    public static Vector2 AuditNetZr;

    // Generates the reference, deforms it around terrain, and servos the body
    // toward its tick-0 target. Returns false when the regime doesn't apply
    // (caller falls back to the ballistic-qp path).
    public static bool TryApply(EnvironmentContext ctx, CorrectorScratch s,
                                in FoldProfile fold, AmbientPolicy policy, int dir,
                                ref MovementVars vars)
    {
        if (!Admit(ctx, s, out var template, out float anchorY)) return false;
        int n = Rollout(ctx, s, fold, dir, template, anchorY);
        Track(ctx, s, fold, policy, n, ref vars);
        return true;
    }

    // The regime gates (shared with the lattice engine — LATTICE_PATH_PLANNER
    // §4.7 inherits them verbatim). anchorY = the support surface under the
    // body's x; false = not this engine's regime.
    internal static bool Admit(EnvironmentContext ctx, CorrectorScratch s,
                               out CObstacleTemplate template, out float anchorY)
    {
        var cfg = MovementConfig.Current;
        template = null; anchorY = 0f;
        if (s == null || ctx.Dt <= 0f) return false;
        var body = ctx.Body;

        if (ctx.Modifiers.PreserveExternalVelocity) return false;          // knockback
        // Launched — support-relative (BACKLOG 5.8): a body riding a rising
        // floor (sprout lift) is anchored in the floor's frame, not ballistic.
        float supportVy = ctx.TryGetGround(out var supportFsd) ? supportFsd.SurfaceVelocity.Y : 0f;
        if (-(body.Velocity.Y - supportVy) > cfg.SpringMaxRiseSpeed) return false;
        if (body.Velocity.Y > cfg.MaxGroundEngageVnRel) return false;      // plunging

        template = CObstacleTemplate.For(body.Polygon);
        anchorY = AmbientCorrector.FloorEnvelope(ctx.Chunks, template, body.Position.X,
            body.Position.Y - 2f, body.Position.Y + BallisticPredictor.SupportReach,
            out bool anchored);
        return anchored;                                                   // else free flight
    }

    // ── The reference rollout ────────────────────────────────────────────
    // Fills s.Samples / s.CoastVel for the horizon; returns the tick count.
    // With dir == 0 this is the pure hover column the lattice engine also
    // uses (a direction-ordered lattice has no order without a direction).
    internal static int Rollout(EnvironmentContext ctx, CorrectorScratch s,
                                in FoldProfile fold, int dir,
                                CObstacleTemplate template, float anchorY)
    {
        var cfg = MovementConfig.Current;
        var body = ctx.Body;
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
        return n;
    }

    // ── Rows → deform → servo over whatever reference s.Samples[0..n) holds ──
    // The tail every reference-generating engine shares (ref, lattice).
    internal static void Track(EnvironmentContext ctx, CorrectorScratch s,
                               in FoldProfile fold, AmbientPolicy policy, int n,
                               ref MovementVars vars)
    {
        var cfg = MovementConfig.Current;
        var body = ctx.Body;
        float dt = ctx.Dt;

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
        Vector2 zr0 = Vector2.Zero;   // TEMP EXPERIMENT: redirect δv at tick 0
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
            // TEMP EXPERIMENT: qp's Redirect (Thales plant-and-deflect disc)
            // grafted in as a second channel — same mask as BuildFold (near
            // ground but not supported), gated on FoldRedirectEnabled for
            // hot A/B. If the solver's clean it should be harmless; watching
            // for the vx-eating pathology the qp audit measured.
            if (cfg.FoldRedirectEnabled)
            {
                float legReach = 2f * PlayerCharacter.Radius - 2f + 20f;
                for (int k = 0; k < n; k++)
                    s.ChannelMask[1][k] = !s.Samples[k].Grounded
                        && !float.IsPositiveInfinity(s.Samples[k].FloorY)
                        && s.Samples[k].FloorY - s.Samples[k].Pos.Y <= legReach;
                p.Channels[1] = new ChannelDef
                {
                    Id = CorrectionChannel.Redirect,
                    Lever = LeverKind.VelocityUpdate, Weight = 1e-6f, Redirect = true,
                    ActiveMask = s.ChannelMask[1], SkipSoftHorizontal = true,
                };
                p.PrevApplied[1] = Vector2.Zero;
                p.ChannelCount = 2;
            }
            p.DeltaWeight = cfg.CorrectorDeltaWeight;
            p.HingeWeight = HingeWeight;
            p.InnerIterations = cfg.FoldIterations;
            p.RowPush = s.RowPush;

            CorrectionSolver.Solve(p, s.Z, s.ZScratch);
            z0 = s.Z[0];
            // TEMP EXPERIMENT: redirect's tick-0 velocity update (z layout
            // is [c*H + k], so channel 1 tick 0 lives at index n).
            if (p.ChannelCount == 2) zr0 = s.Z[n];
            AuditSolves++;
            if (p.ChannelCount == 2)
            {
                bool any = false;
                for (int k = 0; k < n; k++) if (s.ChannelMask[1][k]) { any = true; break; }
                if (any) AuditMaskFrames++;
                if (zr0 != Vector2.Zero)
                {
                    AuditFireFrames++;
                    AuditNetZr += zr0;
                    AuditMaxZr = MathF.Max(AuditMaxZr, zr0.Length());
                }
            }

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
        Vector2 v1Des = s.Samples[0].Vel + z0 / dt + zr0;
        Vector2 v1Cur = body.Velocity + (body.AppliedForce + ctx.Gravity) * dt;
        Vector2 dF = (v1Des - v1Cur) / dt;
        float mag = dF.Length();
        if (mag > cfg.GuidedMaxForce) dF *= cfg.GuidedMaxForce / mag;
        body.AppliedForce += dF;
    }
}
