using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests.Sim;

// TEMP DIAGNOSTIC: input -> motion step responses for the movement system. Delete me.
public class ZzzMovementResponse
{
    private static readonly string Out =
        Environment.GetEnvironmentVariable("MTILE_RESP_OUT") ?? Path.Combine(Path.GetTempPath(), "mtile_resp.txt");

    private static readonly PlayerInput None = default;
    private static readonly PlayerInput R = new() { Right = true };
    private static readonly PlayerInput L = new() { Left = true };
    private static readonly PlayerInput J = new() { Space = true };
    private static readonly PlayerInput RJ = new() { Right = true, Space = true };

    private const int Settle = 40;
    private const float T = Chunk.TileSize;
    private const float FloorTop = 6 * T;
    private const float FX = 20 * T;   // feature x

    private static ChunkMap Flat() => SimTerrain.FromAscii(new string('X', 80), 0, 6);

    private static ChunkMap Rows(params string[] rows) => SimTerrain.FromAscii(string.Join("\n", rows), 0, 0);

    private static SimFrame[] Run(ChunkMap t, float x, InputScript s, int frames, float y = FloorTop - 20.5f)
        => SimRunner.Run(new SimConfig { Terrain = t, StartPosition = new Vector2(x, y), Script = s, Frames = frames });

    private static void Dump(StringBuilder sb, string title, SimFrame[] fr, int from, int to, int every = 1)
    {
        sb.AppendLine($"## {title}");
        for (int i = from; i < Math.Min(to, fr.Length); i += every)
        {
            var f = fr[i];
            sb.AppendLine($"  f{i - Settle,4} x={f.X,8:F2} y={f.Y,7:F2} vx={f.Vx,7:F1} vy={f.Vy,7:F1} fx={f.Fx,8:F0} fy={f.Fy,8:F0} {f.State}");
        }
    }

