using Microsoft.Xna.Framework;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

public class BotManeuverAntiAirJumpSlashTests(ITestOutputHelper output)
{
    [Fact]
    public void JumpSlash_HitsOpponent_DroppingOntoBot()
    {
        // P1 spawns in the air above the bot and just falls (no input): the bot should
        // hop up and AirSlash it.
        var r = BotArena.Fight(BotArena.Flat(), new Vector2(210f, 10f), new Vector2(220f, 67f), 120,
                               BotArena.Only(new AntiAirJumpSlashManeuver()));
        output.WriteLine(r.Summary());
        Assert.True(r.Saw("AirSlash1"), "bot should air-slash the overhead target");
        Assert.True(r.DamageToP1 > 0f, "air slash should connect");
    }

    [Fact]
    public void JumpSlash_HitsOpponent_JumpingOverBot()
    {
        // P1 hops toward P2 and over it.
        
        var r = BotArena.Fight(BotArena.Flat(), new Vector2(150f, 67f), new Vector2(220f, 67f), 200,
                               BotArena.Only(new AntiAirJumpSlashManeuver()), setup: null,
                               p1: (f, sim) => { return new PlayerInput { Right = f > 10, Space = f is > 40 and < 70 }; });
        output.WriteLine(r.Summary());
        // The jumper crosses behind the bot, so the click may resolve as the air turn-around slash.
        Assert.True(r.Saw("AirSlash1") || r.Saw("AirTurnSlash"), "bot should air-slash the jumper");
        Assert.True(r.DamageToP1 > 0f, "air slash should connect");
    }
}
