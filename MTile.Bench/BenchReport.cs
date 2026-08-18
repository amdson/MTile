using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace MTile.Bench;

// Collects the run's measurements, prints the table, and — the part that makes this a
// regression gate rather than a curiosity — diffs against a saved baseline.
//
// Format is deliberately flat text (`name<TAB>mean_us<TAB>worst_us`), not JSON: it diffs
// legibly in git, so a committed baseline shows in review exactly which scenario moved and
// by how much.
internal sealed class BenchReport
{
    internal readonly record struct Row(string Name, double MeanUs, double WorstUs);

    private readonly List<Row> _rows = new();

    // What counts as a regression rather than machine noise. Both thresholds must be
    // crossed, because measured spread here is NOT uniform: over three back-to-back runs on
    // an idle machine, `flat run` (~25 µs) swung 23–36 µs (±25%) while `anim
    // biped_rabbit/idle` (~2.4 ms) held within 1%. Scheduler jitter is roughly a constant
    // number of µs, so it looks enormous on the cheap scenarios and invisible on the
    // expensive ones — a single relative band would either cry wolf on the former or sleep
    // through the latter.
    //
    // So: relative band wide enough to survive the noisiest row, plus an absolute floor
    // that ignores swings too small to matter regardless of their percentage.
    //
    // These are set WIDE on purpose, and the honest description of what they buy is
    // narrow: this gate catches a scenario that got roughly 1.5× slower or worse. It is
    // not a 10% drift detector, and tightening it on a developer desktop just produces
    // false alarms — even at min-of-3, sub-100 µs rows here were observed swinging 2×
    // across process launches (`anim .../idle (no terrain)` is bimodal: whether the
    // dormancy gate settles into skipping depends on the pose it lands in).
    //
    // What that leaves genuinely gated is the set that matters: `bumpy corridor` (~280 µs),
    // `rollback frame` (~340 µs), and the animation rows (0.5–2.3 ms). If this ever runs on
    // a quiet dedicated box, both numbers can come down a long way.
    public const double NoiseBand = 0.50;
    public const double NoiseFloorUs = 100.0;

    public void Add(string name, double meanUs, double worstUs = 0)
    {
        _rows.Add(new Row(name, meanUs, worstUs));
        Console.WriteLine($"{name,-30} {meanUs,10:F1} {worstUs,22:F1}");
    }

    public static void Header()
        => Console.WriteLine($"{"scenario",-30} {"µs/tick",10} {"worst 60-tick bucket",22}");

    public void Save(string path)
    {
        using var w = new StreamWriter(path);
        w.WriteLine("# MTile.Bench baseline — regenerate with: dotnet run -c Release --project MTile.Bench -- --save <path>");
        w.WriteLine("# name\tmean_us\tworst_us");
        foreach (var r in _rows)
            w.WriteLine($"{r.Name}\t{r.MeanUs.ToString("F1", CultureInfo.InvariantCulture)}\t{r.WorstUs.ToString("F1", CultureInfo.InvariantCulture)}");
        Console.WriteLine($"\nbaseline written: {path}");
    }

    // Returns true if nothing regressed beyond the noise band. Rows missing from either
    // side are reported, not silently dropped — a renamed or deleted scenario is exactly
    // the case where a silent pass would be worst.
    public bool CompareTo(string path)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine($"\nno baseline at {path} — run with --save {path} to create one.");
            return true;
        }

        var baseline = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(path))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split('\t');
            if (parts.Length >= 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double us))
                baseline[parts[0]] = us;
        }

        Console.WriteLine($"\nvs baseline {path}:");
        bool ok = true;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in _rows)
        {
            seen.Add(r.Name);
            if (!baseline.TryGetValue(r.Name, out double was))
            {
                Console.WriteLine($"  {r.Name,-30}  NEW  {r.MeanUs:F1} µs");
                continue;
            }
            double delta = was > 0 ? (r.MeanUs - was) / was : 0;
            double absDelta = Math.Abs(r.MeanUs - was);
            if (absDelta < NoiseFloorUs) continue;
            if (delta > NoiseBand)
            {
                Console.WriteLine($"  {r.Name,-30}  REGRESSED  {was:F1} -> {r.MeanUs:F1} µs  ({delta:+0.0%;-0.0%})");
                ok = false;
            }
            else if (delta < -NoiseBand)
                Console.WriteLine($"  {r.Name,-30}  improved   {was:F1} -> {r.MeanUs:F1} µs  ({delta:+0.0%;-0.0%})");
        }
        foreach (var name in baseline.Keys)
            if (!seen.Contains(name))
                Console.WriteLine($"  {name,-30}  MISSING from this run");

        Console.WriteLine(ok ? $"  no regressions beyond ±{NoiseBand:P0} / {NoiseFloorUs:F0} µs." : "  REGRESSIONS FOUND.");
        return ok;
    }
}
