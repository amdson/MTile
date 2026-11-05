using System;
using System.Collections.Generic;

namespace MTile.Net;

// What the controller needs from the bet ledger (Net/Bot/Control/BetLedger.cs implements
// it). Null ledger on the controller = no learning; corrected bet = raw bet.
public interface IBetLedger
{
    // The raw bet plus the learned offset for (name, bucket).
    Bet Corrected(string name, int bucket, Bet bet);
    // A behaviour actually acquired a resource: start scoring `bet` over its horizon.
    int Open(string name, int bucket, Bet bet, int frame);
    // Once per frame: accumulate HP drops into open records, settle the elapsed ones.
    void Tick(int frame, float myHp, float theirHp);
}

// The only writer of a bot's PlayerInput (Plans/BOT_BEHAVIOR_ARCHITECTURE.md). Two
// parallel selectors over registered behaviours plus a constraint pass:
//
//   Move: committed reflex, else a committed action with a Move claim, else the stance.
//   Act:  at most one committed action, chosen against an implicit Idle arm.
//   Constraints mask the finished input, last.
//
// Selection is by corrected bet value (dealt - Lambda*taken), softmax with Sharpness
// (argmax when !Explore). Hysteresis: a holder is compared using the corrected bet it
// was started with, plus PreemptGap (and StayBonus for the current stance). No minimum
// hold timers. Bot-side only: no sim state is written, randomness only through v.Rng.
//
// The controller owns v.FramesSinceManeuver (0 on every reflex/action acquisition,
// +1 per Step). The cursor (input.MouseWorldPosition) is the caller's: it is never touched.
public sealed class BotController
{
    public const string IdleName = "Idle";

    public float Lambda     = 1f;     // value = dealtC - Lambda * takenC
    public float Sharpness  = 3f;     // softmax inverse temperature
    public float PreemptGap = 0.25f;  // HP a challenger must beat the holder by
    public float StayBonus  = 0.1f;   // extra HP the current stance is credited with
    public bool  Explore    = true;   // softmax sampling; false = argmax (deterministic)
    public Bet   IdleBet    = new(0f, 0f, 30);

    public IBetLedger   Ledger;
    public MoveExecutor Executor = new();
    public readonly ControllerTrace Trace = new();

    public readonly List<Stance>       Stances     = new();
    public readonly List<BotBehaviour> Reflexes    = new();
    public readonly List<BotBehaviour> Actions     = new();
    public readonly List<Constraint>   Constraints = new();

    public Stance       CurrentStance   => _stance;
    public bool         StanceActive    => _stance != null && _stanceActive;
    public BotBehaviour CommittedReflex => _reflex;
    public BotBehaviour CommittedAction => _action;

    private Stance       _stance;
    private bool         _stanceActive;
    private Bet          _stanceBet;
    private int          _stanceSettle;
    private BotBehaviour _reflex, _action;
    private Bet          _actionBet;
    private int          _idleSettle = int.MinValue;
    private float        _lastDamage;
    private readonly List<(BotBehaviour B, Bet Bet)> _cands = new();

    public void Add(BotBehaviour b)
    {
        switch (b.Category)
        {
            case Category.Positioning:
                Stances.Add(b as Stance ?? throw new ArgumentException($"{b.Name}: Positioning behaviours must derive from Stance"));
                break;
            case Category.Constraint:
                Constraints.Add(b as Constraint ?? throw new ArgumentException($"{b.Name}: Constraint behaviours must derive from Constraint"));
                break;
            case Category.Reflex: Reflexes.Add(b); break;
            default:              Actions.Add(b);  break;
        }
    }

    public float Value(Bet b) => b.Dealt - Lambda * b.Taken;

