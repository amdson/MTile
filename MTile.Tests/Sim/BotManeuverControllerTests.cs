using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using MTile.Net;
using Xunit;

namespace MTile.Tests.Sim;

// BotController core (Plans/BOT_BEHAVIOR_ARCHITECTURE.md M1): the two selectors, Move
// claims, preemption + hysteresis, the Idle arm, ledger records only for executed arms,
// the constraint pass, and the ManeuverBehaviour adapter's registry. Scripted fake
// behaviours over a real (unstepped) two-player Simulation; Explore = false throughout.
public class BotManeuverControllerTests
{
    // ── fakes ────────────────────────────────────────────────────────────────────

    private sealed class Fake : BotBehaviour
    {
        private readonly string _name;
        private readonly Category _cat;
        private readonly MoveClaim _move;
        public Bet NextBet = Net.Bet.Pass;
        public Func<BotView, PlayerInput, (PlayerInput, bool)> OnStep = (_, i) => (i, true);
        public int Begun, Steps, AbortedCount, CooldownFrames = 30;

        public Fake(string name, Category cat, MoveClaim move) { _name = name; _cat = cat; _move = move; }
        public override string    Name     => _name;
        public override Category  Category => _cat;
        public override MoveClaim Move     => _move;
        public override int       Cooldown => CooldownFrames;
        public override Bet Bet(BotView v) => NextBet;
        public override void Begin(BotView v) => Begun++;
        public override bool Step(BotView v, ref PlayerInput input)
        {
            Steps++;
            var (o, more) = OnStep(v, input);
            input = o;
            return more;
        }
        public override void Aborted(BotView v, ref PlayerInput input) => AbortedCount++;
    }

    private sealed class FakeStance : Stance
    {
        private readonly string _name;
        public Bet NextBet;
        public float OffsetX = 100f;
        public int SuspendedCount, ResumedCount;
        public FakeStance(string name, Bet bet) { _name = name; NextBet = bet; }
        public override string Name => _name;
        public override Bet Bet(BotView v) => NextBet;
        public override Vector2 Target(BotView v) => v.Me + new Vector2(OffsetX, 0f);
        public override void Suspended(BotView v) => SuspendedCount++;
        public override void Resumed(BotView v) => ResumedCount++;
    }

    private sealed class ClearRight : Constraint
    {
        public override bool Mask(BotView v, ref PlayerInput input)
        {
            if (!input.Right) return false;
            input.Right = false;
            return true;
        }
    }

    private sealed class RecordingLedger : IBetLedger
    {
        public readonly List<(string Name, int Bucket, Bet Bet, int Frame)> Opens = new();
        public Bet Corrected(string name, int bucket, Bet bet) => bet;
        public int Open(string name, int bucket, Bet bet, int frame) { Opens.Add((name, bucket, bet, frame)); return Opens.Count; }
        public void Tick(int frame, float myHp, float theirHp) { }
    }

    private sealed class FakeManeuver(bool owns) : BotManeuver
    {
        public float S;
        public override bool OwnsLocomotion => owns;
        public override float Score(BotView v) => S;
        public override bool Step(BotView v, ref PlayerInput input) => false;
    }

    // ── harness ──────────────────────────────────────────────────────────────────

    private static BotView View()
    {
        var sim = new Simulation(BotArena.Flat(), new Vector2(60f, BotArena.FloorBodyY));
        var (p2, _) = sim.AddSecondaryPlayer(new Vector2(200f, BotArena.FloorBodyY));
        return new BotView
        {
            Sim = sim, Self = p2, Target = sim.Player, Rng = new Random(1),
            Me = p2.Body.Position, MyVel = Vector2.Zero, TargetPos = sim.Player.Body.Position,
        };
    }

    private static BotController Controller(IBetLedger ledger = null) => new() { Explore = false, Ledger = ledger };

    private static PlayerInput Tick(BotController c, BotView v)
    {
        var input = new PlayerInput { MouseWorldPosition = v.TargetPos };
        c.Step(v, ref input);
        v.Frame++;
        return input;
    }

    private static Func<BotView, PlayerInput, (PlayerInput, bool)> Writes(Action<PlayerInput[]> w)
        => (_, i) => { var a = new[] { i }; w(a); return (a[0], true); };

