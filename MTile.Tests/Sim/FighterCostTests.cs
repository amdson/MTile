using System;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Phase 3 gate for Plans/FIGHTER_DESIGN_PLAN.md: FighterSpec → cost model → compiler.
// Every roster archetype compiles under the default budget with no violations; each
// hard constraint of §3.2 has a spec that trips it with a message a designer can read;
// a spec over any single currency is refused (no blueprint at all); and the physics the
// prices claim actually shows up in the sim — the Brick is heavier AND slower than the
// Sprinter. Runs against the C# defaults of FighterCostConfig (nothing here Loads the
// json), which ConfigLayoutTests keeps equal to the shipped file's values by review.
public class FighterCostTests(ITestOutputHelper output)
{
    private static readonly PhysicsCostModel Model = new(new FighterCostConfig());

    public static TheoryData<string> Archetypes()
    {
        var d = new TheoryData<string>();
        foreach (var make in FighterRoster.All) d.Add(make().Name);
        return d;
    }

    private static FighterSpec Roster(string name) => FighterRoster.All.Select(f => f()).Single(s => s.Name == name);

    private static CompileResult Compile(FighterSpec s) => FighterCompiler.Compile(s, Model);

    // ── The roster ───────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Archetypes))]
    public void EveryArchetypeCompilesUnderBudget(string name)
    {
        var r = Compile(Roster(name));
        output.WriteLine($"{name}:\n{r.Report()}");
        Assert.Empty(r.Violations);
        Assert.NotNull(r.Blueprint);
        Assert.True(r.Total.Mass   <= r.Budget.MaxMass);
        Assert.True(r.Total.Slots  <= r.Budget.MaxSlots);
        Assert.True(r.Total.Points <= r.Budget.MaxPoints);
        Assert.Equal(r.Total.Mass, r.Blueprint.Mass);
    }

    [Fact]
    public void BruteShapedBody_CompilesToTheBrutesMass()
    {
        // The coefficient anchor from FighterCosts.cs: 3 HP, a stock melee, a 70 px/s
        // walk and a ~260 px/s jump at the default reaction time. Since §16 the body term
        // is gone (radius is derived from mass), so the anchor is the Brute's 1.2 minus
        // its 0.36 body term; the derived radius at Density 1 is then ≈ 11·√0.84 ≈ 10 px.
        var brute = new FighterSpec
        {
            Name = "BruteAnchor", Kind = EntityKind.FighterSlot0,
            Health = 3f, GroundPower = 100f, JumpImpulse = 312f,
            Actions = { ActionSpec.Default(ActionKind.Melee) },
            Brain = s => new FighterCloserBrain(s),
        };
        var r = Compile(brute);
        output.WriteLine(r.Report());
        Assert.Empty(r.Violations);
        Assert.InRange(r.Total.Mass, 0.80f, 0.90f);
        Assert.InRange(r.Blueprint.Radius, 9.5f, 10.5f);
    }

    // ── Hard constraints (§3.2) ──────────────────────────────────────────────

    [Fact]
    public void FlyerThatBuysHealth_IsTooHeavyToFly()
    {
        var s = FighterRoster.Flyer();
        s.Health = 6f;                                   // +0.4 mass: 900 / 1.67 < 600
        AssertRefused(s, "fly");
    }

    [Fact]
    public void WeakJump_IsRefused()
    {
        var s = FighterRoster.Gunner();
        s.JumpImpulse = 100f;                            // 100 / 1.2 ≈ 83 px/s < v_min 150
        AssertRefused(s, "jump");
    }

    [Fact]
    public void WeakLegs_AreRefused()
    {
        var s = FighterRoster.Brick();
        s.GroundPower = 60f;                             // 60 / 2.34 ≈ 26 px/s² < a_min 40
        AssertRefused(s, "walk");
    }

    [Fact]
    public void RootedWithGroundPower_IsRefused()
    {
        var s = FighterRoster.Turret();
        s.GroundPower = 100f;
        AssertRefused(s, "Rooted");
    }

    [Fact]
    public void RootedWithThrust_IsRefused()
    {
        var s = FighterRoster.Turret();
        s.Thrust = 2000f;
        AssertRefused(s, "Rooted");
    }

    [Fact]
    public void StrengthBelowFloor_IsRefused()
    {
        var s = FighterRoster.Gunner();
        s.Strength = 0.2f;
        AssertRefused(s, "Strength");
    }

