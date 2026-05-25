using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// One bone's local transform in a saved pose, matched back to a live skeleton by
// Bone name (so reordering bones doesn't corrupt files). Plain properties so
// System.Text.Json round-trips it without custom converters.
public sealed class PoseBoneEntry
{
    public string Bone     { get; set; }
    public float  Tx       { get; set; }
    public float  Ty       { get; set; }
    public float  Rotation { get; set; }
    public float  Sx       { get; set; } = 1f;
    public float  Sy       { get; set; } = 1f;
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
            var t = pose.Local[i];
            list.Add(new PoseBoneEntry
            {
                Bone = pose.Skeleton.Bones[i].Name,
                Tx = t.Translation.X, Ty = t.Translation.Y,
                Rotation = t.Rotation,
                Sx = t.Scale.X, Sy = t.Scale.Y,
            });
        }
        return list;
    }

    // Apply onto a pose (resets to bind first; bones absent from the list stay at bind).
    public static void Apply(List<PoseBoneEntry> bones, SkeletonPose pose)
    {
        pose.SetToBind();
        if (bones == null) return;
        foreach (var e in bones)
        {
            int i = pose.Skeleton.IndexOf(e.Bone);
            if (i < 0) continue;
            pose.SetLocal(i, new BoneTransform(
                new Vector2(e.Tx, e.Ty), e.Rotation, new Vector2(e.Sx, e.Sy)));
        }
    }
}
