using System;
using System.Collections.Generic;
using System.IO;
using MTile;

namespace MTile.Bench;

// Records one fighter bout to a fight file the game can replay:
//
//   dotnet run --project MTile.Bench -- --record-fight Brick Sprinter flat Fights/brick_v_sprinter.fight.json [--frames 720]
//
// Left and right are roster archetype names (FighterRoster.All) — or a path to a
// .fight.json whose first entry's spec should be reused — and the terrain is one of the
// arena's (flat / corridor / hills). The bout runs headless, its result and per-frame
// checksums go into the file, and `MTile.Desktop -- --fight <file>` shows it.
internal static class FightRecorderCli
{
    public static int Run(string[] args)
    {
        int at = Array.IndexOf(args, "--record-fight");
        if (at < 0 || args.Length < at + 5)
        {
            Console.Error.WriteLine("usage: --record-fight <left> <right> <flat|corridor|hills> <out.fight.json> [--frames N]");
            return 2;
        }
        string left = args[at + 1], right = args[at + 2], terrain = args[at + 3], outPath = args[at + 4];
        int frames = FighterArena.DefaultMaxFrames;
        int fi = Array.IndexOf(args, "--frames");
        if (fi >= 0 && fi + 1 < args.Length) frames = int.Parse(args[fi + 1]);

        // Same three configs Game1 loads at boot, so the recorded bout is the one the
        // game would show.
        string root = Program.RepoRoot();
        ImpactProfiles.Load(Path.Combine(root, "configs", "impact_profiles.json"));
        MaterialStrengths.Load(Path.Combine(root, "configs", "material_strengths.json"));
        FighterCosts.Load(Path.Combine(root, "configs", "fighter_costs.json"));

        var entries = new List<ArenaEntry>
        {
            new(Resolve(left),  FighterArena.LeftSpawn,              1),
            new(Resolve(right), FighterArena.RightSpawnFor(terrain), 2),
        };
        var rec = FightRecord.Describe(terrain, entries, FighterArena.PlayerPark, frames);
        rec.SimVersion = GitHash(root);
        rec.Notes      = $"recorded by MTile.Bench --record-fight {left} {right} {terrain}";

        var sw  = System.Diagnostics.Stopwatch.StartNew();
        var res = rec.Record();
        rec.Save(outPath);

        Console.WriteLine(rec.Summary());
        Console.WriteLine($"  health left {res.HealthLeft[0]:0.00} / {res.HealthLeft[1]:0.00}, " +
                          $"dealt {res.DamageDealt[0]:0.00} / {res.DamageDealt[1]:0.00}, " +
                          $"{rec.Checksums.Count} checksums, {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"  wrote {Path.GetFullPath(outPath)}");
        Console.WriteLine($"  view:  dotnet run --project MTile.Desktop -- --fight {outPath}");
        return 0;
    }

    private static FighterSpec Resolve(string nameOrPath)
    {
        if (nameOrPath.EndsWith(".fight.json", StringComparison.OrdinalIgnoreCase))
            return FightRecord.Load(nameOrPath).Entries[0].Spec.ToSpec();
        foreach (var make in FighterRoster.All)
        {
            var s = make();
            if (string.Equals(s.Name, nameOrPath, StringComparison.OrdinalIgnoreCase)) return s;
        }
        var names = new List<string>();
        foreach (var make in FighterRoster.All) names.Add(make().Name);
        throw new ArgumentException($"'{nameOrPath}' is not a roster fighter ({string.Join(", ", names)}) or a .fight.json");
    }

    // Best effort: the checked-out commit, read straight from .git, so the file says
    // which sim produced it. Empty when that fails (not a git checkout).
    private static string GitHash(string root)
    {
        try
        {
            string head = File.ReadAllText(Path.Combine(root, ".git", "HEAD")).Trim();
            if (!head.StartsWith("ref:")) return head;
            string refPath = Path.Combine(root, ".git", head.Substring(4).Trim().Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(refPath)) return File.ReadAllText(refPath).Trim();
            string packed = Path.Combine(root, ".git", "packed-refs");
            if (File.Exists(packed))
                foreach (var line in File.ReadAllLines(packed))
                    if (line.EndsWith(head.Substring(4).Trim())) return line.Split(' ')[0];
        }
        catch { }
        return "";
    }
}