    [Fact]
    public void MeleeTriggerBeyondItsReach_FailsTheOrderingRule()
    {
        var s = FighterRoster.Brick();
        var a = s.Actions[0];
        a.MaxRange = 60f;                                // reach is 8 + 24 + 6 = 38
        s.Actions[0] = a;
        AssertRefused(s, "ordering");
    }

    [Fact]
    public void ActionCostingMoreThanTheReserve_IsRefused()
    {
        var s = FighterRoster.Gunner();
        s.EnergyReserve = 0.5f;                          // a stock shot is 1.0
        AssertRefused(s, "unaffordable");
    }

    // ── Budget: over any ONE currency is refused ─────────────────────────────

    [Fact]
    public void OverMass_IsRefused()
    {
        var s = FighterRoster.Gunner();
        s.Health = 20f;                                  // +1.7 mass
        var r = AssertRefused(s, "mass budget");
        Assert.True(r.Total.Slots  <= r.Budget.MaxSlots);
        Assert.True(r.Total.Points <= r.Budget.MaxPoints);
    }

    [Fact]
    public void OverSlots_IsRefused()
    {
        var s = FighterRoster.Gunner();
        s.Actions.Clear();
        for (int i = 0; i < 5; i++) s.Actions.Add(ActionSpec.Default(ActionKind.Contact));
        var r = AssertRefused(s, "slot budget");
        Assert.True(r.Total.Mass   <= r.Budget.MaxMass);
        Assert.True(r.Total.Points <= r.Budget.MaxPoints);
    }

    [Fact]
    public void OverPoints_IsRefused()
    {
        var s = FighterRoster.Gunner();
        s.EnergyReserve = 8f;                            // afford a bolt, so points is the only failure
        s.Actions.Clear();
        s.Actions.Add(ActionSpec.Default(ActionKind.RailShot));   // 2 points + memory 1 = 3 > 2
        var r = AssertRefused(s, "points budget");
        Assert.True(r.Total.Mass  <= r.Budget.MaxMass);
        Assert.True(r.Total.Slots <= r.Budget.MaxSlots);
    }

    private CompileResult AssertRefused(FighterSpec s, string word)
    {
        var r = Compile(s);
        output.WriteLine(r.Report());
        Assert.Null(r.Blueprint);
        Assert.Contains(r.Violations, v => v.Contains(word, StringComparison.OrdinalIgnoreCase));
        return r;
    }

    // ── The report ───────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Archetypes))]
    public void ReportPartsSumToTheTotal(string name)
    {
        var r = Compile(Roster(name));
        var sum = r.Parts.Aggregate(Cost.Zero, (acc, p) => acc + p.Cost);
        Assert.Equal(r.Total.Mass,   sum.Mass,   5);
        Assert.Equal(r.Total.Energy, sum.Energy, 5);
        Assert.Equal(r.Total.Slots,  sum.Slots);
        Assert.Equal(r.Total.Points, sum.Points);
        Assert.Equal(2 + r.ActionCosts.Count, r.Parts.Count());
    }

    [Theory]
    [MemberData(nameof(Archetypes))]
    public void CompilingTwiceGivesIdenticalCosts(string name)
    {
        var a = Compile(Roster(name));
        var b = Compile(Roster(name));
        Assert.Equal(a.Total, b.Total);
        Assert.Equal(a.Parts.ToArray(), b.Parts.ToArray());
        Assert.Equal(a.Blueprint.Mass, b.Blueprint.Mass);
    }

    [Fact]
    public void CompiledActionsChargeThePricedEnergy()
    {
        var s = FighterRoster.Gunner();
        var a = s.Actions[0];
        a.EnergyCost = 99f;                              // the model's price overwrites this
        s.Actions[0] = a;
        var r = Compile(s);
        var act = r.Blueprint.Actions()[0];
        Assert.Equal(r.ActionCosts[0].Cost.Energy, act.Spec.EnergyCost, 5);
        Assert.Equal(1.0f, act.Spec.EnergyCost, 3);      // 0.002 · 500 px/s · 1.0 dmg
    }

