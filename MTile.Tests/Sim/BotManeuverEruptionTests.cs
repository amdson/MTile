using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// EruptionManeuver end to end: the bot charges RMB in the floor, whips the cursor out
// along the surface and releases, and the MassBall it detaches raises a ridge that runs
// out to P1 (AFK). In the open the ridge lifts/carries P1; under a low ceiling the
// sprouts wedge P1 and crush.
public class BotManeuverEruptionTests(ITestOutputHelper output)
{
    // BotArena.Flat with an optional ceiling row from column `ceilingFrom` rightward.
    private static ChunkMap Arena(int ceilingFrom = -1, int ceilingRow = 0)
    {
        var rows = new List<string>();
        for (int y = 0; y < 12; y++)
        {
            var sb = new StringBuilder();
            for (int x = 0; x < 40; x++)
                sb.Append(y >= 8 || x == 0 || x == 39 ||
                          (ceilingFrom >= 0 && x >= ceilingFrom && y == ceilingRow) ? 'X' : 'O');
            rows.Add(sb.ToString());
        }
        return BotArena.FromAscii(string.Join("\n", rows));
    }

    private sealed class Obs
    {
        public MassBall Ball;
        public int      BallFrame = -1;
        public float    BallMass;   // at birth, before it leaks
        public bool     P1Carried;
        public float    P1MaxDisp;
    }

    private (BotArena.Run run, Obs obs) Fight(ChunkMap terrain, int tiles, int frames = 200,
                                              Action<Simulation> setup = null)
    {
        var p2 = new Vector2(100f, BotArena.FloorBodyY);
        var p1 = p2 + new Vector2(tiles * Chunk.TileSize, 0f);
        var o = new Obs();
        Vector2 settled = p1;
        var run = BotArena.Fight(terrain, p1, p2, frames, BotArena.Only(new EruptionManeuver()),
            p1: (f, sim) =>
            {
                if (o.Ball == null && sim.Entities.FirstOrDefault(e => e is MassBall) is MassBall b)
                { o.Ball = b; o.BallFrame = f; o.BallMass = b.BuildMass; }
                if (f == 10) settled = sim.Player.Body.Position;
                if (f > 10) o.P1MaxDisp = MathF.Max(o.P1MaxDisp, Vector2.Distance(sim.Player.Body.Position, settled));
                o.P1Carried |= sim.Player.CurrentStateName == "TerrainCarriedState";
                return default;
            }, setup: setup);
        output.WriteLine($"{tiles} tiles: ball@{o.BallFrame} mass {o.BallMass:F1}; P1 carried {o.P1Carried}, " +
                         $"maxDisp {o.P1MaxDisp:F1}; {run.Summary()}");
        return (run, o);
    }

    // Mid range on open floor: the gesture erupts (a MassBall owned by P2's faction) and
    // the ridge reaches P1 and moves them (lifted ~30 px onto it / carried).
    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    public void OpenFloor_MidRange_EruptsAndDisplacesP1(int tiles)
    {
        var (run, o) = Fight(Arena(), tiles);
        Assert.True(run.Saw(nameof(BlockPaintAction)), "the bot never held a paint/charge stroke");
        Assert.NotNull(o.Ball);
        Assert.Equal(run.P2.Faction, o.Ball.Faction);
        Assert.True(o.BallMass > 0f);
        Assert.True(o.P1Carried || o.P1MaxDisp > 20f,
            $"the eruption never reached P1 (maxDisp {o.P1MaxDisp:F1})");
        Assert.Equal(0f, run.DamageToP2);
    }

    // Ceiling three open rows above P1's floor: the ridge has nowhere to push P1, so the
    // sprouts are crushed out against them and bill SproutCrushDamage.
    [Fact]
    public void LowCeiling_RidgeCrushesP1()
    {
        var (run, o) = Fight(Arena(ceilingFrom: 18, ceilingRow: 4), tiles: 6);
        Assert.NotNull(o.Ball);
        Assert.True(run.DamageToP1 >= 0.5f, $"expected a sprout crush on P1; lost {run.DamageToP1}");
    }

    // Too close: inside MinRange the slash is the tool and the ridge would sprout under us.
    [Fact]
    public void TooClose_DoesNotErupt()
    {
        var (run, o) = Fight(Arena(), tiles: 2, frames: 120);
        Assert.Null(o.Ball);
        Assert.False(run.Saw(nameof(BlockPaintAction)));
    }

    // The meter is real: with the reservoir drained there's nothing to charge with, so
    // Score refuses instead of holding RMB in the floor for nothing.
    [Fact]
    public void EmptyReservoir_DoesNotErupt()
    {
        var (run, o) = Fight(Arena(), tiles: 6, frames: 30, setup: sim =>
        {
            var m = sim.SecondaryPlayers[0].Player.Abilities.Meters;
            m.Build = 0f;
        });
        Assert.Null(o.Ball);
        Assert.False(run.Saw(nameof(BlockPaintAction)));
    }
}
