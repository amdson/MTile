using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// One bone's per-keyframe pose data. The only authored channel is Rotation —
// translation, scale, and bone length live in the rig (Skeletons/<name>.json) and
// are shared by every animation, so all clips draw against the same figure. Stale
// Tx/Ty/Sx/Sy properties in old JSON files are silently ignored on load (unknown
// members) and scrubbed on the next save.
public sealed class PoseBoneEntry
{
    public string Bone     { get; set; }
    public float  Rotation { get; set; }
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
            list.Add(new PoseBoneEntry
            {
                Bone     = pose.Skeleton.Bones[i].Name,
                Rotation = pose.Local[i].Rotation,
            });
        }
        return list;
    }

    // Apply onto a pose (resets to default first; bones absent from the list stay at
    // their rest pose). Rotation comes from the keyframe; the bone's length (its local
    // +X offset) comes from the shared skeleton — the rig is the single source of truth
    // for proportions, so a rig edit shows in every clip.
    public static void Apply(List<PoseBoneEntry> bones, SkeletonPose pose)
    {
        pose.SetToDefault();
        if (bones == null) return;
        foreach (var e in bones)
        {
            int i = pose.Skeleton.IndexOf(e.Bone);
            if (i < 0) continue;
            float length = pose.Skeleton.Bones[i].Length;
            pose.SetLocal(i, new BoneTransform(Vector2.UnitX * length, e.Rotation, Vector2.One));
        }
    }
}
