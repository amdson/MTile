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
        Crouch(sim);
        var p = sim.Player;
        var m = p.Abilities.Meters;
        float before = m.BuildMove + m.EruptMove;
        Assert.True(SeedPlanting.TryPlant(Request(p, 5, Row), m, sim.Chunks, sim));
        var plant = Assert.Single(Plants(sim));
        Assert.IsType<SpitterPlant>(plant);
        Assert.Equal(p.Id, plant.Owner);
        Assert.Equal(p.Faction, plant.Faction);
        Assert.Equal(p.Team, plant.Team);
        Assert.Equal(before - PlantTuning.CostFor(SeedKind.Spitter), m.BuildMove + m.EruptMove, 3);
        Assert.Equal(CellCenter(5, Row), plant.Body.Position);
    }

    [Fact]
    public void TryPlant_Refuses_OccupiedCell_OutOfReach_AndUnaffordable()
    {
        var sim = Make();
        Crouch(sim);
        var p = sim.Player;
        Assert.True(SeedPlanting.TryPlant(Request(p, 5, Row), null, sim.Chunks, sim));
        Assert.False(SeedPlanting.TryPlant(Request(p, 5, Row), null, sim.Chunks, sim));     // occupied
        Assert.False(SeedPlanting.TryPlant(Request(p, 25, Row), null, sim.Chunks, sim));    // ~20 tiles away
        var m = p.Abilities.Meters;
        m.BuildMove = 0f; m.EruptMove = 0f;
        Assert.Equal(PlantVerdict.CantAfford,
            PlantRules.Evaluate(Request(p, 3, Row), sim.Chunks, sim.Entities, m, out _));
        Assert.False(SeedPlanting.TryPlant(Request(p, 3, Row), m, sim.Chunks, sim));
        Assert.Single(Plants(sim));
    }

    [Fact]
    public void Cap_LivePlantsPerPlayer_Enforced_AndFreesOnDeath()
    {
        var sim = Make();
        Crouch(sim);
        var p = sim.Player;
        int cap = PlantTuning.PlantCapPerPlayer;
        for (int i = 0; i < cap; i++)
        {
            PutOver(p, 3 + i * 3);
            Assert.True(SeedPlanting.TryPlant(Request(p, 3 + i * 3, Row), null, sim.Chunks, sim));
        }
        int fresh = 3 + cap * 3;
        PutOver(p, fresh);   // a fresh site in reach, so only the cap refuses it
        Assert.Equal(PlantVerdict.CapReached,
            PlantRules.Evaluate(Request(p, fresh, Row), sim.Chunks, sim.Entities, null, out _));
        Assert.False(SeedPlanting.TryPlant(Request(p, fresh, Row), null, sim.Chunks, sim));
        Assert.Equal(cap, Plants(sim).Count);

        Plants(sim)[0].Health = 0f;
        sim.Step(new PlayerInput { Down = true });
        PutOver(p, fresh);
        Assert.True(SeedPlanting.TryPlant(Request(p, fresh, Row), null, sim.Chunks, sim));
    }

    [Fact]
    public void UnbuiltSeeds_AreRefused_AsNotImplemented()
    {
        var sim = Make();
        Run(sim, 0.5f);
        var p = sim.Player;
        foreach (var k in System.Enum.GetValues<SeedKind>().Where(k => !SeedPlanting.IsImplemented(k)))
        {
            Assert.Equal(PlantVerdict.NotImplemented,
                PlantRules.Evaluate(Request(p, 6, Row, k), sim.Chunks, sim.Entities, null, out _));
            Assert.False(SeedPlanting.TryPlant(Request(p, 6, Row, k), null, sim.Chunks, sim));
        }
        Assert.Empty(Plants(sim));
    }

    [Fact]
    public void Thorns_PlantThroughTheEntryPoint_AsATilePatch_NotAnEntity()
    {
        var sim = Make();
        Crouch(sim);
        var p = sim.Player;
        Assert.Equal(PlantVerdict.Ok,
            PlantRules.Evaluate(Request(p, 5, Row, SeedKind.Thorns), sim.Chunks, sim.Entities, null, out _));
        Assert.True(SeedPlanting.TryPlant(Request(p, 5, Row, SeedKind.Thorns), null, sim.Chunks, sim));
        Assert.True(sim.Chunks.Thorns.IsSeedCell(5, Row));
        Assert.Empty(Plants(sim));
        // The same cell cannot take a second patch.
        Assert.False(SeedPlanting.TryPlant(Request(p, 5, Row, SeedKind.Thorns), null, sim.Chunks, sim));
    }

    [Fact]
    public void BlockEater_PlantsOnAnEnemyOwnedBlock_NotOnOwnOrUnownedOnes()
    {
        var sim = Make();
        Crouch(sim);
        var p = sim.Player;
        byte mine = BlockOwnership.OwnerFor(p.Faction);
        byte theirs = mine == BlockOwnership.Player1 ? BlockOwnership.Player2 : BlockOwnership.Player1;
        int now = sim.Chunks.TerrainFrame;
        // Floor cells: 6 unowned, 7 owned by me, 8 owned by the other player.
        sim.Chunks.Ownership.Stamp(7, FloorRow, mine, now);
        sim.Chunks.Ownership.Stamp(8, FloorRow, theirs, now);
        PutOver(p, 7);   // all three are beneath / beside the body

        Assert.False(SeedPlanting.TryPlant(Request(p, 6, FloorRow, SeedKind.BlockEater), null, sim.Chunks, sim));
        Assert.False(SeedPlanting.TryPlant(Request(p, 7, FloorRow, SeedKind.BlockEater), null, sim.Chunks, sim));
        Assert.Equal(PlantVerdict.Ok,
            PlantRules.Evaluate(Request(p, 8, FloorRow, SeedKind.BlockEater), sim.Chunks, sim.Entities, null, out _));
        Assert.True(SeedPlanting.TryPlant(Request(p, 8, FloorRow, SeedKind.BlockEater), null, sim.Chunks, sim));
        Assert.True(sim.Chunks.BlockEater.Has(8, FloorRow));
        Assert.Empty(Plants(sim));
    }

    // Close reach (PlantRules.InReach, PlantTuning.PlantReachMargin): the ROOT must be beside
    // or beneath the crouched body — two tiles off, above the head, or a ceiling is refused.
    [Fact]
    public void Reach_BesideAndBeneath_Accepted_TwoTilesOff_AboveTheHead_AndCeilings_Refused()
    {
        var sim = Make();
        Crouch(sim);
        var p = sim.Player;
        PutOver(p, 4);
        Assert.IsType<CrouchedState>(p.CurrentState);
        PlantVerdict V(int x, int y, out PlantSite site)
            => PlantRules.Evaluate(Request(p, x, y), sim.Chunks, sim.Entities, null, out site);

        Assert.Equal(PlantVerdict.Ok, V(4, Row, out var s));            // beneath the body
        Assert.Equal(PlantRoot.Below, s.Root);
        Assert.Equal(PlantVerdict.Ok, V(3, Row, out _));                // beside, on the floor
        Assert.Equal(PlantVerdict.Ok, V(5, Row, out _));
        Assert.Equal(PlantVerdict.OutOfReach, V(6, Row, out _));        // two tiles off
        Assert.Equal(PlantVerdict.OutOfReach, V(2, Row, out _));
        Assert.Equal(PlantVerdict.OutOfReach, V(10, Row, out _));

        var c = sim.Chunks;
        c.EditorSetCell(5, Row - 1, TileState.Solid, TileType.Dirt);    // a wall beside the body
        Assert.Equal(PlantVerdict.Ok, V(4, Row - 1, out s));
        Assert.Equal(PlantRoot.Right, s.Root);
        c.EditorSetCell(5, Row - 2, TileState.Solid, TileType.Dirt);    // the same wall above the head
        Assert.Equal(PlantVerdict.OutOfReach, V(6, Row - 2, out _));
        c.EditorSetCell(4, Row - 3, TileState.Solid, TileType.Dirt);    // a ceiling over the head
        Assert.Equal(PlantVerdict.OutOfReach, V(4, Row - 2, out _));
    }

    [Fact]
    public void Blocks_CanBePlacedOverAPlant()
    {
        var sim = Make();
        Crouch(sim);
        Assert.True(SeedPlanting.TryPlant(Request(sim.Player, 5, Row), null, sim.Chunks, sim));
        var plant = Plants(sim).Single();
        var pos = plant.Body.Position;
        Assert.NotNull(sim.Chunks.TryRequestTile(5, Row, TileType.Dirt));
        Run(sim, 2f);
        Assert.Equal(TileState.Solid, sim.Chunks.GetCellState(5, Row));
        Assert.False(plant.IsDead);
        Assert.True(plant.Dormant);
        Assert.Equal(pos, plant.Body.Position);   // rooted: the block did not shove it out
    }
}
