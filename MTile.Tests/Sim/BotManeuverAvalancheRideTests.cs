using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// AvalancheRideManeuver end to end: P1 (AFK) stands on a high ledge across a pit; the
// bot erupts a diagonal wave from under its own feet, rides it over the pit and up, and
// hops off onto the ledge (or, pinned against the cliff face, finishes with the
// WallClimb controller).
//
// Tuning record (a sweep over 11 pit/ledge layouts, pit 7–13 wide, ledge 4–10 up):
// LaunchReach 9–14 × angle 45/55 all landed 8–11 of them; 12 × 45° and 12 × 50° landed
// 11/11, 12 × 55° 10/11.
public class BotManeuverAvalancheRideTests(ITestOutputHelper output)
{
    // Left plateau (top row `leftTop`, cols < pitFrom), a pit with a deep catch floor,
    // and a right ledge (top row `ledgeTop`, cols ≥ pitTo). Thick side walls.
    internal static ChunkMap PitArena(int leftTop = 22, int ledgeTop = 14, int pitFrom = 14, int pitTo = 24,
                                      int rows = 44, int cols = 40, bool mirror = false)
    {
        var sb = new StringBuilder();
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < cols; x++)
            {
                int xx = mirror ? cols - 1 - x : x;
                bool wall = x < 3 || x >= cols - 3;
                bool solid = wall || y >= rows - 4
                          || (xx < pitFrom && y >= leftTop)
                          || (xx >= pitTo && y >= ledgeTop);
                sb.Append(solid ? 'X' : 'O');
            }
            sb.Append('\n');
        }
        return BotArena.FromAscii(sb.ToString());
    }

    // World position of a standing body at ascii column `col` on a floor whose top row is `top`.
    internal static Vector2 StandAt(int col, int top, bool mirror = false, int cols = 40)
        => new(((mirror ? cols - 1 - col : col) - 4) * Chunk.TileSize + Chunk.TileSize * 0.5f, top * Chunk.TileSize - 21f);

    // Deepest (largest) y P2 reached in the last Trace — "did it go down into the pit?"
    private float _maxY;

    private BotArena.Run Trace(ChunkMap terrain, Vector2 p1, Vector2 p2, int frames, BrawlerBotInputSource bot,
                           bool verbose = true)
    {
        var lines = new List<string>();
        _maxY = float.MinValue;
        var run = BotArena.Fight(terrain, p1, p2, frames, bot, p1: (f, sim) =>
        {
            var b = sim.SecondaryPlayers[0].Player;
            _maxY = MathF.Max(_maxY, b.Body.Position.Y);
            if (verbose && f % 8 == 0 || !verbose && f % 20 == 0)
                lines.Add($"f{f,3} {b.Body.Position.X,7:F1},{b.Body.Position.Y,6:F1} v {b.Body.Velocity.X,6:F0},{b.Body.Velocity.Y,6:F0} " +
                          $"{b.CurrentStateName,-20} {b.CurrentActionName,-18} {bot.Active?.Name}");
            return default;
        });
        foreach (var l in lines) output.WriteLine(l);
        output.WriteLine(run.Summary());
        return run;
    }

    // Control: the slash bot alone walks at the ledge and drops into the pit — the
    // geometry really is out of reach for the default chase.
    [Fact]
    public void Control_SlashOnly_CannotReachLedge()
    {
        var r = Trace(PitArena(), StandAt(31, 14), StandAt(8, 22), 400, BotArena.Only(new SlashManeuver()), verbose: false);
        Assert.Equal(0f, r.DamageToP1);
        Assert.True(r.P2.Body.Position.Y > 14 * Chunk.TileSize, "the slash-only bot should not be up on the ledge");
    }

    // Comparison: BuildClimb (+ slash) DOES get there in this arena — but only by
    // falling into the pit and building a column off its floor (a pit with no floor, the
    // usual kind, is a death for it), and later. The ride never drops below the plateau.
    [Fact]
    public void Comparison_BuildClimbGoesThroughThePit()
    {
        var bc = Trace(PitArena(), StandAt(31, 14), StandAt(8, 22), 420,
                       BotArena.Only(new BuildClimbManeuver(), new SlashManeuver()), verbose: false);
        float bcDepth = _maxY;
        var ride = Trace(PitArena(), StandAt(31, 14), StandAt(8, 22), 420,
                         BotArena.Only(new AvalancheRideManeuver(), new SlashManeuver()), verbose: false);
        float rideDepth = _maxY;
        output.WriteLine($"first hit: ride {ride.FirstHitOnP1} (deepest y {rideDepth:F0}), " +
                         $"build-climb {bc.FirstHitOnP1} (deepest y {bcDepth:F0}); plateau body y {StandAt(8, 22).Y}");
        float plateauY = StandAt(8, 22).Y;
        Assert.True(bcDepth > plateauY + 5f * Chunk.TileSize, "build-climb was expected to drop into the pit");
        Assert.True(rideDepth < plateauY + Chunk.TileSize, "the ride should never go down into the pit");
        Assert.True(ride.FirstHitOnP1 >= 0 && (bc.FirstHitOnP1 < 0 || ride.FirstHitOnP1 < bc.FirstHitOnP1));
    }

    // The ride: the bot launches, gets carried across the pit, hops onto the ledge and
    // lands a hit. Geometry varied: bot start column, ledge height, pit width.
    [Theory]
    [InlineData(8, 14, 14, 24, false)]
    [InlineData(6, 14, 14, 24, false)]
    [InlineData(10, 16, 14, 24, false)]
    [InlineData(8, 13, 14, 22, false)]
    [InlineData(8, 15, 12, 25, false)]
    [InlineData(5, 18, 14, 22, false)]   // ledge only 4 tiles up, narrow pit
    [InlineData(8, 14, 14, 24, true)]    // mirrored: ride leftward
    [InlineData(9, 17, 14, 26, true)]
    public void PitLedge_RidesAcrossAndHits(int botCol, int ledgeTop, int pitFrom, int pitTo, bool mirror)
    {
        var m = new AvalancheRideManeuver();
        var r = Trace(PitArena(ledgeTop: ledgeTop, pitFrom: pitFrom, pitTo: pitTo, mirror: mirror),
                      StandAt(31, ledgeTop, mirror), StandAt(botCol, 22, mirror), 420,
                      BotArena.Only(m, new SlashManeuver()), verbose: false);
        output.WriteLine($"angle {m.LastAngle} fired {m.Fired} carried {m.Carried}; starts " +
                         string.Join(",", r.Bot.Starts.Select(kv => $"{kv.Key}={kv.Value}")));
        Assert.True(m.Fired, "the bot never erupted");
        Assert.Contains(nameof(TerrainCarriedState), r.P2States);
        Assert.True(r.DamageToP1 > 0f, "the bot never landed a hit on the ledge");
        Assert.Equal(0, r.P2Deaths);
    }

    // Not applicable: target on our own level a few tiles away — no pit, no ledge.
    [Fact]
    public void OpenFloor_SameLevel_DoesNotRide()
    {
        var m = new AvalancheRideManeuver();
        var r = Trace(BotArena.Flat(floorRows: 12, wallCols: 3), new Vector2(250f, BotArena.FloorBodyY),
                      new Vector2(100f, BotArena.FloorBodyY), 200, BotArena.Only(m, new SlashManeuver()), verbose: false);
        Assert.False(m.Fired);
        Assert.False(r.Bot.Starts.ContainsKey(m.Name));
    }

    // The meter is real: an empty reservoir can't fund the charge.
    [Fact]
    public void EmptyReservoir_DoesNotRide()
    {
        var m = new AvalancheRideManeuver();
        var r = BotArena.Fight(PitArena(), StandAt(31, 14), StandAt(8, 22), 120, BotArena.Only(m),
            setup: sim => sim.SecondaryPlayers[0].Player.Abilities.Meters.Build = 0f);
        output.WriteLine(r.Summary());
        Assert.False(m.Fired);
        Assert.False(r.Saw(nameof(BlockPaintAction)));
    }
}
