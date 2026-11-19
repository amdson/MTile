using System.Linq;
using Microsoft.Xna.Framework;
using Xunit;
using static MTile.Tests.Sim.PlantTestArena;

namespace MTile.Tests.Sim;

// The snapper (Plans/PLANT_SEEDS_PLAN.md §4.2): a close-range guard. Mature and awake, an
// opposing player within ~2 tiles with a sightline triggers a wind-up, then one heavy-tier
// bite toward where the target was; then it is spent for a re-arm. Never its owner.
public class PlantSnapperTests
{
    private const int Row = FloorRow - 1;
    private const int Cx  = 10;

    // The plant at (Cx, Row), P2 parked far away (col 40) so it can be walked in once the
    // plant is mature. Returns once the plant is mature and armed.
    private static (Simulation sim, SnapperPlant plant, PlayerCharacter p2) MatureWithP2Far()
    {
        SnapperPlant plant = null;
        var sim = Make(g =>
        {
            g.AddSecondaryPlayer(Spawn(40));
            plant = new SnapperPlant(new PlantSite(Cx, Row, PlantRoot.Below), g.Player.Id, g.Player.Faction, g.Player.Team);
            g.SpawnEntity(plant);
        });
        Run2(sim, PlantTuning.SnapperGrowSeconds + 0.1f);
        Assert.True(plant.IsMature);
        return (sim, plant, sim.SecondaryPlayers[0].Player);
    }

    private static void Park(PlayerCharacter p, Vector2 pos) { p.Body.Position = pos; p.Body.Velocity = Vector2.Zero; }
    private static Vector2 Near => Spawn(Cx + 1);   // one tile from the plant, well within range

    [Fact]
    public void Tuning_BiteIsHeavyTier_AndBeatsAHeldGuard()
    {
        Assert.InRange(PlantTuning.SnapperBiteDamage, 0.6f, 0.9f);
        Assert.True(PlantTuning.SnapperBiteDamage > 0.5f);   // a held guard blocks <= 0.5
    }

    [Fact]
    public void BitesAnOpponentInRange_AfterTheWindup_NeverTheOwner()
    {
        var (sim, plant, p2) = MatureWithP2Far();
        Park(p2, Near);
        Park(sim.Player, Spawn(Cx - 1));   // the owner is just as close
        float hp0 = p2.Health;

        Run2(sim, PlantTuning.SnapperWindup - 0.1f);
        Assert.Equal(hp0, p2.Health);       // still winding up
        Assert.Equal(1, plant.Phase);

        float minHp = hp0;
        for (int i = 0; i < (int)(0.5f / Dt); i++)
        {
            sim.Step(default, default);
            minHp = System.Math.Min(minHp, p2.Health);
        }
        Assert.True(minHp <= hp0 - 0.6f, $"expected a heavy bite, hp {hp0} -> {minHp}");
        Assert.Equal(sim.Player.MaxHealth, sim.Player.Health);
    }

    [Fact]
    public void OwnerAlone_IsNeverBitten()
    {
        SnapperPlant plant = null;
        var sim = Make(g =>
        {
            plant = new SnapperPlant(new PlantSite(Cx, Row, PlantRoot.Below), g.Player.Id, g.Player.Faction, g.Player.Team);
            g.SpawnEntity(plant);
        });
        Run(sim, PlantTuning.SnapperGrowSeconds + 0.1f);
        Park(sim.Player, Near);
        Run(sim, 3f);
        Assert.Equal(0, plant.Phase);
        Assert.Equal(sim.Player.MaxHealth, sim.Player.Health);
    }

    [Fact]
    public void NoBiteBeyondRange()
    {
        var (sim, plant, p2) = MatureWithP2Far();
        Park(p2, Spawn(Cx + 4));   // 4 tiles: out of the ~2 tile reach
        Run2(sim, 2f);
        Assert.Equal(0, plant.Phase);
        Assert.Equal(p2.MaxHealth, p2.Health);
    }

    [Fact]
    public void NoBiteThroughAWall()
    {
        // Same spot, with and without a stone wall between: the control must bite.
        var spot = new Vector2((Cx + 2) * Ts + 1f, Row * Ts + Ts * 0.5f);
        var (ctrl, cPlant, cP2) = MatureWithP2Far();
        Park(cP2, spot);
        Run2(ctrl, 1f);
        Assert.True(cP2.Health < cP2.MaxHealth, "control: with no wall it should bite");

        var (sim, plant, p2) = MatureWithP2Far();
        sim.Chunks.EditorSetCell(Cx + 1, Row, TileState.Solid, TileType.Stone);
        sim.Chunks.EditorSetCell(Cx + 1, Row - 1, TileState.Solid, TileType.Stone);
        Park(p2, spot);
        Run2(sim, 1f);
        Assert.Equal(0, plant.Phase);
        Assert.Equal(p2.MaxHealth, p2.Health);
    }

