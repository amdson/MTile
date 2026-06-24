using System;
using Microsoft.Xna.Framework;

namespace MTile;

// 2D affine transform stored as a 2x3 matrix (linear part + translation):
//
//     | M11 M12 Tx |   | x |
//     | M21 M22 Ty | * | y |
//     |  0   0  1  |   | 1 |
//
// This is the *world-space* representation a bone resolves to once its local TRS
// is composed down the hierarchy. Affine (not just rotation + uniform scale) so a
// parent's non-uniform squash/stretch shears its children correctly — the look you
// want for cartoony 2D rigs. Pure value type; render-only, never touches the sim.
public readonly struct Affine2
{
    public readonly float M11, M12, M21, M22, Tx, Ty;

    public Affine2(float m11, float m12, float m21, float m22, float tx, float ty)
    {
        M11 = m11; M12 = m12; M21 = m21; M22 = m22; Tx = tx; Ty = ty;
    }

    public static readonly Affine2 Identity = new(1f, 0f, 0f, 1f, 0f, 0f);

    // Transform composed as R * T * S (apply S, then T, then R). The
    // outer R rotates the translation along with the linear part, so a bone's local
    // Rotation rotates its joint position around the parent — the standard skeletal-
    // animation convention. Under this composition, pose.Rotation literally means
    // "this bone's angle relative to its parent": editing it rotates the bone's own
    // segment around the parent's joint and drags the subtree along, while leaving
    // siblings untouched.
    public static Affine2 FromTRS(Vector2 translation, float rotation, Vector2 scale)
    {
        float c = MathF.Cos(rotation), s = MathF.Sin(rotation);
        return new Affine2(
            c * scale.X, -s * scale.Y,
            s * scale.X,  c * scale.Y,
            c * translation.X - s * translation.Y,
            s * translation.X + c * translation.Y);
    }

    // Compose: Multiply(a, b) applies b first, then a — i.e. world = parentWorld * childLocal.
    public static Affine2 Multiply(in Affine2 a, in Affine2 b)
        => new(
            a.M11 * b.M11 + a.M12 * b.M21,
            a.M11 * b.M12 + a.M12 * b.M22,
            a.M21 * b.M11 + a.M22 * b.M21,
            a.M21 * b.M12 + a.M22 * b.M22,
            a.M11 * b.Tx + a.M12 * b.Ty + a.Tx,
            a.M21 * b.Tx + a.M22 * b.Ty + a.Ty);

    public static Affine2 operator *(Affine2 a, Affine2 b) => Multiply(a, b);

    // Transform a position (includes translation).
    public Vector2 TransformPoint(Vector2 p)
        => new(M11 * p.X + M12 * p.Y + Tx, M21 * p.X + M22 * p.Y + Ty);

    // Transform a direction / offset (ignores translation).
    public Vector2 TransformVector(Vector2 v)
        => new(M11 * v.X + M12 * v.Y, M21 * v.X + M22 * v.Y);

    // Inverse transform. Used to map a world/screen point back into a parent's
    // local space (e.g. an editor dragging a joint). Returns Identity if singular.
    public Affine2 Inverse()
    {
        float det = M11 * M22 - M12 * M21;
        if (MathF.Abs(det) < 1e-9f) return Identity;
        float inv = 1f / det;
        float a =  M22 * inv, b = -M12 * inv;
        float c = -M21 * inv, d =  M11 * inv;
        return new Affine2(a, b, c, d, -(a * Tx + b * Ty), -(c * Tx + d * Ty));
    }

    public Vector2 Translation => new(Tx, Ty);

    // Angle of the transformed local +X axis. Good for line/orientation drawing;
    // not meaningful under heavy shear, but fine for skeletons in practice.
    public float Angle => MathF.Atan2(M21, M11);
}

public struct BoneTransform
{
    public Vector2 Translation;
    public float   Rotation;   // radians; Y-down, so +angle rotates clockwise on screen
    public Vector2 Scale;

    public BoneTransform(Vector2 translation, float rotation, Vector2 scale)
    {
        Translation = translation; Rotation = rotation; Scale = scale;
    }

    public BoneTransform(Vector2 translation, float rotation)
        : this(translation, rotation, Vector2.One) { }

    public static BoneTransform Identity => new(Vector2.Zero, 0f, Vector2.One);

    // Local bone transform as R · T · S (apply S, then T, then R) 
    public Affine2 ToAffine()
    {
        float c = MathF.Cos(Rotation), s = MathF.Sin(Rotation);
        float dx = Translation.X, dy = Translation.Y;
        float rot_dx = c * dx - s * dy, rot_dy = s * dx + c * dy;
        return new Affine2(c * Scale.X, -s * Scale.Y, s * Scale.X, c * Scale.Y, rot_dx, rot_dy);
    }

    // Per-component blend; rotation takes the shortest angular path.
    public static BoneTransform Lerp(in BoneTransform a, in BoneTransform b, float t)
        => new(
            Vector2.Lerp(a.Translation, b.Translation, t),
            LerpAngle(a.Rotation, b.Rotation, t),
            Vector2.Lerp(a.Scale, b.Scale, t));
            
    // Shortest-path angular interpolation (WrapAngle keeps the delta in [-pi, pi]).
    public static float LerpAngle(float a, float b, float t)
        => a + MathHelper.WrapAngle(b - a) * t;
}
