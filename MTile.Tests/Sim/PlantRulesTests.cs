using System.Linq;
using Xunit;
using static MTile.Tests.Sim.PlantTestArena;

namespace MTile.Tests.Sim;

// Planting legality, cap and cost through the one entry point (SeedPlanting.TryPlant /
// PlantRules.Evaluate). Plans/PLANT_SEEDS_PLAN.md §2.1, §2.10, §4 "Shared rules", §6.1.
public class PlantRulesTests
{
    private const int Row = FloorRow - 1;   // the open row on top of the floor

    [Fact]
    public void DirtFloor_IsLegal_StoneFloor_IsNot()
    {
        var c = Terrain();
        Assert.True(PlantRules.TryDirtSite(c, 5, Row, out var site));
        Assert.Equal(PlantRoot.Below, site.Root);
        Assert.False(PlantRules.TryDirtSite(c, 32, Row, out _));   // stone surface
        Assert.False(PlantRules.TryDirtSite(c, 5, Row - 3, out _)); // mid-air
        Assert.False(PlantRules.TryDirtSite(c, 5, FloorRow, out _)); // inside the floor
    }

    [Fact]
    public void DirtWall_RootsSideways()
    {
        var c = Terrain();
        c.EditorSetCell(10, Row - 4, TileState.Solid, TileType.Dirt);   // a dirt block in the air
        Assert.True(PlantRules.TryDirtSite(c, 11, Row - 4, out var right));
        Assert.Equal(PlantRoot.Left, right.Root);
        Assert.True(PlantRules.TryDirtSite(c, 10, Row - 3, out var under));
        Assert.Equal(PlantRoot.Above, under.Root);
    }

    [Fact]
    public void TryPlant_OnStone_Fails_AndSpendsNothing()
    {
        var sim = Make();
        Run(sim, 0.5f);
        var p = sim.Player;
        p.Body.Position = CellCenter(32, Row);   // stand on the stone stretch for reach
        var m = p.Abilities.Meters;
        float before = m.BuildMove + m.EruptMove;
        Assert.False(SeedPlanting.TryPlant(Request(p, 32, Row), m, sim.Chunks, sim));
        Assert.Empty(Plants(sim));
        Assert.Equal(before, m.BuildMove + m.EruptMove);
    }

    [Fact]
    public void TryPlant_OnDirt_SpawnsSpitter_AndPaysItsCost()
    {
        var sim = Make();
        Run(sim, 0.5f);
        var p = sim.Player;
        var m = p.Abilities.Meters;
        float before = m.BuildMove + m.EruptMove;
        Assert.True(SeedPlanting.TryPlant(Request(p, 6, Row), m, sim.Chunks, sim));
        var plant = Assert.Single(Plants(sim));
        Assert.IsType<SpitterPlant>(plant);
        Assert.Equal(p.Id, plant.Owner);
        Assert.Equal(p.Faction, plant.Faction);
        Assert.Equal(p.Team, plant.Team);
        Assert.Equal(before - PlantTuning.CostFor(SeedKind.Spitter), m.BuildMove + m.EruptMove, 3);
        Assert.Equal(CellCenter(6, Row), plant.Body.Position);
    }

    [Fact]
    public void TryPlant_Refuses_OccupiedCell_OutOfReach_AndUnaffordable()
    {
        var sim = Make();
        Run(sim, 0.5f);
        var p = sim.Player;
        Assert.True(SeedPlanting.TryPlant(Request(p, 6, Row), null, sim.Chunks, sim));
        Assert.False(SeedPlanting.TryPlant(Request(p, 6, Row), null, sim.Chunks, sim));     // occupied
        Assert.False(SeedPlanting.TryPlant(Request(p, 25, Row), null, sim.Chunks, sim));    // ~20 tiles away
        var m = p.Abilities.Meters;
        m.BuildMove = 1f; m.EruptMove = 0f;
        Assert.Equal(PlantVerdict.CantAfford,
            PlantRules.Evaluate(Request(p, 7, Row), sim.Chunks, sim.Entities, m, out _));
        Assert.False(SeedPlanting.TryPlant(Request(p, 7, Row), m, sim.Chunks, sim));
        Assert.Single(Plants(sim));
    }

    [Fact]
    public void Cap_IsThreeLivePlantsPerPlayer_AndFreesOnDeath()
    {
        var sim = Make();
        Run(sim, 0.5f);
        var p = sim.Player;
        Assert.True(SeedPlanting.TryPlant(Request(p, 2, Row), null, sim.Chunks, sim));
        Assert.True(SeedPlanting.TryPlant(Request(p, 4, Row), null, sim.Chunks, sim));
        Assert.True(SeedPlanting.TryPlant(Request(p, 6, Row), null, sim.Chunks, sim));
        Assert.Equal(PlantVerdict.CapReached,
            PlantRules.Evaluate(Request(p, 8, Row), sim.Chunks, sim.Entities, null, out _));
        Assert.False(SeedPlanting.TryPlant(Request(p, 8, Row), null, sim.Chunks, sim));
        Assert.Equal(3, Plants(sim).Count);

        Plants(sim)[0].Health = 0f;
        sim.Step(default);
        Assert.True(SeedPlanting.TryPlant(Request(p, 8, Row), null, sim.Chunks, sim));
    }

    [Fact]
    public void UnbuiltSeeds_AreRefused_AsNotImplemented()
    {
        var sim = Make();
        Run(sim, 0.5f);
        var p = sim.Player;
        foreach (var k in new[] { SeedKind.Thorns, SeedKind.SiegePod, SeedKind.BlockEater })
        {
            Assert.Equal(PlantVerdict.NotImplemented,
                PlantRules.Evaluate(Request(p, 6, Row, k), sim.Chunks, sim.Entities, null, out _));
            Assert.False(SeedPlanting.TryPlant(Request(p, 6, Row, k), null, sim.Chunks, sim));
        }
        Assert.Empty(Plants(sim));
    }

    [Fact]
    public void Blocks_CanBePlacedOverAPlant()
    {
        var sim = Make();
        Run(sim, 0.5f);
        Assert.True(SeedPlanting.TryPlant(Request(sim.Player, 6, Row), null, sim.Chunks, sim));
        var plant = Plants(sim).Single();
        var pos = plant.Body.Position;
        Assert.NotNull(sim.Chunks.TryRequestTile(6, Row, TileType.Dirt));
        Run(sim, 2f);
        Assert.Equal(TileState.Solid, sim.Chunks.GetCellState(6, Row));
        Assert.False(plant.IsDead);
        Assert.True(plant.Dormant);
        Assert.Equal(pos, plant.Body.Position);   // rooted: the block did not shove it out
    }
}
