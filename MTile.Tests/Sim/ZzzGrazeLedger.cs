using System;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// SCRATCH — per-frame corrector ledger through the air-graze approach.
public class ZzzGrazeLedger(ITestOutputHelper output)
{
    private static ChunkMap Terrain() => SimTerrain.FromAscii(@"
        OOOOOOOOOOXXXXXXXXXXXX
        OOOOOOOOOOXXXXXXXXXXXX
        OOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 4);

    private void Run(string tag, bool plant, int horizon = 10)
    {
        var cfg = MovementConfig.Current;
        bool sp = cfg.FoldCornerPlantEnabled; int sh = cfg.AmbientHorizon;
        cfg.FoldCornerPlantEnabled = plant; cfg.AmbientHorizon = horizon;
        try
        {
            output.WriteLine($"=== {tag} (slab bottom y=96, slab left face x=160, floor top y=144) ===");
            var sim = new Simulation(Terrain(), new Vector2(90f, 130f));
            sim.Player.Body.Velocity = new Vector2(150f, -200f);
            var inp = new PlayerInput { Right = true };
            for (int f = 0; f < 46; f++)
            {
                sim.Step(inp);
                var b = sim.Player.Body;
                var L = sim.Player.ForceLedger;
                output.WriteLine($"f{f,2} x={b.Position.X,6:F1} y={b.Position.Y,6:F1} " +
                                 $"top={b.Position.Y - 10.4f,6:F1} v=({b.Velocity.X,6:F1},{b.Velocity.Y,6:F1}) " +
                                 $"{sim.Player.CurrentStateName}");
                for (int c = 0; c < L.ChannelCount; c++)
                {
                    var e = L.Channels[c];
                    if (e.Force.LengthSquared() < 0.25f) continue;
                    output.WriteLine($"      ch {e.Channel,-14} ({e.Force.X,7:F0},{e.Force.Y,7:F0})");
                }
                for (int c = 0; c < L.ContactCount; c++)
                {
                    var e = L.Contacts[c];
                    output.WriteLine($"      row n=({e.Normal.X,4:F0},{e.Normal.Y,4:F0}) " +
                                     $"F=({e.Force.X,7:F0},{e.Force.Y,7:F0}) " +
                                     $"cell={(e.HasCell ? $"{e.CellX},{e.CellY}" : "ref")} " +
                                     $"@({e.Pos.X:F0},{e.Pos.Y:F0})");
                }
            }
        }
        finally { cfg.FoldCornerPlantEnabled = sp; cfg.AmbientHorizon = sh; }
    }

    [Fact] public void Ledger_PlantOff() => Run("cornerPlant OFF (shipped default)", false);
    [Fact] public void Ledger_PlantOn()  => Run("cornerPlant ON", true);
    [Fact] public void Ledger_PlantOff_H48() => Run("cornerPlant OFF, AmbientHorizon 48", false, 48);
}
