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
    private static ChunkMap Stairs(int steps = 10, int treadWidth = 1)
    {
        const int W = 40;
        int h = steps + 6;              // rows: plateau headroom + runway floor
        int floorRow = h - 2;           // runway floor top row
        var rows = new StringBuilder[h];
        for (int r = 0; r < h; r++) rows[r] = new StringBuilder(new string('O', W));

        for (int c = 0; c < W; c++)
        {
            int top = c <= 7 ? floorRow
                    : floorRow - Math.Min(steps, (c - 8) / treadWidth + 1);
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

    // ── StairClimbState (2026-09-21): a regular flight is ONE fold climb ──────────────

    // Height of the body above the 45° line through the fixture's tread corners.
    private static float CornerLineHeight(float x, float y, int steps)
    {
        int floorRow = steps + 6 - 2;
        float lineY = (floorRow + 7) * Chunk.TileSize - x;
        return lineY - y;
    }

    private static SimFrame[] Climb(ChunkMap terrain, int steps, InputScript script = null, int frames = 400)
    {
        int floorRowTop = (steps + 6 - 2) * Chunk.TileSize;
        return SimRunner.Run(new SimConfig
        {
            Terrain = terrain,
            StartPosition = new Vector2(Chunk.TileSize + Chunk.TileSize / 2f, floorRowTop - PlayerCharacter.Radius),
            Script = script ?? InputScript.Always(new PlayerInput { Right = true }),
            Frames = frames, Dt = Dt, Gravity = Gravity,
        });
    }

    private static bool OnRamp(SimFrame f, int steps)
        => f.X > 9 * Chunk.TileSize && f.X < (7 + steps) * Chunk.TileSize;

    [Fact]
    public void RegularFlight_IsOneStairClimb_AtHoverHeight_NotAChainOfVaults()
    {
        const int Steps = 10;
        var frames = Climb(Stairs(Steps), Steps);
        var ramp = frames.Where(f => OnRamp(f, Steps)).ToArray();
        Assert.True(ramp.Length > 30, "never crossed the ramp");
        var states = ramp.Select(f => f.State).Distinct().ToList();
        output.WriteLine($"ramp states: {string.Join(", ", states)}");
        float hMean = ramp.Average(f => CornerLineHeight(f.X, f.Y, Steps));
        float hMin  = ramp.Min(f => CornerLineHeight(f.X, f.Y, Steps));
        float vyMin = ramp.Min(f => f.Vy);
        int topFrame = Array.FindIndex(frames, f => f.X > (8 + Steps) * Chunk.TileSize + Chunk.TileSize / 2f);
        output.WriteLine($"corner-line height mean {hMean:F1} min {hMin:F1} px; fastest rise {vyMin:F0} px/s; top at {topFrame * Dt:F2}s");
        // One state owns the flight — no vault re-entries riser by riser, and no one-frame
        // drops to Falling/WallSlide at the corners (the stair probe, not the ground probe).
        Assert.Equal(new[] { "StairClimbState" }, states);
        // The body rides the corner line at hover height (the stairs clip assumes ~19 px;
        // the vault chain measured 12 px and dipped to 8), as a glide, not a sawtooth.
        Assert.True(hMean >= 15f, $"corner-line height mean {hMean:F1} px");
        Assert.True(hMin >= 12f, $"corner-line height min {hMin:F1} px — bottom vertex on the lips");
        Assert.True(vyMin > -130f, $"vertical spike {vyMin:F0} px/s — the hop sawtooth is back");
        Assert.True(topFrame > 0 && topFrame * Dt < 4f, $"top at {topFrame * Dt:F2}s");
    }

    [Fact]
    public void TwoColumnTreads_KeepTheClimbAcrossTheWholeTread()
    {
        const int steps = 10;
        var frames = Climb(Stairs(steps, treadWidth: 2), steps);
        var middle = frames.Where(f => f.X > 10 * Chunk.TileSize && f.X < 24 * Chunk.TileSize).ToArray();
        Assert.True(middle.Length > 30);
        Assert.All(middle, f => Assert.Equal("StairClimbState", f.State));
        Assert.Contains(frames, f => f.X > 29 * Chunk.TileSize);
    }

    [Fact]
    public void JumpPreemptsTheStairClimb()
    {
        const int Steps = 10;
        bool climbing = false;
        var script = new InputScript()
            .Until(new PlayerInput { Right = true }, f => { if (f.State == "StairClimbState" && f.X > 10 * Chunk.TileSize) climbing = true; return climbing; })
            .For(3, new PlayerInput { Right = true, Space = true })
            .Forever(new PlayerInput { Right = true });
        var frames = Climb(Stairs(Steps), Steps, script);
        int first = Array.FindIndex(frames, f => f.State == "StairClimbState");
        Assert.True(first > 0, "the flight was never claimed");
        Assert.Contains(frames.Skip(first), f => f.State.Contains("Jump"));
    }

    [Theory]
    [InlineData(1)]   // a lone step is the vault's, not a flight
    [InlineData(0)]   // flat ground: nothing to climb
    public void LoneStepOrFlat_NeverClaimsTheStairClimb(int steps)
    {
        var frames = Climb(Stairs(steps), steps, frames: 240);
        Assert.DoesNotContain(frames, f => f.State == "StairClimbState");
    }

    // Terrain edited under the climb: a tread deleted two columns ahead. The probe sees the
    // riser go missing when the body reaches the column before it and the flight ends there —
    // the state yields instead of climbing into the gap.
    [Fact]
    public void TreadDeletedMidClimb_EndsTheClimbAtTheGap()
    {
        const int Steps = 10;
        var terrain = Stairs(Steps);
        int floorRow = Steps + 6 - 2;
        int floorRowTop = floorRow * Chunk.TileSize;
        int gapCol = -1; bool broken = false; int lastClimbFrame = -1; float lastClimbX = 0f;
        var frames = SimRunner.RunMulti(new SimConfigMulti
        {
            Terrain = terrain, Frames = 300, Dt = Dt, Gravity = Gravity,
            Players = { new SimPlayer { StartPosition = new Vector2(Chunk.TileSize + Chunk.TileSize / 2f, floorRowTop - PlayerCharacter.Radius),
                                        Script = InputScript.Always(new PlayerInput { Right = true }) } },
        }, onFrame: (f, players) =>
        {
            var p = players[0];
            int col = (int)MathF.Floor(p.Body.Position.X / Chunk.TileSize);
            if (!broken && p.CurrentStateName == "StairClimbState" && col >= 10)
            {
                gapCol = col + 2;                                   // a tread two columns ahead
                terrain.BreakCell(gapCol, floorRow - (gapCol - 7)); // its top tile
                broken = true;
            }
            if (p.CurrentStateName == "StairClimbState") { lastClimbFrame = f; lastClimbX = p.Body.Position.X; }
        })[0];
        Assert.True(broken, "the climb never started");
        output.WriteLine($"gap at column {gapCol}; last climb frame {lastClimbFrame} at x={lastClimbX:F1} (gap column starts at x={gapCol * Chunk.TileSize})");
        // The state ended before the body's centre entered the gap column.
        Assert.True(lastClimbX < gapCol * Chunk.TileSize, $"still climbing at x={lastClimbX:F1}, inside/past the gap column");
    }

    [Fact]
    public void Disabled_FallsBackToTheVaultChain()
    {
        const int Steps = 10;
        bool prev = MovementConfig.Current.StairClimbEnabled;
        MovementConfig.Current.StairClimbEnabled = false;
        try
        {
            var frames = Climb(Stairs(Steps), Steps);
            Assert.DoesNotContain(frames, f => f.State == "StairClimbState");
            Assert.Contains(frames, f => f.State == "ParkourState");
        }
        finally { MovementConfig.Current.StairClimbEnabled = prev; }
    }
}