    public void Step(BotView v, ref PlayerInput input)
    {
        Trace.BeginFrame();
        int bucket = Situation.Bucket(v);

        // a. Abort on damage (stance untouched), cooldowns, ledger.
        float dmg = v.Self.Combat.DamageTaken;
        if (dmg > _lastDamage)
        {
            if (_reflex != null) { _reflex.Aborted(v, ref input); Release(ref _reflex); }
            if (_action != null) { _action.Aborted(v, ref input); Release(ref _action); }
        }
        _lastDamage = dmg;
        TickCooldowns(Stances); TickCooldowns(Reflexes); TickCooldowns(Actions);
        Ledger?.Tick(v.Frame, v.MyHealth, v.TargetHealth);
        SyncStance(v);

        // b. Act selector.
        if (_action == null) SelectAction(v, bucket);
        SyncStance(v);

        // c. Move selector: reflexes.
        if (_reflex == null) SelectReflex(v, bucket, ref input);
        SyncStance(v);

        // d. Stance selection.
        if (_reflex == null && !ActionSuspendsStance) SelectStance(v, bucket);
        SyncStance(v);

        // e. Write inputs.
        string moveHolder = _reflex?.Name
                            ?? (_action != null && _action.Move.Kind != MoveClaim.Mode.None ? _action.Name
                            : StanceActive ? _stance.Name : null);
        string actHolder = _action?.Name;

        if (_reflex != null)
        {
            if (!_reflex.Step(v, ref input)) Release(ref _reflex);
        }
        else if (StanceActive) Executor.Drive(v, _stance, ref input);

        if (_action != null)
        {
            bool more = _action.Step(v, ref input);
            _action.Move.Mask(ref input);
            if (!more) Release(ref _action);
        }
        SyncStance(v);

        // f. Constraints, last.
        foreach (var c in Constraints)
            if (c.Mask(v, ref input)) Trace.Masked(c.Name);

        // g. Trace.
        Trace.EndFrame(moveHolder, actHolder);
        v.FramesSinceManeuver++;
    }

    // ── selectors ────────────────────────────────────────────────────────────────

    private void SelectAction(BotView v, int bucket)
    {
        _cands.Clear();
        foreach (var a in Actions)
        {
            if (a.CooldownLeft > 0) continue;
            var raw = a.Bet(v);
            if (raw.Applicable) _cands.Add((a, Correct(a.Name, bucket, raw)));
        }
        if (_cands.Count == 0) return;
        var idle = Correct(IdleName, bucket, IdleBet);
        _cands.Insert(0, (null, idle));   // first, so Idle wins value ties

        var (b, bet) = _cands[Choose(v)];
        if (b == null)
        {
            // Idle won a contest: it is an executed arm too, one record per horizon.
            if (v.Frame >= _idleSettle) { Open(IdleName, bucket, idle, v.Frame); _idleSettle = v.Frame + Math.Max(1, idle.Horizon); }
            return;
        }
        // Atomic acquisition: a Move claim cannot be granted while a reflex holds Move.
        if (b.Move.Kind != MoveClaim.Mode.None && _reflex != null) return;

        b.Begin(v);
        _action = b; _actionBet = bet;
        Open(b.Name, bucket, bet, v.Frame);
        Trace.Acquired(b.Name);
        v.FramesSinceManeuver = 0;
    }

    private void SelectReflex(BotView v, int bucket, ref PlayerInput input)
    {
        _cands.Clear();
        foreach (var r in Reflexes)
        {
            if (r.CooldownLeft > 0) continue;
            var raw = r.Bet(v);
            if (raw.Applicable) _cands.Add((r, Correct(r.Name, bucket, raw)));
        }
        if (_cands.Count == 0) return;
        var (b, bet) = _cands[Choose(v)];

        bool actionHolds = _action != null && _action.Move.Kind != MoveClaim.Mode.None;
        float holder = actionHolds ? Value(_actionBet) : StanceActive ? Value(_stanceBet) : 0f;
        if (Value(bet) <= holder + PreemptGap) return;

        if (actionHolds)
        {
            // Claims are atomic: losing Move means losing Act too.
            _action.Aborted(v, ref input);
            Trace.Preempted(_action.Name);
            Release(ref _action);
        }
        else if (StanceActive) Trace.Preempted(_stance.Name);   // SyncStance suspends it

        b.Begin(v);
        _reflex = b;
        Open(b.Name, bucket, bet, v.Frame);
        Trace.Acquired(b.Name);
        v.FramesSinceManeuver = 0;
    }

