using Microsoft.Xna.Framework;

namespace MTile;

// Visual options for the stick-figure renderer.
public struct SkeletonDrawStyle
{
    public Color BoneColor;
    public Color JointColor;
    public Color RootColor;
    public Color AxisColor;     // local +X tick of length Bone.Length; skipped if Length <= 0
    public float BoneThickness;
    public float JointRadius;
    public bool  DrawAxes;

    public static SkeletonDrawStyle Default => new()
    {
        BoneColor     = Color.White,
        JointColor    = Color.OrangeRed,
        RootColor     = Color.Yellow,
        AxisColor     = new Color(120, 200, 255),
        BoneThickness = 2f,
        JointRadius   = 2.5f,
        DrawAxes      = false,
    };
}

// Stick-figure rendering of a skeleton through the existing DrawContext line/disc
// primitives. A debug / V1 view: once bones carry real art (baked-texture limbs or
// a skinned mesh) this gets replaced, but the Skeleton + SkeletonPose code is
// unaffected. Render-only.
public static class SkeletonRenderer
{
    public static void Draw(DrawContext ctx, SkeletonPose pose, in Affine2 root)
        => Draw(ctx, pose, root, SkeletonDrawStyle.Default);

    public static void Draw(DrawContext ctx, SkeletonPose pose, in Affine2 root, in SkeletonDrawStyle style)
    {
        var world = pose.ComputeWorld(root);
        var bones = pose.Skeleton.Bones;

        // Bone segments: each bone is its OWN body, joint → tip (Length along local +X). The rig
        // is a joint chain (child.joint == parent.tip exactly), so these connect end-to-end with
        // no gaps or duplicates — and every leaf (head, forearms, feet) draws, which a
        // parent.joint→this.joint pass would miss.
        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i].Length <= 0f) continue;   // a length-0 root / marker has no segment
            Vector2 here = world[i].Translation;
            Vector2 tip  = world[i].TransformPoint(new Vector2(bones[i].Length, 0f));
            ctx.Line(here, tip, style.BoneColor, style.BoneThickness);
        }

        // Joints drawn last so they read clearly on top of the segments. Skipped
        // entirely when JointRadius <= 0 (hosts hide the nodes that way).
        if (style.JointRadius > 0f)
            for (int i = 0; i < bones.Length; i++)
            {
                Color c = bones[i].IsRoot ? style.RootColor : style.JointColor;
                ctx.Disc(world[i].Translation, style.JointRadius, c);
            }
    }
}
