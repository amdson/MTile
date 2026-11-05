using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Corner escape: our back is to a wall, the opponent is between us and open ground —
// dash past them to the open side instead of trading in the corner.
//
// Trigger (grounded, can act, opponent not helpless):
//   * a solid cell within WallProbe (2 tiles = 22 px) of our centre on the side AWAY from
//     the opponent, at body height or one tile above;
//   * the opponent MinDx..MaxDx (16..60 px) away horizontally, within 24 px vertically;
//   * no solid at body height HeadroomTiles (3) beyond them, i.e. room to land.
// Score 1.3. Cooldown 90 so it can't loop.
//
// Why a dash and not a hop: a standing hop from the wall carries only ~30 px horizontally
// (measured: 24 px gap -> lands 6 px past), so it barely clears an adjacent opponent. The
// dash chord (Space+Shift, see DashManeuver) throws ~86 px (360 px/s x 0.24 s) along
// body -> cursor with a half-speed carry; the cursor is aimed 80 px beyond the opponent
// and 14 px up, and measured it ends ~48 px past a target 24 px away. Done when DashState
// ends (or if the chord is declined within 12 frames).
public sealed class CornerEscapeManeuver : BotManeuver
{
    public float Utility       = 1.3f;
    public float WallProbe     = 2f * Chunk.TileSize;
    public float MinDx         = 16f, MaxDx = 60f, MaxDy = 24f;
    public int   HeadroomTiles = 3;
    public float AimBeyond     = 80f, AimUp = 14f;
    private const int EnterTimeout = 12;

    public override int Cooldown => 90;
    public override bool OwnsLocomotion => true;
    public override Category  Category => Category.Reflex;   // a mover whose job is to not get hit: writes Space+Shift dash and Left/Right out of the corner
    public override MoveClaim Move     => MoveClaim.Exclusive;

    private int  _t, _away;
    private bool _dashed;

    private bool WallBehind(BotView v, int dirToWall)
    {
        for (float d = 8f; d <= WallProbe; d += Chunk.TileSize * 0.5f)
            for (int dy = -1; dy <= 0; dy++)
                if (v.Solid(new Vector2(v.Me.X + dirToWall * d, v.Me.Y + dy * Chunk.TileSize))) return true;
        return false;
    }

    public override float Score(BotView v)
    {
        if (!v.Grounded || v.TargetHelpless) return 0f;
        var c = v.Self.Combat;
        if (c.HitstunActive || c.StunActive || c.GrabbedActive) return 0f;
        float ax = MathF.Abs(v.Dx);
        if (ax < MinDx || ax > MaxDx || MathF.Abs(v.Dy) > MaxDy) return 0f;
        int open = v.Toward;
        if (!WallBehind(v, -open)) return 0f;
        if (v.Solid(new Vector2(v.TargetPos.X + open * HeadroomTiles * Chunk.TileSize, v.Me.Y))) return 0f;
        return Utility;
    }

    // Bet (M4): reflex, deals nothing; trading with our back to a wall costs ~0.6 (avoided taken);
    // the 86 px dash + carry ~ 30 f. Value 0.60 clears Chase (-0.15) and StepOut (0.25) by > 0.25.
    public override Bet Bet(BotView v)
    {
        float s = Score(v);
        if (s <= 0f) return Net.Bet.Pass;
        return new Bet(0f, -0.6f, 30);
    }

    public override void Begin(BotView v) { _t = 0; _dashed = false; _away = v.Toward; }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        input.MouseWorldPosition = new Vector2(v.TargetPos.X + _away * AimBeyond, v.Me.Y - AimUp);
        BotInput.Move(ref input, _away);
        if (_t == 0) { input.Space = true; input.Shift = true; }
        else if (_t == 1) input.Shift = true;
        _t++;
        bool dashing = v.Self.CurrentStateName == "DashState";
        if (dashing) _dashed = true;
        if (_dashed && !dashing) return false;
        return _dashed || _t <= EnterTimeout;
    }
}
