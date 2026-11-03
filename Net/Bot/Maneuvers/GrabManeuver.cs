using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Entity grab and throw: Shift+LMB with the cursor ON the target's body, hold while the
// victim is carried, then swipe the cursor and release so the swipe velocity becomes the
// throw (GrabAction.Exit hands the victim to a GrabThrowEntity at the cursor's EMA
// velocity). Phases:
//
//   Face   — the grab latches in a box in FRONT of the grabber, so turn to the target first.
//   Press  — Shift+LMB edge, cursor on the target. 12 frames of windup hold nobody; if the
//            windup passes with nobody caught it was a whiff — release and end.
//   Carry  — park the cursor on the throw side so the victim is dragged there.
//   Swipe  — cursor races outward along the throw direction, then LMB comes up mid-swipe.
//
// Throw direction: the nearest wall within WallSearch px of the target (slam into it),
// else straight down if there is floor under the target (crush into it), else away from us.
public sealed class GrabManeuver : BotManeuver
{
    // Centre-to-centre reach. The latch box is ~19 px ahead of the grabber, +-29 px wide,
    // and the victim's hurtbox adds a few more; stay a little inside that.
    public float Reach          = 36f;
    public float VerticalReach  = 24f;
    public float WallSearch     = 140f;
    public int   CooldownFrames = 90;

    private const int   FaceFrames   = 4;     // give up turning after this many frames
    private const int   WindupFrames = 22;    // GrabAction startup is 20; slack to latch
    private const int   CarryFrames  = 8;
    private const int   SwipeFrames  = 5;
    private const float SwipeStep    = 14f;   // px per frame ~ 840 px/s, over the 800 px/s throw cap

    private enum Phase { Face, Press, Carry, Swipe }

    private Phase   _phase;
    private int     _t;
    private Vector2 _dir;        // throw direction (unit)
    private Vector2 _swipeFrom;

    public override int Cooldown => CooldownFrames;

    public override float Score(BotView v)
    {
        if (v.Self.Combat.GrabbedActive || v.Self.Combat.BlocksAttack) return 0f;
        if (v.Target.Combat.GrabbedActive) return 0f;
        if (MathF.Abs(v.Dx) > Reach || MathF.Abs(v.Dy) > VerticalReach) return 0f;
        // Slightly above a slash: a grab beats a guard, and a thrown body takes crush damage.
        return 1.1f;
    }

    public override void Begin(BotView v) { _phase = Phase.Face; _t = 0; }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        _t++;
        if (_phase == Phase.Face)
        {
            if (v.Facing != v.Toward && _t <= FaceFrames)
            {
                BotInput.Move(ref input, v.Toward);
                return true;
            }
            _phase = Phase.Press; _t = 1;
        }

        switch (_phase)
        {
            case Phase.Press:
                // Cursor is already on the target (brain default). Hold Shift+LMB; the
                // first frame of this is the press edge.
                input.Shift = true; input.LeftClick = true;
                if (v.Target.Combat.GrabbedActive)
                {
                    _dir = ChooseThrow(v);
                    _phase = Phase.Carry; _t = 0;
                    return true;
                }
                return _t <= WindupFrames + 4;   // whiff: buttons up on the frame we give up

            case Phase.Carry:
                input.Shift = true; input.LeftClick = true;
                // Parked on the throw side; the carry tracks the cursor out to hold reach.
                input.MouseWorldPosition = v.Me + _dir * 30f;
                if (_t >= CarryFrames) { _phase = Phase.Swipe; _t = 0; _swipeFrom = input.MouseWorldPosition; }
                // The victim broke free mid-carry: nothing left to throw.
                return v.Target.Combat.GrabbedActive;

            default: // Swipe
                input.MouseWorldPosition = _swipeFrom + _dir * (SwipeStep * _t);
                if (_t >= SwipeFrames) return false;       // release on this frame (buttons up)
                input.Shift = true; input.LeftClick = true;
                return true;
        }
    }

    // Wall slam if one is close, else floor slam, else just shove it away from us.
    private Vector2 ChooseThrow(BotView v)
    {
        var p = v.Target.Body.Position;
        float best = WallSearch + 1f; int side = 0;
        for (int s = -1; s <= 1; s += 2)
        {
            var hit = v.FirstSolidAlong(p, p + new Vector2(s * WallSearch, 0f));
            if (hit.HasValue)
            {
                float d = MathF.Abs(hit.Value.X - p.X);
                if (d < best) { best = d; side = s; }
            }
        }
        if (side != 0) return Vector2.Normalize(new Vector2(side, -0.15f));
        if (v.FirstSolidAlong(p, p + new Vector2(0f, 3 * Chunk.TileSize)).HasValue) return new Vector2(0f, 1f);
        return Vector2.Normalize(new Vector2(v.Toward, -0.15f));
    }
}
