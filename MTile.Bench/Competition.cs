using System;
using System.Collections.Generic;
using System.IO;
using MTile;

namespace MTile.Bench;

// The fighter-package competition CLI (Plans/FIGHTER_COMPETITION.md):
//
//   dotnet run --project MTile.Bench -c Release -- --competition split --wave N --ladder <league.md>
//                                                               [--out Fights/competition/wave<N>.pool.json]
//   dotnet run --project MTile.Bench -c Release -- --competition files --pool <pool.json>
//   dotnet run --project MTile.Bench -c Release -- --competition score --pool <pool.json>
//                                                               [--submission Name]... [--out Fights/competition/wave<N>.md] [--quiet] [--no-rating]
//   dotnet run --project MTile.Bench -c Release -- --competition roll --pool <pool.json> --ladder <league.md> --wave N+1
//                                                               [--out Fights/competition/wave<N+1>.pool.json]
//
// `files` prints the holdout package paths (repo-relative, one per line, nothing else) —
// scripts/competition.py parses it to cut the competition base branch.
//
// All of the competition is FighterCompetition (library side, tested by
// FighterCompetitionTests); this file is argument parsing and printing only.
internal static class Competition
{
    public static int Run(string[] args)
    {
        int at = Array.IndexOf(args, "--competition");
        string cmd = at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        string root = Program.RepoRoot();
        try
        {
            switch (cmd)
            {
                case "split": LoadConfigs(root); return Split(args, root);
                case "files": LoadConfigs(root); return Files(args, root);
                case "score": LoadConfigs(root); return Score(args, root);
                case "roll":  LoadConfigs(root); return Roll(args, root);
                default:
                    Console.Error.WriteLine($"competition: expected a subcommand (split | files | score | roll), got '{cmd ?? "(none)"}'.");
                    return 2;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException
                                       or FormatException or OverflowException or InvalidOperationException)
        {
            Console.Error.WriteLine($"competition {cmd}: {ex.Message}");
            return 2;
        }
    }

    // Same three configs Game1 loads at boot (League.cs / Forge.cs), so scores use the game's tuning.
    private static void LoadConfigs(string root)
    {
        ImpactProfiles.Load(Path.Combine(root, "configs", "impact_profiles.json"));
        MaterialStrengths.Load(Path.Combine(root, "configs", "material_strengths.json"));
        FighterCosts.Load(Path.Combine(root, "configs", "fighter_costs.json"));
    }

    private static int Split(string[] args, string root)
    {
        int wave      = Int(Required(args, "--wave"), "--wave");
        string ladder = Required(args, "--ladder");
        string out_   = Value(args, "--out") ?? DefaultPoolPath(root, wave);
        var ranked = LeagueLadderFile.ParseScores(File.ReadAllText(Input(ladder, root)));
        var pool = CompetitionPool.Split(wave, ranked, source: Slashes(ladder));
        pool.Save(out_);
        PrintPool(pool, out_);
        return 0;
    }

    private static int Files(string[] args, string root)
    {
        var pool = CompetitionPool.Load(Input(Required(args, "--pool"), root));
        foreach (var f in pool.HoldoutFiles(root)) Console.WriteLine(f);
        return 0;
    }

    private static int Roll(string[] args, string root)
    {
        var prev      = CompetitionPool.Load(Input(Required(args, "--pool"), root));
        string ladder = Required(args, "--ladder");
        int wave      = Int(Required(args, "--wave"), "--wave");
        if (wave != prev.Wave + 1)
            throw new ArgumentException($"--wave {wave} does not follow the pool's wave {prev.Wave} (expected {prev.Wave + 1}).");
        string out_ = Value(args, "--out") ?? DefaultPoolPath(root, wave);
        var ranked = LeagueLadderFile.ParseScores(File.ReadAllText(Input(ladder, root)));
        var next = FighterCompetition.Roll(prev, ranked, Slashes(ladder));
        next.Save(out_);
        PrintPool(next, out_);
        return 0;
    }

    private static int Score(string[] args, string root)
    {
        var pool   = CompetitionPool.Load(Input(Required(args, "--pool"), root));
        bool quiet = Array.IndexOf(args, "--quiet") >= 0;
        bool rating = Array.IndexOf(args, "--no-rating") < 0;   // the pool's own all-to-all league is the slow part
        string out_ = Value(args, "--out")
                      ?? Path.Combine(root, "Fights", "competition", $"wave{pool.Wave}.md");
        List<string> subs = null;
        for (int i = 0; i < args.Length; i++)
            if (args[i] == "--submission")
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--"))
                    throw new ArgumentException("--submission needs a name.");
                (subs ??= new()).Add(args[i + 1]);
            }
        var listed = subs ?? pool.Submissions();
        if (listed.Count == 0)
            throw new ArgumentException($"no submissions: every participant is already in the wave-{pool.Wave} pool.");

        Console.WriteLine();
        Console.WriteLine($"competition wave {pool.Wave}: {listed.Count} submission(s) ({string.Join(", ", listed)}) " +
                          $"vs holdout {pool.Holdout.Count} / dev {pool.Dev.Count}, {pool.Frames}-frame bouts, " +
                          $"seeds {pool.FirstSeed}..{pool.FirstSeed + (ulong)Math.Max(pool.Seeds - 1, 0)}");

        // Which league is running: Run logs "<Name>: scoring vs ..." before a submission's
        // holdout league then its dev league (each restarts the bout index at 0), and
        // "head-to-head: ..." before the submissions-only league.
        string current = "?";
        int league = 0;
        void Log(string msg)
        {
            Console.WriteLine("  " + msg);
            int s = msg.IndexOf(": scoring vs", StringComparison.Ordinal);
            if (s >= 0) { current = msg.Substring(0, s); league = 0; }
            else if (msg.StartsWith("head-to-head", StringComparison.Ordinal)) { current = "head-to-head"; league = 0; }
        }
        Action<LeagueBout, int, int> onBout = null;
        if (!quiet)
            onBout = (b, k, n) =>
            {
                if (k == 0) league++;
                string label = current == "head-to-head" ? "head-to-head"
                             : $"{current} vs {(league <= 1 ? "holdout" : "dev")}";
                var r = b.Result;
                string res = r.WinnerTeam == 1 ? "left wins" : r.WinnerTeam == 2 ? "right wins" : "draw";
                Console.WriteLine($"    {label}  {k + 1}/{n}  {b.Terrain,-8} s{b.Seed} {b.LeftName} vs {b.RightName}: {res} after {r.Frames}f");
            };

        var report = FighterCompetition.Run(pool, subs, Log, onBout, rating);
        string md = report.Markdown();

        string dir = Path.GetDirectoryName(Path.GetFullPath(out_));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(out_, md);

        Console.WriteLine();
        Console.Write(md);
        Console.WriteLine();
        Console.WriteLine($"scored in {report.Elapsed.TotalSeconds:F1} s");
        Console.WriteLine($"wrote {Path.GetFullPath(out_)}");
        return 0;
    }

