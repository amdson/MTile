using System;

namespace MTile.Net;

// Re-tap latch for the bots' 1-frame LMB taps (2026-11-05). An attack OPENER accepts a
// click only IntentBuffer.OpenerLookbackFrames (12f) old, so a tap issued while the bot is
// still in recovery or hitstun ages out before the swing can start - a human re-presses,
// the maneuvers (27 single-tap sites) were written against the old 20f window and don't.
// This watches the bot's own output: a tap (LMB down for one frame, no Shift - a Shift
// press is the grab / throw family's) that has not turned into an action within a few
// frames is re-issued every RetapEvery frames for RetapWindow frames, until the player's
// action leaves the neutral / recovery hub. The bot's gesture vocabulary is unchanged:
// holds (stab, charge, grab) are not taps and are left alone.
public struct BotClickRetap
{
    public int RetapEvery  = 6;
    public int RetapWindow = 36;
    public int Retaps;                 // diagnostics

    private int  _tapFrame;
    private bool _pending;

    public BotClickRetap() { _tapFrame = int.MinValue / 2; _pending = false; Retaps = 0; }

    // Call after the maneuver / controller has written `input` for this frame, with the
    // previous frame's input and the bot's own character.
    public void Apply(ref PlayerInput input, in PlayerInput prev, PlayerCharacter self, int frame)
    {
        if (self == null) return;
        string action = self.CurrentActionName;
        bool neutral  = action == "NullAction" || action == "RecoveryAction";

        // A fresh press edge from the brain itself: remember it; it is a tap if it is up
        // next frame (decided below), and any pending retap is superseded.
        if (input.LeftClick && !prev.LeftClick)
        {
            _tapFrame = frame;
            _pending  = !input.Shift;
            return;
        }
        if (!_pending) return;
        // Held past the edge frame: a hold gesture, not a tap. Stop.
        if (input.LeftClick) { _pending = false; return; }
        // Consumed: the action left the hub. Done.
        if (!neutral && frame - _tapFrame > 1) { _pending = false; return; }
        if (frame - _tapFrame > RetapWindow) { _pending = false; return; }
        // Still neutral some frames after the tap: re-issue it (never under Shift).
        int age = frame - _tapFrame;
        if (age >= RetapEvery && age % RetapEvery == 0 && !input.Shift && !input.RightClick)
        {
            input.LeftClick = true;
            Retaps++;
        }
    }
}
