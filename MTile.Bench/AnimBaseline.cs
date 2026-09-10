using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Xna.Framework;
using MTile;
using MTile.Tests.Sim;

namespace MTile.Bench;

// The reproducible animation baseline (Plans/ANIMATION_RUNTIME_SIMPLIFICATION_PLAN.md §1):
//
//   dotnet run -c Release --project MTile.Bench -- --anim-baseline [--save <path>] [--compare <path>]
//
// Scripted locomotion scenarios × {biped, biped_rabbit}, driven through the game's exact
// render-side call path (sim.Step → TerrainSurfaces.Extract → CharacterAnimSample.From with
// the LatticePathSampler predictor, as CosmeticUpdateSystem does → anim.Update), recording
// per-frame animator cost and motion quality over each scenario's measurement window.
//
// PERF is animator-only (Stopwatch around anim.Update, nothing else), per-frame min over
// Reps runs (the sim + animator are deterministic, so each rep replays the same frames and
// min strips scheduler noise per frame). Solver counters are per LM-solve frame. seed_evals
// is DERIVED, not counted: SolvePhaseStepLm's coarse Δφ search costs 11 residual evaluations
// (the warm-start candidate + 10 grid seeds) per cadence solve, outside Minimize's counters.
//
// QUALITY is measured on the RENDERED pose, placed with AttackGlowSystem.RigRoot exactly as
// Game1 draws it — so tgt_err (rendered contact tip vs its solve target) exposes any
// disagreement between where the solver placed the rig and where it is drawn.
internal static class AnimBaseline
{
    private const int Reps = 3;
    private const float Dt = Simulation.FixedDt;
    private static float Scale => Game1.SkeletonScale;
    private const int TS = Chunk.TileSize;

    // NoteFrame ≥ 0: report the cadence phase at that frame (e.g. the stop's release frame).
    private sealed record Scenario(string Name, Func<ChunkMap> Terrain, Vector2 Spawn,
                                   Func<int, PlayerCharacter, PlayerInput> Input,
                                   int Frames, int MeasureFrom, int NoteFrame = -1);

    // ---- metric table layout (order = file column order) ----
    private static readonly string[] Cols =
    {
        "upd_p50_us", "upd_p95_us", "upd_p99_us", "upd_max_us", "alloc_B_mean", "alloc_B_max",
        "solve_frac", "cad_frac", "iters", "res_evals", "jac_evals", "rejected", "seed_evals",
        "rows_mean", "rows_max", "vars",
        "rate_mean", "rate_max", "rate_jump_max",
        "slip_mean", "slip_max", "tgt_err_mean", "tgt_err_max", "pen_max",
        "dx_max", "dy_max", "dth_max", "foot_acc_p99", "foot_acc_max",
    };

    public static int Run(string[] args)
    {
        string save = ArgValue(args, "--save");
        string compare = ArgValue(args, "--compare");
        SimTrace.Enabled = false;
        // The game's tuning, not the code defaults (Game1 loads this at boot; nothing else in the
        // bench does, and the two differ by 2–5× on the contact/prior tiers).
        AnimSolverConfig.Load(Path.Combine(RepoRoot(), "configs", "anim_solver_config.json"));

        var header = Provenance();
        var rows = new List<(string Rig, string Scen, Dictionary<string, double> M, string Notes)>();

        foreach (string rig in new[] { "biped", "biped_rabbit" })
        {
            string clipDir = Path.Combine(RepoRoot(), "SkeletonStates", rig);
            var clips = AnimationStore.LoadAll(clipDir);
            var skel = SkeletonExamples.Load(rig);
            foreach (var sc in Scenarios())
            {
                var (m, notes) = Measure(sc, skel, clips);
                rows.Add((rig, sc.Name, m, notes));
            }
        }

        var text = Format(header, rows);
        Console.WriteLine(text);
        if (save != null) { File.WriteAllText(save, text); Console.WriteLine($"saved {save}"); }
        if (compare != null) Compare(compare, rows);
        return 0;
    }

