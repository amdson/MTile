using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Citadel combines Rampart terrain control with Bulldog hit-confirmed melee.
// Independent brain; existing agents and shared maneuvers are unchanged.
public sealed class CitadelBot : IRemoteInputSource
{
    public static readonly DuelBot Create = (self, seed) => new CitadelBot(seed, self);

    public int   ReactionFrames = 8;

    private const int   JumpHoldFrames = 14;
    private const int   StuckFrames    = 10;
    private const float StuckSpeed     = 8f;

    public IReadOnlyList<BotManeuver> Maneuvers => _maneuvers;
    public BotManeuver Active => _active;
    public readonly Dictionary<string, int> Starts = new();
    public readonly SpikeGuard Spikes = new();
    public readonly RampartSense Sense = new();
    public readonly HashSet<string> Disabled = new();   // ablation (tests)
    // Diagnostics (tests/tuning): opponent HP lost / own HP lost, credited to the
    // maneuver that started most recently (within CreditFrames), else "-".
    public readonly Dictionary<string, float> Dealt = new(), Taken = new(), DealtBySize = new();
    private const int CreditFrames = 90;
    private string _lastName = "-";
    private float _tHp = -1f, _myHp = -1f;

    private readonly Random _rng;
    private readonly int _selfIndex;
    private readonly List<BotManeuver> _maneuvers;
    private readonly BotView _view = new();
    private readonly BulldogSense _melee = new();
    private readonly int[] _facing = new int[Ring];

    private const int Ring = 32;
    private readonly Vector2[] _pos = new Vector2[Ring];
    private readonly string[]  _act = new string[Ring];
    private readonly bool[]    _gnd = new bool[Ring];
    private readonly bool[]    _hlp = new bool[Ring];
    private readonly bool[]    _grb = new bool[Ring];
    private int _seen;

    private BotManeuver _active;
    private int   _jumpFrames, _jumpRelease, _stuck;
    private float _lastDamage;
    private PlayerInput _prev;
    private int   _sinceAttack = int.MaxValue / 2, _sinceManeuver;

    public CitadelBot(int seed = 1, int selfIndex = 0)
    {
        _rng = new Random(seed);
        _selfIndex = selfIndex;
        _view.Rng = _rng;
        _melee.Mix = _rng.NextDouble();
        var s = Sense;
        _maneuvers = new List<BotManeuver>
        {
            new EscapeGrabManeuver(),
            new RampartGuard(),
            new RampartPulse(s),
            new BulldogBreaker(),
            new BulldogString(_melee),
            new BulldogStab(_melee),
            new BulldogGrab(_melee),
            new BulldogLowPoke(_melee),
            new BulldogPunish(_melee),
            new DownAirManeuver(),
            new WallClimbManeuver(),
            new RampartEnclose(s),
            new RampartCrystalWave(s),
            new RampartAvalancheCrush(s),
            new RampartWallUp(s),
            new RampartSpikeMoat(s),
            new RampartBackoff(s),
            new BreachManeuver(),
        };
    }

