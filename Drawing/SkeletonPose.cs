using System;
using Microsoft.Xna.Framework;

namespace MTile;

// The moving state of a skeleton: one local BoneTransform per bone plus a cached
// array of resolved world transforms. Render-only — nothing here feeds the sim.
//
// Typical use each render frame — the pose arrives already solved (CharacterAnimator does
// its easing INSIDE the solve; there is no procedural blend step here any more):
//     var world = pose.ComputeWorld(root);   // root = world placement
//     SkeletonRenderer.Draw(ctx, pose, root);
public sealed class SkeletonPose
{
    public readonly Skeleton        Skeleton;
    public readonly BoneTransform[] Local;    // editable per-bone local transforms
    private readonly Affine2[]      _world;    // resolved by ComputeWorld
    private bool _worldValid;

    public SkeletonPose(Skeleton skeleton)
    {
        Skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));
        Local  = new BoneTransform[skeleton.Count];
        _world = new Affine2[skeleton.Count];
        SetToDefault();
    }

    public int Count => Local.Length;

    // ---- pose setup / editing ------------------------------------------------
    // set to default theta from skeleton bone rotations
    public void SetToDefault()
    {
        for (int i = 0; i < Local.Length; i++)
        {
            float rotation = Skeleton.Bones[i].Rotation;
            float length  = Skeleton.Bones[i].Length;
            Local[i] = new BoneTransform(Vector2.UnitX*length, rotation, Vector2.One);
        }
        _worldValid = false;
    }

    public void CopyFrom(SkeletonPose other)
    {
        if (other.Count != Count) throw new ArgumentException("Pose bone count mismatch.");
        Array.Copy(other.Local, Local, Count);
        _worldValid = false;
    }

    // ---- recorder capture (render-only) --------------------------------------
    // Value-copy of just the local bone transforms — enough to redraw this exact pose
    // later (ComputeWorld re-derives world from a root). Used by the in-game animation
    // recorder to store the composed pose per frame for stable back-and-forth scrubbing.
    public BoneTransform[] CloneLocal() => (BoneTransform[])Local.Clone();

    // Overwrite the local transforms from a CloneLocal() capture and invalidate world.
    public void LoadLocal(BoneTransform[] src)
    {
        if (src.Length != Count) throw new ArgumentException("Pose bone count mismatch.");
        Array.Copy(src, Local, Count);
        _worldValid = false;
    }

    public void SetLocal(int bone, in BoneTransform t)
    {
        Local[bone] = t;
        _worldValid = false;
    }


    // ---- interpolation -------------------------------------------------------

    // Per-bone blend of two source poses into `dest`. t = 0 → a, t = 1 → b.
    // The three poses must share the same skeleton (same bone count).
    public static void Lerp(SkeletonPose a, SkeletonPose b, float t, SkeletonPose dest)
    {
        if (a.Count != b.Count || a.Count != dest.Count)
            throw new ArgumentException("Pose bone count mismatch.");
        for (int i = 0; i < dest.Count; i++)
            dest.Local[i] = BoneTransform.Lerp(a.Local[i], b.Local[i], t);
        dest._worldValid = false;
    }


    // ---- world resolution ----------------------------------------------------

    // Resolve every bone's world transform under a `root` placement (the whole-
    // skeleton translate/rotate/scale/flip — build it with Affine2.FromTRS, e.g.
    // a (-1, 1) scale to mirror for facing). Single forward pass thanks to the
    // topological bone order. Returns the internal buffer; do not retain it across
    // frames (it is overwritten on the next call).
    public Affine2[] ComputeWorld(in Affine2 root)
    {
        for (int i = 0; i < Local.Length; i++)
        {
            var local  = Local[i].ToAffine();
            int parent = Skeleton.Bones[i].Parent;
            _world[i]  = parent < 0 ? root * local : _world[parent] * local;
        }
        _worldValid = true;
        return _world;
    }

    public Affine2 WorldOf(int bone)
    {
        if (!_worldValid)
            throw new InvalidOperationException("Call ComputeWorld before reading world transforms.");
        return _world[bone];
    }

}
