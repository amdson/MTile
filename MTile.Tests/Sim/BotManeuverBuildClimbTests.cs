using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// BuildClimbManeuver: the bot reaches a target standing on a sheer pillar too tall to
// jump by raising a block column under its own feet (Shift+RMB on the foot cell).
public class BotManeuverBuildClimbTests(ITestOutputHelper output)
{
    // 40×24 arena, floor top at row 16 (world y 176), and a sheer pillar at ascii
    // columns 26..31 (world x 242..308) rising to row `pillarTop` (8 ⇒ world y 88, eight
    // tiles up). Walls and floor are thick because the slash exchange on top carves
    // terrain and knocks bodies around; a 1-tile wall gets cut through.
    private static ChunkMap PillarArena(int pillarTop = 8)
    {
        var rows = new List<string>();
        for (int y = 0; y < 24; y++)
        {
            var sb = new StringBuilder();
            for (int x = 0; x < 40; x++)
                sb.Append(y >= 16 || x <= 2 || x >= 37 || (x >= 26 && x <= 31 && y >= pillarTop) ? 'X' : 'O');
            rows.Add(sb.ToString());
        }
        return BotArena.FromAscii(string.Join("\n", rows));
    }

    private static float StandY(int floorRow) => floorRow * Chunk.TileSize - 21f;

    private static readonly Vector2 P1OnPillar = new(275f, StandY(8));
    private static readonly Vector2 P2OnFloor  = new(72f,  StandY(16));

    [Fact]
    public void Control_SlashOnly_CannotReachPillarTop()
    {
        var r = BotArena.Fight(PillarArena(), P1OnPillar, P2OnFloor, 900,
                               bot: BotArena.Only(new SlashManeuver()));
        output.WriteLine(r.Summary());
        Assert.Equal(0f, r.DamageToP1);
        Assert.True(r.P2.Body.Position.Y > StandY(8) + 3f * Chunk.TileSize,
            $"control bot got up the pillar without building (y {r.P2.Body.Position.Y})");
    }

    [Fact]
    public void BuildClimb_GetsUpPillar_AndLandsAHit()
    {
        var r = BotArena.Fight(PillarArena(), P1OnPillar, P2OnFloor, 900,
                               bot: BotArena.Only(new BuildClimbManeuver(), new SlashManeuver()));
        output.WriteLine(r.Summary());
        int top = FirstFrameAtOrAbove(r, P1OnPillar.Y + 3f);
        output.WriteLine($"P2 reached the pillar top at frame {top}");
        Assert.True(r.Saw(nameof(BlockPlaceAction)), "never placed a block");
        Assert.True(top >= 0 && top < 250, $"took {top} frames to get up");
        Assert.True(r.FirstHitOnP1 >= 0, "never hit P1 on the pillar");
        Assert.True(r.FirstHitOnP1 < 300, $"took {r.FirstHitOnP1} frames to land a hit");
    }

    // Ten tiles up, approached from the right: the column-picking and the step-off
    // direction must mirror.
    [Fact]
    public void BuildClimb_TallerPillar_FromTheRight()
    {
        var p1 = new Vector2(275f, StandY(6));
        var p2 = new Vector2(345f, StandY(16));
        var r = BotArena.Fight(PillarArena(pillarTop: 6), p1, p2, 900,
                               bot: BotArena.Only(new BuildClimbManeuver(), new SlashManeuver()));
        output.WriteLine(r.Summary());
        Assert.True(r.Saw(nameof(BlockPlaceAction)), "never placed a block");
        Assert.True(r.FirstHitOnP1 >= 0 && r.FirstHitOnP1 < 300, $"first hit at {r.FirstHitOnP1}");
    }

    [Fact]
    public void BuildClimb_FoamSelected_SwitchesToDirt()
    {
        var r = BotArena.Fight(PillarArena(), P1OnPillar, P2OnFloor, 900,
                               bot: BotArena.Only(new BuildClimbManeuver(), new SlashManeuver()),
                               setup: s => s.SecondaryPlayers[0].Player.ActiveBlockType = TileType.Foam);
        output.WriteLine(r.Summary());
        Assert.Equal(TileType.Dirt, r.P2.ActiveBlockType);
        Assert.True(r.FirstHitOnP1 >= 0, "never hit P1 on the pillar");
    }

    [Fact]
    public void BuildClimb_ScoresZero_OnFlatGround()
    {
        var climb = new BuildClimbManeuver();
        var r = BotArena.Fight(BotArena.Flat(), new Vector2(250f, BotArena.FloorBodyY),
                               new Vector2(40f, BotArena.FloorBodyY), 400,
                               bot: BotArena.Only(climb, new SlashManeuver()));
        output.WriteLine(r.Summary());
        Assert.False(r.Saw(nameof(BlockPlaceAction)), "built on flat ground");
        Assert.True(r.FirstHitOnP1 >= 0, "the slash control path should still connect");

        // And directly: a fresh view of the flat arena scores ≤ 0 however long it waits.
        float max = float.MinValue;
        for (int i = 0; i < 100; i++) max = MathF.Max(max, climb.Score(Views.Of(r)));
        Assert.True(max <= 0f, $"scored {max} on flat ground");
    }

    // BotArena.Run doesn't keep a position trace, so re-run the same deterministic
    // fight (same seed, same terrain, AFK P1) and report the first frame P2's body is at
    // or above `y`. Only valid for the default PillarArena / spawns.
    private static int FirstFrameAtOrAbove(BotArena.Run r, float y)
    {
        var bot = BotArena.Only(new BuildClimbManeuver(), new SlashManeuver());
        var sim = new Simulation(PillarArena(), P1OnPillar);
        var (p2, _) = sim.AddSecondaryPlayer(P2OnFloor);
        for (int f = 0; f < r.Frames; f++)
        {
            sim.Step(default, bot.Poll(sim, f));
            if (p2.Body.Position.Y <= y) return f;
        }
        return -1;
    }

    // Builds a BotView from a finished run (true positions; the reaction lag doesn't
    // matter for a parked target).
    private static class Views
    {
        public static BotView Of(BotArena.Run r) => new()
        {
            Sim = r.Sim, Self = r.P2, Target = r.P1, Rng = new Random(1),
            Me = r.P2.Body.Position, MyVel = r.P2.Body.Velocity,
            TargetPos = r.P1.Body.Position, TargetVel = r.P1.Body.Velocity,
        };
    }
}
