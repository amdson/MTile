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
            for (int i = 0; i < n; i++)
                pose.SetLocal(chain[i], new BoneTransform(
                    Vector2.UnitX * rig.Bones[chain[i]].Length, xs[i], Vector2.One));
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
    // Translations (a keyframe's Stretch) are left as they are; only rotations move.
    public sealed class DragSession
    {
        public readonly int   Tip;
        public readonly int[] Chain;
        private readonly Skeleton _rig;
        private readonly float[] _orig, _prev, _x, _lo, _hi;
        private readonly LeastSquaresSolver _ls;
        private readonly Affine2 _identity = Affine2.FromTRS(Vector2.Zero, 0f, Vector2.One);
        private SkeletonPose _pose;
        private Vector2 _target;
        public float WeightOriginal = 0.2f;   // rig units per radian toward the drag-start pose
        public float WeightPrevious = 0.05f;  // toward last frame's solution (continuity)
        public float Range          = 2.5f;   // per-joint |Δ| bound from the drag-start angle
        public Result Last { get; private set; }

        public DragSession(Skeleton rig, SkeletonPose pose, int tip, int[] chain = null)
        {
            _rig = rig; Tip = tip;
            Chain = chain ?? DefaultChain(rig, tip);
            int n = Chain.Length;
            _orig = new float[n]; _prev = new float[n]; _x = new float[n]; _lo = new float[n]; _hi = new float[n];
            for (int i = 0; i < n; i++) _orig[i] = _prev[i] = pose.Local[Chain[i]].Rotation;
            _ls = new LeastSquaresSolver(Math.Max(n, 1), 2 + 2 * n);
        }

        // Restore the drag-start rotations (Escape mid-drag).
        public void Restore(SkeletonPose pose)
        {
            for (int i = 0; i < Chain.Length; i++) pose.Local[Chain[i]].Rotation = _orig[i];
        }

        // One frame: pull `Tip` toward `targetLocal` (root-local rig units). Solves in place.
        public Result Step(SkeletonPose pose, Vector2 targetLocal)
        {
            int n = Chain.Length;
            _pose = pose; _target = targetLocal;
            for (int i = 0; i < n; i++)
            {
                _x[i] = _prev[i];   // warm start
                float o = _orig[i];
                float lo = o - Range, hi = o + Range;
                // Fold guard: a `*_lower` bone's bend keeps the drag-start sign of its local angle.
                if (_rig.Bones[Chain[i]].Name.EndsWith("_lower", StringComparison.Ordinal) && MathF.Abs(o) > 0.05f)
                {
                    if (o > 0f) lo = MathF.Max(lo, 0f); else hi = MathF.Min(hi, 0f);
                }
                _lo[i] = lo; _hi[i] = hi;
            }
            _ls.Minimize(Residuals, _x, _lo, _hi, iters: 40);
            Span<float> final = stackalloc float[2 + 2 * n];
            Residuals(_x, final);
            for (int i = 0; i < n; i++) _prev[i] = _x[i];
            Last = new Result(targetLocal + new Vector2(final[0], final[1]),
                              MathF.Sqrt(final[0] * final[0] + final[1] * final[1]));
            return Last;
        }

        private int Residuals(ReadOnlySpan<float> xs, Span<float> r)
        {
            int n = Chain.Length;
            for (int i = 0; i < n; i++) _pose.Local[Chain[i]].Rotation = xs[i];
            Vector2 tip = _pose.ComputeWorld(_identity)[Tip].Translation;
            r[0] = tip.X - _target.X;
            r[1] = tip.Y - _target.Y;
            for (int i = 0; i < n; i++)
            {
                r[2 + i]     = WeightOriginal * (xs[i] - _orig[i]);
                r[2 + n + i] = WeightPrevious * (xs[i] - _prev[i]);
            }
            return 2 + 2 * n;
        }
    }
}