    // ------------------------------------------------------------------ scenarios

    private static PlayerInput R => new() { Right = true };
    private static PlayerInput None => default;
    // Pulse-width input: Right held for `on` of every `period` frames. The only way to reach
    // walk speed with digital input (MaxWalkSpeed 100 px/s is above the 40 px/s Run clip
    // threshold, so a held key always plays Run) — and it does NOT hold the walk band: the
    // best pattern measured (2/4) spends ~24% of frames in 12–40 px/s. 2/5 (mean ≈ 34 px/s)
    // is used; the clip mix in the notes shows what it actually exercises.
    private static PlayerInput Pwm(int f, int on, int period) => new() { Right = f % period < on };

    private static IEnumerable<Scenario> Scenarios()
    {
        var flatSpawn = new Vector2(3.5f * TS, 10 * TS - PlayerCharacter.Radius);
        yield return new("steady run", () => Flat(), flatSpawn, (f, p) => R, 420, 180);
        yield return new("walk-speed (pwm 2/5)", () => Flat(), flatSpawn, (f, p) => Pwm(f, 2, 5), 420, 120);
        yield return new("accel from rest", () => Flat(), flatSpawn, (f, p) => f < 60 ? None : R, 180, 60);
        yield return new("gradual decel", () => Flat(), flatSpawn,
            (f, p) => f < 240 ? R : f < 360 ? Pwm(f, (int)MathF.Round(8f * (1f - (f - 240) / 120f)), 8) : None,
            480, 240);
        foreach (int off in new[] { 0, 7, 14 })
            yield return new($"abrupt stop +{off}", () => Flat(), flatSpawn,
                (f, p) => f < 240 + off ? R : None, 360 + off, 230 + off, NoteFrame: 239 + off);
        yield return new("restart while settling", () => Flat(), flatSpawn,
            (f, p) => f < 240 ? R : f < 252 ? None : R, 400, 230);
        yield return new("run-walk-run (pwm)", () => Flat(), flatSpawn,
            (f, p) => f < 180 ? R : f < 300 ? Pwm(f, 2, 5) : R, 420, 170);
        yield return new("single step-up", () => StepUp(), flatSpawn, (f, p) => R, 180, 30);
        var stairSpawn = new Vector2(1.5f * TS, 15 * TS - PlayerCharacter.Radius);
        yield return new("stairs run", () => Stairs(), stairSpawn, (f, p) => R, 200, 30);
        yield return new("stairs slow (pwm 2/4)", () => Stairs(), stairSpawn, (f, p) => Pwm(f, 2, 4), 400, 30);
        yield return new("low ceiling corridor", () => Corridor(), new Vector2(24f, 14 * TS - PlayerCharacter.Radius),
            (f, p) => R, 480, 60);
        yield return new("support loss (ledge)", () => Ledge(), flatSpawn, (f, p) => R, 180, 30);
        // A 2-tile-thick floor: its UNDERSIDE face sits within terrain extraction reach, and
        // (found building this harness) that downward half-plane shoves the whole rig into the
        // floor — δ pins at the box, contact targets miss the ground. Kept as its own row so the
        // pathology is measured rather than hidden; every other terrain is thick like game ground.
        yield return new("thin platform run", () => Build(12, 300, (r, c) => r >= 10), flatSpawn, (f, p) => R, 420, 180);
        yield return new("slash while running", () => Flat(), flatSpawn,
            (f, p) => new PlayerInput
            {
                Right = true,
                LeftClick = f >= 180 && (f - 180) % 45 < 1,
                MouseWorldPosition = p.Body.Position + new Vector2(60f * (p.Facing == 0 ? 1 : p.Facing), 0f),
            }, 420, 180);
    }

