using Microsoft.Xna.Framework;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Bot maneuvers: PulseManeuver (Circle gesture) and GuardManeuver (parry + counter).
public class BotManeuverPulseGuardTests
{
    private readonly ITestOutputHelper output;
    public BotManeuverPulseGuardTests(ITestOutputHelper output) => this.output = output;

    // P1 slashes toward P2 every `period` frames (1-frame LMB tap = Click).
    private static System.Func<int, Simulation, PlayerInput> P1Slasher(int period) => (f, sim) => new PlayerInput
    {
        LeftClick          = f % period == 0,
        MouseWorldPosition = sim.SecondaryPlayers[0].Player.Body.Position,
    };

    [Fact]
    public void Pulse_FiresAndHitsAdjacentTarget()
    {
        var r = BotArena.Fight(BotArena.Flat(), new Vector2(100f, 67f), new Vector2(120f, 67f), 120,
                               BotArena.Only(new PulseManeuver()));
        output.WriteLine(r.Summary());
        Assert.True(r.Saw("PulseAction"), "Circle gesture should be decoded into a PulseAction.");
        Assert.True(r.DamageToP1 > 0f, $"The ring should hit an adjacent target (P1 lost {r.DamageToP1}).");
    }

    [Fact]
    public void Pulse_NotScoredWhenTargetFar()
    {
        var r = BotArena.Fight(BotArena.Flat(), new Vector2(60f, 67f), new Vector2(250f, 67f), 5,
                               BotArena.Only(new PulseManeuver()));
        Assert.False(r.Saw("PulseAction"));
    }

    [Fact]
    public void Guard_ParriesSlashes_AndReducesDamage()
    {
        // Baseline: a bot that stands there (no maneuvers), slashed every 40 frames.
        var none = BotArena.Fight(BotArena.Flat(), new Vector2(100f, 67f), new Vector2(124f, 67f), 240,
                                  BotArena.Only(), P1Slasher(40));
        var guard = BotArena.Fight(BotArena.Flat(), new Vector2(100f, 67f), new Vector2(124f, 67f), 240,
                                   BotArena.Only(new GuardManeuver()), P1Slasher(40));
        output.WriteLine("no guard: " + none.Summary());
        output.WriteLine("guard   : " + guard.Summary());
        Assert.True(none.DamageToP2 > 0f, "Baseline P1 slashes should hurt an idle bot.");
        Assert.True(guard.Saw("GuardAction"), "Bot should raise a guard against incoming slashes.");
        Assert.True(guard.DamageToP2 < none.DamageToP2,
            $"Guarding should reduce damage ({guard.DamageToP2} vs {none.DamageToP2}).");
    }

    [Fact]
    public void Guard_ParryIsPunishedWithRetaliate()
    {
        var r = BotArena.Fight(BotArena.Flat(), new Vector2(100f, 67f), new Vector2(124f, 67f), 240,
                               BotArena.Only(new GuardManeuver()), P1Slasher(40));
        output.WriteLine(r.Summary());
        Assert.True(r.Saw("GuardRetaliateAction"), "A successful parry should be followed by the counter.");
        Assert.True(r.DamageToP1 > 0f, $"The counter should connect (P1 lost {r.DamageToP1}).");
    }
}
