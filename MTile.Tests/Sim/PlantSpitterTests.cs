using System.Linq;
using Xunit;
using static MTile.Tests.Sim.PlantTestArena;

namespace MTile.Tests.Sim;

// The spitter (Plans/PLANT_SEEDS_PLAN.md §4.1): mature and awake, it spits poke-tier balls
// at the nearest opposing player in range with a sightline — never at its owner, never
// while growing or buried, never through a wall.
public class PlantSpitterTests
{
    private const int Row = FloorRow - 1;
    private const int Cx  = 10;

    private static (Simulation sim, SpitterPlant plant) Planted(int p2Col)
    {
        SpitterPlant plant = null;
        var sim = Make(g =>
        {
            if (p2Col >= 0) g.AddSecondaryPlayer(Spawn(p2Col));
            plant = Spitter(g, Cx, Row, g.Player);
            g.SpawnEntity(plant);
        });
        return (sim, plant);
    }

    private static int Spits(Simulation s) => s.Entities.Count(e => e is EnergyBallProjectile && !e.IsDead);

    [Fact]
    public void ShotIsPokeTier()
        => Assert.True(PlantTuning.SpitterShotDamage <= 0.3f);

    [Fact]
    public void Mature_SpitsAtTheOpponent_AndHurtsThem()
    {
        var (sim, plant) = Planted(p2Col: 24);   // 14 tiles away
        var p2 = sim.SecondaryPlayers[0].Player;
        Run2(sim, PlantTuning.SpitterGrowSeconds - 0.5f);
        Assert.Equal(0, Spits(sim));              // a seedling does nothing
        Assert.Equal(p2.MaxHealth, p2.Health);

        int seen = 0;
        float minHp = p2.Health;
        for (int i = 0; i < (int)(4f / Dt); i++)
        {
            sim.Step(default, default);
            seen = System.Math.Max(seen, Spits(sim));
            minHp = System.Math.Min(minHp, p2.Health);
        }
        Assert.True(seen >= 1);
        Assert.True(minHp < p2.MaxHealth, "the spit should land on P2");
        Assert.Equal(sim.Player.MaxHealth, sim.Player.Health);   // never the owner
    }

    [Fact]
    public void FiresNoMoreThanOnceAPeriod()
    {
        var (sim, plant) = Planted(p2Col: 22);
        Run2(sim, PlantTuning.SpitterGrowSeconds + 0.05f);
        int spawned = 0;
        var known = new System.Collections.Generic.HashSet<EntityId>();
        float window = 6f;
        for (int i = 0; i < (int)(window / Dt); i++)
        {
            sim.Step(default, default);
            foreach (var e in sim.Entities)
                if (e is EnergyBallProjectile && known.Add(e.Id)) spawned++;
        }
        Assert.InRange(spawned, (int)(window / PlantTuning.SpitterShotPeriod) - 1,
                                (int)(window / PlantTuning.SpitterShotPeriod) + 1);
    }

    [Fact]
    public void OwnerAlone_IsNeverTargeted()
    {
        var (sim, plant) = Planted(p2Col: -1);
        sim.Player.Body.Position = Spawn(16);   // in range, clear sightline
        Run(sim, PlantTuning.SpitterGrowSeconds + 4f);
        Assert.True(plant.IsMature);
        Assert.Equal(0, Spits(sim));
        Assert.False(plant.WindingUp);
    }

    [Fact]
    public void OpponentOutOfRange_OrBehindAWall_IsNotTargeted()
    {
        var (sim, plant) = Planted(p2Col: 40);   // 30 tiles: out of range
        Run2(sim, PlantTuning.SpitterGrowSeconds + 3f);
        Assert.Equal(0, Spits(sim));

        var (sim2, _) = Planted(p2Col: 22);
        for (int y = Row - 6; y <= Row; y++) sim2.Chunks.EditorSetCell(16, y, TileState.Solid, TileType.Stone);
        Run2(sim2, PlantTuning.SpitterGrowSeconds + 3f);
        Assert.Equal(0, Spits(sim2));
    }

    [Fact]
    public void Buried_DoesNotSpit()
    {
        var (sim, plant) = Planted(p2Col: 22);
        sim.Chunks.EditorSetCell(Cx, Row, TileState.Solid, TileType.Stone);
        Run2(sim, PlantTuning.SpitterGrowSeconds + 3f);
        Assert.False(plant.IsMature);   // growth froze under the block
        Assert.Equal(0, Spits(sim));
    }

