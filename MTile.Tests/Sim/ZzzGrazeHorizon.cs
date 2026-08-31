using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// SCRATCH — is AmbientAirGraze short on FORCE or short on TIME?
// Sweeps AmbientHorizon with everything else at shipped defaults and reports
// the exact quantity the failing test asserts (minVx over x in (130,175)).
public class ZzzGrazeHorizon(ITestOutputHelper output)
{
    private static ChunkMap Terrain() => SimTerrain.FromAscii(@"
        OOOOOOOOOOXXXXXXXXXXXX
        OOOOOOOOOOXXXXXXXXXXXX
        OOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 4);

    private static SimFrame[] Arc() => SimRunner.Run(new SimConfig
    {
        Terrain = Terrain(),
        StartPosition = new Vector2(90f, 130f),
        StartVelocity = new Vector2(150f, -200f),
        Script = InputScript.Always(new PlayerInput { Right = true }),
        Frames = 60, Dt = 1f / 30f, Gravity = new Vector2(0f, 600f),
    });

    [Fact]
    public void Sweep_AmbientHorizon()
    {
        var cfg = MovementConfig.Current;
        int savedH = cfg.AmbientHorizon; bool savedP = cfg.FoldCornerPlantEnabled;
        try
        {
            output.WriteLine("test asserts minVx > 120 over x in (130,175); shipped AmbientHorizon = 10");
            foreach (bool plant in new[] { false, true })
            foreach (int h in new[] { 10, 14, 18, 24, 32, 40, 48 })
            {
                cfg.AmbientHorizon = h; cfg.FoldCornerPlantEnabled = plant;
                var frames = Arc();
                var win = frames.Where(f => f.X > 130f && f.X < 175f).ToArray();
                float minVx = win.Length == 0 ? float.NaN : win.Min(f => f.Vx);
                float minTop = frames.Where(f => f.X > 120f && f.X < 165f)
                                     .Select(f => f.Y - 10.4f).DefaultIfEmpty(float.NaN).Min();
                bool bonked = frames.Any(f => f.State.Contains("WallSliding"));
                output.WriteLine($"plant={(plant ? "on " : "off")} H={h,2} | minVx={minVx,6:F0} | " +
                                 $"highest body-top near slab={minTop,6:F1} (needs >= 96) | " +
                                 $"bonk={(bonked ? "YES" : "no ")} | {(minVx > 120f ? "PASS" : "fail")}");
            }
        }
        finally { cfg.AmbientHorizon = savedH; cfg.FoldCornerPlantEnabled = savedP; }
    }
}
