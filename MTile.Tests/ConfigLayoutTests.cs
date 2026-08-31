using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Layout guard for the runtime config files (configs/*.json).
//
// These are loaded by path string, through two different arms that must agree:
//
//   CWD-relative     Path.GetFullPath("configs/x.json") — hits the repo-source
//                    file when the game is launched from the repo root, which is
//                    what makes movement/anim-solver hot-reload edit the file you
//                    actually have open in an editor.
//   title-relative   TitleContent → TitleContainer — hits the per-host copy
//                    (Desktop: next to the binary; Web: under wwwroot).
//
// Both arms use the SAME string, so the source sub-path and the copied sub-path
// have to match. Nothing in the compiler enforces that: move the source without
// updating a host's copy rule and everything still builds, then the game boots
// with silently-defaulted tuning — every loader here is written to no-op on a
// missing file rather than throw. This test is the check that isn't happening
// anywhere else.
//
// Deliberately does NOT call the real loaders: MovementConfig.Load,
// ImpactProfiles.Load and MaterialStrengths.Load all swap process-wide statics,
// and this assembly runs tests un-parallelised precisely because that kind of
// mutation leaks between classes. Existence plus a JSON parse is enough to catch
// a bad move or a corrupted file.
public class ConfigLayoutTests(ITestOutputHelper output)
{
    private static readonly string[] Configs =
    {
        "game_config.json",
        "movement_config.json",
        "anim_solver_config.json",
        "impact_profiles.json",
        "material_strengths.json",
    };

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            if (File.Exists(Path.Combine(d.FullName, "MTile.Core.csproj"))) return d.FullName;
            d = d.Parent;
        }
        return null;
    }

    [Fact]
    public void EveryRuntimeConfigLivesUnderConfigsAndParses()
    {
        var root = RepoRoot();
        if (root == null) { output.WriteLine("Repo root not found — skipping."); return; }

        foreach (var name in Configs)
        {
            var path = Path.Combine(root, "configs", name);
            Assert.True(File.Exists(path), $"Missing {Path.Combine("configs", name)}.");

            // The loaders tolerate // comments; JsonDocument needs to be told.
            var opts = new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            using var doc = JsonDocument.Parse(File.ReadAllText(path), opts);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        }
        output.WriteLine($"All {Configs.Length} configs present under configs/ and parse.");
    }

    [Fact]
    public void NoConfigIsLeftBehindAtTheRepoRoot()
    {
        var root = RepoRoot();
        if (root == null) { output.WriteLine("Repo root not found — skipping."); return; }

        // A stray copy at the old location is worse than a missing one: whichever
        // the loader reaches first wins, so edits land in a file the game may not
        // be reading and the two drift apart silently.
        foreach (var name in Configs)
            Assert.False(File.Exists(Path.Combine(root, name)),
                $"{name} still exists at the repo root — it moved to configs/, and " +
                "two copies means edits can land in the one nothing reads.");
    }

    [Fact]
    public void DesktopHostCopiesConfigsToTheSameSubPathTheLoaderUses()
    {
        var root = RepoRoot();
        if (root == null) { output.WriteLine("Repo root not found — skipping."); return; }

        // Only meaningful once the Desktop host has been built; skip otherwise so
        // a Core-only or test-only build doesn't fail on an absent output dir.
        var outDir = Path.Combine(root, "MTile.Desktop", "bin", "Debug", "net8.0");
        if (!Directory.Exists(outDir)) { output.WriteLine("Desktop output not built — skipping."); return; }

        foreach (var name in Configs)
            Assert.True(File.Exists(Path.Combine(outDir, "configs", name)),
                $"Desktop build didn't stage configs/{name}. The <None Link=\"configs\\...\"> " +
                "rule in MTile.Desktop.csproj has to mirror the source sub-path.");
        output.WriteLine("Desktop output mirrors configs/.");
    }

    // VALUE parity, the twin of the path parity above. Every loader no-ops on a
    // missing file, so MovementConfig's C# property initialisers are both the
    // missing-file fallback AND what this whole assembly runs on — nothing in
    // the test host calls MovementConfig.Load (deliberately: it swaps a
    // process-wide static, and this assembly is un-parallelised precisely
    // because that kind of mutation leaks). So any key whose shipped value
    // differs from its C# default is tuning the GAME uses and no test ever sees.
    //
    // Not hypothetical: FoldEngine sat at "qp" here while the game shipped
    // "lattice", so every corrector and movement test was validating a solver
    // that does not ship. Compared by reflection rather than by calling Load,
    // to keep the no-static-mutation property the rest of this file documents.
    [Fact]
    public void ShippedMovementConfigMatchesTheCodeDefaultsTestsRunOn()
    {
        var root = RepoRoot();
        if (root == null) { output.WriteLine("Repo root not found — skipping."); return; }

        var path = Path.Combine(root, "configs", "movement_config.json");
        Assert.True(File.Exists(path), path);

        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling     = JsonCommentHandling.Skip,
        });

        var fresh = new MovementConfig();
        var props = typeof(MovementConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(p => p.Name, p => p);

        var diffs = new List<string>();
        int bound = 0;
        foreach (var kv in doc.RootElement.EnumerateObject())
        {
            if (!props.TryGetValue(kv.Name, out var prop)) continue;   // json-only keys aren't this test's business
            bound++;

            string shipped = kv.Value.ValueKind switch
            {
                JsonValueKind.String => kv.Value.GetString(),
                JsonValueKind.True   => "True",
                JsonValueKind.False  => "False",
                _                    => kv.Value.GetRawText(),
            };
            string dflt = Convert.ToString(prop.GetValue(fresh), CultureInfo.InvariantCulture);

            bool same =
                double.TryParse(dflt,    NumberStyles.Any, CultureInfo.InvariantCulture, out double a) &&
                double.TryParse(shipped, NumberStyles.Any, CultureInfo.InvariantCulture, out double b)
                    ? Math.Abs(a - b) < 1e-6
                    : string.Equals(dflt, shipped, StringComparison.Ordinal);

            if (!same) diffs.Add($"{kv.Name}: shipped={shipped}, C# default={dflt}");
        }

        output.WriteLine($"{bound} movement_config keys bound to MovementConfig; {diffs.Count} diverge.");
        foreach (var d in diffs) output.WriteLine("  " + d);

        Assert.True(diffs.Count == 0,
            "configs/movement_config.json disagrees with MovementConfig's C# defaults, so the suite " +
            "is testing tuning the game does not ship: " + string.Join("; ", diffs));
    }
}
