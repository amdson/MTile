using Microsoft.Xna.Framework;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

public class BotManeuverAntiAirStabTests(ITestOutputHelper output)
{
    [Fact]
    public void AntiAirStab_HitsOpponent_FallingStraightDown()
    {
        // P1 spawns above the bot and simply falls onto it.
        var r = BotArena.Fight(BotArena.Flat(), new Vector2(200f, -60f), new Vector2(200f, 70f), 200,
                               BotArena.Only(new AntiAirStabManeuver()));
        output.WriteLine(r.Summary());
        Assert.True(r.Saw("StabAction"));
        Assert.True(r.DamageToP1 > 0f, "upward stab should connect");
    }

    [Fact]
    public void AntiAirStab_HitsJumpInFromSide()
    {
        // P1 hops (Space held) while walking toward the bot.
        var r = BotArena.Fight(BotArena.Flat(), new Vector2(120f, 70f), new Vector2(200f, 70f), 240,
                               BotArena.Only(new AntiAirStabManeuver()),
                               p1: (f, sim) => new PlayerInput { Right = true, Space = f > 5 && f < 40 });
        output.WriteLine(r.Summary());
        Assert.True(r.Saw("StabAction"));
        Assert.True(r.DamageToP1 > 0f, "upward stab should connect");
    }
}
