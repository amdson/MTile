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

    [Fact]
    public void Mine_LiesOnTheFloor_IsVisibleToBothSides_AndArms()
    {
        var m = ActionSpec.Default(ActionKind.Mine);
        var (sim, bots) = Arena(Tool(s => s.Frame == 10 ? Ask(0) : new EnemyInput(), m), Post());
        for (int f = 0; f < 30; f++) sim.Step(default);
        var mine = Assert.Single(Mines(sim));
        Assert.Equal(bots[0].Id, mine.Owner);
        Assert.True(mine.Body.Position.Y < FighterArena.FloorTopY && mine.Body.Position.Y > FighterArena.FloorTopY - 10f,
                    $"mine at y {mine.Body.Position.Y}, floor top {FighterArena.FloorTopY}");
        Span<MineView> views = stackalloc MineView[4];
        Assert.Equal(1, bots[1].Senses.Mines(views));
        Assert.False(views[0].Armed);          // 20 frames old: not armed yet
        for (int f = 0; f < 30; f++) sim.Step(default);
        Assert.Equal(1, bots[1].Senses.Mines(views));
        Assert.True(views[0].Armed);
        Assert.Equal(1, views[0].Team);
    }

    [Fact]
    public void Mine_GoesOffOnItsFuse_AndHurtsItsOwner()
    {
        var m = ActionSpec.Default(ActionKind.Mine);
        var (sim, bots) = Arena(Tool(s => s.Frame == 10 ? Ask(0) : new EnemyInput(), m), Post());
        float hp = bots[0].Health;
        for (int f = 0; f < 30; f++) sim.Step(default);
        Assert.Single(Mines(sim));
        for (int f = 0; f < (int)(m.Fuse * 60) + 10; f++) sim.Step(default);
        Assert.Empty(Mines(sim));
        Assert.True(bots[0].Health < hp, $"owner standing on its mine took nothing ({bots[0].Health} of {hp})");
        Assert.Equal(bots[1].MaxHealth, bots[1].Health);   // 180 px away: out of reach
    }

    [Fact]
    public void Mine_OpposingContactSetsItOff_OwnerContactDoesNot()
    {
        // The owner lays a mine and walks off left; a Brick on the other team walks in
        // from close by, steps on it, and takes the blast long before the fuse.
        var m = ActionSpec.Default(ActionKind.Mine);
        m.Fuse = 6f;
        var owner = Tool(s => s.Frame == 5 ? Ask(0) : s.Frame > 30 ? new EnemyInput { MoveDir = new Vector2(-1f, 0f) } : new EnemyInput(), m);
        var brick = FighterRoster.Brick();
        var (sim, bots) = Arena(owner, brick, FighterArena.LeftSpawn + new Vector2(70f, 0f));
        float hp = bots[1].Health;
        int gone = -1;
        for (int f = 0; f < 300 && gone < 0; f++)
        {
            sim.Step(default);
            if (f > 20 && Mines(sim).Count == 0) gone = f;
        }
        Assert.True(gone > 0 && gone < 300, "the mine never went off under the walker");
        Assert.True(bots[1].Health < hp, "the walker stepped on the mine and took nothing");
        Assert.True(gone < m.Fuse * 60, "went off on the fuse, not on contact");
    }

    [Fact]
    public void Mine_AnyHitSetsItOff_EvenUnarmed()
    {
        var m = ActionSpec.Default(ActionKind.Mine);
        var (sim, _) = Arena(Tool(s => s.Frame == 10 ? Ask(0) : new EnemyInput(), m), Post());
        for (int f = 0; f < 26; f++) sim.Step(default);   // laid ~frame 22, arms ~18 frames later
        var mine = Assert.Single(Mines(sim));
        Assert.False(mine.Armed);
        mine.Health -= 0.1f;                 // a hit
        sim.Step(default); sim.Step(default); sim.Step(default);
        Assert.Empty(Mines(sim));
    }

    [Fact]
    public void Mine_AtMostMaxLivePerFighter()
    {
        var m = ActionSpec.Default(ActionKind.Mine);
        var (sim, _) = Arena(Tool(_ => Ask(0, -1f), m), Post());
        int most = 0;
        for (int f = 0; f < 240; f++) { sim.Step(default); most = Math.Max(most, Mines(sim).Count); }
        Assert.Equal(m.MaxLive, most);
    }

    // ── Rollback ────────────────────────────────────────────────────────────

    [Fact]
    public void SnapshotWithALiveMineAndADash_ReplaysBitIdentically()
    {
        // Snapshot while a mine is armed; inside the window: a dash, the fuse firing (the
        // mine is destroyed, so the restore must rehydrate it), and a second mine.
        var m = ActionSpec.Default(ActionKind.Mine);
        var d = ActionSpec.Default(ActionKind.Dash);
        EnemyInput Brain(FighterSenses s) => s.Frame switch
        {
            10 => Ask(0),
            60 => Ask(1, -1f),
            220 => Ask(0),
            _ => new EnemyInput(),
        };
        var (sim, _) = Arena(Tool(Brain, m, d), Post());
        for (int f = 0; f < 40; f++) sim.Step(default);
        Assert.Single(Mines(sim));
        var snap = sim.Snapshot();
        var live = new List<ulong>();
        for (int f = 0; f < 300; f++) { sim.Step(default); live.Add(sim.Checksum()); }
        sim.Restore(snap);
        for (int f = 0; f < 300; f++)
        {
            sim.Step(default);
            Assert.True(sim.Checksum() == live[f], $"replay diverged {f + 1} steps after the restore");
        }
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
        }
        var r = FighterCompiler.Compile(Tool(_ => new EnemyInput(), d, m), new PhysicsCostModel());
        Assert.False(r.IsValid);
    }
}
