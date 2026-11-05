using System;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// M3 of Plans/BOT_BEHAVIOR_ARCHITECTURE.md: each maneuver declares its own Move claim, and
// the controller enforces it. Unit pins on the claim table, plus two scenarios on a real
// Simulation showing the claim is what the emitted input obeys.
public class BotManeuverClaimTests(ITestOutputHelper output)
{
    [Fact]
    public void ClaimTable_Pins()
    {
        Assert.Equal(Category.Action, new GuardManeuver().Category);
        Assert.Equal(MoveClaim.Mode.Forbid, new GuardManeuver().Move.Kind);
        Assert.Equal(MoveKeys.All, new GuardManeuver().Move.Forbidden);
        Assert.Equal(MoveKeys.All, new PulseManeuver().Move.Forbidden);
        Assert.Equal(MoveKeys.All, new BulldogGuard().Move.Forbidden);

        Assert.Equal(Category.Reflex, new CornerEscapeManeuver().Category);
        Assert.Equal(MoveClaim.Mode.Exclusive, new CornerEscapeManeuver().Move.Kind);
        Assert.Equal(Category.Reflex, new BackoffManeuver().Category);
        Assert.Equal(Category.Reflex, new AntiAirSidestepManeuver().Category);

        Assert.Equal(Category.Action, new DashManeuver().Category);
        Assert.Equal(MoveClaim.Mode.Exclusive, new DashManeuver().Move.Kind);
        Assert.Equal(MoveClaim.Mode.Exclusive, new DownAirManeuver().Move.Kind);

        var burst = new BurstManeuver().Move;
        Assert.Equal(MoveClaim.Mode.Forbid, burst.Kind);
        Assert.Equal(MoveKeys.Space | MoveKeys.Down, burst.Forbidden);
        Assert.False(burst.SuspendsStance);
    }

    // Move=None: the slash fires while the chase stance keeps walking.
    [Fact]
    public void Slash_FiresWhileChaseStanceIsStillWalking()
    {
        var sim = BotDuel.BuildArena("plain_close");
        var bot = new BulldogBot(1, 0);
        int both = 0;
        for (int f = 0; f < 600; f++)
        {
            var inp = bot.Poll(sim, f);
            sim.Step(default, inp);
            var c = bot.Controller;
            if ((inp.Left || inp.Right) && inp.LeftClick && c.CurrentStance is ChaseStance
                && c.CommittedAction is ManeuverBehaviour mb && mb.Move.Kind == MoveClaim.Mode.None)
                both++;
        }
        output.WriteLine($"walk+click frames: {both}");
        Assert.True(both > 0, "never saw Left/Right and LeftClick together under ChaseStance");
    }

    // Still: a committed guard leaves Move empty even though the stance would walk.
    [Fact]
    public void Guard_LeavesMoveEmpty()
    {
        var sim = BotDuel.BuildArena("plain_close");
        var bot = new BulldogBot(1, 0);
        bot.Maneuvers.RemoveAll(m => m is not GuardManeuver);
        int guardFrames = 0, bad = 0;
        for (int f = 0; f < 900; f++)
        {
            // P1 swings every 40 frames, aimed at the bot.
            var p1 = new PlayerInput { MouseWorldPosition = sim.SecondaryPlayers[0].Player.Body.Position, LeftClick = f % 40 < 3 };
            var inp = bot.Poll(sim, f);
            sim.Step(p1, inp);
            if (bot.Controller.CommittedAction is ManeuverBehaviour mb && mb.Maneuver is GuardManeuver)
            {
                guardFrames++;
                if (inp.Left || inp.Right || inp.Space || inp.Down) bad++;
            }
        }
        output.WriteLine($"guard frames {guardFrames}, with a move key {bad}");
        Assert.True(guardFrames > 0, "guard never committed");
        Assert.Equal(0, bad);
    }
}
