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
    Jump,        // Space went from up → down this frame (movement-side; short window)
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

    // Jump presses use a much shorter window than the global 2 s gesture cap: a
    // slightly-early press should buffer onto the next landing/launch, not replay
    // half a second later. Guided states (LedgePull) extend a pending jump's life
    // via Refresh so it fires at the maneuver's natural jump point.
    public const int JumpBufferFrames = 4;

    private readonly List<ActionIntent> _intents = new();
    public IReadOnlyList<ActionIntent> All => _intents;

    public void Issue(in ActionIntent intent) => _intents.Add(intent);

    // First non-consumed, non-expired intent of `type`. Pure peek — no side effect.
    // maxAgeFrames narrows the acceptance window below the global cap (jump states
    // pass JumpBufferFrames).
    public bool Peek(IntentType type, int currentFrame, out ActionIntent intent, int maxAgeFrames = MaxAgeFrames)
    {
        for (int i = 0; i < _intents.Count; i++)
        {
            var it = _intents[i];
            if (it.Consumed) continue;
            if (it.Type != type) continue;
            if (currentFrame - it.IssuedFrame > maxAgeFrames) continue;
            intent = it;
            return true;
        }
        intent = default;
        return false;
    }

    // Mark the first matching intent as Consumed. Pruned on the next Prune call.
    public bool Consume(IntentType type, int currentFrame, int maxAgeFrames = MaxAgeFrames)
    {
        for (int i = 0; i < _intents.Count; i++)
        {
            var it = _intents[i];
            if (it.Consumed) continue;
            if (it.Type != type) continue;
            if (currentFrame - it.IssuedFrame > maxAgeFrames) continue;
            it.Consumed = true;
            _intents[i] = it;
            return true;
        }
        return false;
    }

    // Bump the IssuedFrame of the first matching live intent to `currentFrame` so it
    // survives past its age window. Guided movement states call this each frame while
    // the body is committed to a maneuver that will honor the input at its natural
    // exit point; the keep-alive stops when the state ends, so the intent then expires
    // on its normal window. Only refreshes intents still inside maxAgeFrames — a
    // long-dead press can't be resurrected.
    public bool Refresh(IntentType type, int currentFrame, int maxAgeFrames = MaxAgeFrames)
    {
        for (int i = 0; i < _intents.Count; i++)
        {
            var it = _intents[i];
            if (it.Consumed) continue;
            if (it.Type != type) continue;
            if (currentFrame - it.IssuedFrame > maxAgeFrames) continue;
            it.IssuedFrame = currentFrame;
            _intents[i] = it;
            return true;
        }
        return false;
    }

    public void Prune(int currentFrame)
        => _intents.RemoveAll(i => i.Consumed
            || currentFrame - i.IssuedFrame > MaxAgeFrames
            || (i.Type == IntentType.Jump && currentFrame - i.IssuedFrame > JumpBufferFrames));

    // Snapshot/restore (roadmap goal 4 §E). ActionIntent is a struct, so the array
    // copy is a deep copy; restore replaces the live list contents in place.
    public ActionIntent[] Capture() => _intents.ToArray();

    public void Restore(ActionIntent[] intents)
    {
        _intents.Clear();
        if (intents != null) _intents.AddRange(intents);
    }
}