    // All terrain is ≥ 8 tiles thick (see "thin platform run" for why that matters).
    // Floor top at row 10, long enough for a sustained run.
    private static ChunkMap Flat(int w = 300) => Build(18, w, (r, c) => r >= 10);

    // One-tile riser at column 30, plateau beyond.
    private static ChunkMap StepUp() => Build(18, 200, (r, c) => r >= (c < 30 ? 10 : 9));

    // StairAnimationTests' staircase: runway, ten one-tile risers, plateau.
    private static ChunkMap Stairs() => Build(24, 60, (r, c) => r >= 15 - Math.Clamp(c - 7, 0, 10));

    // Runway, then the floor drops five tiles at column 40.
    private static ChunkMap Ledge() => Build(24, 200, (r, c) => r >= (c < 40 ? 10 : 15));

    // MTile.Bench's corridor geometry (flat runway into a 3-high tunnel with floor bumps ≡1
    // mod 4 and ceiling bumps ≡3 mod 4, so 2-high at the bumps), with a thick floor AND a
    // thick tunnel roof (11 tiles): a thin roof's upward face is the mirror of the thin-floor
    // problem, and the map's top row borders unloaded (Empty) space.
    private static ChunkMap Corridor() => Build(22, 64, (r, c) =>
    {
        bool tunnel = c >= 16;
        return r >= 14 || (r <= 10 && tunnel) || (r == 11 && tunnel && c % 4 == 3) || (r == 13 && tunnel && c % 4 == 1);
    });

    private static ChunkMap Build(int h, int w, Func<int, int, bool> solid)
    {
        var sb = new StringBuilder();
        for (int r = 0; r < h; r++)
        {
            for (int c = 0; c < w; c++) sb.Append(solid(r, c) ? 'X' : 'O');
            if (r < h - 1) sb.Append('\n');
        }
        return SimTerrain.FromAscii(sb.ToString(), originTileX: 0, originTileY: 0);
    }

    // ------------------------------------------------------------------ measurement

    private struct Frame
    {
        public double Us; public long Alloc;
        public bool Solved, Cadence, CadenceMode;
        public int Iters, Res, Jac, Rej, Rows, Vars;
        public float Phase, Dx, Dy, DTh;
        public AnimClip Clip;
        public string Action;
        public bool LowCeiling;
        public (int Bone, Vector2 Target, float Weight)[] Contacts;
        public Vector2[] Tips;        // rendered world tip of every bone
        public float Pen;             // max vertical depth of any rendered tip under an upward face
    }

