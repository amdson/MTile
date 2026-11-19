using System.Linq;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;
using static MTile.Tests.Sim.PlantTestArena;

namespace MTile.Tests.Sim;

// The siege pod entity (Entities/Plants/SiegePodPlant.cs, Plans/PLANT_SEEDS_PLAN.md §4.4):
// planted on release with the dragged aim, one alive per player, grows 20 s with its aim
// telegraphed, fires ONCE by itself (SiegeBlast) and is gone; burial delays it, an early
// kill removes it with no shot. The shot is not team-filtered and kills plants.
// The firing unit itself is covered by PlantSiegeBlastTests; these pin the wrapper.
public class PlantSiegePodTests(ITestOutputHelper output)
{
    private const int Row = FloorRow - 1;
    private static readonly Vector2 Right = new(1f, 0f), Left = new(-1f, 0f);

    private static SiegePodPlant Pod(int cx, Vector2 drag, PlayerCharacter owner)
        => new(new PlantSite(cx, Row, PlantRoot.Below), owner.Id, owner.Faction, owner.Team, drag * 40f);

    private static (Simulation sim, SiegePodPlant pod) Planted(int cx, Vector2 drag, System.Action<Simulation> extra = null)
    {
        SiegePodPlant pod = null;
        var sim = Make(g =>
        {
            extra?.Invoke(g);
            pod = Pod(cx, drag, g.Player);
            g.SpawnEntity(pod);
        });
        return (sim, pod);
    }

    private static bool HazardThisFrame(Simulation s) => s.Hitboxes.All.Any(h => h.Owner == Faction.Hazard);

    // Steps until the pod has fired; returns the number of frames stepped.
    private static int StepUntilFired(Simulation s, SiegePodPlant pod, bool two = false, int max = 4000)
    {
        for (int i = 1; i <= max; i++)
        {
            if (two) s.Step(default, default); else s.Step(default);
            if (pod.HasFired) return i;
        }
        return -1;
    }

    private static bool Solid(Simulation s, int x, int y) => s.Chunks.GetCellState(x, y) == TileState.Solid;

