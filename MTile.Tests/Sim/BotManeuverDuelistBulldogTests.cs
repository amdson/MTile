using System.Collections.Generic;
using System.Linq;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Bulldog, the rushdown duelist (Net/Bot/Duelists/Bulldog/): deterministic, plays both
// sides, lands its slash string, and beats the stock Brawler in the league format.
public class BotManeuverDuelistBulldogTests(ITestOutputHelper output)
{
    [Fact]
    public void Bout_IsDeterministic()
    {
        var a = BotDuel.Run("hall_close", BulldogBot.Create, BotDuel.Brawler, seed: 3, frames: 1200);
        var b = BotDuel.Run("hall_close", BulldogBot.Create, BotDuel.Brawler, seed: 3, frames: 1200);
        output.WriteLine(a.ToString());
        Assert.Equal(a.ToString(), b.ToString());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void DrivesEitherSide_AndFights(int selfIndex)
    {
        var r = selfIndex < 0
            ? BotDuel.Run("plain_close", BulldogBot.Create, BotDuel.Brawler, seed: 1, frames: 900)
            : BotDuel.Run("plain_close", BotDuel.Brawler, BulldogBot.Create, seed: 1, frames: 900);
        output.WriteLine(r.ToString());
        float dealt = selfIndex < 0 ? r.DamageToP2 : r.DamageToP1;
        Assert.True(dealt >= 2f, $"Bulldog dealt only {dealt:F1} in 15 s");
    }

    // Against a target that does nothing, one opener runs the whole ground string:
    // S1 → S2 → S3 all connect (1.5 HP).
    [Fact]
    public void String_ChainsAllThreeHits()
    {
        var sim = BotDuel.BuildArena("plain_close");
        var bot = new BulldogBot(1, 0);
        bot.Maneuvers.RemoveAll(m => m is not BulldogString);
        var dummy = sim.Player;
        var me = sim.SecondaryPlayers[0].Player;
        var hitsBy = new List<string>();
        float prev = 0f;
        for (int f = 0; f < 330; f++)
        {
            sim.Step(default, bot.Poll(sim, f));
            if (dummy.Combat.DamageTaken > prev) hitsBy.Add(me.CurrentActionName);
            prev = dummy.Combat.DamageTaken;
        }
        output.WriteLine(string.Join(", ", hitsBy));
        Assert.Equal(new[] { "GroundSlash1", "GroundSlash2", "GroundSlash3" }, hitsBy.Take(3));
    }

    [Fact]
    public void RoundRobin_BeatsBrawler()
    {
        var bots = new List<(string, DuelBot)> { ("Bulldog", BulldogBot.Create), ("Brawler", BotDuel.Brawler) };
        var table = BotDuel.RoundRobin(bots, new[] { 1, 2, 3, 4 }, BotDuel.Arenas, BotDuel.DefaultFrames,
                                       s => output.WriteLine(s));
        foreach (var s in table) output.WriteLine(s.ToString());
        var me  = table.First(s => s.Name == "Bulldog");
        var opp = table.First(s => s.Name == "Brawler");
        Assert.True(me.Points > opp.Points, $"Bulldog {me.Points} vs Brawler {opp.Points}");
        Assert.True(me.W > me.L * 3, $"record {me.W}-{me.D}-{me.L}");
    }
}