    // ── tests ────────────────────────────────────────────────────────────────────

    [Fact]
    public void ForbidAllAction_SuspendsStance_AndLeavesMoveEmpty()
    {
        var v = View();
        var c = Controller();
        var stance = new FakeStance("Chase", new Bet(0.1f, 0f, 60));
        var aim = new Fake("Aim", Category.Action, MoveClaim.Still)
        {
            OnStep = Writes(a => { a[0].Right = true; a[0].Space = true; a[0].LeftClick = true; }),
        };
        c.Add(stance); c.Add(aim);

        var f0 = Tick(c, v);
        Assert.True(f0.Right, "the stance walks toward its target first");

        aim.NextBet = new Bet(1f, 0f, 30);
        var f1 = Tick(c, v);
        Assert.Same(aim, c.CommittedAction);
        Assert.False(c.StanceActive);
        Assert.Equal(1, stance.SuspendedCount);
        Assert.False(f1.Left || f1.Right || f1.Space || f1.Down, "Forbid(All) must leave Move empty even if the action wrote Right");
        Assert.True(f1.LeftClick);
        Assert.Equal("Aim", c.Trace.MoveHolder);
        Assert.Equal("Aim", c.Trace.ActHolder);
    }

    [Fact]
    public void Reflex_PreemptsCommittedExclusiveAction_ByTheGap()
    {
        var v = View();
        var c = Controller();
        var jump = new Fake("JumpSlash", Category.Action, MoveClaim.Exclusive)
        {
            NextBet = new Bet(0.5f, 0f, 30),
            OnStep = Writes(a => a[0].Right = true),
        };
        var dodge = new Fake("Sidestep", Category.Reflex, MoveClaim.Exclusive)
        {
            OnStep = Writes(a => a[0].Left = true),
        };
        c.Add(jump); c.Add(dodge);

        Tick(c, v);
        Assert.Same(jump, c.CommittedAction);

        dodge.NextBet = new Bet(0.7f, 0f, 20);   // beats 0.5 by 0.2 < gap
        var f1 = Tick(c, v);
        Assert.Same(jump, c.CommittedAction);
        Assert.Null(c.CommittedReflex);
        Assert.True(f1.Right);

        dodge.NextBet = new Bet(1f, 0f, 20);     // beats by 0.5 > gap
        var f2 = Tick(c, v);
        Assert.Same(dodge, c.CommittedReflex);
        Assert.Null(c.CommittedAction);
        Assert.Equal(1, jump.AbortedCount);
        Assert.Equal(jump.Cooldown, jump.CooldownLeft);
        Assert.True(f2.Left && !f2.Right, "the reflex drives Move this frame");
        Assert.Equal(1, c.Trace.Preemptions["JumpSlash"]);
    }

    [Fact]
    public void MoveNoneAction_AndStance_HoldAtTheSameTime()
    {
        var v = View();
        var c = Controller();
        c.Add(new FakeStance("Chase", new Bet(0.1f, 0f, 60)));
        var slash = new Fake("Slash", Category.Action, MoveClaim.None)
        {
            NextBet = new Bet(0.5f, 0.1f, 30),
            OnStep = Writes(a => a[0].LeftClick = true),
        };
        c.Add(slash);

        var f = Tick(c, v);
        Assert.True(f.Right, "stance direction");
        Assert.True(f.LeftClick, "action's click");
        Assert.True(c.StanceActive);
        Assert.Equal("Chase", c.Trace.MoveHolder);
        Assert.Equal("Slash", c.Trace.ActHolder);
    }

    [Fact]
    public void IdleArm_BeatsANegativeValueAction()
    {
        var v = View();
        var c = Controller();
        var bad = new Fake("Bad", Category.Action, MoveClaim.None) { NextBet = new Bet(0.1f, 0.5f, 30) };
        c.Add(bad);
        for (int i = 0; i < 5; i++) Tick(c, v);
        Assert.Equal(0, bad.Begun);
        Assert.Null(c.CommittedAction);
    }

