using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests.Sim;

// Dash and Mine (Plans/FIGHTER_GUIDE.md): request-only tools. A dash moves the body
// Speed × Active and publishes no hurtbox for its first Intangible seconds; a mine lies
// on the floor and goes off on opposing contact (once armed), on any hit, or on its fuse,
// with a Neutral blast that hurts everyone in reach — its owner included.
public class FighterDashMineTests
{
    // A scripted brain: the test decides each frame's input from the senses.
    private sealed class Script(Func<FighterSenses, EnemyInput> f) : FighterController
    {
        protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m) => f(s);
    }

    private static FighterSpec Tool(Func<FighterSenses, EnemyInput> brain, params ActionSpec[] actions)
    {
        var s = FighterRoster.Brick();
        s.Name = "Tool";
        s.Actions.Clear();
        foreach (var a in actions) s.Actions.Add(a);
        s.PreferredAction = -1;
        s.Health = 6f; s.Armor = 0f; s.Strength = 1f;
        s.EnergyReserve = 4f; s.EnergyRegen = 2f;
        s.GroundPower = 200f;
        s.Brain = _ => new Script(brain);
        return s;
    }

    // A target that never moves or attacks.
    private static FighterSpec Post()
    {
        var s = Tool(_ => new EnemyInput());
        s.Name = "Post";
        return s;
    }

    private static (Simulation sim, EnemyEntity[] bots) Arena(FighterSpec left, FighterSpec right,
                                                             Vector2? rightPos = null)
    {
        foreach (var spec in new[] { left, right })
        {
            var r = FighterCompiler.Compile(spec, new PhysicsCostModel());
            Assert.True(r.IsValid, string.Join("\n", r.Violations));
        }
        var entries = new List<ArenaEntry>
        {
            new(left,  FighterArena.LeftSpawn, 1),
            new(right, rightPos ?? FighterArena.RightSpawn, 2),
        };
        var bots = new EnemyEntity[2];
        var sim = new Simulation(FighterArena.Flat(), FighterArena.PlayerPark,
                                 g => FighterArena.Populate(g, entries, null, bots));
        return (sim, bots);
    }

    private static List<FighterMine> Mines(Simulation g)
        => g.Entities.OfType<FighterMine>().Where(m => !m.IsDead).ToList();

    private static EnemyInput Ask(int action, float moveX = 0f)
        => new() { WantAttack = true, RequestedAction = action, MoveDir = new Vector2(moveX, 0f) };

    // ── Dash ────────────────────────────────────────────────────────────────

    [Fact]
    public void Dash_IsNeverPickedUnlessRequested()
    {
        var (sim, bots) = Arena(Tool(_ => new EnemyInput { WantAttack = true }, ActionSpec.Default(ActionKind.Dash)), Post());
        for (int f = 0; f < 240; f++)
        {
            sim.Step(default);
            Assert.Equal(ActionKind.Special, bots[0].TellKind);
        }
    }

    [Fact]
    public void Dash_CoversItsDistance_InTheRequestedDirection_AndIsUntouchableBriefly()
    {
        var d = ActionSpec.Default(ActionKind.Dash);
        var (sim, bots) = Arena(Tool(s => s.Frame >= 30 && s.Frame < 32 ? Ask(0, -1f) : new EnemyInput(), d), Post());
        for (int f = 0; f < 29; f++) sim.Step(default);
        float x0 = bots[0].Body.Position.X;
        int intangible = 0;
        for (int f = 0; f < 60; f++)
        {
            sim.Step(default);
            if (bots[0].IsIntangible)
            {
                intangible++;
                sim.Step(default);   // hurtboxes for this frame were published from the intangible vars
                Assert.DoesNotContain(sim.Hurtboxes.All, hb => hb.Target == bots[0].Id);
                f++;
            }
        }
        float moved = x0 - bots[0].Body.Position.X;
        Assert.InRange(moved, d.Speed * d.Active * 0.8f, d.Speed * d.Active * 2f);
        Assert.InRange(intangible, 1, 6);
        // Hittable again afterwards.
        Assert.Contains(sim.Hurtboxes.All, hb => hb.Target == bots[0].Id);
    }

    // ── Mine ────────────────────────────────────────────────────────────────

    // Summon at a point; `feet` = the floor just under the body.
    private static EnemyInput Summon(Vector2 at, float moveX = 0f)
        => new() { WantAttack = true, RequestedAction = 0, AimWorld = at, MoveDir = new Vector2(moveX, 0f) };
    private static Vector2 Feet(FighterSenses s) => s.Self.Position + new Vector2(0f, s.Self.Radius);
    private const int Summoned = 75;   // frames after a request by which the 1.0 s summon has landed

    [Fact]
    public void Mine_IsSummonedAtTheAimedPoint_AfterAPublicWarning_AndArms()
    {
        var m = ActionSpec.Default(ActionKind.Mine);
        var spot = FighterArena.LeftSpawn + new Vector2(90f, -40f);   // mid-air, between the two
        var (sim, bots) = Arena(Tool(s => s.Frame == 10 ? Summon(spot) : new EnemyInput(), m), Post());
        Span<MineView> views = stackalloc MineView[4];

        // During the summon: nothing in the world yet, but the opponent sees it coming.
        for (int f = 0; f < 40; f++) sim.Step(default);
        Assert.Empty(Mines(sim));
        Assert.Equal(1, bots[1].Senses.Mines(views));
        Assert.True(views[0].Pending);
        Assert.InRange(views[0].AppearsIn, 0.1f, m.Windup);
        Assert.True(Vector2.Distance(views[0].Position, spot) < 0.01f);

        for (int f = 0; f < Summoned - 40; f++) sim.Step(default);
        var mine = Assert.Single(Mines(sim));
        Assert.Equal(bots[0].Id, mine.Owner);
        Assert.True(Vector2.Distance(mine.Body.Position, spot) < 0.01f, $"mine at {mine.Body.Position}, aimed {spot}");
        Assert.Equal(1, bots[1].Senses.Mines(views));
        Assert.False(views[0].Pending);
        for (int f = 0; f < 30; f++) sim.Step(default);
        Assert.Equal(1, bots[1].Senses.Mines(views));
        Assert.True(views[0].Armed);
        Assert.True(Vector2.Distance(Assert.Single(Mines(sim)).Body.Position, spot) < 0.01f);   // rooted: never falls
    }

    [Fact]
    public void Mine_AimedTooFar_IsPulledInToMaxRange_AndInsideRockFizzles()
    {
        var m = ActionSpec.Default(ActionKind.Mine);
        var far = FighterArena.LeftSpawn + new Vector2(1000f, 0f);
        var (sim, bots) = Arena(Tool(s => s.Frame == 10 ? Summon(far) : new EnemyInput(), m), Post());
        for (int f = 0; f < Summoned; f++) sim.Step(default);
        var mine = Assert.Single(Mines(sim));
        Assert.Equal(m.MaxRange, Vector2.Distance(mine.Body.Position, bots[0].Body.Position), 1);

        var rock = new Vector2(FighterArena.LeftSpawn.X, FighterArena.FloorTopY + 8f);
        var (sim2, _) = Arena(Tool(s => s.Frame == 10 ? Summon(rock) : new EnemyInput(), m), Post());
        for (int f = 0; f < Summoned + 10; f++) sim2.Step(default);
        Assert.Empty(Mines(sim2));
    }

    [Fact]
    public void Mine_GoesOffOnItsFuse_AndHurtsItsOwner()
    {
        var m = ActionSpec.Default(ActionKind.Mine);
        var (sim, bots) = Arena(Tool(s => s.Frame == 10 ? Summon(Feet(s)) : new EnemyInput(), m), Post());
        float hp = bots[0].Health;
        for (int f = 0; f < Summoned; f++) sim.Step(default);
        Assert.Single(Mines(sim));
        for (int f = 0; f < (int)(m.Fuse * 60) + 10; f++) sim.Step(default);
        Assert.Empty(Mines(sim));
        Assert.True(bots[0].Health < hp, $"owner standing on its mine took nothing ({bots[0].Health} of {hp})");
        Assert.Equal(bots[1].MaxHealth, bots[1].Health);   // 180 px away: out of reach
    }

    [Fact]
    public void Mine_OpposingContactSetsItOff_OwnerContactDoesNot()
    {
        // The owner summons a mine 50 px ahead of itself, in the path of a Brick on the
        // other team that walks in to fight it; the Brick steps on it long before the fuse.
        // The owner stands next to it the whole time and never sets it off.
        var m = ActionSpec.Default(ActionKind.Mine);
        m.Fuse = 6f;
        var owner = Tool(s => s.Frame == 5 ? Summon(Feet(s) + new Vector2(50f, 0f)) : new EnemyInput(), m);
        var brick = FighterRoster.Brick();
        var (sim, bots) = Arena(owner, brick, FighterArena.LeftSpawn + new Vector2(150f, 0f));
        float hp = bots[1].Health;
        int gone = -1;
        for (int f = 0; f < 400 && gone < 0; f++)
        {
            sim.Step(default);
            if (f > Summoned && Mines(sim).Count == 0) gone = f;
        }
        Assert.True(gone > 0, "the mine never went off under the walker");
        Assert.True(bots[1].Health < hp, "the walker stepped on the mine and took nothing");
        Assert.True(gone < m.Fuse * 60, "went off on the fuse, not on contact");
    }

    [Fact]
    public void Mine_AnyHitSetsItOff_EvenUnarmed()
    {
        var m = ActionSpec.Default(ActionKind.Mine);
        var (sim, _) = Arena(Tool(s => s.Frame == 10 ? Summon(Feet(s)) : new EnemyInput(), m), Post());
        while (Mines(sim).Count == 0) sim.Step(default);
        var mine = Assert.Single(Mines(sim));
        Assert.False(mine.Armed);
        mine.Health -= 0.1f;                 // a hit
        sim.Step(default); sim.Step(default); sim.Step(default);
        Assert.Empty(Mines(sim));
    }

    [Fact]
    public void Mine_AtMostMaxLivePerFighter_SummonsIncluded()
    {
        var m = ActionSpec.Default(ActionKind.Mine);
        var (sim, bots) = Arena(Tool(s => Summon(Feet(s), -1f), m), Post());
        Span<MineView> views = stackalloc MineView[8];
        int most = 0;
        for (int f = 0; f < 480; f++)
        {
            sim.Step(default);
            int n = bots[1].Senses.Mines(views);
            most = Math.Max(most, n);
        }
        Assert.Equal(m.MaxLive, most);
    }

    // ── Rollback ────────────────────────────────────────────────────────────

    [Fact]
    public void SnapshotWithALiveMineAndADash_ReplaysBitIdentically()
    {
        // Snapshot at 100 (a mine armed; inside the window a dash, the fuse firing — the
        // mine is destroyed, so the restore must rehydrate it — and a second summon) and at
        // 220 (mid-way through that second summon: the locked destination must survive).
        var m = ActionSpec.Default(ActionKind.Mine);
        var d = ActionSpec.Default(ActionKind.Dash);
        EnemyInput Brain(FighterSenses s) => s.Frame switch
        {
            10  => Summon(Feet(s)),
            120 => Ask(1, -1f),
            200 => Summon(Feet(s) + new Vector2(60f, -20f)),
            _   => new EnemyInput(),
        };
        foreach (int at in new[] { 100, 220 })
        {
            var (sim, _) = Arena(Tool(Brain, m, d), Post());
            for (int f = 0; f < at; f++) sim.Step(default);
            var snap = sim.Snapshot();
            var live = new List<ulong>();
            for (int f = 0; f < 300; f++) { sim.Step(default); live.Add(sim.Checksum()); }
            sim.Restore(snap);
            for (int f = 0; f < 300; f++)
            {
                sim.Step(default);
                Assert.True(sim.Checksum() == live[f], $"snapshot at {at}: replay diverged {f + 1} steps after the restore");
            }
        }
    }

    // ── The fight record ────────────────────────────────────────────────────

    [Fact]
    public void EveryActionSpecField_SurvivesTheFightRecord()
    {
        // Every league bout and every saved fight goes through ActionSpecDto; a field it
        // drops silently reverts to zero in the bout (it did, for every Dash and Mine
        // knob, through wave 4's scoring). Set every field to a distinct value and require
        // all of them back.
        object boxed = new ActionSpec();
        int k = 1;
        foreach (var f in typeof(ActionSpec).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            object v = f.FieldType == typeof(float) ? 0.5f + k
                     : f.FieldType == typeof(int) ? 1 + k
                     : f.FieldType == typeof(Vector2) ? new Vector2(k, -k)
                     : f.FieldType == typeof(ActionKind) ? ActionKind.Mine
                     : f.FieldType == typeof(TileType) ? TileType.Stone
                     : throw new InvalidOperationException($"new field type {f.FieldType} on ActionSpec.{f.Name}: extend this test");
            f.SetValue(boxed, v);
            k++;
        }
        var a = (ActionSpec)boxed;
        var back = ActionSpecDto.From(a).ToSpec();
        foreach (var f in typeof(ActionSpec).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            Assert.True(Equals(f.GetValue(a), f.GetValue(back)), $"ActionSpec.{f.Name} is lost by the fight record");
    }

    // ── Pricing and limits ──────────────────────────────────────────────────

    [Fact]
    public void StockDashAndMine_CompileAndAreSensiblyPriced()
    {
        var model = new PhysicsCostModel();
        var s = Tool(_ => new EnemyInput(), ActionSpec.Default(ActionKind.Dash), ActionSpec.Default(ActionKind.Mine));
        var r = FighterCompiler.Compile(s, model);
        Assert.True(r.IsValid, string.Join("\n", r.Violations));
        Assert.InRange(r.ActionCosts[0].Cost.Energy, 0.4f, 0.8f);
        Assert.InRange(r.ActionCosts[1].Cost.Energy, 0.9f, 1.5f);
        // A slower arm is cheaper; a longer fuse dearer.
        var slow = ActionSpec.Default(ActionKind.Mine); slow.ArmDelay = 0.6f;
        var longer = ActionSpec.Default(ActionKind.Mine); longer.Fuse = 6f;
        float stock = model.ActionCost(ActionSpec.Default(ActionKind.Mine), s).Energy;
        Assert.True(model.ActionCost(slow, s).Energy < stock);
        Assert.True(model.ActionCost(longer, s).Energy > stock);
    }

    [Theory]
    [InlineData("intangible")]
    [InlineData("distance")]
    [InlineData("maxlive")]
    [InlineData("radius")]
    [InlineData("fuse")]
    [InlineData("range")]
    public void OutOfBoundsTools_AreRefused(string what)
    {
        var d = ActionSpec.Default(ActionKind.Dash);
        var m = ActionSpec.Default(ActionKind.Mine);
        switch (what)
        {
            case "intangible": d.Intangible = d.Active + 0.05f; break;
            case "distance":   d.Speed = 2000f; break;
            case "maxlive":    m.MaxLive = 9; break;
            case "radius":     m.BlastRadius = 300f; break;
            case "fuse":       m.Fuse = m.ArmDelay; break;
            case "range":      m.MaxRange = 900f; break;
        }
        var r = FighterCompiler.Compile(Tool(_ => new EnemyInput(), d, m), new PhysicsCostModel());
        Assert.False(r.IsValid);
    }
}
