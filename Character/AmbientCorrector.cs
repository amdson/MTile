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

// Ambient corrector mode (BALLISTIC_CORRECTOR_PLAN step 7) — the corrector run
// during FREE movement, replacing ReflexSystem + SteeringRamps. The "reference"
// is the coast prediction itself (no authored arc, no fidelity term): predict the
// free coast under the current input, emit clearance rows for PASSABLE features
// only (vertical faces — lips and corner tops; wall faces emit nothing and the
// coast deep-truncates, see ClearanceConstraintBuilder.verticalFacesOnly), solve
// a Redirect-only channel, and apply z₀ iff the plan is FEASIBLE.
//
// Energy honesty: the redirect disc only rotates/sheds the velocity the body
// already has — ambient mode injects nothing. Too slow for a corner ⇒ the plan is
// infeasible ⇒ silence ⇒ flush stall ⇒ the mantle's precondition takes the case:
// the same taper handoff the ramps had, now emergent.
//
// Anti-autopilot: corrections are input-gated (no held direction ⇒ nothing) and
// FEASIBLE-ONLY — if the passive redirect cannot fully clear the rows, apply
// NOTHING and let physics deliver the collision. Best-effort against an
// unavoidable impact is the autopilot slowdown; only maneuvers (mid-commitment)
// get best-effort. Net guarantee: ambient corrections never oppose the input
// direction — the system only helps the player go where they were already going.
//
// Cross-frame state is exactly two MovementVars fields (snapshot-covered):
// AmbientPrevDv (the Δu anchor) and AmbientLiftActive (the ground states'
// anti-pop exemption, one frame stale — the role SteeringRamp.AnyEngaged had).
// Scratch is the player's pooled CorrectorScratch: by this point in the frame
// the maneuver trigger/update solves that share it have already consumed it.
public sealed class AmbientCorrector
{
    private const float RedirectEpsilon = 1e-6f;   // uniqueness regularizer, not a knob
    private const float HingeWeight     = 1e6f;    // stiffness constant, not a knob

    // Runs after the movement state's Update (its policy + forces are final),
    // before the physics step reads AppliedForce — the slot Reconcile had.
    public void Apply(EnvironmentContext ctx, AmbientPolicy policy, bool startGrounded, ref MovementVars vars)
    {
        var cfg = MovementConfig.Current;
        var s = ctx.Corrector;
        int dir = ctx.Intent.CurrentHorizontal;

        if (s == null || !cfg.AmbientCorrectorEnabled || dir == 0
            || (!policy.Over && !policy.Under))
        {
            vars.AmbientPrevDv = Vector2.Zero;
            vars.AmbientLiftActive = false;
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
            s.Rows, out _, verticalFacesOnly: true);

        // Per-sense policy: Over gates upward rows (corner-vault assist), Under
        // gates downward rows (head-tuck assist) — same split AmbientPolicy always had.
        int kept = 0;
        for (int j = 0; j < rowCount; j++)
        {
            bool up = s.Rows[j].Normal.Y < 0f;
            if (up ? policy.Over : policy.Under)
                s.Rows[kept++] = s.Rows[j];
        }
        rowCount = kept;

        if (rowCount == 0)
        {
            vars.AmbientPrevDv = Vector2.Zero;
            vars.AmbientLiftActive = false;
            return;
        }

        // Homotopy pick (the plan's greedy first-hit side, made deterministic):
        // vertical rows of BOTH senses can coexist when a thick feature exposes a
        // far-side face (a tunnel roof offers "climb over" up-rows next to the
        // mouth's "duck under" down-rows) — contradictory demands that would
        // refuse as a set. Try the shallower sense first (the side the coast
        // actually grazes), fall back to the other; first feasible plan wins.
        Span<int> senseOrder = stackalloc int[2];
        int senses = SenseOrder(s.Rows, rowCount, senseOrder);
        Span<ClearanceRow> allRows = stackalloc ClearanceRow[ClearanceConstraintBuilder.MaxEvents];
        for (int j = 0; j < rowCount; j++) allRows[j] = s.Rows[j];

        for (int si = 0; si < senses; si++)
        {
            int senseRows = KeepSense(allRows, rowCount, senseOrder[si], s.Rows);
            if (senseRows == 0) continue;

            for (int k = 0; k < n; k++) s.CoastVel[k] = s.Samples[k].Vel;
            var p = s.Problem;
            p.H = n; p.Dt = ctx.Dt;
            p.CoastVel = s.CoastVel;
            p.Rows = s.Rows; p.RowCount = senseRows;
            p.ChannelCount = 1;
            p.Channels[0] = new ChannelDef
            {
                Lever = LeverKind.VelocityUpdate, Weight = RedirectEpsilon,
                Redirect = true, ActiveFrom = 0, ActiveTo = n,
            };
            p.PrevApplied[0] = vars.AmbientPrevDv;
            p.DeltaWeight = cfg.CorrectorDeltaWeight;
            p.HingeWeight = HingeWeight;
            p.InnerIterations = CorrectionSolver.DefaultInnerIterations;

            float residual = CorrectionSolver.Solve(p, s.Z, s.ZScratch);

            // Feasible-only: an unresolvable plan is never applied (honest bonk).
            if (residual > cfg.AmbientRefusalResidual) continue;

            if (ctx.Dt > 0f) ctx.Body.AppliedForce += s.Z[0] / ctx.Dt;
            vars.AmbientPrevDv = s.Z[0];
            // Lift flag for the ground states' anti-pop exemption: an upward
            // correction was applied this frame (read one frame stale, like the
            // ramps' Engaged bit was).
            vars.AmbientLiftActive = s.Z[0].Y < -0.01f;
            return;
        }

        vars.AmbientPrevDv = Vector2.Zero;
        vars.AmbientLiftActive = false;
    }

    // Sense partition: 0 = single mixed set (only one sense present — use all rows),
    // else two passes ordered shallower-max-depth first. senseOut holds -1 (up),
    // +1 (down), or 0 (all rows).
    private static int SenseOrder(ClearanceRow[] rows, int rowCount, Span<int> senseOut)
    {
        float upMax = 0f, downMax = 0f;
        for (int j = 0; j < rowCount; j++)
        {
            if (rows[j].Normal.Y < 0f) upMax = MathF.Max(upMax, rows[j].Depth);
            else downMax = MathF.Max(downMax, rows[j].Depth);
        }
        if (upMax == 0f || downMax == 0f) { senseOut[0] = 0; return 1; }
        senseOut[0] = downMax <= upMax ? 1 : -1;
        senseOut[1] = -senseOut[0];
        return 2;
    }

    // Copies the requested sense's rows from source into dest (sense 0 = all);
    // returns the kept count. Row order is preserved — deterministic.
    private static int KeepSense(Span<ClearanceRow> source, int rowCount, int sense, ClearanceRow[] dest)
    {
        int kept = 0;
        for (int j = 0; j < rowCount; j++)
        {
            bool up = source[j].Normal.Y < 0f;
            if (sense == 0 || (sense == -1 && up) || (sense == 1 && !up))
                dest[kept++] = source[j];
        }
        return kept;
    }
}
