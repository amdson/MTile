using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// movement_todo #1: walking into a 45° staircase (1-block rise per 1-block
// run) climbs it SMOOTHLY — continuously chained move-up moves, no stalls,
// no jump spam, no bonk-and-retry churn. The contract is the outcome
// (monotonic-ish ascent at a real fraction of walk speed), not which layer
// does it — the ambient fold's climb rows and the mantle family are both
// legitimate stair-climbers; thrashing between them is not.
public class StairClimbTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;
    private static readonly Vector2 Gravity = new(0f, 600f);

    // 45° staircase: flat runway (cols 0..7, floor top at row FloorRow), then
    // one-tile rise per column for `steps` columns, then a plateau.
    private static ChunkMap Stairs(int steps = 10)
    {
        const int W = 40;
        int h = steps + 6;              // rows: plateau headroom + runway floor
        int floorRow = h - 2;           // runway floor top row
        var rows = new StringBuilder[h];
        for (int r = 0; r < h; r++) rows[r] = new StringBuilder(new string('O', W));

        for (int c = 0; c < W; c++)
        {
            int top = c <= 7 ? floorRow
                    : c < 8 + steps ? floorRow - (c - 7)
                    : floorRow - steps;
            for (int r = top; r < h; r++) rows[r][c] = 'X';
        }
        return SimTerrain.FromAscii(string.Join("\n", rows.Select(r => r.ToString())),
                                    originTileX: 0, originTileY: 0);
    }

    [Fact]
    public void WalkIntoStairs_ClimbsSmoothlyToTheTop()
    {
        const int Steps = 10;
        var terrain = Stairs(Steps);
        int floorRowTop = (Steps + 6 - 2) * Chunk.TileSize;   // runway floor surface y
        float startY = floorRowTop - PlayerCharacter.Radius;
        float plateauY = floorRowTop - Steps * Chunk.TileSize; // plateau surface y

        var frames = SimRunner.Run(new SimConfig
        {
            Terrain = terrain,
            StartPosition = new Vector2(Chunk.TileSize + Chunk.TileSize / 2f, startY),
            StartVelocity = Vector2.Zero,
            Script = InputScript.Always(new PlayerInput { Right = true }),
            Frames = 600,
            Dt = Dt, Gravity = Gravity,
        });

        var last = frames[^1];
        int topFrame = -1;
        float maxY = float.MinValue;   // lowest point reached (y-down: max = deepest)
        int backslides = 0;
        for (int i = 1; i < frames.Length; i++)
        {
            // Reached the plateau: standing height above the top surface.
            if (topFrame < 0 && frames[i].X > (8 + Steps) * Chunk.TileSize + Chunk.TileSize / 2f
                             && frames[i].Y < plateauY - 8f) topFrame = i;
            // Backslide: dropping a full step's height after having climbed
            // onto the stairs is a fall-and-retry, not a smooth climb.
            if (topFrame < 0 && frames[i].X > 9 * Chunk.TileSize
                             && frames[i].Y > frames[i - 1].Y + 0.5f) backslides++;
            maxY = MathF.Max(maxY, frames[i].Y);
        }

        var states = frames.Select(f => f.State).Distinct().ToList();
        output.WriteLine($"final x={last.X:F1} y={last.Y:F1} (plateau y≈{plateauY - 10}), " +
                         $"top at frame {topFrame}, backslide frames {backslides}");
        output.WriteLine($"states: {string.Join(", ", states)}");

        Assert.True(topFrame > 0, $"never reached the plateau (final x={last.X:F1}, y={last.Y:F1})");

        // Smoothness: the whole 10-step climb (160px up, ~176px across from
        // the stair base) lands inside a real-speed window. 600 frames = 10s
        // is generous; a chained climb at a healthy fraction of walk speed
        // should finish in well under half that.
        float climbSeconds = topFrame * Dt;
        output.WriteLine($"climbed {Steps} steps in {climbSeconds:F2}s");
        Assert.True(climbSeconds < 6f, $"stair climb too slow: {climbSeconds:F2}s for {Steps} steps");

        // Smoothness: descending frames while on the stairs mean fall-and-
        // retry churn. A few frames of settle are fine; sustained backsliding
        // is the bug this test exists to catch.
        Assert.True(backslides < 30, $"{backslides} descending frames mid-climb — bonk/retry churn");
    }
}
