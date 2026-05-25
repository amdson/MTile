using System;
using Microsoft.Xna.Framework;

namespace MTile;

// The moving state of a skeleton: one local BoneTransform per bone plus a cached
// array of resolved world transforms. Render-only — nothing here feeds the sim.
//
// Typical use each render frame:
//     pose.BlendToward(targetPose, 1f - MathF.Exp(-stiffness * dt));  // procedural ease
//     var world = pose.ComputeWorld(root);                            // root = world placement
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
        SetToBind();
    }

    public int Count => Local.Length;

    // ---- pose setup / editing ------------------------------------------------

    public void SetToBind()
    {
        for (int i = 0; i < Local.Length; i++) Local[i] = Skeleton.Bones[i].Bind;
        _worldValid = false;
    }

    public void CopyFrom(SkeletonPose other)
    {
        if (other.Count != Count) throw new ArgumentException("Pose bone count mismatch.");
        Array.Copy(other.Local, Local, Count);
        _worldValid = false;
    }

    public void SetLocal(int bone, in BoneTransform t)
    {
        Local[bone] = t;
        _worldValid = false;
    }

    public void SetLocal(string bone, in BoneTransform t) => SetLocal(Skeleton.IndexOf(bone), t);

    // Rotate / translate / scale a single bone's local transform in place.
    public void Rotate(int bone, float deltaRadians)
    {
        Local[bone].Rotation += deltaRadians;
        _worldValid = false;
    }

    public void Translate(int bone, Vector2 delta)
    {
        Local[bone].Translation += delta;
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

    // In-place blend toward a target — the building block for spring / critically-
    // damped smoothing. Call each render frame with t = 1 - exp(-stiffness * dt)
    // for framerate-independent easing of the current pose toward `target`.
    public void BlendToward(SkeletonPose target, float t)
    {
        if (target.Count != Count) throw new ArgumentException("Pose bone count mismatch.");
        for (int i = 0; i < Count; i++)
            Local[i] = BoneTransform.Lerp(Local[i], target.Local[i], t);
        _worldValid = false;
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

    public Vector2 WorldPosition(int bone) => WorldOf(bone).Translation;
}
