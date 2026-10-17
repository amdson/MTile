using Microsoft.Xna.Framework;
using MTile;

namespace MTile.Tests;

public class CurtainSimTests
{
    // A 3×5 cape hanging straight down from its anchor: 10 px wide, 20 px long, drawn
    // facing right (so "behind" is −X — here the rest shape is symmetric, and the tests
    // look at the hem's motion, not its drawn offset).
    private static CurtainSim Cape()
    {
        var def = new CurtainDefinition { Type = "Curtain", Columns = 3, Rows = 5, PixelSize = 1f };
        var (rest, _, _) = CurtainAsset.BuildGrid(def, 10, 20, (x, y) => 255);
        return new CurtainSim(def, rest, 3, 5);
    }

    private static Vector2 Hem(CurtainSim s) => s.Points[^2];   // bottom row, middle column

    private static void Run(CurtainSim s, Vector2 anchor, int facing, int frames)
    {
        for (int i = 0; i < frames; i++) s.Update(anchor, facing, CurtainSim.Step);
    }

    [Fact]
    public void AtRest_SettlesNearItsDrawnShape()
    {
        var s = Cape();
        Run(s, new Vector2(100, 50), 1, 1);
        Run(s, new Vector2(100, 50), 1, 120);
        var hem = Hem(s);
        Assert.InRange(hem.X, 100f - 0.5f, 100f + 0.5f);   // hangs plumb under the anchor
        Assert.InRange(hem.Y, 50f + 19f, 50f + 23f);       // ≈ its drawn length (a little sag)
    }

    [Fact]
    public void RunningRight_TheHemTrailsBehind_ThenSwingsBack()
    {
        var s = Cape();
        Run(s, new Vector2(0, 0), 1, 30);
        var anchor = Vector2.Zero;
        for (int i = 0; i < 20; i++) { anchor.X += 3f; s.Update(anchor, 1, CurtainSim.Step); }   // 180 px/s
        Assert.True(Hem(s).X < anchor.X - 2f, $"hem {Hem(s).X} should lag the anchor {anchor.X}");

        Run(s, anchor, 1, 6);   // stop: momentum carries the hem forward past plumb at some point
        float maxAhead = float.MinValue;
        for (int i = 0; i < 30; i++) { s.Update(anchor, 1, CurtainSim.Step); maxAhead = MathF.Max(maxAhead, Hem(s).X - anchor.X); }
        Assert.True(maxAhead > -1f, "the stop should swing the hem back toward (or past) plumb");
    }

    [Fact]
    public void Turning_MirrorsTheSheet_SoItStaysBehind()
    {
        var s = Cape();
        Run(s, Vector2.Zero, 1, 30);
        var anchor = Vector2.Zero;
        for (int i = 0; i < 20; i++) { anchor.X += 3f; s.Update(anchor, 1, CurtainSim.Step); }
        float lagBefore = anchor.X - Hem(s).X;                // behind = to the left
        s.Update(anchor, -1, 0f);                             // turn on the spot
        float lagAfter = Hem(s).X - anchor.X;                 // behind = to the right now
        Assert.True(lagBefore > 2f);
        Assert.InRange(lagAfter, lagBefore - 0.01f, lagBefore + 0.01f);
    }

    [Fact]
    public void Teleport_ResetsToRest_InsteadOfWhippingAcrossTheMap()
    {
        var s = Cape();
        Run(s, Vector2.Zero, 1, 30);
        s.Update(new Vector2(1000, 0), 1, CurtainSim.Step);
        Assert.Equal(s.RestWorld(s.Points.Length - 2), Hem(s));
    }

    [Fact]
    public void Hitch_IsCapped_NotSimulatedInFull()
    {
        var s = Cape();
        Run(s, Vector2.Zero, 1, 2);
        s.Update(new Vector2(10, 0), 1, 5f);   // a 5 s stall must not run 300 steps
        Assert.All(s.Points, p => Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y)));
    }

    [Fact]
    public void Floor_StopsTheHem_WhenTheAnchorDrops()
    {
        var s = Cape();
        s.IsSolid = p => p.Y >= Chunk.TileSize * 6;          // solid from y = 66 down
        Run(s, new Vector2(0, 50), 1, 60);                  // hem ≈ 70: rests ON the floor
        Run(s, new Vector2(0, 58), 1, 60);                  // crouch: the hem would reach ~78
        Assert.All(s.Points, p => Assert.True(p.Y < Chunk.TileSize * 6, $"point at y {p.Y} is in the floor"));
    }
}
