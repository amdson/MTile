using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;

namespace MTile.Tests;

// Stage.Coop: the two players are allies. No player-faction hit lands on the other
// player, enemies still hurt both, and a dead partner respawns at Simulation.RespawnPoint
// (which a stage checkpoint can move, so it rides the snapshot).
public class CoopModeTests
{
    private static ChunkMap Floor() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXX", originTileX: -4, originTileY: 0);

    private static Simulation Build(bool coop, Vector2? respawn = null)
    {
        var stage = new Stage { Name = "coop-test", Terrain = Floor, PlayerSpawn = new Vector2(40f, 38f), Coop = coop, Populate = _ => { } };
        var sim = new Simulation(new GameConfig { SpawnSecondPlayer = false }, stage);
        sim.AddSecondaryPlayer(new Vector2(100f, 38f));
        if (respawn is Vector2 r) sim.RespawnPoint = r;
        return sim;
    }

    // One CombatSystem pass: `owner`'s hitbox sitting on top of both players, hurtboxes
    // for both. Returns (p1, p2) health afterwards.
    private static (float P1, float P2) Swing(bool coop, Faction owner)
    {
        // Built through a Simulation so the two players hold distinct EntityIds.
        var sim = Build(coop);
        var p1 = sim.Player;
        var p2 = sim.SecondaryPlayers[0].Player;
        p1.Body.Position = new Vector2(40f, 38f);
        p2.Body.Position = new Vector2(48f, 38f);
        var combat = new CombatSystem { Coop = coop };
        var hits = new HitboxWorld();
        var hurts = new HurtboxWorld();
        hurts.Publish(new Hurtbox(p1.Body.Bounds, Faction.Player1, p1.Id));
        hurts.Publish(new Hurtbox(p2.Body.Bounds, Faction.Player2, p2.Id));
        var region = new BoundingBox(0f, 0f, 120f, 80f);
        hits.Publish(new Hitbox(region, hitId: 1, damage: 0.5f, knockbackImpulse: new Vector2(100f, 0f),
                                owner: owner, source: new EntityId(99), debugColor: Color.White));
        var byId = new Dictionary<EntityId, IHittable> { [p1.Id] = p1, [p2.Id] = p2 };
        combat.Apply(Floor(), hits, hurts, id => byId.TryGetValue(id, out var h) ? h : null);
        return (p1.Health, p2.Health);
    }

    [Fact]
    public void Coop_PartnerSlashDoesNotHurt()
    {
        var (p1, p2) = Swing(coop: true, Faction.Player1);
        var full = new PlayerCharacter(Vector2.Zero).MaxHealth;
        Assert.Equal(full, p2);      // partner untouched
        Assert.Equal(full, p1);      // self-immune as ever
    }

    [Fact]
    public void Pvp_PartnerSlashStillHurts()
    {
        var (_, p2) = Swing(coop: false, Faction.Player1);
        Assert.True(p2 < new PlayerCharacter(Vector2.Zero).MaxHealth);
    }

    [Fact]
    public void Coop_EnemyStillHurtsBothPlayers()
    {
        var (p1, p2) = Swing(coop: true, Faction.Enemy);
        var full = new PlayerCharacter(Vector2.Zero).MaxHealth;
        Assert.True(p1 < full, $"p1={p1} p2={p2} full={full}");
        Assert.True(p2 < full);
    }

    [Fact]
    public void Coop_DeadSecondaryRespawnsAtRespawnPoint_AndCountsADeath()
    {
        var sim = Build(coop: true, respawn: new Vector2(60f, 38f));
        var p2 = sim.SecondaryPlayers[0].Player;
        p2.Health = -1f;
        float p1Before = sim.Player.Body.Position.X;
        sim.Step(new PlayerInput());
        Assert.True(p2.IsAlive);
        Assert.Equal(1, sim.PlayerDeaths);
        // Respawned beside the checkpoint: 24 px right of RespawnPoint, i.e. (60+24)-40
        // ahead of where the primary stood. Relative, because both bodies settle on the
        // first frame.
        Assert.InRange(p2.Body.Position.X - sim.Player.Body.Position.X, 40f, 48f);
    }

    [Fact]
    public void NonCoop_DeadSecondaryStaysDead()
    {
        var sim = Build(coop: false);
        var p2 = sim.SecondaryPlayers[0].Player;
        p2.Health = -1f;
        sim.Step(new PlayerInput());
        Assert.False(p2.IsAlive);
        Assert.Equal(0, sim.PlayerDeaths);
    }

    [Fact]
    public void RespawnPoint_RoundTripsThroughSnapshot()
    {
        var sim = Build(coop: true);
        var before = sim.RespawnPoint;
        var snap = sim.Snapshot();
        sim.RespawnPoint = new Vector2(500f, -20f);   // a checkpoint ticker moves it
        sim.Restore(snap);
        Assert.Equal(before, sim.RespawnPoint);

        sim.RespawnPoint = new Vector2(500f, -20f);
        var snap2 = sim.Snapshot();
        sim.RespawnPoint = before;
        sim.Restore(snap2);
        Assert.Equal(new Vector2(500f, -20f), sim.RespawnPoint);
    }
}
