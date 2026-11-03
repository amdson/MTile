using System.Collections.Generic;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// The bot-vs-bot duel harness (Net/Bot/BotDuel.cs): bouts run, are deterministic, and
// count kills/damage on both sides. The league itself is run by hand, not as a test.
public class BotManeuverDuelTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("hall_close")]
    [InlineData("hall_far")]
    [InlineData("plain_close")]
    public void BrawlerMirror_FightsAndIsDeterministic(string arena)
    {
        var a = BotDuel.Run(arena, BotDuel.Brawler, BotDuel.Brawler, seed: 1, frames: 1800);
        var b = BotDuel.Run(arena, BotDuel.Brawler, BotDuel.Brawler, seed: 1, frames: 1800);
        output.WriteLine(a.ToString());
        Assert.Equal(a.ToString(), b.ToString());
        Assert.True(a.DamageToP1 + a.DamageToP2 > 0.5f, "the two brawlers should actually fight");
    }

    [Fact]
    public void BotDrivingP1_TargetsP2()
    {
        var sim = BotDuel.BuildArena("hall_close");
        var bot = new BrawlerBotInputSource(seed: 1, selfIndex: -1);
        float startGap = System.MathF.Abs(sim.Player.Body.Position.X - sim.SecondaryPlayers[0].Player.Body.Position.X);
        for (int f = 0; f < 240; f++) sim.Step(bot.Poll(sim, f), default);
        float gap = System.MathF.Abs(sim.Player.Body.Position.X - sim.SecondaryPlayers[0].Player.Body.Position.X);
        output.WriteLine($"gap {startGap:F0} -> {gap:F0}, P2 lost {sim.SecondaryPlayers[0].Player.Combat.DamageTaken}");
        Assert.True(gap < startGap * 0.5f || sim.SecondaryPlayers[0].Player.Combat.DamageTaken > 0f);
    }
}
