using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// The package gate (Plans/FIGHTER_PACKAGE_GUIDE.md §1, §5): every IFighterPackage that
// FighterPackages.Discover() finds must
//   (a) compile under the default budget,
//   (b) hand out a fresh spec on every Spec() call,
//   (c) fight deterministically (same result, same per-frame checksums, twice) — and
//       record through a FightRecord to the very same fight (the package brain survives
//       the fight file),
//   (d) survive snapshot → run → restore → replay bit-identically (rollback),
//   (e) carry no writable instance fields (nor mutable statics) on its brain,
//   (f) stay under FighterCostConfig.DecideBudgetMicros per Decide on average.
// Theories are keyed on package NAME so the test output names each package; the
// package is resolved inside the test.
public class FighterPackageTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Packages()
        => FighterPackages.Discover().Select(p => new object[] { p.Name });

    private static IFighterPackage Get(string name)
        => FighterPackages.Discover().Single(p => p.Name == name);

    private static List<ArenaEntry> VsBrick(FighterSpec spec, string terrain = "flat") => new()
    {
        new ArenaEntry(spec,                 FighterArena.LeftSpawn,              1),
        new ArenaEntry(FighterRoster.Brick(), FighterArena.RightSpawnFor(terrain), 2),
    };

    [Fact]
    public void Discover_FindsTheExample_SortedByName()
    {
        var names = FighterPackages.Discover().Select(p => p.Name).ToList();
        Assert.Contains(nameof(ExampleBrawler), names);
        var sorted = names.OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(sorted, names);
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        output.WriteLine("packages: " + string.Join(", ", names));
    }

    // (a)
    [Theory, MemberData(nameof(Packages))]
    public void Spec_CompilesUnderTheDefaultBudget(string name)
    {
        var r = FighterCompiler.Compile(Get(name).Spec(), new PhysicsCostModel());
        output.WriteLine(r.Report());
        Assert.True(r.IsValid, $"{name} does not compile:\n{r.Report()}");
    }

    // (b)
    [Theory, MemberData(nameof(Packages))]
    public void Spec_IsFreshEachCall(string name)
    {
        var p = Get(name);
        var a = p.Spec();
        var b = p.Spec();
        Assert.NotSame(a, b);
        Assert.NotSame(a.Actions, b.Actions);
        var model = new PhysicsCostModel();
        Assert.Equal(FighterCompiler.Compile(a, model).Total, FighterCompiler.Compile(b, model).Total);
        // Mutating one (as the arena does) must not reach the other.
        a.Team = 7; a.Kind = EntityKind.FighterSlot1;
        Assert.NotEqual(7, b.Team);
    }

    // (c)
    [Theory, MemberData(nameof(Packages))]
    public void Match_IsDeterministic_AndRecordsToTheSameFight(string name)
    {
        var p = Get(name);
        var sumsA = new List<ulong>();
        var sumsB = new List<ulong>();
        var map   = FighterArena.Flat;
        var a = FighterArena.Run(map(), VsBrick(p.Spec()), FighterArena.PlayerPark, 720, null, sumsA);
        var b = FighterArena.Run(map(), VsBrick(p.Spec()), FighterArena.PlayerPark, 720, null, sumsB);
        Assert.Equal(a.WinnerTeam,  b.WinnerTeam);
        Assert.Equal(a.Frames,      b.Frames);
        Assert.Equal(a.HealthLeft,  b.HealthLeft);
        Assert.Equal(a.DamageDealt, b.DamageDealt);
        Assert.Equal(sumsA, sumsB);

        // The league scores through FightRecord (spec → DTO → spec). The package's brain
        // must survive that trip, or the recorded fight is a different fight.
        var rec = FightRecord.Describe("flat", VsBrick(p.Spec()), FighterArena.PlayerPark, 720);
        Assert.Equal(name, rec.Entries[0].Spec.Package);
        var r = rec.Record();
        Assert.Equal(a.WinnerTeam, r.WinnerTeam);
        Assert.Equal(sumsA, rec.Checksums);
        output.WriteLine($"{name} vs Brick on flat: winner team {a.WinnerTeam} after {a.Frames} frames, " +
                         $"health {a.HealthLeft[0]:F2} / {a.HealthLeft[1]:F2}");
    }

    // (d) — the rollback gate the guide's §5 promises.
    [Theory, MemberData(nameof(Packages))]
    public void Rollback_SnapshotRunRestoreReplay_IsBitIdentical(string name)
    {
        var entries = VsBrick(Get(name).Spec());
        var sim = new Simulation(FighterArena.Flat(), FighterArena.PlayerPark,
                                 g => FighterArena.Populate(g, entries, null));
        const int Warm = 60, N = 240;
        for (int f = 0; f < Warm; f++) sim.Step(default);
        var snap = sim.Snapshot();

        var live = new List<ulong>(N);
        for (int f = 0; f < N; f++) { sim.Step(default); live.Add(sim.Checksum()); }
        sim.Restore(snap);
        for (int f = 0; f < N; f++)
        {
            sim.Step(default);
            ulong c = sim.Checksum();
            Assert.True(c == live[f], $"{name}: replay diverged at frame {Warm + f + 1} (step {f} after restore).");
        }
    }

    // (e)
    [Theory, MemberData(nameof(Packages))]
    public void Brain_HasNoWritableFields(string name)
    {
        var p    = Get(name);
        var spec = p.Spec();
        var ctrl = spec.Brain(spec);
        Assert.IsAssignableFrom<FighterController>(ctrl);

        var bad = new List<string>();
        for (var t = ctrl.GetType(); t != null && t != typeof(object); t = t.BaseType)
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (!f.IsInitOnly) bad.Add($"{t.Name}.{f.Name}");
        // Statics too (guide §5: no statics): a mutable static on the brain or the package
        // is shared state the snapshot cannot see.
        foreach (var t in new[] { ctrl.GetType(), p.GetType() })
            foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (!f.IsInitOnly && !f.IsLiteral) bad.Add($"static {t.Name}.{f.Name}");
        Assert.True(bad.Count == 0,
            $"{name}'s brain has writable fields: {string.Join(", ", bad)}. Per-entity memory belongs in BrainScratch; " +
            "config must be readonly (or an init-only property).");
    }

    // (f)
    [Theory, MemberData(nameof(Packages))]
    public void Brain_StaysUnderTheComputeBudget(string name)
    {
        var p = Get(name);
        // Warm-up bout: JIT for the brain and FighterSenses would otherwise land in the
        // first timed Decide and dominate a 300-call mean.
        FighterArena.Run(FighterArena.Flat(), VsBrick(p.Spec()), FighterArena.PlayerPark, 60);

        var timer = new DecideTimer();
        var spec  = p.Spec();
        spec.Brain = TimedController.Wrap(spec.Brain, timer);
        var entries = VsBrick(spec);
        var sim = new Simulation(FighterArena.Flat(), FighterArena.PlayerPark,
                                 g => FighterArena.Populate(g, entries, null));
        var sw = Stopwatch.StartNew();
        for (int f = 0; f < 300; f++) sim.Step(default);
        sw.Stop();

        float budget = FighterCosts.Current.DecideBudgetMicros;
        output.WriteLine($"{name}: {timer.Calls} Decide calls, {timer.MeanMicros:F2} µs mean (budget {budget:0.#}), " +
                         $"{timer.PaidPerCall:F3} paid reads / frame; whole step loop {sw.Elapsed.TotalMilliseconds * 1000 / 300:F1} µs/frame");
        Assert.True(timer.Calls > 0, "the brain was never asked to decide");
        Assert.True(timer.MeanMicros < budget,
            $"{name} spends {timer.MeanMicros:F2} µs per Decide on average; the budget is {budget:0.#} µs (DecideBudgetMicros).");
    }
}
