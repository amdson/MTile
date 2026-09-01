using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// SCRATCH — the two failing CaveMouthTests assertions, per FoldEngine.
// Mirrors CaveMouthTests' fixtures exactly.
public class ZzzCaveMouthEngines(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;
    private static readonly Vector2 Gravity = new(0f, 600f);

    private static ChunkMap CaveTerrain(int ledgeCols = 8, int ledgeTopRow = 3)
    {
        var rows = new string[8];
        for (int r = 0; r < 8; r++)
        {
            var sb = new System.Text.StringBuilder(30);
            for (int c = 0; c < 30; c++)
            {
                bool wall = c >= 12;
                bool ledge = c <= ledgeCols && r >= ledgeTopRow;
                sb.Append(r switch
                {
                    >= 6 => 'X',
                    4 or 5 when wall => 'O',
                    _ when wall => 'X',
                    _ when ledge => 'X',
                    _ => 'O',
                });
            }
            rows[r] = sb.ToString();
        }
        return SimTerrain.FromAscii(string.Join("\n", rows), originTileX: 0, originTileY: 0);
    }

    private static SimFrame[] Drop(Vector2 start, Vector2 vel, int ledgeCols, int ledgeTopRow)
        => SimRunner.Run(new SimConfig
        {
            Terrain = CaveTerrain(ledgeCols, ledgeTopRow),
            StartPosition = start, StartVelocity = vel,
            Script = InputScript.Always(new PlayerInput { Right = true }),
            Frames = 240, Dt = Dt, Gravity = Gravity,
        });

    private static int FaceStallFrames(SimFrame[] f) =>
        f.Count(x => x.X > 170f && x.X < 194f && MathF.Abs(x.Vx) < 5f && x.Y < 66f);

    [Fact]
    public void Both_CaveMouthAsserts_PerEngine()
    {
        var cfg = MovementConfig.Current;
        string se = cfg.FoldEngine; bool sp = cfg.FoldCornerPlantEnabled;
        cfg.FoldCornerPlantEnabled = true;   // both tests run with the plant ON
        try
        {
            output.WriteLine("NearMiss wants: stalls==0, atFace.Y in [64,82], entered frame<115, slowFrames<10, trimmed");
            output.WriteLine("Bonk wants: stalls > 0");
            foreach (string e in new[] { "qp", "ref", "lm", "lattice" })
            {
                cfg.FoldEngine = e;

                var nm = Drop(new Vector2(100f, 59f), new Vector2(100f, 0f), 9, 5);
                var atFace = nm.FirstOrDefault(f => f.X > 184f);
                var inside = nm.FirstOrDefault(f => f.X > 240f);
                int nmStalls = FaceStallFrames(nm);
                int slow = nm.Count(f => f.X is > 170f and < 200f && MathF.Abs(f.Vx) < 30f);
                bool trimmed = nm.Any(f => f.X is > 140f and < 190f && f.Y < 80f && f.Fy > 50f);

                var bk = Drop(new Vector2(120f, 10f), new Vector2(110f, 0f), 8, 3);
                int bkStalls = FaceStallFrames(bk);

                bool nmPass = nmStalls == 0 && atFace != null && atFace.Y >= 64f && atFace.Y <= 82f
                              && inside != null && inside.Frame < 115 && slow < 10 && trimmed;
                output.WriteLine($"engine={e,-8} | NearMiss: stalls={nmStalls} atFaceY={atFace?.Y ?? -1,6:F1} " +
                                 $"enterF={inside?.Frame ?? -1,4} slow={slow,2} trim={trimmed,-5} -> {(nmPass ? "PASS" : "fail")}" +
                                 $" || Bonk: stalls={bkStalls} -> {(bkStalls > 0 ? "PASS" : "fail")}");
            }
        }
        finally { cfg.FoldEngine = se; cfg.FoldCornerPlantEnabled = sp; }
    }
}
