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
    // Per-row multiplier on the solver's HingeWeight. Tile clearance rows are 1
    // (hard). The stand-fold's hover-support rows are soft (≪1) so obstacle rows
    // can overpower them — that yielding IS the duck/crest mechanic. Soft rows
    // are excluded from the refusal residual.
    public float   HingeScale;
    // Servable ONLY by plant channels (ChannelDef.PlantServes — the corner-
    // plant redirect). Ambient-mode wall-face rows carry this: the fold must
    // never dodge or brake for a wall (anti-autopilot), but a face row near a
    // CONVEX corner may recruit a hand-plant deflection (the cave-mouth
    // duck-in). With no plant tick in reach the row sits unserved in the
    // residual — the bonk stays honest.
    public bool    PlantOnly;
    // Source tile (gtx/gty) whose facet emitted this row — the cell at the
    // run's max-depth tick when consecutive ticks merged. Valid iff HasCell;
    // soft reference rows (hover/progress envelopes) have none. Bookkeeping
    // for CorrectorLedger's contact→tile attribution (block-breaking input);
    // never read by the solve.
    public int     CellX, CellY;
    public bool    HasCell;
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
    // Raised 4 → 32 for the stand fold: hover-support rows are per-tick (up to a
    // full horizon of them) and must not crowd out real obstacle rows.
    public const int   MaxEvents            = 32;
    public const float DefaultDeepViolation = Chunk.TileSize * 0.5f;   // px — half a tile reads as "impact", not "graze"

    // Facet-count ceiling for the C-obstacle template (hexagon body ⇒ ~8).
    public const int MaxFacets = 16;

    // A face (PlantOnly) row is worth a row-budget slot only if a plant tick
    // could serve it — otherwise it would sit unserved AND crowd out the
    // envelope's per-tick hover rows (the bumpy-corridor regression).
    private static bool NearPlantTick(bool[] plantTicks, int tick, int count)
    {
        if (plantTicks == null) return false;
        for (int k = Math.Max(0, tick - 2); k <= Math.Min(count - 1, tick + 2); k++)
            if (plantTicks[k]) return true;
        return false;
    }

    // Builds rows[0..return) from samples[0..count). truncatedAt == count when the
    // whole coast was scanned; otherwise the tick of the first deep violation.
    //
    // Geometry comes from the exact C-space obstacle template (CObstacle.cs):
    // per solid cell, the body center is tested against the template's facets
    // (tile ⊕ reflected body — axis faces PLUS the corner bevels, which is how
    // lips and corners are visible to planning as real geometry). The shallowest
    // EXPOSED facet is the cell's pop-out; exposure masks prune facets interior
    // to the C-space union (neighbor solidity).
    //
    // verticalFacesOnly is the AMBIENT-mode emission filter (the anti-autopilot
    // rule): only facets with a significant vertical component may emit rows —
    // pure wall faces emit NOTHING and the coast deep-truncates at the impact
    // (full-speed honest bonk). Corner bevels count as vertical-ish, so grazes
    // and lips stay actionable. Deep truncation keys on penetration regardless
    // of the filter, so a filtered wall impact still kills the scan.
    public static int Build(
        ChunkMap chunks, Polygon body,
        CoastSample[] samples, int count,
        float margin, float deepViolation,
        ClearanceRow[] rows, out int truncatedAt,
        bool verticalFacesOnly = false, bool[] plantTicks = null)
    {
        var template = CObstacleTemplate.For(body);
        var facets = template.Facets;
        int F = Math.Min(facets.Length, MaxFacets);
        float reach = template.Reach + margin;

        int rowCount = 0;
        truncatedAt = count;

        // Per-facet run state: depth/tick of the worst violation in the current
        // contiguous violated stretch; runActive marks a stretch in progress.
        // The run also carries the CELL that produced the worst violation, so
        // the emitted row can name its source tile (CorrectorLedger).
        Span<bool>  runActive = stackalloc bool[MaxFacets];
        Span<float> runDepth  = stackalloc float[MaxFacets];
        Span<int>   runTick   = stackalloc int[MaxFacets];
        Span<int>   runCellX  = stackalloc int[MaxFacets];
        Span<int>   runCellY  = stackalloc int[MaxFacets];
        Span<float> tickDepth = stackalloc float[MaxFacets];
        Span<int>   tickCellX = stackalloc int[MaxFacets];
        Span<int>   tickCellY = stackalloc int[MaxFacets];
        Span<float> cellDepth = stackalloc float[MaxFacets];

        int ts = Chunk.TileSize;
        float half = ts * 0.5f;

        for (int k = 0; k < count; k++)
        {
            var pos = samples[k].Pos;
            for (int f = 0; f < F; f++) tickDepth[f] = 0f;
            float tickPenetration = 0f;

            int cMin = (int)MathF.Floor((pos.X - reach) / ts), cMax = (int)MathF.Floor((pos.X + reach) / ts);
            int rMin = (int)MathF.Floor((pos.Y - reach) / ts), rMax = (int)MathF.Floor((pos.Y + reach) / ts);

            for (int gtx = cMin; gtx <= cMax; gtx++)
            for (int gty = rMin; gty <= rMax; gty++)
            {
                float cx = gtx * ts + half, cy = gty * ts + half;
                if (!TileQuery.IsSolidAt(chunks, cx, cy)) continue;

                // Inside the margin-inflated C-obstacle iff EVERY facet is
                // violated (convexity). Strict, so flush edges don't count.
                var rel = new Vector2(pos.X - cx, pos.Y - cy);
                bool inside = true;
                for (int f = 0; f < F; f++)
                {
                    cellDepth[f] = facets[f].Offset + margin - Vector2.Dot(rel, facets[f].Normal);
                    if (cellDepth[f] <= 0f) { inside = false; break; }
                }
                if (!inside) continue;

                int   bestFacet = -1;
                float rawBest   = float.MaxValue;   // min over ALL exposed facets

                for (int f = 0; f < F; f++)
                {
                    var fc = facets[f];
                    if (TileQuery.IsSolidAt(chunks, cx + fc.MaskDx1 * ts, cy + fc.MaskDy1 * ts)) continue;
                    if (fc.TwoMasks && TileQuery.IsSolidAt(chunks, cx + fc.MaskDx2 * ts, cy + fc.MaskDy2 * ts)) continue;

                    if (cellDepth[f] < rawBest) { rawBest = cellDepth[f]; bestFacet = f; }
                }
                if (rawBest != float.MaxValue && rawBest > tickPenetration)
                    tickPenetration = rawBest;

                // Ambient-mode wall faces: the TRUE nearest exit is kept (never
                // a preference among exits — steering to a deeper "climb over
                // the top" facet is how sideways wall hits used to read as
                // climb commands), but the row it produces is PlantOnly:
                // servable solely by the corner-plant redirect. A flat wall
                // with no convex corner in reach leaves the row unserved and
                // the coast bonks via deep truncation exactly as when these
                // rows were vetoed outright.

                // A fully-enclosed cell (no exposed facets) contributes nothing —
                // a shallower row exists at whatever surface cell the coast
                // crossed to get here.
                if (bestFacet >= 0 && tickDepth[bestFacet] < rawBest)
                {
                    tickDepth[bestFacet] = rawBest;
                    tickCellX[bestFacet] = gtx;
                    tickCellY[bestFacet] = gty;
                }
            }

            bool deep = tickPenetration >= deepViolation;
            for (int f = 0; f < F; f++)
            {
                if (tickDepth[f] > 0f)
                {
                    if (!runActive[f] || tickDepth[f] > runDepth[f])
                    {
                        runActive[f] = true; runDepth[f] = tickDepth[f]; runTick[f] = k;
                        runCellX[f] = tickCellX[f]; runCellY[f] = tickCellY[f];
                    }
                }
                else if (runActive[f])
                {
                    runActive[f] = false;
                    if (rowCount < MaxEvents)
                        {
                        bool faceRow = verticalFacesOnly && MathF.Abs(facets[f].Normal.Y) < 0.3f;
                        if (!faceRow || NearPlantTick(plantTicks, runTick[f], count))
                            rows[rowCount++] = new ClearanceRow { Tick = runTick[f], Normal = facets[f].Normal,
                                                                  Depth = runDepth[f], HingeScale = 1f, PlantOnly = faceRow,
                                                                  CellX = runCellX[f], CellY = runCellY[f], HasCell = true };
                    }
                }
            }

            if (deep)
            {
                truncatedAt = k;
                break;
            }
        }

        // Finalize runs still open at the horizon / truncation point.
        for (int f = 0; f < F; f++)
            if (runActive[f] && rowCount < MaxEvents)
                {
                        bool faceRow = verticalFacesOnly && MathF.Abs(facets[f].Normal.Y) < 0.3f;
                        if (!faceRow || NearPlantTick(plantTicks, runTick[f], count))
                            rows[rowCount++] = new ClearanceRow { Tick = runTick[f], Normal = facets[f].Normal,
                                                                  Depth = runDepth[f], HingeScale = 1f, PlantOnly = faceRow,
                                                                  CellX = runCellX[f], CellY = runCellY[f], HasCell = true };
                    }

        return rowCount;
    }
}