    private static (Dictionary<string, double>, string) Measure(Scenario sc, Skeleton skel,
                                                              List<AnimationDocument> clips)
    {
        Frame[] q = null;
        var us = new double[sc.Frames];
        for (int rep = 0; rep < Reps; rep++)
        {
            var fr = RunOnce(sc, skel, clips);
            if (q == null) { q = fr; for (int i = 0; i < fr.Length; i++) us[i] = fr[i].Us; }
            else for (int i = 0; i < fr.Length; i++) us[i] = Math.Min(us[i], fr[i].Us);
        }

        int a = sc.MeasureFrom, b = sc.Frames;
        var m = new Dictionary<string, double>();
        var win = Enumerable.Range(a, b - a).ToArray();

        var t = win.Select(i => us[i]).OrderBy(x => x).ToArray();
        m["upd_p50_us"] = Pct(t, 0.50); m["upd_p95_us"] = Pct(t, 0.95);
        m["upd_p99_us"] = Pct(t, 0.99); m["upd_max_us"] = t[^1];
        m["alloc_B_mean"] = win.Average(i => (double)q[i].Alloc);
        m["alloc_B_max"] = win.Max(i => (double)q[i].Alloc);

        var solved = win.Where(i => q[i].Solved).ToArray();
        int cad = win.Count(i => q[i].Cadence);
        m["solve_frac"] = solved.Length / (double)win.Length;
        m["cad_frac"] = cad / (double)win.Length;
        double S(Func<Frame, double> f) => solved.Length == 0 ? 0 : solved.Average(i => f(q[i]));
        m["iters"] = S(f => f.Iters); m["res_evals"] = S(f => f.Res); m["jac_evals"] = S(f => f.Jac);
        m["rejected"] = S(f => f.Rej);
        m["seed_evals"] = solved.Length == 0 ? 0 : 11.0 * cad / solved.Length;
        m["rows_mean"] = S(f => f.Rows);
        m["rows_max"] = solved.Length == 0 ? 0 : solved.Max(i => q[i].Rows);
        m["vars"] = solved.Length == 0 ? 0 : q[solved[0]].Vars;

        // Phase rate (cycles/s) between consecutive frames on the same cadence clip, and its
        // jump (cycles/s²) across three such frames.
        var rates = new List<double>(); double jump = 0;
        double? prevRate = null;
        for (int i = Math.Max(a, 1); i < b; i++)
        {
            bool ok = q[i].CadenceMode && q[i - 1].CadenceMode && q[i].Clip == q[i - 1].Clip;
            if (!ok) { prevRate = null; continue; }
            double d = q[i].Phase - q[i - 1].Phase; if (d < 0) d += 1;
            double rate = d / Dt;
            rates.Add(rate);
            if (prevRate.HasValue) jump = Math.Max(jump, Math.Abs(rate - prevRate.Value) / Dt);
            prevRate = rate;
        }
        m["rate_mean"] = rates.Count == 0 ? 0 : rates.Average();
        m["rate_max"] = rates.Count == 0 ? 0 : rates.Max();
        m["rate_jump_max"] = jump;

        // Planted-contact slip: rendered tip motion between consecutive frames while the same
        // bone holds the same target. Target error: rendered tip vs its solve target, over
        // engaged contacts (weight ≥ 0.5; fresh captures ramp in from ~0 and sit at the tip).
        var slips = new List<double>(); var errs = new List<double>();
        for (int i = a; i < b; i++)
        {
            foreach (var c in q[i].Contacts)
            {
                if (c.Weight >= 0.5f) errs.Add((q[i].Tips[c.Bone] - c.Target).Length());
                if (i == 0) continue;
                foreach (var pc in q[i - 1].Contacts)
                    if (pc.Bone == c.Bone && (pc.Target - c.Target).LengthSquared() < 1e-4f)
                        slips.Add((q[i].Tips[c.Bone] - q[i - 1].Tips[c.Bone]).Length());
            }
        }
        m["slip_mean"] = slips.Count == 0 ? 0 : slips.Average();
        m["slip_max"] = slips.Count == 0 ? 0 : slips.Max();
        m["tgt_err_mean"] = errs.Count == 0 ? 0 : errs.Average();
        m["tgt_err_max"] = errs.Count == 0 ? 0 : errs.Max();
        m["pen_max"] = win.Max(i => (double)q[i].Pen);

        m["dx_max"] = win.Max(i => (double)MathF.Abs(q[i].Dx));
        m["dy_max"] = win.Max(i => (double)MathF.Abs(q[i].Dy));
        m["dth_max"] = win.Max(i => (double)q[i].DTh);

        // Rendered foot-tip acceleration (second difference, px/frame²) over the rig's
        // contact-capable nodes — the velocity-discontinuity proxy.
        var feet = FootBones(skel, clips);
        var acc = new List<double>();
        for (int i = Math.Max(a, 2); i < b; i++)
            foreach (int f in feet)
                acc.Add((q[i].Tips[f] - 2f * q[i - 1].Tips[f] + q[i - 2].Tips[f]).Length());
        acc.Sort();
        m["foot_acc_p99"] = acc.Count == 0 ? 0 : Pct(acc.ToArray(), 0.99);
        m["foot_acc_max"] = acc.Count == 0 ? 0 : acc[^1];

        // Notes: what the scenario actually exercised (clip mix, overlay actions, low ceiling).
        var clipMix = win.GroupBy(i => q[i].Clip).OrderByDescending(g => g.Count())
                         .Select(g => $"{g.Key}:{100.0 * g.Count() / win.Length:0}%");
        var actions = win.Select(i => q[i].Action).Where(x => x != null).Distinct();
        int low = win.Count(i => q[i].LowCeiling);
        string notes = (sc.NoteFrame >= 0 ? $"release_phase={q[sc.NoteFrame].Phase:0.00} " : "")
                     + "clips=" + string.Join(",", clipMix)
                     + (actions.Any() ? " actions=" + string.Join(",", actions) : "")
                     + (low > 0 ? $" lowceil={100.0 * low / win.Length:0}%" : "");
        return (m, notes);
    }

