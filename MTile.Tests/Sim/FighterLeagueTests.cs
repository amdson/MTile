using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// The league (MTile.Bench --league → FighterLeague.Run) is deterministic: the same
// field, terrains and frame cap give the same ladder and matrices on every run. Only
// the µs/Decide column is wall-clock, so the comparison drops it (LadderText(false)).
public class FighterLeagueTests(ITestOutputHelper output)
{
    private static LeagueSettings Tiny(string outDir = null)
    {
        var s = new LeagueSettings { Frames = 120, OutDir = outDir };
        s.Include.Add(nameof(ExampleBrawler));
        s.Include.Add("Brick");
        s.Terrains.Add("flat");
        return s;
    }

    [Fact]
    public void League_SameLadderTwice()
    {
        var a = FighterLeague.Run(Tiny());
        var b = FighterLeague.Run(Tiny());
        output.WriteLine(a.LadderText());
        output.WriteLine(a.MatrixText("flat"));

        Assert.Equal(2, a.Bouts.Count);                    // both spawn orders
        Assert.All(a.Standings, s => Assert.Equal(2, s.Played));
        Assert.Equal(a.LadderText(timing: false), b.LadderText(timing: false));
        Assert.Equal(a.MatrixText("flat"), b.MatrixText("flat"));
        Assert.All(a.Standings, s => Assert.True(s.Compute.Calls > 0, $"{s.Participant.Name} was never timed"));
    }

    [Fact]
    public void League_FieldIsPackagesThenRoster_AndOnlyRestrictsBouts()
    {
        var all = FighterLeague.AllParticipants();
        var pk  = FighterPackages.Discover().Select(p => p.Name);
        var ro  = FighterRoster.All.Select(m => m().Name);
        Assert.Equal(pk.Concat(ro), all.Select(p => p.Name));

        var s = new LeagueSettings { Frames = 30 };
        s.Only.Add("Brick");
        s.Terrains.Add("flat");
        var r = FighterLeague.Run(s);
        Assert.Equal(2 * (all.Count - 1), r.Bouts.Count);
        Assert.All(r.Bouts, b => Assert.True(b.LeftName == "Brick" || b.RightName == "Brick"));
    }

    [Fact]
    public void League_SavedBoutReplaysIdentically()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mtile_league_test_" + System.Guid.NewGuid().ToString("N"));
        try
        {
            var r = FighterLeague.Run(Tiny(dir));
            var b = r.Bouts[0];
            Assert.True(File.Exists(b.SavedTo), b.SavedTo);
            Assert.EndsWith($"{b.LeftName}_vs_{b.RightName}_flat.fight.json", b.SavedTo);
            var rep = FightRecord.Load(b.SavedTo).Replay();
            Assert.True(rep.Identical, $"saved league bout diverged at frame {rep.FirstDivergence}");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
