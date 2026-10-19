using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// One bone's per-keyframe pose data. The authored channels are Rotation and an
// optional length Stretch — translation direction, scale, and the base bone length
// live in the rig (Skeletons/<name>.json) and are shared by every animation, so all
// clips draw against the same figure. Stale Tx/Ty/Sx/Sy properties in old JSON files
// are silently ignored on load (unknown members) and scrubbed on the next save.
public sealed class PoseBoneEntry
{
    public string Bone     { get; set; }
    public float  Rotation { get; set; }
    // Length multiplier for this keyframe — pseudo-3D foreshortening, measured against the
    // rig bone's StretchLength (its TrueLength, else its drawn Length). Within [-1, 1] it
    // is cos of the bone's tilt out of the screen plane; negative = swung past the depth
    // axis (a strut whose socket crosses the centerline). The editor clamps to [-1, 1];
    // older keys above 1 still load and draw. Null = the drawn Length (unstretched);
    // omitted from JSON so legacy files round-trip clean.
    public float? Stretch  { get; set; }
    // Which side of the screen plane a foreshortened bone leans: +1 its far end comes
    // toward the viewer, -1 it goes away. Null = unset (reads as toward). Authoring data
    // for the editor's depth view and IK; nothing at runtime reads it yet.
    public int?   DepthSide { get; set; }

    public PoseBoneEntry Clone() => new() { Bone = Bone, Rotation = Rotation, Stretch = Stretch, DepthSide = DepthSide };
}

// Convert between a live SkeletonPose and the serializable per-bone list. Shared by
// single poses and animation keyframes.
public static class PoseData
{
    // Capture, carrying each bone's DepthSide over from `previous` (the keyframe being
    // re-captured): the side is not part of a SkeletonPose, so a plain Capture drops it.
    public static List<PoseBoneEntry> Capture(SkeletonPose pose, List<PoseBoneEntry> previous)
    {
        var list = Capture(pose);
        if (previous == null) return list;
        foreach (var e in list)
        {
            var old = previous.Find(p => p.Bone == e.Bone);
            if (old != null) e.DepthSide = old.DepthSide;
        }
        return list;
    }

    public static List<PoseBoneEntry> Capture(SkeletonPose pose)
    {
        var list = new List<PoseBoneEntry>(pose.Count);
        for (int i = 0; i < pose.Count; i++)
        {
            // Recover the stretch from the local translation (always along local ±X by
            // construction). Free-form editor translations (TRANSLATE drag) keep their
            // legacy fate — only the axial component persists, as a stretch.
            var bone       = pose.Skeleton.Bones[i];
            float length   = bone.StretchLength;
            float? stretch = null;
            if (length > 1e-4f && MathF.Abs(pose.Local[i].Translation.X - bone.Length) > 1e-4f * length)
                stretch = pose.Local[i].Translation.X / length;
            list.Add(new PoseBoneEntry
            {
                Bone     = pose.Skeleton.Bones[i].Name,
                Rotation = pose.Local[i].Rotation,
                Stretch  = stretch,
            });
        }
        return list;
    }

    // Apply onto a pose (resets to default first; bones absent from the list stay at
    // their rest pose). Rotation and Stretch come from the keyframe; the bone's base
    // length (its local +X offset) comes from the shared skeleton — the rig is the
    // single source of truth for proportions, so a rig edit shows in every clip.
    public static void Apply(List<PoseBoneEntry> bones, SkeletonPose pose)
    {
        pose.SetToDefault();
        if (bones == null) return;
        foreach (var e in bones)
        {
            int i = pose.Skeleton.IndexOf(e.Bone);
            if (i < 0) continue;
            var bone = pose.Skeleton.Bones[i];
            float length = e.Stretch.HasValue ? bone.StretchLength * e.Stretch.Value : bone.Length;
            pose.SetLocal(i, new BoneTransform(Vector2.UnitX * length, e.Rotation, Vector2.One));
        }
    }
}
