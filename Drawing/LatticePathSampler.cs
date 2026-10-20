using System;
using Microsoft.Xna.Framework;

namespace MTile;

// PREDICTION ADAPTER for the step planner (Plans/ANIMATION_STEP_PLANNER_IMPL.md §4):
// turns the lattice engine's already-solved short-horizon path (CorrectorScratch
// .LatticePath, freshness-stamped by LatticeTracker) into the `PredictAt(dtAhead)`
// delegate CharacterAnimSample carries. Render-only read of sim scratch — the same
// one-way direction as reading body position; nothing flows back.
//
// The lattice path is a POLYLINE of planned positions, not a time series, so the
// sample walks it by arc length at the body's current speed. When the path is stale
// (a maneuver state owns the trajectory, hitstun, the engine off) or trivial, it
// falls back to velocity extrapolation — deliberately NOT BallisticPredictor
// (owner's call, 2026-09-05).
//
// One instance per animator, rebound to its player each frame, so the delegate is
// allocated once instead of per frame.
public sealed class LatticePathSampler
{
    private const int   FreshFrames = 3;    // path older than this many sim frames = stale
    private const float MinWalkSpeed = 30f; // arc-length speed floor (px/s) so a momentary
                                            // stop doesn't freeze the prediction at the body

    private PlayerCharacter _p;
    public readonly Func<float, Vector2> PredictAt;

    public LatticePathSampler() => PredictAt = Sample;

    public void Bind(PlayerCharacter p) => _p = p;

    private Vector2 Sample(float dtAhead)
    {
        var p = _p;
        if (p == null) return default;
        Vector2 pos = p.Body.Position, vel = p.Body.Velocity;
        var s = p.CorrectorDebug;
        int count = s?.LatticePathCount ?? 0;
        if (s == null || count < 2 || p.Frame - s.LatticePathFrame > FreshFrames)
            return pos + vel * dtAhead;

        // The body's nearest point ON the path (closest segment projection), then advance by
        // speed·dtAhead of arc length from there. Starting at the nearest NODE instead walked
        // back to it whenever it lay behind the body, spending up to twice that gap of the
        // budget — a short prediction that pulsed every time the body passed a node.
        var path = s.LatticePath;
        int j = 1; Vector2 at = path[0].Pos; float bestD = float.MaxValue;
        for (int i = 1; i < count; i++)
        {
            Vector2 a = path[i - 1].Pos, ab = path[i].Pos - a;
            float len2 = ab.LengthSquared();
            float t = len2 > 1e-6f ? MathHelper.Clamp(Vector2.Dot(pos - a, ab) / len2, 0f, 1f) : 0f;
            Vector2 q = a + ab * t;
            float d = Vector2.DistanceSquared(q, pos);
            if (d < bestD) { bestD = d; j = i; at = q; }
        }
        float remaining = MathF.Max(vel.Length(), MinWalkSpeed) * MathF.Max(dtAhead, 0f);
        for (int i = j; i < count && remaining > 0f; i++)
        {
            Vector2 next = path[i].Pos;
            float seg = Vector2.Distance(at, next);
            if (seg <= 1e-3f) { at = next; continue; }
            if (seg >= remaining) return at + (next - at) * (remaining / seg);
            at = next; remaining -= seg;
        }
        return at;   // path exhausted — clamp to its end rather than inventing terrain-blind travel
    }
}
