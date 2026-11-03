using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Riposte — a counter-fighter. See README.md beside this file.
//
// Same contract as BrawlerBotInputSource: a function from "what I can see" to a raw
// PlayerInput. Everything about the opponent comes through RiposteSense, which keeps its
// own ring of DELAYED visible samples (position, visible action/pose, flinch, grounded),
// ReactionFrames behind the truth. The opponent's HP (HUD) and our own state are read
// live. Randomness only through the seeded stream; no statics with state.
public sealed class RiposteBot : IRemoteInputSource
{
    public static readonly DuelBot Create = (self, seed) => new RiposteBot(seed, self);

    public const int ReactionFrames = 7;

    public readonly Dictionary<string, int> Starts = new();
    public BotManeuver Active => _active;
    public RiposteModel Model => _sense.Model;
    public RiposteParry Parry => (RiposteParry)_maneuvers[1];

    private readonly Random _rng;
    private readonly int _selfIndex;
    private readonly BotView _view = new();
    private readonly RiposteSense _sense = new();
    private readonly List<BotManeuver> _maneuvers;
    private BotManeuver _active;
    private float _lastDamage;
    private PlayerInput _prev;
    private int _lastLmbPress = -100, _lastSpacePress = -100;

    // Locomotion state.
    private int _jumpFrames, _jumpRelease, _stuck;
    private int _danceT, _danceLen = 20, _danceDir;   // bait rhythm: lean in / drift out

    public RiposteBot(int seed = 1, int selfIndex = 0)
    {
        _rng = new Random(seed);
        _selfIndex = selfIndex;
        _view.Rng = _rng;
        _maneuvers = new List<BotManeuver>
        {
            new EscapeGrabManeuver(),
            new RiposteParry(_sense),
            new RipostePunish(_sense),
            new RiposteDashIn(_sense),
            new RiposteStab(_sense),
            new RiposteSlash(_sense),
            new RiposteRetreat(_sense),
            new RiposteGated(new WallClimbManeuver(), v => MathF.Abs(v.Dx) > 50f),
            new BreachManeuver(),
        };
    }

    public PlayerInput Poll(Simulation sim, int frame)
    {
        if (sim.Player == null || sim.SecondaryPlayers.Count <= Math.Max(_selfIndex, 0)) return default;
        var v = Perceive(sim, frame);
        var input = new PlayerInput { MouseWorldPosition = v.TargetPos };

        float damage = v.Self.Combat.DamageTaken;
        bool hurt = damage > _lastDamage;
        if (hurt && _active != null && _active is not RiposteParry) { _active.Abort(v, ref input); Finish(); }
        _lastDamage = damage;
        _sense.AfterPerceive(v, hurt);

        foreach (var m in _maneuvers) if (m.CooldownLeft > 0) m.CooldownLeft--;

        // Reactive maneuvers may interrupt a running non-reactive one (a parry beats
        // finishing a walk-back).
        if (_active != null && _active is not RiposteParry && _active is not EscapeGrabManeuver
            && _active.OwnsLocomotion && _active is RiposteRetreat)
        {
            var parry = _maneuvers[1];
            if (parry.CooldownLeft <= 0 && parry.Score(v) > 0f) { Finish(); Start(parry, v); }
        }

        if (_active == null)
        {
            BotManeuver best = null;
            float bestScore = 0f;
            foreach (var m in _maneuvers)
            {
                if (m.CooldownLeft > 0) continue;
                float s = m.Score(v);
                if (s > bestScore) { best = m; bestScore = s; }
            }
            if (best != null) Start(best, v);
        }

        if (_active == null || !_active.OwnsLocomotion) Locomote(v, ref input);
        else { _jumpFrames = 0; _stuck = 0; }

        if (_active != null && !_active.Step(v, ref input)) Finish();

        // Human-rate cap: no button is pressed again within 5 frames of its last press
        // (≤ 12 presses/s), whatever the maneuvers asked for.
        if (input.LeftClick && !_prev.LeftClick)
        {
            if (frame - _lastLmbPress < 5) input.LeftClick = false; else _lastLmbPress = frame;
        }
        if (input.Space && !_prev.Space)
        {
            if (frame - _lastSpacePress < 5) input.Space = false; else _lastSpacePress = frame;
        }
        bool attackEdge = (input.LeftClick && !_prev.LeftClick) || (input.RightClick && !_prev.RightClick);
        v.FramesSinceAttack = attackEdge ? 0 : v.FramesSinceAttack + 1;
        _prev = input;
        v.FramesSinceManeuver++;
        return input;
    }

    private void Start(BotManeuver m, BotView v)
    {
        _active = m;
        v.FramesSinceManeuver = 0;
        Starts[m.Name] = Starts.GetValueOrDefault(m.Name) + 1;
        m.Begin(v);
    }

    private void Finish()
    {
        _active.CooldownLeft = _active.Cooldown;
        _active = null;
    }

