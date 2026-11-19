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
}
