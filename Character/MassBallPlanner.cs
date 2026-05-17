using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Mass-ball alternative to the priority-field planner. Simulates a mass-laden
// ball pulled along the recorded sweep path by a virtual "puller" point via a
// spring. As the ball moves, mass leaks into the field cell under it. When a
// cell crosses Threshold mass, a sprout fires there; further mass landing on a
// spawned cell or on already-solid terrain spills equally to the 4 neighbors.
// Recursive spilling terminates by an amount-cutoff and a depth cap.
//
// Compared to the priority-field planner, this is noisier and more emergent —
// shape arises from the dynamics rather than being read out of a scoring
// function. The ball's inertia means a fast sweep flings mass past the original
// path, and recursive spills create chained tendrils when the ball drains while
// stationary. Trade: less predictable count + shape, more "alive" feel.
//
// Toggle between this and the priority-field planner at runtime via the 'P'
// key in Game1 (EruptionPlanner.CurrentMode).
public static class MassBallPlanner
{
    // Sim integration step. ~60Hz; ~50-200 steps over a typical gesture.
    private const float DtPerStep         = 1f / 60f;
    private const int   MaxSteps          = 240;
    // Puller advances by this many path-sample indices per sim step. <1 keeps
    // the puller a bit slower than the original cursor so the ball doesn't
    // overshoot the end of the recorded path before its mass is spent.
    private const float PullerStepRate    = 0.6f;
    // Spring + damping between ball and puller. Stiffness controls how tightly
    // the ball tracks; damping controls overshoot. Underdamped = the ball wobbles
    // around the puller and the deposit pattern wobbles with it (a feature).
    private const float SpringStiffness   = 60f;
    private const float SpringDamping     = 12f;
    // Per-step leak — fraction of remaining mass. Scaled by ball speed so a
    // stationary ball still drains slowly (SpeedScaleMin floor) but a fast ball
    // dumps more per step.
    private const float LeakFractionBase  = 0.05f;
    private const float SpeedRef          = 100f;
    private const float SpeedScaleMin     = 0.3f;
    private const float SpeedScaleMax     = 2.0f;
    // Mass needed to spawn one tile. Total spawned ≈ budget / Threshold, modulo
    // mass lost to terrain-discard and spill-cutoffs.
    private const float Threshold         = 1f;
    // Recursion guards on the spill cascade.
    private const float EpsAmount         = 0.001f;
    private const int   MaxSpillDepth     = 8;

    public static readonly TileType DefaultType = TileType.Dirt;

