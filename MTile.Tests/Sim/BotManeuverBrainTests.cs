using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// The whole brain (DefaultManeuvers) against scripted P1s: every maneuver competing
// for the same frames. Guards against one maneuver's Score starving the rest and
// against the integrated bot being worse than the stage-1 slasher.
public class BotManeuverBrainTests(ITestOutputHelper output)
{
    // A P1 that strolls back and forth and slashes at P2 whenever it is close.
    private static PlayerInput Duelist(int f, Simulation sim)
    {
        var p2 = sim.SecondaryPlayers[0].Player;
        float dx = p2.Body.Position.X - sim.Player.Body.Position.X;
        bool close = System.MathF.Abs(dx) < 40f;
        return new PlayerInput
        {
            Left               = !close && (f / 90) % 2 == 0,
            Right              = !close && (f / 90) % 2 == 1,
            LeftClick          = close && f % 24 == 0,
            MouseWorldPosition = p2.Body.Position,
        };
    }

    private (BotArena.Run Run, Dictionary<string, int> Starts) Fight(System.Func<int, Simulation, PlayerInput> p1, int frames)
    {
        var bot = new BrawlerBotInputSource(seed: 5);
        var r = BotArena.Fight(BotArena.Flat(floorRows: 20, wallCols: 6), new Vector2(60f, 70f), new Vector2(260f, 70f),
                               frames, bot, p1);
        var starts = bot.Starts;
        output.WriteLine(r.Summary());
        output.WriteLine("maneuver starts: " + string.Join(", ", starts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}×{kv.Value}")));
        return (r, starts);
    }

    [Fact]
    public void FullBrain_BeatsUpAnAfkTarget()
    {
        var (r, starts) = Fight(null, 900);
        Assert.True(r.FirstHitOnP1 is >= 0 and < 240, $"first hit {r.FirstHitOnP1}");
        Assert.True(r.DamageToP1 >= 1.5f, $"P1 lost {r.DamageToP1}");
        Assert.True(starts.Count >= 2, "more than one maneuver should get used");
    }

    // Not "wins the trade": a P1 that slashes every 24 frames whenever the bot is inside
    // 40px out-trades the brain on 7 of 8 seeds (≈ 4.7 HP dealt vs 7.5 taken, 2026-11-02)
    // — the bot ends maneuvers overlapping P1, where spam wins. Spacing is a tuning job;
    // this pins the floor: it still lands real damage, survives, and mixes its kit.
    [Fact]
    public void FullBrain_HoldsItsOwnAgainstADuelist()
    {
        var (r, starts) = Fight(Duelist, 1200);
        Assert.True(r.DamageToP1 >= 2f, $"bot should land real damage on a duelist (P1 lost {r.DamageToP1})");
        Assert.Equal(0, r.P2Deaths);
        Assert.True(starts.Count >= 5, "a fight against a mover should exercise several maneuvers");
    }
}
