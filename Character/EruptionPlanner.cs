using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// One sample on the sweep path captured during BlockEruption.Update.
public readonly struct PathSample
{
    public readonly Vector2 Position;
    public readonly Vector2 Velocity;
    public PathSample(Vector2 p, Vector2 v) { Position = p; Velocity = v; }
}

// Selects between the priority-field placement and the mass-ball simulation.
// Toggle at runtime via the 'P' key in Game1 — both planners share the
// (origin, samples, budget) signature so swapping is free.
public enum EruptionPlannerMode { PriorityField, MassBall }

// Priority-field block placement. Given a path of pen samples and a charge-time
// budget, scores nearby empty cells and spawns sprouts in the top-K. Each
// sample contributes:
//   weight       — falls off with progress along the path (front-loaded burst)
//   radius       — varies inversely with local pen speed (slow=wide, fast=narrow)
//   falloff      — quadratic from the sample center
//
// Solid cells are skipped entirely so their share of the budget is discarded
// (which is what the user wants — the eruption energy is "absorbed" by terrain).
public static class EruptionPlanner
{
    // Runtime planner-mode selector. BlockEruptionAction.Exit calls Plan(),
    // which dispatches to the chosen implementation. Defaults to PriorityField.
    public static EruptionPlannerMode CurrentMode = EruptionPlannerMode.MassBall;

    public static void Plan(ChunkMap chunks, Vector2 origin, IReadOnlyList<PathSample> samples, int budget)
    {
        if (CurrentMode == EruptionPlannerMode.MassBall)
            MassBallPlanner.Plan(chunks, origin, samples, budget);
        else
            PlanPriorityField(chunks, origin, samples, budget);
    }

    // Per-sample base radius in pixels. Tuned so a stationary pen covers ~3 tiles
    // each direction, giving a wide mound; a fast pen shrinks to ~1-1.5 tiles.
    private const float BaseRadius      = 48f;
    private const float MinRadius       = 16f;
    private const float MaxRadius       = 80f;
    // Velocity at which radius is exactly BaseRadius. Slower → wider, faster → narrower.
    private const float RefSpeed        = 120f;
    private const float MinSpeed        = 20f;   // floor so a near-stationary sample doesn't blow radius up

    // Default tile type for eruption-spawned blocks. Sprout-spawned tiles default
    // to Stone in the existing code path; for the eruption move we override to Dirt.
    public static readonly TileType DefaultType = TileType.Dirt;

    private static void PlanPriorityField(ChunkMap chunks, Vector2 origin, IReadOnlyList<PathSample> samples, int budget)
    {
        if (budget <= 0 || samples == null || samples.Count == 0) return;

        // Pass 1 — accumulate scores into empty cells reachable from any sample.
        var scores = new Dictionary<(int, int), float>();

        for (int i = 0; i < samples.Count; i++)
        {
            // Front-loaded weight: (1 - progress)^2. First sample gets weight 1,
            // last gets weight ~0. Sum is roughly N/3 — normalized away when we
            // sort and top-K, but the shape matters.
            float t      = samples.Count <= 1 ? 0f : (float)i / (samples.Count - 1);
            float weight = (1f - t) * (1f - t);
            if (weight < 1e-4f) continue;

            var p     = samples[i].Position;
            var v     = samples[i].Velocity;
            float spd = MathF.Max(v.Length(), MinSpeed);
            // Area-conserving radius — slow pen → wide deposit, fast pen → narrow.
            float radius = BaseRadius * MathF.Sqrt(RefSpeed / spd);
            radius = MathHelper.Clamp(radius, MinRadius, MaxRadius);
            float r2 = radius * radius;

            int rCells = (int)MathF.Ceiling(radius / Chunk.TileSize);
            int cx     = (int)MathF.Floor(p.X / Chunk.TileSize);
            int cy     = (int)MathF.Floor(p.Y / Chunk.TileSize);

            for (int dy = -rCells; dy <= rCells; dy++)
            for (int dx = -rCells; dx <= rCells; dx++)
            {
                int gtx = cx + dx;
                int gty = cy + dy;
                // Discard solid cells (and sprouting/pending ones) immediately —
                // their share of the budget is absorbed by existing terrain.
                if (chunks.GetCellState(gtx, gty) != TileState.Empty) continue;

                float cellCx = gtx * Chunk.TileSize + Chunk.TileSize * 0.5f;
                float cellCy = gty * Chunk.TileSize + Chunk.TileSize * 0.5f;
                float ddx    = cellCx - p.X;
                float ddy    = cellCy - p.Y;
                float dist2  = ddx * ddx + ddy * ddy;
                if (dist2 > r2) continue;

                float distNorm = MathF.Sqrt(dist2) / radius;     // 0..1
                float falloff  = (1f - distNorm) * (1f - distNorm);

                var key = (gtx, gty);
                scores.TryGetValue(key, out float s);
                scores[key] = s + weight * falloff;
            }
        }

        if (scores.Count == 0) return;

        // Pass 2 — pick top-K by score.
        var ranked = new List<KeyValuePair<(int, int), float>>(scores);
        ranked.Sort((a, b) => b.Value.CompareTo(a.Value));
        int spawnCount = Math.Min(budget, ranked.Count);

        // Pass 3 — spawn in distance-from-origin order so the visible wavefront
        // expands outward (closer cells finalize first; farther ones wait as
        // Pending in the sprout graph). The sprout graph handles parent
        // dependencies; we just submit the requests.
        var picks = new List<(int, int)>(spawnCount);
        for (int i = 0; i < spawnCount; i++) picks.Add(ranked[i].Key);
        picks.Sort((a, b) =>
        {
            float ax = a.Item1 * Chunk.TileSize + Chunk.TileSize * 0.5f;
            float ay = a.Item2 * Chunk.TileSize + Chunk.TileSize * 0.5f;
            float bx = b.Item1 * Chunk.TileSize + Chunk.TileSize * 0.5f;
            float by = b.Item2 * Chunk.TileSize + Chunk.TileSize * 0.5f;
            float da = (ax - origin.X) * (ax - origin.X) + (ay - origin.Y) * (ay - origin.Y);
            float db = (bx - origin.X) * (bx - origin.X) + (by - origin.Y) * (by - origin.Y);
            return da.CompareTo(db);
        });

        foreach (var (gtx, gty) in picks)
        {
            chunks.TryRequestTile(gtx, gty, DefaultType);
        }
    }
}