    [Fact]
    public void OnlyExecutedArms_OpenLedgerRecords()
    {
        var v = View();
        var ledger = new RecordingLedger();
        var c = Controller(ledger);
        var dodge = new Fake("Dodge", Category.Reflex, MoveClaim.Exclusive) { NextBet = new Bet(0f, -1f, 20) };
        var guard = new Fake("Guard", Category.Action, MoveClaim.Still);
        c.Add(dodge); c.Add(guard);

        Tick(c, v);
        Assert.Same(dodge, c.CommittedReflex);

        guard.NextBet = new Bet(2f, 0f, 30);     // best action, but a reflex holds Move
        for (int i = 0; i < 3; i++) Tick(c, v);
        Assert.Equal(0, guard.Begun);
        Assert.Null(c.CommittedAction);
        Assert.DoesNotContain(ledger.Opens, o => o.Name == "Guard");
        Assert.Single(ledger.Opens, o => o.Name == "Dodge");
    }

    [Fact]
    public void Stance_KeepsMove_UnlessBeatenByGapPlusStayBonus()
    {
        var v = View();
        var c = Controller();
        var a = new FakeStance("A", new Bet(0.1f, 0f, 600));
        var b = new FakeStance("B", Bet.Pass);
        c.Add(a); c.Add(b);

        Tick(c, v);
        Assert.Same(a, c.CurrentStance);

        b.NextBet = new Bet(0.4f, 0f, 600);      // +0.3 < 0.25 + 0.1
        for (int i = 0; i < 3; i++) Tick(c, v);
        Assert.Same(a, c.CurrentStance);

        b.NextBet = new Bet(0.5f, 0f, 600);      // +0.4 > 0.35
        Tick(c, v);
        Assert.Same(b, c.CurrentStance);
        Assert.Equal(1, a.SuspendedCount);
        Assert.Equal(1, c.Trace.Preemptions["A"]);
    }

    [Fact]
    public void StanceRecord_ReopensEachHorizon_WhileHeld()
    {
        var v = View();
        var ledger = new RecordingLedger();
        var c = Controller(ledger);
        c.Add(new FakeStance("Chase", new Bet(0.1f, 0f, 10)));
        for (int i = 0; i < 25; i++) Tick(c, v);
        Assert.Equal(new[] { 0, 10, 20 }, ledger.Opens.Where(o => o.Name == "Chase").Select(o => o.Frame).ToArray());
    }

    [Fact]
    public void Constraint_MasksLast()
    {
        var v = View();
        var c = Controller();
        c.Add(new FakeStance("Chase", new Bet(0.1f, 0f, 60)));
        c.Add(new ClearRight());
        var f = Tick(c, v);
        Assert.False(f.Right);
        Assert.Contains(nameof(ClearRight), c.Trace.MaskedThisFrame);
        Assert.Equal(1, c.Trace.Masks[nameof(ClearRight)]);
        Assert.Contains("Chase", c.Trace.Report());
    }

    [Fact]
    public void ManeuverBehaviour_DefaultsFromOwnsLocomotion_RegistryOverridesGuard()
    {
        var owns = new ManeuverBehaviour(new FakeManeuver(true));
        Assert.Equal(Category.Action, owns.Category);
        Assert.Equal(MoveClaim.Mode.Exclusive, owns.Move.Kind);
        var plain = new ManeuverBehaviour(new FakeManeuver(false));
        Assert.Equal(MoveClaim.Mode.None, plain.Move.Kind);

        var guardM = new GuardManeuver();
        Assert.True(guardM.OwnsLocomotion);
        var guard = new ManeuverBehaviour(guardM);
        Assert.Equal(Category.Action, guard.Category);
        Assert.Equal(MoveClaim.Mode.Forbid, guard.Move.Kind);
        Assert.Equal(MoveKeys.All, guard.Move.Forbidden);
        Assert.Equal("GuardManeuver", guard.Name);

        Assert.Equal(Category.Reflex, new ManeuverBehaviour(new DashManeuver()).Category);

        var v = View();
        var fm = new FakeManeuver(false) { S = 2f };
        var bet = new ManeuverBehaviour(fm).Bet(v);
        Assert.Equal(1f, bet.Dealt);
        Assert.Equal(90, bet.Horizon);
        fm.S = 0f;
        Assert.False(new ManeuverBehaviour(fm).Bet(v).Applicable);
    }
}
