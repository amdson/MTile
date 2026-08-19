using System;
using Microsoft.Xna.Framework;

namespace MTile;

// The reference-path corrector — the third way a state uses the solve (see
// Movement.cs). For a GUIDED move the future isn't physics to predict, it's an
// authored arc (a retargeted HermiteClip) — so there is NO coast phase: the
// reference path itself is the trajectory the constraint builder sweeps, and
// the solve's single free channel deforms THE PATH (per-tick path-velocity
// tweaks, capped) just enough to clear terrain. The state's servo
// (ReferencePath.TrackForce) then tracks the deformed target — the solver
// plans where the servo should aim; the servo owns all actuation.
//
// One solve per tick against the raw reference (no inner re-rollout pass —
// the per-frame re-solve is the outer loop), Δ-anchored across frames like
// every other corrector loop. The force ledger is NOT written here: the
// deformation is a plan adjustment, not an exerted force — the body force is
// the servo's; per-tile attribution of servo forces is future work.
public static class ReferenceCorrector
{
    private const float HingeWeight  = 1e6f;    // stiffness constant, not a feel knob
    private const float DeformWeight = 1e-3f;   // regularizer: prefer the authored arc
    // Path-deformation authority: per-tick path-velocity tweak cap (px/s).
    // Enough to bend an arc around a jutting lip over a few ticks; small
    // enough that a badly retargeted clip stalls against terrain (the servo's
    // own force cap keeps the stall honest) instead of teleporting around it.
    private const float DeformCap = 150f;

    // Evaluate the clip at `progress` and deform that target around terrain.
    // With no scratch (hand-built test contexts) or no violated rows, returns
    // the raw clip target — behavior-identical to servoing the clip directly.
    public static void DeformedTarget(
        EnvironmentContext ctx, HermiteClipDocument clip, in ReferenceFrame frame,
        float progress, float duration, ref ChannelAnchors anchors,
        out Vector2 target, out Vector2 targetVel)
    {
        duration  = MathF.Max(duration, 1e-4f);
        target    = frame.Map(clip.Eval(progress));
        targetVel = frame.MapTangent(clip.EvalTangent(progress)) / duration;

        var s = ctx.Corrector;
        if (s == null || ctx.Dt <= 0f) return;

        var cfg = MovementConfig.Current;
        int n = Math.Min(cfg.CorrectorHorizon, BallisticPredictor.MaxHorizon);

        // The reference path IS the swept trajectory: sample the clip at the
        // next n ticks (clamped at the gate — the servo holds there past 1).
        Vector2 prev = target;
        for (int k = 0; k < n; k++)
        {
            float t = MathF.Min(progress + (k + 1) * ctx.Dt / duration, 1f);
            var pos = frame.Map(clip.Eval(t));
            s.Samples[k].Pos      = pos;
            s.Samples[k].Vel      = (pos - prev) / ctx.Dt;
            s.Samples[k].Grounded = false;
            s.Samples[k].FloorY   = float.PositiveInfinity;
            s.CoastVel[k] = s.Samples[k].Vel;
            prev = pos;
        }

        int rowCount = ClearanceConstraintBuilder.Build(
            ctx.Chunks, ctx.Body.Polygon, s.Samples, n,
            cfg.CorrectorMargin, ClearanceConstraintBuilder.DefaultDeepViolation,
            s.Rows, out _);

        if (s.CaptureTrajectories)
        {
            Array.Copy(s.Samples, s.BallisticTrajectory, n);   // the raw reference
            s.BallisticCount = n;
        }
        if (rowCount == 0) { anchors = default; return; }

        var p = s.Problem;
        p.H = n; p.Dt = ctx.Dt;
        p.CoastVel = s.CoastVel;
        p.Rows = s.Rows; p.RowCount = rowCount;
        p.ChannelCount = 1;
        p.Channels[0] = new ChannelDef
        {
            Id = CorrectionChannel.PathDeform,
            Lever = LeverKind.VelocityUpdate, Weight = DeformWeight, Cap = DeformCap,
            ActiveFrom = 0, ActiveTo = n,
        };
        p.PrevApplied[0] = anchors[0] * CorrectorChannels.AnchorLeak;
        p.DeltaWeight = cfg.CorrectorDeltaWeight;
        p.HingeWeight = HingeWeight;
        p.InnerIterations = CorrectionSolver.DefaultInnerIterations;
        p.RowPush = s.RowPush;

        CorrectionSolver.Solve(p, s.Z, s.ZScratch);
        anchors = default;
        anchors[0] = s.Z[0];

        // Deformed tick-0 target: a tick-0 path-velocity tweak z₀ moves the
        // path's tick-0 position by z₀·dt (the VelocityUpdate lever at T = 0).
        target    += s.Z[0] * ctx.Dt;
        targetVel += s.Z[0];

        if (s.CaptureTrajectories)
        {
            // Deformed path for the overlay: pos′_T = pos_T + Σ_{k≤T} (T−k+1)·dt·z_k.
            for (int T = 0; T < n; T++)
            {
                var off = Vector2.Zero;
                for (int k = 0; k <= T; k++) off += (T - k + 1) * ctx.Dt * s.Z[k];
                s.SolvedTrajectory[T] = s.Samples[T];
                s.SolvedTrajectory[T].Pos += off;
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
}
