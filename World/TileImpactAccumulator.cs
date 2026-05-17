using System;
using System.Collections.Generic;

namespace MTile;

// Per-cell running sum of impact impulse, with exponential decay between adds.
// Built to solve the "spring padding" problem: a body landing into a spring-
// constrained surface (e.g. the player's standing FSD) can have its impulse
// spread across several frames as the spring catches the velocity. With a
// per-frame threshold check, each frame's contribution sits below threshold
// and no damage fires. The accumulator collects them, fires damage when the
// running sum crosses threshold, and decays so unrelated small impulses
// (walking, tiny bumps) don't accrete forever.
//
// Lifecycle:
//   AccrueAndConsume — add impulse from one contact event; returns the over-
//                      threshold portion so the caller can compute damage.
//                      Threshold is subtracted from the stored sum on fire.
//   Tick             — exponential decay each frame; prunes near-zero entries.
public sealed class TileImpactAccumulator
{
    // Decay rate (1/sec) — exp(-Decay·dt) per frame. Tuned for a half-life of
    // ~0.23s so a spring-cushioned landing's worth of frames still adds up,
    // but stray small impulses from normal play bleed off quickly.
    private const float Decay = 0.1f;
    // Below this, an entry is removed entirely to keep the dictionary bounded.
    private const float PruneEps = 0.5f;

    private readonly Dictionary<(int gtx, int gty), float> _accum = new();
    private readonly List<(int gtx, int gty)> _scratchPrune = new();

    // Add impulse to (gtx, gty); if the running total crossed `threshold`,
    // returns the over-threshold portion AND subtracts the threshold from the
    // stored value. Otherwise returns 0 and stores the new total.
    public float AccrueAndConsume(int gtx, int gty, float impulse, float threshold)
    {
        if (impulse <= 0f) return 0f;
        var key = (gtx, gty);
        _accum.TryGetValue(key, out float cur);
        cur += impulse;
        if (cur >= threshold)
        {
            float over = cur - threshold;
            // Keep the remainder so a sustained heavy impact (something pressed
            // hard into a wall) keeps firing per frame rather than waiting a
            // full threshold's worth between hits.
            _accum[key] = 0f;
            return over;
        }
        _accum[key] = cur;
        return 0f;
    }

    public void Tick(float dt)
    {
        if (_accum.Count == 0 || dt <= 0f) return;
        float k = MathF.Exp(-Decay * dt);
        _scratchPrune.Clear();
        // Two-pass: iterate to compute new values, mutate after to avoid
        // modifying-during-enumeration. Reusing _scratchPrune avoids per-tick
        // allocation; impact events are common enough during action to matter.
        foreach (var kv in _accum)
        {
            float v = kv.Value * k;
            if (v < PruneEps) _scratchPrune.Add(kv.Key);
            else _accum[kv.Key] = v;
        }
        foreach (var key in _scratchPrune) _accum.Remove(key);
    }
}
