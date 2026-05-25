using Microsoft.Xna.Framework;

namespace MTile;

// Example rigs to exercise the skeleton utilities. Pure construction helpers — not
// wired into the game. Coordinates are Y-down (up is -Y), local to each parent.
public static class SkeletonExamples
{
    // A minimal humanoid stick figure: hip root, spine→chest→head, two arms, two
    // legs. Joint positions alone define the silhouette (segments connect parent→
    // child), so all rotations are 0 in the bind pose. Returns a fresh Skeleton;
    // call CreatePose() to get an editable pose.
    public static Skeleton Biped()
    {
        var b = new SkeletonBuilder();

        int hip   = b.AddRoot("hip",   BoneTransform.Identity);
        int chest = b.Add("chest", hip,   new BoneTransform(new Vector2(0f, -16f), 0f), 16f);
        b.Add("head",  chest, new BoneTransform(new Vector2(0f, -10f), 0f), 8f);

        int armLU = b.Add("arm_l_upper", chest, new BoneTransform(new Vector2(-7f, -2f), 0f), 10f);
        b.Add("arm_l_lower", armLU, new BoneTransform(new Vector2(-10f, 0f), 0f), 10f);

        int armRU = b.Add("arm_r_upper", chest, new BoneTransform(new Vector2(7f, -2f), 0f), 10f);
        b.Add("arm_r_lower", armRU, new BoneTransform(new Vector2(10f, 0f), 0f), 10f);

        int legLU = b.Add("leg_l_upper", hip, new BoneTransform(new Vector2(-5f, 2f), 0f), 12f);
        b.Add("leg_l_lower", legLU, new BoneTransform(new Vector2(-1f, 14f), 0f), 12f);

        int legRU = b.Add("leg_r_upper", hip, new BoneTransform(new Vector2(5f, 2f), 0f), 12f);
        b.Add("leg_r_lower", legRU, new BoneTransform(new Vector2(1f, 14f), 0f), 12f);

        return b.Build();
    }
}
