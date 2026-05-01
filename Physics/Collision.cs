using System;
using Microsoft.Xna.Framework;

namespace MTile;

// SAT-based collision detection for convex polygons and circles.
// All methods return a CollisionResult whose MTV pushes A out of B when Intersects is true.
public static class Collision
{
    // Convex polygon vs convex polygon.
    public static CollisionResult Check(
        Polygon a, Vector2 posA, float rotA,
        Polygon b, Vector2 posB, float rotB)
    {
        var vertsA = a.GetVertices(posA, rotA);
        var vertsB = b.GetVertices(posB, rotB);

        float minDepth = float.MaxValue;
        Vector2 mtvAxis = Vector2.Zero;

        foreach (var axis in a.GetAxes(vertsA))
        {
            if (!TryGetOverlap(vertsA, vertsB, axis, ref minDepth, ref mtvAxis))
                return CollisionResult.None;
        }

        foreach (var axis in b.GetAxes(vertsB))
        {
            if (!TryGetOverlap(vertsA, vertsB, axis, ref minDepth, ref mtvAxis))
                return CollisionResult.None;
        }

        // Orient MTV so it points from B toward A.
        if (Vector2.Dot(posA - posB, mtvAxis) < 0f)
            mtvAxis = -mtvAxis;

        return new CollisionResult(true, mtvAxis * minDepth, minDepth);
    }

    // Circle vs convex polygon.
    public static CollisionResult CheckCircle(
        Vector2 center, float radius,
        Polygon polygon, Vector2 polyPos, float polyRot)
    {
        var verts = polygon.GetVertices(polyPos, polyRot);

        float minDepth = float.MaxValue;
        Vector2 mtvAxis = Vector2.Zero;

        // Edge-normal axes from the polygon.
        foreach (var axis in polygon.GetAxes(verts))
        {
            float circleMin = Vector2.Dot(center, axis) - radius;
            float circleMax = circleMin + radius * 2f;
            var (polyMin, polyMax) = Polygon.Project(verts, axis);

            float overlap = MathF.Min(circleMax, polyMax) - MathF.Max(circleMin, polyMin);
            if (overlap <= 0f) return CollisionResult.None;

            if (overlap < minDepth)
            {
                minDepth = overlap;
                mtvAxis = axis;
            }
        }

        // Axis from the circle center to the nearest polygon vertex — handles corner cases.
        var nearest = NearestVertex(center, verts);
        var vertAxis = center - nearest;
        if (vertAxis != Vector2.Zero)
        {
            vertAxis = Vector2.Normalize(vertAxis);
            float circleMin = Vector2.Dot(center, vertAxis) - radius;
            float circleMax = circleMin + radius * 2f;
            var (polyMin, polyMax) = Polygon.Project(verts, vertAxis);

            float overlap = MathF.Min(circleMax, polyMax) - MathF.Max(circleMin, polyMin);
            if (overlap <= 0f) return CollisionResult.None;

            if (overlap < minDepth)
            {
                minDepth = overlap;
                mtvAxis = vertAxis;
            }
        }

        if (Vector2.Dot(center - polyPos, mtvAxis) < 0f)
            mtvAxis = -mtvAxis;

        return new CollisionResult(true, mtvAxis * minDepth, minDepth);
    }

    // Fast AABB check — delegates to MonoGame's Rectangle.Intersects.
    public static bool CheckAABB(Rectangle a, Rectangle b) => a.Intersects(b);

    // Circle vs circle.
    public static CollisionResult CheckCircles(Vector2 centerA, float radiusA, Vector2 centerB, float radiusB)
    {
        var delta = centerA - centerB;
        float distSq = delta.LengthSquared();
        float radSum = radiusA + radiusB;

        if (distSq >= radSum * radSum) return CollisionResult.None;

        float dist = MathF.Sqrt(distSq);
        float depth = radSum - dist;
        var mtv = dist > 0f ? delta / dist * depth : new Vector2(depth, 0f);
        return new CollisionResult(true, mtv, depth);
    }

