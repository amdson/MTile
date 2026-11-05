using System;

namespace MTile.Net;

// Struggle out of a grab. While the bot is held (GrabbedActive, movement state
// GrabbedState) every normal attack is gated off; the one attack left is GrabbedSlash,
// which needs a Click intent and erodes the grabber's GrabStrength (3 => breaks on the
// 3rd connecting hit). So: mash LMB with the cursor on the grabber, alternating down/up
// so each down-edge + release is a fresh Click. GrabbedSlash's own 9-frame end-lag
// throttles it; the mash just keeps a click always ready.
public sealed class EscapeGrabManeuver : BotManeuver
{
    public override int Cooldown => 0;

    // Chase/backoff locomotion would fight the grab; the maneuver only leans toward the
    // grabber (to face them — the struggle arc is short and one-sided).
    public override bool OwnsLocomotion => true;
    public override MoveClaim Move => MoveClaim.Exclusive;   // mashes LMB and leans toward the grabber with Left/Right itself; the chase must not run

    // Dominates everything: nothing else can be done while held.
    public override float Score(BotView v) => v.Self.Combat.GrabbedActive ? 10f : 0f;

    // Bet (M4): GrabbedSlash mashes chip the grabber (~0.2); staying held eats the throw and its
    // follow-up (~1.5, as avoided taken); three breaking hits at ~9 f lag + the lean ~40 f.
    public override Bet Bet(BotView v)
    {
        float s = Score(v);
        if (s <= 0f) return Net.Bet.Pass;
        return new Bet(0.2f, -1.5f, 40);
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        if (!v.Self.Combat.GrabbedActive) return false;
        // Mash at a human rate, ~10 clicks/s: 3 frames down, 3 up. (Toggling every
        // frame is 30/s, which no hand does.) Each release is a Click → GrabbedSlash.
        input.LeftClick = v.Frame % 6 < 3;
        BotInput.Move(ref input, v.Toward);
        return true;
    }
}
