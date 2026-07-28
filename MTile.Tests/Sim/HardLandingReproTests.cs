using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// REPRO (stand fold): overwhelm the support (fast fall → physical tile contact)
// and check whether the solver pushes the body back up to hover. Diagnostic.
public class HardLandingReproTests(ITestOutputHelper output)
{
    [Fact]
    public void FastFall_ToFloorContact_ReturnsToHover()
    {
        var rows = new string[9];
        for (int r = 0; r < 9; r++)
        {
            var sb = new StringBuilder(32);
            for (int c = 0; c < 32; c++) sb.Append(r == 8 ? 'X' : 'O');
            rows[r] = sb.ToString();
        }
        var terrain = SimTerrain.FromAscii(string.Join("\n", rows), originTileX: 0, originTileY: 0);

        // Floor top y=128; resting contact center ≈ 116; hover target ≈ 106.
        var sim = new Simulation(terrain, new Vector2(100f, 60f),
                                 Stages.Get("gym").Populate);
        sim.Player.Body.Velocity = new Vector2(0f, 500f);   // beyond the catch envelope
        sim.Player.CorrectorDebug.CaptureTrajectories = true;

        output.WriteLine("  frame      y      vy   state  rows  z0/channel");
        string last = "";
        for (int f = 0; f < 60; f++)
        {
            sim.Step(default);
            var b = sim.Player.Body;
            var cd = sim.Player.CorrectorDebug;
            string st = sim.Player.CurrentStateName;
            bool tr = st != last; last = st;
            if (f % 5 == 0 || tr)
            {
                var sb2 = new StringBuilder();
                for (int c = 0; c < 5; c++)
                    sb2.Append($" c{c}=({cd.Z[c * 10].X:F0},{cd.Z[c * 10].Y:F0})");
                output.WriteLine($"  {f,5} {b.Position.Y,7:F1} {b.Velocity.Y,7:F1}  {st,-14} {cd.ContactCount,3} {sb2}");
            }
        }
    }
}
