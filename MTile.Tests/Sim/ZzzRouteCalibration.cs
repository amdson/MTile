using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;
using Takeoff = MTile.Net.JumpTable.Takeoff;

namespace MTile.Tests.Sim;

// Plans/BOT_ROUTING_PLAN.md M1: measure what the routing executor's jump gesture can
// reach. Scratch harness (Zzz), run by hand in Release; the result is baked into
// Net/Bot/Routing constants and guarded by a regression test.
//
// Setup per probe: a start platform whose right edge is at column E, a deep pit, and a
// destination platform whose top is `dy` cells above the start floor (negative = below)
// and whose left face is `dx` cells right of E (dx = 1 ⇒ no gap: a step). The player
// walks right from a run-up, presses Space when its centre is `lead` px short of the
// edge, holds it `hold` frames, keeps Right held, and succeeds if it ends standing on
// the destination top.
public class ZzzRouteCalibration(ITestOutputHelper output)
{
    private static float T => Chunk.TileSize;
    private const int Rows = 60, Cols = 60, FloorRow = 40, Edge = 20;

    private static ChunkMap Arena(int dx, int dy)
    {
        var sb = new StringBuilder();
        int destTop = FloorRow - dy;
        for (int y = 0; y < Rows; y++)
        {
            for (int x = 0; x < Cols; x++)
            {
                bool start = x < Edge && y >= FloorRow;
                bool dest  = x >= Edge + dx - 1 && y >= destTop;   // left face at column Edge + dx - 1
                bool wall  = x == 0 || x == Cols - 1 || y == Rows - 1;
                sb.Append(start || dest || wall ? 'X' : 'O');
            }
            sb.Append('\n');
        }
        return SimTerrain.FromAscii(sb.ToString());
    }

    public record Probe(int Dx, int Dy, float Lead, int Hold, bool Ok, int Frames, float Hurt = 0f, int LandCol = 0,
                        Dictionary<int, (int Top, int Bottom)> Sweep = null);

    public static Probe Run(int dx, int dy, float lead, int hold, float runUpCells = 5)
    {
        var chunks = Arena(dx, dy);
        float edgeX = Edge * T;
        var sim = new Simulation(chunks, new Vector2(edgeX - runUpCells * T, FloorRow * T - 21f));
        for (int i = 0; i < 20; i++) sim.Step(default);
        var p = sim.Player;
        int pressed = -1;
        var sweep = new Dictionary<int, (int Top, int Bottom)>();
        int destTop = FloorRow - dy;
        int destLeft = Edge + dx - 1;
        for (int f = 0; f < 240; f++)
        {
            // Brake: travel direction held until the centre passes the middle of the
            // destination's first column, then released.
            bool brake = p.Body.Position.X >= (destLeft + 0.5f) * T;
            var input = new PlayerInput { Right = !brake, MouseWorldPosition = p.Body.Position + new Vector2(40f, 0f) };
            bool walkOnly = lead < 0f;
            if (pressed < 0 && (walkOnly ? p.Body.Position.X >= edgeX : p.IsGrounded && p.Body.Position.X >= edgeX - lead)) pressed = f;
            if (pressed >= 0 && !walkOnly && f - pressed < hold) input.Space = true;
            sim.Step(input);
            if (pressed >= 0)
            {
                var bb = p.Body.Bounds;
                int c0 = (int)MathF.Floor(bb.Left / T) - (Edge - 1), c1 = (int)MathF.Floor(bb.Right / T) - (Edge - 1);
                int r0 = (int)MathF.Floor(bb.Top / T) - (FloorRow - 1), r1 = (int)MathF.Floor(bb.Bottom / T) - (FloorRow - 1);
                for (int c = Math.Max(0, c0); c <= c1; c++)
                    sweep[c] = sweep.TryGetValue(c, out var tb) ? (Math.Min(tb.Top, r0), Math.Max(tb.Bottom, r1)) : (r0, r1);
            }
            if (pressed >= 0 && f - pressed > 4 && p.IsGrounded)
            {
                // Standing where? The first solid cell under the body centre.
                int fx = (int)MathF.Floor(p.Body.Position.X / T);
                int fy = (int)MathF.Floor(p.Body.Position.Y / T);
                while (fy < Rows && chunks.GetCellState(fx, fy) != TileState.Solid) fy++;
                bool onDest = fx >= destLeft && fy == destTop;
                return new Probe(dx, dy, lead, hold, onDest, f - pressed, p.Combat.DamageTaken, fx - (Edge - 1), sweep);
            }
            if (p.Body.Position.Y > (FloorRow - dy + 4) * T) break;   // fell past the destination top
        }
        return new Probe(dx, dy, lead, hold, false, -1);
    }

