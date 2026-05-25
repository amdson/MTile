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

        // Bone segments: a line from the parent joint to this joint.
        for (int i = 0; i < bones.Length; i++)
        {
            Vector2 here = world[i].Translation;

            if (bones[i].Parent >= 0)
            {
                Vector2 parent = world[bones[i].Parent].Translation;
                ctx.Line(parent, here, style.BoneColor, style.BoneThickness);
            }

            // Orientation tick along the bone's local +X by its Length — shows
            // facing even for leaf bones that have no child segment.
            if (style.DrawAxes && bones[i].Length > 0f)
            {
                Vector2 tip = world[i].TransformPoint(new Vector2(bones[i].Length, 0f));
                ctx.Line(here, tip, style.AxisColor, 1f);
            }
        }

        // Joints drawn last so they read clearly on top of the segments.
        for (int i = 0; i < bones.Length; i++)
        {
            Color c = bones[i].IsRoot ? style.RootColor : style.JointColor;
            ctx.Disc(world[i].Translation, style.JointRadius, c);
        }
    }
}
