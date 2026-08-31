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

    [Fact]
    public void Engines_MinVx()
    {
        var cfg = MovementConfig.Current;
        string se = cfg.FoldEngine; bool sp = cfg.FoldCornerPlantEnabled;
        try
        {
            output.WriteLine("assert: minVx > 120 over x in (130,175). config json ships FoldEngine=lattice; code default=qp");
            foreach (string e in new[] { "qp", "ref", "lm", "lattice" })
            foreach (bool plant in new[] { false, true })
            {
                cfg.FoldEngine = e; cfg.FoldCornerPlantEnabled = plant;
                var frames = Arc();
                var win = frames.Where(f => f.X > 130f && f.X < 175f).ToArray();
                float minVx = win.Length == 0 ? float.NaN : win.Min(f => f.Vx);
                float maxX = frames.Max(f => f.X);
                bool bonk = frames.Any(f => f.State.Contains("WallSliding"));
                output.WriteLine($"engine={e,-8} plant={(plant ? "on " : "off")} | minVx={minVx,6:F0} | maxX={maxX,6:F1} | bonk={(bonk ? "YES" : "no ")} | {(minVx > 120f ? "PASS" : "fail")}");
            }
        }
        finally { cfg.FoldEngine = se; cfg.FoldCornerPlantEnabled = sp; }
    }

    [Fact]
    public void VaultDtInvariant_PerEngine()
    {
        var cfg = MovementConfig.Current;
        string se = cfg.FoldEngine;
        try
        {
            var step = SimTerrain.FromAscii(@"
                OOOOOOOOOOOOOOOOOOOO
                OOOOOOOOOOOOOOOOOOOO
                OOOOOOOOXXXXXXXXOOOO
                XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);
            output.WriteLine("wants: Parkour seen, delivered (x>140,y<12), |restY - 8| < 2.5");
            foreach (string e in new[] { "qp", "ref", "lm", "lattice" })
            foreach (float dt in new[] { 1f / 30f, 1f / 60f })
            {
                cfg.FoldEngine = e;
                int n = (int)(3f / dt);
                var tr = SimRunner.Run(new SimConfig
                {
                    Terrain = step, StartPosition = new Vector2(12f, 20f), StartVelocity = Vector2.Zero,
                    Script = InputScript.Always(new PlayerInput { Right = true }),
                    Frames = n, Dt = dt, Gravity = new Vector2(0f, 600f),
                });
                bool parkour = tr.Any(f => f.State.Contains("Parkour"));
                bool delivered = tr.Any(f => f.X > 140f && f.Y < 12f);
                var onTop = tr.Where(f => f.X > 150f && f.X < 230f && f.State.Contains("Standing"))
                              .OrderBy(f => f.Y).ToArray();
                float restY = onTop.Length > 0 ? onTop[onTop.Length / 2].Y : float.NaN;
                bool pass = parkour && delivered && onTop.Length > 0 && MathF.Abs(restY - 8f) < 2.5f;
                output.WriteLine($"engine={e,-8} dt={dt:F4} | parkour={parkour,-5} delivered={delivered,-5} " +
                                 $"restY={restY,6:F2} -> {(pass ? "PASS" : "fail")}");
            }
        }
        finally { cfg.FoldEngine = se; }
    }
}
