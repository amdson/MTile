using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace MTile.Tests.Sim;

// FighterRating: Bradley–Terry on the Elo scale. Pure arithmetic over a game list, so the
// checks are on ordering, symmetry, anchoring and determinism; the competition wiring is
// checked through FighterCompetition.Run on a tiny pool.
public class FighterRatingTests
{
    private static List<(string A, string B, double ScoreA)> Games(params (string, string, double, int)[] spec)
    {
        var g = new List<(string, string, double)>();
        foreach (var (a, b, s, n) in spec)
            for (int i = 0; i < n; i++) g.Add((a, b, s));
        return g;
    }

    [Fact]
    public void Dominance_OrdersRatings_AndAnchorMeanIs1500()
    {
        // A beats B 9 of 10, B beats C 9 of 10, A beats C 10 of 10.
        var games = Games(("A", "B", 1, 9), ("A", "B", 0, 1), ("B", "C", 1, 9), ("B", "C", 0, 1), ("A", "C", 1, 10));
        var r = FighterRating.Fit(games, new[] { "A", "B", "C" });
        Assert.True(r["A"] > r["B"] && r["B"] > r["C"], $"{r["A"]} {r["B"]} {r["C"]}");
        Assert.Equal(1500, (r["A"] + r["B"] + r["C"]) / 3, 6);
        Assert.True(r["A"] - r["C"] > 300, $"spread {r["A"] - r["C"]}");
    }

    [Fact]
    public void Symmetric_IsEqual_DrawsCountHalf_AndDeterministic()
    {
        var games = Games(("A", "B", 1, 5), ("B", "A", 1, 5), ("A", "C", 0.5, 10), ("B", "C", 0.5, 10));
        var r1 = FighterRating.Fit(games, new[] { "A", "B", "C" });
        var r2 = FighterRating.Fit(games, new[] { "A", "B", "C" });
        Assert.Equal(r1["A"], r1["B"], 6);
        Assert.Equal(r1["A"], r1["C"], 6);
        Assert.Equal(1500, r1["A"], 6);
        Assert.Equal(r1, r2);
    }

    [Fact]
    public void NeverWins_StillFinite_AndAnchorIgnoresUnknownNames()
    {
        var games = Games(("A", "B", 1, 20));
        var r = FighterRating.Fit(games, new[] { "A", "NoSuchBot" });
        Assert.True(double.IsFinite(r["B"]) && r["B"] < r["A"]);
        Assert.Equal(1500, r["A"], 6);                      // only A anchors
        Assert.Throws<ArgumentException>(() => FighterRating.Fit(Games(("A", "B", 1.5, 1)), null));
        Assert.Empty(FighterRating.Fit(Array.Empty<(string, string, double)>(), null));
    }

    [Fact]
    public void Competition_Run_FitsRatings_PoolMean1500_AndNoRatingSkips()
    {
        var pool = new CompetitionPool
        {
            Wave = 3, Source = "test", Frames = 240, Seeds = 1, FirstSeed = 100,
            Dev = { "Brick" }, Holdout = { "Sprinter" }, Terrains = { "flat" },
        };
        var subs = new[] { "ExampleBrawler", "Flyer" };
        var rep = FighterCompetition.Run(pool, subs);
        Assert.NotNull(rep.Ratings);
        Assert.NotNull(rep.PoolLeague);
        Assert.Equal(2, rep.PoolLeague.Participants.Count);
        Assert.Equal(4, rep.Ratings.Count);
        Assert.Equal(1500, (rep.Ratings["Brick"] + rep.Ratings["Sprinter"]) / 2, 6);
        Assert.All(rep.Scores, s => Assert.True(double.IsFinite(s.Rating)));
        string md = rep.Markdown();
        Assert.Contains("## By author", md);
        Assert.Contains("## Ratings", md);
        Assert.Contains("rating", md);
        Assert.Equal(md, FighterCompetition.Run(pool, subs).Markdown());

        var off = FighterCompetition.Run(pool, subs, rating: false);
        Assert.Null(off.Ratings);
        Assert.Null(off.PoolLeague);
        Assert.All(off.Scores, s => Assert.True(double.IsNaN(s.Rating)));
        Assert.DoesNotContain("## Ratings", off.Markdown());
    }
}
