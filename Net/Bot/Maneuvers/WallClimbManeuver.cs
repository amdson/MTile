using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Get up a wall/pillar the default jump can't clear: jump at it, wall-slide, kick off
// and re-attach higher, and when the lip is within reach hold Up for LedgeGrab → LedgePull.
//
// OwnsLocomotion: the default chase would just keep pushing into the face and jump every
// 10 stuck-frames (which does wall-slide but never kicks off or presses Up), so we write
// every key. The controller is REACTIVE (keyed off the movement state each frame), not a
// timed script, so it tolerates the physics being a little different from my estimates:
//
//   * always press toward the wall (sticky side chosen at Begin);
//   * grounded / WallSlidingState: Space on a fresh edge (alternate frames). On the
//     ground that is the jump; on the wall it is a WallJump (any horizontal key + Jump
//     intent + wall contact), which kicks away at (200, -150) — holding toward the wall
//     steers the body back onto it, now higher;
//   * rising in a jump state: keep Space held (variable-height jump / wall-jump hold);
//   * body within LipReach of the top of the wall: hold Up. LedgeGrab path A needs a
//     buffered UpPress with wall + ledge corner above head and the body FACING the lip
//     (we press into it, so we do); LedgePull then needs Up held. Hold Up throughout
//     LedgeGrabState / LedgePullState too.
//
// Score > 0 only when the default locomotion fails: target >= 3.2 tiles above (the plain
// jump clears ~2.5), within 12 tiles sideways, with solid wall straight ahead of us.
public sealed class WallClimbManeuver : BotManeuver
{
    public float MinRise  = 3.2f * Chunk.TileSize;
    public float MaxSide  = 12f * Chunk.TileSize;
    // Start pressing Up when the wall's top is this close above the body centre.
    public float LipReach = 3.0f * Chunk.TileSize;
    public int   TimeoutFrames = 420;

    public override int  Cooldown => 30;
    public override bool OwnsLocomotion => true;

    private int _t, _side;

    public override float Score(BotView v)
    {
        if (!v.Grounded) return 0f;
        if (v.Dy > -MinRise || MathF.Abs(v.Dx) > MaxSide) return 0f;
        int s = v.Toward;
        // A wall within 4 tiles ahead at body height (otherwise there's nothing to climb:
        // the target is on a floating ledge and this isn't the right tool).
        for (int k = 1; k <= 4; k++)
            if (v.Solid(v.Me + new Vector2(s * k * Chunk.TileSize, 0f))) return 1.5f;
        return 0f;
    }

    public override void Begin(BotView v) { _t = 0; _side = v.Toward; }

    private bool WallAhead(BotView v, float tiles)
    {
        for (float d = 0.5f; d <= tiles; d += 0.5f)
            if (v.Solid(v.Me + new Vector2(_side * (PlayerCharacter.Radius + d * Chunk.TileSize), 0f))) return true;
        return false;
    }

    // Y of the top face of the wall column beside the body (cell scan upward from the
    // body's row). Returns float.NaN if there is no wall beside us.
    private float WallTop(BotView v)
    {
        var c = BotView.Cell(v.Me + new Vector2(_side * Chunk.TileSize, 0f));
        if (!v.Solid(c)) return float.NaN;
        int y = c.Y;
        for (int i = 0; i < 40 && v.Solid(c.X, y - 1); i++) y--;
        return y * Chunk.TileSize;
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        _t++;
        if (_t > TimeoutFrames) return false;
        string st = v.Self.CurrentStateName;

        // Done: stood up near the target's level (on top of the pillar).
        if (v.Grounded && v.Dy > -Chunk.TileSize && _t > 5) return false;

        // Re-aim at the target if a wall-kick sent us over the top and past it; the dead
        // zone stops chatter when we are right below it.
        if (MathF.Abs(v.Dx) > 8f) _side = v.Toward;
        BotInput.Move(ref input, _side);

        bool ledge = st is "LedgeGrabState" or "LedgePullState";
        float top  = WallTop(v);
        bool nearLip = !float.IsNaN(top) && v.Me.Y - top < LipReach;
        if (ledge || nearLip) input.Up = true;

        bool rising = st is "JumpingState" or "RunningJumpState" or "WallJumpingState"
                      or "DoubleJumpingState" or "LedgeJumpState";
        if (ledge)                       { /* Up only; Space would bail out of the hang */ }
        else if (rising && v.MyVel.Y < 0f) input.Space = true;
        else if (!v.Previous.Space && st == "WallSlidingState") input.Space = true;
        // Take off only once the face is within ~2 tiles: jumping from far out wastes the
        // run-up and lands short of the wall.
        else if (!v.Previous.Space && v.Grounded && WallAhead(v, 2.0f)) input.Space = true;
        return true;
    }
}
