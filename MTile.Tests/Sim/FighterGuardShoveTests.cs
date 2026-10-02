using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests.Sim;

// Guard and Shove (Plans/FIGHTER_PACKAGE_GUIDE.md §2). A raised guard blocks frontal hits
// for energy (no damage, no stagger, a chip of the knockback); a hit from behind, a shove,
// or a hit the meter cannot pay for breaks it and lands. Holding it drains the meter and
// slows the walk; letting go lowers it through an open recovery.
public class FighterGuardShoveTests
{
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
        s.EnergyReserve = 4f; s.EnergyRegen = 0f;
        s.GroundPower = 200f;
        s.EngageRange = 10f;
        s.Brain = _ => new Script(brain);
        return s;
    }

    private static (Simulation sim, EnemyEntity[] bots) Arena(FighterSpec left, FighterSpec right, float gap = 180f)
    {
        foreach (var spec in new[] { left, right })
        {
            var r = FighterCompiler.Compile(spec, new PhysicsCostModel());
            Assert.True(r.IsValid, string.Join("\n", r.Violations));
        }
        var entries = new List<ArenaEntry>
        {
            new(left,  FighterArena.LeftSpawn, 1),
            new(right, FighterArena.LeftSpawn + new Vector2(gap, 0f), 2),
        };
        var bots = new EnemyEntity[2];
        var sim = new Simulation(FighterArena.Flat(), FighterArena.PlayerPark,
                                 g => FighterArena.Populate(g, entries, null, bots));
        return (sim, bots);
    }

    // Guard up from frame 5 on, toward the opponent on the right.
    private static EnemyInput HoldGuard(FighterSenses s)
        => s.Frame >= 5 ? new EnemyInput { WantAttack = true, RequestedAction = 0, Guard = true,
                                           AimWorld = s.Self.Position + new Vector2(100f, 0f) }
                        : new EnemyInput();

    private static FighterSpec Post() => Tool(_ => new EnemyInput());

    // A hit on `bot` whose origin sits `dx` px to its side.
    private static Hitbox HitFrom(EnemyEntity bot, EnemyEntity src, float dx, float damage, bool shove = false)
    {
        var p = bot.Body.Position;
        var region = new BoundingBox(p.X - 8f, p.Y - 8f, p.X + 8f, p.Y + 8f);
        return new Hitbox(region, 9001 + (int)dx, damage, new Vector2(MathF.Sign(-dx) * 300f, -50f),
                          Faction.Enemy, src.Id, origin: p + new Vector2(dx, 0f), guardBreak: shove);
    }

    private static Hurtbox Hurt(EnemyEntity e) => new(e.Body.Bounds, Faction.Enemy, e.Id);

    private static void RaiseGuard(Simulation sim, EnemyEntity bot)
    {
        for (int f = 0; f < 40 && !bot.IsGuarding; f++) sim.Step(default);
        Assert.True(bot.IsGuarding, "the guard never went up");
        Assert.Equal(1, bot.GuardFacing);
    }

    [Fact]
    public void FrontalHit_IsBlockedForEnergy_NoDamage_NoStagger()
    {
        var (sim, bots) = Arena(Tool(HoldGuard, ActionSpec.Default(ActionKind.Guard)), Post());
        var g = bots[0];
        RaiseGuard(sim, g);
        float hp = g.Health, e = g.Energy;
        var k = FighterCosts.Current;
        g.OnHit(HitFrom(g, bots[1], +30f, 3f), Hurt(g));
        Assert.Equal(hp, g.Health);
        Assert.Equal(e - (k.GuardCostPerHit + k.GuardCostPerDamage * 3f), g.Energy, 4);
        Assert.False(g.IsStaggered);
        Assert.True(g.IsGuarding);
    }

    [Fact]
    public void HitFromBehind_Lands_AndLowersTheGuard()
    {
        var (sim, bots) = Arena(Tool(HoldGuard, ActionSpec.Default(ActionKind.Guard)), Post());
        var g = bots[0];
        RaiseGuard(sim, g);
        float hp = g.Health;
        g.OnHit(HitFrom(g, bots[1], -30f, 1f), Hurt(g));
        Assert.Equal(hp - 1f, g.Health, 4);
        Assert.False(g.IsGuarding);
    }

    [Fact]
    public void AnAttackerOnTopOfTheGuard_CountsAsFront()
    {
        // A dash that ends on the body: the origin sits a hair behind centre. Blocked.
        var (sim, bots) = Arena(Tool(HoldGuard, ActionSpec.Default(ActionKind.Guard)), Post());
        var g = bots[0];
        RaiseGuard(sim, g);
        float hp = g.Health;
        g.OnHit(HitFrom(g, bots[1], -2f, 3f), Hurt(g));
        Assert.Equal(hp, g.Health);
        Assert.True(g.IsGuarding);
    }

    [Fact]
    public void Shove_BreaksTheGuard_EvenFromTheFront()
    {
        var (sim, bots) = Arena(Tool(HoldGuard, ActionSpec.Default(ActionKind.Guard)), Post());
        var g = bots[0];
        RaiseGuard(sim, g);
        float hp = g.Health;
        g.OnHit(HitFrom(g, bots[1], +30f, 0.3f, shove: true), Hurt(g));
        Assert.Equal(hp - 0.3f, g.Health, 4);
        Assert.False(g.IsGuarding);
    }

    [Fact]
    public void AHitTheMeterCannotPayFor_BreaksTheGuard()
    {
        var (sim, bots) = Arena(Tool(HoldGuard, ActionSpec.Default(ActionKind.Guard)), Post());
        var g = bots[0];
        RaiseGuard(sim, g);
        g.Energy = 0.2f;                       // below GuardCostPerHit alone
        float hp = g.Health;
        g.OnHit(HitFrom(g, bots[1], +30f, 2f), Hurt(g));
        Assert.Equal(hp - 2f, g.Health, 4);
        Assert.False(g.IsGuarding);
    }

    [Fact]
    public void Guard_IsHeldWhileAsked_DrainsTheMeter_SlowsTheWalk_AndLowersOnRelease()
    {
        // Walk right with the guard up for frames 5..60, then let go (still walking).
        EnemyInput Brain(FighterSenses s)
        {
            var i = new EnemyInput { MoveDir = new Vector2(1f, 0f), AimWorld = s.Self.Position + new Vector2(100f, 0f) };
            if (s.Frame >= 5 && s.Frame < 60) { i.WantAttack = true; i.RequestedAction = 0; i.Guard = true; }
            return i;
        }
        var (sim, bots) = Arena(Tool(Brain, ActionSpec.Default(ActionKind.Guard)), Post(), gap: 400f);
        var g = bots[0];
        float e0 = 0f, vGuard = 0f;
        for (int f = 0; f < 120; f++)
        {
            sim.Step(default);
            if (f == 20) e0 = g.Energy;
            if (f == 55) { Assert.True(g.IsGuarding); vGuard = g.Body.Velocity.X; Assert.True(g.Energy < e0); }
        }
        Assert.False(g.IsGuarding);
        Assert.Equal(ActionKind.Special, g.TellKind);                     // lowered and done
        Assert.True(g.Body.Velocity.X > vGuard * 1.8f, $"walk {g.Body.Velocity.X:0} vs guarded {vGuard:0}: not slowed");
    }

    [Fact]
    public void AShoveIntoARaisedGuard_PushesItFarAndStaggersIt()
    {
        // The guard stays up facing the shover; the shover walks in and shoves.
        EnemyInput Shover(FighterSenses s) => new()
        {
            MoveDir = new Vector2(-1f, 0f), AimWorld = s.Self.Position - new Vector2(100f, 0f),
            WantAttack = true, RequestedAction = 0,
        };
        var guard = Tool(HoldGuard, ActionSpec.Default(ActionKind.Guard));
        var shover = Tool(Shover, ActionSpec.Default(ActionKind.Shove));
        var (sim, bots) = Arena(guard, shover, gap: 60f);
        var g = bots[0];
        float x0 = g.Body.Position.X;
        bool staggered = false;
        float hp = g.Health;
        for (int f = 0; f < 120; f++) { sim.Step(default); staggered |= g.IsStaggered; }
        Assert.True(g.Health < hp, "the shove never landed");
        Assert.True(staggered, "a shove into a guard must stagger it");
        Assert.True(x0 - g.Body.Position.X > 30f, $"pushed only {x0 - g.Body.Position.X:0} px");
    }

    [Fact]
    public void SnapshotWhileGuarding_ReplaysBitIdentically_ThroughAShove()
    {
        EnemyInput Shover(FighterSenses s) => new()
        {
            MoveDir = new Vector2(-1f, 0f), AimWorld = s.Self.Position - new Vector2(100f, 0f),
            WantAttack = s.Frame > 40, RequestedAction = 0,
        };
        var (sim, bots) = Arena(Tool(HoldGuard, ActionSpec.Default(ActionKind.Guard)),
                                Tool(Shover, ActionSpec.Default(ActionKind.Shove)), gap: 80f);
        for (int f = 0; f < 30; f++) sim.Step(default);
        Assert.True(bots[0].IsGuarding);
        var snap = sim.Snapshot();
        var live = new List<ulong>();
        for (int f = 0; f < 200; f++) { sim.Step(default); live.Add(sim.Checksum()); }
        sim.Restore(snap);
        for (int f = 0; f < 200; f++)
        {
            sim.Step(default);
            Assert.True(sim.Checksum() == live[f], $"replay diverged {f + 1} steps after the restore");
        }
    }

    [Fact]
    public void ARammersTouch_OnARaisedGuard_DoesNothing()
    {
        // Thornbull's 7-damage touch from the front: blocked for 0.3 + 0.7 energy.
        var (sim, bots) = Arena(Tool(HoldGuard, ActionSpec.Default(ActionKind.Guard)),
                                FighterPackages.Find("Thornbull").Spec(), gap: 160f);
        var g = bots[0];
        float hp = g.Health, e = 0f;
        bool touched = false;
        for (int f = 0; f < 240 && !touched; f++)
        {
            float before = g.Energy;
            sim.Step(default);
            if (bots[1].TellKind == ActionKind.Contact && g.Energy < before - 0.5f) touched = true;
            e = g.Energy;
        }
        Assert.True(touched, "Thornbull never touched the guard");
        Assert.Equal(hp, g.Health);
        Assert.True(g.IsGuarding);
    }

    [Fact]
    public void KnockbackAboveStock_IsPricedOnEveryKind_BelowStockIsFree()
    {
        // A swing must not buy a free shove: push above the kind's stock costs mass.
        var model = new PhysicsCostModel();
        var s = Tool(_ => new EnemyInput());
        var stock = ActionSpec.Default(ActionKind.Melee);
        var hard = stock; hard.Knockback = new Vector2(1000f, -150f);
        var soft = stock; soft.Knockback = new Vector2(50f, 0f);
        float m0 = model.ActionCost(stock, s).Mass;
        Assert.True(model.ActionCost(hard, s).Mass > m0 + 0.1f);
        Assert.Equal(m0, model.ActionCost(soft, s).Mass, 5);
    }

    [Fact]
    public void StockGuardAndShove_Compile_AndOutOfBoundsKnobsAreRefused()
    {
        var model = new PhysicsCostModel();
        var ok = FighterCompiler.Compile(Tool(_ => new EnemyInput(), ActionSpec.Default(ActionKind.Guard),
                                              ActionSpec.Default(ActionKind.Shove)), model);
        Assert.True(ok.IsValid, string.Join("\n", ok.Violations));
        Assert.True(ok.ActionCosts[1].Cost.Mass > ActionSpec.Default(ActionKind.Shove).Damage * 0.005f * 16f,
                    "a shove pays for its push, not only its damage");

        var longHold = ActionSpec.Default(ActionKind.Guard); longHold.Active = 9f;
        Assert.False(FighterCompiler.Compile(Tool(_ => new EnemyInput(), longHold), model).IsValid);
        var hugePush = ActionSpec.Default(ActionKind.Shove); hugePush.Knockback = new Vector2(3000f, 0f);
        Assert.False(FighterCompiler.Compile(Tool(_ => new EnemyInput(), hugePush), model).IsValid);
        var farTrigger = ActionSpec.Default(ActionKind.Shove); farTrigger.MaxRange = 60f;   // beyond its reach
        Assert.False(FighterCompiler.Compile(Tool(_ => new EnemyInput(), farTrigger), model).IsValid);
    }
}
