using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using MTile.Net;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Movement-tech / positional bot maneuvers (Dash, WallClimb, CrouchSlash, DownAir,
// Dropdown), each run in isolation against an AFK P1 placed where the default chase
// can't (or shouldn't) deal with it.
public class BotManeuverMovementTests(ITestOutputHelper output)
{
    // 40x12 arena like BotArena.Flat (origin tile -4, floor from row 8) with extra solid
    // cells given as (col, row) rectangles [c0..c1] x [r0..r1].
    private static ChunkMap Arena(params (int c0, int c1, int r0, int r1)[] blocks)
    {
        var rows = new List<string>();
        for (int y = 0; y < 12; y++)
        {
            var sb = new StringBuilder();
            for (int x = 0; x < 40; x++)
            {
                bool solid = y >= 8 || x == 0 || x == 39;
                foreach (var b in blocks) if (x >= b.c0 && x <= b.c1 && y >= b.r0 && y <= b.r1) solid = true;
                sb.Append(solid ? 'X' : 'O');
            }
            rows.Add(sb.ToString());
        }
        return BotArena.FromAscii(string.Join("\n", rows));
    }

    // Frame-by-frame P2 trace (every `every` frames) for debugging a maneuver.
    private void Trace(ChunkMap terrain, Vector2 p1, Vector2 p2, int frames, BrawlerBotInputSource bot, int every = 3)
    {
        var sim = new Simulation(terrain, p1);
        var (s, _) = sim.AddSecondaryPlayer(p2);
        for (int f = 0; f < frames; f++)
        {
            var i = bot.Poll(sim, f);
            sim.Step(default, i);
            if (f % every == 0)
                output.WriteLine($"{f,4} {s.CurrentStateName,-20} {s.CurrentActionName,-14} pos {s.Body.Position.X:0},{s.Body.Position.Y:0} v {s.Body.Velocity.X:0},{s.Body.Velocity.Y:0} in {(i.Left?"L":"")}{(i.Right?"R":"")}{(i.Up?"U":"")}{(i.Down?"D":"")}{(i.Space?"S":"")}{(i.Shift?"H":"")}{(i.LeftClick?"C":"")}");
        }
    }

    [Fact]
    public void Dash_ClosesGap_AndDashSlashes()
    {
        var r = BotArena.Fight(BotArena.Flat(), new Vector2(260f, 70f), new Vector2(120f, 70f), 120,
                               BotArena.Only(new DashManeuver()));
        output.WriteLine(r.Summary());
        Assert.Contains("DashState", r.P2States);
        Assert.True(r.Saw("DashSlash"), "The dash should throw a DashSlash on arrival.");
        Assert.True(r.DamageToP1 > 0f, "The dash-slash should connect with an AFK target.");
    }

    [Fact]
    public void WallClimb_GetsUpA5TilePillar()
    {
        // Pillar cols 20-22, rows 3-7 (5 tiles above the floor). P1 on top, P2 at its foot.
        // Without the maneuver the default chase can't do this (it only jumps ~2.5 tiles).
        var terrain = Arena((20, 22, 3, 7));
        var r = BotArena.Fight(terrain, new Vector2(192f, 0f), new Vector2(125f, 70f), 300,
                               BotArena.Only(new WallClimbManeuver()));
        output.WriteLine(r.Summary());
        Assert.Contains("WallSlidingState", r.P2States);
        Assert.Contains("WallJumpingState", r.P2States);
        Assert.True(r.P2.Body.Position.Y < 20f && r.P2.Body.Position.X > 170f && r.P2.Body.Position.X < 215f,
                    $"P2 should end up standing on top of the pillar ({r.P2.Body.Position}).");

        var control = BotArena.Fight(terrain, new Vector2(192f, 0f), new Vector2(125f, 70f), 300,
                                     BotArena.Only(new SlashManeuver()));
        output.WriteLine("control: " + control.Summary());
        Assert.True(control.P2.Body.Position.Y > 50f, "Default chase alone should NOT get up the pillar.");
    }

    [Fact]
    public void CrouchSlash_PokesFromCrouch()
    {
        // P2 stands on a one-tile ledge (cols 22-25), P1 on the floor beside it: a low target.
        var r = BotArena.Fight(Arena((22, 25, 7, 7)), new Vector2(184f, 70f), new Vector2(210f, 50f), 200,
                               BotArena.Only(new CrouchSlashManeuver()));
        output.WriteLine(r.Summary());
        Assert.Contains("CrouchedState", r.P2States);
        Assert.True(r.Saw("CrouchSlash"));
        Assert.True(r.DamageToP1 > 0f, "The low poke should reach a target just outside stand-slash range.");
    }

    [Fact]
    public void DownAir_ChopsFromAbove()
    {
        var r = BotArena.Fight(BotArena.Flat(), new Vector2(160f, 70f), new Vector2(220f, 70f), 200,
                               BotArena.Only(new DownAirManeuver()));
        output.WriteLine(r.Summary());
        Assert.True(r.Saw("DownAirSlash"));
    }

    [Fact]
    public void Dropdown_SlidesOffLedge()
    {
        // Raised shelf cols 5-14 (rows 5-7); P2 on it, P1 on the floor to the right.
        var terrain = Arena((5, 14, 4, 7));
        var r = BotArena.Fight(terrain, new Vector2(150f, 70f), new Vector2(60f, 20f), 300,
                               BotArena.Only(new DropdownManeuver(), new SlashManeuver()));
        output.WriteLine(r.Summary());
        Assert.Contains("DropdownState", r.P2States);
        Assert.True(r.P2.Body.Position.Y > 55f, "P2 should have dropped to the floor.");
    }
}
