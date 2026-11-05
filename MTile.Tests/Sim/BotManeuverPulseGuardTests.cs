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
        // Baseline: a bot that stands there (no maneuvers), slashed in true strings - a
        // click every 17 frames chains S1 -> S2 -> S3 at the combo cadence (2026-11-05: the
        // light slashes land at 4-7f, under the bot's 6f perception, so the parry is a read
        // on the string's follow-up rather than a reaction to a lone swing every 40f).
        var none = BotArena.Fight(BotArena.Flat(), new Vector2(100f, 67f), new Vector2(124f, 67f), 240,
                                  BotArena.Only(), P1Slasher(17));
        var guard = BotArena.Fight(BotArena.Flat(), new Vector2(100f, 67f), new Vector2(124f, 67f), 240,
                                   BotArena.Only(new GuardManeuver()), P1Slasher(17));
        output.WriteLine("no guard: " + none.Summary());
        output.WriteLine("guard   : " + guard.Summary());
        Assert.True(none.DamageToP2 > 0f, "Baseline P1 slashes should hurt an idle bot.");
        Assert.True(guard.Saw("GuardAction"), "Bot should raise a guard against incoming slashes.");
        // Not strictly less: a stun now has first-wins + immunity (CombatState, 2026-11-05),
        // so the second parry's counter no longer re-stuns the slasher and one more exchange
        // can land either way. Guarding must never cost MORE, and the parry must pay off.
        Assert.True(guard.DamageToP2 <= none.DamageToP2,
            $"Guarding should not increase damage ({guard.DamageToP2} vs {none.DamageToP2}).");
        Assert.True(guard.DamageToP1 > none.DamageToP1,
            $"The parry should pay off as a counter (P1 lost {guard.DamageToP1} vs {none.DamageToP1} unguarded).");
    }

    [Fact]
    public void Guard_ParryIsPunishedWithRetaliate()
    {
        var r = BotArena.Fight(BotArena.Flat(), new Vector2(100f, 67f), new Vector2(124f, 67f), 240,
                               BotArena.Only(new GuardManeuver()), P1Slasher(17));
        output.WriteLine(r.Summary());
        Assert.True(r.Saw("GuardRetaliateAction"), "A successful parry should be followed by the counter.");
        Assert.True(r.DamageToP1 > 0f, $"The counter should connect (P1 lost {r.DamageToP1}).");
    }
}
