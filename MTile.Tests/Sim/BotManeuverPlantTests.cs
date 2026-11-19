using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;
using static MTile.Tests.Sim.PlantTestArena;

namespace MTile.Tests.Sim;

// Bot plant play (Plans/PLANT_SEEDS_PLAN.md §8b, Net/Bot/Maneuvers/PlantManeuvers.cs) on
// the plant arena: a dirt surface at row 12 (stone at columns 30-35), P1 an AFK opponent,
// P2 the shipped Ranger (no learner, so selection is argmax and repeatable).
public class BotManeuverPlantTests(ITestOutputHelper output)
{
    private const int BotCol = 40;    // 36 tiles from P1 at column 4
    private const int Row    = FloorRow - 1;

    private sealed class Duel
    {
        public Simulation Sim;
        public PlayerCharacter P1, P2;
        public BulldogBot Bot;
        public int Frame;
        public PlantSpitterManeuver Planter => Bot.Maneuvers.OfType<PlantSpitterManeuver>().Single();
        public PlantStrikeManeuver  Striker => Bot.Maneuvers.OfType<PlantStrikeManeuver>().Single();

        public void Step(Func<PlayerInput, PlayerInput> edit = null)
        {
            var input = Bot.Poll(Sim, Frame++);
            if (edit != null) input = edit(input);
            Sim.Step(default, input);
        }
    }

    private static Duel Make(int p1Col = 4, Action<Simulation, PlayerCharacter> setup = null, Action<BulldogBot> tweak = null)
    {
        var sim = new Simulation(Terrain(), Spawn(p1Col));
        var (p2, _) = sim.AddSecondaryPlayer(Spawn(BotCol));
        setup?.Invoke(sim, p2);
        var bot = RangerBot.Build(seed: 7, self: 0, out _, learn: false);
        tweak?.Invoke(bot);
        return new Duel { Sim = sim, P1 = sim.Player, P2 = p2, Bot = bot };
    }

    private static List<SpitterPlant> Spitters(Simulation s, PlayerCharacter owner)
        => s.Entities.OfType<SpitterPlant>().Where(p => !p.IsDead && p.Owner == owner.Id).ToList();

    // A view of the settled arena for Score checks.
    private static BotView View(Simulation sim, PlayerCharacter self, PlayerCharacter target, int frame)
        => new() { Sim = sim, Self = self, Target = target, Frame = frame,
                   Me = self.Body.Position, TargetPos = target.Body.Position };

    // ── Planting ────────────────────────────────────────────────────────────

    [Fact]
    public void Plant_PlantsOneSpitterOnALegalSite_AndEndsOnTheBlockBar()
    {
        var d = Make();
        bool sawSeedBar = false;
        int maxLive = 0;
        for (int f = 0; f < 240; f++)
        {
            d.Step();
            sawSeedBar |= d.P2.SeedBarActive;
            maxLive = Math.Max(maxLive, Spitters(d.Sim, d.P2).Count);
        }
        var live = Spitters(d.Sim, d.P2);
        output.WriteLine($"planted {d.Planter.Planted} @ {d.Planter.PlantedFrame} cell {d.Planter.PlantedCell}; live {live.Count}");
        Assert.True(sawSeedBar, "the bot should swap to the seed bar");
        Assert.True(d.Planter.Planted);
        Assert.Single(live);
        Assert.Equal(1, maxLive);
        var p = live[0];
        Assert.Equal(PlantRoot.Below, p.Root);
        Assert.True(PlantRules.IsDirtRoot(d.Sim.Chunks, p.CellX, p.CellY + 1), "rooted in dirt");
        Assert.Equal(Row, p.CellY);
        Assert.False(d.P2.SeedBarActive, "the bot must end on the block bar");
        Assert.Equal(TileType.Dirt, d.P2.ActiveBlockType);   // the block selection is untouched
    }

