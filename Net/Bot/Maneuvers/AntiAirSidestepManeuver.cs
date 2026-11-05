using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Anti-air answer #1: step out from under, then punish the landing.
//
// A player who jumps over the bot and pogoes on its head with DownAirSlash needs the bot
// to stay under the chop. So when the target is airborne, above us (Dy < -14, Y-down) and
// within ~1.5 body widths horizontally, we walk sideways out of the chop column and let
// it whiff. Once the target lands (or has dropped to our level) we turn and slash.
//
// Numbers relied on:
//   * Trigger: |Dx| <= 40 and Dy in [-130, -14], target not grounded. DownAirSlash's
//     wedge is +-30 degrees (see DownAirManeuver), so at Dy = -40 the column is ~+-23 px;
//     clearing it needs ~|Dx| > 0.58*|Dy| + body half-width, hence ClearDx = 40.
//   * Side: away from the target's horizontal velocity if it is moving (>25 px/s), else
//     away from its offset; flipped if a wall is within 1.5 tiles on that side.
//   * Phase 2 slash reach: 42 px horizontal / 30 px vertical like SlashManeuver; we walk
//     toward the target to get in range, up to Timeout frames total.
// Owns locomotion; Cooldown is short since the situation recurs per jump.
public sealed class AntiAirSidestepManeuver : BotManeuver
{
    public float TriggerDx = 40f, ClearDx = 40f;
    public float MinAbove = 14f, MaxAbove = 130f;
    public float Reach = 42f, VerticalReach = 30f;
    public int   Timeout = 90;

    private int  _t, _dir;
    private bool _clicked;

    public override int Cooldown => 20;
    public override bool OwnsLocomotion => true;
    public override Category  Category => Category.Reflex;   // a mover whose job is to not get hit: drives Left/Right itself
    public override MoveClaim Move     => MoveClaim.Exclusive;

    public override float Score(BotView v)
    {
        if (v.TargetGrounded || v.TargetHelpless) return 0f;
        if (v.Dy > -MinAbove || v.Dy < -MaxAbove) return 0f;
        if (MathF.Abs(v.Dx) > TriggerDx) return 0f;
        return 1.4f;
    }

    // Bet (M4): reflex; out of the chop column avoids a ~1 HP pogo ~35% (-0.34), then slash the
    // landing 0.5 ~60% (0.3); clear + wait for the landing ~45 f. Value 0.64 clears Chase / StepOut
    // by > 0.25 but not a WhiffPunish holding Move (0.40 + 0.25), as before.
    public override Bet Bet(BotView v)
    {
        float s = Score(v);
        if (s <= 0f) return Net.Bet.Pass;
        return new Bet(0.3f, -0.34f, 45);
    }

    public override void Begin(BotView v)
    {
        _t = 0; _clicked = false;
        int dir = MathF.Abs(v.TargetVel.X) > 25f ? (v.TargetVel.X > 0f ? -1 : 1)
                : (v.Dx > 0f ? -1 : 1);
        if (v.Solid(v.Me + new Vector2(dir * Chunk.TileSize * 1.5f, 0f))) dir = -dir;
        _dir = dir;
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        if (_clicked) return false;                       // release frame emitted
        if (++_t > Timeout) return false;

        bool landed = v.TargetGrounded || v.Dy > -MinAbove;
        if (!landed)
        {
            // Phase 1: keep clearing the column; stop once clear so we don't run off.
            bool clear = MathF.Abs(v.Dx) >= ClearDx && Math.Sign(v.Dx) == -_dir;
            BotInput.Move(ref input, clear ? 0 : _dir);
            return true;
        }

        // Phase 2: face and slash the landing.
        if (MathF.Abs(v.Dx) <= Reach && MathF.Abs(v.Dy) <= VerticalReach)
        {
            input.LeftClick = true;
            if (v.Facing != v.Toward) BotInput.Move(ref input, v.Toward);
            _clicked = true;
            return true;
        }
        BotInput.Move(ref input, v.Toward);
        return true;
    }
}
