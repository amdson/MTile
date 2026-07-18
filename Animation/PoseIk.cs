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
    // the limb itself. foot_l → [leg_l_upper, leg_l_lower, foot_l]; a hand
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
}
