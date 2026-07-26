using System;
using Microsoft.Xna.Framework;

namespace MTile;

// One clearance requirement for the CorrectionSolver (BALLISTIC_CORRECTOR_PLAN §3/§4):
// by predicted tick Tick, the body must have moved Depth px along Normal (outward,
// away from the violated tile face) relative to the coast, or it clips terrain.
public struct ClearanceRow
{
    public int     Tick;    // predicted tick index the requirement anchors to
    public Vector2 Normal;  // outward clearance normal (axis-aligned: a tile face)
    public float   Depth;   // required outward displacement (px) — margin-inflated
}

// Sweeps the margin-inflated body along a predicted coast polyline directly against
// tiles and emits up to MaxEvents clearance rows. Relevance is defined by the
// prediction, not a scan pattern — anything the coast touches is a candidate,
// nothing else is (lifts the corridor probe's x-monotone limitation).
//
// Per sample: solid cells in the inflated AABB's neighborhood; per cell the
// EXPOSED faces (neighbor solidity — purely local) whose margin-inflated support
// plane the body crosses; per cell the shallowest violated face wins (pop out the
// nearest face — same philosophy as ResolveChunkCollisions' deepest-first push-out,
// and the deterministic version of "greedy first-hit side" for free-standing
// blocks). Depths are support-plane based: slightly conservative at the hexagon's
// cut corners vs exact SAT, which errs on the clearing side — acceptable, the
// margin exists for the same reason.
//
// Consecutive violated ticks against the same normal are ONE event: the row
// anchors at the tick of maximum depth with that depth (clearing the worst tick
// clears the run for a smooth deflection). A violation deeper than deepViolation
// truncates the scan — samples past an unavoided impact are meaningless, their
// rows are moot (the caller sees truncatedAt < count and treats the tail as dead).
//
// Determinism: fixed iteration order (ticks → cells column-major → faces in
// Up/Down/Left/Right order), no statics, no allocation beyond one bounding-box
// derivation per call (hoisted into pooled scratch when wired onto PlayerCharacter).
public static class ClearanceConstraintBuilder
{
    public const int   MaxEvents            = 4;
    public const float DefaultDeepViolation = 8f;   // px — half a tile reads as "impact", not "graze"

    // Axis normal slots, fixed order: 0 = up (0,-1), 1 = down (0,1), 2 = left (-1,0), 3 = right (1,0).
    private static readonly Vector2[] AxisNormals =
    {
        new(0f, -1f), new(0f, 1f), new(-1f, 0f), new(1f, 0f),
    };

    // Builds rows[0..return) from samples[0..count). truncatedAt == count when the
    // whole coast was scanned; otherwise the tick of the first deep violation.
    public static int Build(
        ChunkMap chunks, Polygon body,
        CoastSample[] samples, int count,
        float margin, float deepViolation,
        ClearanceRow[] rows, out int truncatedAt)
    {
        // Body support extents along the four axis directions (the AABB of the
        // local polygon — exact supports for axis normals).
        var bb = body.GetBoundingBox(Vector2.Zero);

        int rowCount = 0;
        truncatedAt = count;

        // Per-normal run state: depth/tick of the worst violation in the current
        // contiguous violated stretch; runActive marks a stretch in progress.
        Span<bool>  runActive = stackalloc bool[4];
        Span<float> runDepth  = stackalloc float[4];
        Span<int>   runTick   = stackalloc int[4];

        int ts = Chunk.TileSize;

        for (int k = 0; k < count; k++)
        {
            var pos = samples[k].Pos;
            float infL = pos.X + bb.Left  - margin;
            float infR = pos.X + bb.Right + margin;
            float infT = pos.Y + bb.Top    - margin;
            float infB = pos.Y + bb.Bottom + margin;

            // This tick's violation per axis normal (max depth across cells).
            Span<float> tickDepth = stackalloc float[4] { 0f, 0f, 0f, 0f };

            int cMin = (int)MathF.Floor(infL / ts), cMax = (int)MathF.Floor(infR / ts);
            int rMin = (int)MathF.Floor(infT / ts), rMax = (int)MathF.Floor(infB / ts);

            for (int gtx = cMin; gtx <= cMax; gtx++)
            for (int gty = rMin; gty <= rMax; gty++)
            {
                float cx = gtx * ts + ts * 0.5f, cy = gty * ts + ts * 0.5f;
                if (!TileQuery.IsSolidAt(chunks, cx, cy)) continue;

                float x0 = gtx * ts, x1 = x0 + ts;
                float y0 = gty * ts, y1 = y0 + ts;

                // The inflated body must actually overlap the cell (strict, so
                // flush edges don't count). Once it does, all four support-plane
                // depths are positive; the EXPOSED face with the smallest depth
                // is the pop-out face for this cell.
                if (!(infR > x0 && infL < x1 && infB > y0 && infT < y1)) continue;

                int   bestAxis  = -1;
                float bestDepth = float.MaxValue;

                // Up (tile top face, push body up = (0,-1))
                if (!TileQuery.IsSolidAt(chunks, cx, cy - ts))
                {
                    float m = infB - y0;
                    if (m < bestDepth) { bestDepth = m; bestAxis = 0; }
                }
                // Down (tile bottom face, push body down = (0,1))
                if (!TileQuery.IsSolidAt(chunks, cx, cy + ts))
                {
                    float m = y1 - infT;
                    if (m < bestDepth) { bestDepth = m; bestAxis = 1; }
                }
                // Left (tile left face, push body left = (-1,0))
                if (!TileQuery.IsSolidAt(chunks, cx - ts, cy))
                {
                    float m = infR - x0;
                    if (m < bestDepth) { bestDepth = m; bestAxis = 2; }
                }
                // Right (tile right face, push body right = (1,0))
                if (!TileQuery.IsSolidAt(chunks, cx + ts, cy))
                {
                    float m = x1 - infL;
                    if (m < bestDepth) { bestDepth = m; bestAxis = 3; }
                }

                // A fully-enclosed cell (no exposed faces) contributes nothing —
                // a shallower row exists at whatever surface cell the coast
                // crossed to get here.
                if (bestAxis >= 0 && tickDepth[bestAxis] < bestDepth)
                    tickDepth[bestAxis] = bestDepth;
            }

            bool deep = false;
            for (int a = 0; a < 4; a++)
            {
                if (tickDepth[a] > 0f)
                {
                    if (!runActive[a]) { runActive[a] = true; runDepth[a] = tickDepth[a]; runTick[a] = k; }
                    else if (tickDepth[a] > runDepth[a]) { runDepth[a] = tickDepth[a]; runTick[a] = k; }
                    if (tickDepth[a] >= deepViolation) deep = true;
                }
                else if (runActive[a])
                {
                    runActive[a] = false;
                    if (rowCount < MaxEvents)
                        rows[rowCount++] = new ClearanceRow { Tick = runTick[a], Normal = AxisNormals[a], Depth = runDepth[a] };
                }
            }

            if (deep)
            {
                truncatedAt = k;
                break;
            }
        }

        // Finalize runs still open at the horizon / truncation point.
        for (int a = 0; a < 4; a++)
            if (runActive[a] && rowCount < MaxEvents)
                rows[rowCount++] = new ClearanceRow { Tick = runTick[a], Normal = AxisNormals[a], Depth = runDepth[a] };

        return rowCount;
    }
}