    // Takeoff options: (name, runUpCells, lead px). "stand" = spawned at the edge,
    // pressed at once; the others walk from 5 cells back and press `lead` px short.
    public static readonly (string Name, float RunUp, float Lead)[] Takeoffs =
    {
        ("walk", 2f, -1f), ("stand", 0.6f, 1000f), ("edge", 5f, 0f), ("half", 5f, 5.5f), ("one", 5f, 11f), ("two", 5f, 22f),
    };

    [Fact]
    public void DeepDropTrace()
    {
        var chunks = Arena(3, -10);
        var sim = new Simulation(chunks, new Vector2(Edge * T - 5 * T, FloorRow * T - 21f));
        for (int i = 0; i < 20; i++) sim.Step(default);
        var p = sim.Player;
        for (int f = 0; f < 140; f++)
        {
            sim.Step(new PlayerInput { Right = true, Space = f >= 30 && f < 60 });
            if (f % 4 == 0) output.WriteLine($"f{f} {p.CurrentStateName} {p.CurrentActionName} pos {p.Body.Position} gnd {p.IsGrounded} hp {p.Health:F2} bottom {p.Body.Bounds.Bottom:F1} destTop {(FloorRow + 10) * T}");
        }
    }

    // A takeoff is robust for a cell if it still lands with the start nudged: lead ±2 px
    // and a shorter run-up (3 cells instead of 5; stand: 0.6 / 0.9 cells from the edge).
    public static bool Robust(int dx, int dy, int k, out int frames, out float hurt)
        => Robust(dx, dy, k, out frames, out hurt, out _, out _, out _);

    public static bool Robust(int dx, int dy, int k, out int frames, out float hurt,
                              out int landMin, out int landMax, out Dictionary<int, (int Top, int Bottom)> sweep)
    {
        landMin = int.MaxValue; landMax = int.MinValue; sweep = new();
        var t = Takeoffs[k];
        var variants = t.Name == "stand" ? new[] { (t.RunUp, t.Lead), (0.9f, t.Lead) }
                     : t.Name == "walk"  ? new[] { (t.RunUp, t.Lead), (0.7f, t.Lead), (5f, t.Lead) }
                     : new[] { (t.RunUp, t.Lead), (t.RunUp, t.Lead - 2f), (t.RunUp, t.Lead + 2f), (3f, t.Lead) };
        frames = 0; hurt = 0f;
        foreach (var (ru, lead) in variants)
        {
            var r = Run(dx, dy, lead, 30, ru);
            if (!r.Ok) return false;
            frames = Math.Max(frames, r.Frames); hurt = Math.Max(hurt, r.Hurt);
            landMin = Math.Min(landMin, r.LandCol); landMax = Math.Max(landMax, r.LandCol);
            foreach (var (c, tb) in r.Sweep)
                sweep[c] = sweep.TryGetValue(c, out var o) ? (Math.Min(o.Top, tb.Top), Math.Max(o.Bottom, tb.Bottom)) : tb;
        }
        return true;
    }

    [Fact]
    public void ReachTable()
    {
        var jobs = new List<(int, int, int)>();
        for (int dx = 1; dx <= 10; dx++)
        for (int dy = -24; dy <= 5; dy++)
        for (int k = 0; k < Takeoffs.Length; k++) jobs.Add((dx, dy, k));
        var res = new ConcurrentDictionary<(int, int, int), (bool Ok, int Frames, float Hurt)>();
        Parallel.ForEach(jobs, j => { bool ok = Robust(j.Item1, j.Item2, j.Item3, out int fr, out float h); res[j] = (ok, fr, h); });

        var sb = new StringBuilder("ROBUST takeoffs (s=stand e=edge h=half o=one t=two); [n] = max HP lost\n       ");
        for (int dx = 1; dx <= 10; dx++) sb.Append($"{dx,8}");
        sb.AppendLine();
        var bake = new StringBuilder();
        for (int dy = 5; dy >= -24; dy--)
        {
            sb.Append($"dy{dy,3}  ");
            bake.Append($"        /* dy{dy,3} */ \"");
            for (int dx = 1; dx <= 10; dx++)
            {
                string c = ""; float hurt = 0f; int best = -1, bestFr = int.MaxValue;
                for (int k = 0; k < Takeoffs.Length; k++)
                {
                    var r = res[(dx, dy, k)];
                    if (!r.Ok) continue;
                    c += "wsehot"[k]; hurt = Math.Max(hurt, r.Hurt);
                    if (r.Frames < bestFr) { bestFr = r.Frames; best = k; }
                }
                sb.Append($"{(c == "" ? "." : c + (hurt > 0f ? $"[{hurt:F1}]" : "")),8}");
                bake.Append(best < 0 ? '.' : "wsehot"[best]);
            }
            sb.AppendLine();
            bake.AppendLine("\",");
        }
        output.WriteLine(sb.ToString());
        output.WriteLine("BAKE (fastest robust takeoff per cell, dx 1..10):\n" + bake);
    }

