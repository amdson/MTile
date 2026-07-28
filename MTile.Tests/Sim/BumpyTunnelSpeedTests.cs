using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// REPRO (corrector branch): hold Right through the bumpy corridor
// (Levels/course.txt layout) and read the trace. Diagnostic — no asserts.
public class BumpyTunnelSpeedTests(ITestOutputHelper output)
{
    [Fact]
    public void HoldRight_BumpyCorridor_Trace()
    {
        // Ceiling row 10, stalactites row 11, floor pillars row 13, floor row 14
        // (top y=224, pit at cols 10-11) — the start-stage corridor's twin.
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXX
            OOXOOOXOOOXOOOOX
            OOOOOOOOOOOOOOOO
            XOOOXOOOXOOOXXOO
            XXXXXXXXXXOOXXXX
            XXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        var frames = SimRunner.Run(new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(24f, 200f),
            StartVelocity = new Vector2(200f, 0f),
            Script        = InputScript.Always(new PlayerInput { Right = true }),
            Frames        = 300,
            Dt            = 1f / 60f,
            Gravity       = new Vector2(0f, 600f),
        });

        output.WriteLine("  frame     t      x      y     vx     vy  state");
        foreach (var f in frames)
            if (f.Frame % 5 == 0 || f.Transition)
                output.WriteLine($"  {f.Frame,5} {f.T,5:F2} {f.X,6:F1} {f.Y,6:F1} {f.Vx,6:F1} {f.Vy,6:F1}  {f.State}{(f.Transition ? "  <- enter" : "")}");

