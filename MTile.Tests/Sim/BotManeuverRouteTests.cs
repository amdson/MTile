using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// RouteManeuver (Net/Bot/Routing): the bot plans a NavGrid path to an AFK P1 and walks /
// jumps / drops along it. Static terrain, no building or digging (M2 of
// Plans/BOT_ROUTING_PLAN.md). Maps are ascii, 'X' solid; 'A' marks where P2 starts and
// 'B' where P1 stands (both are the leg cell of a standing body, i.e. a stand node).
public class BotManeuverRouteTests(ITestOutputHelper output)
{
    private static float T => Chunk.TileSize;
    private const int OriginX = -4;

    private static (ChunkMap Map, Vector2 A, Vector2 B) Parse(string[] rows)
    {
        Vector2 a = default, b = default;
        var clean = rows.Select((r, y) =>
        {
            int ia = r.IndexOf('A'), ib = r.IndexOf('B');
            if (ia >= 0) a = Stand(ia, y);
            if (ib >= 0) b = Stand(ib, y);
            return r.Replace('A', '.').Replace('B', '.').Replace('.', 'O');
        });
        return (BotArena.FromAscii(string.Join("\n", clean), OriginX, 0), a, b);
    }

    // Body centre of a player standing with its legs in cell (col, row): floor top is the
    // next row down; a standing body's centre sits 21 px above it.
    private static Vector2 Stand(int col, int row) => new((col + OriginX + 0.5f) * T, (row + 1) * T - 21f);

    private int TraceFrom = -1, TraceTo = -1;
    private (int Frame, BotArena.Run Run, RouteManeuver Route) Go(string[] rows, int frames = 900)
    {
        var (map, a, b) = Parse(rows);
        var route = new RouteManeuver { AlwaysOn = true, Log = output.WriteLine };
        int reached = -1;
        var bot = BotArena.Only(route);
        var r = BotArena.Fight(map, b, a, frames, bot: bot, p1: (f, sim) =>
        {
            var p2 = sim.SecondaryPlayers[0].Player;
            var n2 = NavGrid.NodeUnder((x, y) => sim.Chunks.GetCellState(x, y) == TileState.Solid, p2.Body.Position);
            var n1 = NavGrid.NodeUnder((x, y) => sim.Chunks.GetCellState(x, y) == TileState.Solid, sim.Player.Body.Position);
            if (f >= TraceFrom && f < TraceTo)
                output.WriteLine($"f{f} active {bot.Active?.Name} {route.Follower.PhaseName} edge {route.Follower.Current} x {p2.Body.Position.X:F1} y {p2.Body.Position.Y:F1} vx {p2.Body.Velocity.X:F0} {p2.CurrentStateName} in {(bot.Previous.Left ? "L" : "")}{(bot.Previous.Right ? "R" : "")}{(bot.Previous.Space ? "S" : "")}");
            if (reached < 0 && p2.IsGrounded && n1 is NavGrid.Node t && n2 is NavGrid.Node m
                && Math.Abs(m.X - t.X) <= 2 && Math.Abs(m.Y - t.Y) <= 1) reached = f;
            return default;
        });
        output.WriteLine($"reached at {reached}; plans {route.Plans}, failures {route.Failures}, reached-goal {route.Reached}");
        if (route.LastPlan != null) output.WriteLine("last plan: " + string.Join(" ", route.LastPlan));
        output.WriteLine(r.Summary());
        return (reached, r, route);
    }

    [Fact]
    public void Stairs_ThreeFlightsUp()
    {
        var (f, _, _) = Go(new[]
        {
            "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX",
            "X......................................X",
            "X......................................X",
            "X......................................X",
            "X......................................X",
            "X......................................X",
            "X......................................X",
            "X.............................B........X",
            "X..........................XXXXXXXXXXXXX",
            "X..........................XXXXXXXXXXXXX",
            "X..........................XXXXXXXXXXXXX",
            "X.....................XXXXXXXXXXXXXXXXXX",
            "X.....................XXXXXXXXXXXXXXXXXX",
            "X.....................XXXXXXXXXXXXXXXXXX",
            "X.................XXXXXXXXXXXXXXXXXXXXXX",
            "X.................XXXXXXXXXXXXXXXXXXXXXX",
            "X.................XXXXXXXXXXXXXXXXXXXXXX",
            "X....A..........XXXXXXXXXXXXXXXXXXXXXXXX",
            "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX",
            "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX",
        });
        Assert.True(f >= 0 && f < 600, $"never got up the stairs (frame {f})");
    }

    [Fact]
    public void Pit_GapJump()
    {
        var (f, _, _) = Go(new[]
        {
            "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX",
            "X......................................X",
            "X......................................X",
            "X......................................X",
            "X......................................X",
            "X......................................X",
            "X......................................X",
            "X......................................X",
            "X...A.............................B....X",
            "XXXXXXXXXXXXXXXXXX....XXXXXXXXXXXXXXXXXX",
            "XXXXXXXXXXXXXXXXXX....XXXXXXXXXXXXXXXXXX",
            "X......................................X",
            "X......................................X",
            "X......................................X",
            "X......................................X",
            "X......................................X",
            "X......................................X",
            "X......................................X",
            "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX",
        });
        Assert.True(f >= 0 && f < 500, $"never crossed the pit (frame {f})");
    }

    [Fact]
    public void TallStep_FourUp()
    {
        var (f, _, _) = Go(new[]
        {
            "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX",
            "X......................................X",
            "X......................................X",
            "X......................................X",
            "X......................................X",
            "X......................................X",
            "X......................................X",
            "X......................................X",
            "X..........................B...........X",
            "X.....................XXXXXXXXXXXXXXXXXX",
            "X.....................XXXXXXXXXXXXXXXXXX",
            "X.....................XXXXXXXXXXXXXXXXXX",
            "X....A................XXXXXXXXXXXXXXXXXX",
            "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX",
            "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX",
        });
        Assert.True(f >= 0 && f < 500, $"never got up the 4-cell step (frame {f})");
    }

    // The target is below and to the LEFT, but the shelf's left end is walled to the
    // ceiling: the only way down is off the RIGHT end, then back under the shelf.
    [Fact]
    public void DownAndAround()
    {
        var (f, _, route) = Go(new[]
        {
            "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX",
            "X......................................X",
            "XXXXXXXX...............................X",
            "XXXXXXXX...............................X",
            "XXXXXXXX...............................X",
            "XXXXXXXX..........A....................X",
            "XXXXXXXXXXXXXXXXXXXXXXXXXXXXX..........X",
            "X......................................X",
            "X......................................X",
            "X......................................X",
            "X......................................X",
            "X....B.................................X",
            "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX",
            "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX",
        });
        Assert.True(f >= 0 && f < 700, $"never found the way down and around (frame {f})");
    }
}
