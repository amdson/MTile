using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Bézier curve evaluators. Scalar overloads operate on a single axis (useful for
// shaping animation timing — easing, anticipation, overshoot); Vector2 overloads
// compose two scalar curves component-wise.
//
// Quadratic uses three control points (P0, P1, P2); cubic uses four (P0..P3).
// Both forms include a Derivative variant that returns dB/dt at t — handy when
// you want velocity (not position) along the curve, e.g. to drive a particle's
// initial speed or align a sprite's rotation with the tangent.
public static class Bezier
{
    // ---------- Quadratic ------------------------------------------------------

    // B(t) = (1-t)² P0 + 2(1-t)t P1 + t² P2.
    public static float Quadratic(float p0, float p1, float p2, float t)
    {
        float u = 1f - t;
        return u * u * p0 + 2f * u * t * p1 + t * t * p2;
    }

    public static Vector2 Quadratic(Vector2 p0, Vector2 p1, Vector2 p2, float t)
    {
        float u = 1f - t;
        float a = u * u;
        float b = 2f * u * t;
        float c = t * t;
        return new Vector2(
            a * p0.X + b * p1.X + c * p2.X,
            a * p0.Y + b * p1.Y + c * p2.Y);
    }

    // dB/dt = 2(1-t)(P1 - P0) + 2t(P2 - P1).
    public static float QuadraticDerivative(float p0, float p1, float p2, float t)
    {
        return 2f * (1f - t) * (p1 - p0) + 2f * t * (p2 - p1);
    }

    public static Vector2 QuadraticDerivative(Vector2 p0, Vector2 p1, Vector2 p2, float t)
    {
        float u = 1f - t;
        return new Vector2(
            2f * u * (p1.X - p0.X) + 2f * t * (p2.X - p1.X),
            2f * u * (p1.Y - p0.Y) + 2f * t * (p2.Y - p1.Y));
    }

    // ---------- Cubic ----------------------------------------------------------

    // B(t) = (1-t)³ P0 + 3(1-t)²t P1 + 3(1-t)t² P2 + t³ P3.
    public static float Cubic(float p0, float p1, float p2, float p3, float t)
    {
        float u  = 1f - t;
        float uu = u * u;
        float tt = t * t;
        return uu * u * p0
             + 3f * uu * t * p1
             + 3f * u * tt * p2
             + tt * t * p3;
    }

    public static Vector2 Cubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float u  = 1f - t;
        float uu = u * u;
        float tt = t * t;
        float a  = uu * u;
        float b  = 3f * uu * t;
        float c  = 3f * u * tt;
        float d  = tt * t;
        return new Vector2(
            a * p0.X + b * p1.X + c * p2.X + d * p3.X,
            a * p0.Y + b * p1.Y + c * p2.Y + d * p3.Y);
    }

    // dB/dt = 3(1-t)²(P1-P0) + 6(1-t)t(P2-P1) + 3t²(P3-P2).
    public static float CubicDerivative(float p0, float p1, float p2, float p3, float t)
    {
        float u  = 1f - t;
        return 3f * u * u * (p1 - p0)
             + 6f * u * t * (p2 - p1)
             + 3f * t * t * (p3 - p2);
    }

    public static Vector2 CubicDerivative(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float u = 1f - t;
        float a = 3f * u * u;
        float b = 6f * u * t;
        float c = 3f * t * t;
        return new Vector2(
            a * (p1.X - p0.X) + b * (p2.X - p1.X) + c * (p3.X - p2.X),
            a * (p1.Y - p0.Y) + b * (p2.Y - p1.Y) + c * (p3.Y - p2.Y));
    }
}
