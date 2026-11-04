using Microsoft.Xna.Framework;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

public class BotManeuverAntiAirSidestepTests(ITestOutputHelper output)
{
    // P1 starts beside the bot, jumps over it, and down-chops (cursor straight below,
    // click) whenever it is well above the floor.
    private static PlayerInput Jumper(int f, Simulation sim)
    {
        var p = new PlayerInput();
        var me = sim.Player.Body.Position;
        p.MouseWorldPosition = me + new Vector2(0f, 100f);
        if (f >= 5 && f < 25) { p.Space = true; p.Right = true; }
        if (f > 20 && me.Y < BotArena.FloorBodyY - 25f && f % 4 == 0) p.LeftClick = true;
        return p;
    }

    private static BotArena.Run Go(BotManeuver m)
        => BotArena.Fight(BotArena.Flat(), new Vector2(200f, BotArena.FloorBodyY), new Vector2(240f, BotArena.FloorBodyY),
                          200, m == null ? BotArena.Only() : BotArena.Only(m), p1: Jumper);

    [Fact]
    public void Sidestep_TakesLessDamage_ThanStandingStill()
    {
        var baseline = Go(null);
        var r = Go(new AntiAirSidestepManeuver());
        output.WriteLine("baseline: " + baseline.Summary());
        output.WriteLine("sidestep: " + r.Summary());
        Assert.True(r.DamageToP2 <= baseline.DamageToP2, $"{r.DamageToP2} vs {baseline.DamageToP2}");
        Assert.True(r.DamageToP2 < baseline.DamageToP2 || r.DamageToP1 > 0f, "should dodge or punish");
    }
}
