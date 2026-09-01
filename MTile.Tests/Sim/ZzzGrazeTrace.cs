using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// SCRATCH — full trace of AmbientAirGraze_PreservesSpeedThroughCorner.
public class ZzzGrazeTrace(ITestOutputHelper output)
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

    private void Dump(string tag)
    {
        output.WriteLine($"=== {tag} ===");
        output.WriteLine("  f |    x      y  |   vx     vy |   fx     fy | state");
        foreach (var f in Arc().Take(40))
            output.WriteLine($"{f.Frame,3} | {f.X,6:F1} {f.Y,6:F1} | {f.Vx,6:F1} {f.Vy,6:F1} | {f.Fx,6:F0} {f.Fy,6:F0} | {f.State}");
    }

    [Fact]
    public void Trace_AmbientOn()
    {
        var cfg = MovementConfig.Current;
        bool saved = cfg.AmbientCorrectorEnabled;
        try { cfg.AmbientCorrectorEnabled = true; Dump("ambient ON"); }
        finally { cfg.AmbientCorrectorEnabled = saved; }
    }

    [Fact]
    public void Trace_AmbientOff()
    {
        var cfg = MovementConfig.Current;
        bool saved = cfg.AmbientCorrectorEnabled;
        try { cfg.AmbientCorrectorEnabled = false; Dump("ambient OFF"); }
        finally { cfg.AmbientCorrectorEnabled = saved; }
    }

    [Fact]
    public void Trace_AmbientOn_LmEngine()
    {
        var cfg = MovementConfig.Current;
        bool saved = cfg.AmbientCorrectorEnabled; string e = cfg.FoldEngine;
        try { cfg.AmbientCorrectorEnabled = true; cfg.FoldEngine = "lm"; Dump("ambient ON / lm"); }
        finally { cfg.AmbientCorrectorEnabled = saved; cfg.FoldEngine = e; }
    }

    [Fact]
    public void Trace_AmbientOn_RefEngine()
    {
        var cfg = MovementConfig.Current;
        bool saved = cfg.AmbientCorrectorEnabled; string e = cfg.FoldEngine;
        try { cfg.AmbientCorrectorEnabled = true; cfg.FoldEngine = "ref"; Dump("ambient ON / ref"); }
        finally { cfg.AmbientCorrectorEnabled = saved; cfg.FoldEngine = e; }
    }

    [Fact]
    public void Trace_AmbientOn_CornerPlantOn()
    {
        var cfg = MovementConfig.Current;
        bool sa = cfg.AmbientCorrectorEnabled, sp = cfg.FoldCornerPlantEnabled;
        try { cfg.AmbientCorrectorEnabled = true; cfg.FoldCornerPlantEnabled = true; Dump("ambient ON / cornerPlant ON"); }
        finally { cfg.AmbientCorrectorEnabled = sa; cfg.FoldCornerPlantEnabled = sp; }
    }
}
