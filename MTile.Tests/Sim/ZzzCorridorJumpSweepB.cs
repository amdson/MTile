using System;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// SCRATCH DIAGNOSTIC — "jump UP into a 2-high corridor whose mouth is elevated".
//
// Geometry (tile 16, y-down), 10 rows x 40 cols:
//   row 8      ground floor, top y = 128     (player starts here)
//   c >= 14:   rows 6,7 solid  -> a 2-tile step, corridor floor top y = 96
//              row 3 solid     -> corridor ceiling, bottom y = 64
//              rows 4,5 open   -> 32 px interior: standing (~31 px) fits by ~1 px
// So entering needs a jump that clears a 32 px step and arrives INSIDE a 32 px slot.
public class ZzzCorridorJumpSweepB(ITestOutputHelper output) : IDisposable
{
    private readonly string _prevEngine = Swap("lattice");
    private static string Swap(string e) { var p = MovementConfig.Current.FoldEngine; MovementConfig.Current.FoldEngine = e; return p; }
    public void Dispose() => MovementConfig.Current.FoldEngine = _prevEngine;

    private const int Ts = Chunk.TileSize;
    private static readonly float Rest = 2f * PlayerCharacter.Radius - 3.6f;   // 20.4
    private static readonly PlayerInput Right = new() { Right = true };
    private static readonly PlayerInput RightJump = new() { Right = true, Space = true };
    private static readonly PlayerInput RightUp = new() { Right = true, Up = true };
    private const int JumpHoldFrames = 12;

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

    private static ChunkMap Elevated() =>
        Terrain(10, 40, (r, c) => r == 8 || (c >= 14 && (r == 6 || r == 7 || r == 3)));

    private static Vector2 OnFloor(float x, int floorRow) => new(x, floorRow * Ts - Rest);

    private const float MouthX = 14 * Ts;   // 224

    [Fact]
    public void Sweep_JumpTiming_UpIntoElevatedTwoHighCorridor()
    {
        output.WriteLine($"step face x={MouthX}; corridor floor top y=96 (rest y=75.6); ceiling bottom y=64");
        output.WriteLine("jumpX |  endX   endY | maxX  | in? | apexY | endState");
        for (float jumpX = 120f; jumpX <= 225f; jumpX += 5f)
        {
            var sim = new Simulation(Elevated(), OnFloor(40f, 8));
            int pressed = -1; float minY = float.MaxValue, maxX = 0f;
            for (int f = 0; f < 300; f++)
            {
                if (pressed < 0 && sim.Player.Body.Position.X >= jumpX) pressed = f;
                var inp = pressed < 0 ? Right
                        : (f - pressed) < JumpHoldFrames ? RightJump : Right;
                sim.Step(inp);
                var q = sim.Player.Body.Position;
                minY = MathF.Min(minY, q.Y); maxX = MathF.Max(maxX, q.X);
            }
            var end = sim.Player.Body.Position;
            bool inside = end.X > MouthX + 2 * Ts && end.Y > 64f && end.Y < 96f;
            output.WriteLine($"{jumpX,5:F0} | {end.X,6:F1} {end.Y,6:F1} | {maxX,6:F1} | {(inside ? "YES" : "no ")} | {minY,6:F1} | {sim.Player.CurrentStateName}");
        }
    }

    // Same terrain, but hold Up into the face (the ArcJump / parkour route) with no Space.
    [Fact]
    public void HoldUp_RunIntoElevatedTwoHighCorridor()
    {
        var sim = new Simulation(Elevated(), OnFloor(40f, 8));
        string prev = "";
        for (int f = 0; f < 300; f++)
        {
            sim.Step(RightUp);
            var s = sim.Player.CurrentStateName;
            var q = sim.Player.Body.Position;
            if (s != prev) { output.WriteLine($"  f{f,3} x={q.X,7:F1} y={q.Y,6:F1} {s}"); prev = s; }
        }
        var end = sim.Player.Body.Position;
        output.WriteLine($"hold-up end=({end.X:F1},{end.Y:F1}) state={sim.Player.CurrentStateName}");
    }

    // Plain run into the face, no jump input at all — control.
    [Fact]
    public void Control_RunIntoFace_NoJump()
    {
        var sim = new Simulation(Elevated(), OnFloor(40f, 8));
        string prev = "";
        for (int f = 0; f < 300; f++)
        {
            sim.Step(Right);
            var s = sim.Player.CurrentStateName;
            var q = sim.Player.Body.Position;
            if (s != prev) { output.WriteLine($"  f{f,3} x={q.X,7:F1} y={q.Y,6:F1} {s}"); prev = s; }
        }
        var end = sim.Player.Body.Position;
        output.WriteLine($"run-in end=({end.X:F1},{end.Y:F1}) state={sim.Player.CurrentStateName}");
    }
}
