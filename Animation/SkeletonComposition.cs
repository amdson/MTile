using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Layers clip-local ExtraBones (see AnimationDocument.ExtraBones) onto a base rig.
// The base Skeletons/<name>.json stays minimal; bones that belong only to a specific
// attack (a held knife, a spear tip) live in that clip's file and are composed in here.
// Two consumers: the editor layers the *active* clip's bones so walk/idle don't show a
// knife; the runtime layers the *union* across all bound clips so TryBoneOrigin and the
// rig can resolve any attachment a slash uses. Pure / render-only.
public static class SkeletonComposition
{
    // Append extra bones onto `baseRig`. Each must Parent an existing bone (the base or
    // one added earlier in this call). Bones whose name already exists are skipped (union
    // dedup); a bone whose Parent can't be resolved is skipped rather than throwing, so a
    // stale attachment in one clip can't break the whole rig. Returns `baseRig` unchanged
    // when there's nothing to add.
    public static Skeleton Compose(Skeleton baseRig, IEnumerable<SkeletonBoneRecord> extras)
    {
        if (extras == null) return baseRig;
        var rig = baseRig;
        foreach (var r in extras)
        {
            if (r?.Name == null || rig.IndexOf(r.Name) >= 0) continue;
            // Attachments must hang off an existing bone — a clip-local root makes no
            // sense (it would float free of the body). Skip a null/unknown parent.
            if (r.Parent == null) continue;
            int parent = rig.IndexOf(r.Parent);
            if (parent < 0) continue;
            var bind = new BoneTransform(new Vector2(r.Tx, r.Ty), r.Rotation, new Vector2(r.Sx, r.Sy));
            rig = rig.WithBone(r.Name, parent, bind, r.Length);
        }
        return rig;
    }

    // Runtime rig: base ∪ the extra bones of every clip authored against this rig. A clip
    // that doesn't declare extras contributes nothing; one that does adds its bones once
    // (dedup by name across clips). Clips for other rigs are ignored, mirroring how
    // CharacterAnimator filters its bindings by Skeleton name.
    public static Skeleton WithClipBones(Skeleton baseRig, IEnumerable<AnimationDocument> clips)
    {
        if (clips == null) return baseRig;
        var rig = baseRig;
        foreach (var c in clips)
        {
            if (c?.ExtraBones == null || c.ExtraBones.Count == 0) continue;
            if (c.Skeleton != baseRig.Name) continue;
            rig = Compose(rig, c.ExtraBones);
        }
        return rig;
    }
}