    public static void Plan(ChunkMap chunks, Vector2 origin, IReadOnlyList<PathSample> samples, int budget)
    {
        if (budget <= 0 || samples == null || samples.Count == 0) return;

        Vector2 ballPos = origin;
        Vector2 ballVel = Vector2.Zero;
        float   mass    = budget;

        var field         = new Dictionary<(int, int), float>();
        var sproutedSet   = new HashSet<(int, int)>();
        var sproutedOrder = new List<(int, int)>();

        float pullerSampleFloat = 0f;
        int   lastSample        = samples.Count - 1;

        for (int step = 0; step < MaxSteps; step++)
        {
            if (mass < EpsAmount) break;

            // Advance puller along the polyline. Past the recorded end, linearly
            // extrapolate along the last sample's velocity — a brief sweep keeps
            // the puller drifting in the swept direction so mass deposits along a
            // line rather than piling up at the end cell. Near-stationary releases
            // (last velocity ≈ 0) naturally degenerate to "puller parked at end".
            pullerSampleFloat += PullerStepRate;
            Vector2 puller;
            if (pullerSampleFloat <= lastSample)
            {
                int   i0   = (int)pullerSampleFloat;
                int   i1   = Math.Min(i0 + 1, lastSample);
                float frac = i1 == i0 ? 0f : pullerSampleFloat - i0;
                puller = Vector2.Lerp(samples[i0].Position, samples[i1].Position, frac);
            }
            else
            {
                // Convert overshoot (in path-sample indices) to seconds: samples are
                // recorded at 60Hz, so 1 index = DtPerStep / PullerStepRate seconds.
                float overshoot       = pullerSampleFloat - lastSample;
                float secondsPerIndex = DtPerStep / PullerStepRate;
                puller = samples[lastSample].Position + samples[lastSample].Velocity * (overshoot * secondsPerIndex);
            }

            // Spring force toward puller, with velocity damping.
            Vector2 disp  = puller - ballPos;
            Vector2 force = disp * SpringStiffness - ballVel * SpringDamping;
            ballVel += force * DtPerStep;
            ballPos += ballVel * DtPerStep;

            // Leak — fraction of remaining mass, scaled by ball speed.
            float speed = ballVel.Length();
            float scale = MathHelper.Clamp(speed / SpeedRef, SpeedScaleMin, SpeedScaleMax);
            float leak  = MathF.Min(mass, mass * LeakFractionBase * scale);
            mass -= leak;

            int gtx = (int)MathF.Floor(ballPos.X / Chunk.TileSize);
            int gty = (int)MathF.Floor(ballPos.Y / Chunk.TileSize);

            Deposit(chunks, gtx, gty, leak, 0, field, sproutedSet, sproutedOrder);

            // No early-break on "puller at end + ball at rest" — when the player
            // barely moves the mouse, the ball settles within ~10 steps and we'd
            // exit with most of the budget unspent. Letting the loop run until
            // mass is depleted (or MaxSteps caps it) means a stationary release
            // dumps its full budget into the rest cell, and the spill cascade
            // radiates that mass outward through neighbors — same total block
            // count as a swept gesture, just clustered tighter.
        }

        // Spawn in the order cells crossed the threshold during the sim. The
        // sprout-graph still handles parent-dependency ordering — we just submit.
        foreach (var (gtx, gty) in sproutedOrder)
            chunks.TryRequestTile(gtx, gty, DefaultType);
    }

    private static void Deposit(
        ChunkMap chunks, int gtx, int gty, float amount, int depth,
        Dictionary<(int, int), float> field,
        HashSet<(int, int)> sproutedSet,
        List<(int, int)> sproutedOrder)
    {
        if (amount < EpsAmount) return;
        if (depth > MaxSpillDepth) return;

        // Discard mass dropped onto already-solid terrain (matches the user's
        // "discard for already-solid cells" instruction from the priority-field
        // version).
        if (chunks.GetCellState(gtx, gty) == TileState.Solid) return;

        var key = (gtx, gty);
        if (sproutedSet.Contains(key))
        {
            // Already filled this run — spill equally to the 4 neighbors.
            float share = amount * 0.25f;
            Deposit(chunks, gtx + 1, gty,     share, depth + 1, field, sproutedSet, sproutedOrder);
            Deposit(chunks, gtx - 1, gty,     share, depth + 1, field, sproutedSet, sproutedOrder);
            Deposit(chunks, gtx,     gty + 1, share, depth + 1, field, sproutedSet, sproutedOrder);
            Deposit(chunks, gtx,     gty - 1, share, depth + 1, field, sproutedSet, sproutedOrder);
            return;
        }

        field.TryGetValue(key, out float cur);
        cur += amount;
        if (cur < Threshold)
        {
            field[key] = cur;
            return;
        }

        // Crossed the threshold — mark, queue, spill excess.
        sproutedSet.Add(key);
        sproutedOrder.Add(key);
        field[key] = Threshold;
        float excess = cur - Threshold;
        if (excess > EpsAmount)
        {
            float share = excess * 0.25f;
            Deposit(chunks, gtx + 1, gty,     share, depth + 1, field, sproutedSet, sproutedOrder);
            Deposit(chunks, gtx - 1, gty,     share, depth + 1, field, sproutedSet, sproutedOrder);
            Deposit(chunks, gtx,     gty + 1, share, depth + 1, field, sproutedSet, sproutedOrder);
            Deposit(chunks, gtx,     gty - 1, share, depth + 1, field, sproutedSet, sproutedOrder);
        }
    }
}
