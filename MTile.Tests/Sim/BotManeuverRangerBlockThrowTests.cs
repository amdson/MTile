using Microsoft.Xna.Framework;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

public class BotManeuverRangerBlockThrowTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(120f)]
    [InlineData(180f)]
    [InlineData(250f)]
    public void Throw_HitsStandingOpponent_AtRange(float gap)
    {
        var r = BotArena.Fight(BotArena.Flat(), new Vector2(80f, 70f), new Vector2(80f + gap, 70f), 300,
                               BotArena.Only(new RangerBlockThrowManeuver()));
        output.WriteLine(r.Summary());
        Assert.True(r.Saw("BlockGrabAction"));
        Assert.True(r.DamageToP1 > 0f, "clod should hit P1");
    }
}
