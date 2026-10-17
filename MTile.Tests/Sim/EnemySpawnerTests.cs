using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests.Sim;

// EnemySpawner (Entities/EnemySpawner.cs) and EntityFactory.CreateEnemy: the nest holds
// a live-count cap on its kind within its radius, refills one interval at a time, stops
// when destroyed, and survives snapshot/restore even when recreated by Rehydrate.
// Arena: a floor at row 12 and a full-height wall at columns 10-11 with the player
// parked behind it, so the fight never reaches them. Spawnee is a Turret (rooted, so it
// stays inside the radius for the whole run).
public class EnemySpawnerTests
{
    private const float FloorTopY = 12 * Chunk.TileSize;
    private const float Dt = Simulation.FixedDt;

    private static ChunkMap Arena()
    {
        var rows = new List<string>();
        for (int y = 0; y < 16; y++)
        {
            var sb = new StringBuilder();
            for (int x = 0; x < 48; x++)
                sb.Append(y >= 12 || x == 10 || x == 11 ? 'X' : 'O');
            rows.Add(sb.ToString());
        }
        return SimTerrain.FromAscii(string.Join("\n", rows), originTileX: 0, originTileY: 0);
    }

    private static Vector2 NestPos => new(30 * Chunk.TileSize, FloorTopY - 5 * Chunk.TileSize);

    private static Simulation Make(int cap = 3, float interval = 1f, float radius = 320f, System.Action<Simulation> extra = null)
        => new(Arena(), new Vector2(4 * Chunk.TileSize, FloorTopY - 30f), g =>
        {
            g.SpawnEntity(new EnemySpawner(NestPos, EntityKind.Turret, cap, interval, radius));
            extra?.Invoke(g);
        });

    private static EnemySpawner Nest(Simulation s) => s.Entities.OfType<EnemySpawner>().SingleOrDefault();
    private static List<Entity> Turrets(Simulation s) => s.Entities.Where(e => e.Kind == EntityKind.Turret && !e.IsDead).ToList();
    private static void Run(Simulation s, float seconds) { for (int i = 0; i < (int)(seconds / Dt); i++) s.Step(default); }

    [Fact]
    public void Spawner_CapsAtMaxLive_NeverExceeds()
    {
        var sim = Make(cap: 3, interval: 0.5f);
        int max = 0;
        for (int i = 0; i < 60 * 12; i++) { sim.Step(default); max = System.Math.Max(max, Turrets(sim).Count); }
        Assert.Equal(3, max);
        Assert.Equal(3, Turrets(sim).Count);
    }

    [Fact]
    public void Spawner_FirstSpawnAfterShortDelay_NotAtFrameZero()
    {
        var sim = Make();
        sim.Step(default);
        Assert.Empty(Turrets(sim));
        Run(sim, 1.2f);
        Assert.Single(Turrets(sim));
    }

    [Fact]
    public void Spawner_KillOne_ResumesButNotInstantly()
    {
        var sim = Make(cap: 2, interval: 2f);
        Run(sim, 6f);
        Assert.Equal(2, Turrets(sim).Count);

        Turrets(sim)[0].Health = 0f;
        sim.Step(default);
        Assert.Single(Turrets(sim));
        Run(sim, 1.0f);
        Assert.Single(Turrets(sim));          // half an interval later: still waiting
        Run(sim, 1.3f);
        Assert.Equal(2, Turrets(sim).Count);  // refilled once the interval has run
    }

    [Fact]
    public void Spawner_Destroyed_StopsForGoodAndDespawns()
    {
        var sim = Make(cap: 3, interval: 0.5f);
        Run(sim, 1.2f);
        int before = Turrets(sim).Count;
        Nest(sim).Health = 0f;
        sim.Step(default);
        Assert.Null(Nest(sim));
        foreach (var t in Turrets(sim)) t.Health = 0f;
        Run(sim, 5f);
        Assert.True(before >= 1);
        Assert.Empty(Turrets(sim));
    }

    [Fact]
    public void Spawner_PlayerHitDestroysIt()
    {
        var sim = Make();
        var nest = Nest(sim);
        Assert.True(nest.Faction == Faction.Enemy && nest.Rooted);
        var before = nest.Body.Position;
        nest.OnHit(new Hitbox(nest.Body.Bounds, 1, 5f, new Vector2(900, 0), Faction.Player1, default, Color.White,
                              bodyDamage: 5f), new Hurtbox(nest.Body.Bounds, Faction.Enemy, nest.Id));
        Run(sim, 0.2f);
        Assert.Null(Nest(sim));
    }

