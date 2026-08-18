using System;
using System.Diagnostics;
using Microsoft.Xna.Framework;

namespace MTile;

// A self-contained microbenchmark for the corrector QP that runs on BOTH runtimes.
//
// MTile.Bench is a desktop console app and cannot answer "how slow is this in the browser".
// This file lives at the repo root, so it compiles into MTile.Core (DesktopGL) and into
// MTile.Web (KNI) alike, and the same code times the same solve on both — which is the only
// way the wasm penalty is a number rather than a guess.
//
// Method: run a headless corridor sim until a real FOLD subproblem has been captured, then
// replay that one frozen problem in a tight loop under a SINGLE clock pair. Per-call timing
// is not an option in the browser — wasm's Stopwatch resolution is coarse (Spectre-era clock
// clamping), so bracketing a ~100 µs call individually measures the clock, not the solve.
// Re-solving is safe to repeat: Solve cold-starts z at zero, so every replay is identical
// work with an identical result and nothing accumulates.
public static class QpBench
{
    // Set by the web host when the page URL carries ?qpbench=1; Game1 runs the bench once,
    // a few frames in, and prints to the console. The in-game F8 hotkey does the same thing
    // for a human at a keyboard — this exists because Chrome swallows the function keys, so
    // a headless driver has no way to press F8.
    public static bool RunOnStartup;
    private static bool _ranOnStartup;

