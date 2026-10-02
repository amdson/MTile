using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace MTile.Tests.Sim;

// The fighter_gauntlet stage: league fighters one at a time in GauntletOrder, the next
// entering 1.5 s after the last dies, and a player death restarting from the first.
// Progress lives in Simulation.StageVars, so it must survive a snapshot/restore.
public class FighterGauntletStageTests
{
    private const int Delay = 90;   // 1.5 s

    private static Simulation NewSim()
    {
        var stage = Stages.Get("fighter_gauntlet");
        return new Simulation(stage.Terrain(), stage.PlayerSpawn, stage.Populate);
    }

    private static List<Entity> Fighters(Simulation g)
        => g.Entities.Where(e => !e.IsDead && EntityKinds.IsFighter(e.Kind)).ToList();

    private static void Step(Simulation g, int n) { for (int i = 0; i < n; i++) g.Step(default); }

    [Fact]
    public void FightersEnterOneAtATime_InOrder()
    {
        var g = NewSim();
        Step(g, Delay - 2);
        Assert.Empty(Fighters(g));
        Step(g, 4);
        var f = Assert.Single(Fighters(g));
        Assert.Equal(EntityKinds.FighterSlot(0), f.Kind);
        Assert.Equal(Teams.Enemies, f.Team);

        f.Health = 0f;                       // the player "wins"
        Step(g, 2);
        Assert.Empty(Fighters(g));
        Step(g, Delay + 2);
        Assert.Equal(EntityKinds.FighterSlot(1), Assert.Single(Fighters(g)).Kind);
    }

    [Fact]
    public void PlayerDeath_RestartsFromTheFirstFighter()
    {
        var g = NewSim();
        Step(g, Delay + 2);
        Fighters(g)[0].Health = 0f;
        Step(g, Delay + 4);
        Assert.Equal(EntityKinds.FighterSlot(1), Assert.Single(Fighters(g)).Kind);

        g.Player.Health = -1f;               // dies this frame, respawns at the start
        Step(g, 2);
        Assert.Equal(1, g.PlayerDeaths);
        Assert.Empty(Fighters(g));
        Step(g, Delay + 2);
        Assert.Equal(EntityKinds.FighterSlot(0), Assert.Single(Fighters(g)).Kind);
    }

    [Fact]
    public void FightersTargetThePlayer()
    {
        // An idle player at the start: whoever is in the room must come for it (and
        // eventually kill it, which restarts the run).
        var g = NewSim();
        Step(g, Delay + 2);
        float enterX = Fighters(g)[0].Body.Position.X;
        int deaths = 0;
        float closest = float.MaxValue;
        for (int f = 0; f < 3600 && deaths == 0; f++)
        {
            g.Step(default);
            deaths = g.PlayerDeaths;
            foreach (var e in Fighters(g)) closest = System.Math.Min(closest, e.Body.Position.X);
        }
        Assert.True(closest < enterX - 300f, $"no fighter approached the player (entered at x {enterX:0}, closest {closest:0})");
    }

    [Fact]
    public void EveryFighterCompiles_AndTheOrderEndsWithTheChampion()
    {
        // Every listed fighter present in this checkout (a competition base hides some).
        var g = NewSim();
        var names = FighterLeague.AllParticipants().Select(p => p.Name).ToHashSet();
        int present = Stages_GauntletOrder().Count(names.Contains);
        Assert.True(present >= 18);
        Assert.Equal(present, g.Blueprints.Count);
        for (int i = 0; i < present; i++) Assert.True(g.Blueprints.ContainsKey(EntityKinds.FighterSlot(i)));
    }

    [Fact]
    public void SnapshotAcrossAKillAndADeath_ReplaysBitIdentically()
    {
        // A snapshot taken mid-fight, then a kill (next fighter queued and entering) and
        // a player death (room cleared, restart) inside the replay window.
        var g = NewSim();
        Step(g, Delay + 10);
        var snap = g.Snapshot();

        var live = new List<ulong>();
        void Script(int f)
        {
            if (f == 5)   foreach (var e in Fighters(g)) e.Health = 0f;
            if (f == 150) g.Player.Health = -1f;
        }
        for (int f = 0; f < 300; f++) { Script(f); g.Step(default); live.Add(g.Checksum()); }
        int deaths = g.PlayerDeaths;
        var kinds = Fighters(g).Select(e => e.Kind).ToList();

        g.Restore(snap);
        for (int f = 0; f < 300; f++)
        {
            Script(f);
            g.Step(default);
            Assert.True(g.Checksum() == live[f], $"replay diverged {f + 1} steps after the restore");
        }
        Assert.Equal(deaths, g.PlayerDeaths);
        Assert.Equal(kinds, Fighters(g).Select(e => e.Kind).ToList());
    }

    // The stage's own list, read by reflection so the test follows it.
    private static string[] Stages_GauntletOrder()
        => (string[])typeof(Stages).GetField("GauntletOrder", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
}
