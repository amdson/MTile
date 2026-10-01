using System;
using System.IO;
using MTile;

namespace MTile.Bench;

// The fighter league CLI (Plans/FIGHTER_PACKAGE_GUIDE.md §6):
//
//   dotnet run --project MTile.Bench -c Release -- --league [--only <Name>]... [--frames N]
//                                                         [--out <dir>] [--no-record] [--quiet]
//
// Every discovered package plus the six roster archetypes, every ordered pair on every
// arena terrain. Prints the ladder and one matrix per terrain, writes the same to
// <out>/league.md, and saves each bout as <out>/<A>_vs_<B>_<terrain>.fight.json unless
// --no-record. `--only X` plays X against everyone (may repeat).
//
// All of the league is FighterLeague (library side, tested by FighterLeagueTests); this
// file is argument parsing and printing only.
internal static class League
{
    public static int Run(string[] args)
    {
        string root = Program.RepoRoot();
        // Same three configs Game1 loads at boot, so the league scores the game's tuning.
        ImpactProfiles.Load(Path.Combine(root, "configs", "impact_profiles.json"));
        MaterialStrengths.Load(Path.Combine(root, "configs", "material_strengths.json"));
        FighterCosts.Load(Path.Combine(root, "configs", "fighter_costs.json"));

        bool quiet    = Array.IndexOf(args, "--quiet") >= 0;
        bool noRecord = Array.IndexOf(args, "--no-record") >= 0;
        string outDir = Value(args, "--out") ?? "Fights/league";
        var settings = new LeagueSettings
        {
            Frames     = int.Parse(Value(args, "--frames") ?? "720"),
            OutDir     = noRecord ? null : outDir,
            SimVersion = FightRecorderCli.GitHash(root),
        };
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--only") settings.Only.Add(args[i + 1]);
        if (!quiet)
            settings.OnBout = (b, k, n) =>
            {
                var r = b.Result;
                string res = r.WinnerTeam == 1 ? "left wins" : r.WinnerTeam == 2 ? "right wins" : "draw";
                Console.WriteLine($"  [{k + 1,3}/{n}] {b.Terrain,-8} {b.LeftName} vs {b.RightName}: " +
                                  $"{res} after {r.Frames}f, margin {b.Margin:+0.00;-0.00;0.00}");
            };

        Console.WriteLine();
        Console.WriteLine($"league: {settings.Frames}-frame bouts" +
                          (settings.Only.Count > 0 ? $", only {string.Join(", ", settings.Only)}" : "") +
                          (noRecord ? ", not recording" : $", recording to {outDir}"));

        LeagueResult result;
        try { result = FighterLeague.Run(settings); }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }

        Console.WriteLine();
        foreach (var d in result.Disqualified) Console.WriteLine("DISQUALIFIED " + d);
        Console.Write(result.LadderText());
        foreach (var t in result.Terrains) { Console.WriteLine(); Console.Write(result.MatrixText(t)); }
        Console.WriteLine();
        Console.WriteLine($"{result.Bouts.Count} bouts in {result.Elapsed.TotalSeconds:F1} s " +
                          $"({result.Elapsed.TotalMilliseconds / Math.Max(result.Bouts.Count, 1):F0} ms each)");

        Directory.CreateDirectory(outDir);
        string md = Path.Combine(outDir, "league.md");
        File.WriteAllText(md, result.Markdown());
        Console.WriteLine($"wrote {Path.GetFullPath(md)}");
        if (!noRecord)
            Console.WriteLine($"view a bout:  dotnet run --project MTile.Desktop -- --fight {outDir}/<A>_vs_<B>_<terrain>.fight.json");
        return 0;
    }

    private static string Value(string[] args, string flag)
    {
        int i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null;
    }
}
