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
    // Length multiplier on the rig bone's Length for this keyframe — pseudo-3D
    // foreshortening (a pelvis strut shrinking as the hips yaw about the implicit
    // depth axis). May go slightly negative (a strut swung past the depth axis).
    // Null = 1 (unstretched); omitted from JSON so legacy files round-trip clean.
    public float? Stretch  { get; set; }
}

// Convert between a live SkeletonPose and the serializable per-bone list. Shared by
// single poses and animation keyframes.
public static class PoseData
{
    public static List<PoseBoneEntry> Capture(SkeletonPose pose)
    {
        var list = new List<PoseBoneEntry>(pose.Count);
        for (int i = 0; i < pose.Count; i++)
        {
            // Recover the stretch from the local translation (always along local ±X by
            // construction). Free-form editor translations (TRANSLATE drag) keep their
            // legacy fate — only the axial component persists, as a stretch.
            float length   = pose.Skeleton.Bones[i].Length;
            float? stretch = null;
            if (length > 1e-4f)
            {
                float s = pose.Local[i].Translation.X / length;
                if (MathF.Abs(s - 1f) > 1e-4f) stretch = s;
            }
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
            float length = pose.Skeleton.Bones[i].Length * (e.Stretch ?? 1f);
            pose.SetLocal(i, new BoneTransform(Vector2.UnitX * length, e.Rotation, Vector2.One));
        }
    }
}