    private void SelectStance(BotView v, int bucket)
    {
        if (_stance != null)
        {
            var live = _stance.Bet(v);
            if (!live.Applicable)
            {
                // The current stance no longer applies: it gives Move up without a contest.
                DropStance(v);
            }
            else if (v.Frame >= _stanceSettle)
            {
                // A stance that keeps running is a sequence of bets: re-open each horizon.
                _stanceBet = Correct(_stance.Name, bucket, live);
                Open(_stance.Name, bucket, _stanceBet, v.Frame);
                _stanceSettle = v.Frame + Math.Max(1, _stanceBet.Horizon);
            }
        }

        _cands.Clear();
        foreach (var s in Stances)
        {
            if (s == _stance || s.CooldownLeft > 0) continue;
            var raw = s.Bet(v);
            if (raw.Applicable) _cands.Add((s, Correct(s.Name, bucket, raw)));
        }
        if (_cands.Count == 0) return;
        var (b, bet) = _cands[Choose(v)];

        if (_stance != null)
        {
            if (Value(bet) <= Value(_stanceBet) + PreemptGap + StayBonus) return;
            Trace.Preempted(_stance.Name);
            DropStance(v);
        }

        _stance = (Stance)b; _stanceBet = bet; _stanceActive = true;
        Executor.Reset();
        _stance.Begin(v);
        _stance.Resumed(v);
        Open(b.Name, bucket, bet, v.Frame);
        _stanceSettle = v.Frame + Math.Max(1, bet.Horizon);
        Trace.Acquired(b.Name);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private bool ActionSuspendsStance => _action != null && _action.Move.SuspendsStance;

    // Fire Suspended / Resumed on the current stance when Move is taken from it or given back.
    private void SyncStance(BotView v)
    {
        if (_stance == null) { _stanceActive = false; return; }
        bool active = _reflex == null && !ActionSuspendsStance;
        if (active == _stanceActive) return;
        _stanceActive = active;
        Executor.Reset();
        if (active) _stance.Resumed(v);
        else        _stance.Suspended(v);
    }

    private void DropStance(BotView v)
    {
        if (_stanceActive) _stance.Suspended(v);
        _stance.CooldownLeft = _stance.Cooldown;
        _stance = null; _stanceActive = false;
        Executor.Reset();
    }

    private static void Release(ref BotBehaviour b)
    {
        b.CooldownLeft = b.Cooldown;
        b = null;
    }

    private static void TickCooldowns<T>(List<T> list) where T : BotBehaviour
    {
        foreach (var b in list) if (b.CooldownLeft > 0) b.CooldownLeft--;
    }

    private Bet Correct(string name, int bucket, Bet bet) => Ledger?.Corrected(name, bucket, bet) ?? bet;

    private void Open(string name, int bucket, Bet bet, int frame) => Ledger?.Open(name, bucket, bet, frame);

    // Index into _cands: argmax by value (first wins ties) or a softmax sample.
    private int Choose(BotView v)
    {
        int best = 0;
        float bestV = Value(_cands[0].Bet);
        for (int i = 1; i < _cands.Count; i++)
        {
            float val = Value(_cands[i].Bet);
            if (val > bestV) { best = i; bestV = val; }
        }
        if (!Explore || _cands.Count == 1) return best;

        double sum = 0;
        for (int i = 0; i < _cands.Count; i++) sum += Math.Exp(Sharpness * (Value(_cands[i].Bet) - bestV));
        double r = v.Rng.NextDouble() * sum;
        for (int i = 0; i < _cands.Count; i++)
        {
            r -= Math.Exp(Sharpness * (Value(_cands[i].Bet) - bestV));
            if (r <= 0) return i;
        }
        return best;
    }
}
