using System.Text;
using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests.Sim;

// A clod thrown into a wall from point-blank range bursts AT the wall. The strike
// impulse is visible for one frame only, and it used to land inside the arm delay
// (Age < ArmDelay, which a grab's Detach also restarts) — the clod lost its sideways
// speed, slid down the wall and burst on the floor instead.
public class BlockThrowWallStrikeTests
{
    const int T = Chunk.TileSize, WallCol = 40, Row = 20;

    static ChunkMap Terrain()
    {
        var sb = new StringBuilder();
        for (int r = 0; r < 40; r++)
        {
            for (int c = 0; c < 60; c++) sb.Append(c >= WallCol || r >= 36 ? 'X' : 'O');
            if (r < 39) sb.Append('\n');
        }
        return SimTerrain.FromAscii(sb.ToString());
    }

    [Theory]
    [InlineData(150f, 1f)] [InlineData(150f, 6f)]
    [InlineData(250f, 2f)] [InlineData(250f, 8f)]
    [InlineData(400f, 1f)] [InlineData(400f, 12f)]
    public void PointBlankThrow_BurstsAtTheWall(float speed, float distance)
    {
        var sim = new Simulation(Terrain(), new Vector2(60, Row * T));
        var ball = new LobbedAreaProjectile(new Vector2(WallCol * T - distance, Row * T), new Vector2(speed, 0f),
                                            budget: 6, tileType: TileType.Dirt,
                                            hitId: sim.HitIds.Next(), owner: Faction.Player1);
        sim.SpawnEntity(ball);
        int burst = -1;
        for (int f = 0; f < 120 && burst < 0; f++) { sim.Step(default); if (ball.Detonated) burst = f; }

        Assert.True(burst >= 0, "the clod never burst");
        Assert.True(burst <= 10, $"burst at f{burst}: it fell instead of bursting on the wall");
        Assert.True(ball.Body.Position.Y < Row * T + T, $"burst at y={ball.Body.Position.Y:F0}, below the strike point");
    }
}
