using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Block throw (Plans/BLOCK_THROW_PLAN.md): peel a clod out of the ground and fling it.
//
// Gesture, exactly what a human does with Shift+LMB on terrain (BlockGrabAction):
//   Paint — press with the cursor ON a solid block within PullPointEntity.GrabReach; the
//           held button paints "tether" onto cells under the cursor (the peel group).
//   Pull  — sweep the cursor AWAY from the group; the spring ramps until the glue gives
//           and the group collapses into a ball tracking the cursor (point.HasBall).
//   Carry — ball in hand: park the cursor on the target's side so the ball is held there.
//   Swipe — race the cursor at the target and release mid-swipe; the cursor's EMA
//           velocity at release is the throw (the ball detaches at swipe speed).
// If no ball comes free within PullFrames (spring snapped, block too strong), the
// maneuver releases and ends.
public sealed class BlockThrowManeuver : BotManeuver
{
    public float MinRange = 44f;    // closer than this a slash/grab is the better tool
    public float MaxRange = 170f;   // the swipe speed (~840 px/s) still reaches this in a fraction of a second
    public float VerticalReach = 50f;
    public int   CooldownFrames = 150;

    private const int   PaintFrames = 14;
    private const int   PullFrames  = 50;
    private const int   CarryFrames = 4;
    private const int   SwipeFrames = 5;
    private const float SwipeStep   = 14f;
    private const float PullDist    = 48f;    // how far past the block the cursor sweeps

    private enum Phase { Paint, Pull, Carry, Swipe }

    private Phase   _phase;
    private int     _t;
    private Vector2 _block;      // world centre of the cell being peeled
    private Vector2 _dir;        // throw direction (unit)
    private Vector2 _swipeFrom;

    public override int Cooldown => CooldownFrames;

    public override float Score(BotView v)
    {
        if (v.Self.Combat.GrabbedActive || v.Self.Combat.BlocksAttack) return 0f;
        float ax = MathF.Abs(v.Dx);
        if (ax < MinRange || ax > MaxRange || MathF.Abs(v.Dy) > VerticalReach) return 0f;
        if (!v.Grounded || !v.LineOfSight()) return 0f;
        return FindBlock(v).HasValue ? 0.9f : 0f;
    }

    public override void Begin(BotView v)
    {
        _block = FindBlock(v) ?? v.Me;
        _phase = Phase.Paint; _t = 0;
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        _t++;
        // Paint/Pull/Carry all hold Shift+LMB; the Swipe's last frame lets go.
        switch (_phase)
        {
            case Phase.Paint:
                Hold(ref input);
                input.MouseWorldPosition = _block;
                if (_t >= PaintFrames) { _phase = Phase.Pull; _t = 0; }
                return true;

            case Phase.Pull:
                Hold(ref input);
                // Away from the ground the block sits in: up for a floor cell.
                input.MouseWorldPosition = _block + new Vector2(0f, -PullDist);
                if (HasBall(v))
                {
                    _dir = Vector2.Normalize(v.TargetPos - v.Me);
                    _phase = Phase.Carry; _t = 0;
                    return true;
                }
                return _t <= PullFrames;       // out of time: buttons up, give up

            case Phase.Carry:
                Hold(ref input);
                input.MouseWorldPosition = v.Me + _dir * 30f;
                if (_t >= CarryFrames) { _phase = Phase.Swipe; _t = 0; _swipeFrom = input.MouseWorldPosition; }
                return true;

            default: // Swipe
                input.MouseWorldPosition = _swipeFrom + _dir * (SwipeStep * _t);
                if (_t >= SwipeFrames) return false;   // release on this frame
                Hold(ref input);
                return true;
        }
    }

    private static void Hold(ref PlayerInput input) { input.Shift = true; input.LeftClick = true; }

    // Is there a ball in this bot's hand? Its pull point tracks our body (OwnerPos).
    private static bool HasBall(BotView v)
    {
        foreach (var e in v.Sim.Entities)
            if (e is PullPointEntity pp && pp.Driven && pp.HasBall
                && Vector2.DistanceSquared(pp.OwnerPos, v.Me) < 9f)
                return true;
        return false;
    }

    // A grabbable floor cell just ahead of our feet (toward the target), within reach.
    // Scans down from the body for the first solid cell, a tile or two toward the target
    // first and then straight below.
    private static Vector2? FindBlock(BotView v)
    {
        var me = BotView.Cell(v.Me);
        for (int off = 1; off >= 0; off--)
        {
            int gx = me.X + v.Toward * off;
            for (int gy = me.Y; gy <= me.Y + 4; gy++)
            {
                if (!v.Solid(gx, gy)) continue;
                if (!TileTypes.IsGrabbable(v.Sim.Chunks.GetCellType(gx, gy))) break;
                var c = BotView.CellCenter(new Point(gx, gy));
                // Inside the grab reach, with margin for the body moving.
                return Vector2.Distance(c, v.Me) <= PullPointEntity.GrabReach - 8f ? c : null;
            }
        }
        return null;
    }
}
