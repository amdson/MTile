using System;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;

namespace MTile.Tests.Presentation;

// The telegraph list is the sim→render seam for overlay shapes: sim-side code appends,
// Drawing/TelegraphRenderer draws. These run headless — no GraphicsDevice — which is the
// point of the seam: an action's visual declaration is testable like any other output.
public class TelegraphListTests
{
    [Fact]
    public void RecordsShapesInOrderAndClears()
    {
        var t = new TelegraphList(initialCapacity: 2);   // forces a grow on the 3rd shape
        t.Line(new Vector2(0, 0), new Vector2(10, 0), Color.Red, 2f);
        t.Box(5, 6, 7, 8, Color.Green);
        t.Ring(new Vector2(1, 1), 9f, Color.Blue, segments: 12, thickness: 3f);
        t.RotatedRect(new Vector2(2, 2), new Vector2(4, 6), 0.5f, Color.White);

        Assert.Equal(4, t.Count);
        Assert.Equal(TelegraphKind.Line,        t[0].Kind);
        Assert.Equal(new Vector2(10, 0),        t[0].B);
        Assert.Equal(2f,                        t[0].Thickness);
        Assert.Equal(TelegraphKind.Box,         t[1].Kind);
        Assert.Equal(new Vector2(5, 6),         t[1].A);
        Assert.Equal(new Vector2(7, 8),         t[1].B);
        Assert.Equal(TelegraphKind.Ring,        t[2].Kind);
        Assert.Equal(9f,                        t[2].B.X);
        Assert.Equal(12,                        t[2].Segments);
        Assert.Equal(TelegraphKind.RotatedRect, t[3].Kind);
        Assert.Equal(0.5f,                      t[3].Rotation);

        t.Clear();
        Assert.Equal(0, t.Count);
    }

    [Fact]
    public void CentredRectAndRayDeriveFromBoxAndLine()
    {
        var t = new TelegraphList();
        t.Rect(new Vector2(10, 20), 4f, Color.White);
        t.Ray(new Vector2(0, 0), 0f, 30f, 1f, Color.White);

        Assert.Equal(TelegraphKind.Box, t[0].Kind);
        Assert.Equal(new Vector2(8, 18), t[0].A);
        Assert.Equal(new Vector2(4, 4),  t[0].B);
        Assert.Equal(TelegraphKind.Line, t[1].Kind);
        Assert.Equal(30f, t[1].B.X, 3);
        Assert.Equal(0f,  t[1].B.Y, 3);
    }

    [Fact]
    public void TrailEmitsFadingLineSegmentsWithoutGraphics()
    {
        var trail = new Trail(capacity: 8, lifetime: 1f);
        for (int i = 0; i < 4; i++) trail.Push(new Vector2(i * 10f, 0f));

        var t = new TelegraphList();
        trail.Emit(t, Color.White, Color.Transparent, startWidth: 3f);

        Assert.True(t.Count > 0);
        for (int i = 0; i < t.Count; i++)
        {
            Assert.Equal(TelegraphKind.Line, t[i].Kind);
            Assert.True(t[i].Thickness > 0f && t[i].Thickness <= 3f);
        }
        // Head is the newest sample; the ribbon walks newest → oldest.
        Assert.Equal(30f, t[0].A.X, 3);
    }

    [Fact]
    public void ArcSpansTheRequestedWedgeAsLineSegments()
    {
        var t = new TelegraphList();
        var centre = new Vector2(100f, 200f);
        t.Arc(centre, radius: 10f, centerAngle: 0f, halfAngle: MathF.PI / 3f,
              Color.White, segments: 6, thickness: 2f);

        Assert.Equal(6, t.Count);
        for (int i = 0; i < t.Count; i++)
        {
            Assert.Equal(TelegraphKind.Line, t[i].Kind);
            Assert.Equal(2f, t[i].Thickness);
            // Every vertex sits on the circle.
            Assert.Equal(10f, (t[i].A - centre).Length(), 3);
            Assert.Equal(10f, (t[i].B - centre).Length(), 3);
        }
        // Ends at -60 deg / +60 deg of the centre angle, and the chain is contiguous.
        Assert.Equal(centre + new Vector2(MathF.Cos(-MathF.PI / 3f), MathF.Sin(-MathF.PI / 3f)) * 10f,
                     t[0].A);
        Assert.Equal(centre + new Vector2(MathF.Cos(MathF.PI / 3f), MathF.Sin(MathF.PI / 3f)) * 10f,
                     t[5].B);
        Assert.Equal(t[0].B, t[1].A);
    }

