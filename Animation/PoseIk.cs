using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Offline inverse kinematics for clip authoring (the MTile.Probe `ik` command): given a
// posed rig, a tip bone, and a target point in root-local rig units, solve the chain's
// local rotations so the tip best reaches the target while staying close to the seed
// pose. Least-squares with a soft prior toward the seed, so an unreachable target
// returns the closest reachable pose plus the miss distance instead of failing — the
// caller reads the miss and revises the target. Authoring-tool analogue of the runtime
// FixedPoint solve; never runs in the game loop.
public static class PoseIk
{
    public readonly struct Result
    {
        public readonly Vector2 Achieved;   // tip world position after the solve
        public readonly float   Miss;       // |achieved − target| in rig units
        public Result(Vector2 achieved, float miss) { Achieved = achieved; Miss = miss; }
    }

    // Default chain for a tip: the bone plus its ancestors up to (excluding) the torso —
    // the limb itself. leg_l_lower → [leg_l_upper, leg_l_lower]; a hand
    // (arm_*_lower) → [arm_*_upper, arm_*_lower]. Root-most first.
    public static int[] DefaultChain(Skeleton rig, int tip)
    {
        Span<int> tmp = stackalloc int[8];
        int n = 0;
        for (int b = tip; b >= 0 && n < tmp.Length; b = rig.Bones[b].Parent)
        {
            string name = rig.Bones[b].Name;
            if (name == "hip" || name == "chest") break;
            tmp[n++] = b;
        }
        var chain = new int[n];
        for (int i = 0; i < n; i++) chain[i] = tmp[n - 1 - i];
        return chain;
    }

    // Solves in place: `pose` enters as the seed and leaves holding the solved angles.
    // `priorWeight` is rig-units-per-radian — how hard each joint is pulled back toward
    // its seed relative to the two tip-position rows (lever arms on this rig are
    // ~10–20 units/rad, so the default barely resists reach but breaks redundancy and
    // keeps the solution minimal-change). `range` bounds each angle to seed ± range.
    public static Result Solve(Skeleton rig, SkeletonPose pose, int tipBone, Vector2 target,
                               int[] chain, float priorWeight = 0.5f, float range = 2.5f)
    {
        int n = chain.Length;
        var x = new float[n]; var lo = new float[n]; var hi = new float[n];
        var seed = new float[n];
        for (int i = 0; i < n; i++)
        {
            seed[i] = x[i] = pose.Local[chain[i]].Rotation;
            lo[i] = seed[i] - range;
            hi[i] = seed[i] + range;
        }
        var root = Affine2.FromTRS(Vector2.Zero, 0f, Vector2.One);

        int Residuals(ReadOnlySpan<float> xs, Span<float> r)
        {
            // Rotations only: each bone keeps its local translation (the keyframe's Stretch).
            for (int i = 0; i < n; i++) pose.Local[chain[i]].Rotation = xs[i];
            Vector2 tip = pose.ComputeWorld(root)[tipBone].Translation;
            r[0] = tip.X - target.X;
            r[1] = tip.Y - target.Y;
            for (int i = 0; i < n; i++) r[2 + i] = priorWeight * (xs[i] - seed[i]);
            return 2 + n;
        }

        var solver = new LeastSquaresSolver(n, 2 + n);
        solver.Minimize(Residuals, x, lo, hi, iters: 60);

        // Re-evaluate at the accepted x so the pose holds the solution (the solver's
        // last internal evaluation may have been a rejected trial step).
        Span<float> final = stackalloc float[2 + n];
        Residuals(x, final);
        return new Result(target + new Vector2(final[0], final[1]),
                          MathF.Sqrt(final[0] * final[0] + final[1] * final[1]));
    }

