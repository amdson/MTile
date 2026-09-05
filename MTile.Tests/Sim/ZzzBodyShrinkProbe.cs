using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Scratch probe for the body-polygon shrink work: measures standing/crouch
// equilibrium height above the floor and 2-high corridor traversal. No asserts.
public class ZzzBodyShrinkProbe(ITestOutputHelper output)
{
    private const int TS = Chunk.TileSize;

    [Fact]
    public void StandAndCrouchEquilibrium()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXX
            XXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);
        float floorTop = 6f * TS;

        foreach (bool down in new[] { false, true })
        {
            var frames = SimRunner.Run(new SimConfig
            {
                Terrain       = terrain,
                StartPosition = new Vector2(8f * TS, floorTop - 30f),
                StartVelocity = Vector2.Zero,
                Script        = InputScript.Always(new PlayerInput { Down = down }),
                Frames        = 240,
                Dt            = 1f / 60f,
                Gravity       = new Vector2(0f, 600f),
            });
            var last = frames[^1];
            float centerAbove = floorTop - last.Y;
            var poly = PlayerCharacter.CreateBodyPolygon();
            var bb = poly.GetBoundingBox(Vector2.Zero);
            output.WriteLine($"{(down ? "CROUCH" : "STAND ")}: state={last.State} centerY={last.Y:F2} " +
                             $"centerAboveFloor={centerAbove:F2} topAboveFloor={centerAbove - bb.Top:F2} " +
                             $"bottomGap={centerAbove - bb.Bottom:F2}");
            output.WriteLine($"        polygon bb: yTop={bb.Top:F2} yBottom={bb.Bottom:F2} " +
                             $"xLeft={bb.Left:F2} xRight={bb.Right:F2} (extent {bb.Height:F2} x {bb.Width:F2})");
        }
    }

    // Walk-speed equilibrium vs FoldHoverOffset: isolates whether the ~102 px/s
    // equilibrium tracks the hover offset or the leg-reach constants.
    [Fact]
    public void WalkSpeedVsHover()
    {
        var terrain = SimTerrain.FromAscii(new string('X', 60), originTileX: 0, originTileY: 6);
        float floorTop = 6f * TS;
        foreach (float hover in new[] { 14.8f, 12f, 10f, 8f })
        {
            float prev = MovementConfig.Current.FoldHoverOffset;
            MovementConfig.Current.FoldHoverOffset = hover;
            try
            {
                var frames = SimRunner.Run(new SimConfig
                {
                    Terrain       = terrain,
                    StartPosition = new Vector2(64f, floorTop - 24f),
                    Script        = InputScript.Always(new PlayerInput { Right = true }),
                    Frames        = 90,
                    Dt            = 1f / 60f,
                    Gravity       = new Vector2(0f, 600f),
                });
                output.WriteLine($"hover={hover,5:F1}: settled vx={frames[^1].Vx:F2} y={frames[^1].Y:F2}");
            }
            finally { MovementConfig.Current.FoldHoverOffset = prev; }
        }
    }

    // Row09 twin with a state trace: neutral jump on flat ground under lattice.
    [Fact]
    public void NeutralJumpTrace()
    {
        var prev = MovementConfig.Current.FoldEngine;
        MovementConfig.Current.FoldEngine = "lattice";
        try
        {
            var sb = new StringBuilder();
            for (int r = 0; r < 7; r++)
            {
                for (int c = 0; c < 24; c++) sb.Append(r == 6 ? 'X' : 'O');
                if (r < 6) sb.Append('\n');
            }
            var chunks = SimTerrain.FromAscii(sb.ToString());
            float rest = 2f * PlayerCharacter.Radius - 3.6f;
            var start = new Vector2(100f, 6 * TS - rest);
            var sim = new Simulation(chunks, start);
            string lastState = "";
            float minY = float.MaxValue;
            for (int f = 0; f < 120; f++)
            {
                var input = f < 12 ? new PlayerInput { Space = true } : default;
                sim.Step(input);
                var p = sim.Player.Body.Position;
                minY = MathF.Min(minY, p.Y);
                string state = sim.Player.CurrentStateName;
                if (f % 5 == 0 || state != lastState)
                    output.WriteLine($"  f={f,3} y={p.Y,6:F1} vy={sim.Player.Body.Velocity.Y,7:F1} {state}{(state != lastState ? " <-" : "")}");
                lastState = state;
            }
            output.WriteLine($"  rise={start.Y - minY:F1}");
        }
        finally { MovementConfig.Current.FoldEngine = prev; }
    }

    // Flat runway feeding a 2-tile-high (22px) corridor. Hold Right (+Down in the
    // second pass). Prints how far the body gets.
    [Fact]
    public void TwoHighCorridorTraversal()
    {
        const int W = 40;
        var rows = new string[8];
        for (int r = 0; r < 8; r++)
        {
            var sb = new StringBuilder(W);
            for (int c = 0; c < W; c++)
            {
                bool tunnel = c >= 12;
                sb.Append(r == 6 ? 'X'
                    : (tunnel && r <= 3) ? 'X'
                    : 'O');
            }
            rows[r] = sb.ToString();
        }
        var terrain = SimTerrain.FromAscii(string.Join("\n", rows), originTileX: 0, originTileY: 0);
        float floorTop = 6f * TS;   // corridor interior rows 4-5 => gap 22px

        foreach (bool down in new[] { false, true })
        {
            var frames = SimRunner.Run(new SimConfig
            {
                Terrain       = terrain,
                StartPosition = new Vector2(24f, floorTop - 25f),
                StartVelocity = new Vector2(100f, 0f),
                Script        = InputScript.Always(new PlayerInput { Right = true, Down = down }),
                Frames        = 600,
                Dt            = 1f / 60f,
                Gravity       = new Vector2(0f, 600f),
            });
            var last = frames[^1];
            output.WriteLine($"{(down ? "RIGHT+DOWN" : "RIGHT     ")}: final x={last.X:F1} y={last.Y:F1} state={last.State} " +
                             $"(mouth at {12 * TS}, end at {W * TS})");
            foreach (var f in frames)
                if (f.Frame % 30 == 0)
                    output.WriteLine($"    f={f.Frame,3} x={f.X,6:F1} y={f.Y,6:F1} {f.State}");
        }
    }
}