    // Returns true if there is overlap on this axis and updates minDepth/mtvAxis accordingly.
    private static bool TryGetOverlap(
        Vector2[] vertsA, Vector2[] vertsB, Vector2 axis,
        ref float minDepth, ref Vector2 mtvAxis)
    {
        var (minA, maxA) = Polygon.Project(vertsA, axis);
        var (minB, maxB) = Polygon.Project(vertsB, axis);

        float overlap = MathF.Min(maxA, maxB) - MathF.Max(minA, minB);
        if (overlap <= 0f) return false;

        if (overlap < minDepth)
        {
            minDepth = overlap;
            mtvAxis = axis;
        }
        return true;
    }

    // Swept convex polygon A translating by displacement vs static convex polygon B.
    // Returns the first time of contact T in [0,1] and the surface normal at that contact.
    // Normal points from B toward A (i.e. pushes A away). T=0 with Normal=Zero means already overlapping.
    public static SweptResult Swept(
        Polygon a, Vector2 posA, float rotA,
        Vector2 displacement,
        Polygon b, Vector2 posB, float rotB)
    {
        var vertsA = a.GetVertices(posA, rotA);
        var vertsB = b.GetVertices(posB, rotB);

        float tFirst = 0f;
        float tLast = 1f;
        Vector2 contactNormal = Vector2.Zero;

        if (!TestSweptAxes(a.GetAxes(vertsA), vertsA, vertsB, displacement, ref tFirst, ref tLast, ref contactNormal))
            return SweptResult.NoHit;
        if (!TestSweptAxes(b.GetAxes(vertsB), vertsA, vertsB, displacement, ref tFirst, ref tLast, ref contactNormal))
            return SweptResult.NoHit;

        if (tFirst > tLast || tFirst > 1f) return SweptResult.NoHit;

        // Orient normal to point from B toward A at contact time.
        var contactPos = posA + displacement * MathF.Max(tFirst, 0f);
        if (contactNormal != Vector2.Zero && Vector2.Dot(contactPos - posB, contactNormal) < 0f)
            contactNormal = -contactNormal;

        return new SweptResult(true, MathF.Max(tFirst, 0f), contactNormal);
    }

    // Tests all axes for swept overlap, updating tFirst/tLast and the contact normal.
    // Returns false immediately if any axis is permanently separating.
    private static bool TestSweptAxes(
        Vector2[] axes, Vector2[] vertsA, Vector2[] vertsB,
        Vector2 displacement,
        ref float tFirst, ref float tLast, ref Vector2 contactNormal)
    {
        for (int i = 0; i < axes.Length; i++)
        {
            var axis = axes[i];
            var (minA, maxA) = Polygon.Project(vertsA, axis);
            var (minB, maxB) = Polygon.Project(vertsB, axis);
            float vn = Vector2.Dot(displacement, axis);

            if (MathF.Abs(vn) < 1e-6f)
            {
                // If velocity along this axis is zero, the objects must be overlapping to collide.
                // An exact touch (maxA == minB or minA == maxB) is not a deep overlap,
                // so we consider them separated during this sweep.
                if (maxA <= minB || minA >= maxB) return false;
                continue;                                      // stationary and overlapping: no constraint
            }

            // Time interval [t0, t1] during which A and B overlap on this axis.
            float t0 = (minB - maxA) / vn;
            float t1 = (maxB - minA) / vn;
            if (t0 > t1) (t0, t1) = (t1, t0);

            if (t0 > tFirst) { tFirst = t0; contactNormal = axis; }
            tLast = MathF.Min(tLast, t1);

            if (tFirst > tLast) return false;
        }
        return true;
    }

    private static Vector2 NearestVertex(Vector2 point, Vector2[] vertices)
    {
        var nearest = vertices[0];
        float minDist = Vector2.DistanceSquared(point, vertices[0]);
        for (int i = 1; i < vertices.Length; i++)
        {
            float d = Vector2.DistanceSquared(point, vertices[i]);
            if (d < minDist) { minDist = d; nearest = vertices[i]; }
        }
        return nearest;
    }
}
