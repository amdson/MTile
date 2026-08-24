using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Pooled per-player scratch for the corrector's predict → build → solve loop.
// Pure derived data, fully rewritten every solve — never snapshot state. The
// only cross-frame corrector state lives in MovementVars: ManeuverChannelPrev
// (the maneuver stack's Δu anchors), AmbientPrevDv, and AmbientChannelPrev
// (the fold's).
public sealed class CorrectorScratch
{
    public readonly CoastSample[]  Samples  = new CoastSample[BallisticPredictor.MaxHorizon];
    public readonly ClearanceRow[] Rows     = new ClearanceRow[ClearanceConstraintBuilder.MaxEvents];
    public readonly Vector2[]      CoastVel = new Vector2[BallisticPredictor.MaxHorizon];
    // Z layout is z[c·H + k] — sized for the full channel stack.
    public readonly Vector2[]      Z        = new Vector2[CorrectionSolver.MaxChannels * BallisticPredictor.MaxHorizon];
    public readonly Vector2[]      ZScratch = new Vector2[CorrectionSolver.MaxChannels * BallisticPredictor.MaxHorizon];
    public readonly Vector2[]      TickDv   = new Vector2[BallisticPredictor.MaxHorizon];
    // Per-channel per-tick activation masks + velocity-conditioned caps — frozen
    // from the coast each solve, pure derived data. The cross-frame Δ anchors
    // live in MovementVars.AmbientChannelPrev (snapshot-covered), NOT here.
    public readonly bool[][]  ChannelMask = MakeMasks();
    public readonly float[][] ChannelCap  = MakeCaps();
    // Convex-corner plant ticks (CorrectorChannels.MarkCornerPlants) — filled
    // from the coast by the integration layers (AmbientCorrector.Apply /
    // ManeuverCorrector.Run) before the channel build; pure derived data.
    public readonly bool[] CornerPlant = new bool[BallisticPredictor.MaxHorizon];
    private static bool[][] MakeMasks()
    {
        var m = new bool[CorrectionSolver.MaxChannels][];
        for (int c = 0; c < m.Length; c++) m[c] = new bool[BallisticPredictor.MaxHorizon];
        return m;
    }
    private static float[][] MakeCaps()
    {
        var m = new float[CorrectionSolver.MaxChannels][];
        for (int c = 0; c < m.Length; c++) m[c] = new float[BallisticPredictor.MaxHorizon];
        return m;
    }
    public readonly CorrectionProblem Problem = new()
    {
        Channels    = new ChannelDef[CorrectionSolver.MaxChannels],
        PrevApplied = new Vector2[CorrectionSolver.MaxChannels],
    };
    // Hypothetical-state probe for feasibility-as-trigger: CheckPreConditions
    // rolls the WOULD-BE maneuver (post-hop state) through the same predict →
    // rows → solve loop without touching the real body. Polygon is re-pointed at
    // the owning body's before every use.
    public readonly PhysicsBody ProbeBody =
        new(PlayerCharacter.CreateBodyPolygon(), Vector2.Zero);

    // ── Trajectory capture for the debug overlay (render-only diagnostics) ──
    // CaptureTrajectories is set by the HOST from its draw flags; the sim only
    // gates capture WORK on it — the captured buffers are never read by sim
    // logic, so the flag cannot affect simulation state. Reference = the arc
    // planned at Enter (frozen for the maneuver); Ballistic = this tick's
    // uncorrected coast; Solved = this tick's coast with the final corrections
    // applied. Ballistic/Solved are cleared every frame (BeginFrame) so the
    // renderer only ever sees trajectories computed THIS timestep.
    public bool CaptureTrajectories;
    public readonly CoastSample[] ReferenceTrajectory = new CoastSample[BallisticPredictor.MaxHorizon];
    public int ReferenceCount;
    public readonly CoastSample[] BallisticTrajectory = new CoastSample[BallisticPredictor.MaxHorizon];
    public int BallisticCount;
    public readonly CoastSample[] SolvedTrajectory = new CoastSample[BallisticPredictor.MaxHorizon];
    public int SolvedCount;

    // Per-contact push attribution for the APPLIED solve this frame (ambient or
    // maneuver — at most one applies per frame): each clearance row's predicted
    // contact position (body center at the row's tick) and the δv it shoved into
    // the applied tick-0 correction (CorrectionProblem.RowPush; force = δv/dt).
    // Cleared every frame; empty whenever nothing was applied.
    // Elective-deliverability scratch (AmbientCorrector): the corrected rollout
    // and the R1 stepped-reference record it is checked against. Pure per-frame
    // derived data, never snapshot state.
    public readonly CoastSample[] DeliverySamples = new CoastSample[BallisticPredictor.MaxHorizon];
    public readonly float[] RefY     = new float[BallisticPredictor.MaxHorizon];
    public readonly bool[]  RefClimb = new bool[BallisticPredictor.MaxHorizon];

    public readonly Vector2[] RowPush    = new Vector2[ClearanceConstraintBuilder.MaxEvents];
    public readonly Vector2[] ContactPos = new Vector2[ClearanceConstraintBuilder.MaxEvents];
    public readonly Vector2[] ContactDv  = new Vector2[ClearanceConstraintBuilder.MaxEvents];
    public int ContactCount;

    // What the applied solve exerted this step, by channel and by contact tile
    // (CorrectorLedger doc) — always-on bookkeeping, unlike the capture buffers.
    public readonly CorrectorLedger Ledger = new();

    // Nonlinear fold engine (MovementConfig.FoldEngine "lm") — pooled per
    // player like everything here; stateless between solves (seed is always
    // the straight line at current velocity), so never snapshot state.
    public readonly TrajectoryLm Lm = new();
    // Lattice fold engine (MovementConfig.FoldEngine "lattice") — the DP's
    // pooled scratch plus its output polyline; fully rewritten every solve,
    // never snapshot state.
    public readonly LatticePathPlanner Lattice = new();
    public readonly CoastSample[] LatticePath = new CoastSample[LatticePathPlanner.MaxPath];
    // LatticeTracker's bead scratch: the reference polyline (body + path
    // nodes) with cumulative arc length, and the corrected displacement per
    // tick between outer passes. Pure per-solve derived data.
    public readonly Vector2[] BeadVerts = new Vector2[LatticePathPlanner.MaxPath + 1];
    public readonly float[]   BeadArc   = new float[LatticePathPlanner.MaxPath + 1];
    public readonly Vector2[] TrackDelta = new Vector2[BallisticPredictor.MaxHorizon];

    public void BeginFrame() { BallisticCount = 0; SolvedCount = 0; ContactCount = 0; Ledger.Clear(); }
}
