using System;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// SCRATCH DIAGNOSTIC — sweep of "jump into a 2-high corridor" over jump timing.
public class ZzzCorridorJumpSweep(ITestOutputHelper output) : IDisposable
{
    private readonly string _prevEngine = Swap("lattice");
    private static string Swap(string e) { var p = MovementConfig.Current.FoldEngine; MovementConfig.Current.FoldEngine = e; return p; }
    public void Dispose() => MovementConfig.Current.FoldEngine = _prevEngine;

    private const int Ts = Chunk.TileSize;
    private static readonly float Rest = 2f * PlayerCharacter.Radius - 3.6f;
    private static readonly PlayerInput Right = new() { Right = true };
    private static readonly PlayerInput RightJump = new() { Right = true, Space = true };
    private const int JumpHoldFrames = 12;
    private static PlayerInput JumpInput(int since, bool right) =>
        since < JumpHoldFrames ? (right ? RightJump : new PlayerInput { Space = true }) : (right ? Right : default);

    private static ChunkMap Terrain(int rows, int cols, Func<int, int, bool> solid)
    {
        var sb = new StringBuilder();
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++) sb.Append(solid(r, c) ? 'X' : 'O');
            if (r < rows - 1) sb.Append('\n');
        }
        return SimTerrain.FromAscii(sb.ToString());
    }

    private static Vector2 OnFloor(float x, int floorRow) => new(x, floorRow * Ts - Rest);

    [Fact]
    public void Sweep_JumpTiming_IntoTwoHighTunnel()
    {
        const float mouthX = 14 * Ts;
        output.WriteLine($"mouth at x={mouthX}, ceiling bottom y=64, floor top y=96, band 78.4..83.6");
        output.WriteLine("jumpX |  endX   endY  | maxX  | entered? | minY(apex) | endState");
        for (float jumpX = 40f; jumpX <= 215f; jumpX += 5f)
        {
            var chunks = Terrain(7, 48, (r, c) => r == 6 || (r == 3 && c >= 14));
            var sim = new Simulation(chunks, OnFloor(40f, 6));
            int pressed = -1; float minY = float.MaxValue, maxX = 0f;
            for (int f = 0; f < 240; f++)
            {
                if (pressed < 0 && sim.Player.Body.Position.X >= jumpX) pressed = f;
                sim.Step(pressed < 0 ? Right : JumpInput(f - pressed, right: true));
                var q = sim.Player.Body.Position;
                minY = MathF.Min(minY, q.Y); maxX = MathF.Max(maxX, q.X);
            }
            var end = sim.Player.Body.Position;
            bool entered = end.X > mouthX + 3 * Ts;
            output.WriteLine($"{jumpX,5:F0} | {end.X,6:F1} {end.Y,6:F1} | {maxX,6:F1} | {(entered ? "YES" : "no ")} | {minY,6:F1} | {sim.Player.CurrentStateName}");
        }
    }

    // Never-jump control: does walking in work at all?
    [Fact]
    public void Control_WalkIntoTwoHighTunnel()
    {
        var chunks = Terrain(7, 48, (r, c) => r == 6 || (r == 3 && c >= 14));
        var sim = new Simulation(chunks, OnFloor(40f, 6));
        for (int f = 0; f < 240; f++) sim.Step(Right);
        var end = sim.Player.Body.Position;
        output.WriteLine($"walk-in end=({end.X:F1},{end.Y:F1}) state={sim.Player.CurrentStateName}");
    }
}
