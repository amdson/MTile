using System;
using Microsoft.Xna.Framework;

namespace MTile;

// The lattice fold engine (MovementConfig.FoldEngine "lattice" —
// Plans/LATTICE_PATH_PLANNER.md, phase 2). FoldReference with its hand-written
// rollout swapped for the lattice DP's path: the reference the body rides is
// the cheapest admissible polyline through the C-obstacle bitmap — climbs,
// ducks and give-ups are plain geometry there — time-parameterized at the
// fold's progress speed and handed to the SAME rows → deform → servo tail
// (FoldReference.Track). Nothing downstream knows which engine wrote
// s.Samples.
//
// Inherited verbatim from the ref engine (plan §4.6–4.7):
//   - the regime guards (knockback / launched / plunging / unanchored fall
//     back to the ballistic-qp flow);
//   - dir == 0 → the ref rollout's pure hover column (a direction-ordered
//     lattice has no order without a direction);
//   - progress along u carries at the fold's target speed, ramped by
//     WalkAccel from the body's current along-u speed;
//   - the reference DESCENDS no faster than gravity (a floor cannot pull).
// Deliberately NOT inherited: the rise-rate cap and the climb band. The
// path's climb IS the climb (§3.3: edges are geometry, no actuation gate);
// capping the reference's rise would drop it back into the block's
// C-obstacle and hand the climb to the row/deform mechanism this engine
// exists to replace. Deliverability is the servo's and the give-up's
// question (§4.3), not the reference's.
//
// Timing: progress is measured as the PROJECTION onto u (the ref's x-carry),
// not arc length along the polyline — arc-length pacing halves the carry
// speed on a 60° drop, which reads as the ledge grabbing the body. The
// §3.7 tracker (progress-along-tangent objective) replaces this scheduling
// altogether for jump states; for the fold states this v1 is the plan's
// "PathDeform + servo instance" unchanged.
public static class FoldLattice
{
    public static bool TryApply(EnvironmentContext ctx, CorrectorScratch s,
                                in FoldProfile fold, AmbientPolicy policy, int dir,
                                ref MovementVars vars)
    {
        if (!FoldReference.Admit(ctx, s, out var template, out float anchorY)) return false;
        int n = dir == 0
            ? FoldReference.Rollout(ctx, s, fold, dir, template, anchorY)
            : Rollout(ctx, s, fold, dir, template, anchorY);
        FoldReference.Track(ctx, s, fold, policy, n, ref vars);
        return true;
    }

    private static int Rollout(EnvironmentContext ctx, CorrectorScratch s,
                               in FoldProfile fold, int dir,
                               CObstacleTemplate template, float anchorY)
    {
        var cfg = MovementConfig.Current;
        var body = ctx.Body;
        float dt = ctx.Dt;
        int n = Math.Min(cfg.AmbientHorizon, BallisticPredictor.MaxHorizon);
        var u = new Vector2(dir, 0f);

        var path = s.LatticePath;
        int count = s.Lattice.Solve(ctx.Chunks, body.Polygon, body.Position, body.Velocity,
            u, hover: true, fold.HoverOffset, path, out _, out _);
        // No path: the body is pinned inside an obstacle's margin with no free
        // cell beside or ahead of it (flush against a wall). The ref rollout's
        // raw carry is the honest bonk — rows classify the wall exactly as
        // they do for the ref engine.
        if (count == 0) return FoldReference.Rollout(ctx, s, fold, dir, template, anchorY);

        // The polyline is monotone in p = dot(pos, u) (every DP edge strictly
        // increases p), so y is a function of p along it. The first path node
        // is the (possibly snapped) seed cell's center — within a cell of the
        // body, and possibly a hair behind it; skip anything not ahead so the
        // prefix segment body → path[first] never runs backward.
        float pBody = Vector2.Dot(body.Position, u);
        int first = 0;
        while (first < count && Vector2.Dot(path[first].Pos, u) <= pBody + 1e-3f) first++;

        float target = fold.MaxSpeed * ctx.Modifiers.MaxWalkSpeed;
        float v = Vector2.Dot(body.Velocity, u);      // signed along-u progress speed
        float vyFall = MathF.Max(0f, body.Velocity.Y); // descent budget (ref rule)
        float p = pBody;
        Vector2 prev = body.Position;
        int seg = first;   // segment: (seg == first ? body : path[seg-1]) → path[seg]
        for (int k = 0; k < n; k++)
        {
            v += Math.Clamp(target - v, -cfg.WalkAccel * dt, cfg.WalkAccel * dt);
            p += v * dt;

            Vector2 pos; int node;
            if (p <= pBody || first >= count)
            {
                // Behind the body (decelerating from reverse) or nothing ahead:
                // straight carry along u from the body.
                pos = body.Position + u * (p - pBody);
                node = Math.Min(first, count - 1);
            }
            else
            {
                while (seg < count - 1 && Vector2.Dot(path[seg].Pos, u) < p) seg++;
                Vector2 a = seg == first ? body.Position : path[seg - 1].Pos;
                Vector2 b = path[seg].Pos;
                float pa = Vector2.Dot(a, u), pb = Vector2.Dot(b, u);
                if (p >= pb)
                {
                    pos = b + u * (p - pb);              // past the end: honest carry
                    node = seg;
                }
                else
                {
                    float t = (p - pa) / MathF.Max(pb - pa, 1e-6f);
                    pos = a + (b - a) * t;
                    node = (seg == first || t >= 0.5f) ? seg : seg - 1;
                }
            }

            // A floor cannot pull: descend no faster than gravity would.
            float yMax = prev.Y + (vyFall + ctx.Gravity.Y * dt) * dt;
            if (pos.Y > yMax) { pos.Y = yMax; vyFall += ctx.Gravity.Y * dt; }
            else vyFall = 0f;

            ref readonly var nd = ref path[node];
            s.Samples[k].Pos      = pos;
            s.Samples[k].Vel      = (pos - prev) / dt;
            s.Samples[k].Grounded = nd.Grounded;
            s.Samples[k].FloorY   = nd.FloorY;
            s.CoastVel[k] = s.Samples[k].Vel;
            prev = pos;
        }
        return n;
    }
}
