using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;
namespace MTile.Tests.Sim;
public class ZzzBotDiag(ITestOutputHelper output)
{
    [Fact]
    public void Attribution()
    {
        foreach (int seed in new[]{5,6,7})
        {
        var bot = new BrawlerBotInputSource(seed: seed);
        var hurt = new Dictionary<string, float>(); var dealt = new Dictionary<string, float>();
        float p1p = 0, p2p = 0; string lastName = "idle";
        BotArena.Fight(BotArena.Flat(floorRows: 20, wallCols: 6), new Vector2(60f, 70f), new Vector2(260f, 70f), 1200, bot,
            (f, sim) => {
                var p2 = sim.SecondaryPlayers[0].Player;
                if (bot.Active != null) lastName = bot.Active.Name; else if (f % 30 == 0) lastName = "idle";
                float d1 = sim.Player.Combat.DamageTaken, d2 = p2.Combat.DamageTaken;
                if (d2 > p2p) hurt[lastName + "/" + p2.CurrentActionName] = hurt.GetValueOrDefault(lastName + "/" + p2.CurrentActionName) + d2 - p2p;
                if (d1 > p1p) dealt[lastName] = dealt.GetValueOrDefault(lastName) + d1 - p1p;
                p1p = d1 < p1p ? 0 : d1; p2p = d2 < p2p ? 0 : d2;
                float dx = p2.Body.Position.X - sim.Player.Body.Position.X;
                bool close = System.MathF.Abs(dx) < 40f;
                return new PlayerInput { Left = !close && (f / 90) % 2 == 0, Right = !close && (f / 90) % 2 == 1,
                    LeftClick = close && f % 24 == 0, MouseWorldPosition = p2.Body.Position };
            });
        output.WriteLine($"seed {seed} HURT: " + string.Join(", ", hurt.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value:F2}")));
        output.WriteLine($"seed {seed} DEALT: " + string.Join(", ", dealt.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value:F2}")));
        output.WriteLine("starts " + string.Join(", ", bot.Starts.Select(k => $"{k.Key}×{k.Value}")));
        }
    }
}