    private static ulong TerrainHash(ChunkMap m)
    {
        ulong h = 1469598103934665603UL;
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 64; x++)
            {
                h = (h ^ (ulong)m.GetCellState(x, y)) * 1099511628211UL;
                h = (h ^ (ulong)System.BitConverter.SingleToInt32Bits(m.Damage.Get(x, y))) * 1099511628211UL;
            }
        return h;
    }

    // ── Planting ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(40f, 0f, 1f, 0f)]       // dragged right: aimed right
    [InlineData(-30f, -30f, -0.7071f, -0.7071f)]
    [InlineData(1f, 0f, 0f, -1f)]       // ~zero drag: straight away from the floor (up)
    public void PlantsOnRelease_WithTheDraggedAim(float dx, float dy, float ax, float ay)
    {
        var sim = Make();
        Run(sim, 0.5f);
        sim.Step(new PlayerInput { F = true });
        sim.Step(default);
        sim.Step(new PlayerInput { Num4 = true });
        Assert.Equal(SeedKind.SiegePod, sim.Player.SelectedSeed);

        var press = CellCenter(6, Row);
        var drag  = new Vector2(dx, dy);
        for (int i = 0; i <= 20; i++)
        {
            sim.Step(new PlayerInput { RightClick = true, MouseWorldPosition = press + drag * (i / 20f) });
            Assert.Empty(Plants(sim));   // nothing until the release
        }
        // The pod's price is 16 stone, which under the CODE-default stone cost is more than the
        // working pool holds (MoveMax) — a test-config artefact (the shipped json halves it).
        // Top the charge pool up so the release can pay whichever config is loaded.
        var m = sim.Player.Abilities.Meters;
        m.EruptMove = BuildMeters.EruptMax;
        float before = m.BuildMove + m.EruptMove;
        sim.Step(new PlayerInput { RightClick = false, MouseWorldPosition = press + drag });
        sim.Step(new PlayerInput { RightClick = false, MouseWorldPosition = press + drag });
        Assert.True(m.BuildMove + m.EruptMove < before, "the pod was paid for");

        var pod = Assert.IsType<SiegePodPlant>(Assert.Single(Plants(sim)));
        Assert.Equal(6, pod.CellX);
        Assert.Equal(Row, pod.CellY);
        Assert.Equal(ax, pod.Aim.X, 3);
        Assert.Equal(ay, pod.Aim.Y, 3);
    }

    [Fact]
    public void OneAlivePerPlayer_AndTheSlotFreesWhenItFires()
    {
        SiegePodPlant mine = null;
        var sim = Make(g => g.AddSecondaryPlayer(Spawn(50)));
        Run2(sim, 0.5f);
        var p1 = sim.Player;
        var p2 = sim.SecondaryPlayers[0].Player;
        PlantRequest At(PlayerCharacter p, int x, SeedKind k = SeedKind.SiegePod)
        {
            var r = Request(p, x, Row, k);
            r.OwnerPos = CellCenter(x, Row - 1);
            return r;
        }

        Assert.True(SeedPlanting.TryPlant(At(p1, 2), null, sim.Chunks, sim));
        mine = Assert.IsType<SiegePodPlant>(Plants(sim).Single());
        // A second pod is refused, and the ghost's verdict says so.
        Assert.Equal(PlantVerdict.CapReached, PlantRules.Evaluate(At(p1, 16), sim.Chunks, sim.Entities, null, out _));
        Assert.False(SeedPlanting.TryPlant(At(p1, 16), null, sim.Chunks, sim));
        // Other plants still fit under the shared cap; the other player has their own pod.
        Assert.True(SeedPlanting.TryPlant(At(p1, 16, SeedKind.Spitter), null, sim.Chunks, sim));
        Assert.True(SeedPlanting.TryPlant(At(p2, 45), null, sim.Chunks, sim));
        Assert.Equal(3, Plants(sim).Count);

        Assert.True(StepUntilFired(sim, mine, two: true) > 0);
        // Fired: Spent at once, so the slot is free even during the render linger.
        Assert.Equal(PlantVerdict.Ok, PlantRules.Evaluate(At(p1, 24), sim.Chunks, sim.Entities, null, out _));
        Assert.True(SeedPlanting.TryPlant(At(p1, 24), null, sim.Chunks, sim));
    }

    // ── Firing ────────────────────────────────────────────────────────────────

    [Fact]
    public void FiresExactlyOnce_At20sOfGrowth_AndIsGoneAfterwards()
    {
        var (sim, pod) = Planted(10, Vector2.Zero);   // aimed up
        int fireFrame = -1, hazardFrames = 0;
        int frames = (int)((PlantTuning.SiegePodGrowSeconds + 5f) / Dt);
        for (int f = 1; f <= frames; f++)
        {
            sim.Step(default);
            if (HazardThisFrame(sim)) { hazardFrames++; if (fireFrame < 0) fireFrame = f; }
            if (f == frames / 2) Assert.True(Solid(sim, 10, FloorRow));   // nothing fired mid-growth
        }
        output.WriteLine($"fired on frame {fireFrame} ({fireFrame * Dt:F3} s)");
        Assert.Equal(1, hazardFrames);
        int expected = (int)System.MathF.Round(PlantTuning.SiegePodGrowSeconds / Dt);
        Assert.InRange(fireFrame, expected, expected + 2);
        Assert.False(Solid(sim, 10, FloorRow));                            // the blast ate its root
        Assert.DoesNotContain(sim.Entities, e => e is SiegePodPlant);   // removed, no corpse
    }

    [Fact]
    public void FiredPod_IsUnhittable_AndDoesNotHoldItsCell()
    {
        var (sim, pod) = Planted(10, Right);
        Assert.True(StepUntilFired(sim, pod) > 0);
        Assert.True(pod.Spent);
        var hurt = new HurtboxWorld();
        pod.PublishHurtboxes(hurt);
        Assert.Empty(hurt.All);
        Assert.False(PlantRules.Occupied(sim.Entities, 10, Row));
    }

    [Fact]
    public void Burial_FreezesGrowth_AndDelaysTheShot()
    {
        var (sim, pod) = Planted(10, Vector2.Zero);
        Run(sim, 5f);
        sim.Chunks.EditorSetCell(10, Row, TileState.Solid, TileType.Foam);
        Run(sim, 2f);
        Assert.True(pod.Dormant);
        sim.Chunks.EditorSetCell(10, Row, TileState.Empty, TileType.Foam);
        Run(sim, PlantTuning.SiegePodGrowSeconds - 7f + 0.1f);   // 20.1 s since planting, 18.1 s grown
        Assert.False(pod.HasFired, "two buried seconds must not count toward ripening");
        Assert.True(Solid(sim, 10, FloorRow));
        Run(sim, 2.1f);
        Assert.True(pod.HasFired);
    }

    [Theory]
    [InlineData(true)]    // root broken
    [InlineData(false)]   // health gone
    public void KilledEarly_NoShot_TerrainUntouched(bool breakRoot)
    {
        var (sim, pod) = Planted(10, Right);
        Run(sim, 10f);
        if (breakRoot) sim.Chunks.EditorSetCell(10, FloorRow, TileState.Empty, TileType.Dirt);
        else           pod.Health = 0f;
        ulong before = TerrainHash(sim.Chunks);
        int frames = (int)(15f / Dt), hazard = 0;
        for (int f = 0; f < frames; f++) { sim.Step(default); if (HazardThisFrame(sim)) hazard++; }
        Assert.Equal(0, hazard);
        Assert.False(pod.HasFired);
        Assert.DoesNotContain(sim.Entities, e => e is SiegePodPlant);
        Assert.Equal(before, TerrainHash(sim.Chunks));
        Assert.Equal(0f, sim.Player.Combat.DamageTaken);
    }

    // ── The shot from a real pod ──────────────────────────────────────────────

    [Theory]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void Lance_FromAPlantedPod_ThreeStoneLayersAreNotCover_FourAre(int layers, bool hit)
    {
        // Pod at col 10 aimed +x: its lanes are rows 10 and 11. Stone in both from col 20.
        const int wall = 20;
        var (sim, pod) = Planted(10, Right, g =>
        {
            g.AddSecondaryPlayer(Spawn(30));
            for (int c = wall; c < wall + layers; c++)
                for (int r = Row - 1; r <= Row; r++)
                    g.Chunks.EditorSetCell(c, r, TileState.Solid, TileType.Stone);
        });
        Assert.True(StepUntilFired(sim, pod, two: true) > 0);
        var p2 = sim.SecondaryPlayers[0].Player;
        for (int r = Row - 1; r <= Row; r++)
        {
            for (int c = wall; c < wall + 3; c++) Assert.False(Solid(sim, c, r), $"({c},{r}) should be broken");
            if (layers == 4) Assert.True(Solid(sim, wall + 3, r), $"({wall + 3},{r}) should stand");
        }
        output.WriteLine($"p2 damage {p2.Combat.DamageTaken}");
        if (hit) Assert.Equal(SiegeBlast.LanceBodyDamage, p2.Combat.DamageTaken, 3);
        else     Assert.Equal(0f, p2.Combat.DamageTaken);
    }

    [Fact]
    public void HorizontalShot_FromAFloorPod_DoesNotEatTheGroundRow()
    {
        var (sim, pod) = Planted(10, Right);
        // The lanes are the pod's own row and the one above — never the floor.
        var preview = SiegeBlast.Preview(sim.Chunks, pod.Muzzle, pod.Aim);
        for (int j = 0; j < preview.LaneCount; j++)
            Assert.InRange((int)System.MathF.Floor(preview[j].Start.Y / Ts), Row - 1, Row);
        // The pin: from the cell CENTRE one lane would run along the ground row.
        var centre = SiegeBlast.Preview(sim.Chunks, CellCenter(10, Row), Right);
        Assert.Contains(Enumerable.Range(0, centre.LaneCount),
                        j => (int)System.MathF.Floor(centre[j].Start.Y / Ts) == FloorRow);

        Assert.True(StepUntilFired(sim, pod) > 0);
        // Past the blast disc (5 tiles) the ground row is whole all the way down the lance.
        for (int c = 17; c < 64; c++) Assert.True(Solid(sim, c, FloorRow), $"ground ({c},{FloorRow}) was eaten");
    }

    [Fact]
    public void Owner_StandingOnTheLine_IsHit()
    {
        // The owner (P1, col 4) twelve tiles down the lance, well outside the blast.
        var (sim, pod) = Planted(16, Left);
        Assert.True(StepUntilFired(sim, pod) > 0);
        Assert.Equal(SiegeBlast.LanceBodyDamage, sim.Player.Combat.DamageTaken, 3);
    }

    [Theory]
    [InlineData(false, true)]    // owner's spitter, in the blast (pod aimed up, off the line)
    [InlineData(false, false)]   // owner's spitter, on the lance, past the blast
    [InlineData(true, true)]     // the other player's spitter, in the blast
    [InlineData(true, false)]    // the other player's spitter, on the lance
    public void APlant_InTheBlastOrOnTheLine_Dies(bool othersSpitter, bool inBlast)
    {
        SpitterPlant spitter = null;
        int sx = inBlast ? 23 : 8;
        var (sim, pod) = Planted(20, inBlast ? Vector2.Zero : Left, g =>
        {
            g.AddSecondaryPlayer(Spawn(62));
            var owner = othersSpitter ? g.SecondaryPlayers[0].Player : g.Player;
            spitter = new SpitterPlant(new PlantSite(sx, Row, PlantRoot.Below), owner.Id, owner.Faction, owner.Team);
            g.SpawnEntity(spitter);
            // A hardened root the blast cannot break, so it is the HIT that kills, not the root.
            // (Spawned before the pod, the spitter also updates before it each frame, so
            // the fire frame's root check has already passed when the shot lands.)
            g.Chunks.EditorSetCell(sx, FloorRow, TileState.Solid, TileType.Hardened);
        });
        // Keep both players out of every spitter's range (nothing shoots the pod down).
        sim.Player.Body.Position = Spawn(60);
        Run2(sim, PlantTuning.SpitterGrowSeconds + 0.5f);
        Assert.True(spitter.IsMature);
        Assert.True(spitter.Health > SiegeBlast.BlastBodyDamage && spitter.Health > SiegeBlast.LanceBodyDamage,
                    "a mature spitter outlasts one heavy hit — the kill must come from the plant rule");
        Assert.True(StepUntilFired(sim, pod, two: true) > 0);
        Assert.True(spitter.IsDead, $"spitter health {spitter.Health}");   // on the fire frame itself
        Assert.True(Solid(sim, sx, FloorRow), "its root still stands");
    }

    // ── Telegraph ─────────────────────────────────────────────────────────────

    [Fact]
    public void TelegraphReach_EqualsTheFiredReach()
    {
        const int wall = 22;
        var (sim, pod) = Planted(10, Right, g =>
        {
            for (int c = wall; c < wall + 6; c++)
                for (int r = Row - 1; r <= Row; r++)
                    g.Chunks.EditorSetCell(c, r, TileState.Solid, TileType.Stone);
        });
        Run(sim, 5f);
        var t = new TelegraphList();
        pod.Telegraph(t);
        Assert.True(t.Count > 0, "the aim is telegraphed while growing");

        // The frame before it fires: what the telegraph shows.
        while (!pod.IsMature) sim.Step(default);
        Assert.False(pod.HasFired);
        Assert.True(pod.Charging || pod.IsMature);
        var shown = SiegeBlast.Preview(sim.Chunks, pod.Muzzle, pod.Aim);
        t.Clear();
        pod.Telegraph(t);
        Assert.True(t.Count > 0);

        sim.Step(default);
        Assert.True(pod.HasFired);
        output.WriteLine($"telegraphed {shown.Reach}, fired {pod.ShotReach}");
        Assert.Equal(shown.Reach, pod.ShotReach, 3);
        Assert.True(Solid(sim, wall + 3, Row));
    }

    [Fact]
    public void ChargingTell_OnlyInTheLastStretch()
    {
        var (sim, pod) = Planted(10, Right);
        Run(sim, PlantTuning.SiegePodGrowSeconds - PlantTuning.SiegePodChargeSeconds - 0.2f);
        Assert.False(pod.Charging);
        Run(sim, 0.4f);
        Assert.True(pod.Charging);
    }

    // ── Rollback ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]   // mid-growth
    [InlineData(true)]    // one frame before it fires
    public void SnapshotRestore_ReplaysIdentically(bool frameBeforeFiring)
    {
        var (sim, pod) = Planted(10, Right, g => g.AddSecondaryPlayer(Spawn(30)));
        if (frameBeforeFiring) { while (!pod.IsMature) sim.Step(default, default); Assert.False(pod.HasFired); }
        else Run2(sim, 9f);

        var snap = sim.Snapshot();
        Run2(sim, frameBeforeFiring ? 3f : 14f);
        ulong a = sim.Checksum(), ta = TerrainHash(sim.Chunks);
        float hp = sim.SecondaryPlayers[0].Player.Health;
        Assert.DoesNotContain(sim.Entities, e => e is SiegePodPlant);

        sim.Restore(snap);
        var back = Assert.IsType<SiegePodPlant>(Plants(sim).Single());
        Assert.Equal(Right.X, back.Aim.X, 5);
        Assert.False(back.HasFired);
        Run2(sim, frameBeforeFiring ? 3f : 14f);
        Assert.Equal(a, sim.Checksum());
        Assert.Equal(ta, TerrainHash(sim.Chunks));
        Assert.Equal(hp, sim.SecondaryPlayers[0].Player.Health);
        Assert.True(hp < sim.SecondaryPlayers[0].Player.MaxHealth, "P2 on the line should have been hit");
    }

    [Fact]
    public void Rehydrates_FromItsEntityData()
    {
        var (sim, pod) = Planted(10, new Vector2(0.6f, -0.8f));
        Run(sim, 3f);
        var snap = sim.Snapshot();
        sim.Restore(snap);
        var back = Assert.IsType<SiegePodPlant>(Plants(sim).Single());
        Assert.Equal(pod.Aim, back.Aim);
        Assert.Equal(pod.Growth, back.Growth);
    }
}
