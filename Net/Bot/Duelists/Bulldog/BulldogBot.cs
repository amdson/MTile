using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Bulldog — a rushdown duelist (see README.md in this folder).
//
// Same contract as BrawlerBotInputSource: a function from "what a player can see" to a raw
// PlayerInput record. The brain is a utility selector over its own maneuver set; what is
// different is the perception it hands them (BulldogSense: a few extra facts about the
// opponent, all ReactionFrames stale like BotView's) and the locomotion (walk right into
// the opponent's face instead of stopping at slash range, hop walls, dig through them).
//
// Fairness: every opponent fact comes from a ring of past frames at least ReactionFrames
// old (position, velocity, action, "is flinching", "is on the ground") or from the HUD
// (HP). Our own state is read freely. Randomness only through the seeded Random.
public sealed class BulldogBot : IRemoteInputSource
{
    public static readonly DuelBot Create = (selfIndex, seed) => new BulldogBot(seed, selfIndex);

    public int   ReactionFrames = 6;
    // Horizontal gap at which default locomotion stops walking in: inside slash reach,
    // so the opponent is always within one swing.
    public float EngageRange    = 22f;
    public bool  RetreatInLag   = true;
    public int   RetreatUntil   = 8;
    public float RetreatDist    = 60f;

    private const int   JumpHoldFrames  = 14;
    private const int   StuckFrames     = 8;
    private const float StuckSpeed      = 8f;
    private const float JumpIfAbove     = 1.5f * Chunk.TileSize;

    public readonly Dictionary<string, int> Starts = new();

    public readonly SpikeGuard Spikes = new();
    public BotManeuver Active => _active;
    public List<BotManeuver> Maneuvers => _maneuvers;

    private readonly Random            _rng;
    private readonly int               _selfIndex;
    private readonly List<BotManeuver> _maneuvers;
    private readonly BotView           _view = new();
    private readonly BulldogSense      _sense = new();

    private const int Ring = 32;
    private readonly Vector2[] _seen         = new Vector2[Ring];
    private readonly string[]  _seenAction   = new string[Ring];
    private readonly bool[]    _seenHelpless = new bool[Ring];
    private readonly bool[]    _seenGrounded = new bool[Ring];
    private readonly bool[]    _seenGrabbed  = new bool[Ring];
    private readonly int[]     _seenFacing   = new int[Ring];
    private int _seenCount;

    private BotManeuver _active;
    private int   _jumpFrames, _jumpRelease, _stuck;
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

    public PlayerInput Poll(Simulation sim, int frame)
    {
        if (sim.Player == null || sim.SecondaryPlayers.Count <= Math.Max(_selfIndex, 0)) return default;
        var v = Perceive(sim, frame);
        var input = new PlayerInput { MouseWorldPosition = v.TargetPos };

        float damage = v.Self.Combat.DamageTaken;
        if (damage > _lastDamage && _active != null) { _active.Abort(v, ref input); Finish(); }
        _lastDamage = damage;

        foreach (var m in _maneuvers) if (m.CooldownLeft > 0) m.CooldownLeft--;

        if (_active == null)
        {
            BotManeuver best = null;
            float bestScore = 0f;
            foreach (var m in _maneuvers)
            {
                if (m.CooldownLeft > 0) continue;
                float sc = m.Score(v);
                if (sc > bestScore) { best = m; bestScore = sc; }
            }
            if (best != null)
            {
                _active = best;
                v.FramesSinceManeuver = 0;
                Starts[best.Name] = Starts.GetValueOrDefault(best.Name) + 1;
                best.Begin(v);
            }
        }

        if (_active == null || !_active.OwnsLocomotion) Locomote(v, ref input);
        else { _jumpFrames = 0; _stuck = 0; }

        if (_active != null && !_active.Step(v, ref input)) Finish();

        Spikes.Apply(v, ref input);   // shared spike sense, last pass (Net/Bot/SpikeGuard.cs)
        bool attackEdge = (input.LeftClick && !_prev.LeftClick) || (input.RightClick && !_prev.RightClick);
        v.FramesSinceAttack = attackEdge ? 0 : v.FramesSinceAttack + 1;
        _prev = input;
        v.FramesSinceManeuver++;
        return input;
    }

    private void Finish()
    {
        _active.CooldownLeft = _active.Cooldown;
        _active = null;
        _sense.Mix = _rng.NextDouble();
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

    // Walk into the opponent; hop steps/walls and targets above; steer in the air.
    private void Locomote(BotView v, ref PlayerInput input)
    {
        float dx = v.Dx, dy = v.Dy;
        int toward = v.Toward;

        bool walking = MathF.Abs(dx) > EngageRange;
        // Our own end-lag is the one window we can't answer a swing in: step out of
        // their reach until it is nearly over, then walk straight back in.
        var cond = v.Self.Abilities.Condition;
        int lagLeft = cond.RecoveryActive ? cond.RecoveryExpireFrame - v.SelfFrame : 0;
        bool retreat = RetreatInLag && lagLeft > RetreatUntil && !_sense.TargetHelpless
                       && MathF.Abs(dx) < RetreatDist && MathF.Abs(dy) < 40f && v.Grounded;
        if (retreat) { BotInput.Move(ref input, -toward); walking = false; }
        else if (walking) BotInput.Move(ref input, toward);

        if (walking && MathF.Abs(v.MyVel.X) < StuckSpeed) _stuck++;
        else                                               _stuck = 0;

        if (_jumpFrames > 0)       { input.Space = true; _jumpFrames--; if (_jumpFrames == 0) _jumpRelease = 4; }
        else if (_jumpRelease > 0) _jumpRelease--;
        else if (v.Grounded && (_stuck >= StuckFrames
                                || (dy < -JumpIfAbove && MathF.Abs(dx) < 4f * Chunk.TileSize)))
        {
            input.Space = true;
            _jumpFrames = JumpHoldFrames - 1;
            _stuck      = 0;
        }
        if (!v.Grounded && MathF.Abs(dx) > 6f) BotInput.Move(ref input, toward);
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
}
