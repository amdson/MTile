using System.Linq;
using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests.Sim;

// The "depths" co-op stage: it registers and builds, every plan entry names a creatable
// enemy, entering a room spawns its first wave and moves the checkpoint, and clearing the
// last wave breaks the exit door open and hands over to the next room.
public class DepthsStageTests
{
    private static Simulation NewSim()
    {
        var stage = Stages.Get("depths");
        return new Simulation(new GameConfig(), stage);
    }

    private static void Step(Simulation g, int n) { for (int i = 0; i < n; i++) g.Step(default); }

    private static DepthsMap Layout()
        => DepthsLayout.Build(7, Stages.DepthsShapes());

    private static int Live(Simulation g, Rectangle interior)
        => g.Entities.Count(e => !e.IsDead && Stages.DepthsIsHostile(e)
            && e.Body.Position.X >= interior.Left * Chunk.TileSize && e.Body.Position.X <= interior.Right * Chunk.TileSize);

    private static void Teleport(Simulation g, Vector2 p) { g.Player.Body.Position = p; g.Player.Body.Velocity = Vector2.Zero; }

    // A standing spot inside the room, well clear of its entrance (FloorSpots are 6+ tiles out).
    private static Vector2 Inside(DepthsRoom r) => r.FloorSpots[0] - new Vector2(0f, 6f);

    // Kill everything hostile (nests included) until the room's current wave is gone.
    private static void KillAll(Simulation g)
    {
        foreach (var e in g.Entities.ToList()) if (Stages.DepthsIsHostile(e)) e.Health = 0f;
    }

    [Fact]
    public void Registers_AndBuildsASim()
    {
        var stage = Stages.Get("depths");
        Assert.True(stage.Coop);
        var g = NewSim();
        Assert.True(g.Coop);
        Step(g, 5);
        Assert.Equal(0, g.StageVars[0]);
    }

    [Fact]
    public void EveryPlanEntry_IsCreatable()
    {
        var g = NewSim();   // Populate compiles the fighters into g.Blueprints
        var league = FighterLeague.AllParticipants();
        foreach (var room in Stages.DepthsPlan)
            foreach (var w in room.Waves)
            {
                foreach (var e in w.Enemies) Check(g, league, e.Kind, e.Fighter, room.Name);
                foreach (var n in w.Nests)   Check(g, league, n.Kind, n.Fighter, room.Name);
            }
    }

    private static void Check(Simulation g, System.Collections.Generic.List<LeagueParticipant> league, EntityKind k, string fighter, string room)
    {
        var kind = Stages.DepthsKind(k, fighter);
        if (fighter != null && !g.Blueprints.ContainsKey(kind))
        {
            Assert.DoesNotContain(league, p => string.Equals(p.Name, fighter, System.StringComparison.OrdinalIgnoreCase));
            return;   // holdout package missing from this checkout: skipped at spawn
        }
        var e = EntityFactory.CreateEnemy(kind, new Vector2(0f, 0f), g.Blueprints);
        Assert.NotNull(e);
    }

    [Fact]
    public void EnteringRoom0_SpawnsWave0_AndSetsCheckpoint()
    {
        var g = NewSim();
        var rooms = Layout().Rooms;
        Step(g, 3);
        Assert.Equal(0, g.StageVars[1]);
        var r = rooms[0];
        Teleport(g, Inside(r));
        Step(g, 5);
        Assert.Equal(1, g.StageVars[1]);
        Assert.Equal(r.Entry, g.RespawnPoint);
        Assert.True(Live(g, r.Interior) >= Stages.DepthsPlan[0].Waves[0].Enemies.Sum(e => e.Count));
    }

    [Fact]
    public void ClearingRoom0_AdvancesWaves_OpensDoor_AndRoom1ActivatesCheckpoint()
    {
        var g = NewSim();
        var rooms = Layout().Rooms;
        var r0 = rooms[0];
        Teleport(g, Inside(r0));
        Step(g, 5);
        int waves = Stages.DepthsPlan[0].Waves.Length;
        for (int w = 0; w < waves; w++)
        {
            Assert.Equal(w, g.StageVars[2]);
            KillAll(g);
            Step(g, 120);   // 1.5 s delay + spawn
        }
        Assert.Equal(1, g.StageVars[0]);
        Assert.Equal(0, g.StageVars[1]);
        Assert.NotEmpty(r0.ExitDoor);
        foreach (var c in r0.ExitDoor) Assert.Equal(TileState.Empty, g.Chunks.GetCellState(c.X, c.Y));

        var r1 = rooms[1];
        Teleport(g, Inside(r1));
        Step(g, 5);
        Assert.Equal(1, g.StageVars[1]);
        Assert.Equal(r1.Entry, g.RespawnPoint);
    }

    [Fact]
    public void EveryRoom_ActivatesAndSpawnsEveryWave()
    {
        var rooms = Layout().Rooms;
        Assert.Equal(Stages.DepthsPlan.Length, rooms.Count);
        for (int i = 0; i < rooms.Count; i++)
        {
            var g = NewSim();
            g.StageVars[0] = i;
            var r = rooms[i];
            Teleport(g, Inside(r));
            Step(g, 5);
            for (int w = 0; w < Stages.DepthsPlan[i].Waves.Length; w++)
            {
                Assert.Equal(1, g.StageVars[1]);
                Assert.Equal(w, g.StageVars[2]);
                Assert.True(Live(g, r.Interior) > 0, $"room {i} wave {w} empty");
                KillAll(g);
                Step(g, 120);
            }
            Assert.Equal(i + 1, g.StageVars[0]);
        }
    }
}
