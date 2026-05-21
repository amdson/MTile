using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// A very basic opponent: seeded-random movement + attacks, loosely aimed at the
// primary player. The local stand-in for a network peer (the reference's evil_AI),
// used to bring up + test a two-player Simulation before any transport exists.
//
// Determinism note: the bot is deterministic given its seed and the sequence of
// frames it's polled on, but it lives OUTSIDE the sim — exactly like a keyboard. Its
// output is buffered as that frame's P2 input and replayed from the buffer during a
// rollback; the loop never re-invokes Poll to reconstruct a past frame. So the bot's
// RNG and hold-timers are NOT sim state and are never snapshotted.
public sealed class BotInputSource : IRemoteInputSource
{
    private readonly Random _rng;

    // Movement intent is held for a random run of frames so the bot reads as
    // behavior (walk a while, jump, turn) rather than per-frame noise.
    private bool _left, _right, _up, _space;
    private int  _moveHold;

    // Attack state machine. While _attackFrames > 0 the LMB is held; when it counts
    // down to 0 the button releases, and the release edge is what InputParser turns
    // into a Click (short hold) or Stab (long hold + cursor swipe).
    private int     _attackFrames;
    private int     _attackCooldown;
    private bool    _attackIsStab;
    private Vector2 _aimJitter;

    public BotInputSource(int seed = 1234) => _rng = new Random(seed);

    public PlayerInput Poll(Simulation sim, int frame)
    {
        // ── Movement: re-roll a held direction/jump every ~8-24 frames ──
        if (_moveHold <= 0)
        {
            _moveHold = _rng.Next(8, 24);
            int dir = _rng.Next(3);        // 0 idle, 1 left, 2 right
            _left   = dir == 1;
            _right  = dir == 2;
            _up     = _rng.Next(4) == 0;   // occasional up (climb/ledge intent)
            _space  = _rng.Next(3) == 0;   // jump on ~1/3 of re-rolls
        }
        _moveHold--;

        // ── Aim at the primary player, with a little wander ──
        Vector2 aim = sim.Player != null ? sim.Player.Body.Position : Vector2.Zero;
        if (_attackFrames <= 0)
            _aimJitter = new Vector2(_rng.Next(-20, 21), _rng.Next(-20, 21));
        // A stab sweeps the cursor over its hold so InputParser sees swipe distance;
        // a plain click keeps the cursor roughly put.
        if (_attackIsStab && _attackFrames > 0)
            _aimJitter += new Vector2(_rng.Next(-6, 7), _rng.Next(-6, 7));
        aim += _aimJitter;

        // ── Attacks: start one occasionally when off cooldown ──
        if (_attackFrames <= 0 && _attackCooldown <= 0 && _rng.Next(18) == 0)
        {
            _attackIsStab = _rng.Next(2) == 0;
            // Click = short hold (≤ ClickMaxHoldFrames); Stab = long hold + swipe.
            _attackFrames   = _attackIsStab ? _rng.Next(8, 14) : _rng.Next(1, 4);
            _attackCooldown = _rng.Next(12, 30);
        }

        bool leftClick = _attackFrames > 0;
        if (_attackFrames > 0) _attackFrames--;
        else if (_attackCooldown > 0) _attackCooldown--;

        return new PlayerInput
        {
            Left               = _left,
            Right              = _right,
            Up                 = _up,
            Space              = _space,
            LeftClick          = leftClick,
            MouseWorldPosition = aim,
        };
    }
}