    // INTERACTIVE DRAG SESSION (Plans/ANIMATION_WORKPLAN.md chunk 3.5 — the editor's prior-
    // based kinematics drag mode). One LM solve per frame while a joint is held, pulling the
    // clicked node toward the mouse with two priors:
    //   A — the ORIGINAL pose: a weight toward the drag-start rotations. Elastic minimal
    //       change; error cannot accumulate across the drag because the anchor never moves.
    //   B — the PREVIOUS solution: warm start plus a small weight toward last frame's angles,
    //       so the limb doesn't snap between LM basins mid-drag.
    // A fold-sign guard on `*_lower` bones keeps the bend on the drag-start side of straight
    // ("elbows don't bend backwards") — pragmatic until runtime §8 defines rig-level joint
    // limits, which this should then consume. Solver and arrays are allocated once per
    // session (PoseIk.Solve allocates per call — fine for the probe, not for a 60 Hz drag).
    //
    // FORESHORTENED BONES. A bone with a small Stretch barely moves the tip when it turns,
    // so the solver would treat its rotation as nearly free and spin it to fix tiny errors.
    // Its pull toward the drag-start angle is scaled by 1/|Stretch|, and a bone nearly
    // end-on (|Stretch| < FreezeStretch at drag start) is held fixed for the drag.
    //
    // DEPTH MODE (solveStretch). A short reach can mean "bend" or "turn toward the camera".
    // The plain drag only bends (Stretch is left as it is). In depth mode each chain bone's
    // Stretch is a variable too, bounded to |s| in [MinStretch, 1] on its drag-start side,
    // with rotations held harder than stretches — so pulling the hand in foreshortens the
    // limb instead of folding it. The fold guard is off: a limb turning through the camera
    // axis legitimately flips which side its 2D bend reads on.
    public sealed class DragSession
    {
        public readonly int   Tip;
        public readonly int[] Chain;
        public readonly bool  SolveStretch;
        private readonly Skeleton _rig;
        private readonly float[] _orig, _prev, _x, _lo, _hi, _rotW;
        private readonly float[] _origT;     // drag-start local translations (+X = length)
        private readonly LeastSquaresSolver _ls;
        private readonly Affine2 _identity = Affine2.FromTRS(Vector2.Zero, 0f, Vector2.One);
        private SkeletonPose _pose;
        private Vector2 _target;
        public float WeightOriginal = 0.2f;   // rig units per radian toward the drag-start pose
        public float WeightPrevious = 0.05f;  // toward last frame's solution (continuity)
        public float Range          = 2.5f;   // per-joint |Δ| bound from the drag-start angle
        public const float FreezeStretch = 0.2f;   // |s| below this at drag start: bone held fixed
        public const float MinStretch    = 0.15f;  // depth mode's floor on |s|
        // Depth mode: rotations held this much harder than plain mode, and Stretch's own pull
        // toward its drag-start value (rig units per unit of s). Foreshortening a 10-unit bone
        // by 0.2 costs 0.1 here against ~0.6·(rotation) for the bend that would do the same.
        public float DepthRotationScale = 3f;
        public float WeightStretch      = 0.5f;
        public Result Last { get; private set; }

        public DragSession(Skeleton rig, SkeletonPose pose, int tip, int[] chain = null, bool solveStretch = false)
        {
            _rig = rig; Tip = tip; SolveStretch = solveStretch;
            Chain = chain ?? DefaultChain(rig, tip);
            int n = Chain.Length, nv = solveStretch ? 2 * n : n;
            _orig = new float[nv]; _prev = new float[nv]; _x = new float[nv]; _lo = new float[nv]; _hi = new float[nv];
            _rotW = new float[n]; _origT = new float[n];
            for (int i = 0; i < n; i++)
            {
                _orig[i] = _prev[i] = pose.Local[Chain[i]].Rotation;
                _origT[i] = pose.Local[Chain[i]].Translation.X;
                if (solveStretch) _orig[n + i] = _prev[n + i] = StretchOf(i, _origT[i]);
            }
            _ls = new LeastSquaresSolver(Math.Max(nv, 1), 2 + 2 * nv);
        }

