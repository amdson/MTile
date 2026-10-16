using System;
using Microsoft.Xna.Framework;

namespace MTile;

// The path-following core shared by every state that tracks a GEOMETRIC path
// with capped channels (Plans/LATTICE_PATH_PLANNER.md §3.7). Extracted from
// LatticeTracker so authored maneuvers (LedgePull's clip, ...) follow their
// paths the same way the lattice's walk follows its plan:
//
//   nominal    the exact free rollout — v += (F_baseline + g)·dt, p += v·dt;
//              channel forces enter linearly, nothing is predicted;
//   reference  a sliding BEAD per tick: the nearest point on the polyline to
//              the iterate's tick-T position, monotone along the path — where
//              along the path the body is, is the solve's own output (path
//              following, not trajectory tracking: there is no clock, so the
//              body keeps whatever speed it arrives with);
//   band       hard rows per tick: within Band of the bead PERPENDICULAR to
//              the path — progress along it is free;
//   progress   one soft row at the last tick along the chord body → last bead,
//              whose depth is what the channels could add at their caps —
//              "as fast as the channels allow", no target speed;
//   channels   the caller's (masks + caps); pace lives in their caps
//              (velocity-faded, the LegServo idiom), never in a target speed.
//
// The caller fills the free rollout (FreeRollout), its held-state samples
// (s.Samples — the masks' input), the polyline (BeginPolyline/AppendVertex)
// and the wall rows, then calls Solve with a policy that supplies the parts
// that differ per state; ApplyTick0 applies the result.
public interface IPathTrackPolicy
{
    // Rows after the per-tick band rows (e.g. an x-only speed cap). Return
    // the new row count.
    int AddRows(CorrectorScratch s, int H, float dt, int pass, int rowCount);
    // Progress-row authority along t̂ (the channels' unfaded caps projected on
    // it), from the held state in s.Samples. False: no progress row this solve.
    bool ProgressAuthority(CorrectorScratch s, Vector2 tAlong, out float capAlong);
    // Fill s.Problem.Channels (and their masks/caps); return the count.
    int BuildChannels(CorrectorScratch s, int H, int rowCount);
}

public struct PathTrackSettings
{
    public int     H;
    public float   Dt;
    public Vector2 Origin;          // body position (the polyline's vertex 0)
    public int     VertexCount;     // polyline vertices in s.BeadVerts (< 2: no path)
    public float   Band;            // perpendicular following tolerance
    public float   ArcSpeedCap;     // hard cap on along-path speed; ∞ = none
    public float   HoverLineY;      // no path: band rows on y around this line; NaN = none
    public Vector2 FallbackTangent; // progress direction when there is no path
    public int     WallRows;        // rows already in s.Rows (terrain clearance)
    public int     Iterations;      // CorrectionSolver inner iterations
}

public static class PathTracker
{
    // 5 ticks: stopping times are 1–4 ticks (legs stop a 100 px/s descent in
    // one; tuck + gravity stop a rise in ~3), so the QP sees the deceleration
    // it will need.
    public  const int   Horizon            = 5;
    public  const int   BeadPasses         = 3;      // project → rows → solve, alternated
    private const int   ExactSweepCount    = 3;      // exact coordinate sweeps after the gradient solve
    public  const float HingeWeight        = 1e6f;   // the solver's stiffness constant
    public  const float ProgressHingeScale = 0.02f;

    // Half a lattice cell: the following tolerance every path tracker uses —
    // the path's resolution, not a clearance (the tiles are the wall rows).
    public static float FollowBand(MovementConfig cfg)
        => 0.5f * (float)Chunk.TileSize / Math.Clamp(cfg.LatticeCellsPerTile, 2, 8);

    // ── Nominal: the exact free rollout ──────────────────────────────────
    // p̄_T into s.DeliverySamples, v̄_T into s.CoastVel. The baseline is
    // whatever the state has already put in body.AppliedForce.
    public static void FreeRollout(CorrectorScratch s, PhysicsBody body, Vector2 gravity, int H, float dt)
    {
        Vector2 aFree = body.AppliedForce + gravity;
        Vector2 v = body.Velocity, pos = body.Position;
        for (int k = 0; k < H; k++)
        {
            v += aFree * dt; pos += v * dt;
            s.DeliverySamples[k].Pos = pos;
            s.CoastVel[k] = v;
        }
    }