    private static void PrintPool(CompetitionPool pool, string path)
    {
        Console.WriteLine($"wave {pool.Wave} pool (from {(string.IsNullOrEmpty(pool.Source) ? "(unspecified)" : pool.Source)})");
        Console.WriteLine($"Dev ({pool.Dev.Count}):     {string.Join(", ", pool.Dev)}");
        Console.WriteLine($"Holdout ({pool.Holdout.Count}): {string.Join(", ", pool.Holdout)}");
        Console.WriteLine($"wrote {Path.GetFullPath(path)}");
    }

    private static string DefaultPoolPath(string root, int wave) =>
        Path.Combine(root, "Fights", "competition", $"wave{wave}.pool.json");

    // A path as given (CWD-relative or absolute), falling back to repo-root-relative so the
    // documented repo-relative paths work from any directory.
    private static string Input(string path, string root)
    {
        if (File.Exists(path) || Path.IsPathRooted(path)) return path;
        string alt = Path.Combine(root, path);
        return File.Exists(alt) ? alt : path;
    }

    private static int Int(string v, string flag) =>
        int.TryParse(v, out int n) ? n : throw new ArgumentException($"{flag} expects an integer, got '{v}'.");

    private static string Slashes(string path) => path.Replace('\\', '/');

    private static string Required(string[] args, string flag) =>
        Value(args, flag) ?? throw new ArgumentException($"missing {flag} <value>.");

    private static string Value(string[] args, string flag)
    {
        int i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null;
    }
}
