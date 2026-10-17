using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// PulserEnemy (Entities/Enemies/Types/PulserEnemy.cs): its pulse hurts and throws a
// player in the open, terrain between them is full cover, and it crawls up a wall
// toward a player above rather than falling off it.
public class PulserEnemyTests(ITestOutputHelper output)
{
    private const float FloorTopY = 12 * Chunk.TileSize;   // floor surface, tile row 12

    // 48 tiles wide, rows 0..15, floor at row 12; `raised` lifts columns by h tiles.
    private static ChunkMap Course(params (int fromX, int toX, int h)[] raised)
    {
        var rows = new List<string>();
        for (int y = 0; y < 16; y++)
        {
            var sb = new StringBuilder();
            for (int x = 0; x < 48; x++)
            {
                int h = 0;
                foreach (var r in raised) if (x >= r.fromX && x <= r.toX) h = r.h;
                sb.Append(y >= 12 - h ? 'X' : 'O');
            }
            rows.Add(sb.ToString());
        }
        return SimTerrain.FromAscii(string.Join("\n", rows), originTileX: 0, originTileY: 0);
    }

    private static float StandY(int h) => FloorTopY - h * Chunk.TileSize - PulserEnemy.BodyRadius - 1f;

    private static (Simulation sim, PulserEnemy p) Setup(ChunkMap map, Vector2 player, float pulserX, int pulserH = 0)
    {
        var sim = new Simulation(map, player,
            g => g.SpawnEntity(new PulserEnemy(new Vector2(pulserX, StandY(pulserH)))));
        return (sim, (PulserEnemy)sim.Entities.Single(e => e.Kind == EntityKind.Pulser));
    }

    [Fact]
    public void PulserEnemy_PulseHurtsAndThrowsAPlayerInTheOpen()
    {
        var (sim, p) = Setup(Course(), new Vector2(20 * Chunk.TileSize, FloorTopY - 30f), 25 * Chunk.TileSize);
        int f = 0;
        for (; f < 300 && sim.Player.Combat.DamageTaken == 0f; f++) sim.Step(default);
        float hitX = sim.Player.Body.Position.X;
        float minX = hitX;
        for (int k = 0; k < 40; k++) { sim.Step(default); minX = System.MathF.Min(minX, sim.Player.Body.Position.X); }
        output.WriteLine($"hit on frame {f}, dmg {sim.Player.Combat.DamageTaken:F2}, thrown {hitX - minX:F0} px");

        Assert.True(sim.Player.Combat.DamageTaken >= 1.5f, "A player standing in the open inside the radius should be hit.");
        Assert.True(f >= 60, "The pulse should charge for a long, readable windup before landing.");
        Assert.True(hitX - minX > 20f, "The pulse should throw the player outward, away from its centre.");
        Assert.False(p.PulseReady, "Releasing should start the cooldown.");
    }

    [Fact]
    public void PulserEnemy_TerrainBetweenThemIsFullCover()
    {
        var (sim, p) = Setup(Course(), new Vector2(20 * Chunk.TileSize, FloorTopY - 30f), 25 * Chunk.TileSize);
        int f = 0;
        for (; f < 300 && p.CurrentActionIndex < 0; f++) sim.Step(default);
        Assert.True(p.CurrentActionIndex >= 0, "The pulser should start charging with the player in range and in sight.");

        // Mid-charge, a wall goes up between them — exactly what a player who builds
        // or ducks behind a block does.
        for (int gty = 7; gty <= 11; gty++) sim.Chunks.EditorSetCell(22, gty, TileState.Solid, TileType.Stone);

        for (int k = 0; k < 120; k++) sim.Step(default);
        Assert.False(p.PulseReady, "The pulse should have gone off.");
        Assert.Equal(0f, sim.Player.Combat.DamageTaken);
    }

    [Fact]
    public void PulserEnemy_CrawlsUpAWallTowardAPlayerAbove()
    {
        // A 10-tile plateau from column 30 on; the player waits on top, the pulser
        // starts on the floor below it.
        var map = Course((30, 47, 10));
        float plateauTop = FloorTopY - 10 * Chunk.TileSize;
        var (sim, p) = Setup(map, new Vector2(42 * Chunk.TileSize, plateauTop - 30f), 26 * Chunk.TileSize);

        float minY = p.Body.Position.Y;
        for (int f = 0; f < 60 * 12; f++)
        {
            sim.Step(default);
            minY = System.MathF.Min(minY, p.Body.Position.Y);
        }
        output.WriteLine($"pulser at {p.Body.Position}, highest y {minY:F0}, plateau top {plateauTop:F0}");
        Assert.True(p.Body.Position.Y < plateauTop, "The pulser should have climbed the wall onto the plateau.");
        Assert.True(p.Body.Position.X > 30 * Chunk.TileSize, "…and crawled out over the top toward the player.");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(8)]
    public void PulserEnemy_CrawlsOverABarrierToAPlayerBeyondIt(int height)
    {
        // A two-column barrier on the floor between them; the player waits on the far side.
        var map = Course((30, 31, height));
        var (sim, p) = Setup(map, new Vector2(40 * Chunk.TileSize, FloorTopY - 30f), 22 * Chunk.TileSize);

        float minY = p.Body.Position.Y;
        int crossed = -1;
        for (int f = 0; f < 60 * 15 && crossed < 0; f++)
        {
            sim.Step(default);
            minY = System.MathF.Min(minY, p.Body.Position.Y);
            // Over the top, or charging at the player — which needs range (96 px) it
            // can't have from the near side, and a clear sight line.
            if (p.Body.Position.X > 31 * Chunk.TileSize || p.CurrentActionIndex >= 0) crossed = f;
        }
        output.WriteLine($"h{height}: pulser at {p.Body.Position}, highest y {minY:F0}, crossed on frame {crossed}");
        Assert.True(crossed >= 0, $"The pulser should crawl over a {height}-tile barrier to reach the player.");
    }
}
