using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Execution layer for positioning (Plans/BOT_BEHAVIOR_ARCHITECTURE.md, "Outputs"): turns
// a stance's world-space target into Move inputs. Straight-line walk with the stuck hop
// and air steer lifted from BulldogBot.Locomote, aimed at stance.Target(v) instead of the
// opponent. When the straight line is not enough (a climb, a build, a detour) a Planner
// drives instead; M2 puts the route follower behind that slot.
public sealed class MoveExecutor
{
    public const int   JumpHoldFrames = 14;
    public const int   JumpReleaseFrames = 4;
    public const int   StuckFrames    = 8;
    public const float StuckSpeed     = 8f;     // px/s
    public const float AirSteerDx     = 6f;     // px
    public static float JumpIfAbove   => 1.5f * Chunk.TileSize;
    public static float JumpWithinX   => 4f * Chunk.TileSize;

    // Optional route planner. Null = straight line only.
    public IMovePlanner Planner;

    private int _jumpFrames, _jumpRelease, _stuck;

    public void Reset() { _jumpFrames = 0; _jumpRelease = 0; _stuck = 0; }

    public void Drive(BotView v, Stance stance, ref PlayerInput input)
    {
        var target = stance.Target(v);
        if (Planner != null && Planner.Wants(v, target))
        {
            if (Planner.Step(v, target, ref input)) { Reset(); return; }
            // Planner done or failed: fall through to the straight line this frame.
        }

        float dx = target.X - v.Me.X, dy = target.Y - v.Me.Y;
        int toward = dx >= 0f ? 1 : -1;

        bool walking = MathF.Abs(dx) > stance.Tolerance;
        if (walking) BotInput.Move(ref input, toward);

        if (walking && MathF.Abs(v.MyVel.X) < StuckSpeed) _stuck++;
        else                                               _stuck = 0;

        if (_jumpFrames > 0)       { input.Space = true; _jumpFrames--; if (_jumpFrames == 0) _jumpRelease = JumpReleaseFrames; }
        else if (_jumpRelease > 0) _jumpRelease--;
        else if (stance.MayJump && v.Grounded
                 && (_stuck >= StuckFrames || (dy < -JumpIfAbove && MathF.Abs(dx) < JumpWithinX)))
        {
            input.Space = true;
            _jumpFrames = JumpHoldFrames - 1;
            _stuck      = 0;
        }
        if (!v.Grounded && MathF.Abs(dx) > AirSteerDx) BotInput.Move(ref input, toward);
    }
}

// A route planner behind the executor: used when the straight line to the target needs
// a climb, a build or a detour.
public interface IMovePlanner
{
    // Whether the planner should drive toward `target` this frame instead of the line.
    bool Wants(BotView v, Vector2 target);
    // Write this frame's Move inputs. Return false when done or failed (the executor
    // then walks the straight line).
    bool Step(BotView v, Vector2 target, ref PlayerInput input);
}
