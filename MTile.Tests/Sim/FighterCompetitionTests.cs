using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// The fighter competition (FighterCompetition.cs): the stratified Dev/Holdout split is a
// pure function of the ranked list, the pool file round-trips, the ladder parser reads a
// real league report, and scoring is one-versus-pool and bit-identical run to run.
// Bout runs are kept tiny (one terrain, few frames, two seeds, one-bot sides).
public class FighterCompetitionTests(ITestOutputHelper output)
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MTile.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }

    // The wave-2 ladder as parsed from the committed report (all 19 names).
    private static List<(string Name, double Score)> Wave2LadderFile() =>
        LeagueLadderFile.ParseScores(File.ReadAllText(Path.Combine(RepoRoot(), "Fights", "wave2_long_league.md")));

    // The same ladder restricted to the participants present in this build. On a competition
    // base branch (scripts/competition.py base) the holdout packages' files are deleted, so
    // the field is smaller than the ladder; these tests must still pass there.
    private static List<(string Name, double Score)> Wave2Ladder()
    {
        var present = new HashSet<string>(FighterLeague.AllParticipants().Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
        return Wave2LadderFile().Where(x => present.Contains(x.Name)).ToList();
    }

    private static bool Has(params string[] names)
    {
        var present = new HashSet<string>(FighterLeague.AllParticipants().Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
        return names.All(present.Contains);
    }

    // Pool for scoring tests: never contains ExampleBrawler or Flyer (the submissions).
    private static CompetitionPool TinyPool() => new()
    {
        Wave = 3, Source = "test", Frames = 240, Seeds = 2, FirstSeed = 100,
        Dev = { "Brick" }, Holdout = { "Sprinter" }, Terrains = { "flat" },
    };

    // ── Split ────────────────────────────────────────────────────────────────

    [Fact]
    public void Split_IsDeterministic_SnakeDealt_FixedDevNotDealt()
    {
        var ladder = Wave2Ladder();
        var a = CompetitionPool.Split(3, ladder);
        var b = CompetitionPool.Split(3, ladder);
        Assert.Equal(a.Dev, b.Dev);
        Assert.Equal(a.Holdout, b.Holdout);
        output.WriteLine("Dev: " + string.Join(", ", a.Dev));
        output.WriteLine("Holdout: " + string.Join(", ", a.Holdout));

        var fixedDev = CompetitionPool.DefaultAlwaysDev();
        Assert.Equal(7, fixedDev.Count);
        Assert.Contains("ExampleBrawler", fixedDev);
        foreach (var n in fixedDev) { Assert.Contains(n, a.Dev); Assert.DoesNotContain(n, a.Holdout); }

        Assert.Equal(ladder.Count, a.All.Count());
        int devDealt = a.Dev.Count(n => !fixedDev.Contains(n));
        Assert.True(Math.Abs(devDealt - a.Holdout.Count) <= 1, $"{devDealt} vs {a.Holdout.Count}");

        // The snake deal: dealt rank 1 → Dev, 2 → Holdout, 3 → Holdout, 4 → Dev.
        var dealt = ladder.Select(x => x.Name).Where(n => !fixedDev.Contains(n)).ToList();
        Assert.Contains(dealt[0], a.Dev);
        Assert.Contains(dealt[1], a.Holdout);
        Assert.Contains(dealt[2], a.Holdout);
        Assert.Contains(dealt[3], a.Dev);

        // Full field only (not on a competition base branch): Lancer 1 → Dev, Phalanx 2 →
        // Holdout, Harrier 3 → Holdout, Kestrel 4 → Dev; and Sentinel and Mason print as 0.575
        // each, so the ladder's (full-precision) order must survive ParseScores → Split: both
        // Holdout, Sentinel first.
        if (Has("Phalanx", "Harrier", "Sentinel", "Mason"))
        {
            Assert.Contains("Lancer", a.Dev);
            Assert.Contains("Phalanx", a.Holdout);
            Assert.Contains("Harrier", a.Holdout);
            Assert.Contains("Kestrel", a.Dev);
            int si = a.Holdout.IndexOf("Sentinel"), mi = a.Holdout.IndexOf("Mason");
            Assert.True(si >= 0 && mi >= 0, string.Join(", ", a.Holdout));
            Assert.True(si < mi, string.Join(", ", a.Holdout));
        }
    }

    [Fact]
    public void Split_SortsByScore_TiesKeepInputOrder()
    {
        // (a) Tie-free input: the caller's order does not matter.
        var ladder = Wave2Ladder();
        var tieFree = ladder.Select((x, i) => (x.Name, 1.0 - 0.01 * i)).ToList();
        var reversed = Enumerable.Reverse(tieFree).ToList();
        var a = CompetitionPool.Split(3, tieFree);
        var b = CompetitionPool.Split(3, reversed);
        Assert.Equal(a.Dev, b.Dev);
        Assert.Equal(a.Holdout, b.Holdout);

        // (b) Exact tie between the first two dealt names (Lancer and Phalanx on the full
        // field): input order decides, and swapping them in the input swaps their sides.
        var fixedDev = new HashSet<string>(CompetitionPool.DefaultAlwaysDev(), StringComparer.OrdinalIgnoreCase);
        var dealt = tieFree.Select(x => x.Name).Where(n => !fixedDev.Contains(n)).ToList();
        string n1 = dealt[0], n2 = dealt[1];
        var tied = tieFree.Select(x => x.Name == n1 || x.Name == n2 ? (x.Name, 2.0) : x).ToList();
        var t1 = CompetitionPool.Split(3, tied);
        Assert.Contains(n1, t1.Dev);
        Assert.Contains(n2, t1.Holdout);

        var swapped = new List<(string Name, double Score)>(tied);
        int i1 = swapped.FindIndex(x => x.Name == n1), i2 = swapped.FindIndex(x => x.Name == n2);
        (swapped[i1], swapped[i2]) = (swapped[i2], swapped[i1]);
        var t2 = CompetitionPool.Split(3, swapped);
        Assert.Contains(n2, t2.Dev);
        Assert.Contains(n1, t2.Holdout);
    }

    [Fact]
    public void Split_UnknownOrDuplicateName_Throws()
    {
        var ladder = Wave2Ladder();
        Assert.Throws<ArgumentException>(() => CompetitionPool.Split(3, ladder.Append(("NoSuchBot", 0.9)).ToList()));
        Assert.Throws<ArgumentException>(() => CompetitionPool.Split(3, ladder.Append(("lancer", 0.1)).ToList()));
    }

    // ── Pool file ────────────────────────────────────────────────────────────

    [Fact]
    public void Pool_SaveLoad_RoundTrips()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mtile_comp_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var p = CompetitionPool.Split(3, Wave2Ladder(), source: "Fights/wave2_long_league.md");
            p.Terrains.Add("hills");
            p.Frames = 900; p.Seeds = 7; p.FirstSeed = 12345678901234UL;
            string path = Path.Combine(dir, "sub", "wave3.pool.json");
            p.Save(path);
            var q = CompetitionPool.Load(path);
            Assert.Equal(p.Wave, q.Wave);
            Assert.Equal(p.Created, q.Created);
            Assert.Equal(p.Source, q.Source);
            Assert.Equal(p.Dev, q.Dev);
            Assert.Equal(p.Holdout, q.Holdout);
            Assert.Equal(p.Terrains, q.Terrains);
            Assert.Equal(p.Frames, q.Frames);
            Assert.Equal(p.Seeds, q.Seeds);
            Assert.Equal(p.FirstSeed, q.FirstSeed);
            Assert.DoesNotContain("\"All\"", File.ReadAllText(path));
            Assert.Throws<FileNotFoundException>(() => CompetitionPool.Load(Path.Combine(dir, "missing.pool.json")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // ── Ladder parser ────────────────────────────────────────────────────────

    [Fact]
    public void LadderFile_ParsesWave2Ladder_AndRejectsNoLadder()
    {
        var rows = Wave2LadderFile();
        Assert.Equal(19, rows.Count);
        Assert.Equal("Lancer", rows[0].Name);
        Assert.Equal(0.887, rows[0].Score, 6);
        Assert.Equal("Sprinter", rows[^1].Name);
        for (int i = 1; i < rows.Count; i++) Assert.True(rows[i - 1].Score >= rows[i].Score);

        Assert.Throws<FormatException>(() => LeagueLadderFile.ParseScores("# Fighter league\n\nno ladder here\n\n```\nx\n```\n"));
    }

    // ── Holdout files / submissions ──────────────────────────────────────────

    [Fact]
    public void HoldoutFiles_ExistingPackageFiles_BogusThrows()
    {
        string root = RepoRoot();
        // ExampleBrawler is always Dev by rule, so its file is present on every branch.
        var p = new CompetitionPool { Holdout = { "ExampleBrawler" } };
        var files = p.HoldoutFiles(root);
        Assert.Equal(new[] { "Entities/Enemies/Fighters/Packages/ExampleBrawler.cs" }, files);
        Assert.All(files, f => Assert.True(File.Exists(Path.Combine(root, f)), f));

        var bad = new CompetitionPool { Holdout = { "ExampleBrawler", "NoSuchBot" } };
        Assert.ThrowsAny<Exception>(() => bad.HoldoutFiles(root));
    }

    [Fact]
    public void Submissions_AreParticipantsMinusPool()
    {
        var p = TinyPool();
        var subs = p.Submissions();
        var expect = FighterLeague.AllParticipants().Select(x => x.Name).Where(n => n != "Brick" && n != "Sprinter");
        Assert.Equal(expect, subs);
    }

    // ── Scoring ──────────────────────────────────────────────────────────────

    [Fact]
    public void Score_OneVersusPool_IsBitIdentical()
    {
        var pool = TinyPool();
        var a = FighterCompetition.Score(pool, "ExampleBrawler");
        var b = FighterCompetition.Score(pool, "ExampleBrawler");
        Assert.Equal("ExampleBrawler", a.Name);
        Assert.Equal(1 * 2 * 1 * 2, a.Holdout.Played);
        Assert.Equal(1 * 2 * 1 * 2, a.Dev.Played);
        Assert.InRange(a.HoldoutScore, 0, 1);
        Assert.InRange(a.DevScore, 0, 1);
        Assert.Equal(a.DevScore - a.HoldoutScore, a.OverfitGap);
        Assert.Null(a.HeadToHead);

        Assert.Equal(a.HoldoutScore, b.HoldoutScore);
        Assert.Equal(a.DevScore, b.DevScore);
        Assert.Equal(a.Holdout.ScoreSe, b.Holdout.ScoreSe);
        Assert.Equal(a.Dev.ScoreSe, b.Dev.ScoreSe);
        Assert.Equal((a.Holdout.Wins, a.Holdout.Draws, a.Holdout.Losses), (b.Holdout.Wins, b.Holdout.Draws, b.Holdout.Losses));
        Assert.Equal((a.Dev.Wins, a.Dev.Draws, a.Dev.Losses), (b.Dev.Wins, b.Dev.Draws, b.Dev.Losses));

        Assert.Throws<ArgumentException>(() => FighterCompetition.Score(pool, "Brick"));       // in the pool
        Assert.Throws<ArgumentException>(() => FighterCompetition.Score(pool, "NoSuchBot"));
    }

    [Fact]
    public void Run_TwoSubmissions_HeadToHead_AndStableMarkdown()
    {
        var pool = TinyPool();
        var subs = new[] { "ExampleBrawler", "Flyer" };
        var r = FighterCompetition.Run(pool, subs);
        var r2 = FighterCompetition.Run(pool, subs);
        string md = r.Markdown();
        output.WriteLine(md);

        Assert.NotNull(r.HeadToHead);
        Assert.Equal(subs.OrderBy(x => x, StringComparer.Ordinal), r.HeadToHead.Participants.Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(new[] { 1, 2 }, r.Scores.Select(s => s.Rank));
        Assert.All(r.Scores, s => Assert.NotNull(s.HeadToHead));
        Assert.Empty(r.Disqualified);
        Assert.True(r.Scores[0].HoldoutScore >= r.Scores[1].HoldoutScore);

        Assert.Contains("Dev (visible): Brick", md);
        Assert.Contains("Holdout (hidden): Sprinter", md);
        Assert.Contains("## Head-to-head ladder", md);
        Assert.Contains(r.HeadToHead.LadderText(timing: false), md);
        Assert.Equal(md, r2.Markdown());
    }

    [Fact]
    public void Run_OneSubmission_NoHeadToHead()
    {
        var r = FighterCompetition.Run(TinyPool(), new[] { "ExampleBrawler" });
        Assert.Null(r.HeadToHead);
        Assert.Single(r.Scores);
        Assert.Equal(1, r.Scores[0].Rank);
        Assert.Null(r.Scores[0].HeadToHead);
        string md = r.Markdown();
        Assert.Contains("ExampleBrawler", md);
        Assert.DoesNotContain("Head-to-head ladder", md);
    }

    // ── Roll ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Roll_PoolGrowsBySubmissions_AndReSplits()
    {
        var full = Wave2Ladder();
        // Two dealt (non-fixed) names play the wave's submissions: the top one and the last one.
        var fixedDev = new HashSet<string>(CompetitionPool.DefaultAlwaysDev(), StringComparer.OrdinalIgnoreCase);
        var dealt = full.Select(x => x.Name).Where(n => !fixedDev.Contains(n)).ToList();
        string top = dealt[0], last = dealt[^1];
        var subs = new[] { top, last }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var prev = CompetitionPool.Split(3, full.Where(x => x.Name != top && x.Name != last).ToList());
        prev.Frames = 600; prev.Seeds = 4;
        Assert.Equal(subs, prev.Submissions().OrderBy(x => x, StringComparer.Ordinal));

        var next = FighterCompetition.Roll(prev, full, "fresh");
        Assert.Equal(4, next.Wave);
        Assert.Equal("fresh", next.Source);
        Assert.Equal(prev.All.Concat(subs).OrderBy(x => x, StringComparer.Ordinal),
                     next.All.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Contains(top, next.Dev);          // rank 1 of the new ladder
        Assert.Equal(600, next.Frames);
        Assert.Equal(4, next.Seeds);
        Assert.Empty(next.Submissions());

        Assert.Throws<ArgumentException>(() => FighterCompetition.Roll(prev, full.Where(x => x.Name != last).ToList()));
    }
}
