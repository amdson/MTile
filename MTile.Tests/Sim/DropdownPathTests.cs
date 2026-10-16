using System;
using System.Linq;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// DropdownState follows its clip as a PATH with the feet as the only actuator
// (PATH_TRACKER_UNIFICATION_PLAN phase 2). Swept at the game's 60 Hz (lesson 5:
// the 30 Hz suites missed the ledge pull timing out): every hanging-over start,
// still or running in, slips off within MaxDropdownTime, lands just past the edge,
// and leaves the platform no faster than the old clock-servo did (≈ 210 px/s at
// worst) — the pace fades per axis AND along the path, and the dropdown clip's
// Duration is re-derived for the constant-pace follower.
public class DropdownPathTests(ITestOutputHelper output)
{
    private const float TS = Chunk.TileSize;
    private const string Terrain = @"
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXX";
    private const float Edge = 10f * TS, Top = 5f * TS, LowerTop = 8f * TS;

    [Theory]
    [InlineData(-5f, 0f)]  [InlineData(-5f, 100f)]
    [InlineData(-3f, 0f)]  [InlineData(-3f, 60f)]
    [InlineData( 0f, 0f)]  [InlineData( 0f, 100f)]
    [InlineData( 3f, 0f)]  [InlineData( 3f, 60f)]
    public void HoldDown_At60Hz_SlipsOffAndLandsBelow(float edgeOffset, float entryVx)
    {
        const float dt = 1f / 60f;
        var trace = SimRunner.Run(new SimConfig
        {
            Terrain       = SimTerrain.FromAscii(Terrain, 0, 0),
            StartPosition = new Vector2(Edge + edgeOffset, Top - 19.5f),
            StartVelocity = new Vector2(entryVx, 0f),
            Script        = InputScript.Always(new PlayerInput { Down = true }),
            Frames        = 90,
            Dt            = dt,
            Gravity       = new Vector2(0f, 600f),
        });

        int first = Array.FindIndex(trace, f => f.State == "DropdownState");
        int last  = Array.FindLastIndex(trace, f => f.State == "DropdownState");
        Assert.True(first >= 0, "dropdown never fired");
        float slide = (last - first + 1) * dt;
        Assert.True(slide < MovementConfig.Current.MaxDropdownTime, $"slide ran {slide:F3}s — timed out on the platform");
        Assert.Equal("FallingState", trace[last + 1].State);
        float exitVy = trace[last].Vy;
        output.WriteLine($"off={edgeOffset} vx={entryVx}: slide {slide:F3}s exit vy {exitVy:F0}");
        Assert.True(exitVy < 210f, $"left the platform at vy={exitVy:F0} — yanked off, not slipped");

        var end = trace[^1];
        Assert.True(end.Y > LowerTop - 28f, $"never reached the lower floor: y={end.Y:F1}");
        Assert.True(end.X > Edge && end.X < Edge + 50f, $"landed at x={end.X:F1}, expected just past the edge {Edge}");
    }
}
