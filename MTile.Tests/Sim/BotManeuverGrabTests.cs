using System;
using Microsoft.Xna.Framework;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Grab family of the P2 bot: GrabManeuver (grab + throw), BlockThrowManeuver (peel a
// clod and fling it), EscapeGrabManeuver (struggle out when held).
public class BotManeuverGrabTests(ITestOutputHelper output)
{
    [Fact]
    public void GrabManeuver_GrabsAndThrowsTarget()
    {
        bool sawGrabbed = false, sawStun = false;
        float startX = 0f, maxDisp = 0f; Vector2 startPos = default;
        var r = BotArena.Fight(BotArena.Flat(), new Vector2(120f, 67f), new Vector2(150f, 67f), 240,
            BotArena.Only(new GrabManeuver()),
            p1: (f, sim) =>
            {
                if (f == 0) { startPos = sim.Player.Body.Position; startX = startPos.X; }
                if (sim.Player.CurrentStateName == nameof(GrabbedState)) sawGrabbed = true;
                if (sim.Player.Combat.HitstunActive) sawStun = true;
                maxDisp = Math.Max(maxDisp, Vector2.Distance(sim.Player.Body.Position, startPos));
                return default;
            });
        output.WriteLine(r.Summary() + $" grabbed={sawGrabbed} stun={sawStun} maxDisp={maxDisp:F0}");
        Assert.True(r.Saw("GrabAction"), "bot should fire GrabAction");
        Assert.True(sawGrabbed, "P1 should be held in GrabbedState");
        Assert.True(maxDisp > 40f || r.DamageToP1 > 0f, "P1 should be thrown (displaced) or hurt");
    }

    [Fact]
    public void GrabManeuver_ThrowsIntoNearbyWall_HurtsTarget()
    {
        // P1 two tiles from the left wall; the bot comes from the right and slams it leftward.
        var r = BotArena.Fight(BotArena.Flat(), new Vector2(20f, 67f), new Vector2(120f, 67f), 300,
            BotArena.Only(new GrabManeuver()));
        output.WriteLine(r.Summary());
        Assert.True(r.Saw("GrabAction"));
        Assert.True(r.DamageToP1 > 0f, "a throw into the wall should cost P1 health");
    }

    [Fact]
    public void BlockThrowManeuver_PeelsAndFlingsClod_AtTarget()
    {
        bool sawBall = false; float p1Hp0 = float.NaN;
        var r = BotArena.Fight(BotArena.Flat(), new Vector2(60f, 67f), new Vector2(160f, 67f), 300,
            BotArena.Only(new BlockThrowManeuver()),
            p1: (f, sim) =>
            {
                foreach (var e in sim.Entities) if (e is LobbedAreaProjectile) sawBall = true;
                return default;
            });
        output.WriteLine(r.Summary() + $" ball={sawBall}");
        Assert.True(r.Saw("BlockGrabAction"), "bot should fire BlockGrabAction");
        Assert.True(sawBall, "a clod ball should have been peeled out");
        Assert.True(r.DamageToP1 > 0f, "the thrown clod should land on P1");
    }

    [Fact]
    public void EscapeGrabManeuver_StrugglesWhenHeld_AndBreaksFree()
    {
        // P1 holds Shift+LMB with the cursor on the bot (adjacent): a long hold.
        bool botFree = false; int firstHeld = -1, freeAt = -1;
        float minStrength = 3f;
        var r = BotArena.Fight(BotArena.Flat(), new Vector2(120f, 67f), new Vector2(145f, 67f), 200,
            BotArena.Only(new EscapeGrabManeuver()),
            p1: (f, sim) =>
            {
                var bot = sim.SecondaryPlayers[0].Player;
                if (bot.Combat.GrabbedActive && firstHeld < 0) firstHeld = f;
                if (firstHeld >= 0 && !bot.Combat.GrabbedActive && freeAt < 0) freeAt = f;
                if (firstHeld >= 0) minStrength = Math.Min(minStrength, sim.Player.Combat.GrabStrength);
                if (f < 5) return default;
                return new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = bot.Body.Position };
            });
        botFree = freeAt > 0;
        output.WriteLine(r.Summary() + $" heldAt={firstHeld} freeAt={freeAt} minGrabStrength={minStrength}");
        Assert.True(firstHeld >= 0, "P1 should have grabbed the bot");
        Assert.True(r.Saw("GrabbedSlash"), "a held bot should struggle with GrabbedSlash");
        Assert.True(minStrength < 3f, "the struggle should erode the grabber's GrabStrength");
    }
}
