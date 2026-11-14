using Microsoft.Xna.Framework;

namespace MTile;

// Visual options for the stick-figure renderer.
public struct SkeletonDrawStyle
{
    public Color BoneColor;
    public Color JointColor;
    public Color RootColor;
    public float BoneThickness;
    public float JointRadius;
    // Optional side coding: when set, joints of left-side bones (name has "_l") draw in
    // LeftJointColor and right-side ones ("_r") in RightJointColor. Null = JointColor.
    public Color? LeftJointColor;
    public Color? RightJointColor;

    // Blue = left (the far side on strutted rigs), red = right (the near side).
    public static readonly Color SideLeft  = new(70, 150, 255);
    public static readonly Color SideRight = new(255, 70, 70);

    // -1 left, +1 right, 0 centre — from the bone-name convention (arm_l_upper, hip_r, foot_l).
    public static int SideOf(string bone)
        => bone.EndsWith("_l") || bone.Contains("_l_") ? -1
         : bone.EndsWith("_r") || bone.Contains("_r_") ? +1 : 0;

    public readonly Color JointColorFor(string bone) => SideOf(bone) switch
    {
        -1 => LeftJointColor  ?? JointColor,
        +1 => RightJointColor ?? JointColor,
        _  => JointColor,
    };

    public static SkeletonDrawStyle Default => new()
    {
        BoneColor     = Color.White,
        JointColor    = Color.OrangeRed,
        RootColor     = Color.Yellow,
        BoneThickness = 1f,
        JointRadius   = 2.5f,
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

        // Bone segments: under the R·T·S joint chain a bone's Length is baked into its world
        // translation, so world[i].Translation IS bone i's tip (where its children attach) and
        // world[parent].Translation is this bone's near joint. Each bone is therefore exactly the
        // segment parent.tip → this.tip. Every non-root bone draws — leaves included — with shared
        // joints coinciding (no gaps or duplicates). The root has no incoming segment; length-0
        // bones (markers like a clip's knife) collapse to a point and are skipped.
        for (int i = 0; i < bones.Length; i++)
        {
            int parent = bones[i].Parent;
            if (parent < 0 || bones[i].Length <= 0f) continue;
            ctx.Line(world[parent].Translation, world[i].Translation, style.BoneColor, style.BoneThickness);
        }

        // Joints drawn last so they read clearly on top of the segments. Skipped
        // entirely when JointRadius <= 0 (hosts hide the nodes that way).
        if (style.JointRadius > 0f)
            for (int i = 0; i < bones.Length; i++)
            {
                Color c = bones[i].IsRoot ? style.RootColor : style.JointColorFor(bones[i].Name);
                ctx.Disc(world[i].Translation, style.JointRadius, c);
            }
    }
}