    private BotView Perceive(Simulation sim, int frame)
    {
        var v = _view;
        v.Sim = sim;
        v.Self = BotDuel.Self(sim, _selfIndex);
        v.Target = BotDuel.Opponent(sim, _selfIndex);
        v.Frame = frame;
        v.Me = v.Self.Body.Position;
        v.MyVel = v.Self.Body.Velocity;
        v.Previous = _prev;
        _sense.Record(v, ReactionFrames);
        return v;
    }

    // Spacing locomotion: hold the gap at the edge of the opponent's reach, with a slow
    // in/out "dance" so the edge keeps getting tested (bait), jump steps/walls.
    private void Locomote(BotView v, ref PlayerInput input)
    {
        var s = _sense;
        float adx = MathF.Abs(v.Dx);
        int toward = v.Toward;
        float desired = s.DesiredGap(v);

        // Bait rhythm: every so often lean a step into their reach, then drift back out.
        if (++_danceT >= _danceLen)
        {
            _danceT = 0;
            _danceLen = v.Rng.Next(14, 30);
            int r = v.Rng.Next(100);
            _danceDir = r < s.BaitPercent(v) ? -1 : (r < 75 ? 0 : 1);
        }
        float target = desired + _danceDir * 10f;

        int dir = 0;
        if (MathF.Abs(v.Dy) > 3f * Chunk.TileSize && adx < 6f * Chunk.TileSize) dir = toward;  // different level: go there
        else if (adx > target + 4f) dir = toward;
        else if (adx < target - 4f) dir = s.WallBehind(v) ? 0 : -toward;

        // Never walk into a live swing.
        if (s.TargetAttacking && adx < s.ThreatReach(v) + 14f) dir = s.WallBehind(v) ? 0 : -toward;

        BotInput.Move(ref input, dir);

        bool pushingToward = dir == toward;
        if (pushingToward && MathF.Abs(v.MyVel.X) < 8f) _stuck++; else _stuck = 0;
        if (_jumpFrames > 0) { input.Space = true; _jumpFrames--; if (_jumpFrames == 0) _jumpRelease = 4; }
        else if (_jumpRelease > 0) _jumpRelease--;
        else if (v.Grounded && (_stuck >= 10 || (v.Dy < -1.5f * Chunk.TileSize && adx < 4f * Chunk.TileSize)))
        {
            input.Space = true;
            _jumpFrames = 13;
            _stuck = 0;
        }
        if (!v.Grounded && dir == 0 && adx > 6f && MathF.Abs(v.Dy) > 20f) BotInput.Move(ref input, toward);
    }
}

// Delayed perception + derived facts shared by Riposte's maneuvers.
public sealed class RiposteSense
{
    public readonly RiposteModel Model = new();

    private struct Sample
    {
        public Vector2 Pos, MyPos;
        public string Action, Move;
        public bool Helpless, Grounded;
        public int Facing;
    }

    private readonly Sample[] _ring = new Sample[64];
    private int _count;

    // Perceived (delayed) facts, valid after Record.
    public string Action = "NullAction";
    public int    ActionAge;            // perceived frames the current visible action has run
    public string LastMelee = "";       // the last visible attack they started
    public int    SinceMelee = 1000;    // perceived frames since that attack started
    public bool   Helpless;
    public bool   TargetGrounded;
    public bool   TargetDashing;        // visible dash pose
    public int    TargetFacing;
    public bool   TargetAttacking => RiposteModel.IsMelee(Action);
    public bool   TargetRanged    => RiposteModel.IsRanged(Action);
    // They're in an attack's tail (visible recovery pose right after a visible attack):
    // the punish window.
    // A slash that is still inside its chain window may be followed by the next step, so
    // it only counts once the chain has visibly been dropped (or after the ender / a
    // heavy move, whose tail is long anyway).
    public bool   TargetRecovering => Action == "RecoveryAction" && SinceMelee < 40
        && (ActionAge >= 6 || LastMelee == "GroundSlash3" || LastMelee == "StabAction"
            || LastMelee == "AirHammerAction" || LastMelee == "PulseAction" || LastMelee == "DashSlash"
            || LastMelee == "AirWhipKick" || LastMelee == "GrabAction");
    public float  PerceivedGap;         // |dx| between the delayed target and me at that moment

    // My own attack bookkeeping (for the model).
    public int   MyAttackFrame = -1000;
    public string MyAttackKind;
    private float _targetHpAtAttack;
    public int   Frame;

