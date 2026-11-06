using System;
using System.Linq;
using Microsoft.Xna.Framework;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// todo.txt #7 bot hookup: WallUpManeuver answers a crystal wave, CrystalWaveManeuver
// launches one. Name matches the "BotManeuver" term (bot group).
public class BotManeuverCrystalWaveTests(ITestOutputHelper output)
{
    private static float Ts => Chunk.TileSize;
    private static int CellX(float x) => (int)MathF.Floor(x / Ts);

    // A bot that stands still: a maneuver that never scores, but owns nothing either —
    // the brain still chases, so instead pin P2 by giving it no maneuvers and a target
    // it is already level with. Used only as the "no wall" control.
    private static void SpawnP1Wave(Simulation sim)
    {
        // P1's crystal wave, skimming Flat()'s first air row (tile y 7) rightward.
        var ball = new MassBall(new Vector2(60f, 7 * Ts + Ts * 0.5f), new Vector2(320f, 0f), 160f,
                                TileType.Stone, Faction.Player1, crystal: true) { Team = Teams.Player };
        sim.SpawnEntity(ball);
    }

    private static string Row(ChunkMap c, int y, int x0, int x1)
    {
        var sb = new System.Text.StringBuilder();
        int now = c.TerrainFrame;
        for (int x = x0; x <= x1; x++)
            sb.Append(c.GetCellState(x, y) != TileState.Empty ? 'X'
                : c.Crystals.TryGet(x, y, out var cr) ? (cr.Pending ? ',' : cr.PhaseAt(now) == CrystalPhase.Gone ? '_' : '^') : '.');
        return sb.ToString();
    }

    [Fact]
    public void WallUp_SeesCrystalWave_WallsUp_AndIsNotHit()
    {
        var p1Spawn = new Vector2(20f, BotArena.FloorBodyY);
        var p2Spawn = new Vector2(175f, BotArena.FloorBodyY);
        var wall = new WallUpManeuver();
        var r = BotArena.Fight(BotArena.Flat(), p1Spawn, p2Spawn, 240, BotArena.Only(wall),
                               setup: SpawnP1Wave);
        output.WriteLine(r.Summary());
        for (int y = 2; y <= 8; y++) output.WriteLine($"row {y}: {Row(r.Sim.Chunks, y, -3, 34)}");

        Assert.True(wall.LastWasCrystalThreat, "the wall should have been raised against the crystal wave");
        // A stone column between the bot and the launch point.
        var chunks = r.Sim.Chunks;
        bool column = false;
        for (int gx = CellX(p1Spawn.X) + 1; gx < CellX(r.P2.Body.Position.X) && !column; gx++)
        {
            bool col = true;
            for (int y = 4; y <= 7; y++) col &= chunks.GetCellState(gx, y) == TileState.Solid;
            column |= col;
        }
        Assert.True(column, "no wall column between the bot and the wave");
        Assert.Equal(0f, r.DamageToP2);
    }

    [Fact]
    public void Control_WithoutWallUp_TheWaveHits()
    {
        // Same wave, same spot, P2 simply standing there (no bot, no wall).
        var sim = new Simulation(BotArena.Flat(), new Vector2(20f, BotArena.FloorBodyY));
        var (p2, _) = sim.AddSecondaryPlayer(new Vector2(175f, BotArena.FloorBodyY));
        SpawnP1Wave(sim);
        for (int f = 0; f < 240; f++) sim.Step(default, default);
        float lost = p2.MaxHealth - p2.Health;
        output.WriteLine($"P2 lost {lost}");
        Assert.True(lost > 0f, "with no wall the crystal wave should land");
        Assert.True(lost <= CrystalWave.WaveDamageCap + 1e-3f);
    }

    [Fact]
    public void CrystalWave_BotLaunchesOne_AtMidRange()
    {
        var p1Spawn = new Vector2(80f, BotArena.FloorBodyY);
        var p2Spawn = new Vector2(80f + 12 * Ts, BotArena.FloorBodyY);
        // StaticUtility on: the default only fires on a target keeping its distance.
        var m = new CrystalWaveManeuver { StaticUtility = 1f };
        bool sawBall = false;
        var r = BotArena.Fight(BotArena.Flat(), p1Spawn, p2Spawn, 260, BotArena.Only(m),
                               p1: (f, sim) =>
                               {
                                   sawBall |= sim.Entities.Any(e => e is MassBall { Crystal: true });
                                   if (f % 10 == 0 && sim.Chunks.Crystals.Count > 0)
                                   {
                                       var xs = sim.Chunks.Crystals.Entries.Select(e => e.Key.gtx).ToList();
                                       int vis = sim.Chunks.Crystals.Entries.Count(e => !e.Value.Pending);
                                       output.WriteLine($"f{f}: crystals {xs.Count} (seeded {vis}) x [{xs.Min()},{xs.Max()}] balls {string.Join(",", sim.Entities.OfType<MassBall>().Select(b => b.Body.Position))}");
                                   }
                                   return default;
                               });
        output.WriteLine(r.Summary() + $"; fired {m.Fired} @ {m.FiredFrame}");
        Assert.True(m.Fired, "the bot should release a crystal wave");
        Assert.True(sawBall, "a crystal MassBall should have flown");
        Assert.True(r.P2.Abilities.Meters.CrystalWaveReady, "no cooldown since 2026-11-05: ready again at once");
        Assert.True(r.DamageToP1 > 0f, "the wave should reach an AFK target 12 tiles out");
        Assert.Equal(0f, r.DamageToP2);   // its own wave is harmless to it
    }

    [Fact]
    public void CrystalWave_DoesNotScore_OnCooldown_OrUpClose()
    {
        var sim = new Simulation(BotArena.Flat(), new Vector2(80f, BotArena.FloorBodyY));
        var (p2, _) = sim.AddSecondaryPlayer(new Vector2(80f + 12 * Ts, BotArena.FloorBodyY));
        for (int f = 0; f < 30; f++) sim.Step(default, default);
        var v = new BotView { Sim = sim, Self = p2, Target = sim.Player, Me = p2.Body.Position, TargetPos = sim.Player.Body.Position };
        var m = new CrystalWaveManeuver();
        Assert.Equal(0f, m.Score(v));                  // AFK target: walking in is faster
        v.TargetVel = new Vector2(-40f, 0f);           // P1 (left of us) backing off left
        Assert.True(m.Score(v) > 0f);
        p2.Abilities.Meters.CrystalCooldown = 3f;
        Assert.Equal(0f, m.Score(v));
        p2.Abilities.Meters.CrystalCooldown = 0f;
        v.TargetPos = p2.Body.Position - new Vector2(3 * Ts, 0f);
        Assert.Equal(0f, m.Score(v));
    }
}
