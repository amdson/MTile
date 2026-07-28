using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// REPRO (full FSM on the corrector branch): two playtest reports from the
// corridor/gym stages. Diagnostic — no asserts.
//  1. Holding Jump while a vault fires under a low ceiling: CoveredJumpState
//     (Passive 48 > vault Active 46) steals the maneuver mid-hop and its
//     phase-2 launch ADDS JumpVelocity to the already-rising body.
//  2. Walking off a 1-high ledge: the fold's envelope reference follows the
//     corner bevel down (the anchor re-binds lower every frame, so the
//     TuckReachDown unbind never trips) and the tuck drags the body down.
public class VaultJumpAndLipReproTests(ITestOutputHelper output)
{
    [Fact]
    public void HoldJumpDuringVault_Corridor_Trace()
    {
        // Corridor-stage twin (see BumpyTunnelSpeedTests): flat runway feeding
        // the offset-bump tunnel. Ceiling row 2 (c>=16), top bumps row 3, bottom
        // bumps row 5, floor row 6.
        const int W = 64;
        var rows = new string[7];
        for (int r = 0; r < 7; r++)
        {
            var sb = new StringBuilder(W);
            for (int c = 0; c < W; c++)
            {
                bool tunnel = c >= 16;
                sb.Append(r switch
                {
                    6 => 'X',
                    2 when tunnel => 'X',
                    3 when tunnel && c % 4 == 3 => 'X',
                    5 when tunnel && c % 4 == 1 => 'X',
                    _ => 'O',
                });
            }
            rows[r] = sb.ToString();
        }
        var terrain = SimTerrain.FromAscii(string.Join("\n", rows), originTileX: 0, originTileY: 0);

        static bool InVault(SimFrame f) =>
            f.State is "ParkourCorrectorState" or "MantleCorrectorState" or "ArcJumpCorrectorState";

        var script = new InputScript()
            .Until(new PlayerInput { Right = true }, InVault)
            .Forever(new PlayerInput { Right = true, Space = true });

        var frames = SimRunner.Run(new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(24f, 74f),
            Script        = script,
            Frames        = 400,
            Dt            = 1f / 60f,
            Gravity       = new Vector2(0f, 600f),
        });

        output.WriteLine("  frame     t      x      y     vx     vy       fy  state");
        float minVy = 0f; int minVyFrame = -1; float minY = float.MaxValue;
        foreach (var f in frames)
        {
            if (f.Frame % 5 == 0 || f.Transition)
                output.WriteLine($"  {f.Frame,5} {f.T,5:F2} {f.X,6:F1} {f.Y,6:F1} {f.Vx,6:F1} {f.Vy,6:F1} {f.Fy,8:F0}  {f.State}{(f.Transition ? "  <- enter" : "")}");
            if (f.Vy < minVy) { minVy = f.Vy; minVyFrame = f.Frame; }
            if (f.Y < minY) minY = f.Y;
        }
        output.WriteLine($"\n  peak rise speed: vy={minVy:F1} at frame {minVyFrame} (JumpVelocity=-100); highest y={minY:F1} (tunnel interior y >= ~48)");
    }

    // Jump pressed one frame AFTER a vault commits (open sky, 1-high ledge):
    // with the climb family's Active at 29, the deliberate launch must steal
    // the committed arc (WallJump beside the face / DoubleJump once airborne)
    // and the resulting rise must stay jump-sized — no stacking.
    [Fact]
    public void JumpDuringCommittedVault_OpenSky_Trace()
    {
        var rows = new string[9];
        for (int r = 0; r < 9; r++)
        {
            var sb = new StringBuilder(40);
            for (int c = 0; c < 40; c++)
                sb.Append(r == 8 || (r == 7 && c >= 20) ? 'X' : 'O');
            rows[r] = sb.ToString();
        }
        var terrain = SimTerrain.FromAscii(string.Join("\n", rows), originTileX: 0, originTileY: 0);

        // The vault commits at x≈292 (see the Right-only run); press jump a
        // couple frames into the committed arc. (Until() re-evaluates its
        // condition every frame, so it needs a monotonic trigger like x.)
        var script = new InputScript()
            .Until(new PlayerInput { Right = true }, f => f.X > 295f)
            .Forever(new PlayerInput { Right = true, Space = true });

        var frames = SimRunner.Run(new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(80f, 106f),
            Script        = script,
            Frames        = 240,
            Dt            = 1f / 60f,
            Gravity       = new Vector2(0f, 600f),
        });

        output.WriteLine("  frame     t      x      y     vx     vy       fy  state   (step face x=320, ledge top y=112)");
        float minVy = 0f; int minVyFrame = -1;
        foreach (var f in frames)
        {
            if (f.Frame % 10 == 0 || f.Transition)
                output.WriteLine($"  {f.Frame,5} {f.T,5:F2} {f.X,6:F1} {f.Y,6:F1} {f.Vx,6:F1} {f.Vy,6:F1} {f.Fy,8:F0}  {f.State}{(f.Transition ? "  <- enter" : "")}");
            if (f.Vy < minVy) { minVy = f.Vy; minVyFrame = f.Frame; }
        }
        output.WriteLine($"\n  peak rise speed: vy={minVy:F1} at frame {minVyFrame}");
    }

    [Fact]
    public void WalkOffLedge_TuckDrag_Trace()
    {
        // 1-high ledge (rows 7+8 solid, c<10) dropping to plain floor (row 8).
        // Ledge top y=112, lip at x=160, floor top y=128.
        var rows = new string[9];
        for (int r = 0; r < 9; r++)
        {
            var sb = new StringBuilder(40);
            for (int c = 0; c < 40; c++)
                sb.Append(r == 8 || (r == 7 && c < 10) ? 'X' : 'O');
            rows[r] = sb.ToString();
        }
        var terrain = SimTerrain.FromAscii(string.Join("\n", rows), originTileX: 0, originTileY: 0);

        var script = new InputScript()
            .For(30, default)                                // settle to hover on the ledge
            .Forever(new PlayerInput { Right = true });

        var frames = SimRunner.Run(new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(80f, 95f),
            Script        = script,
            Frames        = 200,
            Dt            = 1f / 60f,
            Gravity       = new Vector2(0f, 600f),
        });

        output.WriteLine("  frame     t      x      y     vx     vy       fy  state   (lip at x=160; fy>0 = applied DOWN force)");
        foreach (var f in frames)
        {
            bool nearLip = f.X > 130f && f.X < 220f;
            if (nearLip || f.Transition || f.Frame % 20 == 0)
                output.WriteLine($"  {f.Frame,5} {f.T,5:F2} {f.X,6:F1} {f.Y,6:F1} {f.Vx,6:F1} {f.Vy,6:F1} {f.Fy,8:F0}  {f.State}{(f.Transition ? "  <- enter" : "")}");
        }
    }
}
