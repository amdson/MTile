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

// How the active movement state participates in the stand fold — the reference-
// shaping half of the per-state membership table (CORRECTOR_CONSOLIDATION_PLAN
// §3.5; the actuation half is CorrectorChannels, the assist gating half is
// AmbientPolicy). Published fresh every frame like AmbientPolicy. Fold = false
// (the default) means the state owns its own support — the ambient layer runs
// only the non-fold graze assists under the state's policy.
public struct FoldProfile
{
    public bool  Fold;          // support/locomotion delegated to the ambient solve
    public float HoverOffset;   // hover clearance above the C-obstacle top surface (px)
    public float ClimbReachUp;  // envelope may bind floors this far above the anchor (px)
    public float MaxSpeed;      // progress-target speed (px/s, pre-modifier)

    public static FoldProfile None => default;

    // Standing/Falling: hover ≈ the old spring equilibrium; 1-high ledges ramp
    // the reference (16 ≤ 20), 2-high walls never do (32 > 20).
    public static FoldProfile Stand => new()
    {
        Fold = true, HoverOffset = 10f, ClimbReachUp = 20f,
        MaxSpeed = MovementConfig.Current.MaxWalkSpeed,
    };

    // Crouched: same fold, lower reference — the crouch IS reference shaping,
    // not a new mechanism. Hover 0 = center resting on the C-obstacle surface
    // (≈ the old crouch spring equilibrium at float height 0); climb reach
    // covers surface roughness only (a crouch never mounts ledges); crawl speed.
    public static FoldProfile Crouch => new()
    {
        Fold = true, HoverOffset = 0f, ClimbReachUp = 4f,
        MaxSpeed = MovementConfig.Current.CrouchMaxWalkSpeed,
    };
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
//  - Actuation is the restricted channel stack (CorrectorChannels.BuildFold):
//    LegServo /
//    Traction / CornerAssist / Redirect / Tuck with per-tick masks and caps,
//    solved JOINTLY — senses are never partitioned (an up/down homotopy split
//    would strip support rows from a down pass).
public sealed class AmbientCorrector
{
    private const float HingeWeight     = 1e6f;    // stiffness constant, not a knob
    private const float RedirectEpsilon = 1e-6f;   // uniqueness regularizer, not a knob
    // Fold constants. Support/progress rows are soft (HingeScale ≪ 1) so hard
    // clearance rows can overpower them; the deeper fixed iteration budget
    // keeps the joint mixed-sense solve converged. Hover offset and climb
    // reach are per-state (FoldProfile); BallisticPredictor.SupportReach is the band of floors
    // the reference may bind BELOW the anchor is BallisticPredictor.SupportReach
    // (further down = free fall) — one constant with the gravity-hold gate.
    private const float HoverHingeScale = 0.02f;
    private const float ProgressHingeScale = 0.02f;
    private const int   FoldIterations  = 16;
    private const float AnchorLeak      = 0.7f;
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
    // fold = the state's published FoldProfile: Fold set means hover support and
    // locomotion are this solve's job (see the fold note above).
    public void Apply(EnvironmentContext ctx, AmbientPolicy policy, bool startGrounded,
                      in FoldProfile fold, ref MovementVars vars)
    {
        var cfg = MovementConfig.Current;
        var s = ctx.Corrector;
        int dir = ctx.Intent.CurrentHorizontal;

        if (s == null || !cfg.AmbientCorrectorEnabled
            || (!policy.Over && !policy.Under && !fold.Fold))
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
        // gates downward rows (head-tuck assist) — same split AmbientPolicy always
        // had. Impact honesty: an UP row anchored at a plunging tick (descent past
        // the FSD-era engagement gate) is a floor slam, not a catchable landing —
        // it belongs to the raw swept-impact path (bounce/break tuning), and
        // keeping it would let the redirect disc air-brake the fall. Dropped
        // regardless of policy.
        int kept = 0;
        for (int j = 0; j < rowCount; j++)
        {
            bool up = s.Rows[j].Normal.Y < 0f;
            if (up && s.Samples[s.Rows[j].Tick].Vel.Y > cfg.MaxGroundEngageVnRel) continue;
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
        // Launch unbind (CORRECTOR_CONSOLIDATION_PLAN §3.2): a body rising
        // faster than any support could ever push it (SpringMaxRiseSpeed — the
        // same gate StandingState's entry uses) is BALLISTIC. The envelope
        // reference lets go entirely: no hover pull-down (an upward launch is
        // not "overshoot"), no walk-speed tracking against launch momentum.
        // Hard clearance rows still protect the arc, and the landing catch
        // re-binds on descent through the anchor query below.
        bool launched = -ctx.Body.Velocity.Y > cfg.SpringMaxRiseSpeed;

        if (fold.Fold && !launched)
        {
            var template = CObstacleTemplate.For(ctx.Body.Polygon);
            int lim = Math.Min(n, truncatedAt);
            float target = dir * fold.MaxSpeed * ctx.Modifiers.MaxWalkSpeed;
            float x0 = ctx.Body.Position.X;
            float y0 = ctx.Body.Position.Y;

            // Support anchor: the surface the body is ACTUALLY standing on (the
            // highest standable surface at-or-below the current pose, within leg
            // reach). The climb band is measured from THIS, not from the coast's
            // own height — so a transient rise cannot re-bind higher floors and
            // ladder the reference up a wall. One-high (16 ≤ ClimbReachUp) binds;
            // two-high (32) never does, whatever the mid-scramble pose.
            float anchorY = FloorEnvelope(ctx.Chunks, template, x0,
                                          y0 - 2f, y0 + BallisticPredictor.SupportReach, out bool anchored);

            // The whole envelope is anchored: unanchored (airborne beyond leg
            // reach) means free flight — no hover request, and no x-progress
            // tracking either (air control is the baseline feedforward's job;
            // solver-tracked walk speed in the air would brake legitimate
            // over-walk-speed flight). Knockback exemption: while modifiers
            // preserve external velocity (hitstun/launch windows), progress
            // tracking is suppressed — support rows still hold the body up,
            // but the solver must not fight the knockback horizontally.
            bool trackProgress = anchored && !ctx.Modifiers.PreserveExternalVelocity;

            for (int k = 0; anchored && k < lim && rowCount < ClearanceConstraintBuilder.MaxEvents - 1; k++)
            {
                float xRef = x0 + target * (k + 1) * ctx.Dt;

                // x-progress tracking (two-sided, dir ≠ 0 only). The forward
                // side is served by Drive; the backward side has NO channel
                // that can serve it (Drive is unilateral along intent) — its
                // one effect is damping Drive's Δ-anchor: without a counter-
                // gradient the cross-frame smoothness chain sustains thrust
                // with no demand and the walk ratchets past its target
                // (one-sided emission ran away to 4× walk speed). Momentum
                // is still never actively braked. dir 0 emits NO x rows:
                // station-keeping is baseline friction (FoldBaseline), and
                // rows here would let hard clearance rows recruit x-channels
                // to dodge.
                float errX = xRef - s.Samples[k].Pos.X;
                if (trackProgress && dir != 0 && MathF.Abs(errX) > 0.01f)
                    s.Rows[rowCount++] = new ClearanceRow
                    {
                        Tick = k, Normal = new Vector2(MathF.Sign(errX), 0f),
                        Depth = MathF.Abs(errX), HingeScale = ProgressHingeScale,
                    };

                // y tracking of the floor envelope at the reference x, bound to
                // the anchor's climb band. The reference does NOT bind against
                // a plunging tick: past the FSD-era engagement gate the landing
                // belongs to the raw swept-impact path (the impact-materials
                // spec is tuned against raw contact speeds) — emitting catch
                // rows there would let the redirect disc air-brake the fall.
                if (s.Samples[k].Vel.Y > cfg.MaxGroundEngageVnRel) continue;
                float envY = FloorEnvelope(ctx.Chunks, template, xRef,
                                           anchorY - fold.ClimbReachUp, anchorY + BallisticPredictor.SupportReach, out bool found);
                if (!found) continue;
                if (envY > anchorY + TuckReachDown) continue;   // lip: unbind, fall naturally
                float yRef = envY - fold.HoverOffset;
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

        if (fold.Fold)
        {
            // The restricted channel stack, solved jointly — every channel
            // contributes perturbations to one plan. Δ anchors live in
            // MovementVars (snapshot-covered), not scratch: rollback restore
            // must reproduce the smoothness chain exactly.
            p.ChannelCount = CorrectorChannels.BuildFold(s, n, rowCount, dir,
                fold.MaxSpeed * ctx.Modifiers.MaxWalkSpeed);
            // LEAKY Δ anchors: continuity across frames without DC memory. A
            // full-strength anchor lets a force channel sustain thrust with no
            // remaining demand (the smoothness chain becomes momentum and the
            // walk ratchets past its target); the leak keeps the anti-bang-bang
            // smoothing over a few frames while bleeding any sustained bias.
            for (int c = 0; c < p.ChannelCount; c++)
                p.PrevApplied[c] = vars.AmbientChannelPrev[c] * AnchorLeak;
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
        p.InnerIterations = fold.Fold ? FoldIterations : CorrectionSolver.DefaultInnerIterations;
        // Contact-push attribution (render-only; the pooled Problem is shared
        // with the maneuver states, so set it explicitly either way).
        p.RowPush = s.CaptureTrajectories ? s.RowPush : null;

        float residual = CorrectionSolver.Solve(p, s.Z, s.ZScratch);

        // Feasible-only refusal survives ONLY outside the fold: a fold plan
        // carries hover support and must always apply.
        if (!fold.Fold && residual > cfg.AmbientRefusalResidual)
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
        if (!fold.Fold) vars.AmbientChannelPrev = default;   // fold anchors die with the fold
        for (int c = 0; c < p.ChannelCount; c++)
        {
            var z0 = s.Z[c * n];
            if (fold.Fold) vars.AmbientChannelPrev[c] = z0;
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

}
