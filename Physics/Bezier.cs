using Microsoft.Xna.Framework;

namespace MTile;

// Bézier curve evaluation. Only the scalar cubic is here: it is what the codebase
// actually uses (ActionStates' arc shaping). The quadratic forms, the Vector2
// overloads and the derivative variants were written speculatively and never called;
// add one back when something needs it rather than keeping eight unused evaluators.
public static class Bezier
{
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
}
