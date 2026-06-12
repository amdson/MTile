namespace MTile;

// Resolves an AnimRegion to a concrete per-bone membership mask for a skeleton.
// Construction-time only (allocates the bool[]); callers cache the result — the
// CharacterAnimator builds one mask per region at construction and indexes them
// by (int)region per frame.
public static class BoneMask
{
    // bool[skeleton.Count]; true = bone owned by the region.
    //   UpperBody = the "chest" bone and all its descendants (head + both arms).
    //   LowerBody = the complement (hip/root + legs + feet).
    //   FullBody  = all bones.
    // A rig without a "chest" bone resolves UpperBody to all-false, so an
    // upper-body overlay on such a rig is a no-op rather than an error.
    public static bool[] Resolve(Skeleton s, AnimRegion region)
    {
        var mask = new bool[s.Count];
        if (region == AnimRegion.FullBody)
        {
            for (int i = 0; i < mask.Length; i++) mask[i] = true;
            return mask;
        }

        // Skeleton guarantees parents precede children, so chest-subtree
        // membership falls out of a single forward pass.
        int chest = s.IndexOf("chest");
        var upper = new bool[s.Count];
        for (int i = 0; i < s.Count; i++)
        {
            int parent = s.Bones[i].Parent;
            upper[i] = i == chest || (parent >= 0 && upper[parent]);
        }

        bool want = region == AnimRegion.UpperBody;
        for (int i = 0; i < mask.Length; i++) mask[i] = upper[i] == want;
        return mask;
    }
}
