using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Edge-triggered gesture detector. Walks the controller's most recent frames and
// emits ActionIntents into the buffer. Edge-triggered means each detector fires at
// most once per discrete event (press, release) — never re-fires by re-pattern-matching
// the buffer. This is what makes the action FSM safe to consume intents without
// worrying about duplicates.
//
// Click vs Stab disambiguate by hold duration + swipe distance, computed at release:
//   short hold (≤ ClickMaxHoldFrames) → Click
//   long hold + swipe (≥ StabSwipeThreshold) → Stab
//   neither → dropped (no intent)
public class InputParser
{
    public const int   ClickMaxHoldFrames     = 6;
    public const float StabSwipeThreshold     = 12f;
    private const float StabSwipeThresholdSq  = StabSwipeThreshold * StabSwipeThreshold;
    // Circle gesture tuning. Mouse must travel at least MinCircleRadius from the
    // press-center for angle samples to contribute (filters noise at the center),
    // and the cumulative signed angular sweep around that center must reach
    // CircleAngleThreshold by release. 270° gives a "rough circle" tolerance —
    // a closed loop, but not requiring perfect closure.
    public const float MinCircleRadius        = 16f;
    public const float CircleAngleThreshold   = MathF.PI * 1.5f;

    // Track the active press so we can measure hold-duration and swipe-distance on release.
    private int     _activePressFrame   = -1;
    private Vector2 _activePressMouse;

    // Circle accumulators — reset on each press edge; consumed on release.
    private float   _cumAngle;
    private Vector2 _lastDir;
    private bool    _hasLastDir;

    // Idempotency guards. Each release edge happens at most once per frame
    // transition (current up, previous down), but the guard makes the invariant
    // explicit and survives weird transitions like a buffer re-seed.
    private int _lastPressEdgeEmitted = int.MinValue;
    private int _lastClickEmitted     = int.MinValue;
    private int _lastStabEmitted      = int.MinValue;
    private int _lastCircleEmitted    = int.MinValue;

    public void Detect(Controller controller, IntentBuffer buffer, int currentFrame)
    {
        var cur  = controller.Current;
        var prev = controller.GetPrevious(1);

        // Press edge — LMB just went down. Start tracking the press.
        if (cur.LeftClick && !prev.LeftClick && currentFrame > _lastPressEdgeEmitted)
        {
            buffer.Issue(new ActionIntent { Type = IntentType.PressEdge, IssuedFrame = currentFrame });
            _lastPressEdgeEmitted = currentFrame;
            _activePressFrame     = currentFrame;
            _activePressMouse     = cur.MouseWorldPosition;
            _cumAngle             = 0f;
            _hasLastDir           = false;
        }

        // Continuous accumulation while LMB held (or just released this frame).
        // Tracks signed angular sweep of the cursor around _activePressMouse.
        // Skips contributions when the cursor is inside MinCircleRadius — noise
        // near the center produces large angular jumps from tiny movements.
        if (_activePressFrame >= 0)
        {
            var fromCenter = cur.MouseWorldPosition - _activePressMouse;
            float dist = fromCenter.Length();
            if (dist >= MinCircleRadius)
            {
                Vector2 dir = fromCenter / dist;
                if (_hasLastDir)
                {
                    float cross = _lastDir.X * dir.Y - _lastDir.Y * dir.X;
                    float dot   = _lastDir.X * dir.X + _lastDir.Y * dir.Y;
                    _cumAngle += MathF.Atan2(cross, dot);
                }
                _lastDir    = dir;
                _hasLastDir = true;
            }
        }

        // Release edge — LMB just went up. Classify the gesture and emit one of
        // Click / Circle / Stab / nothing.
        if (!cur.LeftClick && prev.LeftClick && _activePressFrame >= 0)
        {
            int     holdFrames    = currentFrame - _activePressFrame;
            Vector2 swipe         = cur.MouseWorldPosition - _activePressMouse;
            bool    holdIsClick   = holdFrames <= ClickMaxHoldFrames;
            bool    holdIsCircle  = holdFrames >  ClickMaxHoldFrames
                                  && MathF.Abs(_cumAngle) >= CircleAngleThreshold;
            // Circle wins over Stab when both could match — a closed loop usually
            // ends near the press-center (small swipe) but a wide arc could end far,
            // and a circle "feels" like the right read in that case.
            bool    holdIsStab    = holdFrames >  ClickMaxHoldFrames
                                  && swipe.LengthSquared() >= StabSwipeThresholdSq
                                  && !holdIsCircle;

            if (holdIsClick && currentFrame > _lastClickEmitted)
            {
                buffer.Issue(new ActionIntent { Type = IntentType.Click, IssuedFrame = currentFrame });
                _lastClickEmitted = currentFrame;
            }
            else if (holdIsCircle && currentFrame > _lastCircleEmitted)
            {
                buffer.Issue(new ActionIntent
                {
                    Type        = IntentType.Circle,
                    IssuedFrame = currentFrame,
                });
                _lastCircleEmitted = currentFrame;
            }
            else if (holdIsStab && currentFrame > _lastStabEmitted)
            {
                buffer.Issue(new ActionIntent
                {
                    Type        = IntentType.Stab,
                    IssuedFrame = currentFrame,
                    Direction   = Vector2.Normalize(swipe),
                });
                _lastStabEmitted = currentFrame;
            }
            // else: held long but no recognizable gesture ⇒ no intent.

            _activePressFrame = -1;
        }
    }
}
