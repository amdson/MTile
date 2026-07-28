using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// movement_todo #6 + #4: the 2-block arc is a DELIBERATE move — it fires only
// when running in (≥ ArcJumpRunSpeed) with Up held. Standing at a 2-block
// ledge holding Up grabs it instead. And a gripped corner is a push-off
// point: an inward/neutral jump press from a hang launches off the corner.
public class DeliberateClimbTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;
    private static readonly Vector2 Gravity = new(0f, 600f);

    // Flat runway (floor top y=64) into a 2-block rise (top y=32) at x=224.
    private static ChunkMap TwoBlockLedge()
    {
        var rows = new string[6];
        for (int r = 0; r < 6; r++)
        {
            var sb = new System.Text.StringBuilder(30);
            for (int c = 0; c < 30; c++)
                sb.Append(r >= 4 || (r >= 2 && c >= 14) ? 'X' : 'O');
            rows[r] = sb.ToString();
        }
        return SimTerrain.FromAscii(string.Join("\n", rows), originTileX: 0, originTileY: 0);
    }

    private SimFrame[] Run(Vector2 start, InputScript script, int frames = 300) =>
        SimRunner.Run(new SimConfig
        {
            Terrain = TwoBlockLedge(), StartPosition = start,
            Script = script, Frames = frames, Dt = Dt, Gravity = Gravity,
        });

    [Fact]
    public void RunningIn_WithUpHeld_ArcJumpsTheTwoBlockLedge()
    {
        var frames = Run(new Vector2(100f, 43f),
                         InputScript.Always(new PlayerInput { Right = true, Up = true }));
        output.WriteLine($"states: {string.Join(", ", frames.Select(f => f.State).Distinct())}");
        Assert.Contains(frames, f => f.State.Contains("ArcJump"));
        // Delivered: on the raised floor at some point (the run keeps going
        // and eventually leaves the map — irrelevant here).
        Assert.Contains(frames, f => f.X is > 240f and < 470f && f.Y < 40f);
    }

    [Fact]
    public void RunningIn_WithoutUp_TheArcStaysHome()
    {
        var frames = Run(new Vector2(100f, 43f),
                         InputScript.Always(new PlayerInput { Right = true }));
        var last = frames[^1];
        output.WriteLine($"states: {string.Join(", ", frames.Select(f => f.State).Distinct())}");
        output.WriteLine($"final x={last.X:F1} y={last.Y:F1}");
        Assert.DoesNotContain(frames, f => f.State.Contains("ArcJump"));
        // 2-block walls without Up are simply walls.
        Assert.True(last.Y > 40f, $"climbed the ledge without Up (y={last.Y:F1})");
    }

    [Fact]
    public void StandingAtTheLedge_UpHeld_GrabsInsteadOfArcing()
    {
        // Walk into the face (stalling against it — vx ≈ 0), then hold Up:
        // must grab, not arc — the run gate refuses a stalled entry.
        var frames = Run(new Vector2(180f, 43f),
                         new InputScript()
                             .For(60, new PlayerInput { Right = true })
                             .Forever(new PlayerInput { Right = true, Up = true }));
        output.WriteLine($"states: {string.Join(", ", frames.Select(f => f.State).Distinct())}");
        Assert.DoesNotContain(frames, f => f.State.Contains("ArcJump"));
        Assert.Contains(frames, f => f.State.Contains("LedgeGrab"));
    }

    [Fact]
    public void JumpPressFromTheHang_LaunchesOffTheCorner()
    {
        // Grab the ledge (standing + Up), then press jump with NO direction:
        // the hang releases and JumpingState fires off the corner — not
        // WallJump (that's the away-press bail), not a dead input.
        // Fixed timing (Until inflates downstream segment offsets by its
        // 9999-frame placeholder): the walk-in stalls by ~f55, the Up edge
        // grabs around f68, 12 frames settle the hang, then jump.
        var frames = Run(new Vector2(180f, 43f),
                         new InputScript()
                             .For(60, new PlayerInput { Right = true })
                             .For(8, new PlayerInput { Right = true, Up = true })
                             .For(12, new PlayerInput { Up = true })
                             .For(6, new PlayerInput { Up = true, Space = true })
                             .Forever(default),
                         frames: 300);

        int grabAt = Array.FindIndex(frames, f => f.State.Contains("LedgeGrab"));
        Assert.True(grabAt > 0, "never grabbed the ledge");
        var after = frames.Skip(grabAt).ToArray();
        int jumpAt = Array.FindIndex(after, f => f.State == "JumpingState");
        float maxRise = after.Select(f => -f.Vy).Max();
        output.WriteLine($"grab f{grabAt}, jump at +{jumpAt}, max rise {maxRise:F1}");
        output.WriteLine($"states after grab: {string.Join(", ", after.Select(f => f.State).Distinct())}");
        Assert.True(jumpAt > 0, "jump press from the hang never launched (dead input?)");
        Assert.DoesNotContain(after.Take(jumpAt + 2), f => f.State.Contains("WallJump"));
        Assert.True(maxRise > 90f, $"corner launch too weak: rise {maxRise:F1}");
    }
}