    public PlayerInput Poll(Simulation sim, int frame)
    {
        if (sim.Player == null || sim.SecondaryPlayers.Count < 1) return default;
        var v = Perceive(sim, frame);
        var input = new PlayerInput { MouseWorldPosition = v.TargetPos };

        float damage = v.Self.Combat.DamageTaken;
        if (damage < _lastDamage) { _lastDamage = 0f; Sense.HomeSet = false; }   // respawned: new fort at the spawn
        if (damage > _lastDamage && _active != null) { _active.Abort(v, ref input); Finish(); }
        _lastDamage = damage;

        foreach (var m in _maneuvers) if (m.CooldownLeft > 0) m.CooldownLeft--;
        Credit(v);

        if (_active == null)
        {
            BotManeuver best = null; float bestScore = 0f;
            foreach (var m in _maneuvers)
            {
                if (m.CooldownLeft > 0 || Disabled.Contains(m.Name)) continue;
                float sc = m.Score(v);
                if (sc > bestScore) { best = m; bestScore = sc; }
            }
            if (best != null)
            {
                _active = best;
                _sinceManeuver = 0; v.FramesSinceManeuver = 0;
                Starts[best.Name] = Starts.GetValueOrDefault(best.Name) + 1;
                _lastName = best.Name;
                best.Begin(v);
            }
        }

        if (_active == null || !_active.OwnsLocomotion) Locomote(v, ref input);
        else { _jumpFrames = 0; _stuck = 0; }

        if (_active != null && !_active.Step(v, ref input)) Finish();

        Spikes.Apply(v, ref input);   // shared spike sense, last pass (Net/Bot/SpikeGuard.cs)
        bool edge = (input.LeftClick && !_prev.LeftClick) || (input.RightClick && !_prev.RightClick);
        _sinceAttack = edge ? 0 : _sinceAttack + 1;
        _sinceManeuver++;
        _prev = input;
        return input;
    }

    private void Credit(BotView v)
    {
        float th = v.TargetHealth, mh = v.Self.Health;
        string who = _sinceManeuver <= CreditFrames || _active != null ? _lastName : "-";
        if (_tHp >= 0f && th < _tHp) { Dealt[who] = Dealt.GetValueOrDefault(who) + (_tHp - th); string k = $"{_tHp - th:F2}"; DealtBySize[k] = DealtBySize.GetValueOrDefault(k) + 1; }
        if (_myHp >= 0f && mh < _myHp) Taken[who] = Taken.GetValueOrDefault(who) + (_myHp - mh);
        _tHp = th; _myHp = mh;
    }

    private void Finish()
    {
        _active.CooldownLeft = _active.Cooldown;
        _active = null;
        _melee.Mix = _rng.NextDouble();
    }

    private BotView Perceive(Simulation sim, int frame)
    {
        var v = _view;
        v.Sim = sim;
        v.Self = BotDuel.Self(sim, _selfIndex);
        v.Target = BotDuel.Opponent(sim, _selfIndex);   // read only for Health (HUD)
        v.Frame = frame;
        v.Me = v.Self.Body.Position;
        v.MyVel = v.Self.Body.Velocity;
        v.Previous = _prev;
        v.FramesSinceAttack = _sinceAttack;
        v.FramesSinceManeuver = _sinceManeuver;

        // What is on screen this frame, pushed into the delay line.
        var t = v.Target;
        int i = _seen % Ring;
        _pos[i] = t.Body.Position;
        _act[i] = t.CurrentActionName;
        _gnd[i] = t.IsGrounded;
        _hlp[i] = t.Combat.HitstunActive || t.Combat.StunActive || t.Combat.GrabbedActive;
        _grb[i] = t.Combat.GrabbedActive;
        _facing[i] = t.Facing;
        _seen++;

        int now = Idx(ReactionFrames), before = Idx(ReactionFrames + 1);
        v.TargetPos    = _pos[now];
        v.TargetAction = _act[now];
        v.TargetVel    = _seen > ReactionFrames + 1 ? (_pos[now] - _pos[before]) / Simulation.FixedDt : Vector2.Zero;
        // Smoother velocity for planning: over 6 frames.
        int far = Idx(ReactionFrames + 6);
        Sense.TargetVelSmooth = _seen > ReactionFrames + 6 ? (_pos[now] - _pos[far]) / (6 * Simulation.FixedDt) : Vector2.Zero;
        Sense.TargetGrounded = _gnd[now];
        Sense.TargetHelpless = _hlp[now];
        v.TargetGrounded = _gnd[now];
        v.TargetHelpless = _hlp[now];
        v.TargetGrabbed  = _grb[now];
        _melee.TargetGrounded = v.TargetGrounded;
        _melee.TargetHelpless = v.TargetHelpless;
        _melee.TargetFacing = _facing[now];
        _melee.TargetHp = v.TargetHealth;
        UpdateHome(v);
        return v;
    }