    [Fact]
    public void Spawner_StaysPut_AndSpawneesStartInOpenSpace()
    {
        var sim = Make(cap: 3, interval: 0.3f);
        Run(sim, 3f);
        Assert.Equal(NestPos, Nest(sim).Body.Position);
        foreach (var t in Turrets(sim))
            Assert.False(TileQuery.IsSolidAt(sim.Chunks, t.Body.Position.X, t.Body.Position.Y));
    }

    [Fact]
    public void Spawner_IgnoresSameKindOutsideLocalRadius()
    {
        var far = new Vector2(46 * Chunk.TileSize, FloorTopY - 40f);
        var sim = Make(cap: 2, interval: 0.4f, radius: 100f, extra: g =>
        {
            g.SpawnEntity(EntityFactory.CreateEnemy(EntityKind.Turret, far));
            g.SpawnEntity(EntityFactory.CreateEnemy(EntityKind.Turret, far + new Vector2(-20f, 0)));
            g.SpawnEntity(EntityFactory.CreateEnemy(EntityKind.Turret, far + new Vector2(-40f, 0)));
        });
        Run(sim, 6f);
        var near = Turrets(sim).Where(t => Vector2.Distance(t.Body.Position, NestPos) <= 100f).ToList();
        Assert.Equal(2, near.Count);
        Assert.Equal(5, Turrets(sim).Count);
    }

    [Fact]
    public void Spawner_SnapshotRestore_RecreatesDestroyedNestWithConfig()
    {
        var sim = Make(cap: 2, interval: 0.7f, radius: 280f);
        Run(sim, 1.5f);
        var snap = sim.Snapshot();
        int turretsAtSnap = Turrets(sim).Count;
        float timerAtSnap = Nest(sim).Timer;

        Nest(sim).Health = 0f;
        sim.Step(default);
        Assert.Null(Nest(sim));

        sim.Restore(snap);
        var nest = Nest(sim);
        Assert.NotNull(nest);
        Assert.False(nest.IsDestroyed);
        Assert.Equal(EntityKind.Turret, nest.SpawnKind);
        Assert.Equal(2, nest.MaxLive);
        Assert.Equal(0.7f, nest.Interval);
        Assert.Equal(280f, nest.LocalRadius);
        Assert.Equal(NestPos, nest.Body.Position);
        Assert.Equal(timerAtSnap, nest.Timer);
        Assert.Equal(turretsAtSnap, Turrets(sim).Count);

        // Still works: kill a spawnee, it refills.
        Run(sim, 4f);
        Assert.Equal(2, Turrets(sim).Count);
        Turrets(sim)[0].Health = 0f;
        Run(sim, 2f);
        Assert.Equal(2, Turrets(sim).Count);
    }

    [Fact]
    public void Spawner_ReplayAfterRestore_IsDeterministic()
    {
        var a = Make(cap: 3, interval: 0.5f);
        Run(a, 1.2f);
        var snap = a.Snapshot();
        Run(a, 3f);
        ulong ca = a.Checksum();
        a.Restore(snap);
        Run(a, 3f);
        Assert.Equal(ca, a.Checksum());
    }

    [Fact]
    public void CreateEnemy_SubclassRegisteredAndLocalBlueprint()
    {
        var p = new Vector2(100, 100);
        Assert.IsType<SpearmanEnemy>(EntityFactory.CreateEnemy(EntityKind.Spearman, p));
        Assert.IsType<BruteEnemy>(EntityFactory.CreateEnemy(EntityKind.Brute, p));

        var reg = EntityFactory.CreateEnemy(EntityKind.Skirmisher, p);
        Assert.Equal(EntityKind.Skirmisher, reg.Kind);
        Assert.Equal(p, reg.Body.Position);

        var bp = new EnemyBlueprint { Kind = EntityKind.FighterSlot3, Radius = 9f, Health = 2f,
            Movement = () => new() { new EnemyIdleState() }, Actions = () => new() };
        var local = new Dictionary<EntityKind, EnemyBlueprint> { [EntityKind.FighterSlot3] = bp };
        var l = EntityFactory.CreateEnemy(EntityKind.FighterSlot3, p, local);
        Assert.IsType<BlueprintEnemy>(l);
        Assert.Equal(EntityKind.FighterSlot3, l.Kind);

        Assert.Throws<System.InvalidOperationException>(() => EntityFactory.CreateEnemy(EntityKind.Bullet, p));
    }
}