    // Called every frame by Game1; does nothing unless armed. One-shot.
    public static bool PollStartup(out string report)
    {
        report = null;
        if (!RunOnStartup || _ranOnStartup) return false;
        _ranOnStartup = true;
        report = Run();
        return true;
    }
    // Aim each timed batch at roughly this long — long enough to swamp a coarse clock,
    // short enough not to hang a browser tab.
    private const double TargetMs = 400.0;
    private const int    Reps     = 3;      // report the MINIMUM: noise only ever adds time
    public static string Run()
    {
        // The problem is the frozen one embedded by --dump-problem, NOT a fresh capture:
        // see the codec note below for why capturing per host would compare two different
        // problems.
        var problem = Decode(QpProblem.Corridor);


        int H = problem.H, C = problem.ChannelCount;
        var z       = new Vector2[CorrectionSolver.MaxChannels * H];
        var zScratch= new Vector2[CorrectionSolver.MaxChannels * H];

        // Warm the JIT / wasm tier-up before anything is timed.
        for (int i = 0; i < 200; i++) CorrectionSolver.Solve(problem, z, zScratch);

        // Calibrate the batch size against the actual machine, so this costs the same
        // wall-clock on a fast desktop and a slow browser.
        var cal = Stopwatch.StartNew();
        const int CalN = 200;
        for (int i = 0; i < CalN; i++) CorrectionSolver.Solve(problem, z, zScratch);
        cal.Stop();
        double calMs = Math.Max(cal.Elapsed.TotalMilliseconds, 0.001);
        int batch = Math.Clamp((int)(CalN * TargetMs / calMs), 100, 2_000_000);

        double bestUs = double.MaxValue;
        for (int rep = 0; rep < Reps; rep++)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < batch; i++) CorrectionSolver.Solve(problem, z, zScratch);
            sw.Stop();
            bestUs = Math.Min(bestUs, sw.Elapsed.TotalMilliseconds * 1000.0 / batch);
        }

        double perSweep = bestUs / problem.InnerIterations;
        return $"qp bench: {bestUs:F2} us/solve  ({perSweep:F2} us/sweep)  " +
               $"H={H} channels={C} rows={problem.RowCount} sweeps={problem.InnerIterations}  " +
               $"batch={batch} reps={Reps} clockHz={Stopwatch.Frequency}";
    }

    // Steps a headless corridor sim until the fold corrector has run, keeping a deep copy
    // of the last fold subproblem it solved. Nothing here touches rendering or input
    // hardware, so it is identical work on both hosts.
    private static CorrectionProblem Capture()
    {
        var sim = new Simulation(Corridor(), new Vector2(24f, 74f), g => g.Player.RestrictToFallAndStand());
        var input = new PlayerInput { Right = true };
        for (int f = 0; f < 120; f++) sim.Step(input);

        CorrectionSolver.ArmCapture = true;
        CorrectionSolver.Captured = null;
        for (int f = 0; f < 400; f++) sim.Step(input);
        CorrectionSolver.ArmCapture = false;

        var p = CorrectionSolver.Captured;
        CorrectionSolver.Captured = null;
        return p;
    }


    // Regenerates Diagnostics/QpProblem.g.cs. Desktop-only (MTile.Bench --dump-problem);
    // re-run it if the fold channel stack or the clearance builder changes shape enough
    // that the embedded problem stops being representative.
    public static string DumpProblemLiteral()
    {
        var p = Capture();
        if (p == null) return null;
        var blob = Encode(p);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("// GENERATED by `dotnet run -c Release --project MTile.Bench -- --dump-problem`.");
        sb.AppendLine("// One frozen corrector subproblem, captured from the bumpy-corridor fold path.");
        sb.AppendLine("// Embedded rather than captured at run time because float determinism does not");
        sb.AppendLine("// hold across DesktopGL and wasm, so each host would otherwise time a different");
        sb.AppendLine("// problem. See Diagnostics/QpBench.cs. Do not hand-edit.");
        sb.AppendLine("namespace MTile;");
        sb.AppendLine();
        sb.AppendLine("internal static class QpProblem");
        sb.AppendLine("{");
        sb.AppendLine($"    // H={p.H} channels={p.ChannelCount} rows={p.RowCount} sweeps={p.InnerIterations}");
        sb.AppendLine("    internal static readonly float[] Corridor =");
        sb.AppendLine("    {");
        for (int i = 0; i < blob.Length; i += 8)
        {
            sb.Append("        ");
            for (int k = i; k < i + 8 && k < blob.Length; k++)
                sb.Append(blob[k].ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append("f, ");
            sb.AppendLine();
        }
        sb.AppendLine("    };");
        sb.AppendLine("}");
        return sb.ToString();
    }
    // The same tunnel MTile.Bench's CorrectorDiag uses: a floor, a low ceiling, and
    // alternating floor/ceiling teeth that keep the fold corrector firing on nearly every
    // tick. Duplicated rather than shared because MTile.Tests/Sim is not compiled into the
    // web host.
    private static ChunkMap Corridor()
    {
        const int W = 64, Rows = 7;
        var chunks = new ChunkMap();
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < W; c++)
            {
                bool tunnel = c >= 16;
                bool solid = r switch
                {
                    6 => true,
                    2 when tunnel => true,
                    3 when tunnel && c % 4 == 3 => true,
                    5 when tunnel && c % 4 == 1 => true,
                    _ => false,
                };
                if (solid) SetSolid(chunks, c, r);
            }
        return chunks;
    }

    private static void SetSolid(ChunkMap chunks, int tx, int ty)
    {
        int cx = FloorDiv(tx, Chunk.Size), cy = FloorDiv(ty, Chunk.Size);
        var pos = new Point(cx, cy);
        if (!chunks.TryGet(pos, out var chunk))
        {
            chunk = new Chunk { ChunkPos = pos };
            chunks[pos] = chunk;
        }
        chunk.Tiles[tx - cx * Chunk.Size, ty - cy * Chunk.Size].IsSolid = true;
    }

    private static int FloorDiv(int n, int d)
    {
        int q = n / d;
        if ((n ^ d) < 0 && q * d != n) q--;
        return q;
    }

    // ---- Frozen-problem codec -------------------------------------------------
    //
    // The benchmark cannot capture its own problem at run time and still compare
    // runtimes: float determinism does NOT hold across DesktopGL and wasm (the same
    // reason cross-play is refused), so a few hundred sim ticks diverge and each host
    // ends up timing a DIFFERENT subproblem — measured, not assumed: the browser
    // captured a 2-channel stack where the desktop captured 7.
    //
    // So the problem is captured once on the desktop (`MTile.Bench -- --dump-problem`)
    // and embedded as a flat float blob that both hosts decode bit-identically. Bools
    // and enums ride as floats; arrays are length-prefixed with -1 for null.
    public static float[] Encode(CorrectionProblem p)
    {
        var a = new System.Collections.Generic.List<float>();
        a.Add(p.H); a.Add(p.Dt); a.Add(p.DeltaWeight); a.Add(p.HingeWeight);
        a.Add(p.InnerIterations); a.Add(p.RowCount); a.Add(p.ChannelCount);
        for (int k = 0; k < p.H; k++) { a.Add(p.CoastVel[k].X); a.Add(p.CoastVel[k].Y); }
        for (int j = 0; j < p.RowCount; j++)
        {
            var r = p.Rows[j];
            a.Add(r.Tick); a.Add(r.Normal.X); a.Add(r.Normal.Y);
            a.Add(r.Depth); a.Add(r.HingeScale); a.Add(r.PlantOnly ? 1 : 0);
        }
        for (int c = 0; c < p.ChannelCount; c++)
        {
            var ch = p.Channels[c];
            a.Add((int)ch.Id); a.Add((int)ch.Lever); a.Add(ch.Weight); a.Add(ch.Cap);
            a.Add(ch.Redirect ? 1 : 0); a.Add(ch.ActiveFrom); a.Add(ch.ActiveTo);
            a.Add(ch.Axis.X); a.Add(ch.Axis.Y);
            a.Add(ch.AxisOnly ? 1 : 0); a.Add(ch.Unilateral ? 1 : 0);
            a.Add(ch.ForwardAxis.X); a.Add(ch.ForwardAxis.Y); a.Add(ch.ForwardCap);
            a.Add(ch.PlantServes ? 1 : 0); a.Add(ch.SlewCap);
            a.Add(ch.SkipSoftHorizontal ? 1 : 0);
            AddArray(a, ch.CapPerTick);
            AddBools(a, ch.ActiveMask);
            a.Add(p.PrevApplied[c].X); a.Add(p.PrevApplied[c].Y);
        }
        return a.ToArray();
    }

    private static void AddArray(System.Collections.Generic.List<float> a, float[] v)
    {
        if (v == null) { a.Add(-1); return; }
        a.Add(v.Length);
        foreach (float f in v) a.Add(f);
    }

    private static void AddBools(System.Collections.Generic.List<float> a, bool[] v)
    {
        if (v == null) { a.Add(-1); return; }
        a.Add(v.Length);
        foreach (bool b in v) a.Add(b ? 1 : 0);
    }

    private static CorrectionProblem Decode(float[] a)
    {
        int i = 0;
        var p = new CorrectionProblem
        {
            H = (int)a[i++], Dt = a[i++], DeltaWeight = a[i++], HingeWeight = a[i++],
            InnerIterations = (int)a[i++],
        };
        p.RowCount = (int)a[i++];
        p.ChannelCount = (int)a[i++];
        p.CoastVel = new Vector2[p.H];
        for (int k = 0; k < p.H; k++) p.CoastVel[k] = new Vector2(a[i++], a[i++]);
        p.Rows = new ClearanceRow[Math.Max(p.RowCount, 1)];
        for (int j = 0; j < p.RowCount; j++)
            p.Rows[j] = new ClearanceRow
            {
                Tick = (int)a[i++],
                Normal = new Vector2(a[i++], a[i++]),
                Depth = a[i++], HingeScale = a[i++], PlantOnly = a[i++] != 0f,
            };
        p.Channels = new ChannelDef[Math.Max(p.ChannelCount, 1)];
        p.PrevApplied = new Vector2[Math.Max(p.ChannelCount, 1)];
        for (int c = 0; c < p.ChannelCount; c++)
        {
            var ch = new ChannelDef
            {
                Id = (CorrectionChannel)(int)a[i++], Lever = (LeverKind)(int)a[i++],
                Weight = a[i++], Cap = a[i++], Redirect = a[i++] != 0f,
                ActiveFrom = (int)a[i++], ActiveTo = (int)a[i++],
                Axis = new Vector2(a[i++], a[i++]),
                AxisOnly = a[i++] != 0f, Unilateral = a[i++] != 0f,
                ForwardAxis = new Vector2(a[i++], a[i++]), ForwardCap = a[i++],
                PlantServes = a[i++] != 0f, SlewCap = a[i++],
                SkipSoftHorizontal = a[i++] != 0f,
            };
            int n = (int)a[i++];
            if (n >= 0) { ch.CapPerTick = new float[n]; for (int k = 0; k < n; k++) ch.CapPerTick[k] = a[i++]; }
            n = (int)a[i++];
            if (n >= 0) { ch.ActiveMask = new bool[n]; for (int k = 0; k < n; k++) ch.ActiveMask[k] = a[i++] != 0f; }
            p.Channels[c] = ch;
            p.PrevApplied[c] = new Vector2(a[i++], a[i++]);
        }
        // Attribution stays ON: the shipped solve always sets RowPush (it feeds the force
        // ledger, not just the overlay), so switching it off would time a cheaper loop.
        p.RowPush = new Vector2[p.Rows.Length];
        return p;
    }
}
