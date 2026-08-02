using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Nonlinear trajectory engine for the stand fold (MovementConfig.FoldEngine
// "lm"; the linearized QP channel solve remains "qp"). The predicted tick
// POSITIONS are the variables — no ballistic coast, no frozen row set, no
// convexification gap: LM (LeastSquaresSolver, analytic Jacobian) minimizes
//   - hard hinge: C-obstacle penetration depth at each tick's ACTUAL position
//   - soft hover: y tracks the floor envelope minus the fold's hover offset
//   - soft progress: x tracks x0 + dir·targetSpeed·t (anchored ticks only)
//   - effort: per-tick δv beyond gravity (the TickDv analog, regularizer)
// all re-evaluated at the current iterate each iteration, so every candidate
// trajectory feels the same terrain uniformly. Local minima are accepted —
// the animation solver's bargain, adopted deliberately after the QP fold's
// frozen-row violations (a slow coast's rows can't protect the fast plan).
//
// Kept from the QP fold's physical-honesty gates (coarse, whole-solve):
// plunging past MaxGroundEngageVnRel or rising past SpringMaxRiseSpeed
// unbinds the envelope (raw landings / ballistic launches stay honest).
// NOT carried over (deliberately bare, pending playtest): channel caps,
// down-anchor descent rate limiting, elective climb refusal, per-tile
// contact attribution (the ledger records only the net applied force).
//
// Determinism: one pooled instance per CorrectorScratch, fixed iteration
// budget, no statics, no allocation after construction (delegates cached in
// the ctor). The solve is STATELESS frame-to-frame — the seed is always the
// straight line at the current velocity — so rollback restore reproduces it
// with no snapshot support.
public sealed class TrajectoryLm
{
    private const int ResPerTick = 5;   // pen, hover, progress, effort x/y
    private const float Margin = 1f;    // clearance margin (px), as the QP rows used

    private readonly LeastSquaresSolver _solver =
        new(2 * BallisticPredictor.MaxHorizon, ResPerTick * BallisticPredictor.MaxHorizon);
    private readonly float[] _x  = new float[2 * BallisticPredictor.MaxHorizon];
    private readonly float[] _lo = new float[2 * BallisticPredictor.MaxHorizon];
    private readonly float[] _hi = new float[2 * BallisticPredictor.MaxHorizon];
    private readonly LeastSquaresSolver.ResidualFn _fnDel;
    private readonly LeastSquaresSolver.JacobianFn _jacDel;

    public TrajectoryLm()
    {
        Array.Fill(_lo, -1e9f);
        Array.Fill(_hi,  1e9f);
        _fnDel  = Residuals;
        _jacDel = Jacobian;
    }

    // Per-solve inputs, set by Solve and read by the cached delegates.
    private ChunkMap _chunks;
    private CObstacleTemplate _template;
    private Vector2 _pos0, _vel0, _gravity;
    private float _dt, _target, _hoverOffset, _climbReach, _anchorY;
    private int _dir, _h;
    private bool _anchored, _trackProgress;
    private float _wPen, _wHover, _wProg, _wEff;

    // Solves H ticks from (pos0, vel0) and writes the plan into
    // outSamples[0..H). Returns H (0 only on degenerate input). The plan's
    // tick-0 velocity is outSamples[0].Vel — the apply site corrects toward it.
    public int Solve(ChunkMap chunks, Polygon body, Vector2 pos0, Vector2 vel0,
                     int dir, float targetSpeed, bool trackProgress,
                     float hoverOffset, float climbReach,
                     Vector2 gravity, float dt, int H, int iterations,
                     CoastSample[] outSamples, out float cost)
    {
        cost = 0f;
        if (dt <= 0f) return 0;
        H = Math.Clamp(Math.Min(H, outSamples.Length), 1, BallisticPredictor.MaxHorizon);

        var cfg = MovementConfig.Current;
        _chunks = chunks;
        _template = CObstacleTemplate.For(body);
        _pos0 = pos0; _vel0 = vel0; _gravity = gravity;
        _dt = dt; _h = H; _dir = dir;
        _target = targetSpeed; _hoverOffset = hoverOffset; _climbReach = climbReach;
        _wPen = cfg.FoldLmPenWeight;      _wHover = cfg.FoldLmHoverWeight;
        _wProg = cfg.FoldLmProgressWeight; _wEff  = cfg.FoldLmEffortWeight;

        // Support anchor from the CURRENT pose (the QP fold's rule: the climb
        // band measures from what the body stands on, so a transient rise
        // can't ladder the reference up a wall). Envelope unbinds while
        // plunging (raw-impact honesty) or launched (ballistic honesty).
        _anchorY = AmbientCorrector.FloorEnvelope(chunks, _template, pos0.X,
            pos0.Y - 2f, pos0.Y + BallisticPredictor.SupportReach, out _anchored);
        if (vel0.Y > cfg.MaxGroundEngageVnRel) _anchored = false;
        if (-vel0.Y > cfg.SpringMaxRiseSpeed)  _anchored = false;
        _trackProgress = trackProgress && dir != 0 && _anchored;

        // Seed: straight line at the current velocity.
        for (int k = 0; k < H; k++)
        {
            _x[2 * k]     = pos0.X + vel0.X * (k + 1) * dt;
            _x[2 * k + 1] = pos0.Y + vel0.Y * (k + 1) * dt;
        }

        int vars = 2 * H;
        cost = _solver.Minimize(_fnDel, _jacDel, _x.AsSpan(0, vars),
                                _lo.AsSpan(0, vars), _hi.AsSpan(0, vars), iterations);

        Vector2 prev = pos0;
        for (int k = 0; k < H; k++)
        {
            var p = new Vector2(_x[2 * k], _x[2 * k + 1]);
            outSamples[k].Pos      = p;
            outSamples[k].Vel      = (p - prev) / dt;
            outSamples[k].Grounded = false;
            outSamples[k].FloorY   = float.PositiveInfinity;
            prev = p;
        }
        _chunks = null;   // no stale world refs between solves
        return H;
    }

    private float Envelope(float x, out bool found)
        => AmbientCorrector.FloorEnvelope(_chunks, _template, x,
            _anchorY - _climbReach, _anchorY + BallisticPredictor.SupportReach, out found);

    private int Residuals(ReadOnlySpan<float> x, Span<float> r)
    {
        int ri = 0;
        Vector2 pPrev = _pos0, vPrev = _vel0;
        for (int k = 0; k < _h; k++)
        {
            var p = new Vector2(x[2 * k], x[2 * k + 1]);

            r[ri++] = _wPen * PenetrationAt(p, out _);

            float hover = 0f;
            if (_anchored)
            {
                float env = Envelope(p.X, out bool found);
                if (found) hover = _wHover * (p.Y - (env - _hoverOffset));
            }
            r[ri++] = hover;

            r[ri++] = _trackProgress
                ? _wProg * (p.X - (_pos0.X + _dir * _target * (k + 1) * _dt))
                : 0f;

            var v = (p - pPrev) / _dt;
            var z = v - vPrev - _gravity * _dt;
            r[ri++] = _wEff * z.X;
            r[ri++] = _wEff * z.Y;
            pPrev = p; vPrev = v;
        }
        return ri;
    }

    // Analytic Jacobian, rows in Residuals' order (the solver pre-zeroes the
    // block). Penetration rows use the active facet normal (∂depth/∂p = −n);
    // hover rows finite-difference the envelope's x-slope (bevel ramps) but
    // are otherwise exact; effort rows are the banded second-difference
    // stencil. Nondifferentiable seams (max over cells/facets, hinge
    // boundaries, envelope binds) are left to LM's damping, as in the
    // animation solver.
    private void Jacobian(ReadOnlySpan<float> x, Span<float> jac, int stride)
    {
        float inv = 1f / _dt;
        for (int k = 0; k < _h; k++)
        {
            int row = ResPerTick * k;
            int cx = 2 * k, cy = 2 * k + 1;
            var p = new Vector2(x[cx], x[cy]);

            if (PenetrationAt(p, out Vector2 nrm) > 0f)
            {
                jac[row * stride + cx] = -_wPen * nrm.X;
                jac[row * stride + cy] = -_wPen * nrm.Y;
            }

            if (_anchored)
            {
                Envelope(p.X, out bool found);
                if (found)
                {
                    jac[(row + 1) * stride + cy] = _wHover;
                    float envL = Envelope(p.X - 0.5f, out bool fl);
                    float envR = Envelope(p.X + 0.5f, out bool fr);
                    if (fl && fr)
                        jac[(row + 1) * stride + cx] = -_wHover * (envR - envL);
                }
            }

            if (_trackProgress)
                jac[(row + 2) * stride + cx] = _wProg;

            jac[(row + 3) * stride + cx] = _wEff * inv;
            jac[(row + 4) * stride + cy] = _wEff * inv;
            if (k >= 1)
            {
                jac[(row + 3) * stride + cx - 2] = -2f * _wEff * inv;
                jac[(row + 4) * stride + cy - 2] = -2f * _wEff * inv;
            }
            if (k >= 2)
            {
                jac[(row + 3) * stride + cx - 4] = _wEff * inv;
                jac[(row + 4) * stride + cy - 4] = _wEff * inv;
            }
        }
    }

    // The hinge activates this far OUTSIDE the margin surface. An inactive
    // hinge has zero gradient, so without a band the GN step plans straight
    // through the lip, the trial cost explodes discontinuously, LM's damping
    // ratchets up, and Minimize's no-downhill-step exit fires with even the
    // linear residuals unserved (the corridor-mouth stall). Inside the band
    // the Jacobian sees the wall coming and the plan slides along the
    // boundary at a ~Band standoff instead of freezing at a cost cliff.
    private const float ProximityBand = 3f;

    // Banded C-obstacle clearance residual of the body center at pos, and the
    // active facet's outward normal. sd = min over facets of facet depth
    // (positive inside — the nearest exit; ≈ signed distance outside);
    // returns max(0, sd + ProximityBand) over nearby cells.
    //
    // A cell is skipped when its nearest facet is unexposed (interior to the
    // C-space union — the surface cell covers it) or is a wall face
    // (|n.Y| < 0.3 — the QP builder's verticalFacesOnly rule, mirrored:
    // walls are invisible to planning and physics delivers the honest bonk;
    // without this the mouth cells' nearest exit is "straight back out" and
    // the automatic duck never forms).
    private float PenetrationAt(Vector2 pos, out Vector2 normal)
    {
        normal = Vector2.Zero;
        var facets = _template.Facets;
        int F = Math.Min(facets.Length, ClearanceConstraintBuilder.MaxFacets);
        float reach = _template.Reach + Margin + ProximityBand;
        int ts = Chunk.TileSize;
        float half = ts * 0.5f;

        int cMin = (int)MathF.Floor((pos.X - reach) / ts), cMax = (int)MathF.Floor((pos.X + reach) / ts);
        int rMin = (int)MathF.Floor((pos.Y - reach) / ts), rMax = (int)MathF.Floor((pos.Y + reach) / ts);

        float pen = 0f;
        for (int gtx = cMin; gtx <= cMax; gtx++)
        for (int gty = rMin; gty <= rMax; gty++)
        {
            float cx = gtx * ts + half, cy = gty * ts + half;
            if (!TileQuery.IsSolidAt(_chunks, cx, cy)) continue;

            var rel = new Vector2(pos.X - cx, pos.Y - cy);
            float sd = float.MaxValue;
            int minF = -1;
            for (int f = 0; f < F; f++)
            {
                float d = facets[f].Offset + Margin - Vector2.Dot(rel, facets[f].Normal);
                if (d < sd) { sd = d; minF = f; }
            }
            if (minF < 0 || sd + ProximityBand <= pen) continue;

            var fc = facets[minF];
            if (TileQuery.IsSolidAt(_chunks, cx + fc.MaskDx1 * ts, cy + fc.MaskDy1 * ts)) continue;
            if (fc.TwoMasks && TileQuery.IsSolidAt(_chunks, cx + fc.MaskDx2 * ts, cy + fc.MaskDy2 * ts)) continue;
            if (MathF.Abs(fc.Normal.Y) < 0.3f) continue;   // wall face — invisible

            pen = sd + ProximityBand;
            normal = fc.Normal;
        }
        return pen;
    }
}
