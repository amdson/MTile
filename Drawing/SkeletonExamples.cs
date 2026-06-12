using System;
using System.IO;

namespace MTile;

// Rig loading. Coordinates are Y-down (up is -Y), local to each parent. The ONLY
// source of a rig is its authored Skeletons/<name>.json — there is deliberately no
// procedural fallback, so every host (game, editor, web, tests) renders the same
// figure or fails loudly at startup. Regenerating a lost rig = git.
public static class SkeletonExamples
{
    public const string BipedName = "biped";

    // Load a named rig from Skeletons/<name>.json. Throws with the expected path
    // when the directory or file is missing/unreadable — content is authored-only.
    public static Skeleton Load(string name)
    {
        string dir = FindSkeletonsDir()
            ?? throw new FileNotFoundException(
                "No Skeletons/ directory found (searched up from " +
                $"{AppContext.BaseDirectory}). The rig '{name}' must be authored at " +
                $"Skeletons/{name}.json — restore it from the repo.");
        return SkeletonStore.Load(dir, name)
            ?? throw new FileNotFoundException(
                $"Rig '{name}' not found or unreadable at {Path.Combine(dir, name + ".json")}. " +
                "Content is authored-only (no procedural fallback) — restore it from the repo.");
    }

    // Convenience for callers that hardcode the biped rig (Game1, tests, the
    // editor). Equivalent to Load(BipedName).
    public static Skeleton Biped() => Load(BipedName);

    // Walk up from the running binary looking for either a Skeletons/ folder beside
    // the executable (Desktop layout) or the repo root (Demo running from bin/...).
    // Returns null if no candidate exists.
    private static string FindSkeletonsDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            string c = Path.Combine(d.FullName, "Skeletons");
            if (Directory.Exists(c)) return c;
            // repo-root sentinel for editor runs out of bin/
            if (File.Exists(Path.Combine(d.FullName, "MTile.sln")))
                return Path.Combine(d.FullName, "Skeletons");
            d = d.Parent;
        }
        return null;
    }
}