    // Hit on the very frame the bar first reads "seed" (F was down last frame, so the
    // abort itself cannot make a press edge): the constraint must bring the bar back.
    [Fact]
    public void Plant_HitMidScript_EndsOnTheBlockBar_WithoutPlanting()
    {
        var d = Make();
        bool hit = false;
        int hitFrame = -1;
        for (int f = 0; f < 120; f++)
        {
            if (!hit && d.P2.SeedBarActive)
            {
                d.P2.Combat.DamageTaken += 0.1f;   // the controller aborts on a damage rise
                hit = true; hitFrame = f;
            }
            d.Step();
        }
        output.WriteLine($"hit @ {hitFrame}; planted {d.Planter.Planted}");
        Assert.True(hit, "the planter never started");
        Assert.False(d.P2.SeedBarActive, "the bot must end on the block bar");
        Assert.Empty(Spitters(d.Sim, d.P2));
    }

    // The opponent appears close mid-script (no reaction lag here): the planter bails to Back.
    [Fact]
    public void Plant_OpponentClosesMidScript_EndsOnTheBlockBar_WithoutPlanting()
    {
        var d = Make(tweak: b => b.ReactionFrames = 0);
        bool moved = false;
        for (int f = 0; f < 60; f++)
        {
            if (!moved && d.P2.SeedBarActive)
            {
                d.P1.Body.Position = d.P2.Body.Position + new Vector2(-4 * Ts, 0f);
                moved = true;
            }
            d.Step();
        }
        Assert.True(moved, "the planter never started");
        Assert.False(d.P2.SeedBarActive, "the bot must end on the block bar");
        Assert.Empty(Spitters(d.Sim, d.P2));
    }

    // Even if something leaves the bot on the seed bar with no planter running, the
    // constraint swaps back and masks RMB so nothing gets planted by accident.
    [Fact]
    public void Plant_StrandedOnTheSeedBar_ConstraintSwapsBack()
    {
        var d = Make(p1Col: BotCol - 6);   // close: the planter never applies
        d.P2.SeedBarActive = true;
        for (int f = 0; f < 30; f++) d.Step(i => { i.RightClick = true; return i; });
        Assert.False(d.P2.SeedBarActive);
        Assert.Empty(Spitters(d.Sim, d.P2));
    }

    [Fact]
    public void Plant_Gates_OpponentClose_SpitterAlive_Unaffordable()
    {
        var sim = new Simulation(Terrain(), Spawn(4));
        var (p2, _) = sim.AddSecondaryPlayer(Spawn(BotCol));
        for (int f = 0; f < 30; f++) sim.Step(default, default);
        var m = new PlantSpitterManeuver();
        int frame = 100;

        Assert.True(m.Score(View(sim, p2, sim.Player, frame++)) > 0f, "control: far, grounded, affordable");

        var close = View(sim, p2, sim.Player, frame++);
        close.TargetPos = p2.Body.Position - new Vector2(6 * Ts, 0f);
        Assert.Equal(0f, m.Score(close));

        var meters = p2.Abilities.Meters;
        float bm = meters.BuildMove, em = meters.EruptMove;
        meters.BuildMove = 0f; meters.EruptMove = 0f;
        Assert.Equal(0f, m.Score(View(sim, p2, sim.Player, frame++)));
        meters.BuildMove = bm; meters.EruptMove = em;

        sim.SpawnEntity(Spitter(sim, BotCol + 10, Row, p2));   // out of reach, but alive
        sim.Step(default, default);
        Assert.Equal(0f, m.Score(View(sim, p2, sim.Player, frame++)));
    }

    [Fact]
    public void Plant_NoPlantingWhileOpponentClose_InPlay()
    {
        var d = Make(p1Col: BotCol - 8);
        for (int f = 0; f < 180; f++) d.Step();
        Assert.Empty(Spitters(d.Sim, d.P2));
        Assert.False(d.P2.SeedBarActive);
    }

    // ── Striking plants ─────────────────────────────────────────────────────

