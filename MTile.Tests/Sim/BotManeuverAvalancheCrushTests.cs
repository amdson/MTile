using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// AvalancheCrushManeuver end to end: P1 (AFK) stands in a roofed pocket; the bot
// launches a rising wave that lifts P1 into the roof, and the wedged sprouts crush — then,
// wall-backed, the stunned P1 is pinned against the wall (todo #2 wall pin, 1.5 HP).
// FreeRoof_CrushesP1 has been red since avalanche sprouts went to 0.04 s/cell (c86fc15);
// the wall stop (todo #4) didn't change that — see the maneuver's header.
public class BotManeuverAvalancheCrushTests(ITestOutputHelper output)
{
    // Flat deep floor (top row 8, 12 rows deep), thick walls; right wall's inner face at
    // x = 363. `roofRow` ≥ 0 adds a two-row roof over columns [roofFrom, roofTo) — a
    // pocket of (7 − roofRow) open rows.
    private static ChunkMap Arena(int roofRow = -1, int roofFrom = 30, int roofTo = 40)
    {
        var rows = new List<string>();
        for (int y = 0; y < 20; y++)
        {
            var sb = new StringBuilder();
            for (int x = 0; x < 40; x++)
                sb.Append(y >= 8 || x < 3 || x >= 37 || (roofRow >= 0 && x >= roofFrom && x < roofTo && y <= roofRow && y >= roofRow - 1) ? 'X' : 'O');
            rows.Add(sb.ToString());
        }
        return BotArena.FromAscii(string.Join("\n", rows));
    }

    private const float WallFace = 363f;
    private static Vector2 AtWall(float gapPx) => new(WallFace - PlayerCharacter.Radius - gapPx, BotArena.FloorBodyY);

    private BotArena.Run Fight(ChunkMap terrain, Vector2 p1, int tiles, BrawlerBotInputSource bot, int frames = 200)
    {
        var r = BotArena.Fight(terrain, p1, p1 - new Vector2(tiles * Chunk.TileSize, 0f), frames, bot);
        output.WriteLine($"{tiles} tiles: {r.Summary()}");
        return r;
    }

    // Wall-backed pocket (roof over the last nine columns): roof row 4..1 = 3..6 open rows.
    [Theory]
    [InlineData(4, 1f, 9)]
    [InlineData(3, 11f, 6)]
    [InlineData(2, 22f, 12)]
    [InlineData(1, 1f, 9)]
    [InlineData(1, 11f, 12)]
    public void WallPocket_CrushesP1(int roofRow, float gapPx, int tiles)
    {
        var m = new AvalancheCrushManeuver();
        var r = Fight(Arena(roofRow, 28), AtWall(gapPx), tiles, BotArena.Only(m));
        Assert.True(m.Fired, "never erupted");
        Assert.True(r.DamageToP1 >= 0.5f, $"expected a sprout crush on P1; lost {r.DamageToP1}");
        Assert.Equal(0f, r.DamageToP2);
    }

    // Free-standing roof (no wall): still crushes at the chosen angle/lead.
    [Theory]
    [InlineData(3, 26)]
    [InlineData(2, 29)]
    public void FreeRoof_CrushesP1(int roofRow, int col)
    {
        var m = new AvalancheCrushManeuver();
        var p1 = new Vector2((col - 4) * Chunk.TileSize + Chunk.TileSize * 0.5f, BotArena.FloorBodyY);
        var r = Fight(Arena(roofRow, 24, 32), p1, 9, BotArena.Only(m));
        Assert.True(m.Fired, "never erupted");
        Assert.True(r.DamageToP1 >= 0.5f, $"expected a sprout crush on P1; lost {r.DamageToP1}");
    }

    // An open-topped wall is NOT a crush (the pile carries P1 up the face), so it's not
    // scored.
    [Fact]
    public void OpenWall_DoesNotScore()
    {
        var m = new AvalancheCrushManeuver();
        var r = Fight(Arena(), AtWall(1f), 9, BotArena.Only(m), frames: 120);
        Assert.False(m.Fired);
        Assert.False(r.Saw(nameof(BlockPaintAction)));
    }

    // Where this beats the existing tool: Eruption's floor ridge in the same tall pockets
    // (5–6 open rows) never reaches the roof; this wave climbs into it.
    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    public void TallPocket_BeatsEruption(int roofRow)
    {
        var crush = new AvalancheCrushManeuver();
        var rc = Fight(Arena(roofRow, 28), AtWall(11f), 9, BotArena.Only(crush));
        var re = Fight(Arena(roofRow, 28), AtWall(11f), 9, BotArena.Only(new EruptionManeuver()));
        output.WriteLine($"crush {rc.DamageToP1} vs eruption {re.DamageToP1}");
        Assert.True(rc.DamageToP1 > re.DamageToP1);
    }

    // The target walks out of the pocket mid-charge: let go in the floor, no eruption.
    [Fact]
    public void TargetLeaves_ChargeIsBankedNotFired()
    {
        var m = new AvalancheCrushManeuver();
        var p1 = AtWall(11f);
        var r = BotArena.Fight(Arena(2, 28), p1, p1 - new Vector2(9 * Chunk.TileSize, 0f), 120, BotArena.Only(m),
            p1: (f, sim) => new PlayerInput { Left = f >= 15 });
        output.WriteLine(r.Summary());
        Assert.True(r.Saw(nameof(BlockPaintAction)), "the charge should have started");
        Assert.False(m.Fired);
        Assert.DoesNotContain(r.Sim.Entities, e => e is MassBall);
    }
}
