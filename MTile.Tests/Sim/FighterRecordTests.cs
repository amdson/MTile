using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// A saved fight (FightRecord) must be a complete description of the bout: the spec data
// form round-trips to the same compiled fighter, a record saved to disk and loaded back
// replays to the same checksums and result, and — the one that matters for viewing —
// the Stage the record builds for Game1 produces the SAME sim, frame for frame, as the
// headless arena that scored it.
public class FighterRecordTests(ITestOutputHelper output)
{
    private static FightRecord BrickVsSprinter(int frames = 300) =>
        FightRecord.Describe("flat", new[]
        {
            new ArenaEntry(FighterRoster.Brick(),    FighterArena.LeftSpawn,  1),
            new ArenaEntry(FighterRoster.Sprinter(), FighterArena.RightSpawn, 2),
        }, FighterArena.PlayerPark, frames);

    [Fact]
    public void SpecDto_RoundTripsToTheSameCompiledFighter()
    {
        var model = new PhysicsCostModel();
        foreach (var make in FighterRoster.All)
        {
            var spec = make();
            var back = FighterSpecDto.From(spec).ToSpec();
            back.Kind = spec.Kind;
            var a = FighterCompiler.Compile(spec, model);
            var b = FighterCompiler.Compile(back, model);
            Assert.True(a.IsValid && b.IsValid, spec.Name);
            Assert.Equal(a.Total, b.Total);
            Assert.Equal(FighterSpecDto.BrainOf(spec), FighterSpecDto.BrainOf(back));
            Assert.Equal(spec.Actions.Count, back.Actions.Count);
            for (int i = 0; i < spec.Actions.Count; i++)
                Assert.Equal(spec.Actions[i], back.Actions[i]);
        }
    }

    [Fact]
    public void SaveLoadReplay_IsBitIdentical()
    {
        var rec = BrickVsSprinter();
        var res = rec.Record();
        Assert.Equal(res.Frames, rec.Checksums.Count);
        Assert.True(rec.Checksums.Count > 0);

        string path = Path.Combine(Path.GetTempPath(), $"mtile_fight_{System.Guid.NewGuid():N}.fight.json");
        try
        {
            rec.Save(path);
            var loaded = FightRecord.Load(path);
            Assert.Equal(rec.Summary(), loaded.Summary());

            var report = loaded.Replay();
            Assert.True(report.Identical, $"replay diverged at frame {report.FirstDivergence}");
            Assert.Equal(res.WinnerTeam, report.Result.WinnerTeam);
            Assert.Equal(res.HealthLeft, report.Result.HealthLeft);
            output.WriteLine($"{loaded.Summary()} — {new FileInfo(path).Length} bytes, {loaded.Checksums.Count} checksums");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Replay_NamesTheFirstDivergentFrame()
    {
        var rec = BrickVsSprinter(120);
        rec.Record();
        rec.Checksums[59] ^= 1;   // corrupt frame 60
        var report = rec.Replay();
        Assert.False(report.Identical);
        Assert.Equal(60, report.FirstDivergence);
    }

    [Fact]
    public void GameStage_ProducesTheSameFightAsTheArena()
    {
        // What the viewer shows is built through Simulation(GameConfig, Stage) with the
        // record's stage; what the arena scored went through the headless ctor. Same
        // checksums every frame ⇒ the two are one fight.
        var rec = BrickVsSprinter(240);
        rec.Record();

        var bots  = new EnemyEntity[rec.Entries.Count];
        var stage = rec.ToStage(bots);
        var sim   = new Simulation(new GameConfig(), stage);
        Assert.All(bots, b => Assert.NotNull(b));

        for (int f = 0; f < rec.Checksums.Count; f++)
        {
            sim.Step(default);
            if (sim.Checksum() != rec.Checksums[f])
                Assert.Fail($"stage sim diverged from the arena at frame {f + 1}");
        }
        output.WriteLine($"stage matched the arena across {rec.Checksums.Count} frames; " +
                         $"bots: {bots[0].Health:0.0} hp / {bots[1].Health:0.0} hp");
    }
}
