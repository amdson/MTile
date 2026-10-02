using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace MTile.Tests.Sim;

// The sampled league (LeagueSettings.Sample): a bout budget instead of every pair × terrain
// × seed × side. Pins the contract: exactly the budget, every pair from both sides, an even
// spread (uniform mode), deterministic, and the report says it was sampled.
public class FighterLeagueSampleTests
{
    private static LeagueSettings Small(int sample, bool adaptive = false)
    {
        var s = new LeagueSettings { Frames = 240, Seeds = 4, FirstSeed = 100, Sample = sample, Adaptive = adaptive, Threads = 2 };
        s.Include.AddRange(new[] { "Brick", "Sprinter", "Gunner", "Turret" });
        return s;
    }

    private static Dictionary<(string, string), int> PairCounts(LeagueResult r)
    {
        var c = new Dictionary<(string, string), int>();
        foreach (var b in r.Bouts)
        {
            var key = string.CompareOrdinal(b.LeftName, b.RightName) < 0 ? (b.LeftName, b.RightName) : (b.RightName, b.LeftName);
            c[key] = c.TryGetValue(key, out var n) ? n + 1 : 1;
        }
        return c;
    }

    [Fact]
    public void PlaysExactlyTheBudget_EveryPairFromBothSides_Evenly()
    {
        var r = FighterLeague.Run(Small(30));
        Assert.Equal(30, r.Bouts.Count);
        Assert.Equal(4 * 3 * 3 * 4, r.Possible);   // ordered pairs × terrains × seeds
        Assert.True(r.Sampled);
        var counts = PairCounts(r);
        Assert.Equal(6, counts.Count);
        Assert.True(counts.Values.Min() >= 2);
        Assert.True(counts.Values.Max() - counts.Values.Min() <= 1, string.Join(" ", counts.Values));
        foreach (var (a, b) in counts.Keys)
        {
            Assert.Contains(r.Bouts, x => x.LeftName == a && x.RightName == b);
            Assert.Contains(r.Bouts, x => x.LeftName == b && x.RightName == a);
        }
        Assert.Contains("Sampled league", r.Markdown());
    }

    [Fact]
    public void IsDeterministic()
    {
        string Sig(LeagueResult r) => string.Join(";", r.Bouts.Select(b => $"{b.LeftName}>{b.RightName}@{b.Terrain}/{b.Seed}={b.Result.WinnerTeam}"));
        Assert.Equal(Sig(FighterLeague.Run(Small(26))), Sig(FighterLeague.Run(Small(26))));
        Assert.Equal(Sig(FighterLeague.Run(Small(26, adaptive: true))), Sig(FighterLeague.Run(Small(26, adaptive: true))));
    }

    [Fact]
    public void ABudgetUnderTwoAPair_PlaysOneAPair()
    {
        var r = FighterLeague.Run(Small(6));
        Assert.Equal(6, r.Bouts.Count);
        Assert.All(PairCounts(r).Values, n => Assert.Equal(1, n));
    }

    [Fact]
    public void NoSample_IsTheFullLeague()
    {
        var s = Small(0); s.Seeds = 1;
        var r = FighterLeague.Run(s);
        Assert.Equal(r.Possible, r.Bouts.Count);
        Assert.False(r.Sampled);
    }
}
