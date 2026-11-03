using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// AvalancheChargeManeuver end to end: P1 (AFK) on open ground 9–16 tiles away; the bot
// erupts a shallow wave under its own feet, rides it in, jumps out and air-slashes.
public class BotManeuverAvalancheChargeTests(ITestOutputHelper output)
{
    private static readonly Vector2 P2Spawn = new(20f, BotArena.FloorBodyY);

    private BotArena.Run Fight(int tiles, BrawlerBotInputSource bot, int frames = 240, bool trace = false)
    {
        var p1 = P2Spawn + new Vector2(tiles * Chunk.TileSize, 0f);
        var lines = new List<string>();
        var r = BotArena.Fight(BotArena.Flat(floorRows: 12, wallCols: 3), p1, P2Spawn, frames, bot,
            p1: (f, sim) =>
            {
                var b = sim.SecondaryPlayers[0].Player;
                if (trace && f % 4 == 0)
                    lines.Add($"f{f,3} {b.Body.Position.X,7:F1},{b.Body.Position.Y,6:F1} v {b.Body.Velocity.X,6:F0},{b.Body.Velocity.Y,6:F0} " +
                              $"{b.CurrentStateName,-20} {b.CurrentActionName,-18} {bot.Active?.Name}  P1 {sim.Player.Body.Position.X:F0},{sim.Player.Body.Position.Y:F0}");
                return default;
            });
        foreach (var l in lines) output.WriteLine(l);
        output.WriteLine($"{tiles} tiles: {r.Summary()}");
        return r;
    }

    // The mechanics: rides (TerrainCarriedState), jumps out, and the air slash connects,
    // 9–16 tiles out. (Measured: first hit 84–108 frames; the dash reference below hits
    // in 13–49 — see the maneuver header for why that leaves it a chase tool.)
    [Theory]
    [InlineData(9)]
    [InlineData(11)]
    [InlineData(13)]
    [InlineData(16)]
    public void OpenGround_RidesJumpsAndHits(int tiles)
    {
        // StaticUtility forces it on a standing target to exercise the mechanics.
        var m = new AvalancheChargeManeuver { StaticUtility = 1f };
        var r = Fight(tiles, BotArena.Only(m), trace: tiles == 11);
        output.WriteLine($"fired@{m.FiredFrame} jump@{m.JumpFrame} swing@{m.SwingFrame} firstHit@{r.FirstHitOnP1}");
        Assert.True(m.Fired, "never erupted");
        Assert.Contains(nameof(TerrainCarriedState), r.P2States);
        Assert.True(m.Jumped && m.Swung, $"jumped {m.Jumped} swung {m.Swung}");
        Assert.True(r.DamageToP1 > 0f, "the air slash never connected");
        Assert.Equal(0f, r.DamageToP2);
    }

    // Reference: the dash alone at the same ranges (it can't reach the far ones).
    [Theory]
    [InlineData(9)]
    [InlineData(13)]
    [InlineData(16)]
    public void Reference_DashOnly(int tiles)
    {
        var r = Fight(tiles, BotArena.Only(new DashManeuver(), new SlashManeuver()));
        output.WriteLine($"dash first hit @{r.FirstHitOnP1}");
    }

    // A P1 running away (holding Right from frame 0): walking matches their speed and
    // only catches them at the arena wall; the ride outruns them.
    private BotArena.Run Flee(int tiles, BrawlerBotInputSource bot)
    {
        var p2 = new Vector2(-5f, BotArena.FloorBodyY);
        var p1 = p2 + new Vector2(tiles * Chunk.TileSize, 0f);
        var r = BotArena.Fight(BotArena.Flat(floorRows: 12, wallCols: 3), p1, p2, 300, bot,
            p1: (f, sim) => new PlayerInput { Right = true });
        output.WriteLine($"flee {tiles}: {r.Summary()}");
        return r;
    }

    [Theory]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(12)]
    public void FleeingTarget_ChargeCatchesBeforeWalking(int tiles)
    {
        var m = new AvalancheChargeManeuver();
        var charge = Flee(tiles, BotArena.Only(m, new SlashManeuver()));
        var walk   = Flee(tiles, BotArena.Only(new SlashManeuver()));
        output.WriteLine($"charge first hit {charge.FirstHitOnP1} (fired {m.FiredFrame}, jump {m.JumpFrame}); walk-only {walk.FirstHitOnP1}");
        Assert.True(m.Fired);
        Assert.Contains(nameof(TerrainCarriedState), charge.P2States);
        Assert.True(charge.FirstHitOnP1 >= 0, "the charge never hit the fleeing target");
        Assert.True(walk.FirstHitOnP1 < 0 || charge.FirstHitOnP1 < walk.FirstHitOnP1,
            $"charge {charge.FirstHitOnP1} should beat walking {walk.FirstHitOnP1}");
    }

    // A standing target: walking / the dash are faster, so by default it doesn't score.
    [Fact]
    public void StandingTarget_DefaultDoesNotCharge()
    {
        var m = new AvalancheChargeManeuver();
        var r = Fight(11, BotArena.Only(m, new SlashManeuver()), frames: 150);
        Assert.False(m.Fired);
        Assert.True(r.DamageToP1 > 0f, "walk + slash should have handled a standing target");
    }

    [Fact]
    public void TooClose_DoesNotCharge()
    {
        var m = new AvalancheChargeManeuver { StaticUtility = 1f };
        var r = Fight(4, BotArena.Only(m), frames: 120);
        Assert.False(m.Fired);
    }
}
