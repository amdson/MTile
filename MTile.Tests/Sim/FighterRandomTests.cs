using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Seeded randomness for fighter brains (FighterSenses.Random, state in the snapshotted
// SenseMemory) and seeded bout conditions (ArenaCondition, the seed in FightRecord).
// The contract: a seed names a bout completely — same seed, same fight, forever; a
// different seed is a different fight; the stream rewinds with a rollback; seed 0 is
// the canonical start every pre-seed fight used; and a league over N seeds plays every
// matchup N times and ranks deterministically.
public class FighterRandomTests(ITestOutputHelper output)
{
    // A brain that walks off its random stream and logs the draws: I0 = draws so far,
    // F0 = the latest, F1 = running sum. The stream moves the body, so it is inside the
    // checksum's reach — a rollback that failed to rewind it would show.
    private sealed class DiceBrain : FighterController
    {
        protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m)
        {
            float r = s.Random();
            m.I0++; m.F0 = r; m.F1 += r;
            return new EnemyInput { MoveDir = new Vector2(r < 0.5f ? -1f : 1f, 0f), AimWorld = s.TargetCoarse().Position };
        }
    }

    private static FighterSpec Dice(string name)
    {
        var s = FighterRoster.Brick();
        s.Name  = name;
        s.Brain = _ => new DiceBrain();
        return s;
    }

    private static Simulation DiceSim(ulong seed, EnemyEntity[] bots)
    {
        var entries = FighterArena.Entries(ArenaCondition.Derive("flat", seed), Dice("DiceL"), Dice("DiceR"));
        return new Simulation(FighterArena.Flat(), FighterArena.PlayerPark, g => FighterArena.Populate(g, entries, null, bots, seed));
    }

    private static List<ulong> Play(Simulation sim, int frames)
    {
        var sums = new List<ulong>(frames);
        for (int f = 0; f < frames; f++) { sim.Step(default); sums.Add(sim.Checksum()); }
        return sums;
    }

    [Fact]
    public void BrainRandom_UnitIsInRange_AndSpread()
    {
        ulong state = BrainRandom.Seed(42);
        double sum = 0; float min = 1f, max = 0f;
        const int N = 20000;
        for (int i = 0; i < N; i++)
        {
            float u = BrainRandom.Unit(ref state);
            Assert.InRange(u, 0f, 0.99999994f);
            sum += u; min = MathF.Min(min, u); max = MathF.Max(max, u);
        }
        Assert.InRange(sum / N, 0.48, 0.52);
        Assert.True(min < 0.01f && max > 0.99f, $"range {min}..{max}");
        Assert.NotEqual(0UL, BrainRandom.Seed(0));
        Assert.NotEqual(BrainRandom.Mix(1, 2), BrainRandom.Mix(2, 1));
    }

    [Fact]
    public void Stream_IsAFunctionOfTheBoutSeed_AndDiffersPerSide()
    {
        var botsA = new EnemyEntity[2];
        var botsB = new EnemyEntity[2];
        var botsC = new EnemyEntity[2];
        var a = Play(DiceSim(3, botsA), 60);
        var b = Play(DiceSim(3, botsB), 60);
        var c = Play(DiceSim(4, botsC), 60);

        Assert.Equal(a, b);                                   // same seed ⇒ same fight
        Assert.NotEqual(a, c);                                // another seed ⇒ another fight
        Assert.Equal(60, botsA[0].Scratch.I0);                // one draw per Decide
        Assert.InRange(botsA[0].Scratch.F0, 0f, 1f);
        Assert.NotEqual(botsA[0].Scratch.F1, botsA[1].Scratch.F1);   // the two sides never share a stream
        Assert.Equal(botsA[0].Scratch.F1, botsB[0].Scratch.F1);
        Assert.NotEqual(botsA[0].Scratch.F1, botsC[0].Scratch.F1);
        output.WriteLine($"seed 3: left sum {botsA[0].Scratch.F1:0.000}, right sum {botsA[1].Scratch.F1:0.000}; seed 4: left sum {botsC[0].Scratch.F1:0.000}");
    }

    [Fact]
    public void Unseeded_StreamSeedsItselfFromTheEntityId_Deterministically()
    {
        // Spawned outside the arena (no SeedBrain call): the first Random() seeds from
        // the entity id, so two sims built the same way agree, and the two entities differ.
        var bots = new EnemyEntity[2];
        var entries = FighterArena.Entries(ArenaCondition.Derive("flat", 0), Dice("DiceL"), Dice("DiceR"));
        Simulation Build(EnemyEntity[] b) => new(FighterArena.Flat(), FighterArena.PlayerPark, g =>
        {
            FighterArena.Populate(g, entries, null, b);
            foreach (var e in b) e.SenseMem.Rng = 0;   // undo the arena's seeding
        });
        var a = Play(Build(bots), 30);
        var bots2 = new EnemyEntity[2];
        var b2 = Play(Build(bots2), 30);
        Assert.Equal(a, b2);
        Assert.Equal(30, bots[0].Scratch.I0);
        Assert.NotEqual(bots[0].Scratch.F1, bots[1].Scratch.F1);
    }

    [Fact]
    public void Rollback_StreamRewindsWithTheSnapshot()
    {
        var bots = new EnemyEntity[2];
        var sim  = DiceSim(5, bots);
        Play(sim, 30);
        var snap = sim.Snapshot();

        var live = Play(sim, 120);
        float liveSum = bots[0].Scratch.F1;
        int   liveDraws = bots[0].Scratch.I0;

        sim.Restore(snap);
        var replay = Play(sim, 120);
        for (int f = 0; f < live.Count; f++)
            Assert.True(live[f] == replay[f], $"replay diverged at step {f} after restore");
        Assert.Equal(liveSum,   bots[0].Scratch.F1);
        Assert.Equal(liveDraws, bots[0].Scratch.I0);
    }

    [Fact]
    public void Condition_SeedZeroIsCanonical_DrawsStandOnTheGround()
    {
        float ts = Chunk.TileSize;
        foreach (var (terrain, make) in FighterArena.Terrains)
        {
            var map = make();
            var c0  = ArenaCondition.Derive(terrain, 0);
            Assert.True(c0.IsCanonical);
            Assert.Equal(FighterArena.LeftSpawn,            c0.LeftSpawn(map));
            Assert.Equal(FighterArena.RightSpawnFor(terrain), c0.RightSpawn(map));
            // GroundSpawn itself reproduces the legacy spots at their columns.
            Assert.Equal(FighterArena.LeftSpawn,              FighterArena.GroundSpawn(map, FighterArena.LeftSpawn.X));
            Assert.Equal(FighterArena.RightSpawnFor(terrain), FighterArena.GroundSpawn(map, FighterArena.RightSpawn.X));

            var seps = new HashSet<float>();
            for (ulong seed = 1; seed <= 40; seed++)
            {
                var c = ArenaCondition.Derive(terrain, seed);
                Assert.Equal(c, ArenaCondition.Derive(terrain, seed));
                Assert.InRange(c.Separation, ArenaCondition.MinSeparation, ArenaCondition.MaxSeparation);
                seps.Add(c.Separation);
                foreach (var p in new[] { c.LeftSpawn(map), c.RightSpawn(map) })
                {
                    Assert.InRange(p.X, FighterArena.PlayerPark.X + 300f, FighterArena.Width * ts - 2 * ts);
                    int gtx = (int)MathF.Floor(p.X / ts);
                    int row = (int)MathF.Round((p.Y + 14f) / ts);
                    Assert.True(map.GetCellState(gtx, row) == TileState.Solid, $"{terrain} seed {seed}: nothing under {p}");
                    Assert.True(map.GetCellState(gtx, row - 1) != TileState.Solid, $"{terrain} seed {seed}: {p} is inside the ground");
                    Assert.True(map.GetCellState(gtx, row - 2) != TileState.Solid, $"{terrain} seed {seed}: {p} is inside the ground");
                }
                Assert.True(c.RightSpawn(map).X - c.LeftSpawn(map).X == c.Separation);
            }
            Assert.True(seps.Count > 10, $"{terrain}: only {seps.Count} distinct separations in 40 seeds");
        }
        for (ulong s = 1; s <= 5; s++) output.WriteLine(ArenaCondition.Derive("hills", s).ToString());
    }

    [Fact]
    public void Record_SeedSurvivesSaveLoad_AndReplays()
    {
        var pkg = FighterPackages.Find(nameof(ExampleBrawler));
        var rec9 = FightRecord.Describe("hills", pkg.Spec(), FighterRoster.Brick(), 9, 300);
        var rec0 = FightRecord.Describe("hills", pkg.Spec(), FighterRoster.Brick(), 0, 300);
        Assert.Equal(9UL, rec9.Seed);
        Assert.Equal(nameof(ExampleBrawler), rec9.Entries[0].Spec.Package);
        Assert.True(rec9.Entries[0].X != rec0.Entries[0].X || rec9.Entries[1].X != rec0.Entries[1].X,
                    "seed 9 placed the fighters exactly where seed 0 does");
        rec9.Record();
        rec0.Record();
        Assert.NotEqual(rec9.Checksums, rec0.Checksums);

        string path = Path.Combine(Path.GetTempPath(), $"mtile_seeded_{Guid.NewGuid():N}.fight.json");
        try
        {
            rec9.Save(path);
            var loaded = FightRecord.Load(path);
            Assert.Equal(9UL, loaded.Seed);
            Assert.Contains("seed 9", loaded.Summary());
            var rep = loaded.Replay();
            Assert.True(rep.Identical, $"seeded replay diverged at frame {rep.FirstDivergence}");
        }
        finally { File.Delete(path); }

        // The viewer's stage (Game1 --fight) carries the seed too.
        var bots  = new EnemyEntity[2];
        var sim   = new Simulation(new GameConfig(), rec9.ToStage(bots));
        for (int f = 0; f < rec9.Checksums.Count; f++)
        {
            sim.Step(default);
            Assert.True(sim.Checksum() == rec9.Checksums[f], $"stage sim diverged from the arena at frame {f + 1}");
        }
        output.WriteLine(rec9.Summary() + " — " + rec9.Notes);
    }

    [Fact]
    public void League_SeedsMultiplyBouts_AndRankDeterministically()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mtile_seeded_league_" + Guid.NewGuid().ToString("N"));
        LeagueSettings Settings(string outDir)
        {
            var s = new LeagueSettings { Frames = 120, Seeds = 3, FirstSeed = 0, OutDir = outDir };
            s.Include.Add(nameof(ExampleBrawler));
            s.Include.Add("Brick");
            s.Terrains.Add("flat");
            return s;
        }
        try
        {
            var a = FighterLeague.Run(Settings(dir));
            var b = FighterLeague.Run(Settings(null));
            output.WriteLine(a.LadderText(timing: false));
            output.WriteLine(a.SeedTableText());
            output.WriteLine(a.MatrixText("flat"));

            Assert.Equal(new ulong[] { 0, 1, 2 }, a.Seeds);
            Assert.Equal(6, a.Bouts.Count);                      // 2 orders × 3 seeds
            Assert.Equal(2, a.Matchups);
            Assert.All(a.Standings, s => Assert.Equal(6, s.Played));
            Assert.All(a.Standings, s => Assert.Equal(3, s.ScoreBySeed.Length));
            Assert.All(a.Standings, s => Assert.InRange(s.Score, s.ScoreMin - 1e-9, s.ScoreMax + 1e-9));
            Assert.Equal(a.LadderText(timing: false), b.LadderText(timing: false));
            Assert.Equal(a.MatrixText("flat"), b.MatrixText("flat"));
            Assert.Equal(a.SeedTableText(), b.SeedTableText());
            Assert.Contains("±se", a.LadderText());

            LeagueBout s2 = null;
            foreach (var x in a.Bouts) if (x.Seed == 2) { s2 = x; break; }
            Assert.NotNull(s2);
            Assert.EndsWith($"{s2.LeftName}_vs_{s2.RightName}_flat_s2.fight.json", s2.SavedTo);
            var loaded = FightRecord.Load(s2.SavedTo);
            Assert.Equal(2UL, loaded.Seed);
            Assert.True(loaded.Replay().Identical);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