    private static Frame[] RunOnce(Scenario sc, Skeleton skel, List<AnimationDocument> clips)
    {
        var chunks = sc.Terrain();
        var sim = new Simulation(chunks, sc.Spawn);
        var anim = new CharacterAnimator(skel, Scale, clips);
        var predictor = new LatticePathSampler();
        var surfaces = new SolverSurface[8];   // == CosmeticUpdateSystem's scratch
        var frames = new Frame[sc.Frames];

        for (int f = 0; f < sc.Frames; f++)
        {
            var p = sim.Player;
            sim.Step(sc.Input(f, p));
            p = sim.Player;
            int tc = TerrainSurfaces.Extract(sim.Chunks, anim, p.Body.Position, p.Facing, Scale,
                                             surfaces, out bool near);
            predictor.Bind(p);
            var s = CharacterAnimSample.From(p, Dt, surfaces, tc, near, sim.Chunks, predictor.PredictAt);

            long a0 = GC.GetAllocatedBytesForCurrentThread();
            long t0 = Stopwatch.GetTimestamp();
            anim.Update(s);
            long t1 = Stopwatch.GetTimestamp();
            long a1 = GC.GetAllocatedBytesForCurrentThread();

            ref var fr = ref frames[f];
            fr.Us = (t1 - t0) * 1e6 / Stopwatch.Frequency;
            fr.Alloc = a1 - a0;
            fr.Solved = anim.SolvedThisFrame;
            fr.Cadence = anim.BaselineCadenceSolved;
            fr.CadenceMode = anim.BaselineCadenceMode;
            if (fr.Solved)
            {
                var w = anim.LastSolveWork;
                fr.Iters = w.Iterations; fr.Res = w.ResidualEvals; fr.Jac = w.JacobianEvals;
                fr.Rej = anim.BaselineRejectedTrials; fr.Rows = anim.LastSolveRows; fr.Vars = anim.BaselineVars;
            }
            fr.Phase = anim.State.Phase;
            fr.Clip = anim.State.Clip;
            fr.Dx = anim.HorizontalOffset; fr.Dy = anim.VerticalOffset;
            fr.DTh = anim.BaselineMaxAbsDTheta;
            fr.Action = s.Action is { Length: > 0 } act && act != "NullAction" && act != "None" ? act : null;
            fr.LowCeiling = s.LowCeiling;
            fr.Contacts = new (int, Vector2, float)[anim.BaselineContactCount];
            for (int i = 0; i < fr.Contacts.Length; i++)
            {
                var c = anim.BaselineContact(i);
                fr.Contacts[i] = (c.Bone, c.Target, c.Weight);
            }

            // The rendered pose, placed exactly as Game1 draws it.
            int dir = p.Facing == 0 ? 1 : p.Facing;
            var root = AttackGlowSystem.RigRoot(p.Body.Position, p.Facing, anim, Scale);
            var world = anim.Pose.ComputeWorld(Affine2.FromTRS(root, 0f, new Vector2(dir * Scale, Scale)));
            fr.Tips = new Vector2[skel.Count > 0 ? anim.Skeleton.Count : 0];
            float pen = 0f;
            for (int i = 0; i < fr.Tips.Length; i++)
            {
                fr.Tips[i] = world[i].Translation;
                pen = MathF.Max(pen, DepthBelowUpwardFace(sim.Chunks, fr.Tips[i]));
            }
            fr.Pen = pen;
        }
        return frames;
    }

