using MTile.Net;
using Xunit;
using Xunit.Abstractions;
namespace MTile.Tests.Sim;

public class BotManeuverDuelistCitadelTests(ITestOutputHelper output)
{
    [Fact]
    public void Deterministic()
    {
        var a = BotDuel.Run("plain_close", BotDuel.Brawler, CitadelBot.Create, 7, 900);
        var b = BotDuel.Run("plain_close", BotDuel.Brawler, CitadelBot.Create, 7, 900);
        Assert.Equal(a.ToString(), b.ToString());
        Assert.True(a.DamageToP1 + a.DamageToP2 > 0);
    }

    [Theory]
    [InlineData("Brawler")]
    [InlineData("Bulldog")]
    [InlineData("Riposte")]
    [InlineData("Rampart")]
    public void Benchmark(string opponent)
    {
        DuelBot other = opponent switch {
            "Bulldog" => BulldogBot.Create, "Riposte" => RiposteBot.Create,
            "Rampart" => RampartBot.Create, _ => BotDuel.Brawler };
        var table = BotDuel.RoundRobin(new (string, DuelBot)[] {
            ("Citadel", CitadelBot.Create), (opponent, other)
        }, new[] { 1, 2 }, log: output.WriteLine);
        foreach (var row in table) output.WriteLine(row.ToString());
        Assert.All(table, row => Assert.True(float.IsFinite(row.DamageDealt)));
    }
}
