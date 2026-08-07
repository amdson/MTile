using System;
using System.Diagnostics;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// TEMP EXPERIMENT: time the LatticePlanner prototype. Delete me.
public class ZzzLatticeTiming(ITestOutputHelper output)
{
    [Fact]
    public void Time()
    {
        var mc = MovementConfig.Current;
        const int W = 64;
        var rows = new string[7];
        for (int r = 0; r < 7; r++)
        {
            var sb = new StringBuilder(W);
            for (int c = 0; c < W; c++)
            {
                bool tunnel = c >= 16;
                sb.Append(r switch
                {
                    6 => 'X',
                    2 when tunnel => 'X',
                    3 when tunnel && c % 4 == 3 => 'X',
                    5 when tunnel && c % 4 == 1 => 'X',
                    _ => 'O',
                });
            }
            rows[r] = sb.ToString();
        }
        var tunnelMap = SimTerrain.FromAscii(string.Join("\n", rows), 0, 0);
        var flat = SimTerrain.FromAscii(new string('X', 40), originTileX: 0, originTileY: 6);

        Bench("flat walk ", flat, new Vector2(100f, 72f), new Vector2(100f, 0f));
        Bench("tunnel    ", tunnelMap, new Vector2(392f, 72f), new Vector2(100f, 0f));

        void Bench(string name, ChunkMap chunks, Vector2 p, Vector2 v)
        {
            var body = new PlayerCharacter(p).Body;
            var samples = new CoastSample[BallisticPredictor.MaxHorizon];
            for (int i = 0; i < 20; i++)   // warmup/JIT
                LatticePlanner.Solve(chunks, body.Polygon, p, v, 1, mc.MaxWalkSpeed,
                    mc.FoldHoverOffset, new Vector2(0f, 600f), 1f / 60f, 24, samples, out _);
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            const int N = 200;
            for (int i = 0; i < N; i++)
                LatticePlanner.Solve(chunks, body.Polygon, p, v, 1, mc.MaxWalkSpeed,
                    mc.FoldHoverOffset, new Vector2(0f, 600f), 1f / 60f, 24, samples, out _);
            sw.Stop();
            long a1 = GC.GetAllocatedBytesForCurrentThread();
            output.WriteLine($"{name} {sw.Elapsed.TotalMilliseconds * 1000.0 / N:F0} us/solve, " +
                             $"{(a1 - a0) / (double)N / 1024.0:F1} KB alloc/solve");
        }
    }
}