    private int Idx(int lag)
    {
        lag = Math.Min(Math.Min(lag, Ring - 1), _seen - 1);
        return (_seen - 1 - lag) % Ring;
    }

    // Fort locomotion: hold the home column (where the spike carpet is), step back onto
    // it when knocked off. Home moves (a fortress that never engages is a stall, not a
    // strategy): after AdvanceFrames with the opponent beyond AdvanceGap and not coming,
    // it relocates toward them; and after QuietFrames with no HP lost on either side it
    // pushes into melee range.
    public float HomeSlack    = 0.6f * Chunk.TileSize;
    public float AdvanceGap   = 16f * Chunk.TileSize;
    public int   AdvanceFrames = 90;
    public float AdvanceTo    = 10f * Chunk.TileSize;
    private int  _farFrames, _quiet;
    private bool _pushing;
    private float _hpSum = 10f;
    public int   QuietFrames = 300;

    private void UpdateHome(BotView v)
    {
        var s = Sense;
        if (!s.HomeSet)
        { s.HomeX = v.Me.X; s.HomeSet = true; }
        float adx = MathF.Abs(v.Dx);
        float closing = -s.TargetVelSmooth.X * v.Toward;   // px/s, + = coming at us
        // Out of reach and NOT coming: go to them (in a step, re-planting each time).
        _farFrames = adx > AdvanceGap && closing < 15f ? _farFrames + 1 : 0;
        if (_farFrames > AdvanceFrames && v.Grounded)
        {
            s.HomeX = v.TargetPos.X - v.Toward * AdvanceTo;
            _farFrames = 0;
        }
        // Walking out to them and they started coming: plant the fort right here, so the
        // carpet has time to grow before they arrive.
        if (!_pushing && adx > 9f * Chunk.TileSize && closing > 30f && v.Grounded
            && MathF.Abs(s.HomeX - v.Me.X) > 2f * Chunk.TileSize)
            s.HomeX = v.Me.X;

        // Anti-stall: nobody has lost HP for QuietFrames (two forts staring, waves eaten by
        // kerbs) ⇒ push the fort forward into melee range. A fortress has to keep fighting.
        float hpSum = v.Self.Health + v.TargetHealth;
        if (MathF.Abs(hpSum - _hpSum) > 1e-4f && hpSum < _hpSum) { _quiet = 0; _pushing = false; }
        else _quiet++;
        _hpSum = hpSum;
        if (adx < 5f * Chunk.TileSize) _pushing = false;
        if (_quiet > QuietFrames && adx > 5f * Chunk.TileSize)
        {
            s.HomeX = v.Me.X + v.Toward * MathF.Min(adx - 3f * Chunk.TileSize, 10f * Chunk.TileSize);
            _pushing = true;
            _quiet = QuietFrames / 2;
        }
    }

    private void Locomote(BotView v, ref PlayerInput input)
    {
        float off = Sense.HomeX - v.Me.X;
        int dir = MathF.Abs(off) > HomeSlack ? (off > 0 ? 1 : -1) : 0;
        // Don't walk INTO a target that is standing between us and home: fight it there.
        if (dir == v.Toward && MathF.Abs(v.Dx) < 3f * Chunk.TileSize) dir = 0;
        if (dir != 0) BotInput.Move(ref input, dir);

        if (dir != 0 && MathF.Abs(v.MyVel.X) < StuckSpeed) _stuck++; else _stuck = 0;
        if (_jumpFrames > 0) { input.Space = true; _jumpFrames--; if (_jumpFrames == 0) _jumpRelease = 4; }
        else if (_jumpRelease > 0) _jumpRelease--;
        else if (v.Grounded && _stuck >= StuckFrames)
        {
            input.Space = true; _jumpFrames = JumpHoldFrames - 1; _stuck = 0;
        }
        if (!v.Grounded && dir != 0) BotInput.Move(ref input, dir);
    }
}

