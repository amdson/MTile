using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// The P2 bot brain: chases the primary player and runs maneuvers at them.
//
// It plays through the SAME input surface a human does — held keys, LMB/RMB, Shift and
// the cursor — so InputParser decodes its gestures exactly like a mouse. No virtual
// buttons, no intent injection; the whole bot is a function from "what I can see of the
// sim" to a PlayerInput record (Plans/BOT_AI_PLAN.md).
//
// Structure: default locomotion (walk at the target, jump steps/walls/targets above)
// plus a utility selector over BotManeuvers (see BotManeuver.cs). New behaviors are new
// maneuvers in Net/Bot/Maneuvers/, registered in DefaultManeuvers.
//
// Determinism: same contract as BotInputSource. The bot lives outside the sim; its
// output is buffered as that frame's P2 input and replayed from the buffer on a
// rollback, so its RNG and timers are never snapshotted.
public sealed class BrawlerBotInputSource : IRemoteInputSource
{
    // Horizontal gap at which default locomotion stops walking in.
    public float EngageRange    = 34f;
    // Frames the bot is behind the truth: it chases and aims at where the target was
    // this long ago, so a late dodge makes it whiff. 0 = perfect tracking.
    public int   ReactionFrames = 6;

    private const int   JumpHoldFrames  = 14;   // full-ish jump: a 2-tile step needs most of it
    private const int   StuckFrames     = 10;   // pushing a direction with no progress for this long ⇒ jump
    private const float StuckSpeed      = 8f;   // px/s below which "pushing" counts as blocked
    private const float JumpIfAbove     = 1.5f * Chunk.TileSize;

    public IReadOnlyList<BotManeuver> Maneuvers => _maneuvers;

    private readonly Random            _rng;
    private readonly int               _selfIndex;
    private readonly List<BotManeuver> _maneuvers;
    private readonly BotView           _view = new();

    // Ring of past target positions for the reaction delay.
    private readonly Vector2[] _seen = new Vector2[32];
    private int _seenCount;

    private BotManeuver _active;
    private int   _jumpFrames;     // >0 while Space is held for a jump
    private int   _jumpRelease;    // frames Space must stay up before the next press edge
    private int   _stuck;
    private float _lastDamage;
    private PlayerInput _prev;

    // The full behavior set. Order breaks Score ties (earlier wins).
    public static List<BotManeuver> DefaultManeuvers() => new()
    {
        new SlashManeuver(),
    };

    // selfIndex: which SecondaryPlayers slot this bot drives (0 = P2).
    // maneuvers: the behavior set (null ⇒ DefaultManeuvers()). Tests pass one maneuver
    // to exercise it in isolation.
    public BrawlerBotInputSource(int seed = 1234, int selfIndex = 0, IEnumerable<BotManeuver> maneuvers = null)
    {
        _rng       = new Random(seed);
        _selfIndex = selfIndex;
        _maneuvers = maneuvers != null ? new List<BotManeuver>(maneuvers) : DefaultManeuvers();
        _view.Rng  = _rng;
    }

    public PlayerInput Poll(Simulation sim, int frame)
    {
        if (sim.Player == null || sim.SecondaryPlayers.Count <= _selfIndex) return default;
        var v = Perceive(sim, frame);

        var input = new PlayerInput { MouseWorldPosition = v.TargetPos };

        // Got hit mid-maneuver ⇒ drop it; the script's assumptions are gone.
        float damage = v.Self.Combat.DamageTaken;
        if (damage > _lastDamage && _active != null) { _active.Abort(v); Finish(); }
        _lastDamage = damage;

        foreach (var m in _maneuvers) if (m.CooldownLeft > 0) m.CooldownLeft--;

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
            if (best != null) { _active = best; v.FramesSinceManeuver = 0; best.Begin(v); }
        }

        if (_active == null || !_active.OwnsLocomotion) Locomote(v, ref input);
        else { _jumpFrames = 0; _stuck = 0; }

        if (_active != null && !_active.Step(v, ref input)) Finish();

        _prev = input;
        v.FramesSinceManeuver++;
        return input;
    }

    private void Finish()
    {
        _active.CooldownLeft = _active.Cooldown;
        _active = null;
    }

    private BotView Perceive(Simulation sim, int frame)
    {
        var v = _view;
        v.Sim      = sim;
        v.Self     = sim.SecondaryPlayers[_selfIndex].Player;
        v.Target   = sim.Player;
        v.Frame    = frame;
        v.Me       = v.Self.Body.Position;
        v.MyVel    = v.Self.Body.Velocity;
        v.Previous = _prev;

        _seen[_seenCount % _seen.Length] = sim.Player.Body.Position;
        _seenCount++;
        v.TargetPos = Seen(ReactionFrames);
        v.TargetVel = (_seenCount > ReactionFrames + 1)
            ? (v.TargetPos - Seen(ReactionFrames + 1)) / Simulation.FixedDt
            : Vector2.Zero;
        return v;
    }

    // The target position `lag` frames ago (clamped to what has been seen).
    private Vector2 Seen(int lag)
    {
        lag = Math.Min(Math.Min(lag, _seen.Length - 1), _seenCount - 1);
        return _seen[(_seenCount - 1 - lag) % _seen.Length];
    }

    // Default movement: close the horizontal gap, jump walls/steps and targets above,
    // steer in the air.
    private void Locomote(BotView v, ref PlayerInput input)
    {
        float dx = v.Dx, dy = v.Dy;
        int toward = v.Toward;

        bool walking = MathF.Abs(dx) > EngageRange;
        if (walking) BotInput.Move(ref input, toward);

        // Stuck: pushing toward the target but not moving ⇒ there's a wall/step.
        if (walking && MathF.Abs(v.MyVel.X) < StuckSpeed) _stuck++;
        else                                               _stuck = 0;

        // A held press, then a short release so the next one has an edge.
        if (_jumpFrames > 0)       { input.Space = true; _jumpFrames--; if (_jumpFrames == 0) _jumpRelease = 4; }
        else if (_jumpRelease > 0) _jumpRelease--;
        else if (v.Grounded && (_stuck >= StuckFrames
                                || (dy < -JumpIfAbove && MathF.Abs(dx) < 4f * Chunk.TileSize)))
        {
            input.Space = true;
            _jumpFrames = JumpHoldFrames - 1;
            _stuck      = 0;
        }
        // Keep steering in the air toward the target so a jump lands on it, not past it.
        if (!v.Grounded && MathF.Abs(dx) > 6f) BotInput.Move(ref input, toward);
    }
}
