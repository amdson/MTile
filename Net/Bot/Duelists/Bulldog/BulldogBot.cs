using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Bulldog — a rushdown duelist (see README.md in this folder).
//
// Same contract as BrawlerBotInputSource: a function from "what a player can see" to a raw
// PlayerInput record. Poll is perception (BulldogSense: a few extra facts about the
// opponent, all ReactionFrames stale like BotView's) plus one BotController step
// (Plans/BOT_BEHAVIOR_ARCHITECTURE.md): the maneuvers run through the ManeuverBehaviour
// adapter, positioning is ChaseStance / StepOutStance (BulldogStances.cs) driven by the
// controller's MoveExecutor (walk right into the opponent's face instead of stopping at
// slash range, hop walls), and SpikeGuard masks the finished input as a constraint.
//
// Fairness: every opponent fact comes from a ring of past frames at least ReactionFrames
// old (position, velocity, action, "is flinching", "is on the ground") or from the HUD
// (HP). Our own state is read freely. Randomness only through the seeded Random.
public sealed class BulldogBot : IRemoteInputSource
{
    public static readonly DuelBot Create = (selfIndex, seed) => new BulldogBot(seed, selfIndex);

    public int   ReactionFrames = 6;
    // Horizontal gap at which ChaseStance stops walking in: inside slash reach, so the
    // opponent is always within one swing.
    public float EngageRange    = 22f;
    // StepOutStance: in our own end-lag with more than RetreatUntil frames left and the
    // opponent within RetreatDist, back off.
    public bool  RetreatInLag   = true;
    public int   RetreatUntil   = 8;
    public float RetreatDist    = 60f;

    // Reflex / action acquisitions by maneuver name (fed from the controller's trace).
    public readonly Dictionary<string, int> Starts = new();

    public readonly SpikeGuard Spikes = new();
    // Opt-in ablation by maneuver name (Ranger drops WallClimbManeuver). Like Maneuvers,
    // edits must precede the first Poll: the controller is built from both then.
    public readonly HashSet<string> Disabled = new();
    // Opt-in online learning: the controller's bet ledger. Null = no correction and
    // argmax selection (Bulldog's plain highest-score choice). Set before the first Poll.
    public BetLedger Ledger;
    // Diagnostics: force softmax (true) or argmax (false) selection regardless of Ledger.
    public bool? ExploreOverride;
    // With a ledger: let the learner, not the mix-up die, pick among the close-range arms.
    public bool  LearnerPicksMix = true;
    // With a ledger: optimism bonus (HP) for little-tried arms, see BotController.ExploreBonus.
    public float ExploreBonus = 0f;
    // Softmax inverse temperature for selection (BotController.Sharpness).
    public float Sharpness = 3f;
    // Stance switch gap (BotController.StanceGap); negative = the controller default.
    public float StanceGap = -1f;
    // Minimum frames a stance keeps Move once it has it (BotController.StanceMinHold).
    public int   StanceMinHold = 30;
    // Situation bucket function for the ledger (BotController.Bucketer); null = Situation.Bucket.
    public Func<BotView, int> Bucketer;
    // Diagnostics: ledger records but does not steer (BotController.Shadow).
    public bool  Shadow = false;
    // Data-calibrated bet priors (BotController.Calibration); null = the authored bets.
    public IReadOnlyDictionary<string, (float Dealt, float Taken)> Calibration;
    // The controller's value = dealt - Lambda * taken (selection-time weight on the taken
    // channel; Ranger's learner runs dealt-only, 0). Set before the first Poll.
    public float Lambda = 1f;
    // Opt-in route planner for the MoveExecutor (Ranger's RoutePlanner). Set before the
    // first Poll.
    public IMovePlanner Planner;
    // Opt-in per-frame hook, run with the fresh view before the controller steps.
    public Action<BotView> PerFrame;
    // Positioning stances a derived brain adds after Chase and StepOut (Ranger's
    // HoldRangeStance). Like Maneuvers and Disabled, edit before the first Poll.
    public readonly List<Stance> ExtraStances = new();

