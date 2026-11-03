using System;
using Microsoft.Xna.Framework;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Barrier maneuvers (EncloseManeuver, WallUpManeuver) in a real two-player sim: the bot
// builds through Shift+RMB / spike-picker input only, so these also pin that the
// placement rules (reach, support, meter) leave the plans buildable.
public class BotManeuverBarrierTests
{
    private readonly ITestOutputHelper _out;
    public BotManeuverBarrierTests(ITestOutputHelper o) => _out = o;

    private static float Ts => Chunk.TileSize;
    private static int CellX(float x) => (int)MathF.Floor(x / Ts);

    // P1 AFK on flat floor, bot 80 px away: it walks into its standoff, walls P1 in on
    // both sides, lids it, then spikes the floor under it — and the spikes bite.
    [Fact]
    public void Enclose_AfkTarget_IsWalledInAndSpiked()
    {
        var p1Spawn = new Vector2(200f, BotArena.FloorBodyY);
        var p2Spawn = new Vector2(280f, BotArena.FloorBodyY);
        var bot = BotArena.Only(new EncloseManeuver());
        var r = BotArena.Fight(BotArena.Flat(), p1Spawn, p2Spawn, 360, bot);
        _out.WriteLine(r.Summary());

        var chunks = r.Sim.Chunks;
        var p = r.P1.Body.Position;
        int left  = CellX(p.X - BarrierPlanner.HalfWidth - 1f) - 1;
        int right = CellX(p.X + BarrierPlanner.HalfWidth + 1f) + 1;
        int head  = (int)MathF.Floor((p.Y + BarrierPlanner.Top) / Ts);
        int feet  = (int)MathF.Floor((p.Y + BarrierPlanner.Bottom) / Ts);
        for (int y = head - 3; y <= feet + 3; y++)
        {
            var sb = new System.Text.StringBuilder();
            for (int x = left - 1; x <= right + 1; x++)
                sb.Append(chunks.Spikes.IsSpiked(x, y) ? '^' : chunks.GetCellState(x, y) == TileState.Solid ? 'X' : '.');
            _out.WriteLine($"row {y}: {sb}");
        }

        // Both flanks solid over the whole body height.
        for (int y = head; y <= feet; y++)
        {
            Assert.Equal(TileState.Solid, chunks.GetCellState(left, y));
            Assert.Equal(TileState.Solid, chunks.GetCellState(right, y));
        }
        // Lid over the head.
        for (int x = left; x <= right; x++)
            Assert.Equal(TileState.Solid, chunks.GetCellState(x, head - 1));
        // The trap stayed a trap: P1 wasn't shoved out of its box by a sprout.
        Assert.InRange(p.X, p1Spawn.X - 3f, p1Spawn.X + 3f);
        // Spiked floor under a standing target ticks ContactDamage.
        Assert.True(r.DamageToP1 > 0f, $"expected spike damage, P1 lost {r.DamageToP1}");
        // Picker restored to the starting material (Dirt), spikes off.
        Assert.False(r.P2.SpikesSelected);
        Assert.Equal(TileType.Dirt, r.P2.ActiveBlockType);
    }

    // A walking target is not enclosable: the maneuver must not even start.
    [Fact]
    public void Enclose_MovingTarget_DoesNotBuild()
    {
        var bot = BotArena.Only(new EncloseManeuver());
        var r = BotArena.Fight(BotArena.Flat(), new Vector2(150f, BotArena.FloorBodyY),
                               new Vector2(300f, BotArena.FloorBodyY), 120, bot,
                               p1: (f, _) => new PlayerInput { Left = (f / 40) % 2 == 0, Right = (f / 40) % 2 == 1 });
        _out.WriteLine(r.Summary());
        Assert.DoesNotContain(r.P2Inputs, i => i.RightClick);
    }

    // P1 walks at the bot; the bot throws up a stone column in front of itself and P1
    // stops short of it for a sustained stretch.
    [Fact]
    public void WallUp_WalkingTarget_IsBlocked()
    {
        var p1Spawn = new Vector2(60f, BotArena.FloorBodyY);
        var p2Spawn = new Vector2(300f, BotArena.FloorBodyY);
        var bot = BotArena.Only(new WallUpManeuver());
        // P1's (x, |vx|) per frame, sampled from the script callback (sees the pre-step state).
        var xs = new System.Collections.Generic.List<(float x, float vx)>();
        var r = BotArena.Fight(BotArena.Flat(), p1Spawn, p2Spawn, 300, bot,
                               p1: (f, sim) =>
                               {
                                   xs.Add((sim.Player.Body.Position.X, MathF.Abs(sim.Player.Body.Velocity.X)));
                                   return new PlayerInput { Right = true };
                               });
        _out.WriteLine(r.Summary());

        // Find the wall: the solid column, between the two spawns, that rises >= 4 tiles
        // off the floor (floor top row = 8).
        var chunks = r.Sim.Chunks;
        float wallX = float.NaN;
        for (int gx = CellX(p1Spawn.X); gx <= CellX(p2Spawn.X); gx++)
        {
            bool col = true;
            for (int y = 4; y <= 7; y++) col &= chunks.GetCellState(gx, y) == TileState.Solid;
            if (col) { wallX = gx * Ts; break; }
        }
        Assert.False(float.IsNaN(wallX), "no wall column between the players");

        float maxP1X = float.MinValue;
        int blockedFrames = 0;
        foreach (var (x, vx) in xs)
        {
            maxP1X = MathF.Max(maxP1X, x);
            if (x < wallX && x > wallX - 2f * Ts && vx < 5f) blockedFrames++;
        }
        _out.WriteLine($"wall left edge x={wallX}; P1 max x {maxP1X}, blocked frames {blockedFrames}");
        Assert.True(maxP1X < wallX, $"P1 got past the wall (max x {maxP1X} ≥ {wallX})");
        Assert.True(blockedFrames >= 60, $"P1 pinned against the wall only {blockedFrames} frames");
    }

    // A target standing still (not approaching, not charging) gets no wall.
    [Fact]
    public void WallUp_IdleTarget_DoesNotBuild()
    {
        var bot = BotArena.Only(new WallUpManeuver());
        var r = BotArena.Fight(BotArena.Flat(), new Vector2(150f, BotArena.FloorBodyY),
                               new Vector2(250f, BotArena.FloorBodyY), 90, bot);
        Assert.DoesNotContain(r.P2Inputs, i => i.RightClick);
    }
}