    // ── The shot hits like S1 and ends on impact ───────────────────────────────
    private static (Simulation sim, PlayerCharacter p2) ShotArena(int shooterCol, int p2Col, out SpitProjectile shot,
                                                                  float dirX = 1f)
    {
        SpitProjectile made = null;
        var sim = Make(g =>
        {
            g.AddSecondaryPlayer(Spawn(p2Col));
            made = new SpitProjectile(Spawn(shooterCol), new Microsoft.Xna.Framework.Vector2(dirX, 0f),
                                      g.HitIds.Next(), g.Player.Faction,
                                      PlantTuning.SpitterShotSpeed, PlantTuning.SpitterShotDamage,
                                      PlantTuning.SpitterShotLifetime) { Team = g.Player.Team };
            g.SpawnEntity(made);
        });
        shot = made;
        return (sim, sim.SecondaryPlayers[0].Player);
    }

    [Fact]
    public void Shot_EndsTheFrameItHitsTheOpponent_WithS1Knockback_Hitstun_AndPokeDamage()
    {
        var (sim, p2) = ShotArena(12, 16, out var shot);
        float vx0 = p2.Body.Velocity.X;
        int hitFrame = -1;
        for (int i = 0; i < 120 && hitFrame < 0; i++)
        {
            sim.Step(default, default);
            if (p2.Health < p2.MaxHealth) hitFrame = i;
        }
        Assert.True(hitFrame >= 0, "the shot should land");
        Assert.True(shot.IsDead || !sim.Entities.Contains(shot), "the shot is removed on the frame it hits");
        Assert.Equal(PlantTuning.SpitterShotDamage, p2.MaxHealth - p2.Health, 3);
        Assert.True(p2.Combat.HitstunActive);
        float x0 = p2.Body.Position.X, dvx = p2.Body.Velocity.X - vx0;
        for (int i = 0; i < 12; i++) { sim.Step(default, default); dvx = System.Math.Max(dvx, p2.Body.Velocity.X - vx0); }
        Assert.InRange(dvx, 60f, 400f);                                   // pushed away from the spitter, S1-sized
        for (int i = 0; i < 90; i++) sim.Step(default, default);
        Assert.DoesNotContain(sim.Entities, e => e is SpitProjectile && !e.IsDead);   // did not fly on through
        Assert.Equal(sim.Player.MaxHealth, sim.Player.Health);
    }

    [Fact]
    public void Shot_StoppedByTerrain_IsRemoved()
    {
        var (sim, p2) = ShotArena(12, 30, out var shot);
        for (int y = Row - 6; y <= Row; y++) sim.Chunks.EditorSetCell(15, y, TileState.Solid, TileType.Stone);
        for (int i = 0; i < 120; i++) sim.Step(default, default);
        Assert.True(shot.IsDead || !sim.Entities.Contains(shot));
        Assert.Equal(p2.MaxHealth, p2.Health);
    }

    [Fact]
    public void Shot_NeverHitsOrBurstsOnItsOwner()
    {
        var (sim, p2) = ShotArena(12, 40, out var shot, dirX: 1f);
        sim.Player.Body.Position = Spawn(12);   // standing in the shot's mouth
        int bursts = 0;
        sim.OnShotBurst += (id, pos, team) => bursts++;
        for (int i = 0; i < 20; i++) sim.Step(default, default);
        Assert.False(shot.IsDead);
        Assert.Equal(0, bursts);
        Assert.Equal(sim.Player.MaxHealth, sim.Player.Health);
    }

    [Fact]
    public void Shot_RaisesOneBurstOnImpact()
    {
        var (sim, p2) = ShotArena(12, 16, out var shot);
        int bursts = 0;
        sim.OnShotBurst += (id, pos, team) => bursts++;
        for (int i = 0; i < 120; i++) sim.Step(default, default);
        Assert.Equal(1, bursts);
    }

    [Fact]
    public void Shot_SnapshotRestoreInFlight_ReplaysIdentically()
    {
        var (sim, p2) = ShotArena(12, 20, out var shot);
        for (int i = 0; i < 10; i++) sim.Step(default, default);
        var snap = sim.Snapshot();
        var a = new System.Collections.Generic.List<(ulong, float)>();
        for (int i = 0; i < 90; i++) { sim.Step(default, default); a.Add((sim.Checksum(), p2.Health)); }
        sim.Restore(snap);
        for (int i = 0; i < 90; i++)
        {
            sim.Step(default, default);
            Assert.Equal(a[i], (sim.Checksum(), p2.Health));
        }
        Assert.True(p2.Health < p2.MaxHealth);
    }
}
