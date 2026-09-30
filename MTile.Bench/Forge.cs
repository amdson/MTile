using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using MTile;

namespace MTile.Bench;

// The AI designer's CLI (Plans/FIGHTER_DESIGN_PLAN.md §8.3):
//
//   dotnet run --project MTile.Bench -- --forge [--restarts R] [--steps S] [--seed N]
//                                              [--frames N] [--both-orders] [--quiet]
//
// Random restarts + hill climbing over FighterSpec, scored by win rate against the six
// FighterRoster archetypes on the three FighterArena terrains (draw = ½, ties broken on
// health-fraction margin). Specs that fail FighterCompiler are infeasible and never
// scored. Prints the best spec as a paste-able FighterRoster entry, its cost report, and
// its record against each archetype.
//
// All the search logic is FighterForge (library side, tested by FighterForgeTests); this
// file is argument parsing and printing only.
//
// Defaults: 4 restarts × 30 steps, one spawn order (the candidate spawns left), 720-frame
// matches — 18 matches per evaluation, 125 evaluations (incl. one determinism re-check),
// ≈ 0.65 s each in a Debug build ⇒ ≈ 80 s. --both-orders doubles the matches per
// evaluation (and the runtime).
internal static class Forge
{
    public static int Run(string[] args)
    {
        string root = Program.RepoRoot();
        // The game's boot-time loads (Game1): the fighter economy, plus the two other
        // sim-affecting tables, so a forged spec is scored under the tuning it will ship with.
        ImpactProfiles.Load(Path.Combine(root, "configs", "impact_profiles.json"));
        MaterialStrengths.Load(Path.Combine(root, "configs", "material_strengths.json"));
        FighterCosts.Load(Path.Combine(root, "configs", "fighter_costs.json"));

        int  seed     = Int(args, "--seed", 1);
        bool quiet    = Array.IndexOf(args, "--quiet") >= 0;
        var settings  = new ForgeSettings
        {
            Restarts   = Int(args, "--restarts", 4),
            Steps      = Int(args, "--steps", 30),
            Frames     = Int(args, "--frames", 720),
            BothOrders = Array.IndexOf(args, "--both-orders") >= 0,
        };
        int perEval = settings.Opponents.Count * settings.Terrains.Count * (settings.BothOrders ? 2 : 1);

        Console.WriteLine();
        Console.WriteLine($"forge: seed {seed}, {settings.Restarts} restarts × {settings.Steps} steps, " +
                          $"{settings.Frames}-frame matches, {perEval} matches per evaluation " +
                          $"({settings.Opponents.Count} opponents × {settings.Terrains.Count} terrains × " +
                          $"{(settings.BothOrders ? "both orders" : "one order")})");

        var sw = Stopwatch.StartNew();
        var result = FighterForge.Search(seed, settings, r =>
        {
            if (quiet) return;
            var sp = r.End.Spec;
            Console.WriteLine($"  restart {r.Index + 1}/{settings.Restarts}: {r.StartScore} → {r.EndScore}  " +
                              $"[{r.Accepted}/{r.Evaluations - 1} steps kept]  " +
                              $"{FighterSpecPrinter.BrainClass(r.End.Brain)}, {sp.Actions.Count} actions " +
                              $"({string.Join(", ", sp.Actions.ConvertAll(a => a.Kind.ToString()))}), " +
                              $"R {sp.Radius:0.#} HP {sp.Health:0.#}   ({sw.Elapsed.TotalSeconds:F0}s)");
        });
        sw.Stop();

        var best = result.Best;
        var compiled = FighterCompiler.Compile(best.ToSpec(), new PhysicsCostModel());

        Console.WriteLine();
        Console.WriteLine($"best: {result.BestScore}   — {result.Evaluations} evaluations in " +
                          $"{sw.Elapsed.TotalSeconds:F1}s ({sw.Elapsed.TotalSeconds / Math.Max(result.Evaluations, 1):F2}s each)");
        Console.WriteLine();
        Console.WriteLine("// ── paste into FighterRoster.cs (and add an EntityKind) ──");
        Console.Write(FighterSpecPrinter.ToCSharp(best));
        Console.WriteLine();
        Console.WriteLine("cost report:");
        Console.Write(compiled.Report());
        Console.WriteLine();
        Console.WriteLine("vs the roster (W-L-D across terrains" + (settings.BothOrders ? " × orders" : "") + "):");
        foreach (var m in result.BestScore.PerOpponent)
            Console.WriteLine($"  {m.Opponent,-10} {m.Wins}-{m.Losses}-{m.Draws}");
        return 0;
    }

    private static int Int(string[] args, string flag, int dflt)
    {
        int i = Array.IndexOf(args, flag);
        if (i < 0 || i + 1 >= args.Length) return dflt;
        return int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : dflt;
    }
}
