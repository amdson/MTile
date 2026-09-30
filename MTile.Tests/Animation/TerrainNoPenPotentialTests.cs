using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;

namespace MTile.Tests;

// The unified no-pen potential (NoPenetrationConstraint.Potential) on hand-built face lists —
// the geometry the body-facing filter hands it. Body is up-left of everything (y-down), so
// the faces listed are the ones that face it; far-side faces are absent by construction.
public class TerrainNoPenPotentialTests
{
    private const float T = 16f, H = T / 2f;

    private static bool P(List<SolverSurface> s, Vector2 q, out float pen, out Vector2 g)
        => NoPenetrationConstraint.Potential(s, null, 0, q, out pen, out g);

    // A tile [0,16]² — its top face (−y) and left face (−x), both convex-ended.
    private static List<SolverSurface> Tile() => new()
    {
        new(new Vector2(H, 0f), new Vector2(0f, -1f), 0f, 1, H),
        new(new Vector2(0f, H), new Vector2(-1f, 0f), 0f, 1, H),
    };

    [Fact]
    public void TipThroughThinWall_IsPulledBackTowardTheBody()
    {
        // One-wide wall [0,16] in x; body on the left, so only its left face is listed.
        var s = new List<SolverSurface> { new(new Vector2(0f, 0f), new Vector2(-1f, 0f), 0f, 1, 3 * T) };
        Assert.True(P(s, new Vector2(T + 3f, 0f), out float pen, out Vector2 g));   // poked out the far side
        Assert.Equal(T + 3f, pen, 3);            // depth behind the NEAR face, not 3px to the far one
        Assert.True(g.X > 0.99f);                // ∂P/∂q points in; descent pulls back toward −x (the body)
    }

    [Fact]
    public void SoleJustInsideALip_ExitsUp_NotOffTheCorner()
    {
        Assert.True(P(Tile(), new Vector2(3f, 1f), out float pen, out Vector2 g));   // 1px under top, 3px in from riser
        Assert.InRange(pen, 0f, 1f);
        // ∂P/∂q points INTO the solid (+y); descent is up. The riser 3px away blends in at ~e⁻².
        Assert.True(g.Y > 0.85f && MathF.Abs(g.X) < 0.2f, $"grad ({g.X:0.00},{g.Y:0.00}) should be the top face's");
    }

    [Fact]
    public void CornerTie_Blends_InsteadOfFlipping()
    {
        P(Tile(), new Vector2(2f, 2.01f), out _, out Vector2 a);
        P(Tile(), new Vector2(2.01f, 2f), out _, out Vector2 b);
        Assert.True((a - b).Length() < 0.05f, "direction flipped across the corner diagonal");
    }

    [Fact]
    public void PotentialVanishesContinuously_AtTheFace()
    {
        Assert.True(P(Tile(), new Vector2(8f, 0.01f), out float pen, out _));
        Assert.InRange(pen, -1f, 0.02f);
        Assert.False(P(Tile(), new Vector2(8f, -0.01f), out _, out _));
    }

    [Fact]
    public void ConcaveEnd_StaysInside_ConvexEnd_Releases()
    {
        // A floor top face spanning x ∈ [0,16]; the point is below its line, 4px past the +x end.
        var q = new Vector2(T + 4f, 3f);
        var convex  = new List<SolverSurface> { new(new Vector2(H, 0f), new Vector2(0f, -1f), 0f, 1, H) };
        // +x end concave: a wall rises there (tangent of an up face is +x, so ExtHi).
        var concave = new List<SolverSurface> { new(new Vector2(H, 0f), new Vector2(0f, -1f), 0f, 1, H, 0f, 20f) };
        Assert.False(P(convex, q, out _, out _));                    // in the air round the corner
        Assert.True(P(concave, q, out float pen, out Vector2 g));    // inside the wall's foot
        Assert.Equal(5f, pen, 1);                                    // exits at the corner (4,3 → 5)
        Assert.True(g.X > 0.7f && g.Y > 0.5f);                       // away from the corner ⇒ descent exits up-left through it
    }
}
