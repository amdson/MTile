using Microsoft.Xna.Framework;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

public class BotManeuverCornerEscapeTests(ITestOutputHelper output)
{
    // Flat(): left wall occupies x in [-44,-33]; the bot hugs it and P1 stands to its right.
    [Fact]
    public void Cornered_DashesPastToOpenSide()
    {
        var r = BotArena.Fight(BotArena.Flat(), new Vector2(0f, 70f), new Vector2(-24f, 70f), 50,
                               BotArena.Only(new CornerEscapeManeuver()));
        output.WriteLine(r.Summary());
        Assert.True(r.Saw("DashState") || r.P2States.Contains("DashState"), "bot should dash");
        Assert.True(r.P2.Body.Position.X > r.P1.Body.Position.X + 20f,
                    $"bot should end past P1: bot {r.P2.Body.Position.X} p1 {r.P1.Body.Position.X}");
    }

    [Fact]
    public void NotCornered_DoesNotFire()
    {
        var r = BotArena.Fight(BotArena.Flat(), new Vector2(200f, 70f), new Vector2(170f, 70f), 60,
                               BotArena.Only(new CornerEscapeManeuver()));
        output.WriteLine(r.Summary());
        Assert.DoesNotContain(r.P2Inputs, i => i.Space);
    }
}