    // Held-state samples (the masks' and caps' input): the body's current
    // state for every tick, as the tracker freezes it per solve.
    public static void HoldSamples(CorrectorScratch s, PhysicsBody body, int H, float floorY, bool grounded)
    {
        for (int k = 0; k < H; k++)
        {
            s.Samples[k].Pos      = body.Position;
            s.Samples[k].Vel      = body.Velocity;
            s.Samples[k].FloorY   = floorY;
            s.Samples[k].Grounded = grounded;
        }
    }

    // ── Polyline: vertex 0 is the body, then the path's nodes ────────────
    public static int BeginPolyline(CorrectorScratch s, Vector2 origin)
    {
        s.BeadVerts[0] = origin; s.BeadArc[0] = 0f;
        return 1;
    }

    // Appends q (dropping a duplicate of the last vertex); false when full.
    public static bool AppendVertex(CorrectorScratch s, ref int nv, Vector2 q)
    {
        if ((q - s.BeadVerts[nv - 1]).LengthSquared() < 1e-6f) return true;
        if (nv >= s.BeadVerts.Length) return false;
        s.BeadArc[nv] = s.BeadArc[nv - 1] + (q - s.BeadVerts[nv - 1]).Length();
        s.BeadVerts[nv] = q; nv++;
        return true;
    }

    // ── The solve: sliding beads, outer passes of project → rows → solve ─
    // Leaves the final pass in s.Problem / s.Z / s.TickDv.
    public static void Solve<TPolicy>(CorrectorScratch s, in PathTrackSettings set, ref TPolicy policy)
        where TPolicy : struct, IPathTrackPolicy
    {
        int H = set.H, nv = set.VertexCount;
        float dt = set.Dt, band = set.Band;
        bool havePath  = nv >= 2;
        bool hoverLine = !havePath && !float.IsNaN(set.HoverLineY);
        bool arcSpeed  = havePath && float.IsFinite(set.ArcSpeedCap);
        var pr = s.Problem;

        for (int pass = 0; pass < BeadPasses; pass++)
        {
            int rowCount = set.WallRows;
            Vector2 tLast = set.FallbackTangent, qLast = set.Origin;
            float sPrev = 0f;
            for (int T = 0; T < H; T++)
            {
                Vector2 delta = pass == 0 ? Vector2.Zero : s.TrackDelta[T];
                Vector2 pT = s.DeliverySamples[T].Pos + delta;
                if (havePath)
                {
                    float sT = MathF.Max(sPrev, ProjectArc(s.BeadVerts, s.BeadArc, nv, pT));
                    sPrev = sT;
                    PointAt(s.BeadVerts, s.BeadArc, nv, sT, out var q, out var t);
                    tLast = t; qLast = q;
                    var n = new Vector2(-t.Y, t.X);
                    // e: the FREE rollout's offset from the bead along n
                    // (rows measure Δp from the free rollout).
                    float e = Vector2.Dot(pT - q, n) - Vector2.Dot(delta, n);
                    s.Rows[rowCount++] = new ClearanceRow { Tick = T, Normal = n,  Depth = -band - e, HingeScale = 1f, Reference = true };
                    s.Rows[rowCount++] = new ClearanceRow { Tick = T, Normal = -n, Depth = e - band,  HingeScale = 1f, Reference = true };
                    if (arcSpeed)
                    {
                        // A speed along the path: the body's arc position is the
                        // bead's, so the cap is the bead's own coordinate,
                        // s_T ≤ speed·(T+1)·dt.
                        float sFree = sT - Vector2.Dot(delta, t);
                        s.Rows[rowCount++] = new ClearanceRow
                            { Tick = T, Normal = -t, Depth = sFree - set.ArcSpeedCap * (T + 1) * dt, HingeScale = 1f, Reference = true };
                    }
                }
                else if (hoverLine)
                {
                    float e = s.DeliverySamples[T].Pos.Y - set.HoverLineY;
                    s.Rows[rowCount++] = new ClearanceRow { Tick = T, Normal = new Vector2(0f, 1f),  Depth = -band - e, HingeScale = 1f, Reference = true };
                    s.Rows[rowCount++] = new ClearanceRow { Tick = T, Normal = new Vector2(0f, -1f), Depth = e - band,  HingeScale = 1f, Reference = true };
                }
            }
            rowCount = policy.AddRows(s, H, dt, pass, rowCount);

            // Progress: what the channels could add along t̂ by the last tick
            // at their caps — Σ_k (T−k+1)·dt² = dt²·(T+1)(T+2)/2. t̂ is the
            // CHORD body → last bead, the direction the plan takes over the
            // horizon, not the last segment's tangent: a cell-quantized path
            // zigzags, and a descending segment tangent prices a TUCK as
            // progress.
            if (havePath)
            {
                var chord = qLast - set.Origin;
                if (chord.LengthSquared() > 1e-6f) tLast = Vector2.Normalize(chord);
            }
            if (policy.ProgressAuthority(s, tLast, out float capAlong))
            {
                int T9 = H - 1;
                s.Rows[rowCount++] = new ClearanceRow
                {
                    Tick = T9, Normal = tLast, HingeScale = ProgressHingeScale,
                    Depth = capAlong * dt * dt * (T9 + 1) * (T9 + 2) * 0.5f,
                };
            }

            pr.H = H; pr.Dt = dt;
            pr.CoastVel = s.CoastVel;
            pr.Rows = s.Rows; pr.RowCount = rowCount;
            pr.ChannelCount = policy.BuildChannels(s, H, rowCount);
            // No Δ-smoothing: caps and the band bound the plan, and "as fast
            // as possible" IS bang-bang (a launch is the legs at their cap on
            // tick 0; smoothing anchored it to the previous frame's output).
            for (int c = 0; c < pr.ChannelCount; c++) pr.PrevApplied[c] = Vector2.Zero;
            pr.DeltaWeight = 0f;
            pr.HingeWeight = HingeWeight;
            pr.InnerIterations = set.Iterations;
            pr.RowPush = s.RowPush;

            CorrectionSolver.Solve(pr, s.Z, s.ZScratch);
            // The applied tick, exactly (the sweeps under-deliver: a channel a
            // row asks for at its cap reaches a few percent of it).
            CorrectionSolver.ExactSweeps(pr, s.Z, ExactSweepCount, H);
            CorrectorChannels.ComputeTickDv(s, pr, H, dt);
            if (!havePath) break;                                // no beads to slide
            // Corrected displacement per tick for the next pass's projection:
            // δv at tick k moves tick T by (T − k + 1)·dt.
            for (int T = 0; T < H; T++)
            {
                var d = Vector2.Zero;
                for (int k = 0; k <= T; k++) d += s.TickDv[k] * ((T - k + 1) * dt);
                s.TrackDelta[T] = d;
            }
        }
    }

