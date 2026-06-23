using System;
using System.IO;
using MTile;
using Xunit;

namespace MTile.Tests;

// Not a pass/fail test — a re-runnable inspection tool. It writes per-clip motion-probe
// tables (joint/tip world positions + phase-velocities) to <repo>/.probe/<clip>.md so
// limb geometry can be read as concrete numbers while authoring clips. Re-run with:
//   dotnet test --filter FullyQualifiedName~MotionProbeTests
public class MotionProbeTests
{
    [Fact]
    public void DumpLocomotionProbes()
    {
        var rig = SkeletonExamples.Biped();
        string states = FindUp("SkeletonStates");
        string outDir = Path.Combine(FindUp("MTile.sln", file: true), ".probe");
        Directory.CreateDirectory(outDir);

        var all = AnimationStore.LoadAll(states);

        // Semantic DIGEST for every clip (ground line, planted feet, knee/elbow direction, hand
        // height, lean, trajectory + auto-flags) — the batch-authoring readout.
        foreach (var clip in all)
            File.WriteAllText(Path.Combine(outDir, Safe(clip.Name) + ".digest.md"),
                              MotionProbe.Digest(clip, rig, scale: 1f, facing: +1));

        // Raw coordinate tables for the locomotion / known-reference subset (deep dives).
        foreach (var name in new[] { "walk", "run", "jump", "crouch", "idle" })
        {
            var clip = all.Find(d => d.Name == name);
            if (clip == null) continue;
            File.WriteAllText(Path.Combine(outDir, name + ".md"),
                              MotionProbe.Report(clip, rig, scale: 1f, facing: +1, samples: 16));
        }

        // Example diff: does the vault-hands overlay actually leave the idle rest pose?
        var hands = all.Find(d => d.Name == "vaulthands");
        var idle  = all.Find(d => d.Name == "idle");
        if (hands != null && idle != null)
            File.WriteAllText(Path.Combine(outDir, "vaulthands.vs-idle.md"),
                              MotionProbe.Diff(hands, idle, rig));

        Assert.True(Directory.GetFiles(outDir, "*.md").Length > 0, $"no probe output written to {outDir}");
    }

    private static string Safe(string s)
    {
        foreach (char ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '_');
        return s;
    }

    // Walk up from the test binary for a directory (or a marker file's directory).
    private static string FindUp(string marker, bool file = false)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            string p = Path.Combine(d.FullName, marker);
            if (file ? File.Exists(p) : Directory.Exists(p)) return file ? d.FullName : p;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException($"could not find {marker} above {AppContext.BaseDirectory}");
    }
}