    // Writes Net/Bot/Routing/JumpTable.g.cs: per (dx, dy) the preferred robust takeoff —
    // Walk (no jump at all) if robust, else Stand (one uniform gesture), else the fastest.
    [Fact]
    public void GenerateJumpTable()
    {
        var jobs = new List<(int, int)>();
        for (int dx = 1; dx <= 10; dx++) for (int dy = -19; dy <= 5; dy++) jobs.Add((dx, dy));
        var lines = new ConcurrentDictionary<(int, int), string>();
        Parallel.ForEach(jobs, j =>
        {
            var (dx, dy) = j;
            string best = null; int bestFr = int.MaxValue;
            for (int k = 0; k < Takeoffs.Length; k++)
            {
                if (!Robust(dx, dy, k, out int fr, out float hurt, out int lmin, out int lmax, out var sw)) continue;
                int cols = sw.Keys.Max() + 1;
                var sweep = string.Join(",", Enumerable.Range(0, cols).Select(c =>
                    sw.TryGetValue(c, out var tb) ? $"{tb.Top},{tb.Bottom}" : "0,-1"));
                string line = $"        new({dx}, {dy}, Takeoff.{(Takeoff)k}, {fr}, {lmin}, {lmax}, {hurt:0.00}f, new sbyte[] {{ {sweep} }}),";
                if (k <= 1) { best = line; break; }        // Walk, else Stand, when robust: take it
                if (fr < bestFr) { bestFr = fr; best = line; }
            }
            if (best != null) lines[j] = best;
        });
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated> by MTile.Tests.Sim.ZzzRouteCalibration.GenerateJumpTable — do not hand-edit.");
        sb.AppendLine("// Re-run it after any movement retune, then BotManeuverRouteJumpTableTests.");
        sb.AppendLine("namespace MTile.Net;");
        sb.AppendLine();
        sb.AppendLine("public static partial class JumpTable");
        sb.AppendLine("{");
        sb.AppendLine("    private static readonly Entry[] Entries =");
        sb.AppendLine("    {");
        foreach (var k in lines.Keys.OrderBy(k => k.Item1).ThenByDescending(k => k.Item2)) sb.AppendLine(lines[k]);
        sb.AppendLine("    };");
        sb.AppendLine("}");
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "Plans"))) dir = dir.Parent;
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir!.FullName, "Net", "Bot", "Routing", "JumpTable.g.cs"), sb.ToString());
        output.WriteLine($"{lines.Count} entries");
    }
}

// M3 calibration: how tall a face WallClimbManeuver's controller gets up, for a wide
// cliff and for 1- and 2-wide pillars (the camper's stacked column).
public class ZzzRouteWallClimbCalibration(ITestOutputHelper output)
{
    private static float T => Chunk.TileSize;

    private static (ChunkMap, Vector2 top, Vector2 start) Map(int h, int width)
    {
        const int Rows = 40, Cols = 50, Floor = 34, Face = 25;
        var sb = new StringBuilder();
        for (int y = 0; y < Rows; y++)
        {
            for (int x = 0; x < Cols; x++)
            {
                bool floor = y >= Floor, wall = x == 0 || x == Cols - 1 || y == 0;
                bool block = x >= Face && x < Face + width && y >= Floor - h;
                sb.Append(floor || wall || block ? 'X' : 'O');
            }
            sb.Append('\n');
        }
        var top = new Vector2((Face + width / 2f) * T, (Floor - h) * T - 21f);
        var start = new Vector2((Face - 8) * T, Floor * T - 21f);
        return (SimTerrain.FromAscii(sb.ToString()), top, start);
    }

    [Fact]
    public void FaceHeights()
    {
        var jobs = new List<(int h, int w)>();
        foreach (int w in new[] { 1, 2, 20 }) for (int h = 4; h <= 24; h += 2) jobs.Add((h, w));
        var res = new ConcurrentDictionary<(int, int), string>();
        Parallel.ForEach(jobs, j =>
        {
            var (map, top, start) = Map(j.h, j.w);
            int at = -1;
            var r = BotArena.Fight(map, top, start, 600, bot: BotArena.Only(new MTile.Net.WallClimbManeuver()), p1: (f, sim) =>
            {
                var p2 = sim.SecondaryPlayers[0].Player;
                if (at < 0 && p2.IsGrounded && p2.Body.Position.Y < top.Y + T) at = f;
                return default;
            });
            res[j] = at >= 0 ? at.ToString() : ".";
        });
        foreach (int w in new[] { 1, 2, 20 })
            output.WriteLine($"width {w,2}: " + string.Join(" ", Enumerable.Range(0, 11).Select(i => $"h{4 + 2 * i}={res[(4 + 2 * i, w)]}")));
    }
}