        private float StretchOf(int i, float tx)
        {
            float len = _rig.Bones[Chain[i]].StretchLength;
            return len > 1e-4f ? tx / len : 1f;
        }

        // Restore the drag-start rotations and lengths (Escape mid-drag).
        public void Restore(SkeletonPose pose)
        {
            for (int i = 0; i < Chain.Length; i++)
            {
                pose.Local[Chain[i]].Rotation = _orig[i];
                pose.Local[Chain[i]].Translation = Vector2.UnitX * _origT[i];
            }
        }

        // One frame: pull `Tip` toward `targetLocal` (root-local rig units). Solves in place.
        public Result Step(SkeletonPose pose, Vector2 targetLocal)
        {
            int n = Chain.Length, nv = _x.Length;
            _pose = pose; _target = targetLocal;
            for (int i = 0; i < n; i++)
            {
                _x[i] = _prev[i];   // warm start
                float o = _orig[i];
                float lo = o - Range, hi = o + Range;
                float s0 = MathF.Abs(StretchOf(i, _origT[i]));
                // Fold guard: a `*_lower` bone's bend keeps the drag-start sign of its local angle.
                if (!SolveStretch && _rig.Bones[Chain[i]].Name.EndsWith("_lower", StringComparison.Ordinal) && MathF.Abs(o) > 0.05f)
                {
                    if (o > 0f) lo = MathF.Max(lo, 0f); else hi = MathF.Min(hi, 0f);
                }
                if (s0 < FreezeStretch) lo = hi = o;   // nearly end-on: its angle is noise
                _lo[i] = lo; _hi[i] = hi;
                _rotW[i] = WeightOriginal * (SolveStretch ? DepthRotationScale : 1f) / MathF.Max(s0, FreezeStretch);
            }
            if (SolveStretch)
                for (int i = 0; i < n; i++)
                {
                    int k = n + i;
                    _x[k] = _prev[k];
                    float s0 = _orig[k];
                    // Stay on the drag-start side of the depth axis; a legacy |s| > 1 key keeps
                    // its value reachable rather than snapping to 1 on the first frame.
                    if (s0 >= 0f) { _lo[k] = MathF.Min(MinStretch, s0); _hi[k] = MathF.Max(1f, s0); }
                    else          { _lo[k] = MathF.Min(-1f, s0);        _hi[k] = MathF.Max(-MinStretch, s0); }
                    if (MathF.Abs(s0) < FreezeStretch || _rig.Bones[Chain[i]].StretchLength < 1e-4f) _lo[k] = _hi[k] = s0;
                }
            _ls.Minimize(Residuals, _x, _lo, _hi, iters: 40);
            Span<float> final = stackalloc float[2 + 2 * nv];
            Residuals(_x, final);
            for (int i = 0; i < nv; i++) _prev[i] = _x[i];
            Last = new Result(targetLocal + new Vector2(final[0], final[1]),
                              MathF.Sqrt(final[0] * final[0] + final[1] * final[1]));
            return Last;
        }

        private int Residuals(ReadOnlySpan<float> xs, Span<float> r)
        {
            int n = Chain.Length, nv = xs.Length;
            for (int i = 0; i < n; i++)
            {
                _pose.Local[Chain[i]].Rotation = xs[i];
                if (SolveStretch)
                    _pose.Local[Chain[i]].Translation = Vector2.UnitX * (xs[n + i] * _rig.Bones[Chain[i]].StretchLength);
            }
            Vector2 tip = _pose.ComputeWorld(_identity)[Tip].Translation;
            r[0] = tip.X - _target.X;
            r[1] = tip.Y - _target.Y;
            for (int i = 0; i < nv; i++)
            {
                float w = i < n ? _rotW[i] : WeightStretch;
                r[2 + i]      = w * (xs[i] - _orig[i]);
                r[2 + nv + i] = WeightPrevious * (xs[i] - _prev[i]);
            }
            return 2 + 2 * nv;
        }
    }
}
