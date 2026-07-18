using System;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;

namespace MTile.Tests.Animation;

// Golden properties of the rigid-MLS deformer (Plans/SPRITE_SKIN_PLAN.md §9.1):
//   1. Identity — handles unmoved ⇒ vertices unmoved.
//   2. Rigid reproduction — handles moved by one global rotation+translation ⇒ every
//      vertex moves by exactly that transform (this is what "minimal deformation"
//      means; it also catches any perp/sign error in the A_i precompute, which would
//      produce a reflected or inverse rotation instead).
//   3. Interpolation — a vertex sitting on a handle follows that handle.
//   4. No shear from non-uniform handle stretch — rigid MLS may only rotate+translate
//      locally, so stretching the handle set must not scale a vertex's local frame.
public class MlsDeformerTests
{
    // A small non-degenerate handle set (nothing collinear overall) and a spread of
    // test vertices around it.
    private static readonly Vector2[] Handles =
    {
        new(0, 0), new(10, 0), new(10, 8), new(2, 12), new(-4, 6), new(5, 4),
    };

    private static readonly Vector2[] Verts =
    {
        new(1, 1), new(9, 1), new(6, 7), new(-2, 9), new(4, 11), new(12, 4),
        new(0, 0),            // exactly on a handle (interpolation)
        new(5.01f, 4.02f),    // nearly on a handle
    };

    private static Vector2[] Run(MlsDeformer d, Vector2[] posed)
    {
        var result = new Vector2[Verts.Length];
        d.Deform(posed, result);
        return result;
    }

    [Fact]
    public void UnmovedHandlesLeaveVerticesInPlace()
    {
        var d = MlsDeformer.Bake(Verts, Handles);
        var moved = Run(d, Handles);
        for (int i = 0; i < Verts.Length; i++)
            AssertNear(Verts[i], moved[i], 1e-3f);
    }

    [Theory]
    [InlineData(0.7f, 3f, -2f)]
    [InlineData(-2.1f, 0f, 10f)]
    [InlineData(3.14159f, -5f, 4f)]
    public void GlobalRigidMotionIsReproducedExactly(float angle, float tx, float ty)
    {
        var d = MlsDeformer.Bake(Verts, Handles);
        var posed = Array.ConvertAll(Handles, p => Rigid(p, angle, tx, ty));
        var moved = Run(d, posed);
        for (int i = 0; i < Verts.Length; i++)
            AssertNear(Rigid(Verts[i], angle, tx, ty), moved[i], 1e-3f);
    }

    [Fact]
    public void VertexOnHandleFollowsThatHandle()
    {
        var d = MlsDeformer.Bake(Verts, Handles);
        // Bend the handle set: move handle 0 (which vertex 6 sits on) far away,
        // perturb the others mildly.
        var posed = (Vector2[])Handles.Clone();
        posed[0] = new Vector2(-8, -5);
        posed[3] += new Vector2(1, -1);
        var moved = Run(d, posed);
        // The on-handle vertex's weight for handle 0 is ~1/eps — it must track it
        // closely even though other handles pull elsewhere.
        AssertNear(posed[0], moved[6], 0.05f);
    }

    [Fact]
    public void RigidVariantDoesNotStretchWithHandles()
    {
        // Stretch every handle x2 about the origin (a pure scale). A similarity/affine
        // deformer would double the vertex offsets; the rigid one must keep each
        // vertex's DISTANCE to its local frame (|v - p*|) unchanged. Verify via the
        // midpoint vertex of two handles staying at their posed midpoint (symmetry)
        // while an off-axis vertex keeps its rest distance to p*'s image.
        Vector2[] handles = { new(-5, 0), new(5, 0), new(0, 5) };
        Vector2[] verts   = { new(0, 1) };
        var d = MlsDeformer.Bake(verts, handles);
        var posed = Array.ConvertAll(handles, p => p * 2f);
        var moved = new Vector2[1];
        d.Deform(posed, moved);

        // By symmetry p* and q* sit on the y-axis; rest |v - p*| must be preserved.
        float restDist  = (verts[0] - WeightedCentroid(verts[0], handles)).Length();
        float movedDist = (moved[0] - WeightedCentroid(verts[0], handles, posed)).Length();
        Assert.Equal(restDist, movedDist, 2);
    }

    [Fact]
    public void PruningKeepsNearestHandlesInCharge()
    {
        // With pruning to 3 handles, a far-away handle moving wildly must not disturb
        // a vertex surrounded by 3 near handles.
        Vector2[] handles = { new(0, 0), new(2, 0), new(1, 2), new(100, 100) };
        Vector2[] verts   = { new(1, 0.7f) };
        var d = MlsDeformer.Bake(verts, handles, maxHandlesPerVertex: 3);
        var posed = (Vector2[])handles.Clone();
        posed[3] = new Vector2(-500, 300);
        var moved = new Vector2[1];
        d.Deform(posed, moved);
        AssertNear(verts[0], moved[0], 1e-3f);
    }

    // Weighted centroid with the same kernel the deformer uses (unpruned, eps-regularized),
    // evaluated over `at` positions (defaults to the weight-source positions).
    private static Vector2 WeightedCentroid(Vector2 v, Vector2[] weightFrom, Vector2[] at = null)
    {
        at ??= weightFrom;
        float sum = 0f; Vector2 c = Vector2.Zero;
        for (int i = 0; i < weightFrom.Length; i++)
        {
            float w = 1f / (Vector2.DistanceSquared(weightFrom[i], v) + 1e-4f);
            sum += w; c += at[i] * w;
        }
        return c / sum;
    }

    private static Vector2 Rigid(Vector2 p, float angle, float tx, float ty)
    {
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        return new Vector2(c * p.X - s * p.Y + tx, s * p.X + c * p.Y + ty);
    }

    private static void AssertNear(Vector2 expected, Vector2 actual, float tol)
    {
        float err = Vector2.Distance(expected, actual);
        Assert.True(err <= tol, $"expected {expected}, got {actual} (err {err} > {tol})");
    }
}
