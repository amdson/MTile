using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Backoff (spacing) and Breach (digging) bot maneuvers.
public class BotManeuverSpacingTests(ITestOutputHelper output)
{
    private static (BotArena.Run run, int overlap) Fight(params BotManeuver[] ms)
    {
        int overlap = 0;
        var sim = new Simulation(BotArena.Flat(), new Vector2(100f, 70f));
        var (p2, _) = sim.AddSecondaryPlayer(new Vector2(300f, 70f));
        var bot = BotArena.Only(ms);
        var r = new BotArena.Run { Sim = sim, P1 = sim.Player, P2 = p2, Bot = bot };
        for (int f = 0; f < 600; f++)
        {
            // P1 keeps walking at the bot (a face-hugger), so overlap measures the bot's spacing.
            float toBot = p2.Body.Position.X - sim.Player.Body.Position.X;
            var p1 = new PlayerInput { Right = toBot > 4f, Left = toBot < -4f };
            sim.Step(p1, bot.Poll(sim, f));
            if (MathF.Abs(p2.Body.Position.X - sim.Player.Body.Position.X) < 15f) overlap++;
            if (!r.P2Actions.Contains(p2.CurrentActionName)) r.P2Actions.Add(p2.CurrentActionName);
            if (!r.P2States.Contains(p2.CurrentStateName)) r.P2States.Add(p2.CurrentStateName);
            r.Frames = f + 1;
        }
        r.DamageToP1 = sim.Player.Combat.DamageTaken;
        return (r, overlap);
    }

    [Fact]
    public void BotManeuverBackoff_StillHits_ButOverlapsLess()
    {
        var (a, ovA) = Fight(new SlashManeuver());
        var (b, ovB) = Fight(new SlashManeuver(), new BackoffManeuver());
        output.WriteLine($"slash only: overlap {ovA}, {a.Summary()}");
        output.WriteLine($"slash+backoff: overlap {ovB}, {b.Summary()}");
        Assert.True(b.DamageToP1 > 0f, "Backoff must not stop the bot from landing hits.");
        Assert.True(ovB < ovA, $"Backoff should reduce face-hugging ({ovB} vs {ovA}).");
    }

    // Floor + walls + ceiling row, with a 4-tile-thick wall at columns 14..17.
    private static ChunkMap Walled()
    {
        var rows = new List<string>();
        for (int y = 0; y < 12; y++)
        {
            var sb = new StringBuilder();
            for (int x = 0; x < 40; x++)
                sb.Append(y >= 8 || x == 0 || x == 39 || y == 0 || (x >= 14 && x <= 17) ? 'X' : 'O');
            rows.Add(sb.ToString());
        }
        return BotArena.FromAscii(string.Join("\n", rows));
    }

    [Fact]
    public void BotManeuverBreach_DigsThroughWall_AndHitsP1()
    {
        var r = BotArena.Fight(Walled(), new Vector2(60f, 70f), new Vector2(230f, 70f), 1500,
                               BotArena.Only(new SlashManeuver(), new BreachManeuver()));
        output.WriteLine(r.Summary());
        // Rows 1..7 of the wall columns were solid; digging must have opened some of them.
        int solid = 0;
        for (int tx = 10; tx <= 13; tx++) for (int ty = 1; ty <= 7; ty++) if (r.Sim.Chunks.GetCellState(tx, ty) == TileState.Solid) solid++;
        Assert.True(solid < 28, $"The wall should have been carved ({solid}/28 left).");
        Assert.True(r.FirstHitOnP1 >= 0, "The bot should dig through the wall and hit P1.");
    }
}
