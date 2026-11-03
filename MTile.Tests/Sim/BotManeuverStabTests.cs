using Microsoft.Xna.Framework;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

public class BotManeuverStabTests(ITestOutputHelper output)
{
    [Fact]
    public void Stab_FiresAndLaunches_AtMidRange()
    {
        // 70 px apart: outside slash reach (42), inside stab reach.
        var r = BotArena.Fight(BotArena.Flat(), new Vector2(200f, 70f), new Vector2(270f, 70f), 240,
                               BotArena.Only(new StabManeuver()));
        output.WriteLine(r.Summary());
        Assert.True(r.Saw("StabAction"));
        Assert.True(r.DamageToP1 > 0f, "stab should connect");
    }

    [Fact]
    public void AirHammer_Slams_TargetBelow()
    {
        // A same-level hop only gets ~25 px over the target and the hammer box starts 23 px
        // below the body, so the bot hops off the raised right-hand block (22 px up) onto
        // P1 standing on the lower floor beside it.
        var r = BotArena.Fight(BotArena.Flat(step: true), new Vector2(270f, 67f), new Vector2(300f, 45f), 240,
                               BotArena.Only(new AirHammerManeuver()));
        output.WriteLine(r.Summary());
        Assert.True(r.Saw("AirHammerAction"));
        Assert.True(r.FirstHitOnP1 is >= 0 and < 60, $"hammer should land off the ledge (first hit {r.FirstHitOnP1})");
        Assert.True(r.DamageToP1 >= 1f, "hammer outer box does 1.0 body damage");
    }

    [Fact]
    public void AirWhipKick_CrossUp_KicksBackward()
    {
        var r = BotArena.Fight(BotArena.Flat(), new Vector2(200f, 70f), new Vector2(250f, 70f), 240,
                               BotArena.Only(new AirWhipKickManeuver()));
        output.WriteLine(r.Summary());
        Assert.True(r.Saw("AirWhipKick"));
        Assert.True(r.DamageToP1 >= 0.5f, "kick should connect (stock 0.5 body damage)");
    }
}
