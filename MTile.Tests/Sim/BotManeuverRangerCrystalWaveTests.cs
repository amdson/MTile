using Microsoft.Xna.Framework;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Ranger's crystal wave: fires at a static opponent at mid range, repeatedly (8 s game cooldown).
public class BotManeuverRangerCrystalWaveTests(ITestOutputHelper output)
{
    private static float Ts => Chunk.TileSize;

    [Fact]
    public void RangerWave_FiresAtMidRange_AndHurts()
    {
        var p1 = new Vector2(80f, BotArena.FloorBodyY);
        var p2 = new Vector2(80f + 11 * Ts, BotArena.FloorBodyY);
        var m = new RangerCrystalWaveManeuver();
        var r = BotArena.Fight(BotArena.Flat(), p1, p2, 300, BotArena.Only(m));
        output.WriteLine(r.Summary() + $"; fired {m.Fired} @ {m.FiredFrame}");
        Assert.True(m.Fired, "the bot should launch a wave at a static target");
        Assert.True(r.DamageToP1 > 0f, "the wave should reach the target");
    }

    // The only rate limit is the game's 8 s wave cooldown (a bot-side 45 f retry delay aside):
    // it scores for a static target at range the moment the cooldown is up, and not before,
    // and not in melee reach. (A full duel can't show a second launch: the brain walks in.)
    [Fact]
    public void RangerWave_ScoresAtRange_OnlyWhenCooldownIsUp()
    {
        var sim = new Simulation(BotArena.Flat(), new Vector2(80f, BotArena.FloorBodyY));
        var (p2, _) = sim.AddSecondaryPlayer(new Vector2(80f + 11 * Ts, BotArena.FloorBodyY));
        for (int f = 0; f < 30; f++) sim.Step(default, default);
        var v = new BotView { Sim = sim, Self = p2, Target = sim.Player, Me = p2.Body.Position, TargetPos = sim.Player.Body.Position };
        var m = new RangerCrystalWaveManeuver();
        p2.Abilities.Meters.Build = 100f;
        float s = m.Score(v);
        output.WriteLine($"score {s}");
        Assert.InRange(s, 1.0f, 1.3f);                  // static target, no retreat needed
        p2.Abilities.Meters.CrystalCooldown = 3f;
        Assert.Equal(0f, m.Score(v));                   // game-side cooldown
        p2.Abilities.Meters.CrystalCooldown = 0f;
        v.TargetPos = p2.Body.Position - new Vector2(3 * Ts, 0f);
        Assert.Equal(0f, m.Score(v));                   // melee reach
        Assert.Equal(45, m.Cooldown);
    }
}
