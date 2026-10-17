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

    private static bool BodyClear(ChunkMap c, Vector2 p, float r = 16f)
    {
        int T = Chunk.TileSize;
        for (int a = (int)System.MathF.Floor((p.X - r) / T); a <= (int)System.MathF.Ceiling((p.X + r) / T) - 1; a++)
            for (int b = (int)System.MathF.Floor((p.Y - r) / T); b <= (int)System.MathF.Ceiling((p.Y + r) / T) - 1; b++)
                if (c.GetCellState(a, b) == TileState.Solid) return false;
        return true;
    }

    [Fact]
    public void SecondSpawn_IsExplicit_Clear_AndStandsOnFloor()
    {
        var stage = Stages.Get("depths");
        Assert.NotNull(stage.SecondSpawn);
        var g = new Simulation(new GameConfig { SpawnSecondPlayer = true }, stage);
        var p2 = g.SecondaryPlayers[0].Player;
        Assert.Equal(stage.SecondSpawn.Value, p2.Body.Position);
        Assert.NotEqual(stage.PlayerSpawn, p2.Body.Position);
        Assert.True(BodyClear(g.Chunks, p2.Body.Position), "P2 spawn overlaps rock");
        Assert.Equal(TileState.Solid, g.Chunks.GetCellState(
            (int)System.MathF.Floor(p2.Body.Position.X / Chunk.TileSize),
            (int)System.MathF.Floor((p2.Body.Position.Y + 16f) / Chunk.TileSize)));
    }

    [Fact]
    public void EntryPoints_AreClear_InEveryRoom()
    {
        var map = Stages.DepthsMapCached;
        var chunks = DepthsLayout.MakeChunks(map);
        foreach (var r in map.Rooms)
        {
            Assert.True(BodyClear(chunks, r.Entry), $"room {r.Index} Entry blocked");
            Assert.True(BodyClear(chunks, r.Entry2), $"room {r.Index} Entry2 blocked");
        }
    }

    [Fact]
    public void Terrain_IsFreshPerCall_AndMapBuiltOnce()
    {
        var stage = Stages.Get("depths");
        var a = stage.Terrain();
        var b = stage.Terrain();
        Assert.NotSame(a, b);
        int x = (int)(stage.PlayerSpawn.X / Chunk.TileSize), y = (int)(stage.PlayerSpawn.Y / Chunk.TileSize) + 2;
        Assert.Equal(TileState.Solid, a.GetCellState(x, y));
        a.BreakCell(x, y);
        Assert.Equal(TileState.Solid, b.GetCellState(x, y));   // b unaffected by a's edit
        Assert.Equal(TileState.Solid, stage.Terrain().GetCellState(x, y));
        Assert.Same(Stages.DepthsMapCached, Stages.DepthsMapCached);
    }

    [Fact]
    public void CoopRespawn_OfPartner_IsNeverInRock_InAnyRoom()
    {
        var g = NewSim();
        var p2 = g.SecondaryPlayers.Count > 0 ? g.SecondaryPlayers[0].Player : g.AddSecondaryPlayer(Stages.Get("depths").SecondSpawn.Value).Player;
        foreach (var r in Stages.DepthsMapCached.Rooms)
        {
            g.RespawnPoint = r.Entry;
            p2.Health = 0f;
            g.Step(default);
            Assert.True(BodyClear(g.Chunks, p2.Body.Position), $"room {r.Index}: partner respawned in rock at {p2.Body.Position}");
        }
    }
}
