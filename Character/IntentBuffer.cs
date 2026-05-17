using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Kinds of player input gestures we track. Each input edge (LMB press, release, ...)
// produces at most one intent; the action FSM consumes them when its precondition
// fires. Adding a new gesture means adding a value here + a detector in InputParser.
public enum IntentType
{
    PressEdge,   // LMB went from up → down this frame (starts a Ready)
    Click,       // short press-and-release within ClickMaxHoldFrames (starts a Slash)
    Stab,        // long press + swipe (starts a Stab; Direction = swipe vector)
    Circle,      // long press + roughly-circular drag (starts a Pulse)
}

public struct ActionIntent
{
    public IntentType Type;
    public int        IssuedFrame;
    public Vector2    Direction;     // gesture-specific; unused for PressEdge / Click
    public bool       Consumed;      // set by Consume(); pruned next frame
}

// Frame-scoped ring of recent intents. Lifecycle:
//   InputParser.Detect issues new intents each frame.
//   Action preconditions Peek (non-mutating).
//   The winning action's Enter calls Consume to mark its intent used.
//   PlayerCharacter.Update calls Prune at the end to drop Consumed + aged-out entries.
//
// One global age cap (MaxAgeFrames) — matches the 2-second buffer in the plan.
// Per-intent-type caps are easy to add later if a specific gesture needs a shorter
// memory.
public class IntentBuffer
{
    private const int MaxAgeFrames = 60;

    private readonly List<ActionIntent> _intents = new();
    public IReadOnlyList<ActionIntent> All => _intents;

    public void Issue(in ActionIntent intent) => _intents.Add(intent);

    // First non-consumed, non-expired intent of `type`. Pure peek — no side effect.
    public bool Peek(IntentType type, int currentFrame, out ActionIntent intent)
    {
        for (int i = 0; i < _intents.Count; i++)
        {
            var it = _intents[i];
            if (it.Consumed) continue;
            if (it.Type != type) continue;
            if (currentFrame - it.IssuedFrame > MaxAgeFrames) continue;
            intent = it;
            return true;
        }
        intent = default;
        return false;
    }

    // Mark the first matching intent as Consumed. Pruned on the next Prune call.
    public bool Consume(IntentType type, int currentFrame)
    {
        for (int i = 0; i < _intents.Count; i++)
        {
            var it = _intents[i];
            if (it.Consumed) continue;
            if (it.Type != type) continue;
            if (currentFrame - it.IssuedFrame > MaxAgeFrames) continue;
            it.Consumed = true;
            _intents[i] = it;
            return true;
        }
        return false;
    }

    public void Prune(int currentFrame)
        => _intents.RemoveAll(i => i.Consumed || currentFrame - i.IssuedFrame > MaxAgeFrames);
}
