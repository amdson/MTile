using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Dig through terrain between the bot and its target: aim slashes at the first solid
// tile along the line to the target, walk into the gap, repeat until there is a clear
// line. "The terrain IS the weapon": a slash's Hitbox.Damage carves tiles.
//
// Applicable when the straight line to the target is blocked AND the bot is not making
// progress (no horizontal speed for a while - the default locomotion's jump has
// already failed). The stuck counter lives in Score (private bookkeeping, reset when
// Score stops being called on consecutive frames or the bot moves).
//
// Input: LMB taps (press frame, release frame) with the cursor on the blocking tile
// whenever it is within slash reach; otherwise a held direction toward it. A tap every
// SwingGap frames lets the slash recover.
public sealed class BreachManeuver : BotManeuver
{
    public float Reach       = 36f;   // centre-to-tile-centre distance inside which a swing is aimed at the tile
    public int   StuckNeeded = 20;    // frames of no progress with a blocked line before digging
    public int   SwingGap    = 14;    // frames between taps
    public int   MaxFrames   = 300;   // give up (cooldown) after this long
    public float Utility     = 0.9f;

    private int _stuck, _lastScoreFrame = -10, _frames, _sinceTap;

    public override int Cooldown => 20;
    public override bool OwnsLocomotion => true;

    public override float Score(BotView v)
    {
        // Consecutive-frame bookkeeping only; harmless to Step.
        if (v.Frame != _lastScoreFrame + 1) _stuck = 0;
        _lastScoreFrame = v.Frame;

        bool blocked = v.FirstSolidAlong(v.Me, v.TargetPos) != null;
        // Not grounded-gated: the default locomotion's stuck-jump leaves the bot wall-sliding.
        bool still   = MathF.Abs(v.MyVel.X) < 8f;
        if (blocked && still) _stuck++; else _stuck = 0;
        return _stuck >= StuckNeeded ? Utility : 0f;
    }

    // Bet (M4): positional: digging through deals nothing itself; the fight it reopens is worth
    // ~0.45 over the dig (a few 14 f swings + walking in ~ 120 f); a barricade shields us (0.04).
    // 0.41: over WhiffPunish (also Score 0.9, later in the list).
    public override Bet Bet(BotView v)
    {
        float s = Score(v);
        if (s <= 0f) return Net.Bet.Pass;
        return new Bet(0.45f, 0.04f, 120);
    }

    public override void Begin(BotView v) { _frames = 0; _sinceTap = SwingGap; }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        if (++_frames > MaxFrames) return false;
        var hit = v.FirstSolidAlong(v.Me, v.TargetPos);
        if (hit == null) return false;                       // gap open: locomotion takes over

        var tile = BotView.CellCenter(BotView.Cell(hit.Value));
        input.MouseWorldPosition = tile;
        _sinceTap++;

        Vector2 d = tile - v.Me;
        int side = d.X >= 0f ? 1 : -1;
        if (d.Length() > Reach)
        {
            BotInput.Move(ref input, side);                  // walk up to the face
        }
        else
        {
            // Face the tile on the press frame (a slash behind the body is clamped).
            if (MathF.Abs(d.X) > 6f && v.Facing != side) BotInput.Move(ref input, side);
            if (_sinceTap >= SwingGap && !v.Previous.LeftClick) { input.LeftClick = true; _sinceTap = 0; }
        }
        return true;
    }
}