    [Fact]
    public void Strike_DamagesAnOpposingPlantInReach()
    {
        SpitterPlant theirs = null;
        var d = Make(setup: (sim, p2) =>
        {
            theirs = Spitter(sim, BotCol - 2, Row, sim.Player);   // P1's plant, two cells toward P1
            sim.SpawnEntity(theirs);
        });
        float hp0 = theirs.Health;
        for (int f = 0; f < 150; f++) d.Step();
        output.WriteLine($"swings {d.Striker.SwingsThrown}; plant hp {hp0} -> {theirs.Health} dead {theirs.IsDead}");
        Assert.True(d.Striker.SwingsThrown > 0);
        Assert.True(theirs.IsDead || theirs.Health < hp0, "the opposing plant should take damage");
    }

    [Fact]
    public void Strike_IgnoresItsOwnPlant()
    {
        SpitterPlant mine = null;
        var d = Make(setup: (sim, p2) =>
        {
            mine = Spitter(sim, BotCol - 2, Row, p2);
            sim.SpawnEntity(mine);
        });
        float hp0 = mine.Health;
        for (int f = 0; f < 150; f++) d.Step();
        Assert.Equal(0, d.Striker.SwingsThrown);
        Assert.False(mine.IsDead);
        Assert.Equal(hp0, mine.Health);
    }

    // ── Siege pod dodge ─────────────────────────────────────────────────────

    // P1's pod at `podCol`, aimed straight up (default aim), aged `age` seconds with both
    // players standing still before the bot is switched on.
    private static (Duel d, SiegePodPlant pod) WithPod(int podCol, float age)
    {
        SiegePodPlant pod = null;
        var d = Make(setup: (sim, p2) =>
        {
            pod = new SiegePodPlant(new PlantSite(podCol, Row, PlantRoot.Below), sim.Player.Id,
                                    sim.Player.Faction, sim.Player.Team, Vector2.Zero);
            sim.SpawnEntity(pod);
        });
        Run2(d.Sim, age);
        return (d, pod);
    }

    private static SiegeDodgeManeuver Dodger(Duel d) => d.Bot.Maneuvers.OfType<SiegeDodgeManeuver>().Single();

    [Fact]
    public void Siege_OnTheLine_StepsOffBeforeTheShot_AndTakesNoDamage()
    {
        var (d, pod) = WithPod(BotCol - 2, PlantTuning.SiegePodGrowSeconds - 1.8f);   // bot inside the disc
        Assert.False(pod.HasFired);
        float hp0 = d.P2.Combat.DamageTaken;
        for (int f = 0; f < 180 && !pod.HasFired; f++) d.Step();
        for (int f = 0; f < 10; f++) d.Step();
        output.WriteLine($"fired {pod.HasFired}; dodges {Dodger(d).Dodges}; P2 at {d.P2.Body.Position} muzzle {pod.Muzzle}; dmg {d.P2.Combat.DamageTaken - hp0}");
        Assert.True(pod.HasFired);
        Assert.True(Dodger(d).Dodges > 0, "the dodge reflex should have run");
        Assert.Equal(hp0, d.P2.Combat.DamageTaken);
    }

    [Fact]
    public void Siege_OffTheLine_DoesNotReact()
    {
        var (d, pod) = WithPod(BotCol + 15, PlantTuning.SiegePodGrowSeconds - 1.8f);   // behind the bot, which chases left
        for (int f = 0; f < 180 && !pod.HasFired; f++) d.Step();
        Assert.True(pod.HasFired);
        Assert.Equal(0, Dodger(d).Dodges);
    }

    [Fact]
    public void Siege_EarlyInGrowth_DoesNothing()
    {
        var (d, pod) = WithPod(BotCol - 2, PlantTuning.SiegePodGrowSeconds * 0.3f);
        var v = View(d.Sim, d.P2, d.P1, 999);
        Assert.False(new SiegeDodgeManeuver().Bet(v).Applicable);
        for (int f = 0; f < 90; f++) d.Step();
        Assert.Equal(0, Dodger(d).Dodges);
        Assert.False(pod.HasFired);
    }
}