    // The whole loop for a state whose only actuators are its policy's
    // channels: free rollout from the state's baseline (already in
    // body.AppliedForce), held samples, terrain clearance rows, the bead
    // solve, and tick 0 applied. The polyline must already be in s.BeadVerts
    // (BeginPolyline/AppendVertex, or BuildClipPolyline).
    public static void FollowPolyline<TPolicy>(EnvironmentContext ctx, CorrectorScratch s, int nv,
        ref TPolicy policy, float floorY = float.PositiveInfinity, bool grounded = false)
        where TPolicy : struct, IPathTrackPolicy
    {
        var cfg = MovementConfig.Current;
        var body = ctx.Body;
        float dt = ctx.Dt;
        int H = Math.Min(Horizon, BallisticPredictor.MaxHorizon);
        FreeRollout(s, body, ctx.Gravity, H, dt);
        HoldSamples(s, body, H, floorY, grounded);
        // The path is guidance; the tiles are the constraint (LatticeTracker's
        // wall rows, same margin).
        int wallRows = ClearanceConstraintBuilder.Build(ctx.Chunks, body.Polygon, s.DeliverySamples, H,
            LatticePathPlanner.Clearance, ClearanceConstraintBuilder.DefaultDeepViolation, s.Rows, out _,
            verticalFacesOnly: false);
        wallRows = Math.Min(wallRows, ClearanceConstraintBuilder.MaxEvents - (3 * H + 1));
        var set = new PathTrackSettings
        {
            H = H, Dt = dt, Origin = body.Position, VertexCount = nv,
            Band = FollowBand(cfg),
            ArcSpeedCap = float.PositiveInfinity,
            HoverLineY  = float.NaN,
            FallbackTangent = nv >= 2 ? Vector2.Normalize(s.BeadVerts[1] - s.BeadVerts[0]) : Vector2.Zero,
            WallRows = wallRows,
            Iterations = cfg.FoldIterations,
        };
        Solve(s, set, ref policy);
        ApplyTick0(s, body, dt);
        if (s.CaptureTrajectories)
        {
            int m = Math.Min(nv, BallisticPredictor.MaxHorizon);
            for (int i = 0; i < m; i++) s.SolvedTrajectory[i] = new CoastSample { Pos = s.BeadVerts[i] };
            s.SolvedCount = m;
            for (int k = 0; k < H; k++) s.BallisticTrajectory[k] = s.DeliverySamples[k];
            s.BallisticCount = H;
        }
    }

