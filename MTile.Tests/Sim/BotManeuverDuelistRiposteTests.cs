using System.Collections.Generic;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Riposte (Net/Bot/Duelists/Riposte): the counter-fighter duelist. It is deterministic,
// plays from either side, and beats the stock Brawler in the BotDuel round robin.
public class BotManeuverDuelistRiposteTests(ITestOutputHelper output)
{
    [Fact]
    public void Deterministic_BothSides()
    {
        foreach (bool riposteIsP1 in new[] { true, false })
        {
            DuelBot p1 = riposteIsP1 ? RiposteBot.Create : BotDuel.Brawler;
            DuelBot p2 = riposteIsP1 ? BotDuel.Brawler : RiposteBot.Create;
            var a = BotDuel.Run("hall_close", p1, p2, seed: 3, frames: 900);
            var b = BotDuel.Run("hall_close", p1, p2, seed: 3, frames: 900);
            output.WriteLine(a.ToString());
            Assert.Equal(a.ToString(), b.ToString());
            Assert.True(a.DamageToP1 + a.DamageToP2 > 0.5f, "the bots should actually fight");
        }
    }

    [Fact]
    public void BeatsBrawler_RoundRobin()
    {
        var table = BotDuel.RoundRobin(
            new List<(string, DuelBot)> { ("Riposte", RiposteBot.Create), ("Brawler", BotDuel.Brawler) },
            seeds: new[] { 1, 2, 3, 4 }, log: output.WriteLine);
        foreach (var s in table) output.WriteLine(s.ToString());
        var r = table.Find(s => s.Name == "Riposte");
        var b = table.Find(s => s.Name == "Brawler");
        Assert.True(r.Points > b.Points, "Riposte should out-score the Brawler");
        Assert.True(r.W > r.L * 3, $"Riposte should win the clear majority of bouts ({r.W}-{r.D}-{r.L})");
        Assert.True(r.Kills > r.Deaths, "and out-kill it");
    }
}