        var last = frames[^1];
        float avgVx = (last.X - frames[0].X) / (last.T - frames[0].T);
        output.WriteLine($"\n  avg forward speed: {avgVx:F1} px/s (flat-ground run ≈ 150), final x={last.X:F1}");
    }

    // Long-tunnel stress test: 60 columns, 3-tile-high interior, alternating
    // offset bumps — bottom bumps at col ≡ 1 (mod 4), top bumps at col ≡ 3
    // (mod 4). Every crossing is a vault-then-duck chain; 60 of them back to
    // back is the endurance version of the corridor above.
    [Fact]
    public void HoldRight_LongBumpyTunnel_Trace()
    {
        const int W = 60;
        var bottomBumps = new StringBuilder();
        var topBumps    = new StringBuilder();
        for (int c = 0; c < W; c++)
        {
            bottomBumps.Append(c % 4 == 1 ? 'X' : 'O');
            topBumps.Append(c % 4 == 3 ? 'X' : 'O');
        }
        string solid = new string('X', W);
        string open  = new string('O', W);
        // Rows: ceiling y=0..16, top bumps 16..32, air 32..48, bottom bumps
        // 48..64, floor 64..80. Floor-to-ceiling 48px; 32px over/under a bump.
        var terrain = SimTerrain.FromAscii(
            $"{solid}\n{topBumps}\n{open}\n{bottomBumps}\n{solid}",
            originTileX: 0, originTileY: 0);

        var frames = SimRunner.Run(new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(8f, 41.6f),   // standing rest on floor top y=64, col 0
            StartVelocity = new Vector2(150f, 0f),
            Script        = InputScript.Always(new PlayerInput { Right = true }),
            Frames        = 600,                       // 10s; 960px at 150 px/s ≈ 6.4s
            Dt            = 1f / 60f,
            Gravity       = new Vector2(0f, 600f),
        });

        output.WriteLine("  frame     t      x      y     vx     vy  state");
        foreach (var f in frames)
            if (f.Frame % 5 == 0 || f.Transition)
                output.WriteLine($"  {f.Frame,5} {f.T,5:F2} {f.X,6:F1} {f.Y,6:F1} {f.Vx,6:F1} {f.Vy,6:F1}  {f.State}{(f.Transition ? "  <- enter" : "")}");

        var last = frames[^1];
        float avgVx = (last.X - frames[0].X) / (last.T - frames[0].T);
        output.WriteLine($"\n  avg forward speed: {avgVx:F1} px/s (flat-ground run ≈ 150), final x={last.X:F1} of {W * 16}");
    }

    // Headless twin of the "corridor" STAGE: fall/stand only (the stage's own
    // Populate restriction), flat runway feeding the offset-bump tunnel — the
    // stand-fold experiment's acceptance trace. Standing has no spring/FSD here;
    // hover, bump crests, ceiling ducks, and landings are all the ambient
    // solve's applied corrections.
    [Fact]
    public void CorridorStage_FallStandOnly_HoldRight_Trace()
    {
        const int W = 64;   // 16-col flat runway + 48-col tunnel
        var rows = new string[7];
        var b = new char[7, W];
        for (int r = 0; r < 7; r++)
        for (int c = 0; c < W; c++)
        {
            bool tunnel = c >= 16;
            b[r, c] = r switch
            {
                6 => 'X',                                        // floor
                2 when tunnel => 'X',                            // ceiling
                3 when tunnel && c % 4 == 3 => 'X',              // top bumps
                5 when tunnel && c % 4 == 1 => 'X',              // bottom bumps
                _ => 'O',
            };
        }
        for (int r = 0; r < 7; r++)
        {
            var sb = new StringBuilder(W);
            for (int c = 0; c < W; c++) sb.Append(b[r, c]);
            rows[r] = sb.ToString();
        }
        var terrain = SimTerrain.FromAscii(string.Join("\n", rows), originTileX: 0, originTileY: 0);

        var sim = new Simulation(terrain, new Vector2(24f, 74f),
                                 Stages.Get("corridor").Populate);
        sim.Player.CorrectorDebug.CaptureTrajectories = true;   // contact-push introspection
        var input = new PlayerInput { Right = true };

        output.WriteLine("  frame     t      x      y     vx     vy  state");
        string lastState = "";
        var trace = new List<(int f, float x)>();
        float maxDv = 0f; int maxDvFrame = -1;
        for (int f = 0; f < 400; f++)
        {
            sim.Step(input);
            var body = sim.Player.Body;
            string state = sim.Player.CurrentStateName;
            bool transition = state != lastState;
            lastState = state;
            if (f % 10 == 0 || transition)
                output.WriteLine($"  {f,5} {f / 60f,5:F2} {body.Position.X,6:F1} {body.Position.Y,6:F1} " +
                                 $"{body.Velocity.X,6:F1} {body.Velocity.Y,6:F1}  {state}{(transition ? "  <- enter" : "")}");
            // Applied correction magnitude: z₀ is a per-tick δv; force = z₀/dt.
            var z0 = sim.Player.CorrectorDebug.Z[0];
            if (z0.Length() > maxDv) { maxDv = z0.Length(); maxDvFrame = f; }
            if (f % 10 == 0 || transition)
                output.WriteLine($"        z0=({z0.X,7:F1},{z0.Y,7:F1}) px/s  as force=({z0.X * 60f,9:F0},{z0.Y * 60f,9:F0}) px/s²");
            trace.Add((f, body.Position.X));
        }
        float avgVx = (trace[^1].x - 24f) / (400 / 60f);
        output.WriteLine($"\n  avg forward speed: {avgVx:F1} px/s, final x={trace[^1].x:F1} (tunnel mouth at 256, end at {W * 16})");
        output.WriteLine($"  peak |z0| = {maxDv:F1} px/s (as force {maxDv * 60f:F0} px/s², gravity=600) at frame {maxDvFrame}");
    }

    // Headless twin of the GYM stage under a channel scenario: repeating 1-high
    // ledge (up col 6, down col 12, per 16 tiles), fall/stand only. Diagnostic.
    [Fact]
    public void GymStage_RedirectTraction_HoldRight_Trace()
    {
        var rows = new string[9];
        for (int r = 0; r < 9; r++)
        {
            var sb = new StringBuilder(64);
            for (int c = 0; c < 64; c++)
            {
                int cc = c % 16;
                sb.Append(r == 8 || (r == 7 && cc >= 6 && cc <= 11) ? 'X' : 'O');
            }
            rows[r] = sb.ToString();
        }
        var terrain = SimTerrain.FromAscii(string.Join("\n", rows), originTileX: 0, originTileY: 0);

        var cfg = MovementConfig.Current;
        string saved = cfg.CorrectorScenario;
        try
        {
            cfg.CorrectorScenario = "redirect-traction";
            var sim = new Simulation(terrain, new Vector2(8f, 100f),
                                     Stages.Get("gym").Populate);
            var input = new PlayerInput { Right = true };

            output.WriteLine("  frame     t      x      y     vx     vy  state   (floor hover=106, ledge-top hover=90)");
            string lastState = "";
            for (int f = 0; f < 500; f++)
            {
                sim.Step(input);
                var b = sim.Player.Body;
                string state = sim.Player.CurrentStateName;
                bool tr = state != lastState;
                lastState = state;
                if (f % 10 == 0 || tr)
                    output.WriteLine($"  {f,5} {f / 60f,5:F2} {b.Position.X,6:F1} {b.Position.Y,6:F1} " +
                                     $"{b.Velocity.X,6:F1} {b.Velocity.Y,6:F1}  {state}{(tr ? "  <- enter" : "")}");
            }
            var last = sim.Player.Body.Position;
            output.WriteLine($"\n  final x={last.X:F1} (first ledge face at 96; each period is 256px)");
        }
        finally { cfg.CorrectorScenario = saved; }
    }
}
