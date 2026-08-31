using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// PROTOTYPE (freeze-frame oracle only — not wired into the live sim): layered-
// DAG lattice search with exact-state carrying ("option 2" of the seed-search
// design conversation, 2026-08-05).
//
// The idea under test: replace the hand-designed reference rules (wall
// classification, duck budgets, regime gates) with a short-horizon search.
//   - The graph is layered by time: each edge integrates EdgeTicks of real
//     dynamics under a CONSTANT force, so every path is flyable by
//     construction. Velocity is never quantized in the dynamics.
//   - Enumeration is over OUTCOMES, not channel combinations: from the exact
//     entry state, the reachable displacement box (set by the channel-style
//     axis caps) is sampled uniformly, endpoints and free coast included; each
//     sample's constant force is the closed-form 2d/T². A new channel = a
//     wider box + a cost column, never more branching.
//   - Cells + coarse velocity bins are used ONLY to dedup rival entrants
//     (hybrid-A* style): the best entrant's exact state is what expands, so
//     quantization costs optimality, never feasibility.
//   - Cost mirrors the fold's objective: channel-weighted squared force
//     (effort) + soft tracking toward the carry reference (x at target speed,
//     y at floor-envelope hover). No hard rows: collision is a hard PRUNE.
// Output: per-tick samples of the best final-layer path, for the debug
// overlay (drawn alongside the LM oracle in the freeze-frame inspector).
public static class LatticePlanner
{
    private const int   EdgeTicks   = 3;       // ticks integrated per edge
    private const float CellSize    = 5.33f;   // px dedup lattice — body-relative, independent of Chunk.TileSize
    private const float VBin        = 40f;     // px/s dedup velocity bin
    private const int   FSamples    = 5;       // force-box samples per axis
    private const int   BeamWidth   = 128;     // best-cost survivors per layer
    // Collision probe offsets (approx the hexagon: half-width ~6, half-height
    // ~10.4 at Radius 12). Prototype-only stand-in for the real C-obstacle.
    private static readonly Vector2[] BodyProbe =
    {
        new(0f, 0f), new(6f, 0f), new(-6f, 0f), new(0f, -10.4f), new(0f, 10.4f),
        new(4.2f, -7.3f), new(-4.2f, -7.3f), new(4.2f, 7.3f), new(-4.2f, 7.3f),
    };
    // Channel-style weights/caps (mirrors BuildFold's stack; cost scale is
    // prototype-tuned, not yet unified with the QP's units).
    private const float WLegUp   = 0.01f;      // up-force effort (LegServo)
    private const float WTuck    = 0.5f;       // down-force effort (Tuck)
    private const float WDrive   = 0.05f;      // ground x effort (Drive)
    private const float WAir     = 0.05f;      // air x effort (AirLateral)
    // px^-2 arrival tracking penalty. Must sit orders above the effort weights
    // (the fold's soft rows are 2e4-stiff against the same channel weights) or
    // lagging the reference is cheaper than actuating toward it.
    private const float SoftTrack = 100f;

    // Prototype debug: per-layer frontier sizes of the last Solve.
    public static string LastDebug = "";

    private struct Node
    {
        public Vector2 Pos, Vel;   // EXACT state of the best entrant
        public float  Cost;
        public int    Parent;      // index into the flat node list
        public Vector2 Force;      // constant force on the edge that reached us
    }

