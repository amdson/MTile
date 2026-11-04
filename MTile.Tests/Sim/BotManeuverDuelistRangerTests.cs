using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Ranger (Net/Bot/Duelists/Ranger): Bulldog + RouteManeuver. Determinism (league rule 4)
// and the reason it exists — it follows a camper up the column it stacked.
public class BotManeuverDuelistRangerTests(ITestOutputHelper output)
{
    [Fact]
    public void Deterministic()
    {
        var a = BotDuel.Run("plain_close", CamperBot.Tower, RangerBot.Create, 5, 900);
        var b = BotDuel.Run("plain_close", CamperBot.Tower, RangerBot.Create, 5, 900);
        output.WriteLine(a.ToString());
        Assert.Equal(a.ToString(), b.ToString());
    }

    [Fact]
    public void FollowsACamperUpItsColumn()
    {
        var ranger  = ZzzCamperEval.Bout("Ranger",  RangerBot.Create,  "plain_close", 1, 1500, tower: 15);
        var bulldog = ZzzCamperEval.Bout("Bulldog", BulldogBot.Create, "plain_close", 1, 1500, tower: 15);
        output.WriteLine($"{ranger}\n{bulldog}");
        Assert.True(ranger.DmgToCamper >= 1f, $"Ranger dealt only {ranger.DmgToCamper:F1} to the camper");
        Assert.True(ranger.DmgToCamper > bulldog.DmgToCamper, "routing should beat plain Bulldog against a camper");
    }
}
