using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// BurstManeuver (LMB-hold → RMB edge: BurstAction's close shove) and BlockBurstManeuver
// (RMB-hold → LMB edge: BlockBurstAction's foam plus on the target), each run alone
// against an AFK P1.
public class BotManeuverBurstTests(ITestOutputHelper output)
{
    // Flat arena; optional ceiling row from column 15 rightward, and/or a raised block
    // (columns ≥ stepFrom, `stepRows` tall) for an above-us target.
    private static ChunkMap Arena(int ceilingRow = -1, int stepFrom = -1, int stepRows = 0)
    {
        var rows = new List<string>();
        for (int y = 0; y < 12; y++)
        {
            var sb = new StringBuilder();
            for (int x = 0; x < 40; x++)
                sb.Append(y >= 8 || x == 0 || x == 39 ||
                          (ceilingRow >= 0 && x >= 15 && y == ceilingRow) ||
                          (stepFrom >= 0 && x >= stepFrom && y >= 8 - stepRows) ? 'X' : 'O');
            rows.Add(sb.ToString());
        }
        return BotArena.FromAscii(string.Join("\n", rows));
    }

    private (BotArena.Run run, float maxDisp, bool carried) Fight(
        ChunkMap terrain, Vector2 p2, Vector2 p1, BotManeuver m, int frames)
    {
        float maxDisp = 0f; bool carried = false;
        Vector2 settled = p1;
        var run = BotArena.Fight(terrain, p1, p2, frames, BotArena.Only(m), p1: (f, sim) =>
        {
            if (f == 1) settled = sim.Player.Body.Position;
            if (f > 1) maxDisp = MathF.Max(maxDisp, Vector2.Distance(sim.Player.Body.Position, settled));
            carried |= sim.Player.CurrentStateName == "TerrainCarriedState";
            return default;
        });
        output.WriteLine($"maxDisp {maxDisp:F1} carried {carried}; {run.Summary()}");
        return (run, maxDisp, carried);
    }

    // ── BurstManeuver ───────────────────────────────────────────────────────────

    // In the slash band at close range: the shell connects (0.15 a hit) and shoves P1
    // well clear.
    [Fact]
    public void Burst_CloseInBand_ShovesAndChipsP1()
    {
        var p2 = new Vector2(100f, BotArena.FloorBodyY);
        var (run, disp, _) = Fight(Arena(), p2, p2 + new Vector2(30f, 0f), new BurstManeuver(), 60);
        Assert.True(run.Saw(nameof(BurstAction)), "the gesture never produced a BurstAction");
        Assert.True(run.DamageToP1 >= 0.15f - 1e-4f, $"burst never connected; P1 lost {run.DamageToP1}");
        Assert.True(disp > 25f, $"burst should shove P1 clear; moved {disp:F1}px");
    }

    // P1 standing on a 3-tile block beside us — above the slash's flat band, which is
    // exactly where the up-aimed shell earns its keep.
    [Fact]
    public void Burst_TargetOnLedgeAbove_Connects()
    {
        // Step from column 15 (world x = 121), 3 rows up: P1 stands at y ≈ 67 − 33.
        var p2 = new Vector2(105f, BotArena.FloorBodyY);
        var p1 = new Vector2(130f, BotArena.FloorBodyY - 3 * Chunk.TileSize);
        var (run, _, _) = Fight(Arena(stepFrom: 15, stepRows: 3), p2, p1, new BurstManeuver(), 60);
        Assert.True(run.Saw(nameof(BurstAction)));
        Assert.True(run.DamageToP1 > 0f, $"up-aimed burst should reach a target on the ledge; {run.DamageToP1}");
    }

    // ── BlockBurstManeuver ──────────────────────────────────────────────────────

    // Open floor, 6 tiles out: the puff fires on P1 and the foam shoves them (no damage —
    // nothing pins them, so growth only displaces).
    [Fact]
    public void BlockBurst_OpenFloor_FiresAndDisplacesP1()
    {
        var p2 = new Vector2(100f, BotArena.FloorBodyY);
        var p1 = p2 + new Vector2(6 * Chunk.TileSize, 0f);
        var (run, disp, carried) = Fight(Arena(), p2, p1, new BlockBurstManeuver(), 60);
        Assert.True(run.Saw(nameof(BlockBurstAction)), "the gesture never produced a BlockBurstAction");
        Assert.True(carried || disp > 12f, $"foam never moved P1 ({disp:F1}px)");
        int foam = 0;
        var c = BotView.Cell(p1);
        for (int x = c.X - 4; x <= c.X + 4; x++)
        for (int y = c.Y - 3; y <= c.Y + 1; y++)
            if (run.Sim.Chunks.GetCellState(x, y) == TileState.Solid &&
                run.Sim.Chunks.GetCellType(x, y) == TileType.Foam) foam++;
        Assert.True(foam > 0, "no foam committed near the target");
    }

    // P1 in a crawlspace (three open rows under a ceiling): the plus can't push them out,
    // so the sprouts crush — at least one cell's SproutCrushDamage (0.5).
    [Fact]
    public void BlockBurst_ConfinedTarget_Crushes()
    {
        var p2 = new Vector2(100f, BotArena.FloorBodyY);
        var (run, _, _) = Fight(Arena(ceilingRow: 4), p2, p2 + new Vector2(6 * Chunk.TileSize, 0f),
                                new BlockBurstManeuver(), 60);
        Assert.True(run.Saw(nameof(BlockBurstAction)));
        Assert.True(run.DamageToP1 >= 0.5f, $"confined target should be crushed; lost {run.DamageToP1}");
    }

    // Score shape: the crawlspace case outranks a stock slash, the open case doesn't.
    [Fact]
    public void BlockBurst_Score_HighOnlyWhenConfined()
    {
        float Score(ChunkMap t)
        {
            var sim = new Simulation(t, new Vector2(166f, BotArena.FloorBodyY));
            var (p2, _) = sim.AddSecondaryPlayer(new Vector2(100f, BotArena.FloorBodyY));
            for (int f = 0; f < 5; f++) sim.Step(default, default);
            var v = new BotView
            {
                Sim = sim, Self = p2, Target = sim.Player, Rng = new Random(1),
                Me = p2.Body.Position, TargetPos = sim.Player.Body.Position,
            };
            return new BlockBurstManeuver().Score(v);
        }
        float open = Score(Arena()), confined = Score(Arena(ceilingRow: 4));
        output.WriteLine($"open {open}, confined {confined}");
        Assert.InRange(open, 0.01f, 0.99f);
        Assert.True(confined > 1f);
    }
}