    [Fact]
    public void SteppingOutDuringWindup_DodgesTheBite()
    {
        var (sim, plant, p2) = MatureWithP2Far();
        Park(p2, Near);
        Run2(sim, 0.2f);
        Assert.Equal(1, plant.Phase);
        Park(p2, Spawn(40));
        Run2(sim, 1f);
        Assert.Equal(p2.MaxHealth, p2.Health);
        Assert.Equal(3, plant.Phase);   // it whiffed and is spent
    }

    [Fact]
    public void AfterABite_ItIsSpentUntilTheRearm()
    {
        var (sim, plant, p2) = MatureWithP2Far();
        Park(p2, Near);
        Run2(sim, PlantTuning.SnapperWindup + PlantTuning.SnapperBiteActive + 0.1f);
        Assert.True(p2.Health < p2.MaxHealth);
        Assert.Equal(3, plant.Phase);

        // Keep the target in reach, topped up, through most of the re-arm: no further bite.
        float until = PlantTuning.SnapperRearm - 0.4f;
        for (int i = 0; i < (int)(until / Dt); i++)
        {
            p2.Health = p2.MaxHealth;
            Park(p2, Near);
            sim.Step(default, default);
            Assert.Equal(p2.MaxHealth, p2.Health);
        }
        // After the re-arm it winds up and bites again.
        float minHp = p2.MaxHealth;
        for (int i = 0; i < (int)(1.5f / Dt); i++)
        {
            Park(p2, Near);
            sim.Step(default, default);
            minHp = System.Math.Min(minHp, p2.Health);
        }
        Assert.True(minHp < p2.MaxHealth);
    }

    [Fact]
    public void Burial_ResetsTheWindup_AndNothingBites()
    {
        var (sim, plant, p2) = MatureWithP2Far();
        Park(p2, Near);
        Run2(sim, 0.25f);
        Assert.Equal(1, plant.Phase);
        sim.Chunks.EditorSetCell(Cx, Row, TileState.Solid, TileType.Stone);
        Run2(sim, 0.05f);
        Assert.True(plant.Dormant);
        Assert.Equal(0, plant.Phase);
        Assert.Equal(0f, plant.WindupProgress);
        Run2(sim, 1f);
        Assert.Equal(p2.MaxHealth, p2.Health);
    }

    [Fact]
    public void Seedling_DoesNothing()
    {
        SnapperPlant plant = null;
        var sim = Make(g =>
        {
            g.AddSecondaryPlayer(Spawn(Cx + 1));
            plant = new SnapperPlant(new PlantSite(Cx, Row, PlantRoot.Below), g.Player.Id, g.Player.Faction, g.Player.Team);
            g.SpawnEntity(plant);
        });
        Run2(sim, PlantTuning.SnapperGrowSeconds - 1f);
        Assert.False(plant.IsMature);
        Assert.Equal(0, plant.Phase);
        Assert.Equal(sim.SecondaryPlayers[0].Player.MaxHealth, sim.SecondaryPlayers[0].Player.Health);
    }

    [Theory]
    [InlineData(0.2f)]    // mid wind-up
    [InlineData(1.5f)]    // mid re-arm
    public void SnapshotRestore_ReplaysIdentically(float after)
    {
        var (sim, plant, p2) = MatureWithP2Far();
        Park(p2, Near);
        Run2(sim, after);
        var snap = sim.Snapshot();
        Run2(sim, 5f);
        var a = sim.Checksum();
        float hp = p2.Health;
        sim.Restore(snap);
        Run2(sim, 5f);
        Assert.Equal(a, sim.Checksum());
        Assert.Equal(hp, sim.SecondaryPlayers[0].Player.Health);
        Assert.IsType<SnapperPlant>(Plants(sim).Single());
    }

    [Fact]
    public void Plantable_ThroughTryPlant_AndCountsTowardTheCap()
    {
        var sim = Make();
        Crouch(sim);
        var p = sim.Player;
        Assert.True(SeedPlanting.TryPlant(Request(p, 3, Row, SeedKind.Snapper), null, sim.Chunks, sim));
        Assert.IsType<SnapperPlant>(Plants(sim).Single());
        Assert.True(SeedPlanting.TryPlant(Request(p, 4, Row), null, sim.Chunks, sim));            // spitter
        Assert.True(SeedPlanting.TryPlant(Request(p, 5, Row, SeedKind.Snapper), null, sim.Chunks, sim));
        PutOver(p, 8);   // in reach of a fresh cell: only the cap refuses it
        Assert.False(SeedPlanting.TryPlant(Request(p, 8, Row, SeedKind.Snapper), null, sim.Chunks, sim));
        Assert.Equal(3, Plants(sim).Count);
    }
}