    // Vertical exit depth of a point inside solid terrain: the shorter of the distance up to
    // the column's upward face (a foot sunk into a floor/tread) and down to its downward face
    // (a head poking into a roof). 0 when the point is in open space. Scans ≤ 8 tiles each way.
    private static float DepthBelowUpwardFace(ChunkMap chunks, Vector2 pt)
    {
        int gx = (int)MathF.Floor(pt.X / TS), gy = (int)MathF.Floor(pt.Y / TS);
        if (chunks.GetCellState(gx, gy) != TileState.Solid) return 0f;
        int top = gy, bot = gy;
        for (int k = 0; k < 8 && chunks.GetCellState(gx, top - 1) == TileState.Solid; k++) top--;
        for (int k = 0; k < 8 && chunks.GetCellState(gx, bot + 1) == TileState.Solid; k++) bot++;
        return MathF.Min(pt.Y - top * TS, (bot + 1) * TS - pt.Y);
    }

    private static int[] FootBones(Skeleton skel, List<AnimationDocument> clips)
    {
        var rig = SkeletonComposition.WithClipBones(skel, clips);
        var set = new SortedSet<int>();
        foreach (var d in clips)
        {
            if (d.Skeleton != rig.Name || d.Keyframes == null) continue;
            foreach (var k in d.Keyframes)
                if (k.Contacts != null)
                    foreach (var l in k.Contacts) { int b = rig.IndexOf(l.Node); if (b >= 0) set.Add(b); }
        }
        return set.ToArray();
    }

