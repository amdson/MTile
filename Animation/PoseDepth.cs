using System;
using Microsoft.Xna.Framework;

namespace MTile;

// The pseudo-3D reading of a 2D pose, for the editor's depth view (authoring only — never
// runs in the game loop). A bone's Stretch s (against Bone.StretchLength) and its keyframe
// DepthSide give it a 3D direction:
//     d = (s·cos a, s·sin a, side·√(1 − s²))
// where a is the bone axis's screen angle (root-local) and +z points toward the viewer.
// Its TILT is the angle of d in the plane spanned by its screen axis and z:
//     tilt = atan2(z, s)  — 0 in the screen plane, +90° straight at the viewer,
//                           ±180° pointing back along its own axis (s = −1).
// Stretch stays per bone (a child's foreshortening is its own, not inherited); turning a
// bone "with children" is an EDIT that rotates the descendants' 3D directions along with it
// and writes their projected Stretch / rotation / side back, so the stored data stays flat.
//
// A |s| > 1 key (legacy over-length) has no depth angle: it reads as in-plane (z = 0) and
// turning it replaces the value with cos(tilt).
public static class PoseDepth
{
    public const float SideEps = 1e-3f;   // |z| below this: in the plane, side = null

    public static float StretchOf(SkeletonPose pose, int i)
    {
        float len = pose.Skeleton.Bones[i].StretchLength;
        return len > 1e-4f ? pose.Local[i].Translation.X / len : 1f;
    }

    public static float Tilt(float stretch, int? side)
    {
        float z = Depth(stretch, side);
        return MathF.Atan2(z, stretch);
    }

    public static float Tilt(SkeletonPose pose, int i, int? side) => Tilt(StretchOf(pose, i), side);

    // z of the unit direction: how far toward (+) or away from (−) the viewer it leans.
    public static float Depth(float stretch, int? side)
        => (side ?? 1) * MathF.Sqrt(MathF.Max(0f, 1f - stretch * stretch));

    public static int? SideOf(float z) => z > SideEps ? 1 : z < -SideEps ? -1 : null;

    // Screen angle of each bone's local +X axis, root-local (the identity root). Bones are
    // pure chains with unit scale, so a child's angle is its parent's plus its own rotation.
    public static float[] AxisAngles(SkeletonPose pose)
    {
        var world = pose.ComputeWorld(Affine2.Identity);
        var a = new float[world.Length];
        for (int i = 0; i < a.Length; i++)
        {
            Vector2 ax = world[i].TransformVector(Vector2.UnitX);
            a[i] = MathF.Atan2(ax.Y, ax.X);
        }
        return a;
    }

    // Each joint (bone tip) in 3D, root-local rig units, z toward the viewer. `sides` is per
    // bone index (null entries / array = toward).
    public static Vector3[] Joints(SkeletonPose pose, int?[] sides)
    {
        var rig = pose.Skeleton;
        var a = AxisAngles(pose);
        var p = new Vector3[rig.Count];
        for (int i = 0; i < rig.Count; i++)
        {
            var b = rig.Bones[i];
            Vector3 from = b.Parent >= 0 ? p[b.Parent] : Vector3.Zero;
            p[i] = from + BoneVector(pose, i, a[i], sides?[i]);
        }
        return p;
    }

    private static Vector3 BoneVector(SkeletonPose pose, int i, float angle, int? side)
    {
        var b = pose.Skeleton.Bones[i];
        float tx = pose.Local[i].Translation.X;
        if (b.StretchLength <= 1e-4f) return new Vector3(tx * MathF.Cos(angle), tx * MathF.Sin(angle), 0f);
        float s = tx / b.StretchLength;
        return b.StretchLength * new Vector3(s * MathF.Cos(angle), s * MathF.Sin(angle), Depth(s, side));
    }

    // Set `bone`'s tilt. Its screen rotation is kept; its Stretch becomes cos(tilt) and its
    // side the sign of sin(tilt). With `withChildren`, every descendant's 3D direction turns
    // by the same angle about the in-screen axis perpendicular to `bone` (what turning the
    // shoulder does to the forearm), and each is projected back to rotation + Stretch + side.
    // Mutates `pose` and `sides` (per bone index; must be rig-sized).
    public static void Turn(SkeletonPose pose, int bone, float tilt, bool withChildren, int?[] sides)
    {
        var rig = pose.Skeleton;
        var bb = rig.Bones[bone];
        if (bb.StretchLength <= 1e-4f) return;   // zero length: nothing to tilt
        float[] a = AxisAngles(pose);
        float delta = tilt - Tilt(pose, bone, sides[bone]);

        // Descendants' unit directions BEFORE the selected bone changes (the z of a child
        // doesn't depend on its parent's, but compute everything from one consistent pose).
        Vector3[] d = null;
        if (withChildren)
        {
            d = new Vector3[rig.Count];
            for (int i = bone + 1; i < rig.Count; i++)
            {
                if (!IsDescendant(rig, i, bone)) continue;
                var b = rig.Bones[i];
                if (b.StretchLength <= 1e-4f) { d[i] = new Vector3(MathF.Cos(a[i]), MathF.Sin(a[i]), 0f); continue; }
                float s = StretchOf(pose, i);
                d[i] = new Vector3(s * MathF.Cos(a[i]), s * MathF.Sin(a[i]), Depth(s, sides[i]));
            }
        }

        float c = MathF.Cos(tilt), sn = MathF.Sin(tilt);
        pose.Local[bone].Translation = Vector2.UnitX * (c * bb.StretchLength);
        sides[bone] = SideOf(sn);
        if (!withChildren) return;

        var e1 = new Vector3(MathF.Cos(a[bone]), MathF.Sin(a[bone]), 0f);
        float cd = MathF.Cos(delta), sd = MathF.Sin(delta);
        var na = (float[])a.Clone();   // new axis angles, filled parent-first
        for (int i = bone + 1; i < rig.Count; i++)
        {
            if (!IsDescendant(rig, i, bone)) continue;
            var b = rig.Bones[i];
            Vector3 v = d[i];
            float x1 = Vector3.Dot(v, e1), z = v.Z;
            Vector3 perp = v - x1 * e1 - z * Vector3.UnitZ;
            Vector3 r = perp + (x1 * cd - z * sd) * e1 + (x1 * sd + z * cd) * Vector3.UnitZ;

            float m = MathF.Sqrt(r.X * r.X + r.Y * r.Y);
            na[i] = m > 1e-4f ? MathF.Atan2(r.Y, r.X) : a[i];
            float target = na[i] - na[b.Parent];
            float cur = pose.Local[i].Rotation;
            pose.Local[i].Rotation = cur + MathHelper.WrapAngle(target - cur);
            if (b.StretchLength > 1e-4f)
            {
                pose.Local[i].Translation = Vector2.UnitX * (m * b.StretchLength);
                sides[i] = SideOf(r.Z);
            }
        }
    }

    public static bool IsDescendant(Skeleton rig, int i, int ancestor)
    {
        for (int p = rig.Bones[i].Parent; p >= 0; p = rig.Bones[p].Parent)
            if (p == ancestor) return true;
        return false;
    }
}