    [Fact]
    public void MovementListFollowsWhatWasBought()
    {
        var flyer  = Compile(FighterRoster.Flyer()).Blueprint.Movement();
        var turret = Compile(FighterRoster.Turret()).Blueprint.Movement();
        var sprint = Compile(FighterRoster.Sprinter()).Blueprint.Movement();

        Assert.IsType<EnemyIdleState>(flyer[0]);
        Assert.Contains(flyer, m => m is EnemyFlyState);
        Assert.DoesNotContain(flyer, m => m is EnemyAttackHoldState);   // would ground it
        Assert.Single(turret);                                          // rooted: Idle only
        Assert.Contains(sprint, m => m is EnemyChaseState);
        Assert.Contains(sprint, m => m is EnemyJumpState);
        Assert.Contains(sprint, m => m is EnemyAttackHoldState);
        Assert.Contains(sprint, m => m is EnemyStaggerState);
    }

    // ── The physics the prices claim ─────────────────────────────────────────

    private static ChunkMap Floor() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: -4, originTileY: 0);

    private const float FloorTopY = 8 * Chunk.TileSize;

    // Displacement toward a player 200 px away over `frames`, spawned via the registry
    // (the path a stage uses). One fighter per sim so they cannot collide.
    private static float Walk(EntityKind kind, float radius, int frames)
    {
        var start = new Vector2(0f, FloorTopY - radius - 1f);
        var sim = new Simulation(Floor(), new Vector2(200f, FloorTopY - 12f),
                                 g => g.SpawnEntity(EnemyFactory.Create(kind, start)));
        var e = (EnemyEntity)sim.Entities[0];
        for (int f = 0; f < frames; f++) sim.Step(default);
        return e.Body.Position.X - start.X;
    }

    [Fact]
    public void BrickIsHeavierAndSlowerThanSprinter()
    {
        var brick  = Compile(FighterRoster.Brick());
        var sprint = Compile(FighterRoster.Sprinter());
        Assert.True(brick.Total.Mass > sprint.Total.Mass,
            $"Brick {brick.Total.Mass:F3} should outweigh Sprinter {sprint.Total.Mass:F3}");

        float dBrick  = Walk(EntityKind.Brick,    brick.Blueprint.Radius,  30);
        float dSprint = Walk(EntityKind.Sprinter, sprint.Blueprint.Radius, 30);
        output.WriteLine($"30 frames: Brick {dBrick:F1} px, Sprinter {dSprint:F1} px");
        Assert.True(dBrick > 0f, "the Brick never walked toward the player");
        Assert.True(dSprint > dBrick * 1.5f,
            $"Sprinter ({dSprint:F1} px) should clearly out-walk the Brick ({dBrick:F1} px)");
    }

    [Fact]
    public void FighterSlot_ReRegistersThroughTheCompiler()
    {
        var s = FighterRoster.Gunner();
        s.Kind = EntityKind.FighterSlot3;
        var r = FighterCompiler.Register(s, Model);
        Assert.True(r.IsValid, r.Report());
        var e = EnemyFactory.Create(EntityKind.FighterSlot3, Vector2.Zero);
        Assert.Equal(EntityKind.FighterSlot3, e.Kind);
        Assert.Equal(r.Total.Mass, e.Mass);
    }

    // ── The stage ────────────────────────────────────────────────────────────

    private static string FindLevels()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            string c = Path.Combine(d.FullName, "Levels");
            if (File.Exists(Path.Combine(c, "flat.json"))) return c;
            d = d.Parent;
        }
        return null;
    }

    [Fact]
    public void FightersStage_SpawnsTheSixArchetypes()
    {
        var levels = FindLevels();
        if (levels == null) { output.WriteLine("Levels/ not found — skipping."); return; }

        var registered = Stages.Get("fighters");
        // Same stage, with the level resolved absolutely (the test host has no title
        // container) — the pattern InfiniteTerrainTests uses for "spires".
        var stage = new Stage
        {
            Name          = registered.Name,
            TerrainConfig = Path.Combine(levels, registered.TerrainConfig),
            PlayerSpawn   = registered.PlayerSpawn,
            Populate      = registered.Populate,
        };
        var sim = new Simulation(new GameConfig(), stage);

        var kinds = sim.Entities.OfType<EnemyEntity>().Select(e => e.Kind).ToList();
        Assert.Equal(6, kinds.Count);
        foreach (var k in new[] { EntityKind.Brick, EntityKind.Sprinter, EntityKind.Gunner,
                                  EntityKind.Flyer, EntityKind.Builder, EntityKind.FighterTurret })
            Assert.Contains(k, kinds);

        // Smoke: two seconds of sim with nobody throwing.
        for (int f = 0; f < 120; f++) sim.Step(default);
    }
}