    // The maneuver holding a resource: the committed action, else the committed reflex.
    public BotManeuver Active
        => ((_controller?.CommittedAction ?? _controller?.CommittedReflex) as ManeuverBehaviour)?.Maneuver;
    public List<BotManeuver> Maneuvers => _maneuvers;
    // Null until the first Poll builds it.
    public BotController Controller => _controller;

    private readonly Random            _rng;
    private readonly int               _selfIndex;
    private readonly List<BotManeuver> _maneuvers;
    private readonly BotView           _view = new();
    private readonly BulldogSense      _sense = new();
    private BotController _controller;
    private readonly HashSet<string> _stanceNames = new();

    private const int Ring = 32;
    private readonly Vector2[] _seen         = new Vector2[Ring];
    private readonly string[]  _seenAction   = new string[Ring];
    private readonly bool[]    _seenHelpless = new bool[Ring];
    private readonly bool[]    _seenGrounded = new bool[Ring];
    private readonly bool[]    _seenGrabbed  = new bool[Ring];
    private readonly int[]     _seenFacing   = new int[Ring];
    private int _seenCount;

    private float _lastDamage;
    private PlayerInput _prev;

    // `extra`: opt-in maneuvers appended to the list (HarrierBot is Bulldog plus a
    // whiff punish). Default null = Bulldog exactly as authored.
    public BulldogBot(int seed = 1, int selfIndex = 0, IEnumerable<BotManeuver> extra = null)
    {
        _rng       = new Random(seed);
        _selfIndex = selfIndex;
        _view.Rng  = _rng;
        _sense.Mix = _rng.NextDouble();
        var s = _sense;
        _maneuvers = new List<BotManeuver>
        {
            new EscapeGrabManeuver(),
            new GuardManeuver(),
            new BulldogGuard(),
            new BulldogBreaker(),
            new BulldogGrab(s),
            new BulldogString(s),
            new BulldogStab(s),
            new BulldogCrossUp(s),
            new BulldogLowPoke(s),
            new DownAirManeuver(),
            new BulldogPunish(s),
            new DashManeuver(),
            new BreachManeuver(),
            new WallClimbManeuver(),
            // Added by the arbiter after the re-run (not the author's): wall off a crystal
            // wave, nothing else.
            new WallUpManeuver { CrystalOnly = true },
        };
        if (extra != null) _maneuvers.AddRange(extra);
    }

    private BotController Build()
    {
        var c = new BotController
        {
            Ledger  = Ledger,
            Lambda  = Lambda,
            // Without a learner, argmax: the old "highest score wins" brain.
            Explore = ExploreOverride ?? (Ledger != null),
            ExploreBonus = ExploreBonus,
            Sharpness = Sharpness,
            StanceGap = StanceGap,
            StanceMinHold = StanceMinHold,
            Bucketer = Bucketer ?? Situation.Bucket,
            Shadow = Shadow,
            Calibration = Calibration,
        };
        _sense.AllMixArms = LearnerPicksMix && Ledger != null && !Shadow;
        if (Planner != null) c.Executor.Planner = Planner;
        var stances = new List<Stance> { new ChaseStance(this), new StepOutStance(this) };
        stances.AddRange(ExtraStances);
        foreach (var st in stances)
        {
            c.Add(st);
            _stanceNames.Add(st.Name);
        }
        foreach (var m in _maneuvers)
            if (!Disabled.Contains(m.Name)) c.Add(new ManeuverBehaviour(m));
        c.Add(new SpikeGuardConstraint(Spikes));   // shared spike sense, last pass
        return c;
    }

    private BotClickRetap _retap = new();
    public  int ClickRetaps => _retap.Retaps;