    [Fact]
    public void Trace()
    {
        var sb = new StringBuilder();

        // A. Flat: hold right 40, release.
        {
            var fr = Run(Flat(), 200f, new InputScript().For(Settle, None).For(40, R).Forever(None), Settle + 80);
            Dump(sb, "A flat: R x40 then release", fr, Settle - 2, Settle + 12);
            Dump(sb, "A (release)", fr, Settle + 38, Settle + 52);
            sb.AppendLine($"  rest y={fr[Settle - 1].Y:F2}  y range while walking: {fr.Skip(Settle + 5).Take(30).Min(f => f.Y):F2}..{fr.Skip(Settle + 5).Take(30).Max(f => f.Y):F2}");
        }

        // B. Taps: displacement per tap length.
        sb.AppendLine("## B tap right N frames: total displacement (px), from several sub-tile starts");
        foreach (int n in new[] { 1, 2, 3, 4, 6, 10 })
        {
            sb.Append($"  tap {n,2}f:");
            foreach (float x0 in new[] { 200f, 203f, 207f, 211f, 214f })
            {
                var fr = Run(Flat(), x0, new InputScript().For(Settle, None).For(n, R).Forever(None), Settle + 60);
                sb.Append($" {fr[^1].X - fr[Settle - 1].X,6:F2}");
            }
            sb.AppendLine($"   (ideal {n * 1f:F0}px at 60px/s)");
        }

        // C. Reversal.
        {
            var fr = Run(Flat(), 200f, new InputScript().For(Settle, None).For(30, R).Forever(L), Settle + 50);
            Dump(sb, "C flat: R x30 then L", fr, Settle + 28, Settle + 40);
        }

        // D. Walk over features: vx / y consistency.
        (string name, string[] rows)[] maps =
        {
            ("D1 one-high step up", new[] {
                "........................................",
                "........................................",
                "........................................",
                "........................................",
                "........................................",
                "....................XXXXXXXXXXXXXXXXXXXX",
                "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX" }),
            ("D2 one-high bump (1 wide)", new[] {
                "........................................",
                "........................................",
                "........................................",
                "........................................",
                "........................................",
                "....................X...................",
                "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX" }),
            ("D3 one-wide pit", new[] {
                "........................................",
                "........................................",
                "........................................",
                "........................................",
                "........................................",
                "........................................",
                "XXXXXXXXXXXXXXXXXXXX.XXXXXXXXXXXXXXXXXXX",
                "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX" }),
            ("D4 two-high wall", new[] {
                "........................................",
                "........................................",
                "........................................",
                "........................................",
                "....................XXXXXXXXXXXXXXXXXXXX",
                "....................XXXXXXXXXXXXXXXXXXXX",
                "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX" }),
            ("D5 step down (ledge)", new[] {
                "........................................",
                "........................................",
                "........................................",
                "........................................",
                "........................................",
                "XXXXXXXXXXXXXXXXXXXX....................",
                "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX" }),
        };
        foreach (var (name, rows) in maps)
        {
            bool raised = name.StartsWith("D5");
            float y = (raised ? 5 : 6) * T - 20.5f;
            var fr = Run(Rows(rows), FX - 70f, new InputScript().For(Settle, None).Forever(R), Settle + 150, y);
            var walk = fr.Skip(Settle + 5).ToArray();
            sb.AppendLine($"## {name}: hold R from x={FX - 70} (feature at x={FX})");
            sb.AppendLine($"  vx min/max {walk.Min(f => f.Vx):F1}/{walk.Max(f => f.Vx):F1}  vy min/max {walk.Min(f => f.Vy):F1}/{walk.Max(f => f.Vy):F1}  final x={fr[^1].X:F1} y={fr[^1].Y:F1}");
            sb.AppendLine("  states: " + string.Join(" > ", fr.Where(f => f.Transition).Select(f => $"{f.State}@{f.Frame - Settle}(x{f.X:F0})")));
            // frames to cover each 16px column from 272..400
            sb.Append("  frames per tile column from FX-4T: ");
            for (float cx = FX - 4 * T; cx < FX + 5 * T; cx += T)
            {
                int a = Array.FindIndex(fr, f => f.X >= cx), b = Array.FindIndex(fr, f => f.X >= cx + T);
                sb.Append(a < 0 || b < 0 ? " --" : $" {b - a}");
            }
            sb.AppendLine("  (flat = 11)");
            Dump(sb, name + " detail", fr, Settle + 40, Settle + 110, 1);
        }

        // D6. Repeatability at a step: same walk from 8 sub-tile starts, release after K frames, where do we stop?
        sb.AppendLine("## D6 step-up repeatability: walk R for 70f from x0, release; final x - x0 (flat ideal ~70+stop)");
        foreach (float x0 in new[] { FX - 70f, FX - 68.5f, FX - 67f, FX - 65.5f, FX - 64f, FX - 62.5f, FX - 61f, FX - 59.5f })
        {
            var fr = Run(Rows(maps[0].rows), x0, new InputScript().For(Settle, None).For(70, R).Forever(None), Settle + 130);
            var ff = Run(Flat(), x0, new InputScript().For(Settle, None).For(70, R).Forever(None), Settle + 130);
            sb.AppendLine($"  x0={x0}: step {fr[^1].X - x0,6:F2}   flat {ff[^1].X - x0,6:F2}");
        }

        // F. Jumps: neutral, hold lengths.
        sb.AppendLine("## F neutral jump by hold length: latency (frames to |dy|>0.5), apex rise px, apex frame, landing frame");
        foreach (int n in new[] { 1, 2, 4, 6, 8, 12, 20, 40 })
        {
            var fr = Run(Flat(), 200f, new InputScript().For(Settle, None).For(n, J).Forever(None), Settle + 120);
            float y0 = fr[Settle - 1].Y;
            int lat = Array.FindIndex(fr, Settle, f => y0 - f.Y > 0.5f) - Settle;
            float apex = fr.Skip(Settle).Min(f => f.Y);
            int apexF = Array.FindIndex(fr, Settle, f => f.Y == apex) - Settle;
            int land = Array.FindIndex(fr, Settle + Math.Max(apexF, 1), f => f.Y >= y0 - 0.75f) - Settle;
            sb.AppendLine($"  hold {n,2}f: latency {lat}f  rise {y0 - apex,6:F2}px  apex@{apexF}f  land@{land}f  states: " +
                string.Join(">", fr.Skip(Settle).Where(f => f.Transition).Select(f => $"{f.State.Replace("State", "")}@{f.Frame - Settle}")));
        }
        {
            var fr = Run(Flat(), 200f, new InputScript().For(Settle, None).For(40, J).Forever(None), Settle + 70);
            Dump(sb, "F detail: neutral jump, hold 40f", fr, Settle - 1, Settle + 64, 2);
        }

        // G. Running jump, hold R throughout vs release R at apex.
        {
            var s1 = new InputScript().For(Settle, None).For(30, R).For(30, RJ).Forever(R);
            var fr = Run(Flat(), 200f, s1, Settle + 130);
            float y0 = fr[Settle + 29].Y; float xj = fr[Settle + 29].X;
            int land = Array.FindIndex(fr, Settle + 45, f => f.Y >= y0 - 0.75f);
            sb.AppendLine($"## G running jump (R held): rise {y0 - fr.Skip(Settle + 30).Min(f => f.Y):F2}px, lands {fr[land].X - xj:F1}px downrange after {land - Settle - 30}f; vx at takeoff {fr[Settle + 29].Vx:F1}, mid-air min/max {fr.Skip(Settle + 31).Take(land - Settle - 31).Min(f => f.Vx):F1}/{fr.Skip(Settle + 31).Take(land - Settle - 31).Max(f => f.Vx):F1}");
            sb.AppendLine("  states: " + string.Join(">", fr.Skip(Settle).Where(f => f.Transition).Select(f => $"{f.State.Replace("State", "")}@{f.Frame - Settle}")));
            Dump(sb, "G detail", fr, Settle + 28, land + 8, 2);

            var s2 = new InputScript().For(Settle, None).For(30, R).For(15, RJ).Forever(None);
            var f2 = Run(Flat(), 200f, s2, Settle + 130);
            int land2 = Array.FindIndex(f2, Settle + 45, f => f.Y >= y0 - 0.75f);
            sb.AppendLine($"## G2 running jump, all input released at +15f: lands {f2[land2].X - xj:F1}px downrange, x drift after release {f2[^1].X - f2[Settle + 45].X:F1}px");
            Dump(sb, "G2 detail (release)", f2, Settle + 43, Settle + 62);
        }

        // H. Air steering from a neutral jump: press R at +15f.
        {
            var s = new InputScript().For(Settle, None).For(15, J).For(10, new PlayerInput { Space = true, Right = true }).Forever(L);
            var fr = Run(Flat(), 200f, s, Settle + 60);
            Dump(sb, "H air steer: neutral jump, R at +15f, L at +25f", fr, Settle + 13, Settle + 40);
        }

        // I. Landing: how long until grounded control returns / y settles after a fall of 3 tiles.
        {
            var fr = Run(Flat(), 200f, InputScript.Always(None), 120, FloorTop - 20.5f - 48f);
            float yr = fr[^1].Y;
            int touch = Array.FindIndex(fr, f => f.State.Contains("Standing"));
            int settled = Array.FindLastIndex(fr, f => MathF.Abs(f.Y - yr) > 0.5f) + 1;
            sb.AppendLine($"## I drop 48px onto flat: Standing@{touch}f, y within 0.5px of rest from f{settled}; max overshoot below rest {fr.Max(f => f.Y) - yr:F2}px, rebound above rest {yr - fr.Skip(touch).Min(f => f.Y):F2}px");
            Dump(sb, "I detail", fr, Math.Max(0, touch - 3), touch + 24);
        }

        File.WriteAllText(Out, sb.ToString());
    }
}
