using System.Linq;
using Microsoft.Xna.Framework;
using Xunit;
using static MTile.Tests.Sim.PlantTestArena;

namespace MTile.Tests.Sim;

// The plant base (Entities/Plants/PlantEntity.cs): growth, root death, burial (dormant,
// drains, never recovers), owner immunity, and Snapshot/Restore — including a restore
// that has to rehydrate a plant that died after the snapshot. Plans/PLANT_SEEDS_PLAN.md
// §4 "Shared rules", §5, §9.
public class PlantLifecycleTests
{
    private const int Row = FloorRow - 1;
    private const int Cx  = 8;

    // P2 (when asked for) is added before the plant: secondaries register before entities,
    // as every stage does, so a restore re-registers in the same order.
    private static (Simulation sim, SpitterPlant plant) Planted(int p2Col = -1)
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

    [Fact]
    public void Seedling_GrowsToMature_AndMaturingAddsHealthWithoutHealing()
    {
        var (sim, plant) = Planted();
        Assert.Equal(PlantTuning.SeedlingHealth, plant.Health);
        Run(sim, 1f);
        Assert.False(plant.IsMature);
        float hit = PlantTuning.SeedlingHealth * 0.2f;
        plant.Health -= hit;   // a seedling hit
        for (int i = 0; i < (int)((PlantTuning.SpitterGrowSeconds + 5f) / Dt) && !plant.IsMature; i++)
            sim.Step(default);
        Assert.True(plant.IsMature);
        Assert.Equal(PlantTuning.SpitterHealth - hit, plant.Health, 3);
        Assert.Equal(PlantPose.Idle, plant.Pose);
    }

    [Fact]
    public void RootBreaking_KillsThePlant()
    {
        var (sim, plant) = Planted();
        Run(sim, 1f);
        Assert.True(sim.Chunks.BreakCell(Cx, FloorRow));
        sim.Step(default);
        Assert.True(plant.IsDead);
        Assert.Empty(Plants(sim));
    }

    [Fact]
    public void Burial_IsDormant_Drains_AndLostHealthNeverRecovers()
    {
        var (sim, plant) = Planted();
        Run(sim, PlantTuning.SpitterGrowSeconds + 0.1f);
        Assert.True(plant.IsMature);
        float full = plant.Health;
        Assert.Equal(PlantTuning.SpitterHealth, full, 3);

        sim.Chunks.EditorSetCell(Cx, Row, TileState.Solid, TileType.Foam);
        Run(sim, 0.5f);
        Assert.True(plant.Dormant);
        Assert.Equal(PlantPose.Dormant, plant.Pose);
        float drained = plant.Health;
        Assert.True(drained < full, "buried plants drain");

        sim.Chunks.EditorSetCell(Cx, Row, TileState.Empty, TileType.Foam);
        Run(sim, 0.5f);
        Assert.False(plant.Dormant);
        Assert.True(plant.Health <= drained + 1e-3f, "no regrowth of lost health");

        sim.Chunks.EditorSetCell(Cx, Row, TileState.Solid, TileType.Foam);
        for (int i = 0; i < (int)(120f / Dt) && !plant.IsDead; i++) sim.Step(default);
        Assert.True(plant.IsDead, "a buried plant eventually dies");
    }

    [Fact]
    public void Burial_FreezesGrowth()
    {
        var (sim, plant) = Planted();
        Run(sim, 2f);
        float g = plant.Growth;
        sim.Chunks.EditorSetCell(Cx, Row, TileState.Solid, TileType.Foam);
        Run(sim, 3f);
        Assert.Equal(g, plant.Growth, 3);
    }

    [Fact]
    public void OwnersHits_DoNotLand_OpponentsDo()
    {
        var (sim, plant) = Planted(p2Col: 40);
        var p2 = sim.SecondaryPlayers[0].Player;
        Run2(sim, 0.2f);

        var combat = new CombatSystem();
        var hurt = new HurtboxWorld();
        plant.PublishHurtboxes(hurt);
        var box = plant.Body.Bounds;
        IHittable Resolve(EntityId id) => id == plant.Id ? plant : null;

        var hits = new HitboxWorld();
        hits.Publish(new Hitbox(box, 9001, 0.3f, Vector2.Zero, sim.Player.Faction, sim.Player.Id));
        combat.Apply(sim.Chunks, hits, hurt, Resolve);
        Assert.Equal(PlantTuning.SeedlingHealth, plant.Health);

        hits = new HitboxWorld();
        hits.Publish(new Hitbox(box, 9002, 0.3f, Vector2.Zero, p2.Faction, p2.Id));
        combat.Apply(sim.Chunks, hits, hurt, Resolve);
        Assert.Equal(PlantTuning.SeedlingHealth - 0.3f, plant.Health, 3);
    }

    [Fact]
    public void SnapshotRestore_RewindsGrowthAndDormancy()
    {
        var (sim, plant) = Planted();
        Run(sim, 3f);
        var snap = sim.Snapshot();
        float g = plant.Growth, h = plant.Health;
        sim.Chunks.EditorSetCell(Cx, Row, TileState.Solid, TileType.Foam);
        Run(sim, 2f);
        Assert.True(plant.Dormant);
        sim.Restore(snap);
        var after = Plants(sim).Single();
        Assert.Equal(g, after.Growth, 4);
        Assert.Equal(h, after.Health, 4);
        sim.Step(default);
        Assert.False(after.Dormant);
    }

    [Fact]
    public void SnapshotRestore_RehydratesAPlantThatDiedAfterTheSnapshot()
    {
        var (sim, plant) = Planted();
        Run(sim, PlantTuning.SpitterGrowSeconds + 0.5f);
        var snap = sim.Snapshot();
        float g = plant.Growth;
        var owner = plant.Owner;
        sim.Chunks.BreakCell(Cx, FloorRow);
        Run(sim, 0.2f);
        Assert.Empty(Plants(sim));

        sim.Restore(snap);
        var back = Assert.IsType<SpitterPlant>(Plants(sim).Single());
        Assert.Equal(EntityKind.PlantSpitter, back.Kind);
        Assert.Equal(Cx, back.CellX);
        Assert.Equal(Row, back.CellY);
        Assert.Equal(PlantRoot.Below, back.Root);
        Assert.Equal(owner, back.Owner);
        Assert.Equal(g, back.Growth, 4);
        Assert.True(back.IsMature);
        Assert.Equal(sim.Player.Faction, back.Faction);
        Run(sim, 0.5f);
        Assert.False(back.IsDead);
    }

    [Fact]
    public void ReplayAfterRestore_IsDeterministic()
    {
        var (sim, _) = Planted(p2Col: 20);
        Run2(sim, PlantTuning.SpitterGrowSeconds - 1f);
        var snap = sim.Snapshot();
        Run2(sim, 4f);
        var a = sim.Checksum();
        sim.Restore(snap);
        Run2(sim, 4f);
        Assert.Equal(a, sim.Checksum());
    }
}
