using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// movement_todo #6 + #4: the tall arc is a DELIBERATE move — Up held, and either
// running in (≥ ArcJumpRunSpeed) or standing at a step there is no other way up.
// Standing at a step tall enough to HANG from grabs it instead. And a gripped
// corner is a push-off point: an inward/neutral jump press from a hang launches
// off the corner.
//
// Every fixture here is sized in PX off the config bands and the body, never in
// blocks: which move owns a step is a question about rise height vs. the mantle
// band and the body's standing height, and TileSize has already moved twice
// under these tests (16 → 11 → 10). Two tile heights are what matters:
//
//   ArcRise   — the SHORTEST step too tall to mantle, and still below head
//               height: nothing to hang from, so the arc owns it at any speed.
//   HangRise  — a step taller than the body: the grab probe finds its corner
//               above the head, so a standing entry hangs instead.
public class DeliberateClimbTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;
    private static readonly Vector2 Gravity = new(0f, 600f);
    private const int TS = Chunk.TileSize;

    // Shortest whole-tile rise above MantleMaxRise (the arc band's floor).
    private static int ArcRise => (int)(MovementConfig.Current.MantleMaxRise / TS) + 1;
    // Shortest whole-tile rise above the standing body — a hangable lip.
    private static int HangRise => (int)(PlayerCharacter.StandingHeight / TS) + 1;

    private const int FloorRow = 8, StepCol = 14;

    // Flat runway into a rise of `riseTiles` tiles at column StepCol.
    private static ChunkMap Ledge(int riseTiles)
    {
        var rows = new string[FloorRow + 2];
        for (int r = 0; r < rows.Length; r++)
        {
            var sb = new System.Text.StringBuilder(30);
            for (int c = 0; c < 30; c++)
                sb.Append(r >= FloorRow || (r >= FloorRow - riseTiles && c >= StepCol) ? 'X' : 'O');
            rows[r] = sb.ToString();
        }
        return SimTerrain.FromAscii(string.Join("\n", rows), originTileX: 0, originTileY: 0);
    }

    private static float FloorTopY => FloorRow * TS;
    private static float StepTopY(int riseTiles) => (FloorRow - riseTiles) * TS;
    // Body center when standing on a surface at `topY`.
    private static float StandingCenterY(float topY) => topY - 2f * PlayerCharacter.Radius;

    private SimFrame[] Run(int riseTiles, InputScript script, int frames = 300, float startX = 100f) =>
        SimRunner.Run(new SimConfig
        {
            Terrain = Ledge(riseTiles),
            StartPosition = new Vector2(startX, StandingCenterY(FloorTopY)),
            Script = script, Frames = frames, Dt = Dt, Gravity = Gravity,
        });

    private void Dump(SimFrame[] frames)
    {
        output.WriteLine($"TS={TS} arcRise={ArcRise * TS}px hangRise={HangRise * TS}px " +
                         $"standingHeight={PlayerCharacter.StandingHeight:F1} stepX={StepCol * TS}");
        output.WriteLine("states: " + string.Join(", ", frames.Select(f => f.State).Distinct()));
        foreach (var f in frames.Where(f => f.Transition))
            output.WriteLine($"  f{f.Frame}: {f.State} x={f.X:F1} y={f.Y:F1} vx={f.Vx:F0}");
    }

    // True once the body is standing on top of the step (at rest height, past the lip).
    private static bool OnTop(SimFrame f, int riseTiles) =>
        f.X > StepCol * TS && f.Y <= StandingCenterY(StepTopY(riseTiles)) + 2f;

    [Fact]
    public void RunningIn_WithUpHeld_ArcJumpsTheStep()
    {
        var frames = Run(ArcRise, InputScript.Always(new PlayerInput { Right = true, Up = true }));
        Dump(frames);
        Assert.Contains(frames, f => f.State.Contains("ArcJump"));
        Assert.Contains(frames, f => OnTop(f, ArcRise));
    }

    [Fact]
    public void RunningIn_WithoutUp_TheArcStaysHome()
    {
        var frames = Run(ArcRise, InputScript.Always(new PlayerInput { Right = true }));
        Dump(frames);
        Assert.DoesNotContain(frames, f => f.State.Contains("ArcJump"));
        // A step above the mantle band, without Up, is simply a wall.
        Assert.DoesNotContain(frames, f => OnTop(f, ArcRise));
    }

    // The case the arc's run gate used to swallow: walk up to a step too tall to
    // mantle and too short to hang from, come to rest against its face, THEN hold
    // Up. There is no hangable corner to defer to, so the arc takes the standstill.
    // Two things had to give for this to work — the run gate (which handed every
    // standstill to a grab that cannot see a below-head lip) and the entry
    // feasibility probe (which judged an arc starting inside the step's own
    // clearance margin, and let the pushback eat the hop's rise).
    [Fact]
    public void StandingFlushAtAnUnhangableStep_UpHeld_ArcsOverIt()
    {
        var frames = Run(ArcRise, new InputScript()
            .For(60, new PlayerInput { Right = true })                  // walk in, stall on the face
            .Forever(new PlayerInput { Right = true, Up = true }));
        Dump(frames);

        int stalledAt = 59;
        Assert.True(MathF.Abs(frames[stalledAt].Vx) < 5f,
            $"fixture assumes the walk-in has stalled by f{stalledAt} (vx={frames[stalledAt].Vx:F1})");
        Assert.Contains(frames, f => f.State.Contains("ArcJump"));
        Assert.Contains(frames, f => OnTop(f, ArcRise));
    }

    [Fact]
    public void StandingAtAHangableLedge_UpHeld_GrabsInsteadOfArcing()
    {
        // Walk into the face (stalling against it — vx ≈ 0), then hold Up: the
        // lip is above head height, so the hang owns it and the arc stands down.
        var frames = Run(HangRise, new InputScript()
            .For(60, new PlayerInput { Right = true })
            .Forever(new PlayerInput { Right = true, Up = true }));
        Dump(frames);
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
        var frames = Run(HangRise, new InputScript()
            .For(60, new PlayerInput { Right = true })
            .For(8,  new PlayerInput { Right = true, Up = true })
            .For(12, new PlayerInput { Up = true })
            .For(6,  new PlayerInput { Up = true, Space = true })
            .Forever(default));
        Dump(frames);

        int grabAt = Array.FindIndex(frames, f => f.State.Contains("LedgeGrab"));
        Assert.True(grabAt > 0, "never grabbed the ledge");
        var after = frames.Skip(grabAt).ToArray();
        int jumpAt = Array.FindIndex(after, f => f.State == "JumpingState");
        float maxRise = after.Select(f => -f.Vy).Max();
        output.WriteLine($"grab f{grabAt}, jump at +{jumpAt}, max rise {maxRise:F1}");
        Assert.True(jumpAt > 0, "jump press from the hang never launched (dead input?)");
        Assert.DoesNotContain(after.Take(jumpAt + 2), f => f.State.Contains("WallJump"));
        Assert.True(maxRise > 90f, $"corner launch too weak: rise {maxRise:F1}");
    }
}