    // Applies tick 0 of the last solve to the body and records the ledger.
    public static void ApplyTick0(CorrectorScratch s, PhysicsBody body, float dt)
    {
        body.AppliedForce += s.TickDv[0] / dt;
        s.Ledger.Record(s.Problem, s.Z, s.RowPush, s.Samples, dt);
    }

    // Arc length of the nearest point on the polyline (vertices 0..nv−1,
    // last segment extended as a ray — the honest carry past the end).
    public static float ProjectArc(ReadOnlySpan<Vector2> verts, ReadOnlySpan<float> arc, int nv, Vector2 p)
    {
        float best = float.PositiveInfinity, bestArc = 0f;
        for (int i = 0; i + 1 < nv; i++)
        {
            var a = verts[i]; var d = verts[i + 1] - a;
            float len2 = d.LengthSquared();
            if (len2 < 1e-9f) continue;
            float t = Vector2.Dot(p - a, d) / len2;
            bool last = i + 2 == nv;
            t = last ? MathF.Max(0f, t) : Math.Clamp(t, 0f, 1f);
            var q = a + d * t;
            float dist2 = (p - q).LengthSquared();
            if (dist2 < best) { best = dist2; bestArc = arc[i] + t * MathF.Sqrt(len2); }
        }
        return bestArc;
    }

    // Point and unit tangent at arc length `s` (past the end: along the last
    // segment's ray).
    public static void PointAt(ReadOnlySpan<Vector2> verts, ReadOnlySpan<float> arc, int nv, float s,
                               out Vector2 q, out Vector2 t)
    {
        int i = 0;
        while (i + 2 < nv && s > arc[i + 1]) i++;
        var a = verts[i]; var d = verts[i + 1] - a;
        float len = d.Length();
        t = len > 1e-6f ? d / len : new Vector2(1f, 0f);
        q = a + t * (s - arc[i]);
    }

    // ── Authored paths ───────────────────────────────────────────────────
    // A retargeted reference clip as a GEOMETRIC path: its time
    // parametrization is dropped — the tracker's caps set the pace. The
    // polyline starts at the body and continues from the clip node just past
    // the body's own projection onto the clip, so a body that has already
    // moved along (or arrived moving) is not pulled back to the clip start.
    // Returns the vertex count in s.BeadVerts; totalArc = the full clip's
    // retargeted length (for deriving a pace from an authored duration).
    public const int ClipPathSamples = 24;

    public static int BuildClipPolyline(CorrectorScratch s, Vector2 origin,
        HermiteClipDocument clip, in ReferenceFrame frame, out float totalArc)
    {
        Span<Vector2> pts = stackalloc Vector2[ClipPathSamples + 1];
        Span<float>   arc = stackalloc float[ClipPathSamples + 1];
        for (int i = 0; i <= ClipPathSamples; i++)
        {
            pts[i] = frame.Map(clip.Eval((float)i / ClipPathSamples));
            arc[i] = i == 0 ? 0f : arc[i - 1] + (pts[i] - pts[i - 1]).Length();
        }
        totalArc = arc[ClipPathSamples];
        float a0 = ProjectArc(pts, arc, ClipPathSamples + 1, origin);
        int nv = BeginPolyline(s, origin);
        for (int i = 1; i <= ClipPathSamples; i++)
            if (arc[i] > a0 + 0.5f) AppendVertex(s, ref nv, pts[i]);
        if (nv < 2)
        {
            // Past the clip's end: the path continues along its final direction
            // FROM ITS END POINT — a point on that ray just ahead of the body's own
            // projection — so a body that overshot the arc (a vault cresting high
            // past its gate) is drawn back onto the authored line. A ray from the
            // body itself would hold the body at whatever height it overshot to.
            var end = pts[ClipPathSamples];
            var dir = end - pts[ClipPathSamples - 1];
            float len = dir.Length();
            dir = len > 1e-6f ? dir / len : new Vector2(1f, 0f);
            float ahead = MathF.Max(0f, a0 - totalArc) + 2f * (totalArc / ClipPathSamples);
            AppendVertex(s, ref nv, end + dir * ahead);
        }
        return nv;
    }
}