    [Fact]
    public void GuardActionTelegraphsAShieldInsideTheCoveredCone()
    {
        var body = new PhysicsBody(Polygon.CreateRegular(PlayerCharacter.Radius, 6), new Vector2(100f, 200f));
        var t = new TelegraphList();
        var vars = new ActionVars { Facing = 1 };
        new GuardAction().Telegraph(t, body, in vars);

        // The placeholder shield is an arc of Lines in front of the body.
        Assert.True(t.Count > 2);
        // Every vertex lies inside the guarded cone: fromAttacker direction within
        // GuardConeHalfAngle of facing is exactly what ResolveGuard absorbs.
        float cos = MathF.Cos(CombatState.GuardConeHalfAngle);
        for (int i = 0; i < t.Count; i++)
        {
            Assert.Equal(TelegraphKind.Line, t[i].Kind);
            var dir = t[i].A - body.Position;
            dir.Normalize();
            Assert.True(Vector2.Dot(dir, Vector2.UnitX) >= cos - 1e-3f,
                        $"shield vertex {i} is outside the guarded cone");
        }
    }

    [Fact]
    public void GuardShieldTelegraphMirrorsWithFacing()
    {
        var body = new PhysicsBody(Polygon.CreateRegular(PlayerCharacter.Radius, 6), new Vector2(100f, 200f));
        var left = new TelegraphList();
        var vars = new ActionVars { Facing = -1 };
        new GuardAction().Telegraph(left, body, in vars);

        for (int i = 0; i < left.Count; i++)
            Assert.True(left[i].A.X <= body.Position.X + 1e-3f,
                        "a left-facing guard covers the left side");
    }

    [Fact]
    public void GuardShieldTelegraphRotatesWithTheAim()
    {
        var body = new PhysicsBody(Polygon.CreateRegular(PlayerCharacter.Radius, 6), new Vector2(100f, 200f));
        var t = new TelegraphList();
        var aim  = Vector2.Normalize(new Vector2(1f, -1f));   // up and to the right
        var vars = new ActionVars { Facing = 1, GuardDir = aim };
        new GuardAction().Telegraph(t, body, in vars);

        Assert.True(t.Count > 2);
        float cos = MathF.Cos(CombatState.GuardConeHalfAngle);
        for (int i = 0; i < t.Count; i++)
        {
            var dir = t[i].A - body.Position;
            dir.Normalize();
            Assert.True(Vector2.Dot(dir, aim) >= cos - 1e-3f,
                        $"shield vertex {i} is outside the aimed cone");
        }
    }

    [Fact]
    public void PulseActionTelegraphsNothingOutsideItsHitboxWindow()
    {
        var body = new PhysicsBody(Polygon.CreateRegular(PlayerCharacter.Radius, 6), new Vector2(100f, 200f));
        var t = new TelegraphList();
        var vars = new ActionVars { TimeInState = 0f, IsGrounded = true };
        new PulseAction().Telegraph(t, body, in vars);
        Assert.Equal(0, t.Count);
    }

    // A Sprite carries a name the renderer resolves, and counts the primitives emitted
    // after it as its stand-in — drawn only when the art is missing.
    [Fact]
    public void Sprite_RecordsArtAndCountsItsFallbackShapes()
    {
        var t = new TelegraphList();
        t.Line(Vector2.Zero, Vector2.One, Color.Red);                       // not part of it
        int mark = t.Sprite("spear", "lock", 0.5f, new Vector2(3, 4), 1f, Color.White, mirror: true);
        t.Line(Vector2.Zero, Vector2.One, Color.Brown);
        t.RotatedRect(Vector2.One, Vector2.One, 0f, Color.Gray);
        t.EndFallback(mark);
        t.Line(Vector2.Zero, Vector2.One, Color.Red);                       // after it: always drawn

        Assert.Equal(1, mark);
        Assert.Equal(TelegraphKind.Sprite, t[1].Kind);
        Assert.Equal("spear", t[1].Art);
        Assert.Equal("lock", t[1].Tag);
        Assert.True(t[1].Mirror);
        Assert.Equal(2, t[1].Fallback);
    }
}