    public static int Solve(ChunkMap chunks, Polygon body, Vector2 pos0, Vector2 vel0,
                            int dir, float targetSpeed, float hoverOffset,
                            Vector2 gravity, float dt, int H,
                            CoastSample[] outSamples, out float cost)
    {
        cost = 0f;
        if (dt <= 0f) return 0;
        var cfg = MovementConfig.Current;
        var template = CObstacleTemplate.For(body);
        int layers = Math.Clamp(H, EdgeTicks, BallisticPredictor.MaxHorizon) / EdgeTicks;
        float T = EdgeTicks * dt;
        float legReach = 2f * PlayerCharacter.Radius - 2f + 20f;

        var nodes = new List<Node>(256)
            { new() { Pos = pos0, Vel = vel0, Cost = 0f, Parent = -1 } };
        var frontier = new List<int>(64) { 0 };
        var next = new Dictionary<(int, int, int, int), int>(64);

        for (int layer = 0; layer < layers; layer++)
        {
            next.Clear();
            float tEnd = (layer + 1) * T;
            foreach (int ni in frontier)
            {
                var n = nodes[ni];
                // Regime → force box (the per-state channel table, prototype
                // form): near a floor legs act (strong up, tuck down, drive x);
                // in flight only air control. Unilateral x while dir is held.
                float floorY = AmbientCorrector.FloorEnvelope(chunks, template, n.Pos.X,
                    n.Pos.Y - 2f, n.Pos.Y + BallisticPredictor.SupportReach, out bool found);
                bool near = found && floorY - n.Pos.Y <= legReach;
                float fyMin = near ? -cfg.FoldLegForce : -cfg.FoldAirVerticalForce;
                float fyMax = near ? cfg.FoldTuckForce : cfg.FoldAirVerticalForce;
                float fxCap = near ? cfg.FoldDriveForce : cfg.FoldAirLateralForce;
                float fxMin = dir > 0 ? 0f : -fxCap;
                float fxMax = dir < 0 ? 0f : fxCap;
                if (dir == 0) { fxMin = fxMax = 0f; }

                // Targets: uniform samples of the reachable displacement box
                // around the ballistic coast point (endpoints included), plus
                // the free coast itself (f = 0 — always an edge). The WORLD
                // lattice is only the dedup key below, never the target
                // generator — otherwise a box smaller than a cell can straddle
                // the grid and strand the search (the first prototype's bug).
                Vector2 pc = n.Pos + n.Vel * T + gravity * (0.5f * T * T);
                for (int iy = -1; iy < FSamples; iy++)
                for (int ix = 0; ix < (iy < 0 ? 1 : FSamples); ix++)
                {
                    // iy = -1 is the appended coast edge.
                    Vector2 f = iy < 0
                        ? Vector2.Zero
                        : new Vector2(
                            fxMin + (fxMax - fxMin) * ix / (FSamples - 1),
                            fyMin + (fyMax - fyMin) * iy / (FSamples - 1));
                    Vector2 target = pc + f * (0.5f * T * T);
                    Vector2 vArr = n.Vel + (f + gravity) * T;

                    // Per-tick sweep: prune edges whose quadratic arc clips
                    // solid tiles (hard constraint, not a penalty).
                    bool blocked = false;
                    for (int s = 1; s <= EdgeTicks && !blocked; s++)
                    {
                        float ts = s * dt;
                        Vector2 ps = n.Pos + n.Vel * ts + (f + gravity) * (0.5f * ts * ts);
                        for (int b = 0; b < BodyProbe.Length && !blocked; b++)
                            blocked = TileQuery.IsSolidAt(chunks,
                                ps.X + BodyProbe[b].X, ps.Y + BodyProbe[b].Y);
                    }
                    if (blocked) continue;

                    float effort =
                        (f.Y < 0f ? WLegUp : WTuck) * f.Y * f.Y * T +
                        (near ? WDrive : WAir) * f.X * f.X * T;
                    float xRef = pos0.X + dir * targetSpeed * tEnd;
                    float fy2 = AmbientCorrector.FloorEnvelope(chunks, template, target.X,
                        target.Y - cfg.FoldClimbReachUp,
                        target.Y + BallisticPredictor.SupportReach, out bool f2);
                    float yRef = f2 ? fy2 - hoverOffset : target.Y;
                    float track = SoftTrack * ((target.X - xRef) * (target.X - xRef)
                                            + (target.Y - yRef) * (target.Y - yRef));
                    float c = nodes[ni].Cost + effort + track;

                    var key = ((int)MathF.Round(target.X / CellSize),
                               (int)MathF.Round(target.Y / CellSize),
                               (int)MathF.Floor(vArr.X / VBin),
                               (int)MathF.Floor(vArr.Y / VBin));
                    if (next.TryGetValue(key, out int prev) && nodes[prev].Cost <= c)
                        continue;
                    var node = new Node { Pos = target, Vel = vArr, Cost = c,
                                          Parent = ni, Force = f };
                    if (next.TryGetValue(key, out int slot)) nodes[slot] = node;
                    else { nodes.Add(node); next[key] = nodes.Count - 1; }
                }
            }
            frontier.Clear();
            frontier.AddRange(next.Values);
            // Beam prune: keep the BeamWidth best by cost (index tie-break for
            // determinism). Bounds work per layer; costs only optimality.
            if (frontier.Count > BeamWidth)
            {
                frontier.Sort((a, b) => nodes[a].Cost != nodes[b].Cost
                    ? nodes[a].Cost.CompareTo(nodes[b].Cost) : a.CompareTo(b));
                frontier.RemoveRange(BeamWidth, frontier.Count - BeamWidth);
            }
            LastDebug = layer == 0 ? $"{frontier.Count}" : $"{LastDebug},{frontier.Count}";
            if (frontier.Count == 0) return 0;   // fully blocked: the bonk case
        }

        int best = -1;
        foreach (int ni in frontier)
            if (best < 0 || nodes[ni].Cost < nodes[best].Cost) best = ni;
        cost = nodes[best].Cost;

        // Walk parents back, then emit per-tick samples along each edge's arc.
        Span<int> chain = stackalloc int[layers];
        int cur = best;
        for (int layer = layers - 1; layer >= 0; layer--)
        {
            chain[layer] = cur;
            cur = nodes[cur].Parent;
        }
        int count = 0;
        Vector2 p0 = pos0, v0 = vel0;
        for (int layer = 0; layer < layers && count + EdgeTicks <= outSamples.Length; layer++)
        {
            var n = nodes[chain[layer]];
            for (int s = 1; s <= EdgeTicks; s++)
            {
                float ts = s * dt;
                outSamples[count++] = new CoastSample
                {
                    Pos = p0 + v0 * ts + (n.Force + gravity) * (0.5f * ts * ts),
                    Vel = v0 + (n.Force + gravity) * ts,
                };
            }
            p0 = n.Pos; v0 = n.Vel;
        }
        return count;
    }
}