    public PlayerInput Poll(Simulation sim, int frame)
    {
        if (sim.Player == null || sim.SecondaryPlayers.Count <= Math.Max(_selfIndex, 0)) return default;
        _controller ??= Build();
        var v = Perceive(sim, frame);
        var input = new PlayerInput { MouseWorldPosition = v.TargetPos };

        // The mix-up die rerolls whenever a maneuver ends. Getting hit aborts inside the
        // controller before it selects, so reroll first, as the old brain did.
        var prevAction = _controller.CommittedAction;
        var prevReflex = _controller.CommittedReflex;
        float damage = v.Self.Combat.DamageTaken;
        bool rerolled = false;
        if (damage > _lastDamage && (prevAction != null || prevReflex != null))
        {
            _sense.Mix = _rng.NextDouble();
            rerolled = true;
        }
        _lastDamage = damage;

        PerFrame?.Invoke(v);
        _controller.Step(v, ref input);

        int acquired = 0;
        foreach (var kv in _controller.Trace.Acquisitions)
        {
            if (_stanceNames.Contains(kv.Key)) continue;
            acquired += kv.Value - Starts.GetValueOrDefault(kv.Key);
            Starts[kv.Key] = kv.Value;
        }
        bool ended = (prevAction != null && _controller.CommittedAction != prevAction)
                     || (prevReflex != null && _controller.CommittedReflex != prevReflex)
                     || (acquired > 0 && _controller.CommittedAction == null && _controller.CommittedReflex == null);
        if (ended && !rerolled) _sense.Mix = _rng.NextDouble();
        _retap.Apply(ref input, in _prev, v.Self, frame);   // 1-frame taps that aged out of the 12f opener window

        bool attackEdge = (input.LeftClick && !_prev.LeftClick) || (input.RightClick && !_prev.RightClick);
        v.FramesSinceAttack = attackEdge ? 0 : v.FramesSinceAttack + 1;
        _prev = input;
        return input;
    }

    private BotView Perceive(Simulation sim, int frame)
    {
        var v = _view;
        v.Sim      = sim;
        v.Self     = BotDuel.Self(sim, _selfIndex);
        v.Target   = BotDuel.Opponent(sim, _selfIndex);
        v.Frame    = frame;
        v.Me       = v.Self.Body.Position;
        v.MyVel    = v.Self.Body.Velocity;
        v.Previous = _prev;

        var t = v.Target;
        int w = _seenCount % Ring;
        _seen[w]         = t.Body.Position;
        _seenAction[w]   = t.CurrentActionName;
        _seenHelpless[w] = t.Combat.HitstunActive || t.Combat.StunActive || t.Combat.GrabbedActive;
        _seenGrounded[w] = t.IsGrounded;
        _seenGrabbed[w]  = t.Combat.GrabbedActive;
        _seenFacing[w]   = t.Facing;
        _seenCount++;

        int i = SeenIndex(ReactionFrames);
        v.TargetPos    = _seen[i];
        v.TargetAction = _seenAction[i];
        v.TargetVel    = _seenCount > ReactionFrames + 1
            ? (v.TargetPos - _seen[SeenIndex(ReactionFrames + 1)]) / Simulation.FixedDt
            : Vector2.Zero;

        _sense.TargetHelpless = _seenHelpless[i];
        v.TargetHelpless      = _seenHelpless[i];
        v.TargetGrounded      = _seenGrounded[i];
        v.TargetGrabbed       = _seenGrabbed[i];
        _sense.TargetGrounded = _seenGrounded[i];
        _sense.TargetFacing   = _seenFacing[i];
        _sense.TargetHp       = t.Health;     // HUD
        return v;
    }

    private int SeenIndex(int lag)
    {
        lag = Math.Min(Math.Min(lag, Ring - 1), _seenCount - 1);
        return (_seenCount - 1 - lag) % Ring;
    }
}

// Extra perception shared by Bulldog's maneuvers. Filled by the brain each Poll; every
// opponent fact here is ReactionFrames stale (or HUD-visible, like HP).
public sealed class BulldogSense
{
    public bool   TargetHelpless;   // flinching / stunned / held, as seen ReactionFrames ago
    public bool   TargetGrounded;
    public int    TargetFacing;
    public float  TargetHp;         // the HUD bar
    public double Mix;              // rerolled whenever a maneuver ends: the mix-up die
    // When true the die gates nothing: every mix-up arm (stab, cross-up, low poke, grab) is
    // applicable in its geometric window and the controller's selection, corrected by the
    // bet ledger, decides. Set by the brain when a ledger is present.
    public bool   AllMixArms;
}