    private static double Pct(double[] sorted, double p)
        => sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)Math.Floor(p * (sorted.Length - 1) + 0.5))];

    // ------------------------------------------------------------------ provenance + I/O

    private static List<string> Provenance()
    {
        string root = RepoRoot();
        var h = new List<string>
        {
            "# MTile anim baseline (Plans/ANIMATION_RUNTIME_SIMPLIFICATION_PLAN.md §1)",
            "# regenerate: dotnet run -c Release --project MTile.Bench -- --anim-baseline --save <path>",
            $"# date {DateTime.UtcNow:yyyy-MM-dd HH:mm}Z  reps {Reps}  scale {Scale}  dt {Dt:0.#####}",
            $"# commit {Git(root, "rev-parse HEAD")}  dirty={(Git(root, "status --porcelain").Length > 0 ? "yes" : "no")}",
            $"# configs/anim_solver_config.json sha256={Sha(File.ReadAllBytes(Path.Combine(root, "configs", "anim_solver_config.json")))}",
            $"# configs/movement_config.json sha256={Sha(File.ReadAllBytes(Path.Combine(root, "configs", "movement_config.json")))}",
            // Proof the file above is what ran (the code defaults differ): a few of its tiers.
            $"# solver cfg in effect: TierContact={AnimSolverConfig.Current.TierContact} TierHard={AnimSolverConfig.Current.TierHard} "
            + $"CorePosePrior={AnimSolverConfig.Current.CorePosePrior} PhaseFloorPrior={AnimSolverConfig.Current.PhaseFloorPrior} "
            + $"ComWeightY={AnimSolverConfig.Current.ComWeightY} ComWeightX={AnimSolverConfig.Current.ComWeightX}",
        };
        foreach (string rig in new[] { "biped", "biped_rabbit" })
        {
            var files = Directory.GetFiles(Path.Combine(root, "SkeletonStates", rig), "*.json")
                                 .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            using var ms = new MemoryStream();
            foreach (var f in files)
            {
                var name = Encoding.UTF8.GetBytes(Path.GetFileName(f) + "\0");
                ms.Write(name); ms.Write(File.ReadAllBytes(f));
            }
            string skelFile = Path.Combine(root, "Skeletons", rig + ".json");
            string skelSha = File.Exists(skelFile) ? Sha(File.ReadAllBytes(skelFile))[..16] : "n/a";
            h.Add($"# SkeletonStates/{rig} sha256={Sha(ms.ToArray())} ({files.Length} clips)  Skeletons/{rig}.json sha256[..16]={skelSha}");
        }
        h.Add("# units: us = animator Update only (per-frame min over reps); solver counters = mean per LM-solve frame;");
        h.Add("#   seed_evals derived (11/cadence solve); rate cycles/s, rate_jump cycles/s^2; slip px/frame; tgt_err/pen/dx/dy px;");
        h.Add("#   pen = vertical exit depth of any rendered bone tip inside solid terrain (min of up/down);");
        h.Add("#   dth rad; foot_acc px/frame^2 (second difference of rendered contact-node tips). Rendered pose placed via RigRoot.");
        return h;
    }

    private static string Format(List<string> header,
                                 List<(string Rig, string Scen, Dictionary<string, double> M, string Notes)> rows)
    {
        var sb = new StringBuilder();
        foreach (var l in header) sb.AppendLine(l);
        sb.AppendLine("rig\tscenario\t" + string.Join("\t", Cols) + "\tnotes");
        foreach (var r in rows)
            sb.AppendLine($"{r.Rig}\t{r.Scen}\t" + string.Join("\t", Cols.Select(c => Fmt(r.M[c]))) + "\t" + r.Notes);
        return sb.ToString();
    }

    private static string Fmt(double v) => v.ToString(Math.Abs(v) >= 100 ? "0.0" : "0.000", CultureInfo.InvariantCulture);

    private static void Compare(string path,
                                List<(string Rig, string Scen, Dictionary<string, double> M, string Notes)> cur)
    {
        if (!File.Exists(path)) { Console.WriteLine($"compare: {path} not found"); return; }
        var lines = File.ReadAllLines(path).Where(l => !l.StartsWith("#") && l.Length > 0).ToArray();
        var cols = lines[0].Split('\t');
        var baseRows = new Dictionary<(string, string), Dictionary<string, double>>();
        foreach (var l in lines.Skip(1))
        {
            var parts = l.Split('\t');
            var d = new Dictionary<string, double>();
            for (int i = 2; i < Math.Min(parts.Length, cols.Length); i++)
                if (double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) d[cols[i]] = v;
            baseRows[(parts[0], parts[1])] = d;
        }
        Console.WriteLine($"\n=== compare vs {path} (base → current, Δ) ===");
        foreach (var r in cur)
        {
            if (!baseRows.TryGetValue((r.Rig, r.Scen), out var bm)) { Console.WriteLine($"{r.Rig}/{r.Scen}: (new)"); continue; }
            Console.WriteLine($"{r.Rig} / {r.Scen}");
            foreach (var c in Cols)
            {
                if (!bm.TryGetValue(c, out var bv)) continue;
                double cv = r.M[c], d = cv - bv;
                string pct = Math.Abs(bv) > 1e-9 ? $"{100 * d / Math.Abs(bv):+0;-0;0}%" : (Math.Abs(d) > 1e-9 ? "new" : "");
                Console.WriteLine($"  {c,-14} {Fmt(bv),10} → {Fmt(cv),10}   {(d >= 0 ? "+" : "")}{Fmt(d),-10} {pct}");
            }
        }
    }

    private static string Git(string root, string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args)
            { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false };
            using var p = Process.Start(psi);
            string o = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return o;
        }
        catch { return "unknown"; }
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "MTile.sln"))) d = d.Parent;
        return d?.FullName ?? ".";
    }

    private static string ArgValue(string[] args, string flag)
    {
        int i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
