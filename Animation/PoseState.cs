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

    // Apply onto a pose (resets to bind first; bones absent from the list stay at
    // bind). Rotation comes from the keyframe; translation and scale always come
    // from the shared skeleton's bind — the rig is the single source of truth for
    // proportions, so a rig edit shows in every clip.
    public static void Apply(List<PoseBoneEntry> bones, SkeletonPose pose)
    {
        pose.SetToBind();
        if (bones == null) return;
        foreach (var e in bones)
        {
            int i = pose.Skeleton.IndexOf(e.Bone);
            if (i < 0) continue;
            var bind = pose.Skeleton.Bones[i].Bind;
            pose.SetLocal(i, new BoneTransform(bind.Translation, e.Rotation, bind.Scale));
        }
    }
}