    public void Record(BotView v, int delay)
    {
        var t = v.Target;
        _ring[_count % _ring.Length] = new Sample
        {
            Pos = t.Body.Position, MyPos = v.Me, Action = t.CurrentActionName, Move = t.CurrentStateName,
            Helpless = t.Combat.HitstunActive || t.Combat.StunActive || t.Combat.GrabbedActive,
            Grounded = t.IsGrounded, Facing = t.Facing,
        };
        _count++;
        Frame = v.Frame;
        var cur  = At(delay);
        var prev = At(delay + 1);
        v.TargetPos = cur.Pos;
        v.TargetVel = _count > delay + 2 ? (cur.Pos - prev.Pos) / Simulation.FixedDt : Vector2.Zero;
        v.TargetAction = cur.Action;
        if (cur.Action != Action) { ActionAge = 0; } else ActionAge++;
        if (RiposteModel.IsMelee(cur.Action) && (cur.Action != Action)) { LastMelee = cur.Action; SinceMelee = 0; }
        else SinceMelee++;
        Action = cur.Action;
        Helpless = cur.Helpless;
        TargetGrounded = cur.Grounded;
        TargetDashing = cur.Move == "DashState";
        TargetFacing = cur.Facing;
        PerceivedGap = MathF.Abs(cur.Pos.X - cur.MyPos.X);
    }

    public void AfterPerceive(BotView v, bool hurt)
    {
        Model.Observe(v.Frame, Action, PerceivedGap, hurt, MathF.Abs(v.Dx));
        TrackScore(v, hurt);
        // Resolve my last attack: did their HP (HUD) drop within the window?
        if (MyAttackKind != null && v.Frame - MyAttackFrame > 4)
        {
            bool hit = v.TargetHealth < _targetHpAtAttack - 0.01f;
            if (hit || v.Frame - MyAttackFrame > 30)
            {
                if (MyAttackKind == "stab") { Model.MyStabs++; if (hit) Model.MyStabsHit++; }
                else { Model.MySlashes++; if (hit) Model.MySlashesHit++; }
                MyAttackKind = null;
            }
        }
    }

    // Bout bookkeeping from what the HUD shows (both HP bars) — drives initiative.
    public float Dealt, Taken;
    public int   Kills, Deaths;
    public int   QuietFrames;
    private float _prevTargetHp = -1f, _prevMyHp = -1f;

    // On the scoreboard: ahead on kills, or level on kills with a real damage lead.
    public bool Winning => Kills != Deaths ? Kills > Deaths : Dealt - Taken >= 0.75f;

    // Take the initiative: nothing has happened for a while and we aren't winning, or
    // the clock is running down on a level/losing scoreboard. A counter-fighter that
    // never presses a patient opponent just draws.
    public bool Pressing(BotView v)
        => !Winning && (QuietFrames > 300 || v.Frame > 2700);

    private void TrackScore(BotView v, bool hurt)
    {
        float th = v.TargetHealth, mh = v.MyHealth;
        if (_prevTargetHp >= 0f)
        {
            if (th < _prevTargetHp - 1e-4f) { Dealt += _prevTargetHp - th; QuietFrames = 0; }
            else if (th > _prevTargetHp + 1f) { Kills++; QuietFrames = 0; }
            if (mh < _prevMyHp - 1e-4f) { Taken += _prevMyHp - mh; QuietFrames = 0; }
            else if (mh > _prevMyHp + 1f) { Deaths++; QuietFrames = 0; }
        }
        _prevTargetHp = th; _prevMyHp = mh;
        QuietFrames++;
    }

    public void NoteMyAttack(BotView v, string kind)
    {
        MyAttackFrame = v.Frame;
        MyAttackKind = kind;
        _targetHpAtAttack = v.TargetHealth;
    }

    private Sample At(int lag)
    {
        lag = Math.Min(Math.Min(lag, _ring.Length - 1), _count - 1);
        return _ring[(_count - 1 - lag) % _ring.Length];
    }

    // Predicted present position of the target: delayed sample + velocity × delay.
    public Vector2 Predicted(BotView v, int frames = RiposteBot.ReactionFrames)
        => v.TargetPos + v.TargetVel * (frames * Simulation.FixedDt);

    // Horizontal closing speed (px/s, + = coming at me).
    public float Closing(BotView v) => -v.TargetVel.X * v.Toward;

    // How far their attacks reach, as learned.
    public float ThreatReach(BotView v) => Math.Clamp(Model.ThreatGap + 4f, 46f, 70f);

    // Spacing target: just outside their reach, further when they're trigger-happy,
    // tighter when they zone (deny the space ranged moves need) or when they're low.
    public float DesiredGap(BotView v)
    {
        float gap = ThreatReach(v) + 10f;
        float aggr = Model.Aggression;
        if (aggr > 1.2f) gap += 6f;
        if (Model.RangedShare > 0.35f) gap -= 14f;
        if (v.TargetHealth <= 1.0f) gap -= 8f;
        if (v.MyHealth < v.TargetHealth - 1.5f) gap -= 4f;   // behind: take more risks
        if (Pressing(v)) gap -= 14f;                          // walk them down
        return Math.Clamp(gap, 40f, 82f);
    }

    // Percent of dance beats that lean in (bait). More against a patient opponent.
    public int BaitPercent(BotView v) => Pressing(v) ? 70 : Model.Aggression > 1.0f ? 40 : 55;

    public bool WallBehind(BotView v)
    {
        int away = -v.Toward;
        for (int k = 1; k <= 3; k++)
            if (v.Solid(v.Me + new Vector2(away * k * Chunk.TileSize, 0f))) return true;
        return false;
    }
}
